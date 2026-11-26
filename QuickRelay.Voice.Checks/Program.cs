using System.Text.Json;
using QuickRelay.Voice.Core;
using QuickRelay.Voice.Core.Feed;

FeedChecks.Run();
ContractChecks.Run();
CheckSpeech();
Console.WriteLine("PASS speech policy: every report, differences from the last speech, final summary, warnings and cancellation");
await VoiceChecks.RunAsync();
await TransportChecks.RunAsync();
Console.WriteLine("QRV verification passed.");

static void CheckSpeech()
{
    var now = DateTimeOffset.UtcNow;
    var nationwide = new SpeechSettings(true, new HashSet<RegionKey>(), true, "0");

    // Reports are read regardless of quality: a magnitude from 0 or 1 station is still news.
    var heard = new List<SpeechMark>();
    var quake = new FeedEvent("quake", "test", 1, Forecast(1, "1", "3", 3.5, 10, points: 0), null);
    Expect(Speak(quake, SeriesKind.Forecast, nationwide, heard),
        "緊急地震速報、予報、第1報。石川県能登地方で地震、予想最大震度3、マグニチュード3.5、深さ10キロ。",
        "the first report states everything, whatever the magnitude point count");
    quake = quake with { Revision = 2, Forecast = Forecast(2, "2", "4", 3.8, 10) };
    Expect(Speak(quake, SeriesKind.Forecast, nationwide, heard), "第2報。予想最大震度4に上昇、マグニチュード3.8に上昇。",
        "a later report says only what changed, with its direction");
    quake = quake with { Revision = 3, Forecast = Forecast(3, "3", "4", 3.8, 10) };
    Expect(Speak(quake, SeriesKind.Forecast, nationwide, heard), "（第3報は前回の読み上げから変化がありません）",
        "a report without changes stays silent and says why");
    quake = quake with { Revision = 4, Forecast = Forecast(4, "4", "4", 3.7, 20) };
    Expect(Speak(quake, SeriesKind.Forecast, nationwide, heard), "第4報。マグニチュード3.7に低下、深さ20キロに変更。",
        "a lower magnitude and a new depth are both told");
    quake = quake with { Revision = 5, Forecast = Forecast(5, "5", "4", 3.7, 20, status: SeriesStatus.Final) };
    Expect(Speak(quake, SeriesKind.Forecast, nationwide, heard),
        "第5報、最終報。石川県能登地方で地震、予想最大震度4、マグニチュード3.7、深さ20キロ。",
        "the final report sums up, although QuickRelay gives it no deadline");

    // Reports that arrive while something else plays merge; the words compare with what was last heard.
    var merged = new List<SpeechMark>();
    var burst = new FeedEvent("burst", "test", 1, Forecast(1, "1", "3", 4.0, 10), null);
    Speak(burst, SeriesKind.Forecast, nationwide, merged);
    var queued = SpeechPolicy.FromLive(Update(burst with { Revision = 2, Forecast = Forecast(2, "2", "3", 4.3, 10) },
        SeriesKind.Forecast), nationwide, merged, now)!;
    queued = queued.Merge(SpeechPolicy.FromLive(Update(burst with { Revision = 3, Forecast = Forecast(3, "3", "3", 4.0, 10) },
        SeriesKind.Forecast), nationwide, merged, now)!);
    Check(SpeechPolicy.Compose(queued, burst with { Revision = 3, Forecast = Forecast(3, "3", "3", 4.0, 10) },
        nationwide, merged, now, out var reason) is null && reason.Contains("変化がありません"),
        "a magnitude that went up and back while waiting is not read");

    // A second earthquake in between gets its name said, so overlapping events stay apart.
    var other = new FeedEvent("other", "test", 1, Forecast(1, "1", "2", 3.0, 30, hypocenter: "茨城県南部"), null);
    Speak(other, SeriesKind.Forecast, nationwide, merged);
    Expect(Speak(burst with { Revision = 4, Forecast = Forecast(4, "4", "4", 4.0, 10) }, SeriesKind.Forecast, nationwide, merged),
        "石川県能登地方の地震、第4報。予想最大震度4に上昇。", "the earthquake is named after another one was spoken");

    // Assumed hypocenters and single-station estimates are flagged, and their resolution is told.
    var plum = new List<SpeechMark>();
    var assumed = new FeedEvent("plum", "test", 1,
        Forecast(1, "1", "5-", null, null, quality: ForecastQuality.Assumed), null);
    Expect(Speak(assumed, SeriesKind.Forecast, nationwide, plum),
        "緊急地震速報、予報、第1報。石川県能登地方付近で地震、震源は仮定、予想最大震度5弱。", "an assumed hypocenter is flagged");
    Expect(Speak(assumed with { Revision = 2, Forecast = Forecast(2, "2", "5-", 5.2, 10) }, SeriesKind.Forecast, nationwide, plum),
        "第2報。震源は石川県能登地方と推定、マグニチュード5.2、深さ10キロ。", "the estimated hypocenter replaces the assumed one");
    var single = new List<SpeechMark>();
    var lone = new FeedEvent("lone", "test", 1, Forecast(1, "1", "3", 3.1, 10, quality: ForecastQuality.LowAccuracy), null);
    Expect(Speak(lone, SeriesKind.Forecast, nationwide, single),
        "緊急地震速報、予報、第1報。石川県能登地方で地震、1点観測、予想最大震度3、マグニチュード3.1、深さ10キロ。",
        "a single-station estimate is flagged");
    Expect(Speak(lone with { Revision = 2, Forecast = Forecast(2, "2", "3", 3.1, 10) }, SeriesKind.Forecast, nationwide, single),
        "第2報。震源精度向上。", "more stations confirming the estimate is told");

    // The minimum intensity decides only whether an earthquake starts being read.
    var strong = nationwide with { ForecastThreshold = "4" };
    var gated = new List<SpeechMark>();
    var weak = new FeedEvent("weak", "test", 1, Forecast(1, "1", "3", 3.0, 10), null);
    Check(SpeechPolicy.FromLive(Update(weak, SeriesKind.Forecast), strong, gated, now) is null, "below the minimum stays silent");
    Speak(weak with { Revision = 2, Forecast = Forecast(2, "2", "4", 3.6, 10) }, SeriesKind.Forecast, strong, gated);
    Expect(Speak(weak with { Revision = 3, Forecast = Forecast(3, "3", "3", 3.6, 10) }, SeriesKind.Forecast, strong, gated),
        "第3報。予想最大震度3に低下。", "once read, a drop below the minimum is still told");
    Check(SpeechPolicy.FromLive(Update(weak, SeriesKind.Forecast), strong with { ForecastEnabled = false }, [], now) is null,
        "forecasts stay silent when disabled");

    CheckWarnings(now, nationwide);
    CheckRegions(now);
    CheckSavedHistory(now);

    FeedSeries Forecast(ulong revision, string serial, string intensity, double? magnitude, int? depth,
        string hypocenter = "石川県能登地方", ForecastQuality quality = ForecastQuality.Estimated, int? points = 1,
        SeriesStatus status = SeriesStatus.Active) =>
        new(status, revision, Source("VXSE45", "f" + serial, serial),
            new ForecastData(new ForecastHypocenter("390", hypocenter, quality, points), magnitude, depth,
                new IntensityRange(IntensityKind.Exact, intensity), [], null),
            null, status == SeriesStatus.Final ? null : now.AddSeconds(9));

    FeedSource Source(string code, string id, string serial) =>
        new("JMA", "DMDATA", code, id, serial, InfoType.Issue, now.AddSeconds(-1), null, now, now, SourceOrigin.Live);

    StateMessage Update(FeedEvent value, SeriesKind changed) =>
        new("test", "epoch", 100, DeliveryKind.Live, StateCause.Telegram, now, changed, value);

    string? Speak(FeedEvent value, SeriesKind changed, SpeechSettings settings, List<SpeechMark> marks)
    {
        if (SpeechPolicy.FromLive(Update(value, changed), settings, marks, now) is not { } job)
            return null;
        if (SpeechPolicy.Compose(job, value, settings, marks, now, out var why) is not { } announcement)
            return $"（{why}）";
        Check(SpeechPolicy.CanStart(announcement, value, marks, now), "a composed announcement can start");
        marks.AddRange(announcement.StartAt(now));
        return announcement.Text;
    }
}

