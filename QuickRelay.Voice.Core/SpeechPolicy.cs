using System.Globalization;
using QuickRelay.Voice.Core.Feed;

namespace QuickRelay.Voice.Core;

/// <summary>Speech priority: a lower value interrupts a higher one. The values are stored in speech-*.json.</summary>
public enum SpeechKind { FirstWarning, WarningCancel, WarningUpdate, ForecastCancel, Forecast }

/// <summary>Pending speech for one earthquake merges within a category, so a burst of reports becomes one announcement.</summary>
public enum SpeechCategory { Alarm, Cancel, Update }

public sealed record SpeechSettings(
    bool Nationwide, IReadOnlySet<RegionKey> Regions, bool ForecastEnabled, string ForecastThreshold,
    PlaceReadings? Readings = null)
{
    /// <summary>The place-name pronunciations in use: the defaults unless readings.json was loaded.</summary>
    public PlaceReadings Places => Readings ?? PlaceReadings.Default;
}

/// <summary>What the listener has been told about an earthquake's forecast: the baseline for the next report.</summary>
public sealed record ForecastFacts(
    string? Serial, bool Final, string? Hypocenter, bool Assumed, double? Magnitude, int? DepthKm,
    IntensityRange MaxIntensity, IntensityRange? LongPeriodClass, IReadOnlyList<RegionFact> Regions,
    bool SinglePoint = false);

public sealed record RegionFact(RegionKey Key, string Name, IntensityRange Intensity);

/// <summary>A record that speech started, saved before the sound so a restart does not repeat it.</summary>
public sealed record SpeechMark(
    string EventId, SeriesKind Series, SpeechKind Kind, ulong Revision,
    IReadOnlyList<RegionKey> Regions, DateTimeOffset StartedAt, ForecastFacts? Facts = null);

/// <summary>
/// Speech waiting for its turn. The words are chosen when it plays, from the earthquake's state at that moment,
/// so the listener hears the newest report described against what was last spoken.
/// </summary>
public sealed record SpeechJob(
    string EventId, SpeechKind Kind, DateTimeOffset IssuedAt, DateTimeOffset Until, IReadOnlyList<RegionKey> Regions)
{
    public SpeechCategory Category => Kind switch
    {
        SpeechKind.FirstWarning => SpeechCategory.Alarm,
        SpeechKind.WarningCancel or SpeechKind.ForecastCancel => SpeechCategory.Cancel,
        _ => SpeechCategory.Update
    };

    public SpeechJob Merge(SpeechJob newer) => new(EventId, Kind < newer.Kind ? Kind : newer.Kind,
        IssuedAt < newer.IssuedAt ? IssuedAt : newer.IssuedAt, Until > newer.Until ? Until : newer.Until,
        Regions.Union(newer.Regions).ToArray());
}

/// <summary>Words ready to speak, and the marks to save when they start.</summary>
public sealed record Announcement(
    string EventId, SpeechKind Kind, string Text, DateTimeOffset IssuedAt, DateTimeOffset Until,
    IReadOnlyList<SpeechMark> Marks)
{
    public IReadOnlyList<RegionKey> Regions => Marks.SelectMany(mark => mark.Regions).ToArray();

    public IReadOnlyList<SpeechMark> StartAt(DateTimeOffset now) => Marks.Select(mark => mark with { StartedAt = now }).ToArray();
}

public static class SpeechPolicy
{
    public const string FixedWarning = "緊急地震速報、警報。強い揺れに警戒してください。身の安全を確保してください。";
    private const int ForecastSeconds = 10;
    private const int WarningSeconds = 15;
    private const int CancelSeconds = 30;
    private const int MaxNames = 5;
    private const int MaxRegionFacts = 3;
    private static readonly string[] IntensityCodes = ["0", "1", "2", "3", "4", "5-", "5+", "6-", "6+", "7"];
    private static readonly string[] IntensityNames = ["0", "1", "2", "3", "4", "5弱", "5強", "6弱", "6強", "7"];
    private static readonly string[] Levels = ["prefecture", "region", "zone"];

