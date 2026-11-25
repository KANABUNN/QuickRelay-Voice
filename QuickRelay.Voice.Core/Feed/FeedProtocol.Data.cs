using System.Collections.ObjectModel;
using System.Text.Json;

namespace QuickRelay.Voice.Core.Feed;

public static partial class FeedProtocol
{
    private static WarningData ParseWarning(JsonElement data, JsonElement series, FeedSource current)
    {
        data = Object(data, "warning.data");
        var coverage = Text(data, "coverage") switch
        {
            "complete" => WarningCoverage.Complete,
            "partial" => WarningCoverage.Partial,
            _ => throw Invalid("coverage")
        };
        var regionSources = ParseRegionSources(series, current);
        var active = ParseWarningRegions(data, "activeRegions", requireProvenance: true);
        foreach (var region in active)
        {
            if (region.SourceRef is { } sourceRef)
            {
                if (!regionSources.TryGetValue(sourceRef, out var source))
                {
                    throw Invalid("sourceRef");
                }
                VerifySpeechDeadline(source, region.SpeechUntil, 15);
            }
            else if (region.SpeechUntil is not null || coverage == WarningCoverage.Complete)
            {
                throw Invalid("sourceRef");
            }
        }

        var activeByKey = active.ToDictionary(region => region.Key);
        var report = ParseWarningRegions(data, "reportRegions", requireProvenance: false);
        var reportedKeys = report.Select(region => region.Key).ToHashSet();
        var additions = ParseWarningRegions(data, "addedRegions", requireProvenance: false);
        var resolvedAdditions = new List<WarningRegion>(additions.Count);
        foreach (var addition in additions)
        {
            if (!reportedKeys.Contains(addition.Key) || !activeByKey.TryGetValue(addition.Key, out var region) ||
                region.SourceRef != current.TelegramId || !MatchesProvenance(addition, region))
            {
                throw Invalid("addedRegions");
            }
            resolvedAdditions.Add(region);
        }

        foreach (var region in report)
        {
            if (!activeByKey.TryGetValue(region.Key, out var activeRegion) || !MatchesProvenance(region, activeRegion))
            {
                throw Invalid("reportRegions");
            }
        }

        return new WarningData(coverage, report, resolvedAdditions.AsReadOnly(), active,
            new ReadOnlyDictionary<string, FeedSource>(regionSources));
    }

    private static bool MatchesProvenance(WarningRegion given, WarningRegion original) =>
        given.Level == original.Level && (given.SourceRef is null || given.SourceRef == original.SourceRef) &&
        (given.SpeechUntil is null || given.SpeechUntil == original.SpeechUntil);