static void CheckWarnings(DateTimeOffset now, SpeechSettings nationwide)
{
    var zone = Region("緊急地震速報／地方予報区", "9932", "北陸", "zone", "w1");
    var ishikawa = Region("緊急地震速報／府県予報区", "9170", "石川", "prefecture", "w1");
    var noto = Region("緊急地震速報／細分区域", "391", "石川県能登", "region", "w1");
    var first = Warning(1, "1", "w1", [zone, ishikawa, noto], [zone, ishikawa, noto]);
    var quake = new FeedEvent("warned", "test", 1, null, first);
    var marks = new List<SpeechMark>();

    var alarm = SpeechPolicy.FromLive(Update(quake, SeriesKind.Warning), nationwide, marks, now);
    Check(alarm is { Kind: SpeechKind.FirstWarning }, "a new warning raises the alarm");
    Check(SpeechPolicy.FromLive(Update(quake, SeriesKind.Warning) with { Delivery = DeliveryKind.Replay }, nationwide, marks, now)
        is null, "a replayed warning stays silent");
    Check(SpeechPolicy.FromLive(Update(quake, SeriesKind.Warning) with { Cause = StateCause.Reconcile }, nationwide, marks, now)
        is null, "a reconciled warning stays silent");
    var words = SpeechPolicy.Compose(alarm!, quake, nationwide, marks, now, out _)!;
    Check(words.Text == "対象地域は、石川です。", "the alarm names prefectures once, not every level");
    Check(!SpeechPolicy.CanStart(words, quake, marks, now.AddSeconds(14)), "a queued alarm expires at its deadline");
    marks.AddRange(words.StartAt(now));
    Check(SpeechPolicy.FromLive(Update(quake, SeriesKind.Warning), nationwide, marks, now) is null, "a heard region stays silent");

    quake = quake with { Revision = 2, Forecast = new FeedSeries(SeriesStatus.Active, 1,
        Source("VXSE45", "f2", "2"), new ForecastData(new ForecastHypocenter("390", "石川県能登地方", ForecastQuality.Estimated, 5),
            7.2, 10, new IntensityRange(IntensityKind.LowerBound, "6-"), [], new IntensityRange(IntensityKind.Exact, "3")),
        null, now.AddSeconds(9)) };
    Expect(Speak(quake, SeriesKind.Forecast, nationwide, marks),
        "第2報。石川県能登地方で地震、予想最大震度6弱以上、マグニチュード7.2、深さ10キロ、長周期地震動階級3。",
        "the forecast after the alarm states everything without repeating the alarm");

    var toyama = Region("緊急地震速報／府県予報区", "9160", "富山", "prefecture", "w3");
    var east = Region("緊急地震速報／細分区域", "381", "富山県東部", "region", "w3");
    var more = new[] { "新潟", "福井", "岐阜", "長野", "石川県加賀" }
        .Select((name, index) => Region("緊急地震速報／府県予報区", $"98{index}", name, "prefecture", "w3")).ToArray();
    var active = new[] { zone, ishikawa, noto, toyama, east }.Concat(more).ToArray();
    var second = Warning(2, "3", "w3", active, [toyama, east, .. more], new Dictionary<string, FeedSource>
    {
        ["w1"] = Source("VXSE43", "w1", "1")
    });
    quake = quake with { Revision = 3, Warning = second };
    var update = SpeechPolicy.FromLive(Update(quake, SeriesKind.Warning), nationwide, marks, now);
    Check(update is { Kind: SpeechKind.WarningUpdate }, "added regions are a warning update");
    Expect(Speak(quake, SeriesKind.Warning, nationwide, marks),
        "第3報。警報地域追加、富山、新潟、福井、岐阜、長野、ほか1地域。",
        "added regions are named with the newer report number, and a long list is counted");

    var outside = nationwide with { Nationwide = false, Regions = new HashSet<RegionKey> { new("other", "999") } };
    Check(SpeechPolicy.FromLive(Update(new FeedEvent("warned", "test", 1, null, first), SeriesKind.Warning), outside, [], now)
        is null, "a warning outside the chosen regions stays silent");

    var canceled = quake with
    {
        Revision = 4,
        Warning = new FeedSeries(SeriesStatus.Canceled, 3, Source("VXSE43", "w-cancel", "4") with { InfoType = InfoType.Cancel },
            null, null, now.AddSeconds(29)),
        Forecast = new FeedSeries(SeriesStatus.Canceled, 2, Source("VXSE45", "f-cancel", "4") with { InfoType = InfoType.Cancel },
            null, null, now.AddSeconds(29))
    };
    Check(!SpeechPolicy.CanStart(words, canceled, [], now), "a withdrawn warning cannot start");
    var cancel = SpeechPolicy.FromLive(Update(canceled, SeriesKind.Warning), nationwide, marks, now);
    Check(cancel is { Kind: SpeechKind.WarningCancel }, "a cancellation after speech is offered");
    Check(SpeechPolicy.FromLive(Update(canceled, SeriesKind.Forecast), nationwide, marks, now) is { } forecastCancel
        && cancel!.Merge(forecastCancel).Kind == SpeechKind.WarningCancel, "both cancellations merge into one");
    Check(SpeechPolicy.FromLive(Update(canceled, SeriesKind.Forecast) with { Delivery = DeliveryKind.Replay },
        nationwide, marks, now) is null, "a replayed cancellation stays silent");
    var recovered = canceled with
    {
        Warning = canceled.Warning! with { Source = canceled.Warning.Source with { Origin = SourceOrigin.Recovered } }
    };
    Check(SpeechPolicy.FromLive(Update(recovered, SeriesKind.Warning), nationwide, marks, now) is null,
        "a cancellation from a recovered source stays silent");
    Check(SpeechPolicy.FromLive(Update(canceled, SeriesKind.Warning), nationwide, [], now) is null,
        "the cancellation of an earthquake never spoken stays silent");
    Check(SpeechPolicy.FromLive(Update(canceled, SeriesKind.Warning), nationwide, marks, now.AddSeconds(29)) is null,
        "an expired cancellation stays silent");
    Check(SpeechPolicy.FromLive(Update(canceled with { Revision = 5, Forecast = quake.Forecast }, SeriesKind.Forecast),
        nationwide, marks, now) is null, "a withdrawn earthquake does not return to forecasts");
    Expect(Speak(canceled, SeriesKind.Warning, nationwide, marks),
        "先ほどお伝えした石川県能登地方の緊急地震速報は取り消されました。", "the whole earthquake is withdrawn once");
    Check(marks.Count(mark => mark.Kind is SpeechKind.WarningCancel or SpeechKind.ForecastCancel) == 2,
        "one notice records both withdrawn series");
    Check(SpeechPolicy.FromLive(Update(canceled, SeriesKind.Forecast), nationwide, marks, now) is { } again
        && SpeechPolicy.Compose(again, canceled, nationwide, marks, now, out _) is null, "the notice is not repeated");

    WarningRegion Region(string type, string code, string name, string level, string source) =>
        new(new RegionKey(type, code), name, level, source, now.AddSeconds(14));

    FeedSeries Warning(ulong revision, string serial, string id, IReadOnlyList<WarningRegion> regions,
        IReadOnlyList<WarningRegion> added, IReadOnlyDictionary<string, FeedSource>? sources = null) =>
        new(SeriesStatus.Active, revision, Source("VXSE43", id, serial),
            null, new WarningData(WarningCoverage.Complete, added, added, regions, sources), now.AddSeconds(14));

    FeedSource Source(string code, string id, string serial) =>
        new("JMA", "DMDATA", code, id, serial, InfoType.Issue, now.AddSeconds(-1), null, now, now, SourceOrigin.Live);

    StateMessage Update(FeedEvent value, SeriesKind changed) =>
        new("test", "epoch", 100, DeliveryKind.Live, StateCause.Telegram, now, changed, value);

    string? Speak(FeedEvent value, SeriesKind changed, SpeechSettings settings, List<SpeechMark> marks)
    {
        if (SpeechPolicy.FromLive(Update(value, changed), settings, marks, now) is not { } job
            || SpeechPolicy.Compose(job, value, settings, marks, now, out _) is not { } announcement)
            return null;
        marks.AddRange(announcement.StartAt(now));
        return announcement.Text;
    }
}

