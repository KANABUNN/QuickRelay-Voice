using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuickRelay.Voice.Audio;

internal sealed class AivisClient : IDisposable
{
    internal const int MaxTextLength = 500;
    private const int MaxMetadataBytes = 4 * 1024 * 1024;
    private const int MaxWaveBytes = 16 * 1024 * 1024;
    private readonly AudioOptions options;
    private readonly Uri endpoint;
    private readonly HttpClient http;
    private readonly bool ownsHttp;

    internal AivisClient(AudioOptions options, HttpClient? http)
    {
        if (!Uri.TryCreate(options.EngineUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || !uri.IsLoopback
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.AbsolutePath != "/")
            throw new ArgumentException("AivisSpeechエンジンにローカルのHTTP(S) URLを指定してください。", nameof(options));

        this.options = options;
        endpoint = uri;
        ownsHttp = http is null;
        this.http = http ?? new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(2),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public void Dispose()
    {
        if (ownsHttp)
            http.Dispose();
    }

    internal async Task<IReadOnlyList<AivisVoice>> ListVoicesAsync(CancellationToken ct)
    {
        var payload = await RequestAsync(HttpMethod.Get, "speakers", "話者一覧", null, MaxMetadataBytes,
            TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(payload);
            List<AivisVoice> voices = [];
            foreach (var speaker in document.RootElement.EnumerateArray())
            {
                var name = RequiredText(speaker, "name");
                var uuid = RequiredText(speaker, "speaker_uuid");
                foreach (var style in speaker.GetProperty("styles").EnumerateArray())
                {
                    if (style.TryGetProperty("type", out var type)
                        && type.ValueKind != JsonValueKind.Null && type.GetString() != "talk")
                        continue;
                    voices.Add(new AivisVoice(uuid, name, RequiredText(style, "name"), style.GetProperty("id").GetInt32()));
                }
            }
            return voices.AsReadOnly();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new SpeechEngineException("AivisSpeechの話者一覧を読み取れませんでした。", ex);
        }
    }

    internal async Task<AivisVoice> SelectVoiceAsync(CancellationToken ct)
    {
        var voices = await ListVoicesAsync(ct).ConfigureAwait(false);
        AivisVoice? voice = null;

        if (!string.IsNullOrWhiteSpace(options.SpeakerUuid))
            voice = FindVoice(voices, candidate => candidate.SpeakerUuid.Equals(options.SpeakerUuid, StringComparison.OrdinalIgnoreCase)
                && candidate.StyleName == options.StyleName);
        if (voice is null && !string.IsNullOrWhiteSpace(options.SpeakerName))
            voice = FindVoice(voices, candidate => candidate.SpeakerName == options.SpeakerName
                && candidate.StyleName == options.StyleName);
        if (voice is null && options.StyleId is int styleId)
            voice = FindVoice(voices, candidate => candidate.StyleId == styleId);

        if (voice is null && string.IsNullOrWhiteSpace(options.SpeakerUuid)
            && string.IsNullOrWhiteSpace(options.SpeakerName) && options.StyleId is null)
            voice = voices.FirstOrDefault(candidate => candidate.StyleName == options.StyleName) ?? voices.FirstOrDefault();

        return voice ?? throw new SpeechEngineException("設定に一致するAivisSpeechの話者が見つかりませんでした。");
    }

    internal async Task<WaveClip> SynthesizeAsync(string text, int styleId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength)
            throw new SpeechEngineException("読み上げ文の長さが1〜500文字の範囲外です。");

        var speaker = styleId.ToString(CultureInfo.InvariantCulture);
        var queryUri = $"audio_query?speaker={speaker}&text={Uri.EscapeDataString(text)}";
        var data = await RequestAsync(HttpMethod.Post, queryUri, "音声クエリ生成", null, MaxMetadataBytes,
            TimeSpan.FromSeconds(6), ct).ConfigureAwait(false);
        JsonObject query;
        try
        {
            query = JsonNode.Parse(data) as JsonObject ?? throw new JsonException();
            if (query.ContainsKey("speedScale"))
                query["speedScale"] = options.Speed;
            if (query.ContainsKey("volumeScale"))
                query["volumeScale"] = options.Volume;
        }
        catch (JsonException ex)
        {
            throw new SpeechEngineException("AivisSpeechの音声クエリを読み取れませんでした。", ex);
        }

        var wav = await RequestAsync(HttpMethod.Post, $"synthesis?speaker={speaker}", "音声合成", query, MaxWaveBytes,
            TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return WaveClip.Parse(wav);
    }

    private async Task<byte[]> RequestAsync(
        HttpMethod method, string path, string operation, JsonObject? body, int maxBytes, TimeSpan timeout, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(timeout);
                using var request = new HttpRequestMessage(method, new Uri(endpoint, path));
                if (body is not null)
                    request.Content = JsonContent.Create(body, new MediaTypeHeaderValue("application/json"));
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    if (attempt == 0 && response.StatusCode >= HttpStatusCode.InternalServerError)
                        throw new HttpRequestException("Transient AivisSpeech server error.");
                    throw new SpeechEngineException($"AivisSpeechの{operation}に失敗しました (HTTP {(int)response.StatusCode})。");
                }
                return await ReadBoundedAsync(response.Content, maxBytes, deadline.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException
                || ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                if (attempt != 0)
                    throw new SpeechEngineException($"AivisSpeechの{operation}に失敗しました。", ex);
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        if (content.Headers.ContentLength > maxBytes)
            throw new SpeechEngineException("AivisSpeechの応答が大きすぎます。");
        using var buffer = new MemoryStream();
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var block = new byte[32 * 1024];
        int read;
        while ((read = await stream.ReadAsync(block, ct).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new SpeechEngineException("AivisSpeechの応答が大きすぎます。");
            buffer.Write(block.AsSpan(0, read));
        }
        return buffer.ToArray();
    }

    private static string RequiredText(JsonElement element, string property)
    {
        var value = element.GetProperty(property).GetString();
        return !string.IsNullOrWhiteSpace(value) ? value : throw new JsonException();
    }

    private static AivisVoice? FindVoice(IEnumerable<AivisVoice> voices, Func<AivisVoice, bool> matches)
    {
        var found = voices.Where(matches).Take(2).ToArray();
        if (found.Length > 1)
            throw new SpeechEngineException("設定に一致するAivisSpeechの話者が複数あります。");
        return found.FirstOrDefault();
    }
}
