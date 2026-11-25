using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using QuickRelay.Voice.Audio;
using QuickRelay.Voice.Core;
using QuickRelay.Voice.Core.Feed;

namespace QuickRelay.Voice;

public sealed class VoiceController : IDisposable
{
    private const int QueueLimit = 64;
    private const int MarkLimit = 10_000;
    private const long StateMaxBytes = 48 * 1024 * 1024;
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim speechReady = new(0, 1);
    private readonly List<SpeechJob> queue = [];
    private readonly VoiceFeedClient feed = new();
    private readonly Task speechWorker;
    private AppSettings settings;
    private SpeechSettings policy = new(true, new HashSet<RegionKey>(), false, "4", 1);
    private VoiceStatus status;
    private bool hasToken, synchronized, recoverCancellations, currentStarted, warningPrepared, storageHealthy = true;
    private HealthMessage? health;
    private long healthTick;
    private FeedState? state;
    private List<SpeechMark> marks = [];
    private string markFile = "";
    private AudioOutput? audio;
    private SpeechJob? current;
    private CancellationTokenSource? playing, manualTest, connection;
    private Task playCompletion = Task.CompletedTask;
    private Task connectionTask = Task.CompletedTask;

    public VoiceController()
    {
        status = new VoiceStatus();
        try
        {
            settings = LocalFiles.Read<AppSettings>("settings.json", 64 * 1024) ?? new();
            if (!string.IsNullOrWhiteSpace(settings.FeedUrl))
                settings.Validate();
            else
                settings = new AppSettings { Muted = settings.Muted };
            hasToken = CredentialVault.Read(settings.Scope) is not null;
        }
        catch (Exception ex) when (IsStorageError(ex) || ex is Win32Exception or ArgumentException)
        {
            settings = new();
            status = status with { Connection = "保存済みの設定を読み込めません" };
        }
        status = status with { Output = settings.Muted ? "ミュート" : "未確認" };
        speechWorker = Task.Run(PlayLoopAsync);
    }

    public AppSettings Settings { get { lock (gate) return settings; } }
    public bool HasToken { get { lock (gate) return hasToken; } }
    public VoiceStatus Status { get { lock (gate) return status; } }
    public event Action<VoiceStatus>? StatusChanged;
    public event Action<string>? Activity;

    public async Task ConnectAsync(AppSettings selected, string? replacementToken)
    {
        var endpoint = selected.Validate();
        var scope = selected.Scope;
        var token = replacementToken ?? CredentialVault.Read(scope);
        if (string.IsNullOrWhiteSpace(token) || token.Length > 2048 || token.Any(c => c < '!' || c > '~'))
            throw new ArgumentException("端末用の Bearer トークンを入力してください。");
        if (replacementToken is not null)
            CredentialVault.Write(scope, token);

        await DisconnectAsync().ConfigureAwait(false);
        selected = selected with { FeedUrl = endpoint.AbsoluteUri };
        LocalFiles.Write("settings.json", selected);
        var cursorFile = $"feed-{AppSettings.Hash(scope + "\n" + token)}.json";
        var announcementFile = $"speech-{scope}.json";
        var speaker = NewAudio(selected.AivisPort, selected.StyleId, Prefix(selected.Environment) + SpeechPolicy.FixedWarning);
        lock (gate)
        {
            Dictionary<string, string>? readings = null;
            try { readings = LocalFiles.Read<Dictionary<string, string>>("readings.json", 64 * 1024); }
            catch (Exception ex) when (IsStorageError(ex)) { Log("地名の読み辞書を読み込めません。"); }
            settings = selected;
            policy = new SpeechSettings(selected.Nationwide, ReadRegions(selected.Nationwide ? [] : selected.Regions),
                selected.ForecastEnabled, selected.ForecastThreshold, selected.ForecastStep, readings);
            hasToken = true;
            markFile = announcementFile;
            state = null;
            marks = [];
            storageHealthy = true;
            warningPrepared = false;
            try { state = LocalFiles.Read<FeedState>(cursorFile, StateMaxBytes); }
            catch (Exception ex) when (IsStorageError(ex))
            {
                Log("保存済み状態を読み込めないため全同期します。");
            }
            try
            {
                marks = LocalFiles.Read<List<SpeechMark>>(markFile, 8 * 1024 * 1024) ?? [];
                if (marks.Count > MarkLimit || marks.Any(mark => mark is null || mark.Regions is null))
                    throw new InvalidDataException("報知履歴の形式が無効です。");
            }
            catch (Exception ex) when (IsStorageError(ex))
            {
                marks = [];
                storageHealthy = false;
                Log("報知履歴を読み込めません。重複を避けるため音声を保留します。");
            }
            audio = speaker;
            connection = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            var ct = connection.Token;
            SetStatus(status with { Connection = "接続中", Cursor = CursorText(state?.Cursor), Engine = "確認中",
                Output = !storageHealthy ? "報知履歴エラー" : selected.Muted ? "ミュート" : "未確認" });
            connectionTask = Task.Run(() => RunAsync(endpoint, token, selected.Environment, cursorFile, speaker, ct));
        }
    }