static void CheckRegions(DateTimeOffset now)
{
    var selected = new RegionKey("eew", "001");
    var settings = new SpeechSettings(false, new HashSet<RegionKey> { selected }, true, "4");
    var range = new IntensityRange(IntensityKind.Range, Min: "5-", Max: "5+");
    var data = new ForecastData(new ForecastHypocenter(null, "例示震央", ForecastQuality.Estimated, 2), 6.0, 40,
        new IntensityRange(IntensityKind.Exact, "7"), [new RegionForecast(selected, "例示地域", range, null, ArrivalCondition.Unknown)], null);
    var source = new FeedSource("JMA", "DMDATA", "VXSE45", "f1", "1", InfoType.Issue, now.AddSeconds(-1), null, now, now,
        SourceOrigin.Live);
    var quake = new FeedEvent("regional", "test", 1,
        new FeedSeries(SeriesStatus.Active, 1, source, data, null, now.AddSeconds(9)), null);
    var update = new StateMessage("test", "epoch", 100, DeliveryKind.Live, StateCause.Telegram, now, SeriesKind.Forecast, quake);
    var job = SpeechPolicy.FromLive(update, settings, [], now);
    Check(job is not null && SpeechPolicy.Compose(job, quake, settings, [], now, out _)?.Text
        .Contains("例示地域の予想震度5弱から5強") == true, "the chosen region's forecast keeps its range");
    Check(SpeechPolicy.FromLive(update, settings with { ForecastThreshold = "5+" }, [], now) is null,
        "the lower end of a range decides the minimum");
}

