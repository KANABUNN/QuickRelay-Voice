using System.Collections.ObjectModel;
using System.Text.Json;

namespace QuickRelay.Voice.Core.Feed;

public static partial class FeedProtocol
{
    private static WarningData ParseWarning(JsonElement data, FeedSource current)
    {
        data = Object(data, "warning.data");
        var coverage = Text(data, "coverage") switch
        {
            "complete" => WarningCoverage.Complete,
            "partial" => WarningCoverage.Partial,
            _ => throw Invalid("coverage")
        };
        var regionSources = ParseRegionSources(data, current);
        var active = ParseWarningRegions(data, "activeRegions", requireProvenance: true);
        foreach (var region in active)
        {
            if (region.SourceRef is { } sourceRef)
            {
                var source = sourceRef == current.TelegramId ? current : regionSources.GetValueOrDefault(sourceRef);
                if (source is null)
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
                region.SourceRef != current.TelegramId || addition.Level != region.Level)
            {
                throw Invalid("addedRegions");
            }
            resolvedAdditions.Add(region);
        }

        foreach (var region in report)
        {
            if (!activeByKey.ContainsKey(region.Key))
            {
                throw Invalid("reportRegions");
            }
        }

        return new WarningData(coverage, report, resolvedAdditions.AsReadOnly(), active,
            new ReadOnlyDictionary<string, FeedSource>(regionSources));
    }

    private static Dictionary<string, FeedSource> ParseRegionSources(JsonElement data, FeedSource current)
    {
        var sources = new Dictionary<string, FeedSource>(StringComparer.Ordinal);
        foreach (var item in Array(Required(data, "regionSources"), "regionSources"))
        {
            var source = ParseSource(item, SeriesKind.Warning);
            if (source.TelegramId == current.TelegramId || source.InfoType == InfoType.Cancel
                || !sources.TryAdd(source.TelegramId, source))
                throw Invalid("regionSources");
        }
        return sources;
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

            var level = Text(value, "level");
            if (level is not ("zone" or "prefecture" or "region"))
                throw Invalid("level");
            string? sourceRef = null;
            DateTimeOffset? deadline = null;
            if (requireProvenance)
            {
                var reference = Required(value, "sourceRef");
                sourceRef = reference.ValueKind == JsonValueKind.Null ? null : StringValue(reference, "sourceRef");
                deadline = ReadNullableTime(Required(value, "speechUntil"), "speechUntil");
            }
            regions.Add(new WarningRegion(key, Label(value, "name"), level, sourceRef, deadline));
        }
        return regions.AsReadOnly();
    }

    private static ForecastData ParseForecast(JsonElement data)
    {
        data = Object(data, "forecast.data");
        var location = Required(data, "hypocenter");
        var hypocenter = location.ValueKind == JsonValueKind.Null ? null : ParseHypocenter(location);
        var originTime = ReadNullableTime(Required(data, "originTime"), "originTime");
        var magnitude = ReadNullableNumber(data, "magnitude");
        var depth = Required(data, "depthKm").ValueKind == JsonValueKind.Null ? (int?)null : NonnegativeInt(data, "depthKm");
        if (hypocenter?.Quality == ForecastQuality.Assumed && (originTime is not null || magnitude is not null || depth is not null))
            throw Invalid("assumed forecast data");
        return new ForecastData(hypocenter, magnitude, depth, ParseIntensity(Required(data, "maxIntensity"), "maxIntensity"),
            ParseRegionForecasts(data), ReadNullableIntensity(data, "maxLongPeriodClass"), originTime);
    }

    private static ForecastHypocenter ParseHypocenter(JsonElement value)
    {
        var obj = Object(value, "hypocenter");
        var quality = Object(Required(obj, "quality"), "quality");
        var kind = Text(quality, "kind") switch
        {
            "estimated" => ForecastQuality.Estimated,
            "assumed" => ForecastQuality.Assumed,
            "lowAccuracy" => ForecastQuality.LowAccuracy,
            _ => throw Invalid("hypocenter.quality")
        };
        var points = quality.TryGetProperty("magnitudePoints", out _) ? NonnegativeInt(quality, "magnitudePoints") : (int?)null;
        return new ForecastHypocenter(Label(obj, "code"), Label(obj, "name"), kind, points,
            ReadNullableNumber(obj, "latitude"), ReadNullableNumber(obj, "longitude"),
            OptionalLabel(quality, "epicenterRank"), OptionalLabel(quality, "epicenterRank2"),
            OptionalLabel(quality, "depthRank"), OptionalLabel(quality, "magnitudeRank"));
    }

    private static IReadOnlyList<RegionForecast> ParseRegionForecasts(JsonElement data)
    {
        var keys = new HashSet<RegionKey>();
        var regions = new List<RegionForecast>();
        foreach (var item in Array(Required(data, "regionForecasts"), "regionForecasts"))
        {
            var obj = Object(item, "regionForecasts");
            var key = ParseRegionKey(obj);
            if (!keys.Add(key))
            {
                throw Invalid("regionForecasts");
            }

            var arrival = Object(Required(obj, "arrival"), "arrival");
            var condition = Text(arrival, "condition") switch
            {
                "predicted" => ArrivalCondition.Predicted,
                "arrived" => ArrivalCondition.Arrived,
                "noPrediction" => ArrivalCondition.NoPrediction,
                "unknown" => ArrivalCondition.Unknown,
                _ => throw Invalid("arrival.condition")
            };
            var time = ReadNullableTime(Required(arrival, "time"), "arrival.time");
            var warning = Required(obj, "isWarning").ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw Invalid("isWarning")
            };
            regions.Add(new RegionForecast(key, Label(obj, "name"), ParseIntensity(Required(obj, "maxIntensity"), "maxIntensity"),
                time, condition, ReadNullableIntensity(obj, "maxLongPeriodClass"), OptionalLabel(arrival, "text"),
                OptionalLabel(obj, "prefectureCode"), OptionalLabel(obj, "prefectureName"), OptionalLabel(obj, "kindCode"),
                OptionalLabel(obj, "kindName"), warning));
        }
        return regions.AsReadOnly();
    }

    private static RegionKey ParseRegionKey(JsonElement value) => new(Text(value, "codeType"), Text(value, "code"));

    private static double? ReadNullableNumber(JsonElement obj, string key)
    {
        var value = Required(obj, key);
        if (value.ValueKind == JsonValueKind.Null)
            return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double result) && double.IsFinite(result)
            ? result : throw Invalid(key);
    }

    private static int NonnegativeInt(JsonElement obj, string key)
        => Integer(obj, key) is >= 0 and var value ? value : throw Invalid(key);

    private static IntensityRange? ReadNullableIntensity(JsonElement data, string key)
    {
        var value = Required(data, key);
        return value.ValueKind == JsonValueKind.Null ? null : ParseIntensity(value, key, longPeriod: true);
    }

    private static IntensityRange ParseIntensity(JsonElement value, string key, bool longPeriod = false)
    {
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
