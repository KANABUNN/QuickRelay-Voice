using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using QuickRelay.Voice;
using QuickRelay.Voice.Audio;

internal static class VoiceChecks
{
    private const string Kohaku = "5680ac39-43c9-487a-bc3e-018c0d29cc38";
    private const string Mao = "e756b8e4-b606-4e15-99b1-3f9c6a1b2317";

    public static async Task RunAsync()
    {
        CheckSettings();
        await CheckQueryParametersAsync();
        await CheckVoiceSelectionAsync();
        await CheckSynthesisAheadAsync();
        CheckStartup();
        Console.WriteLine("PASS voice settings: saved settings from 0.2.0, tuning ranges, model fallback, AudioQuery parameters, synthesis ahead of playback, startup entry");
    }

    private static async Task CheckSynthesisAheadAsync()
    {
        // A new warning's region list is made while the fixed warning plays, so it must not wait for anything else.
        var slow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new FakeEngine { HoldSynthesis = slow };
        using var http = new HttpClient(engine);
        await using var output = new AudioOutput(new AudioOptions { StyleId = 888753760 }, http);
        var ahead = output.SynthesizeAheadAsync("対象地域は、石川県能登です。", CancellationToken.None);
        Check(await engine.SynthesisStarted.WaitAsync(TimeSpan.FromSeconds(5)) && !ahead.IsCompleted,
            "synthesis ahead reaches the engine without being awaited");
        slow.SetResult();
        Check((await ahead.WaitAsync(TimeSpan.FromSeconds(5))).Samples.Length == 4, "synthesis ahead returns the speech");

        // When the list is still not ready at the deadline after the warning, it is left out instead of starting late.
        engine.HoldSynthesis = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var unused = new CancellationTokenSource();
        var late = output.SynthesizeAheadAsync("対象地域は、富山県東部です。", unused.Token);
        Check(await engine.SynthesisStarted.WaitAsync(TimeSpan.FromSeconds(5)), "the second synthesis reaches the engine");
        using (var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
        {
            var asked = false;
            Check(await ThrowsAsync<OperationCanceledException>(() => output.SpeakAsync(late,
                _ => { asked = true; return ValueTask.FromResult(true); }, deadline.Token)) && !asked,
                "speech not ready by the deadline does not start");
        }

        // Cancelling speech that will not be played frees the engine for the next announcement.
        await unused.CancelAsync();
        Check(await ThrowsAsync<OperationCanceledException>(() => late), "unused synthesis ahead stops when cancelled");
        Check((await output.ListVoicesAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5))).Count == 4,
            "the engine is free after unused synthesis ahead is cancelled");
    }

    private static void CheckStartup()
    {
        // A scratch key stands in for the user's Run and StartupApproved keys, so the real startup list is untouched.
        const string root = @"Software\QuickRelay.Voice.Checks";
        const string installed = @"C:\Program Files\QuickRelay Voice\QuickRelay Voice.exe";
        Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
        try
        {
            var here = new StartupRegistration(root + @"\Run", root + @"\Approved", installed);
            var moved = new StartupRegistration(root + @"\Run", root + @"\Approved", @"D:\Tools\QRV\QuickRelay Voice.exe");
            var unknown = new StartupRegistration(root + @"\Run", root + @"\Approved", null);
            Check(here.State == StartupState.Off && here.RegisteredCommand is null, "startup starts unregistered");

            here.Enable();
            Check(here.State == StartupState.On && here.RegisteredCommand == $"\"{installed}\"", "startup registers the quoted path");
            Check(moved.State == StartupState.Elsewhere && unknown.State == StartupState.Elsewhere,
                "another copy sees the entry as registered elsewhere");
            Check(!here.DisabledInTaskManager, "a new entry is not disabled");
            using (var approved = Registry.CurrentUser.CreateSubKey(root + @"\Approved"))
                approved.SetValue(StartupRegistration.ValueName, new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
                    RegistryValueKind.Binary);
            Check(here.DisabledInTaskManager, "Task Manager's disabled flag is read");

            moved.Enable();
            Check(moved.State == StartupState.On && here.State == StartupState.Elsewhere, "registering again replaces the path");
            here.Disable();
            Check(here.State == StartupState.Off && moved.State == StartupState.Off, "removal clears the entry");
            here.Disable();
            Check(Throws<InvalidOperationException>(unknown.Enable) && unknown.State == StartupState.Off,
                "a copy without a known executable cannot register");
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
        }
    }

    private static void CheckSettings()
    {
        // settings.json as written by 0.2.0, before the model, tuning and tray settings existed.
        var legacy = JsonSerializer.Deserialize<AppSettings>("""
            {"FeedUrl":"wss://example.test/v1/eew","Environment":"production","Muted":true,"Nationwide":true,
             "Regions":[],"ForecastEnabled":false,"ForecastThreshold":"4","ForecastStep":1,"AivisPort":10101,
             "StyleId":888753760}
            """)!.Normalize();
        Check(legacy.StyleId == 888753760 && legacy.SpeakerUuid is null, "0.2.0 style ID is kept");
        Check(legacy.Tuning == new VoiceTuning() && legacy.Tuning.Speed == 1.08, "0.2.0 settings get the previous speed");
        Check(legacy is { MinimizeToTray: true, StartInTray: false, AutoConnect: false }, "0.2.0 settings get tray defaults");
        legacy.Validate();

        var missing = JsonSerializer.Deserialize<AppSettings>("""{"Tuning":null}""")!;
        Check(missing.Normalize().Tuning == new VoiceTuning(), "null tuning is repaired");
        var outOfRange = JsonSerializer.Deserialize<AppSettings>("""{"Tuning":{"Pitch":0.5}}""")!;
        Check(Throws<ArgumentException>(outOfRange.ValidateVoice), "pitch outside the engine range is rejected");
        Check(outOfRange.Normalize().Tuning == new VoiceTuning(), "out-of-range tuning is reset on load");
        Check(VoiceTuning.Parameters.All(parameter =>
            !parameter.Accepts(parameter.Min - 0.01) && !parameter.Accepts(parameter.Max + 0.01)
            && parameter.Accepts(parameter.Read(new VoiceTuning()))), "defaults sit inside every range");

        var tuned = new AppSettings
        {
            FeedUrl = "wss://example.test/v1/eew", SpeakerUuid = Mao, SpeakerName = "まお", StyleId = 888753761,
            StyleName = "ふつう", Tuning = new VoiceTuning { Speed = 1.25, Pitch = -0.05, PrePhoneme = 0 },
            StartInTray = true, AutoConnect = true
        };
        var json = JsonSerializer.Serialize(tuned);
        Check(!json.Contains("IsValid", StringComparison.Ordinal) && !json.Contains("Parameters", StringComparison.Ordinal),
            "only settings are written to disk");
        var restored = JsonSerializer.Deserialize<AppSettings>(json)!;
        Check(restored.Tuning == tuned.Tuning && restored.SpeakerUuid == Mao && restored.StyleName == "ふつう"
            && restored is { StartInTray: true, AutoConnect: true }, "voice and tray settings survive a round trip");

        var voiceOnly = new AppSettings().WithVoiceFrom(tuned);
        Check(voiceOnly.FeedUrl == "" && voiceOnly.Tuning == tuned.Tuning && voiceOnly.StyleId == tuned.StyleId
            && voiceOnly is { StartInTray: false, AutoConnect: false }, "saving voice settings leaves the rest alone");
        var trayOnly = new AppSettings().WithResidencyFrom(tuned);
        Check(trayOnly.Tuning == new VoiceTuning() && trayOnly is { StartInTray: true, AutoConnect: true },
            "saving tray settings leaves the rest alone");
    }

    private static async Task CheckQueryParametersAsync()
    {
        var engine = new FakeEngine();
        var tuning = new VoiceTuning
        {
            Speed = 1.3, Intonation = 1.4, Tempo = 0.8, Pitch = 0.05, Volume = 0.7,
            PauseScale = 1.5, PrePhoneme = 0.02, PostPhoneme = 0.3
        };
        using var http = new HttpClient(engine);
        using var client = new AivisClient(new AudioOptions { StyleId = 888753760, Tuning = tuning }, http);
        await client.SynthesizeAsync("テスト", 888753760, CancellationToken.None);
        var body = engine.SynthesisBody ?? throw new InvalidOperationException("QRV check failed: synthesis was not called");
        foreach (var parameter in VoiceTuning.Parameters)
        {
            Check(body[parameter.QueryKey]?.GetValue<double>() == parameter.Read(tuning), $"{parameter.QueryKey} is sent");
        }
        Check(body["kana"]?.GetValue<string>() == "テスト" && body.ContainsKey("accent_phrases"),
            "engine fields outside the tuning are passed through");

        var older = new FakeEngine { OmitAivisFields = true };
        using var olderHttp = new HttpClient(older);
        using var olderClient = new AivisClient(new AudioOptions { Tuning = tuning }, olderHttp);
        await olderClient.SynthesizeAsync("テスト", 1, CancellationToken.None);
        Check(older.SynthesisBody is { } olderBody && !olderBody.ContainsKey("tempoDynamicsScale")
            && olderBody["speedScale"]?.GetValue<double>() == 1.3, "fields the engine does not return are not added");
    }

    private static async Task CheckVoiceSelectionAsync()
    {
        Check(await SelectAsync(new AudioOptions()) == 888753760, "no choice uses the first model's normal style");
        Check(await SelectAsync(new AudioOptions { SpeakerUuid = Kohaku }) == 1878365376, "a model alone uses its normal style");
        Check(await SelectAsync(new AudioOptions { SpeakerUuid = Kohaku, StyleId = 1878365377, StyleName = "あまあま" })
            == 1878365377, "a style ID is exact");
        Check(await SelectAsync(new AudioOptions { SpeakerUuid = Kohaku, StyleId = 42, StyleName = "あまあま" }) == 1878365377,
            "a missing style ID falls back to the style name within the model");
        Check(await SelectAsync(new AudioOptions { SpeakerUuid = Kohaku, StyleId = 42, StyleName = "なし" }) == 1878365376,
            "a missing style falls back to the model's first style");
        Check(await SelectAsync(new AudioOptions { StyleId = 888753761 }) == 888753761, "a 0.2.0 style ID still works");

        using var http = new HttpClient(new FakeEngine());
        using var client = new AivisClient(new AudioOptions { SpeakerUuid = "00000000-0000-0000-0000-000000000000" }, http);
        Check(await ThrowsAsync<SpeechEngineException>(() => client.SelectVoiceAsync(CancellationToken.None)),
            "a model that is not installed is reported");

        static async Task<int> SelectAsync(AudioOptions options)
        {
            using var http = new HttpClient(new FakeEngine());
            using var client = new AivisClient(options, http);
            return (await client.SelectVoiceAsync(CancellationToken.None)).StyleId;
        }
    }

    private static void Check(bool condition, string description)
    {
        if (!condition)
            throw new InvalidOperationException("QRV check failed: " + description);
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return true; }
        return false;
    }

    private static async Task<bool> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return true; }
        return false;
    }

    /// <summary>Answers like AivisSpeech Engine 1.1 for the speakers, audio_query and synthesis endpoints.</summary>
    private sealed class FakeEngine : HttpMessageHandler
    {
        public bool OmitAivisFields { get; init; }
        public JsonObject? SynthesisBody { get; private set; }
        /// <summary>While set and not completed, synthesis requests wait, like a slow engine.</summary>
        public TaskCompletionSource? HoldSynthesis { get; set; }
        public SemaphoreSlim SynthesisStarted { get; } = new(0);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/speakers")
                return Json($$"""
                    [{"name":"まお","speaker_uuid":"{{Mao}}","styles":[
                        {"name":"ノーマル","id":888753760,"type":"talk"},{"name":"ふつう","id":888753761,"type":"talk"}]},
                     {"name":"コハク","speaker_uuid":"{{Kohaku}}","styles":[
                        {"name":"ノーマル","id":1878365376,"type":"talk"},{"name":"あまあま","id":1878365377,"type":"talk"}]}]
                    """);
            if (path == "/audio_query")
            {
                var query = new JsonObject
                {
                    ["accent_phrases"] = new JsonArray(), ["speedScale"] = 1.0, ["intonationScale"] = 1.0,
                    ["tempoDynamicsScale"] = 1.0, ["pitchScale"] = 0.0, ["volumeScale"] = 1.0,
                    ["prePhonemeLength"] = 0.1, ["postPhonemeLength"] = 0.1, ["pauseLength"] = null,
                    ["pauseLengthScale"] = 1.0, ["outputSamplingRate"] = 44100, ["outputStereo"] = false, ["kana"] = "テスト"
                };
                if (OmitAivisFields)
                    query.Remove("tempoDynamicsScale");
                return Json(query.ToJsonString());
            }
            if (path == "/synthesis")
            {
                SynthesisBody = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct)) as JsonObject;
                SynthesisStarted.Release();
                if (HoldSynthesis is { } hold)
                    await hold.Task.WaitAsync(ct);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Wave()) };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private static byte[] Wave()
        {
            var wav = new byte[48];
            "RIFF"u8.CopyTo(wav);
            BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(4), 40);
            "WAVEfmt "u8.CopyTo(wav.AsSpan(8));
            BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(16), 16);
            BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(22), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(24), 24000);
            BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(28), 48000);
            BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(32), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(34), 16);
            "data"u8.CopyTo(wav.AsSpan(36));
            BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(40), 4);
            return wav;
        }
    }
}