    public async Task DisconnectAsync()
    {
        CancellationTokenSource? oldConnection;
        AudioOutput? oldAudio;
        Task oldTask, oldPlay;
        lock (gate)
        {
            oldConnection = connection;
            oldTask = connectionTask;
            connection = null;
            oldAudio = audio;
            audio = null;
            InvalidateFeed();
            Cancel(manualTest);
            oldPlay = playCompletion;
            SetStatus(status with { Connection = "未接続", Upstream = "未確認", Capture = "未確認", Clock = "未確認" });
        }
        if (oldConnection is not null)
        {
            await oldConnection.CancelAsync().ConfigureAwait(false);
            try { await oldTask.ConfigureAwait(false); }
            finally { oldConnection.Dispose(); }
        }
        await oldPlay.ConfigureAwait(false);
        if (oldAudio is not null)
            await oldAudio.DisposeAsync().ConfigureAwait(false);
    }

    public void Mute(bool muted)
    {
        lock (gate)
        {
            settings = settings with { Muted = muted };
            StopAudioLocked();
            Cancel(manualTest);
            SetStatus(status with { Output = !storageHealthy ? "報知履歴エラー" : muted ? "ミュート" : "待機中" });
            try { LocalFiles.Write("settings.json", settings); }
            catch (Exception ex) when (IsStorageError(ex)) { Log("ミュート設定の保存に失敗しました。"); }
        }
    }

    public void StopAudio()
    {
        lock (gate)
        {
            StopAudioLocked();
            Cancel(manualTest);
            Log("待機中と再生中の音声を停止しました。");
        }
    }

    public async Task<IReadOnlyList<VoiceStyle>> ListStylesAsync(int port)
    {
        AppSettings.ValidateAudio(port);
        await using var engine = NewAudio(port, null);
        var voices = await engine.ListVoicesAsync(lifetime.Token).ConfigureAwait(false);
        return voices.Select(voice => new VoiceStyle(voice.StyleId, $"{voice.SpeakerName} / {voice.StyleName}")).ToArray();
    }

    public async Task TestAudioAsync(int port, int? styleId)
    {
        AppSettings.ValidateAudio(port);
        using var test = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        lock (gate)
        {
            if (manualTest is not null || !settings.Muted && connection is not null)
                throw new InvalidOperationException("音声テストの前にミュートまたは切断してください。");
            StopAudioLocked();
            manualTest = test;
        }
        try
        {
            await using var engine = NewAudio(port, styleId);
            await engine.TestAsync(test.Token).ConfigureAwait(false);
            lock (gate) SetStatus(status with { Engine = "接続済み", Output = settings.Muted ? "ミュート" : "テスト完了" });
        }
        catch (SpeechEngineException)
        {
            lock (gate) SetStatus(status with { Engine = "合成エラー" });
            throw;
        }
        catch (AudioPlaybackException)
        {
            lock (gate) SetStatus(status with { Output = "再生エラー" });
            throw;
        }
        finally
        {
            lock (gate) manualTest = null;
        }
    }

