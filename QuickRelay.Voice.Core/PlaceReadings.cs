namespace QuickRelay.Voice.Core;

/// <summary>
/// Pronunciations for place names that AivisSpeech misreads. Each entry replaces its key wherever it appears inside a
/// name, the longest key first, so "渡島" fixes every 渡島 district and "上中越" wins over "中越". Only place names pass
/// through here; report numbers and figures never do.
/// </summary>
public sealed class PlaceReadings
{
    private const int MaxPronunciation = 60;

    /// <summary>
    /// Misreadings found by AivisSpeech Engine 1.1.0-dev on JMA epicenter and area names (2026-11-27). Each
    /// pronunciation is the spelling the engine reads cleanly: katakana mostly, but hiragana "へ" would be read as the
    /// particle "え", and some katakana splits a word, so a few keep hiragana or kanji. Keys stay as long as needed to
    /// avoid other names: "道南" alone would also match 東海道南方沖 and 北海道南西沖. Run QuickRelay.Voice.ReadingCheck
    /// after updating the engine or the model to find new misreadings.
    /// </summary>
    private static readonly Dictionary<string, string> Defaults = new(StringComparer.Ordinal)
    {
        ["渡島"] = "オシマ",
        ["後志"] = "シリベシ",
        ["中北部"] = "チュウホクブ",
        ["北海道道南"] = "北海道ドウナン",
        ["北海道道北"] = "北海道ドウホク",
        ["中越"] = "チュウエツ",
        ["上中越"] = "じょうちゅうえつ",
        ["平野部"] = "へいや部",
        ["山沿い"] = "ヤマゾイ",
        ["十島村"] = "としま村",
        ["久米島"] = "クメジマ",
    };

    private readonly KeyValuePair<string, string>[] entries;

    private PlaceReadings(IEnumerable<KeyValuePair<string, string>> source)
        => entries = source.OrderByDescending(entry => entry.Key.Length).ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .ToArray();

    public static PlaceReadings Default { get; } = new(Defaults);

    public IReadOnlyDictionary<string, string> Entries => entries.ToDictionary(StringComparer.Ordinal);

    /// <summary>
    /// Adds the entries of readings.json, replacing defaults with the same key. Entries with an empty key, or a
    /// pronunciation that is empty, longer than 60 characters or holds control characters, are skipped.
    /// </summary>
    public PlaceReadings With(IReadOnlyDictionary<string, string>? overrides)
    {
        if (overrides is null || overrides.Count == 0)
            return this;
        var merged = new Dictionary<string, string>(Entries, StringComparer.Ordinal);
        foreach (var (key, pronunciation) in overrides)
            if (IsValid(key, pronunciation))
                merged[key] = pronunciation;
        return new PlaceReadings(merged);
    }

    public string Read(string name)
    {
        var result = new System.Text.StringBuilder(name.Length);
        var index = 0;
        while (index < name.Length)
        {
            var matched = false;
            foreach (var (key, pronunciation) in entries)
            {
                if (!name.AsSpan(index).StartsWith(key, StringComparison.Ordinal))
                    continue;
                result.Append(pronunciation);
                index += key.Length;
                matched = true;
                break;
            }
            if (!matched)
                result.Append(name[index++]);
        }
        return result.ToString();
    }

    private static bool IsValid(string? key, string? pronunciation)
        => !string.IsNullOrWhiteSpace(key) && !key.Any(char.IsControl)
            && !string.IsNullOrWhiteSpace(pronunciation) && pronunciation.Length <= MaxPronunciation
            && !pronunciation.Any(char.IsControl);
}
