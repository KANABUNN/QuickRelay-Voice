using QuickRelay.Voice.Core.Feed;

namespace QuickRelay.Voice.Core;

public enum SpeechKind { FirstWarning, WarningCancel, WarningUpdate, ForecastCancel, Forecast }

public sealed record SpeechSettings(
    bool Nationwide, IReadOnlySet<RegionKey> Regions, bool ForecastEnabled, string ForecastThreshold, int ForecastStep,
    IReadOnlyDictionary<string, string>? RegionReadings = null);

public sealed record SpeechMark(
    string EventId, SeriesKind Series, SpeechKind Kind, ulong Revision,
    IReadOnlyList<RegionKey> Regions, int? IntensityRank, DateTimeOffset StartedAt);

public sealed record SpeechJob(
    string EventId, SeriesKind Series, SpeechKind Kind, ulong Revision, string TelegramId,
    IReadOnlyList<RegionKey> Regions, int? IntensityRank, DateTimeOffset IssuedAt,
    DateTimeOffset Until, string Text)
{
    public bool IsWarning => Kind is SpeechKind.FirstWarning or SpeechKind.WarningUpdate;

    public SpeechMark Mark(DateTimeOffset now)
        => new(EventId, Series, Kind, Revision, Regions, IntensityRank, now);
}

public static class SpeechPolicy
{
    public const string FixedWarning = "緊急地震速報、警報。強い揺れに警戒してください。身の安全を確保してください。";
    private static readonly string[] IntensityCodes = ["0", "1", "2", "3", "4", "5-", "5+", "6-", "6+", "7"];
    private static readonly string[] IntensityNames = ["0", "1", "2", "3", "4", "5弱", "5強", "6弱", "6強", "7"];

    public static SpeechJob? FromLive(StateMessage update, SpeechSettings settings,
        IReadOnlyList<SpeechMark> marks, DateTimeOffset now)
    {
        var earthquake = update.Event;
        var series = earthquake.GetSeries(update.ChangedSeries);
        if (update.Delivery != DeliveryKind.Live || update.Cause != StateCause.Telegram
            || series?.Source.Origin != SourceOrigin.Live)
            return null;

        if (series.Status == SeriesStatus.Canceled)
            return Cancellation(earthquake, update.ChangedSeries, marks, now);
        if (series.Status != SeriesStatus.Active || series.Source.InfoType != InfoType.Issue)
            return null;

        return update.ChangedSeries == SeriesKind.Warning
            ? Warning(earthquake, series, settings, marks, now)
            : Forecast(earthquake, series, settings, marks, now);
    }

    public static SpeechJob? Cancellation(FeedEvent earthquake, SeriesKind kind,
        IReadOnlyList<SpeechMark> marks, DateTimeOffset now)
    {
        if (earthquake.GetSeries(kind) is not { Status: SeriesStatus.Canceled } series
            || series.Source is not { InfoType: InfoType.Cancel, Origin: SourceOrigin.Live or SourceOrigin.Recovered }
            || Window(series.Source, series.SpeechUntil, 30, now) is not { } deadline)
            return null;
        var history = marks.Where(mark => mark.EventId == earthquake.EventId && mark.Series == kind);
        var cancelKind = kind == SeriesKind.Warning ? SpeechKind.WarningCancel : SpeechKind.ForecastCancel;
        if (!history.Any(mark => mark.Kind is SpeechKind.FirstWarning or SpeechKind.WarningUpdate or SpeechKind.Forecast)
            || history.Any(mark => mark.Kind == cancelKind))
            return null;
        var label = kind == SeriesKind.Warning ? "警報" : "予報";
        return Job(earthquake, kind, cancelKind, series, [], null, deadline,
            $"先ほどお伝えした緊急地震速報の{label}は取り消されました。");
    }

    public static bool CanStart(SpeechJob job, FeedEvent earthquake, IReadOnlyList<SpeechMark> marks, DateTimeOffset now)
    {
        if (now >= job.Until || job.IssuedAt - now > TimeSpan.FromSeconds(1)
            || earthquake.EventId != job.EventId || earthquake.GetSeries(job.Series) is not { } current)
            return false;
        var history = marks.Where(mark => mark.EventId == job.EventId && mark.Series == job.Series);
        return job.Kind switch
        {
            SpeechKind.WarningCancel or SpeechKind.ForecastCancel =>
                current.Status == SeriesStatus.Canceled && current.Revision == job.Revision
                && !history.Any(mark => mark.Kind == job.Kind),
            SpeechKind.Forecast =>
                current.Status == SeriesStatus.Active && current.Revision == job.Revision && earthquake.Warning is null
                && !history.Any(mark => mark.Kind == job.Kind && mark.Revision == job.Revision),
            _ => current.Status == SeriesStatus.Active && current.WarningData is { } data
                && job.Regions.All(key => data.ActiveRegions.Any(region =>
                    region.Key == key && region.SourceRef == job.TelegramId))
                && !history.Any(mark => mark.Regions.Any(job.Regions.Contains))
        };
    }