    private async Task RunAsync(Uri endpoint, string token, string environment, string cursorFile, AudioOutput speaker,
        CancellationToken ct)
    {
        using var preparing = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var preparation = PrepareAsync(speaker, preparing.Token);
        var useCursor = true;
        var attempt = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                FeedState? prior;
                lock (gate) prior = state;
                var exit = await feed.RunAsync(endpoint, token, environment, prior, useCursor,
                    () => { lock (gate) if (!ct.IsCancellationRequested) SetStatus(status with { Connection = "同期中" }); },
                    result => Receive(result, cursorFile, ct), ct).ConfigureAwait(false);
                lock (gate)
                {
                    if (synchronized)
                        attempt = 0;
                    InvalidateFeed();
                    if (ct.IsCancellationRequested)
                        return;
                    var label = exit switch
                    {
                        FeedExit.Authentication => "認証が拒否されました。トークンを確認してください。",
                        FeedExit.Incompatible => "配信側のスキーマ版に対応していません。",
                        FeedExit.Protocol => "配信の順序または形式が不正です。全同期します。",
                        FeedExit.Timeout => "同期またはヘルス通知がタイムアウトしました。",
                        _ => "接続が切れました。"
                    };
                    Log(label);
                    SetStatus(status with { Connection = label, Upstream = "未確認", Capture = "未確認", Clock = "未確認" });
                    if (exit is FeedExit.Authentication or FeedExit.Incompatible)
                        return;
                }
                useCursor = exit != FeedExit.Protocol;
                var delay = Math.Min(30, 0.5 * Math.Pow(2, Math.Min(attempt++, 6)) * (1 + Random.Shared.NextDouble() * 0.2));
                await Task.Delay(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            await preparing.CancelAsync().ConfigureAwait(false);
            await preparation.ConfigureAwait(false);
        }
    }