    /// <summary>Decides whether a live report should be spoken. Every live report qualifies; only the words wait.</summary>
    public static SpeechJob? FromLive(StateMessage update, SpeechSettings settings,
        IReadOnlyList<SpeechMark> marks, DateTimeOffset now)
    {
        var earthquake = update.Event;
        var series = earthquake.GetSeries(update.ChangedSeries);
        if (update.Delivery != DeliveryKind.Live || update.Cause != StateCause.Telegram
            || series?.Source.Origin != SourceOrigin.Live)
            return null;

        if (series.Status == SeriesStatus.Canceled)
        {
            var kind = update.ChangedSeries == SeriesKind.Warning ? SpeechKind.WarningCancel : SpeechKind.ForecastCancel;
            return Announced(earthquake.EventId, marks)
                && Window(series.Source, series.SpeechUntil, CancelSeconds, now) is { } deadline
                ? new SpeechJob(earthquake.EventId, kind, series.Source.IssuedAt!.Value, deadline, []) : null;
        }
        if (series.Source.InfoType != InfoType.Issue || IsWithdrawn(earthquake))
            return null;

        if (update.ChangedSeries == SeriesKind.Warning)
        {
            if (series.WarningData is not { } data)
                return null;
            var heard = Heard(earthquake.EventId, marks);
            var added = Fresh(earthquake, settings, heard, data.AddedRegions.Select(region => region.Key), now);
            if (added.Count == 0)
                return null;
            return new SpeechJob(earthquake.EventId, heard.Count == 0 ? SpeechKind.FirstWarning : SpeechKind.WarningUpdate,
                series.Source.IssuedAt!.Value, added.Min(region => region.Until), added.Select(region => region.Region.Key).ToArray());
        }

        if (!settings.ForecastEnabled || ForecastWindow(series, now) is not { } until)
            return null;
        if (!Announced(earthquake.EventId, marks) && !WarnedInScope(earthquake, settings)
            && !ReachesThreshold(Facts(series, settings), settings))
            return null;
        return new SpeechJob(earthquake.EventId, SpeechKind.Forecast, series.Source.IssuedAt!.Value, until, []);
    }

    /// <summary>
    /// Chooses the words for <paramref name="job"/> from the current state. Returns null with a reason when nothing
    /// is left to say: the reports expired, the earthquake was withdrawn, or nothing changed since the last speech.
    /// </summary>
    public static Announcement? Compose(SpeechJob job, FeedEvent earthquake, SpeechSettings settings,
        IReadOnlyList<SpeechMark> marks, DateTimeOffset now, out string reason)
    {
        if (earthquake.EventId != job.EventId)
        {
            reason = "地震が一致しません";
            return null;
        }
        return job.Category switch
        {
            SpeechCategory.Cancel => Cancellation(earthquake, settings, marks, now, out reason),
            SpeechCategory.Alarm when Heard(earthquake.EventId, marks).Count == 0 =>
                Alarm(job, earthquake, settings, now, out reason),
            _ => Update(job, earthquake, settings, marks, now, out reason)
        };
    }

    /// <summary>Checks, just before the sound starts, that the announcement still describes the earthquake.</summary>
    public static bool CanStart(Announcement announcement, FeedEvent earthquake, IReadOnlyList<SpeechMark> marks, DateTimeOffset now)
    {
        if (now >= announcement.Until || announcement.IssuedAt - now > TimeSpan.FromSeconds(1)
            || earthquake.EventId != announcement.EventId)
            return false;
        var history = marks.Where(mark => mark.EventId == announcement.EventId).ToArray();
        foreach (var mark in announcement.Marks)
        {
            if (IsCancel(mark.Kind))
            {
                if (earthquake.GetSeries(mark.Series)?.Status != SeriesStatus.Canceled || history.Any(item => item.Kind == mark.Kind))
                    return false;
                continue;
            }
            if (mark.Kind == SpeechKind.FirstWarning && history.Any(item => item.Regions.Count > 0))
                return false;
            if (history.Any(item => item.Regions.Any(mark.Regions.Contains)))
                return false;
        }
        return announcement.Kind is SpeechKind.WarningCancel or SpeechKind.ForecastCancel || StillCurrent(announcement, earthquake);
    }

    /// <summary>True while nothing has been withdrawn and every announced warning region is still under warning.</summary>
    public static bool StillCurrent(Announcement announcement, FeedEvent earthquake)
    {
        if (IsWithdrawn(earthquake))
            return false;
        var regions = announcement.Regions;
        return regions.Count == 0 || earthquake.Warning is { Status: SeriesStatus.Active, WarningData: { } data }
            && regions.All(key => data.ActiveRegions.Any(region => region.Key == key));
    }

