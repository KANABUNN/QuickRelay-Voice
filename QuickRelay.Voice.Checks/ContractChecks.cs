using System.Text;
using System.Text.Json;
using QuickRelay.Voice.Core;
using QuickRelay.Voice.Core.Feed;

internal static class ContractChecks
{
    // Synthetic examples copied byte for byte from QuickRelay PR #18 at its merge commit:
    // https://github.com/KANABUNN/QuickRelay/tree/bbea0b9380447db48f0885a5a027264cd5b40dd3/contracts/examples
    private const string Stream = "eew-production";
    private const string Epoch = "g0123456789abcdef";

    public static void Run()
    {
        var beforeSync = new FeedCursor(Stream, Epoch, 1051);
        using var outbound = JsonDocument.Parse(FeedProtocol.CreateSubscribe("production", beforeSync));
        using var expectedSubscription = JsonDocument.Parse(Read("subscribe"));
        Expect(JsonElement.DeepEquals(outbound.RootElement, expectedSubscription.RootElement), "subscribe with resume");

        var begin = (SyncBegin)Load("sync-begin");
        var snapshot = (SnapshotMessage)Load("snapshot");
        var state = (StateMessage)Load("state");
        var finish = (SyncComplete)Load("sync-complete");
        var health = (HealthMessage)Load("health");
        Expect(health is { Upstream: HealthStatus.Healthy, Capture: HealthStatus.Healthy }, "healthy heartbeat");

        var reducer = new FeedReducer("production", new FeedState(beforeSync, new Dictionary<string, FeedEvent>()));
        reducer.Apply(begin);
        Expect(reducer.Apply(state with { Delivery = DeliveryKind.Replay }).LiveUpdate is null, "resume keeps official replay silent");
        var resumed = reducer.Apply(finish);
        Expect(resumed is { CompletedSync: true, CommittedState.Cursor.Seq: 1052 }
            && resumed.CommittedState.Events[state.Event.EventId] == state.Event, "resume restores both series at the published cursor");

        var replacement = new FeedReducer("production");
        replacement.Apply(begin with { Mode = SyncMode.Snapshot });
        replacement.Apply(snapshot);
        Expect(replacement.State.Events.Count == 0, "snapshot remains uncommitted before sync_complete");
        var synchronized = replacement.Apply(finish);
        Expect(synchronized is { CompletedSync: true, CommittedState.Cursor.Seq: 1052, CommittedState.Events.Count: 1 }
            && synchronized.CommittedState.Events.Values.Single().Forecast?.Status == SeriesStatus.Canceled,
            "snapshot commits the forecast cancellation as state");

        var data = state.Event.Forecast?.ForecastData;
        Expect(data is { Hypocenter: { Quality: ForecastQuality.Estimated, MagnitudePoints: 5 },
            MaxIntensity: { Kind: IntensityKind.Range, Min: "5-", Max: "5+" } }, "nested forecast quality");
        Expect(data?.RegionForecasts is
        [
            { Key: { CodeType: "地震情報／細分区域", Code: "000" }, Intensity: { Kind: IntensityKind.Exact, Value: "5+" },
                ArrivalTime: not null, ArrivalCondition: ArrivalCondition.Predicted }
        ], "regional forecast intensity and arrival");
        var warning = state.Event.Warning?.WarningData;
        Expect(warning?.AddedRegions is [{ Key.Code: "001", SourceRef: "demo-w05" }]
            && warning.ActiveRegions is [{ Key.Code: "000", SourceRef: "demo-w02" }, { Key.Code: "001", SourceRef: "demo-w05" }]
            && warning.RegionSources?.ContainsKey("demo-w02") == true, "warning additions use their original Telegram deadlines");

        var issued = health.ServerTime;
        var settings = new SpeechSettings(false, new HashSet<RegionKey> { new("地震情報／細分区域", "001") }, true, "4");
        Expect(SpeechPolicy.FromLive(state, settings, [], issued) is { Kind: SpeechKind.FirstWarning },
            "only the actually added region speaks");
        var existingRegion = new HashSet<RegionKey> { new("地震情報／細分区域", "000") };
        settings = settings with { Regions = existingRegion };
        Expect(SpeechPolicy.FromLive(state, settings, [], issued) is null, "a current warning does not repeat old regions");
        var forecastOnly = state with { ChangedSeries = SeriesKind.Forecast, Event = state.Event with { Warning = null } };
        Expect(Words(forecastOnly, settings, issued)?.Contains("例示地域の予想震度5強") == true,
            "configured regional forecast uses the regional intensity from the official shape");
        var missingQualityNumber = ParseState(ReadText("state").Replace(", \"magnitudePoints\": 5", "", StringComparison.Ordinal));
        var missingQualityForecast = missingQualityNumber with
        {
            ChangedSeries = SeriesKind.Forecast,
            Event = missingQualityNumber.Event with { Warning = null }
        };
        Expect(Words(missingQualityForecast, settings, issued)?.Contains("マグニチュード6.1") == true,
            "a forecast without a point count is still spoken");
        var unknownFields = ParseState(ReadText("state").Replace("\"schemaVersion\": 1",
            "\"schemaVersion\": 1, \"futureDeliveryMetadata\": {}", StringComparison.Ordinal));
        Expect(unknownFields is { Seq: 1052 }, "optional future fields are ignored");
        var nullableReceived = ParseState(ReadText("state").Replace("\"receivedAt\": \"2026-11-24T03:00:18.07Z\"",
            "\"receivedAt\": null", StringComparison.Ordinal));
        Expect(nullableReceived.Event.Forecast?.Source.ReceivedAt is null, "a null reception time is valid data");
        Console.WriteLine("PASS QuickRelay PR #18 official contract examples: subscribe, resume, snapshot, series and regional metadata");
    }

    private static string? Words(StateMessage update, SpeechSettings settings, DateTimeOffset now)
        => SpeechPolicy.FromLive(update, settings, [], now) is { } job
            ? SpeechPolicy.Compose(job, update.Event, settings, [], now, out _)?.Text : null;

    private static StateMessage ParseState(string json) => (StateMessage)FeedProtocol.Parse(Encoding.UTF8.GetBytes(json));
    private static FeedMessage Load(string name) => FeedProtocol.Parse(Read(name));
    private static string ReadText(string name) => Encoding.UTF8.GetString(Read(name));
    private static byte[] Read(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "ContractExamples", $"eew-{name}.valid.json"));

    private static void Expect(bool condition, string requirement)
    {
        if (!condition)
            throw new InvalidOperationException($"QuickRelay PR #18 contract check failed: {requirement}");
    }
}
