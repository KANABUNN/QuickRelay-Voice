using System.Text;
using System.Text.Json;
using QuickRelay.Voice.Core.Feed;

internal static class FeedChecks
{
    public static void Run()
    {
        ParseV1AndProvenance();
        CommitMultipartSnapshotAndResume();
        KeepTheWarningWhenForecastEnds();
        TimeDeliveryWithoutTheLocalClock();
    }

    private static void TimeDeliveryWithoutTheLocalClock()
    {
        // The PC's monotonic clock reads 1000 s ahead of the server; the network takes 15 ms at best.
        var update = (StateMessage)Parse(StateJson);
        var received = update.Event.Warning!.Source.ReceivedAt!.Value;
        update = update with { CommittedAt = received.AddMilliseconds(12) };
        var ahead = TimeSpan.FromSeconds(1000);
        TimeSpan Local(DateTimeOffset server, int networkMs) =>
            server - DateTimeOffset.UnixEpoch + ahead + TimeSpan.FromMilliseconds(networkMs);
        var arrival = Local(update.CommittedAt, 15 + 20);

        var latency = new FeedLatency();
        Check(latency.Measure(update, arrival) == new FeedDelay(TimeSpan.FromMilliseconds(12), null),
            "time at the server is known before any heartbeat, delivery is not");
        latency.Heartbeat(received.AddSeconds(-10), Local(received.AddSeconds(-10), 40));
        latency.Heartbeat(received.AddSeconds(-5), Local(received.AddSeconds(-5), 15));
        latency.Heartbeat(received, Local(received, 25));
        Check(latency.Measure(update, arrival) == new FeedDelay(TimeSpan.FromMilliseconds(12), TimeSpan.FromMilliseconds(20)),
            "delivery is the delay beyond the fastest heartbeat, whatever the clock difference");
        Check(latency.Measure(update with { Cause = StateCause.Reconcile }, arrival).AtServer is null,
            "a reconciled update has no telegram to time");

        for (var beat = 1; beat <= 60; beat++)
            latency.Heartbeat(received.AddSeconds(5 * beat), Local(received.AddSeconds(5 * beat), 30));
        Check(latency.Measure(update, arrival).Delivery == TimeSpan.FromMilliseconds(5),
            "heartbeats older than five minutes are forgotten");
        latency.Reset();
        Check(latency.Measure(update, arrival).Delivery is null, "a new connection starts without a baseline");
    }