    private static Announcement? Alarm(SpeechJob job, FeedEvent earthquake, SpeechSettings settings,
        DateTimeOffset now, out string reason)
    {
        var regions = Fresh(earthquake, settings, new HashSet<RegionKey>(), job.Regions, now);
        if (regions.Count == 0)
        {
            reason = "警報地域の開始期限を過ぎました";
            return null;
        }
        reason = "";
        var warning = earthquake.Warning!;
        var mark = new SpeechMark(earthquake.EventId, SeriesKind.Warning, SpeechKind.FirstWarning, warning.Revision,
            regions.Select(region => region.Region.Key).ToArray(), now);
        return new Announcement(earthquake.EventId, SpeechKind.FirstWarning,
            $"対象地域は、{Names(regions.Select(region => region.Region), settings.Places)}です。",
            regions.Min(region => region.Source.IssuedAt!.Value), regions.Min(region => region.Until), [mark]);
    }

    private static Announcement? Update(SpeechJob job, FeedEvent earthquake, SpeechSettings settings,
        IReadOnlyList<SpeechMark> marks, DateTimeOffset now, out string reason)
    {
        if (IsWithdrawn(earthquake))
        {
            reason = "取り消された地震です";
            return null;
        }
        var history = marks.Where(mark => mark.EventId == earthquake.EventId).ToArray();
        var announced = history.Any(mark => !IsCancel(mark.Kind));
        var heard = history.SelectMany(mark => mark.Regions).ToHashSet();
        var baseline = history.LastOrDefault(mark => mark.Facts is not null)?.Facts;
        var added = Fresh(earthquake, settings, heard, job.Regions, now);

        ForecastFacts? facts = null;
        DateTimeOffset? forecastUntil = null;
        if (settings.ForecastEnabled && earthquake.Forecast is { } forecast && ForecastWindow(forecast, now) is { } until)
        {
            var current = Facts(forecast, settings);
            if (announced || added.Count > 0 || WarnedInScope(earthquake, settings) || ReachesThreshold(current, settings))
                (facts, forecastUntil) = (current, until);
        }
        if (added.Count == 0 && facts is null)
        {
            reason = "読み上げ期限内の新しい報がありません";
            return null;
        }

        var full = facts is not null && (baseline is null || facts.Final);
        var details = facts is null ? new List<string>() : Describe(facts, baseline, full, settings.Places);
        if (added.Count == 0 && details.Count == 0)
        {
            reason = $"{Report(facts!.Serial) ?? "続報"}は前回の読み上げから変化がありません";
            return null;
        }

        // Forecasts and warnings of one earthquake share report numbers, so name the newer one.
        var serial = Report(new[] { facts?.Serial, added.Count > 0 ? earthquake.Warning!.Source.Serial : null }
            .Select(value => ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : (ulong?)null)
            .Max()?.ToString(CultureInfo.InvariantCulture));
        var report = string.Join("、", new[] { serial, facts?.Final == true ? "最終報" : null }.OfType<string>());
        string header;
        if (!announced)
        {
            var label = earthquake.Warning is { Status: SeriesStatus.Active } ? "緊急地震速報、警報" : "緊急地震速報、予報";
            header = report.Length == 0 ? label + "。" : $"{label}、{report}。";
        }
        else
        {
            // Name the earthquake when another one was spoken since, so overlapping events stay apart.
            var lastSpoken = marks.LastOrDefault(mark => !IsCancel(mark.Kind))?.EventId;
            var name = facts?.Hypocenter ?? baseline?.Hypocenter;
            var prefix = lastSpoken != earthquake.EventId && !full && name is not null
                ? $"{ReadName(name, settings.Places)}の地震、" : "";
            header = prefix + (report.Length == 0 ? "続報" : report) + "。";
        }

        var text = header;
        if (added.Count > 0)
        {
            var names = Names(added.Select(region => region.Region), settings.Places);
            text += heard.Count == 0 ? $"警報、対象地域は、{names}。" : $"警報地域追加、{names}。";
        }
        if (details.Count > 0)
            text += string.Join("、", details) + "。";

        var warningUpdate = added.Count > 0;
        var mark = new SpeechMark(earthquake.EventId, warningUpdate ? SeriesKind.Warning : SeriesKind.Forecast,
            warningUpdate ? SpeechKind.WarningUpdate : SpeechKind.Forecast,
            warningUpdate ? earthquake.Warning!.Revision : earthquake.Forecast!.Revision,
            added.Select(region => region.Region.Key).ToArray(), now, facts);
        var deadlines = added.Select(region => region.Until).ToList();
        var issued = added.Select(region => region.Source.IssuedAt!.Value).ToList();
        if (facts is not null)
        {
            deadlines.Add(forecastUntil!.Value);
            issued.Add(earthquake.Forecast!.Source.IssuedAt!.Value);
        }
        reason = "";
        return new Announcement(earthquake.EventId, mark.Kind, text, issued.Max(), deadlines.Min(), [mark]);
    }