    private static SpeechJob? Warning(FeedEvent earthquake, FeedSeries series, SpeechSettings settings,
        IReadOnlyList<SpeechMark> marks, DateTimeOffset now)
    {
        if (series.WarningData is not { } data)
            return null;
        var previous = marks.Where(mark => mark.EventId == earthquake.EventId && mark.Series == SeriesKind.Warning).ToArray();
        var heard = previous.SelectMany(mark => mark.Regions).ToHashSet();
        var added = data.AddedRegions
            .Where(region => (settings.Nationwide || settings.Regions.Contains(region.Key))
                && !heard.Contains(region.Key) && region.SourceRef == series.Source.TelegramId
                && Window(series.Source, region.SpeechUntil, 15, now) is not null)
            .ToArray();
        if (added.Length == 0)
            return null;

        var kind = heard.Count == 0 ? SpeechKind.FirstWarning : SpeechKind.WarningUpdate;
        var names = string.Join("、", added.Select(region => ReadName(region.Name, settings.RegionReadings)).Distinct().Take(4));
        var text = kind == SpeechKind.FirstWarning
            ? $"対象地域は、{names}です。"
            : $"緊急地震速報、警報。{names}でも強い揺れに警戒してください。身の安全を確保してください。";
        var expiry = added.Min(region => Window(series.Source, region.SpeechUntil, 15, now)!.Value);
        return Job(earthquake, SeriesKind.Warning, kind, series, added.Select(region => region.Key).ToArray(),
            null, expiry, text);
    }

    private static SpeechJob? Forecast(FeedEvent earthquake, FeedSeries series, SpeechSettings settings,
        IReadOnlyList<SpeechMark> marks, DateTimeOffset now)
    {
        if (!settings.ForecastEnabled || earthquake.Warning is not null || series.ForecastData is not { } forecast
            || !forecast.HasReliableQuality || Window(series.Source, series.SpeechUntil, 10, now) is not { } deadline)
            return null;

        RegionForecast? target = null;
        var intensity = forecast.MaxIntensity;
        var threshold = Array.IndexOf(IntensityCodes, settings.ForecastThreshold);
        if (threshold < 0)
            return null;
        var history = marks.Where(mark => mark.EventId == earthquake.EventId && mark.Kind == SpeechKind.Forecast).ToArray();
        if (!settings.Nationwide)
        {
            target = forecast.RegionForecasts.Where(region => settings.Regions.Contains(region.Key)
                && ShouldRead(region.Intensity, history.LastOrDefault(mark => mark.Regions.Contains(region.Key))?.IntensityRank))
                .MaxBy(region => Rank(region.Intensity));
            intensity = target?.Intensity;
            if (target is null)
                return null;
        }
        else if (!ShouldRead(intensity, history.LastOrDefault()?.IntensityRank))
            return null;

        var rank = Rank(intensity);
        var spoken = intensity!.Kind switch
        {
            IntensityKind.Range => $"震度{IntensityNames[rank]}から震度{IntensityNames[Array.IndexOf(IntensityCodes, intensity.Max)]}",
            IntensityKind.LowerBound => $"震度{IntensityNames[rank]}以上",
            _ => $"震度{IntensityNames[rank]}"
        };
        var place = target is null ? "予想される最大震度" : $"{ReadName(target.Name, settings.RegionReadings)}の予想震度";
        return Job(earthquake, SeriesKind.Forecast, SpeechKind.Forecast, series,
            target is null ? [] : [target.Key], rank, deadline, $"緊急地震速報、予報。{place}は、{spoken}です。");

        bool ShouldRead(IntensityRange? value, int? previous)
        {
            var next = Rank(value);
            return next >= 0 && (previous is null ? next >= threshold :
                Math.Abs(next - previous.Value) >= settings.ForecastStep || previous.Value < threshold && next >= threshold);
        }
    }

    private static int Rank(IntensityRange? intensity)
    {
        if (intensity is null || intensity.Kind == IntensityKind.Unknown)
            return -1;
        var lower = Array.IndexOf(IntensityCodes, intensity.Kind == IntensityKind.Exact
            ? intensity.Value : intensity.Min ?? intensity.Value);
        return intensity.Kind == IntensityKind.Range && Array.IndexOf(IntensityCodes, intensity.Max) < lower
            ? -1 : lower;
    }

    private static string ReadName(string original, IReadOnlyDictionary<string, string>? readings)
        => readings?.TryGetValue(original, out var pronunciation) == true
            && !string.IsNullOrWhiteSpace(pronunciation) && pronunciation.Length <= 60 && !pronunciation.Any(char.IsControl)
            ? pronunciation : original.Length <= 60 ? original : "対象地域";

    private static DateTimeOffset? Window(FeedSource source, DateTimeOffset? deadline, int seconds, DateTimeOffset now)
    {
        if (source.IssuedAt is not { } issued || deadline is not { } until || issued - now > TimeSpan.FromSeconds(1)
            || until <= issued || now >= until || now - issued >= TimeSpan.FromSeconds(seconds))
            return null;
        return until - issued <= TimeSpan.FromSeconds(seconds) ? until : issued.AddSeconds(seconds);
    }

    private static SpeechJob Job(FeedEvent earthquake, SeriesKind kind, SpeechKind speech, FeedSeries series,
        IReadOnlyList<RegionKey> regions, int? rank, DateTimeOffset until, string text)
        => new(earthquake.EventId, kind, speech, series.Revision, series.Source.TelegramId,
            regions, rank, series.Source.IssuedAt!.Value, until, text);
}