    private static void ParseV1AndProvenance()
    {
        const ulong sequence = 9007199254740993;
        using var subscribe = JsonDocument.Parse(FeedProtocol.CreateSubscribe("production", new FeedCursor("eew", "g1", sequence)));
        Check(subscribe.RootElement.GetProperty("resume").GetProperty("seq").GetString() == "9007199254740993", "subscribe decimal string");
        Check(Parse(StateJson) is StateMessage { Seq: sequence, ChangedSeries: SeriesKind.Warning }, "parse exact sequence");
        var warning = ((StateMessage)Parse(StateJson)).Event.Warning!;
        Check(warning.WarningData!.AddedRegions is [{ Key.Code: "001", SourceRef: "warning-1", SpeechUntil: not null }],
            "added region keeps its active-region provenance");

        Reject(() => Parse(StateJson.Replace("\"schemaVersion\":1", "\"schemaVersion\":2")));
        Reject(() => Parse(StateJson.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1")));
        Reject(() => Parse(StateJson.Replace("\"9007199254740993\"", "9007199254740993")));
        Reject(() => Parse(StateJson.Replace("\"9007199254740993\"", "\"09007199254740993\"")));
        Reject(() => Parse(StateJson.Replace("\"warning-1\",\"speechUntil\"", "\"missing\",\"speechUntil\"")));
        Reject(() => Parse(StateJson.Replace("T00:00:15Z", "T00:00:16Z")));
        Reject(() => Parse(StateJson.Replace("T00:00:15Z", "T00:00:15+09:00")));
        Reject(() => Parse(StateJson.Replace("\"forecast\":null,", "")));
        Reject(() => Parse(StateJson + " {}"));
        Reject(() => FeedProtocol.Parse(new byte[FeedProtocol.MaximumFrameBytes + 1]));
    }

    private static void CommitMultipartSnapshotAndResume()
    {
        var warning = ((StateMessage)Parse(StateJson)).Event;
        var reducer = new FeedReducer("production");
        reducer.Apply(new SyncBegin("eew", "g1", SyncMode.Snapshot, 12));
        Reject(() => reducer.Apply(new SnapshotMessage(12, 0, 2, [warning], "eew", "g1")));
        Reject(() => reducer.Apply(new SnapshotMessage(12, 1, 2, [warning], "eew", "replaced")));
        reducer.Apply(new SnapshotMessage(12, 1, 2, [warning], "eew", "g1"));
        Check(reducer.State.Cursor is null && reducer.State.Events.Count == 0, "partial snapshot stays staged");
        Reject(() => reducer.Apply(new SyncComplete(new FeedCursor("eew", "g1", 12))));
        reducer.Apply(new SnapshotMessage(12, 2, 2, [], "eew", "g1"));
        var complete = reducer.Apply(new SyncComplete(new FeedCursor("eew", "g1", 12)));
        Check(complete.CompletedSync && complete.CommittedState?.Events.Count == 1, "multipart snapshot committed once");

        var resumed = new FeedReducer("production", complete.CommittedState);
        resumed.Apply(new SyncBegin("eew", "g1", SyncMode.Resume, 13));
        var canceled = warning with { Revision = 2, Warning = Canceled(warning.Warning!, "warning-cancel") };
        var replay = Update(13, canceled, SeriesKind.Warning, DeliveryKind.Replay);
        Check(resumed.Apply(replay).LiveUpdate is null, "replay cannot produce a live speech candidate");
        resumed.Apply(replay);
        Check(resumed.State.Cursor?.Seq == 12, "replay cursor remains staged");
        var afterResume = resumed.Apply(new SyncComplete(new FeedCursor("eew", "g1", 13)));
        Check(afterResume.CommittedState?.Events[warning.EventId].Warning?.Status == SeriesStatus.Canceled,
            "a recovered cancellation is retained for its series");
        Check(resumed.Apply(Update(13, canceled, SeriesKind.Warning)).CommittedState is null, "duplicate update ignored");
        Reject(() => resumed.Apply(Update(15, warning, SeriesKind.Warning)));
        Reject(() => resumed.Apply(Update(14, warning with { Environment = "test" }, SeriesKind.Warning)));
        Reject(() => resumed.Apply(Update(14, warning with { Revision = 3, Warning = warning.Warning! with { Revision = 3 } }, SeriesKind.Warning)));
        Check(resumed.State.Cursor?.Seq == 13, "invalid update does not advance cursor");

        var wrongBoundary = new FeedReducer("production", complete.CommittedState);
        Reject(() => wrongBoundary.Apply(new SyncBegin("eew", "g2", SyncMode.Resume, 13)));
        wrongBoundary.Apply(new SyncBegin("eew", "g2", SyncMode.Snapshot, 0));
        wrongBoundary.Apply(new SnapshotMessage(0, 1, 1, [], "eew", "g2"));
        var reset = wrongBoundary.Apply(new SyncComplete(new FeedCursor("eew", "g2", 0)));
        Check(reset.CommittedState is { Events.Count: 0, Cursor.Epoch: "g2" }, "a new epoch replaces prior state");
    }

    private static void KeepTheWarningWhenForecastEnds()
    {
        var warning = ((StateMessage)Parse(StateJson)).Event;
        var reducer = new FeedReducer("production");
        reducer.Apply(new SyncBegin("eew", "g1", SyncMode.Snapshot, 1));
        reducer.Apply(new SnapshotMessage(1, 1, 1, [warning], "eew", "g1"));
        reducer.Apply(new SyncComplete(new FeedCursor("eew", "g1", 1)));

        var forecast = new FeedSeries(SeriesStatus.Final, 1, warning.Warning!.Source with { Code = "VXSE45", TelegramId = "forecast-final", Serial = "9" },
            new ForecastData(null, null, null, new(IntensityKind.Unknown), [], null), null, null);
        var both = warning with { Revision = 2, Forecast = forecast };
        reducer.Apply(Update(2, both, SeriesKind.Forecast));
        var afterCancel = both with { Revision = 3, Forecast = Canceled(forecast, "forecast-cancel") };
        var applied = reducer.Apply(Update(3, afterCancel, SeriesKind.Forecast));
        Check(applied.LiveUpdate?.Event is { Forecast.Status: SeriesStatus.Canceled, Warning.Status: SeriesStatus.Active },
            "lower serial cancels final forecast without changing active warning");

        var polluted = afterCancel with
        {
            Revision = 4,
            Forecast = afterCancel.Forecast! with { SpeechUntil = null },
            Warning = Canceled(warning.Warning!, "warning-cancel")
        };
        Reject(() => reducer.Apply(Update(4, polluted, SeriesKind.Warning)));
        Check(reducer.State.Events[warning.EventId].Warning?.Status == SeriesStatus.Active, "other series remains unchanged");
    }

    private static FeedSeries Canceled(FeedSeries previous, string telegramId) => new(
        SeriesStatus.Canceled, previous.Revision + 1, previous.Source with { TelegramId = telegramId, InfoType = InfoType.Cancel, Serial = "1" },
        null, null, previous.Source.IssuedAt!.Value.AddSeconds(30));

    private static StateMessage Update(ulong seq, FeedEvent state, SeriesKind changed, DeliveryKind delivery = DeliveryKind.Live) =>
        new("eew", "g1", seq, delivery, StateCause.Telegram, DateTimeOffset.UnixEpoch, changed, state);

    private static FeedMessage Parse(string text) => FeedProtocol.Parse(Encoding.UTF8.GetBytes(text));

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"EEW feed check failed: {name}");
        }
    }

    private static void Reject(Action apply)
    {
        try
        {
            apply();
        }
        catch (FeedProtocolException)
        {
            return;
        }
        throw new InvalidOperationException("An invalid EEW feed message was accepted.");
    }

    private const string StateJson = """
        {"schemaVersion":1,"type":"eew.state","streamId":"eew","epoch":"g1","seq":"9007199254740993",
         "delivery":"live","cause":"telegram","committedAt":"2026-11-25T00:00:00.123456789Z","changedSeries":"warning",
         "event":{"eventId":"synthetic","environment":"production","revision":"1","forecast":null,
          "warning":{"status":"active","revision":"1","speechUntil":"2026-11-25T00:00:15Z",
          "source":{"issuer":"JMA","provider":"DMDATA","code":"VXSE43","telegramId":"warning-1","serial":"2","infoType":"issue",
                    "issuedAt":"2026-11-25T00:00:00Z","sentAt":null,"receivedAt":"2026-11-25T00:00:00Z","controlAt":null,"origin":"live"},
          "data":{"coverage":"partial",
                  "regionSources":[],
                  "reportRegions":[{"codeType":"forecast-area","code":"001","name":"例示地域","level":"region"}],
                  "addedRegions":[{"codeType":"forecast-area","code":"001","name":"例示地域","level":"region"}],
                  "activeRegions":[{"codeType":"forecast-area","code":"001","name":"例示地域","level":"region","sourceRef":"warning-1","speechUntil":"2026-11-25T00:00:15Z"},
                                   {"codeType":"forecast-area","code":"002","name":"未確認地域","level":"region","sourceRef":null,"speechUntil":null}]}}}}
        """;
}
