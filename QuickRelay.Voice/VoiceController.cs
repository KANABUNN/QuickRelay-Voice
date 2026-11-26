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
    private SpeechSettings policy = new(true, new HashSet<RegionKey>(), false, "4");
    private VoiceStatus status;
    private bool hasToken, synchronized, currentStarted, preempted, warningPrepared, storageHealthy = true;
    private HealthMessage? health;
    private long healthTick;
    private FeedState? state;
    private List<SpeechMark> marks = [];
    private string markFile = "";
    private AudioOutput? audio;
    private SpeechJob? current;
    private CancellationTokenSource? playing, manualTest, connection, preparing;
    private Task playCompletion = Task.CompletedTask;
    private Task connectionTask = Task.CompletedTask;
    private Task preparation = Task.CompletedTask;

    public VoiceController()
    {
        status = new VoiceStatus();
        try
        {
            settings = (LocalFiles.Read<AppSettings>("settings.json", 64 * 1024) ?? new()).Normalize();
            if (settings.Environment != AppSettings.FeedEnvironment)
                settings = settings with { Environment = AppSettings.FeedEnvironment, Muted = true, ForecastEnabled = false };
            if (!string.IsNullOrWhiteSpace(settings.FeedUrl))
                settings.Validate();
            else
                settings = new AppSettings { Muted = settings.Muted }.WithVoiceFrom(settings).WithResidencyFrom(settings);
            var savedToken = CredentialVault.Read(settings.Scope);
            hasToken = savedToken is not null && !IsRevoked(TokenScope(settings.Scope, savedToken));
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
        var tokenScope = TokenScope(scope, token);
        if (IsRevoked(tokenScope))
            throw new ArgumentException("トークンは失効しています。新しいトークンを入力してください。");
        if (replacementToken is not null)
            CredentialVault.Write(scope, token);

        await DisconnectAsync().ConfigureAwait(false);
        selected = selected with { FeedUrl = endpoint.AbsoluteUri };
        LocalFiles.Write("settings.json", selected);
        var cursorFile = $"feed-prc-{tokenScope}.json";
        var announcementFile = $"speech-{scope}.json";
        var speaker = NewAudio(selected, SpeechPolicy.FixedWarning);
        lock (gate)
        {
            Dictionary<string, string>? readings = null;
            try { readings = LocalFiles.Read<Dictionary<string, string>>("readings.json", 64 * 1024); }
            catch (Exception ex) when (IsStorageError(ex)) { Log("地名の読み辞書を読み込めません。"); }
            settings = selected;
            policy = new SpeechSettings(selected.Nationwide, ReadRegions(selected.Nationwide ? [] : selected.Regions),
                selected.ForecastEnabled, selected.ForecastThreshold, PlaceReadings.Default.With(readings));
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
                if (marks.Count > MarkLimit || marks.Any(mark => mark is null || mark.Regions is null
                    || mark.Facts is { } facts && (facts.Regions is null || facts.MaxIntensity is null)))
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
            StartPreparationLocked(speaker, ct);
            connectionTask = Task.Run(() => RunAsync(endpoint, token, tokenScope, cursorFile, ct));
        }
    }

    public async Task DisconnectAsync()
    {
        CancellationTokenSource? oldConnection, oldPreparing;
        AudioOutput? oldAudio;
        Task oldTask, oldPreparation, oldPlay;
        lock (gate)
        {
            oldConnection = connection;
            oldTask = connectionTask;
            connection = null;
            oldPreparing = preparing;
            oldPreparation = preparation;
            preparing = null;
            oldAudio = audio;
            audio = null;
            InvalidateFeed();
            Cancel(manualTest);
            Cancel(oldPreparing);
            oldPlay = playCompletion;
            SetStatus(status with { Connection = "未接続", Upstream = "未確認", Capture = "未確認", Clock = "未確認" });
        }
        if (oldConnection is not null)
        {
            await oldConnection.CancelAsync().ConfigureAwait(false);
            try { await oldTask.ConfigureAwait(false); }
            finally { oldConnection.Dispose(); }
        }
        await oldPreparation.ConfigureAwait(false);
        oldPreparing?.Dispose();
        await oldPlay.ConfigureAwait(false);
        if (oldAudio is not null)
            await oldAudio.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Saves the AivisSpeech choices in <paramref name="source"/>. While connected, new announcements switch to the
    /// new voice without reconnecting the feed; an announcement already playing finishes with the previous voice.
    /// </summary>
    public async Task<bool> SaveVoiceAsync(AppSettings source)
    {
        source.ValidateVoice();
        AudioOutput? oldAudio = null;
        CancellationTokenSource? oldPreparing = null;
        Task oldPreparation = Task.CompletedTask, oldPlay = Task.CompletedTask;
        lock (gate)
        {
            var next = settings.WithVoiceFrom(source);
            try { LocalFiles.Write("settings.json", next); }
            catch (Exception ex) when (IsStorageError(ex))
            {
                throw new InvalidOperationException("AivisSpeech の設定を保存できませんでした。", ex);
            }
            settings = next;
            if (connection is null || audio is null)
                return false;

            var replacement = NewAudio(next, SpeechPolicy.FixedWarning);
            oldAudio = audio;
            audio = replacement;
            oldPreparing = preparing;
            oldPreparation = preparation;
            Cancel(oldPreparing);
            oldPlay = playCompletion;
            StartPreparationLocked(replacement, connection.Token);
            SetStatus(status with { Engine = "確認中" });
            Log("AivisSpeech の設定を変更しました。警報音声を準備し直します。");
        }
        await oldPreparation.ConfigureAwait(false);
        oldPreparing?.Dispose();
        await oldPlay.ConfigureAwait(false);
        await oldAudio.DisposeAsync().ConfigureAwait(false);
        return true;
    }

    public void SaveResidency(AppSettings source)
    {
        lock (gate)
        {
            settings = settings.WithResidencyFrom(source);
            try { LocalFiles.Write("settings.json", settings); }
            catch (Exception ex) when (IsStorageError(ex)) { Log("常駐設定の保存に失敗しました。"); }
        }
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
        await using var engine = NewAudio(new AppSettings { AivisPort = port });
        var voices = await engine.ListVoicesAsync(lifetime.Token).ConfigureAwait(false);
        return voices.Select(voice => new VoiceStyle(voice.StyleId, voice.SpeakerUuid, voice.SpeakerName, voice.StyleName))
            .ToArray();
    }

    /// <summary>Plays a sample with the AivisSpeech choices in <paramref name="voice"/>, saved or not.</summary>
    public async Task TestAudioAsync(AppSettings voice)
    {
        voice.ValidateVoice();
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
            await using var engine = NewAudio(voice);
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

    private async Task RunAsync(Uri endpoint, string token, string tokenScope, string cursorFile, CancellationToken ct)
    {
        var attempt = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                FeedState? prior;
                lock (gate) prior = state;
                var exit = await feed.RunAsync(endpoint, token, AppSettings.FeedEnvironment, prior, useCursor: true,
                    () => { lock (gate) if (!ct.IsCancellationRequested) SetStatus(status with { Connection = "同期中" }); },
                    result => Receive(result, cursorFile, ct), ct).ConfigureAwait(false);
                lock (gate)
                {
                    if (synchronized)
                        attempt = 0;
                    InvalidateFeed();
                    if (ct.IsCancellationRequested)
                        return;
                    if (exit is FeedExit.Protocol or FeedExit.ResyncRequired)
                    {
                        state = null;
                        try { LocalFiles.Write<FeedState?>(cursorFile, null); }
                        catch (Exception ex) when (IsStorageError(ex)) { Log("古い再開位置をディスクから解除できませんでした。"); }
                    }
                    if (exit == FeedExit.TokenRevoked)
                    {
                        hasToken = false;
                        try { LocalFiles.Write(RevocationFile(tokenScope), true); }
                        catch (Exception ex) when (IsStorageError(ex)) { Log("トークンの失効状態を保存できませんでした。"); }
                    }
                    var label = exit switch
                    {
                        FeedExit.NormalClosure => "配信が正常に終了しました。",
                        FeedExit.ServiceRestart => "配信サービスが停止しました。再接続します。",
                        FeedExit.Authentication => "認証が拒否されました。トークンを確認してください。",
                        FeedExit.Incompatible => "購読要求またはスキーマ版が拒否されました。",
                        FeedExit.ForbiddenEnvironment => "トークンが本番環境の購読を許可されていません。",
                        FeedExit.Protocol => "配信の順序または形式が不正です。全同期します。",
                        FeedExit.Timeout => "同期またはヘルス通知がタイムアウトしました。",
                        FeedExit.ResyncRequired => "配信の世代または保持範囲が変わりました。全同期します。",
                        FeedExit.TokenRevoked => "トークンが失効しました。新しいトークンを入力してください。",
                        FeedExit.LimitExceeded => "配信がフレームまたは同期の上限を超えました。",
                        FeedExit.SlowClient => "配信の送信がタイムアウトしました。再接続します。",
                        FeedExit.RateLimited => "配信側の接続制限に達しました。1分待って再接続します。",
                        _ => "接続が切れました。"
                    };
                    Log(label);
                    SetStatus(status with { Connection = label, Upstream = "未確認", Capture = "未確認",
                        Clock = "未確認", Cursor = CursorText(state?.Cursor) });
                    if (exit is FeedExit.NormalClosure or FeedExit.Authentication or FeedExit.Incompatible
                        or FeedExit.ForbiddenEnvironment or FeedExit.TokenRevoked or FeedExit.LimitExceeded)
                        return;
                }
                var delay = exit == FeedExit.RateLimited ? 60
                    : Math.Min(30, 0.5 * Math.Pow(2, Math.Min(attempt++, 6)) * (1 + Random.Shared.NextDouble() * 0.2));
                await Task.Delay(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            // A feed that stopped for good (for example a revoked token) no longer needs the warning audio.
            lock (gate)
                if (!ct.IsCancellationRequested)
                    Cancel(preparing);
        }
    }

    private void StartPreparationLocked(AudioOutput speaker, CancellationToken connectionToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(connectionToken);
        var ct = source.Token;
        preparing = source;
        warningPrepared = false;
        preparation = Task.Run(() => PrepareAsync(speaker, ct));
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
                SetStatus(status with { Connection = "接続済み" });
                Log($"無音同期完了　{state?.Events.Count ?? 0} 件　{status.Cursor}");
            }
            if (result.LiveUpdate is { } live)
            {
                var series = live.Event.GetSeries(live.ChangedSeries)!;
                var serial = series.Source.Serial is { } number ? $"第{number}報　" : "";
                Log($"{Clip(live.Event.EventId)}　{series.Source.Code}　{serial}{SeriesLabel(series.Status)}　位置 {live.Seq}");
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

    /// <summary>A withdrawn earthquake stops everything but its cancellation notice.</summary>
    private void ApplyStops(FeedEvent earthquake)
    {
        if (earthquake.Warning?.Status != SeriesStatus.Canceled && earthquake.Forecast?.Status != SeriesStatus.Canceled)
            return;
        queue.RemoveAll(job => job.EventId == earthquake.EventId && job.Category != SpeechCategory.Cancel);
        if (current is { } playingJob && playingJob.EventId == earthquake.EventId && playingJob.Category != SpeechCategory.Cancel)
            Cancel(playing);
    }

    private void Offer(SpeechJob? candidate)
    {
        if (candidate is not { } job)
            return;
        if (settings.Muted)
        {
            if (Compose(job, out _) is { } preview)
                Log($"無音検証　{Clip(job.EventId)}　{Words(preview)}");
            return;
        }
        if (!storageHealthy || manualTest is not null)
            return;

        var now = FeedNow();
        queue.RemoveAll(item => item.Until <= now);
        // Reports for one earthquake merge into one pending announcement; its words are chosen when it plays.
        var index = queue.FindIndex(item => item.EventId == job.EventId && item.Category == job.Category);
        if (index >= 0)
            queue[index] = queue[index].Merge(job);
        else
        {
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
        }
        if (current is { } playingJob && job.Kind < playingJob.Kind)
        {
            preempted = true;
            Cancel(playing);
        }
        if (speechReady.CurrentCount == 0)
            speechReady.Release();
    }

    private void Requeue(SpeechJob job)
    {
        if (job.Until <= FeedNow())
            return;
        var index = queue.FindIndex(item => item.EventId == job.EventId && item.Category == job.Category);
        if (index >= 0)
            queue[index] = queue[index].Merge(job);
        else if (queue.Count < QueueLimit)
            queue.Add(job);
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
                    lock (gate)
                    {
                        if (audio is null || queue.MinBy(item => item.Kind) is not { } next)
                            break;
                        job = next;
                        queue.Remove(job);
                        if (!CanDequeue(job))
                            continue;
                        speaker = audio;
                        current = job;
                        currentStarted = preempted = false;
                        playing = playback = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        playCompletion = completion.Task;
                    }
                    try { await PlayAsync(job, speaker, playback.Token).ConfigureAwait(false); }
                    finally
                    {
                        lock (gate)
                        {
                            current = null;
                            currentStarted = preempted = false;
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

    private async Task PlayAsync(SpeechJob job, AudioOutput speaker, CancellationToken ct)
    {
        var started = false;
        IReadOnlyList<SpeechMark> recorded = [];
        Announcement? chosen;
        string reason;
        lock (gate) chosen = Compose(job, out reason);
        if (chosen is not { } announcement)
        {
            lock (gate) Log($"{Clip(job.EventId)}　{JobLabel(job.Kind)}　読み上げなし（{reason}）");
            return;
        }

        ValueTask<bool> Begin(CancellationToken cancellation)
        {
            lock (gate)
            {
                if (cancellation.IsCancellationRequested || !CanSpeak(announcement))
                    return ValueTask.FromResult(false);
                var now = FeedNow();
                var stamped = announcement.StartAt(now);
                var next = marks.Where(mark => now - mark.StartedAt < TimeSpan.FromHours(24)).ToList();
                if (next.Count + stamped.Count > MarkLimit)
                {
                    storageHealthy = false;
                    SetStatus(status with { Output = "報知履歴の上限に到達" });
                    return ValueTask.FromResult(false);
                }
                next.AddRange(stamped);
                try { LocalFiles.Write(markFile, next); }
                catch (Exception ex) when (IsStorageError(ex))
                {
                    storageHealthy = false;
                    Log("報知履歴の保存に失敗しました。音声を保留します。");
                    SetStatus(status with { Output = "報知履歴エラー" });
                    return ValueTask.FromResult(false);
                }
                marks = next;
                recorded = stamped;
                currentStarted = started = true;
                SetStatus(status with { Output = "再生中" });
                Log($"音声開始　{Clip(job.EventId)}　{JobLabel(announcement.Kind)}　発表から {(now - announcement.IssuedAt).TotalMilliseconds:F0} ms");
                Log("読み上げ　" + Words(announcement));
                return ValueTask.FromResult(FeedNow() < announcement.Until);
            }
        }

        ValueTask<bool> Continue(CancellationToken cancellation)
        {
            lock (gate)
                return ValueTask.FromResult(!cancellation.IsCancellationRequested
                    && CanConsiderSpeech() && !settings.Muted && FeedNow() < announcement.Until
                    && state?.Events.TryGetValue(job.EventId, out var earthquake) == true
                    && SpeechPolicy.StillCurrent(announcement, earthquake));
        }

        async Task<bool> SpeakBeforeDeadline(string text, Func<CancellationToken, ValueTask<bool>> start,
            TimeSpan? synthesisBudget = null)
        {
            TimeSpan remaining;
            lock (gate) remaining = announcement.Until - FeedNow();
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
            if (announcement.Kind == SpeechKind.FirstWarning)
            {
                var prepared = await speaker.PlayPreparedAsync(SpeechPolicy.FixedWarning, Begin, ct).ConfigureAwait(false);
                if (prepared)
                    await SpeakBeforeDeadline(announcement.Text, Continue, TimeSpan.FromMilliseconds(750)).ConfigureAwait(false);
                else if (!started)
                    await speaker.PlaySignalAsync(AudioSignal.Alert, Begin, ct).ConfigureAwait(false);
            }
            else
            {
                var canUseTone = announcement.Kind == SpeechKind.WarningUpdate;
                bool prepared;
                lock (gate) prepared = warningPrepared;
                try
                {
                    if (!canUseTone || prepared)
                        await SpeakBeforeDeadline(announcement.Text, Begin,
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
            lock (gate)
            {
                if (!settings.Muted && status.Output == "再生中")
                    SetStatus(status with { Output = "待機中" });
                // Speech cut off by a more urgent one is told again afterwards, against what was heard before it.
                if (preempted && job.Category == SpeechCategory.Update)
                {
                    if (started)
                        Forget(recorded);
                    Requeue(job);
                }
            }
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

    private Announcement? Compose(SpeechJob job, out string reason)
    {
        if (state?.Events.TryGetValue(job.EventId, out var earthquake) != true)
        {
            reason = "地震の状態がありません";
            return null;
        }
        return SpeechPolicy.Compose(job, earthquake!, policy, marks, FeedNow(), out reason);
    }

    private void Forget(IReadOnlyList<SpeechMark> interrupted)
    {
        if (interrupted.Count == 0)
            return;
        var next = marks.Where(mark => !interrupted.Any(item => ReferenceEquals(item, mark))).ToList();
        try
        {
            LocalFiles.Write(markFile, next);
            marks = next;
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            storageHealthy = false;
            Log("報知履歴の保存に失敗しました。音声を保留します。");
            SetStatus(status with { Output = "報知履歴エラー" });
        }
    }

    private bool CanDequeue(SpeechJob job) => !settings.Muted && storageHealthy && manualTest is null && CanConsiderSpeech()
        && state?.Events.ContainsKey(job.EventId) == true && FeedNow() < job.Until;

    private bool CanSpeak(Announcement announcement) => !settings.Muted && storageHealthy && manualTest is null
        && CanConsiderSpeech() && state?.Events.TryGetValue(announcement.EventId, out var earthquake) == true
        && SpeechPolicy.CanStart(announcement, earthquake, marks, FeedNow());

    private static string Words(Announcement announcement)
        => (announcement.Kind == SpeechKind.FirstWarning ? SpeechPolicy.FixedWarning : "") + announcement.Text;

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
        health = null;
        StopAudioLocked();
    }

    private void StopAudioLocked()
    {
        queue.Clear();
        preempted = false;
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
        SpeechKind.WarningUpdate => "警報続報",
        SpeechKind.ForecastCancel => "予報取消",
        _ => "予報"
    };
    private static string TokenScope(string scope, string token) => AppSettings.Hash(scope + "\n" + token);
    private static string RevocationFile(string tokenScope) => $"revoked-{tokenScope}.json";
    private static bool IsRevoked(string tokenScope) => LocalFiles.Read<bool>(RevocationFile(tokenScope), 1024);
    private static HashSet<RegionKey> ReadRegions(string[] regions)
    {
        var keys = new HashSet<RegionKey>();
        foreach (var region in regions)
            if (region.Split(':') is [var type, var code])
                keys.Add(new RegionKey(type, code));
        return keys;
    }

    private static AudioOutput NewAudio(AppSettings voice, string? fixedWarning = null) =>
        new(new AudioOptions
        {
            EngineUrl = $"http://127.0.0.1:{voice.AivisPort}", StyleId = voice.StyleId,
            SpeakerUuid = string.IsNullOrWhiteSpace(voice.SpeakerUuid) ? null : voice.SpeakerUuid,
            StyleName = string.IsNullOrWhiteSpace(voice.StyleName) ? new AudioOptions().StyleName : voice.StyleName,
            Tuning = voice.Tuning, WarmupTexts = fixedWarning is null ? [] : [fixedWarning]
        });

    public void Dispose()
    {
        lifetime.Cancel();
        DisconnectAsync().GetAwaiter().GetResult();
        speechWorker.GetAwaiter().GetResult();
        speechReady.Dispose();
        lifetime.Dispose();
    }
}