    private static Announcement? Cancellation(FeedEvent earthquake, SpeechSettings settings,
        IReadOnlyList<SpeechMark> marks, DateTimeOffset now, out string reason)
    {
        var history = marks.Where(mark => mark.EventId == earthquake.EventId).ToArray();
        if (!history.Any(mark => !IsCancel(mark.Kind)))
        {
            reason = "読み上げていない地震の取消です";
            return null;
        }
        var pending = new List<(SeriesKind Series, SpeechKind Kind, FeedSeries Value, DateTimeOffset Until)>();
        foreach (var series in new[] { SeriesKind.Warning, SeriesKind.Forecast })
        {
            var kind = series == SeriesKind.Warning ? SpeechKind.WarningCancel : SpeechKind.ForecastCancel;
            if (earthquake.GetSeries(series) is { Status: SeriesStatus.Canceled, Source: { InfoType: InfoType.Cancel, Origin: SourceOrigin.Live } } value
                && !history.Any(mark => mark.Kind == kind)
                && Window(value.Source, value.SpeechUntil, CancelSeconds, now) is { } until)
                pending.Add((series, kind, value, until));
        }
        if (pending.Count == 0)
        {
            reason = "取消は読み上げ済みか、開始期限を過ぎました";
            return null;
        }

        reason = "";
        var whole = earthquake.Warning is null or { Status: SeriesStatus.Canceled }
            && earthquake.Forecast is null or { Status: SeriesStatus.Canceled };
        var name = history.LastOrDefault(mark => mark.Facts?.Hypocenter is not null)?.Facts!.Hypocenter;
        var subject = name is null ? "緊急地震速報" : $"{ReadName(name, settings.Places)}の緊急地震速報";
        var label = whole ? "" : pending.Any(item => item.Series == SeriesKind.Warning) ? "、警報" : "、予報";
        var marksToSave = pending.Select(item => new SpeechMark(earthquake.EventId, item.Series, item.Kind,
            item.Value.Revision, [], now)).ToArray();
        return new Announcement(earthquake.EventId, marksToSave[0].Kind, $"先ほどお伝えした{subject}{label}は取り消されました。",
            pending.Max(item => item.Value.Source.IssuedAt!.Value), pending.Min(item => item.Until), marksToSave);
    }

    /// <summary>
    /// Lists what the listener needs to hear: every item on a first or final report, otherwise only what changed.
    /// A changed value carries its direction so the trend is clear from the voice alone.
    /// </summary>
    private static List<string> Describe(ForecastFacts facts, ForecastFacts? before, bool full,
        PlaceReadings readings)
    {
        var items = new List<string>();
        if (facts.Hypocenter is { } hypocenter)
        {
            var name = ReadName(hypocenter, readings);
            var moved = before is not null && before.Hypocenter != facts.Hypocenter;
            if (before is null || full && !moved && before.Assumed == facts.Assumed)
                items.Add(facts.Assumed ? $"{name}付近で地震、震源は仮定" : $"{name}で地震");
            else if (before.Assumed && !facts.Assumed)
                items.Add($"震源は{name}と推定");
            else if (moved)
                items.Add(facts.Assumed ? $"震源は{name}付近、仮定" : $"震源は{name}に変更");
            else if (facts.Assumed && !before.Assumed)
                items.Add("震源は仮定に変更");
        }
        // A single-station estimate is often revised or withdrawn, so say so; and say when more stations confirm it.
        if (facts.SinglePoint && (before is null || full || !before.SinglePoint))
            items.Add("1点観測");
        else if (!facts.SinglePoint && !facts.Assumed && before is { SinglePoint: true })
            items.Add("震源精度向上");

        Intensity(items, "予想最大震度", before?.MaxIntensity, facts.MaxIntensity, full);
        if (facts.Magnitude is { } magnitude)
        {
            var text = "マグニチュード" + magnitude.ToString("0.0", CultureInfo.InvariantCulture);
            if (before?.Magnitude is { } previous && Math.Abs(magnitude - previous) >= 0.05)
                items.Add(text + (magnitude > previous ? "に上昇" : "に低下"));
            else if (full || before?.Magnitude is null)
                items.Add(text);
        }
        if (facts.DepthKm is { } depth)
        {
            if (before?.DepthKm is { } previous && previous != depth)
                items.Add($"深さ{Depth(depth)}に変更");
            else if (full || before?.DepthKm is null)
                items.Add($"深さ{Depth(depth)}");
        }
        if (facts.LongPeriodClass is { } longPeriod
            && (Bounds(longPeriod).High >= 1 || before?.LongPeriodClass is { } earlier && Bounds(earlier).High >= 1))
            Intensity(items, "長周期地震動階級", before?.LongPeriodClass, longPeriod, full);

        foreach (var region in facts.Regions.OrderByDescending(region => Bounds(region.Intensity).Low).Take(MaxRegionFacts))
        {
            var previous = before?.Regions.FirstOrDefault(item => item.Key == region.Key)?.Intensity;
            Intensity(items, ReadName(region.Name, readings) + "の予想震度", previous, region.Intensity, full);
        }
        return items;
    }

