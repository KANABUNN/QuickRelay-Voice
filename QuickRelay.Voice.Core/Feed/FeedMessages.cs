namespace QuickRelay.Voice.Core.Feed;

public enum SeriesKind { Forecast, Warning }
public enum SeriesStatus { Active, Final, Canceled }
public enum SourceOrigin { Live, Recovered, Historical }
public enum InfoType { Issue, Correction, Cancel }
public enum DeliveryKind { Live, Replay }
public enum StateCause { Telegram, Reconcile }
public enum HealthStatus { Healthy, Degraded }
public enum SyncMode { Resume, Snapshot }
public enum WarningCoverage { Complete, Partial }
public enum ForecastQuality { Unknown, Estimated, Assumed, LowAccuracy }
public enum IntensityKind { Unknown, Exact, Range, LowerBound }
public enum ArrivalCondition { Unknown, Predicted, Arrived, NoPrediction }

public sealed record FeedCursor(string StreamId, string Epoch, ulong Seq);

public abstract record FeedMessage;

public sealed record SyncBegin(string StreamId, string Epoch, SyncMode Mode, ulong AsOfSeq) : FeedMessage;

public sealed record StateMessage(
    string StreamId,
    string Epoch,
    ulong Seq,
    DeliveryKind Delivery,
    StateCause Cause,
    DateTimeOffset CommittedAt,
    SeriesKind ChangedSeries,
    FeedEvent Event) : FeedMessage;

public sealed record SnapshotMessage(
    ulong AsOfSeq,
    int Part,
    int Parts,
    IReadOnlyList<FeedEvent> Events,
    string StreamId,
    string Epoch) : FeedMessage;

public sealed record SyncComplete(FeedCursor Cursor) : FeedMessage;

public sealed record HealthMessage(
    DateTimeOffset ServerTime, HealthStatus Upstream, HealthStatus Capture) : FeedMessage;

public sealed record FeedEvent(
    string EventId, string Environment, ulong Revision, FeedSeries? Forecast, FeedSeries? Warning)
{
    public FeedSeries? GetSeries(SeriesKind kind) => kind == SeriesKind.Forecast ? Forecast : Warning;
}

public sealed record FeedSeries(
    SeriesStatus Status,
    ulong Revision,
    FeedSource Source,
    ForecastData? ForecastData,
    WarningData? WarningData,
    DateTimeOffset? SpeechUntil);

public sealed record FeedSource(
    string Issuer,
    string Provider,
    string Code,
    string TelegramId,
    string? Serial,
    InfoType InfoType,
    DateTimeOffset? IssuedAt,
    DateTimeOffset? SentAt,
    DateTimeOffset? ReceivedAt,
    DateTimeOffset? ControlAt,
    SourceOrigin Origin);

public sealed record RegionKey(string CodeType, string Code);

public sealed record WarningRegion(
    RegionKey Key, string Name, string Level, string? SourceRef, DateTimeOffset? SpeechUntil);

public sealed record WarningData(
    WarningCoverage Coverage,
    IReadOnlyList<WarningRegion> ReportRegions,
    IReadOnlyList<WarningRegion> AddedRegions,
    IReadOnlyList<WarningRegion> ActiveRegions,
    IReadOnlyDictionary<string, FeedSource>? RegionSources = null);

public sealed record ForecastHypocenter(
    string? Code,
    string? Name,
    ForecastQuality Quality,
    int? MagnitudePoints,
    double? Latitude = null,
    double? Longitude = null,
    string? EpicenterRank = null,
    string? EpicenterRank2 = null,
    string? DepthRank = null,
    string? MagnitudeRank = null);

public sealed record IntensityRange(IntensityKind Kind, string? Value = null, string? Min = null, string? Max = null);

public sealed record RegionForecast(
    RegionKey Key,
    string Name,
    IntensityRange Intensity,
    DateTimeOffset? ArrivalTime,
    ArrivalCondition ArrivalCondition,
    IntensityRange? MaxLongPeriodClass = null,
    string? ArrivalText = null,
    string? PrefectureCode = null,
    string? PrefectureName = null,
    string? KindCode = null,
    string? KindName = null,
    bool IsWarning = false);

public sealed record ForecastData(
    ForecastHypocenter? Hypocenter,
    double? Magnitude,
    int? DepthKm,
    IntensityRange MaxIntensity,
    IReadOnlyList<RegionForecast> RegionForecasts,
    IntensityRange? MaxLongPeriodClass,
    DateTimeOffset? OriginTime = null)
{
    public bool HasReliableQuality => Hypocenter is { Quality: ForecastQuality.Estimated, MagnitudePoints: >= 2 };
}

public sealed class FeedProtocolException(string message, Exception? innerException = null)
    : Exception(message, innerException);