    private static Dictionary<string, FeedSource> ParseRegionSources(JsonElement series, FeedSource current)
    {
        var sources = new Dictionary<string, FeedSource>(StringComparer.Ordinal) { [current.TelegramId] = current };
        if (Optional(series, "regionSources") is not { } value)
        {
            return sources;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                var source = ParseSource(item, SeriesKind.Warning);
                AddRegionSource(sources, source.TelegramId, source);
            }
        }
        else
        {
            foreach (var item in Object(value, "regionSources").EnumerateObject())
            {
                AddRegionSource(sources, item.Name, ParseSource(item.Value, SeriesKind.Warning));
            }
        }
        return sources;
    }

    private static void AddRegionSource(Dictionary<string, FeedSource> sources, string key, FeedSource source)
    {
        if (key != source.TelegramId || source.InfoType == InfoType.Cancel ||
            !sources.TryAdd(key, source) && sources[key] != source)
        {
            throw Invalid("regionSources");
        }
    }

    private static IReadOnlyList<WarningRegion> ParseWarningRegions(JsonElement data, string name, bool requireProvenance)
    {
        var keys = new HashSet<RegionKey>();
        var regions = new List<WarningRegion>();
        foreach (var element in Array(Required(data, name), name))
        {
            var value = Object(element, name);
            var key = ParseRegionKey(value);
            if (!keys.Add(key))
            {
                throw Invalid(name);
            }

            if (requireProvenance)
            {
                _ = Required(value, "sourceRef");
                _ = Required(value, "speechUntil");
            }
            var sourceRef = OptionalText(value, "sourceRef");
            var deadline = Optional(value, "speechUntil") is { } time ? ReadTime(time, "speechUntil") : (DateTimeOffset?)null;
            regions.Add(new WarningRegion(key, Text(value, "name"), Text(value, "level"), sourceRef, deadline));
        }
        return regions.AsReadOnly();
    }

    private static ForecastData ParseForecast(JsonElement data)
    {
        data = Object(data, "forecast.data");
        var hypocenter = Optional(data, "hypocenter") is { } value ? ParseHypocenter(value) : null;
        return new ForecastData(hypocenter, OptionalNumber(data, "magnitude"), OptionalNonnegativeInt(data, "depthKm"),
            OptionalIntensity(data, "maxIntensity"), ParseRegionForecasts(data), OptionalIntensity(data, "maxLongPeriodClass", longPeriod: true));
    }

    private static ForecastHypocenter ParseHypocenter(JsonElement value)
    {
        var obj = Object(value, "hypocenter");
        var quality = OptionalText(obj, "quality") switch
        {
            "estimated" => ForecastQuality.Estimated,
            "assumed" => ForecastQuality.Assumed,
            "low_accuracy" => ForecastQuality.LowAccuracy,
            "unknown" or null => ForecastQuality.Unknown,
            _ => throw Invalid("hypocenter.quality")
        };
        return new ForecastHypocenter(OptionalText(obj, "code"), OptionalText(obj, "name"), quality,
            OptionalNonnegativeInt(obj, "observationPoints"));
    }

    private static IReadOnlyList<RegionForecast> ParseRegionForecasts(JsonElement data)
    {
        if (Optional(data, "regionForecasts") is not { } entries)
        {
            return [];
        }

        var keys = new HashSet<RegionKey>();
        var regions = new List<RegionForecast>();
        foreach (var item in Array(entries, "regionForecasts"))
        {
            var obj = Object(item, "regionForecasts");
            var key = ParseRegionKey(obj);
            if (!keys.Add(key))
            {
                throw Invalid("regionForecasts");
            }

            regions.Add(new RegionForecast(key, Text(obj, "name"), Text(obj, "level"), OptionalIntensity(obj, "intensity"),
                Optional(obj, "arrivalTime") is { } time ? ReadTime(time, "arrivalTime") : null,
                OptionalText(obj, "arrivalCondition")));
        }
        return regions.AsReadOnly();
    }

    private static RegionKey ParseRegionKey(JsonElement value) => new(Text(value, "codeType"), Text(value, "code"));

    private static double? OptionalNumber(JsonElement obj, string key)
    {
        if (Optional(obj, key) is not { } value)
        {
            return null;
        }
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double result) && double.IsFinite(result)
            ? result : throw Invalid(key);
    }

    private static int? OptionalNonnegativeInt(JsonElement obj, string key) => Optional(obj, key) is null
        ? null : Integer(obj, key) is >= 0 and var value ? value : throw Invalid(key);

    private static IntensityRange? OptionalIntensity(JsonElement data, string key, bool longPeriod = false)
    {
        if (Optional(data, key) is not { } value)
        {
            return null;
        }

        var obj = Object(value, key);
        var kind = Text(obj, "kind");
        var exact = OptionalText(obj, "value");
        var min = OptionalText(obj, "min");
        var max = OptionalText(obj, "max");
        if (kind == "unknown" && exact is null && min is null && max is null)
        {
            return new IntensityRange(IntensityKind.Unknown);
        }
        if (kind == "exact" && IntensityRank(exact, longPeriod) >= 0 && min is null && max is null)
        {
            return new IntensityRange(IntensityKind.Exact, Value: exact);
        }
        if (kind == "lowerBound" && max is null && (exact is null || min is null) && IntensityRank(exact ?? min, longPeriod) >= 0)
        {
            return new IntensityRange(IntensityKind.LowerBound, Value: exact ?? min);
        }
        if (kind == "range" && exact is null && IntensityRank(min, longPeriod) >= 0 &&
            IntensityRank(max, longPeriod) > IntensityRank(min, longPeriod))
        {
            return new IntensityRange(IntensityKind.Range, Min: min, Max: max);
        }
        throw Invalid(key);
    }

    private static int IntensityRank(string? code, bool longPeriod) => code switch
    {
        "0" => 0,
        "1" => 1,
        "2" => 2,
        "3" => 3,
        "4" => 4,
        "5-" when !longPeriod => 5,
        "5+" when !longPeriod => 6,
        "6-" when !longPeriod => 7,
        "6+" when !longPeriod => 8,
        "7" when !longPeriod => 9,
        _ => -1
    };
}