    private static void Intensity(List<string> items, string label, IntensityRange? before, IntensityRange now, bool full)
    {
        var text = label + IntensityText(now);
        if (before == now)
        {
            if (full)
                items.Add(text);
            return;
        }
        if (before is null || before.Kind == IntensityKind.Unknown || now.Kind == IntensityKind.Unknown)
        {
            items.Add(text);
            return;
        }
        var (oldLow, oldHigh) = Bounds(before);
        var (newLow, newHigh) = Bounds(now);
        var direction = newLow + newHigh - oldLow - oldHigh;
        items.Add(text + (direction > 0 ? "に上昇" : direction < 0 ? "に低下" : "に変更"));
    }

    private static ForecastFacts Facts(FeedSeries series, SpeechSettings settings)
    {
        var data = series.ForecastData!;
        return new ForecastFacts(series.Source.Serial, series.Status == SeriesStatus.Final,
            string.IsNullOrWhiteSpace(data.Hypocenter?.Name) ? null : data.Hypocenter.Name,
            data.Hypocenter?.Quality == ForecastQuality.Assumed,
            data.Magnitude is { } magnitude ? Math.Round(magnitude, 1) : null, data.DepthKm,
            data.MaxIntensity, data.MaxLongPeriodClass,
            settings.Nationwide ? [] : data.RegionForecasts.Where(region => settings.Regions.Contains(region.Key))
                .Select(region => new RegionFact(region.Key, region.Name, region.Intensity)).ToArray(),
            data.Hypocenter?.Quality == ForecastQuality.LowAccuracy);
    }

    private static bool ReachesThreshold(ForecastFacts facts, SpeechSettings settings)
    {
        var threshold = Array.IndexOf(IntensityCodes, settings.ForecastThreshold);
        if (threshold <= 0)
            return true;
        return settings.Nationwide
            ? Bounds(facts.MaxIntensity).Low >= threshold
            : facts.Regions.Any(region => Bounds(region.Intensity).Low >= threshold);
    }

    private static bool WarnedInScope(FeedEvent earthquake, SpeechSettings settings)
        => earthquake.Warning is { Status: SeriesStatus.Active, WarningData: { } data }
            && (settings.Nationwide || data.ActiveRegions.Any(region => settings.Regions.Contains(region.Key)));

    /// <summary>
    /// Warning regions this listener has not heard, among <paramref name="candidates"/> (regions that live reports
    /// added), that are still inside the deadline of the report that added them.
    /// </summary>
    private static List<(WarningRegion Region, FeedSource Source, DateTimeOffset Until)> Fresh(
        FeedEvent earthquake, SpeechSettings settings, IReadOnlySet<RegionKey> heard,
        IEnumerable<RegionKey> candidates, DateTimeOffset now)
    {
        var result = new List<(WarningRegion, FeedSource, DateTimeOffset)>();
        if (earthquake.Warning is not { Status: SeriesStatus.Active, WarningData: { } data } warning
            || warning.Source.Origin != SourceOrigin.Live || IsWithdrawn(earthquake))
            return result;
        var wanted = candidates.ToHashSet();
        foreach (var region in data.ActiveRegions)
        {
            if (!wanted.Contains(region.Key) || heard.Contains(region.Key) || region.SourceRef is not { } reference
                || !(settings.Nationwide || settings.Regions.Contains(region.Key)))
                continue;
            var source = reference == warning.Source.TelegramId ? warning.Source : data.RegionSources?.GetValueOrDefault(reference);
            if (source is { Origin: SourceOrigin.Live } && Window(source, region.SpeechUntil, WarningSeconds, now) is { } until)
                result.Add((region, source, until));
        }
        return result;
    }

