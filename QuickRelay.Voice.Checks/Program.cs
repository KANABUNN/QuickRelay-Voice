using QuickRelay.Voice.Core;
using QuickRelay.Voice.Core.Feed;

FeedChecks.Run();
CheckSpeech();
Console.WriteLine("PASS speech policy: warning scope, forecast quality and ranges, expiry and cancellation history");
await TransportChecks.RunAsync();
Console.WriteLine("QRV verification passed.");

static void CheckSpeech()
{
    var now = DateTimeOffset.UtcNow;
    var selected = new RegionKey("eew", "001");
    var other = new RegionKey("eew", "002");
    var settings = new SpeechSettings(false, new HashSet<RegionKey> { selected }, true, "4", 2);
    var warningSource = Source("VXSE43", "w1");
    var region = new WarningRegion(selected, "例示地域", "region", "w1", now.AddSeconds(14));
    var unknown = new WarningRegion(other, "未確認地域", "region", null, null);
    var warningData = new WarningData(WarningCoverage.Partial, [region, unknown], [region], [region, unknown]);
    var warning = new FeedSeries(SeriesStatus.Active, 1, warningSource, null, warningData, now.AddSeconds(14));
    var first = new FeedEvent("example", "test", 1, null, warning);
    var alert = SpeechPolicy.FromLive(Update(first, SeriesKind.Warning), settings, [], now);
    Check(alert is { Kind: SpeechKind.FirstWarning } && alert.Regions.SequenceEqual([selected]), "explicit warning additions in partial state");
    Check(SpeechPolicy.FromLive(Update(first, SeriesKind.Warning) with { Delivery = DeliveryKind.Replay },
        settings, [], now) is null, "replayed warning stays silent");
    Check(SpeechPolicy.FromLive(Update(first, SeriesKind.Warning) with { Cause = StateCause.Reconcile },
        settings, [], now) is null, "reconciled warning stays silent");
    Check(SpeechPolicy.FromLive(Update(first, SeriesKind.Warning), settings with
    {
        Regions = new HashSet<RegionKey> { other }
    }, [], now) is null, "unknown provenance stays silent");
    Check(!SpeechPolicy.CanStart(alert!, first, [], now.AddSeconds(14)), "queued warning expires at its deadline");
    Check(SpeechPolicy.FromLive(Update(first, SeriesKind.Warning), settings, [alert!.Mark(now)], now) is null,
        "already started region remains silent");

    var canceled = warning with
    {
        Revision = 2, Status = SeriesStatus.Canceled, Source = Source("VXSE43", "w0") with { InfoType = InfoType.Cancel },
        WarningData = null, SpeechUntil = now.AddSeconds(29)
    };
    var voidWarning = first with { Revision = 2, Warning = canceled };
    var mark = alert.Mark(now);
    var cancel = SpeechPolicy.Cancellation(voidWarning, SeriesKind.Warning, [mark], now);
    Check(cancel is { Kind: SpeechKind.WarningCancel }, "warning cancel after a started warning");
    Check(!SpeechPolicy.CanStart(alert, voidWarning, [mark], now), "canceled warning cannot start");
    Check(SpeechPolicy.Cancellation(voidWarning, SeriesKind.Warning, [], now) is null, "silent cancellation stays silent");
    Check(SpeechPolicy.Cancellation(voidWarning, SeriesKind.Warning, [mark, cancel!.Mark(now)], now) is null,
        "durable cancel mark blocks a second cancellation");
    Check(SpeechPolicy.Cancellation(voidWarning, SeriesKind.Warning, [mark], now.AddSeconds(29)) is null,
        "expired cancellation stays silent");

    var range = new IntensityRange(IntensityKind.Range, Min: "5-", Max: "5+");
    var forecastData = new ForecastData(new(null, null, ForecastQuality.Estimated, 2), null, null,
        new(IntensityKind.Exact, "7"), [new(selected, "例示地域", "region", range, null, null)], null);
    var forecast = new FeedSeries(SeriesStatus.Active, 1, Source("VXSE45", "f1"), forecastData, null, now.AddSeconds(9));
    var eew = new FeedEvent("example", "test", 1, forecast, null);
    var estimate = SpeechPolicy.FromLive(Update(eew, SeriesKind.Forecast), settings, [], now);
    Check(estimate?.Text.Contains("震度5弱から震度5強") == true, "selected-area forecast retains the range");
    Check(SpeechPolicy.FromLive(Update(eew, SeriesKind.Forecast), settings with { ForecastThreshold = "5+" }, [], now)
        is null, "range lower bound decides threshold");
    Check(SpeechPolicy.FromLive(Update(eew with { Warning = canceled }, SeriesKind.Forecast), settings, [], now)
        is null, "warning cancellation does not return to forecasts");
    Check(SpeechPolicy.FromLive(Update(eew with { Forecast = forecast with { Status = SeriesStatus.Final } },
        SeriesKind.Forecast), settings, [], now) is null, "forecast final stays silent");
    Check(SpeechPolicy.FromLive(Update(eew with { Forecast = forecast with
    {
        ForecastData = forecastData with { Hypocenter = new(null, null, ForecastQuality.Estimated, 1) }
    } }, SeriesKind.Forecast), settings, [], now) is null, "one observation point stays silent");

    FeedSource Source(string code, string id) =>
        new("JMA", "DMDATA", code, id, "1", InfoType.Issue, now.AddSeconds(-1), null, now, now, SourceOrigin.Live);
    StateMessage Update(FeedEvent value, SeriesKind changed) =>
        new("test", "epoch", 100, DeliveryKind.Live, StateCause.Telegram, now, changed, value);
}

static void Check(bool condition, string description)
{
    if (!condition)
        throw new InvalidOperationException("QRV check failed: " + description);
}
