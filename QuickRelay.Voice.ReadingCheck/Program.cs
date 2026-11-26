using System.Text;
using System.Text.Json;
using QuickRelay.Voice.Core;

// Prints how AivisSpeech reads JMA place names, before and after QRV's pronunciation dictionary, for a person to check.
// Run it after updating the engine or the model, and add any misreading to PlaceReadings or readings.json.
//
//   dotnet run --project .\QuickRelay.Voice.ReadingCheck -- [names.txt ...] [--port 10101] [--style <ID>] [--readings <path>]
//
// Without arguments it reads the bundled place-names.txt, and takes the port, the style and readings.json from
// %LOCALAPPDATA%\QuickRelay Voice, as QRV does.
Console.OutputEncoding = Encoding.UTF8;
var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuickRelay Voice");
var files = new List<string>();
int? port = null, style = null;
string? readingsPath = null;
for (var index = 0; index < args.Length; index++)
{
    switch (args[index])
    {
        case "--port" when index + 1 < args.Length:
            port = int.Parse(args[++index]);
            break;
        case "--style" when index + 1 < args.Length:
            style = int.Parse(args[++index]);
            break;
        case "--readings" when index + 1 < args.Length:
            readingsPath = args[++index];
            break;
        default:
            files.Add(args[index]);
            break;
    }
}

using (var saved = OpenJson(Path.Combine(folder, "settings.json")))
{
    if (saved?.RootElement.TryGetProperty("AivisPort", out var savedPort) == true && savedPort.TryGetInt32(out var number))
        port ??= number;
    if (saved?.RootElement.TryGetProperty("StyleId", out var savedStyle) == true && savedStyle.TryGetInt32(out var id))
        style ??= id;
}
readingsPath ??= Path.Combine(folder, "readings.json");
var overrides = File.Exists(readingsPath)
    ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(readingsPath)) : null;
var readings = PlaceReadings.Default.With(overrides);
if (files.Count == 0)
    files.Add(Path.Combine(AppContext.BaseDirectory, "place-names.txt"));
var names = files.SelectMany(File.ReadLines).Select(line => line.Trim())
    .Where(line => line.Length > 0 && !line.StartsWith('#')).Distinct().ToList();

using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port ?? 10101}"), Timeout = TimeSpan.FromSeconds(15) };
style ??= await FirstStyleAsync(http);
Console.Error.WriteLine($"エンジン {http.BaseAddress}　スタイル {style}　名称 {names.Count} 件　"
    + $"辞書 {readings.Entries.Count} 件{(overrides is null ? "" : $"（{readingsPath} を含む）")}");
Console.WriteLine("名称\t読み（辞書なし）\t辞書適用後の名称\t読み（辞書適用後）");
var changed = 0;
foreach (var name in names)
{
    var spoken = readings.Read(name);
    var before = await ReadAsync(http, name, style.Value);
    if (spoken == name)
    {
        Console.WriteLine($"{name}\t{before}\t\t");
        continue;
    }
    changed++;
    Console.WriteLine($"{name}\t{before}\t{spoken}\t{await ReadAsync(http, spoken, style.Value)}");
}
Console.Error.WriteLine($"辞書で読みを変えた名称 {changed} 件。読み（辞書なし）が正しいか、変えた名称は辞書適用後の読みが正しいかを確かめてください。");

static async Task<string> ReadAsync(HttpClient http, string text, int style)
{
    var query = $"/accent_phrases?text={Uri.EscapeDataString(text)}&speaker={style}";
    using var response = await http.PostAsync(query, content: null);
    response.EnsureSuccessStatusCode();
    using var phrases = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
    return string.Join("/", phrases.RootElement.EnumerateArray().Select(phrase =>
        string.Concat(phrase.GetProperty("moras").EnumerateArray().Select(mora => mora.GetProperty("text").GetString()))));
}

static async Task<int> FirstStyleAsync(HttpClient http)
{
    using var speakers = JsonDocument.Parse(await http.GetStringAsync("/speakers"));
    return speakers.RootElement.EnumerateArray().SelectMany(speaker => speaker.GetProperty("styles").EnumerateArray())
        .First().GetProperty("id").GetInt32();
}

static JsonDocument? OpenJson(string path)
{
    try { return File.Exists(path) ? JsonDocument.Parse(File.ReadAllText(path)) : null; }
    catch (JsonException) { return null; }
}