    /// <summary>
    /// Names the regions at one level, preferring prefectures, so a report listing a district, its prefecture and
    /// its zone reads once. Long lists end with the count of the rest.
    /// </summary>
    private static string Names(IEnumerable<WarningRegion> regions, PlaceReadings readings)
    {
        var list = regions.ToList();
        var level = Levels.FirstOrDefault(level => list.Any(region => region.Level == level)) ?? list[0].Level;
        var names = list.Where(region => region.Level == level).Select(region => ReadName(region.Name, readings))
            .Distinct().ToList();
        var text = string.Join("、", names.Take(MaxNames));
        return names.Count > MaxNames ? $"{text}、ほか{names.Count - MaxNames}地域" : text;
    }

    private static DateTimeOffset? ForecastWindow(FeedSeries series, DateTimeOffset now)
    {
        if (series is not { Status: SeriesStatus.Active or SeriesStatus.Final, ForecastData: not null,
                Source: { InfoType: InfoType.Issue, Origin: SourceOrigin.Live } })
            return null;
        // QuickRelay leaves the final report without a deadline; QRV reads it within the same window as the others.
        var deadline = series.SpeechUntil
            ?? (series.Status == SeriesStatus.Final ? series.Source.IssuedAt?.AddSeconds(ForecastSeconds) : null);
        return Window(series.Source, deadline, ForecastSeconds, now);
    }

    private static DateTimeOffset? Window(FeedSource source, DateTimeOffset? deadline, int seconds, DateTimeOffset now)
    {
        if (source.IssuedAt is not { } issued || deadline is not { } until || issued - now > TimeSpan.FromSeconds(1)
            || until <= issued || now >= until || now - issued >= TimeSpan.FromSeconds(seconds))
            return null;
        return until - issued <= TimeSpan.FromSeconds(seconds) ? until : issued.AddSeconds(seconds);
    }

    private static bool IsWithdrawn(FeedEvent earthquake)
        => earthquake.Warning?.Status == SeriesStatus.Canceled || earthquake.Forecast?.Status == SeriesStatus.Canceled;

    private static bool IsCancel(SpeechKind kind) => kind is SpeechKind.WarningCancel or SpeechKind.ForecastCancel;

    private static bool Announced(string eventId, IReadOnlyList<SpeechMark> marks)
        => marks.Any(mark => mark.EventId == eventId && !IsCancel(mark.Kind));

    private static HashSet<RegionKey> Heard(string eventId, IReadOnlyList<SpeechMark> marks)
        => marks.Where(mark => mark.EventId == eventId).SelectMany(mark => mark.Regions).ToHashSet();

    private static string? Report(string? serial) => serial is null ? null : $"第{serial}報";

    private static string Depth(int kilometers) => kilometers switch
    {
        0 => "ごく浅い",
        >= 700 => "700キロ以上",
        _ => $"{kilometers}キロ"
    };

    private static (int Low, int High) Bounds(IntensityRange intensity) => intensity.Kind switch
    {
        IntensityKind.Exact => (Code(intensity.Value), Code(intensity.Value)),
        IntensityKind.Range => (Code(intensity.Min), Code(intensity.Max)),
        IntensityKind.LowerBound => (Code(intensity.Value), IntensityCodes.Length),
        _ => (-1, -1)
    };

    private static int Code(string? value) => Array.IndexOf(IntensityCodes, value);

    private static string IntensityText(IntensityRange intensity) => intensity.Kind switch
    {
        IntensityKind.Exact => Name(intensity.Value),
        IntensityKind.Range => $"{Name(intensity.Min)}から{Name(intensity.Max)}",
        IntensityKind.LowerBound => $"{Name(intensity.Value)}以上",
        _ => "不明"
    };

    private static string Name(string? code) => Code(code) is >= 0 and var index ? IntensityNames[index] : "不明";

    private static string ReadName(string original, PlaceReadings readings)
        => !string.IsNullOrWhiteSpace(original) && original.Length <= 60 ? readings.Read(original) : "対象地域";
}