    private async Task PrepareAsync(AudioOutput speaker, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await speaker.PrepareAsync(ct).ConfigureAwait(false);
                    lock (gate)
                    {
                        if (!ct.IsCancellationRequested)
                        {
                            warningPrepared = true;
                            SetStatus(status with { Engine = "接続済み・警報音声準備済み" });
                        }
                    }
                    return;
                }
                catch (SpeechEngineException)
                {
                    lock (gate) if (!ct.IsCancellationRequested) SetStatus(status with { Engine = "合成エラー・警報音声を再準備します" });
                    await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private void Receive(FeedApplyResult result, string cursorFile, CancellationToken ct)
    {
        lock (gate)
        {
            if (ct.IsCancellationRequested || connection is null)
                return;
            if (result.Health is { } heartbeat)
            {
                health = heartbeat;
                healthTick = Stopwatch.GetTimestamp();
                if (!ClockOkay())
                    StopAudioLocked();
                if (heartbeat.Upstream != HealthStatus.Healthy || heartbeat.Capture != HealthStatus.Healthy)
                    StopAudioLocked();
                SetStatus(status with { Upstream = HealthLabel(heartbeat.Upstream), Capture = HealthLabel(heartbeat.Capture),
                    Clock = ClockOkay() ? "正常" : "配信時刻との差が大きいため音声保留" });
            }

            if (result.CommittedState is { } committed)
            {
                state = committed;
                if (result.LiveUpdate is { } update)
                    ApplyStops(update.Event);
                try { LocalFiles.Write(cursorFile, committed, StateMaxBytes); }
                catch (Exception ex) when (IsStorageError(ex))
                {
                    InvalidateFeed();
                    Log("再開位置を保存できません。全同期します。");
                    throw new FeedProtocolException("Cursor save failed.");
                }
                SetStatus(status with { Cursor = CursorText(committed.Cursor) });
            }

            if (result.CompletedSync)
            {
                synchronized = true;
                recoverCancellations = true;
                SetStatus(status with { Connection = "接続済み" });
                Log($"無音同期完了　{state?.Events.Count ?? 0} 件　{status.Cursor}");
            }
            if (synchronized && recoverCancellations && CanConsiderSpeech() && state is { } currentState)
            {
                recoverCancellations = false;
                foreach (var earthquake in currentState.Events.Values)
                {
                    Offer(SpeechPolicy.Cancellation(earthquake, SeriesKind.Warning, marks, FeedNow()));
                    Offer(SpeechPolicy.Cancellation(earthquake, SeriesKind.Forecast, marks, FeedNow()));
                }
            }
            if (result.LiveUpdate is { } live)
            {
                var series = live.Event.GetSeries(live.ChangedSeries)!;
                Log($"{Clip(live.Event.EventId)}　{series.Source.Code}　{SeriesLabel(series.Status)}　位置 {live.Seq}");
                if (settings.Muted && series.WarningData is { AddedRegions.Count: > 0 } warning)
                    Log("警報の追加地域　" + string.Join("、", warning.AddedRegions.Take(8).Select(region =>
                        $"{Clip(region.Name)} [{Clip(region.Key.CodeType)}:{Clip(region.Key.Code)}]")));
                if (CanConsiderSpeech())
                    Offer(SpeechPolicy.FromLive(live, policy, marks, FeedNow()));
                else
                    Log("同期・上流・取込・時刻を確認できるまで音声を保留します。");
            }
        }
    }

    private void ApplyStops(FeedEvent earthquake)
    {
        if (earthquake.Warning is not null || earthquake.Forecast?.Status != SeriesStatus.Active)
        {
            queue.RemoveAll(job => job.EventId == earthquake.EventId && job.Kind == SpeechKind.Forecast);
            if (!currentStarted && current is { Kind: SpeechKind.Forecast } job && job.EventId == earthquake.EventId)
                Cancel(playing);
        }
        if (earthquake.Warning?.Status == SeriesStatus.Canceled)
            InvalidateSeries(earthquake.EventId, SeriesKind.Warning);
        if (earthquake.Forecast?.Status == SeriesStatus.Canceled)
            InvalidateSeries(earthquake.EventId, SeriesKind.Forecast);
    }

    private void InvalidateSeries(string eventId, SeriesKind kind)
    {
        queue.RemoveAll(job => job.EventId == eventId && job.Series == kind
            && job.Kind is not (SpeechKind.ForecastCancel or SpeechKind.WarningCancel));
        if (current is { } playingJob && playingJob.EventId == eventId && playingJob.Series == kind
            && playingJob.Kind is not (SpeechKind.ForecastCancel or SpeechKind.WarningCancel))
            Cancel(playing);
    }

    private void Offer(SpeechJob? candidate)
    {
        if (candidate is not { } job)
            return;
        if (settings.Muted)
        {
            Log($"無音検証　{Clip(job.EventId)}　{JobLabel(job.Kind)} の候補");
            return;
        }
        if (!storageHealthy || manualTest is not null)
            return;
        if (queue.Any(item => item.EventId == job.EventId && item.Kind == job.Kind && item.Revision == job.Revision))
            return;

        var now = FeedNow();
        queue.RemoveAll(item => item.Until <= now || (item.EventId == job.EventId
            && item.Kind == SpeechKind.Forecast && job.Kind == SpeechKind.Forecast));
        if (queue.Count == QueueLimit)
        {
            var lowest = queue.Max(item => item.Kind);
            if (lowest <= job.Kind)
            {
                Log($"音声キュー満杯　{Clip(job.EventId)}　{JobLabel(job.Kind)} を保留");
                SetStatus(status with { Output = "音声キュー満杯" });
                return;
            }
            queue.RemoveAt(queue.FindLastIndex(item => item.Kind == lowest));
        }
        queue.Add(job);
        if (current is { } playingJob && (job.Kind < playingJob.Kind
            || (job.Kind == SpeechKind.Forecast && playingJob.Kind == SpeechKind.Forecast
                && job.EventId == playingJob.EventId && job.Revision > playingJob.Revision)))
            Cancel(playing);
        if (speechReady.CurrentCount == 0)
            speechReady.Release();
    }

    private async Task PlayLoopAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await speechReady.WaitAsync(lifetime.Token).ConfigureAwait(false);
                while (!lifetime.IsCancellationRequested)
                {
                    SpeechJob job;
                    AudioOutput speaker;
                    CancellationTokenSource playback;
                    TaskCompletionSource completion;
                    string prefix;
                    lock (gate)
                    {
                        if (audio is null || queue.MinBy(item => item.Kind) is not { } next)
                            break;
                        job = next;
                        queue.Remove(job);
                        if (!CanStart(job))
                            continue;
                        speaker = audio;
                        current = job;
                        currentStarted = false;
                        playing = playback = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        playCompletion = completion.Task;
                        prefix = Prefix(settings.Environment);
                    }
                    try { await PlayAsync(job, speaker, prefix, playback.Token).ConfigureAwait(false); }
                    finally
                    {
                        lock (gate)
                        {
                            current = null;
                            currentStarted = false;
                            playing = null;
                            completion.TrySetResult();
                        }
                        playback.Dispose();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async Task PlayAsync(SpeechJob job, AudioOutput speaker, string prefix, CancellationToken ct)
    {
        var started = false;
        ValueTask<bool> Begin(CancellationToken cancellation)
        {
            lock (gate)
            {
                if (cancellation.IsCancellationRequested || !CanStart(job))
                    return ValueTask.FromResult(false);
                var now = FeedNow();
                var next = marks.Where(mark => now - mark.StartedAt < TimeSpan.FromHours(24)).ToList();
                if (next.Count >= MarkLimit)
                {
                    storageHealthy = false;
                    SetStatus(status with { Output = "報知履歴の上限に到達" });
                    return ValueTask.FromResult(false);
                }
                next.Add(job.Mark(now));
                try { LocalFiles.Write(markFile, next); }
                catch (Exception ex) when (IsStorageError(ex))
                {
                    storageHealthy = false;
                    Log("報知履歴の保存に失敗しました。音声を保留します。");
                    SetStatus(status with { Output = "報知履歴エラー" });
                    return ValueTask.FromResult(false);
                }
                marks = next;
                currentStarted = started = true;
                SetStatus(status with { Output = "再生中" });
                Log($"音声開始　{Clip(job.EventId)}　{JobLabel(job.Kind)}　発表から {(now - job.IssuedAt).TotalMilliseconds:F0} ms");
                return ValueTask.FromResult(FeedNow() < job.Until);
            }
        }

        ValueTask<bool> Continue(CancellationToken cancellation)
        {
            lock (gate)
                return ValueTask.FromResult(!cancellation.IsCancellationRequested
                    && CanConsiderSpeech() && !settings.Muted && FeedNow() < job.Until
                    && state?.Events.TryGetValue(job.EventId, out var earthquake) == true
                    && earthquake.Warning is { Status: SeriesStatus.Active, WarningData: { } warning }
                    && job.Regions.All(key => warning.ActiveRegions.Any(region =>
                        region.Key == key && region.SourceRef == job.TelegramId)));
        }

        async Task<bool> SpeakBeforeDeadline(string text, Func<CancellationToken, ValueTask<bool>> start,
            TimeSpan? synthesisBudget = null)
        {
            TimeSpan remaining;
            lock (gate) remaining = job.Until - FeedNow();
            if (remaining <= TimeSpan.Zero)
                return false;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(synthesisBudget is { } budget && budget < remaining ? budget : remaining);
            try
            {
                return await speaker.SpeakAsync(text, token =>
                {
                    deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                    return start(token);
                }, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        }

        try
        {
            if (job.Kind == SpeechKind.FirstWarning)
            {
                var prepared = await speaker.PlayPreparedAsync(prefix + SpeechPolicy.FixedWarning, Begin, ct).ConfigureAwait(false);
                if (prepared)
                    await SpeakBeforeDeadline(job.Text, Continue, TimeSpan.FromMilliseconds(750)).ConfigureAwait(false);
                else if (!started && prefix.Length == 0)
                    await speaker.PlaySignalAsync(AudioSignal.Alert, Begin, ct).ConfigureAwait(false);
                else if (!started)
                    await SpeakBeforeDeadline(prefix + SpeechPolicy.FixedWarning + job.Text, Begin).ConfigureAwait(false);
            }
            else
            {
                var canUseTone = job.IsWarning && prefix.Length == 0;
                bool prepared;
                lock (gate) prepared = warningPrepared;
                try
                {
                    if (!canUseTone || prepared)
                        await SpeakBeforeDeadline(prefix + job.Text, Begin,
                            canUseTone ? TimeSpan.FromSeconds(2) : null).ConfigureAwait(false);
                }
                catch (SpeechEngineException) when (canUseTone && !started)
                {
                    lock (gate) SetStatus(status with { Engine = "合成エラー・内蔵音に切替" });
                }
                if (canUseTone && !started)
                    await speaker.PlaySignalAsync(AudioSignal.Alert, Begin, ct).ConfigureAwait(false);
            }
            if (started)
                lock (gate) if (!settings.Muted) SetStatus(status with { Output = "待機中" });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            lock (gate) if (!settings.Muted && status.Output == "再生中") SetStatus(status with { Output = "待機中" });
        }
        catch (SpeechEngineException)
        {
            lock (gate)
            {
                SetStatus(status with { Engine = "合成エラー", Output = started ? "待機中" : status.Output });
                Log($"AivisSpeech の合成に失敗　{Clip(job.EventId)}");
            }
        }
        catch (AudioPlaybackException)
        {
            lock (gate)
            {
                SetStatus(status with { Output = "Windows 音声出力エラー" });
                Log($"Windows 音声出力に失敗　{Clip(job.EventId)}");
            }
        }
    }

    private bool CanStart(SpeechJob job) => !settings.Muted && storageHealthy && manualTest is null && CanConsiderSpeech()
        && state?.Events.TryGetValue(job.EventId, out var earthquake) == true
        && SpeechPolicy.CanStart(job, earthquake, marks, FeedNow());

    private bool CanConsiderSpeech()
        => synchronized && health is { Upstream: HealthStatus.Healthy, Capture: HealthStatus.Healthy } && ClockOkay();

    private bool ClockOkay() => health is { } heartbeat
        && (DateTimeOffset.UtcNow - heartbeat.ServerTime - Stopwatch.GetElapsedTime(healthTick)).Duration() <= TimeSpan.FromSeconds(2)
        && Stopwatch.GetElapsedTime(healthTick) < FeedProtocol.HealthTimeout;

    private DateTimeOffset FeedNow()
    {
        var local = DateTimeOffset.UtcNow;
        if (health is not { } heartbeat)
            return local;
        var server = heartbeat.ServerTime + Stopwatch.GetElapsedTime(healthTick);
        return local > server ? local : server;
    }

    private void InvalidateFeed()
    {
        synchronized = false;
        recoverCancellations = false;
        health = null;
        StopAudioLocked();
    }

    private void StopAudioLocked()
    {
        queue.Clear();
        Cancel(playing);
    }

    private static void Cancel(CancellationTokenSource? source)
    {
        if (source is { IsCancellationRequested: false })
            source.Cancel();
    }

    private void SetStatus(VoiceStatus value)
    {
        if (value == status)
            return;
        status = value;
        StatusChanged?.Invoke(value);
    }

    private void Log(string message) => Activity?.Invoke($"{DateTimeOffset.Now:HH:mm:ss}　{message}");

    private static bool IsStorageError(Exception error) =>
        error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException;

    private static string HealthLabel(HealthStatus status) => status == HealthStatus.Healthy ? "正常" : "異常";
    private static string CursorText(FeedCursor? cursor) =>
        cursor is null ? "—" : $"{Clip(cursor.StreamId)} / {Clip(cursor.Epoch)} / {cursor.Seq}";
    private static string Clip(string value) => value.Length <= 80 ? value : value[..80] + "…";
    private static string SeriesLabel(SeriesStatus status) => status switch
    {
        SeriesStatus.Canceled => "取消",
        SeriesStatus.Final => "最終",
        _ => "更新"
    };
    private static string JobLabel(SpeechKind kind) => kind switch
    {
        SpeechKind.FirstWarning => "警報",
        SpeechKind.WarningCancel => "警報取消",
        SpeechKind.WarningUpdate => "警報地域追加",
        SpeechKind.ForecastCancel => "予報取消",
        _ => "予報"
    };
    private static string Prefix(string environment) => environment switch
    {
        "training" => "訓練。これは訓練です。",
        "test" => "試験。これは試験です。",
        _ => ""
    };
    private static HashSet<RegionKey> ReadRegions(string[] regions)
    {
        var keys = new HashSet<RegionKey>();
        foreach (var region in regions)
            if (region.Split(':') is [var type, var code])
                keys.Add(new RegionKey(type, code));
        return keys;
    }

    private static AudioOutput NewAudio(int port, int? styleId, string? fixedWarning = null) =>
        new(new AudioOptions { EngineUrl = $"http://127.0.0.1:{port}", StyleId = styleId,
            WarmupTexts = fixedWarning is null ? [] : [fixedWarning] });

    public void Dispose()
    {
        lifetime.Cancel();
        DisconnectAsync().GetAwaiter().GetResult();
        speechWorker.GetAwaiter().GetResult();
        speechReady.Dispose();
        lifetime.Dispose();
    }
}
