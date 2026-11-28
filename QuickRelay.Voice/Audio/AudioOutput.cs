using System.Collections.Concurrent;

namespace QuickRelay.Voice.Audio;

public sealed record AudioOptions
{
    public string EngineUrl { get; init; } = "http://127.0.0.1:10101";
    public string? SpeakerUuid { get; init; }
    public string? SpeakerName { get; init; }
    public string StyleName { get; init; } = "ノーマル";
    public int? StyleId { get; init; }
    public int? OutputDevice { get; init; }
    public VoiceTuning Tuning { get; init; } = new();
    public IReadOnlyList<string> WarmupTexts { get; init; } = [];
}

public sealed record AivisVoice(string SpeakerUuid, string SpeakerName, string StyleName, int StyleId);
public sealed record AudioDevice(int Index, string Name);

public enum AudioSignal { Alert, EngineError }

public sealed class SpeechEngineException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class AudioPlaybackException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class AudioOutput : IAsyncDisposable
{
    private const string TestText = "クイックリレーボイスの読み上げテストです。";
    private readonly AudioOptions options;
    private readonly string[] warmupTexts;
    private readonly AivisClient engine;
    private readonly SemaphoreSlim engineLock = new(1);
    private readonly SemaphoreSlim playbackLock = new(1);
    private readonly CancellationTokenSource stopping = new();
    private readonly ConcurrentDictionary<string, WaveClip> prepared = new(StringComparer.Ordinal);
    private readonly WaveClip alert;
    private readonly WaveClip engineError;
    private AivisVoice? voice;
    private int disposed;

    public AudioOutput(AudioOptions options, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Tuning is not { IsValid: true })
            throw new ArgumentOutOfRangeException(nameof(options), "AivisSpeechの音声パラメーターが範囲外です。");
        if (options.OutputDevice < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "出力機器の番号が無効です。");

        this.options = options;
        warmupTexts = options.WarmupTexts.Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (warmupTexts.Length > 16 || warmupTexts.Any(text => text.Length > AivisClient.MaxTextLength))
            throw new ArgumentException("固定音声は最大16件、1件500文字です。", nameof(options));
        engine = new AivisClient(options, httpClient);
        alert = WaveClip.CreateSignal(AudioSignal.Alert, options.Tuning.Volume);
        engineError = WaveClip.CreateSignal(AudioSignal.EngineError, options.Tuning.Volume);
    }

    public static IReadOnlyList<AudioDevice> ListDevices() => WinMmPlayer.ListDevices();

    public async Task<IReadOnlyList<AivisVoice>> ListVoicesAsync(CancellationToken ct)
    {
        using var operation = Link(ct);
        var token = operation.Token;
        await engineLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await engine.ListVoicesAsync(token).ConfigureAwait(false);
        }
        finally
        {
            engineLock.Release();
        }
    }

    public async Task PrepareAsync(CancellationToken ct)
    {
        using var operation = Link(ct);
        var token = operation.Token;
        await engineLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            voice ??= await engine.SelectVoiceAsync(token).ConfigureAwait(false);
        }
        finally
        {
            engineLock.Release();
        }

        foreach (var text in warmupTexts)
        {
            if (prepared.ContainsKey(text))
                continue;
            await SynthesizeAsync(text, token, cache: true).ConfigureAwait(false);
        }
    }

    public async Task<bool> SpeakAsync(
        string text, Func<CancellationToken, ValueTask<bool>> mayStart, CancellationToken ct)
    {
        using var operation = Link(ct);
        var token = operation.Token;
        if (!prepared.TryGetValue(text, out var clip))
            clip = await SynthesizeAsync(text, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return await PlayAsync(clip, mayStart, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts synthesizing <paramref name="text"/> at once, so it can be made while other audio plays. Play the
    /// result with <see cref="SpeakAsync(Task{WaveClip}, Func{CancellationToken, ValueTask{bool}}, CancellationToken)"/>,
    /// and cancel <paramref name="ct"/> when it will not be played, which frees the engine for other speech.
    /// </summary>
    internal async Task<WaveClip> SynthesizeAheadAsync(string text, CancellationToken ct)
    {
        using var operation = Link(ct);
        return prepared.TryGetValue(text, out var clip) ? clip
            : await SynthesizeAsync(text, operation.Token).ConfigureAwait(false);
    }

    /// <summary>Plays speech started with <see cref="SynthesizeAheadAsync"/>, waiting for it if it is not ready.</summary>
    internal async Task<bool> SpeakAsync(
        Task<WaveClip> synthesis, Func<CancellationToken, ValueTask<bool>> mayStart, CancellationToken ct)
    {
        using var operation = Link(ct);
        var token = operation.Token;
        var clip = await synthesis.WaitAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return await PlayAsync(clip, mayStart, token).ConfigureAwait(false);
    }

    public async Task<bool> PlayPreparedAsync(
        string text, Func<CancellationToken, ValueTask<bool>> mayStart, CancellationToken ct)
    {
        using var operation = Link(ct);
        operation.Token.ThrowIfCancellationRequested();
        return prepared.TryGetValue(text, out var clip)
            && await PlayAsync(clip, mayStart, operation.Token).ConfigureAwait(false);
    }

    public async Task<bool> PlaySignalAsync(
        AudioSignal signal, Func<CancellationToken, ValueTask<bool>> mayStart, CancellationToken ct)
    {
        using var operation = Link(ct);
        var clip = signal switch
        {
            AudioSignal.Alert => alert,
            AudioSignal.EngineError => engineError,
            _ => throw new ArgumentOutOfRangeException(nameof(signal)),
        };
        return await PlayAsync(clip, mayStart, operation.Token).ConfigureAwait(false);
    }

    public Task TestAsync(CancellationToken ct) =>
        SpeakAsync(TestText, _ => ValueTask.FromResult(true), ct);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        await stopping.CancelAsync().ConfigureAwait(false);
        await playbackLock.WaitAsync().ConfigureAwait(false);
        playbackLock.Release();
        await engineLock.WaitAsync().ConfigureAwait(false);
        engine.Dispose();
        engineLock.Release();
        // Keep the cancellation source available to any operation already racing with shutdown.
    }

    private CancellationTokenSource Link(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return CancellationTokenSource.CreateLinkedTokenSource(stopping.Token, ct);
    }

    private async Task<WaveClip> SynthesizeAsync(string text, CancellationToken ct, bool cache = false)
    {
        await engineLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (prepared.TryGetValue(text, out var cached))
                return cached;
            voice ??= await engine.SelectVoiceAsync(ct).ConfigureAwait(false);
            var clip = await engine.SynthesizeAsync(text, voice.StyleId, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (cache)
                prepared.TryAdd(text, clip);
            return clip;
        }
        catch (SpeechEngineException)
        {
            voice = null;
            throw;
        }
        finally
        {
            engineLock.Release();
        }
    }

    private async Task<bool> PlayAsync(
        WaveClip clip, Func<CancellationToken, ValueTask<bool>> mayStart, CancellationToken ct)
    {
        await playbackLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await WinMmPlayer.PlayAsync(clip, options.OutputDevice, mayStart, ct).ConfigureAwait(false);
        }
        finally
        {
            playbackLock.Release();
        }
    }
}