static void CheckSavedHistory(DateTimeOffset now)
{
    // speech-*.json written before this version: no forecast baseline, and an IntensityRank no longer used.
    var older = JsonSerializer.Deserialize<List<SpeechMark>>("""
        [{"EventId":"20261126073135","Series":0,"Kind":4,"Revision":4,"Regions":[],"IntensityRank":3,
          "StartedAt":"2026-11-25T22:32:08.9996884+00:00"}]
        """)!;
    Check(older is [{ Kind: SpeechKind.Forecast, Facts: null, Regions.Count: 0 }], "earlier speech history still loads");

    var facts = new ForecastFacts("3", false, "石川県能登地方", false, 4.1, 10, new IntensityRange(IntensityKind.Exact, "4"),
        null, [new RegionFact(new RegionKey("eew", "001"), "例示地域", new IntensityRange(IntensityKind.Range, Min: "3", Max: "4"))]);
    var mark = new SpeechMark("quake", SeriesKind.Forecast, SpeechKind.Forecast, 3, [], now, facts);
    var restored = JsonSerializer.Deserialize<SpeechMark>(JsonSerializer.Serialize(mark))!;
    Check(restored.Facts is { Magnitude: 4.1, DepthKm: 10, Hypocenter: "石川県能登地方" } restoredFacts
        && restoredFacts.MaxIntensity == facts.MaxIntensity && restoredFacts.Regions.Single() == facts.Regions.Single(),
        "the spoken baseline survives a restart");
}

static void Expect(string? actual, string expected, string description)
    => Check(actual == expected, $"{description}\n  expected: {expected}\n  actual:   {actual ?? "(silent)"}");

static void Check(bool condition, string description)
{
    if (!condition)
        throw new InvalidOperationException("QRV check failed: " + description);
}
