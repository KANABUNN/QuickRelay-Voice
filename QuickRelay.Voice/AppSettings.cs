using System.Security.Cryptography;
using System.Text;

namespace QuickRelay.Voice;

public sealed record AppSettings
{
    internal const string FeedEnvironment = "production";
    public string FeedUrl { get; init; } = "";
    public string Environment { get; init; } = FeedEnvironment;
    public bool Muted { get; init; } = true;
    public bool Nationwide { get; init; } = true;
    public string[] Regions { get; init; } = [];
    public bool ForecastEnabled { get; init; }
    public string ForecastThreshold { get; init; } = "4";
    public int AivisPort { get; init; } = 10101;
    public int? StyleId { get; init; }
    public string? SpeakerUuid { get; init; }
    public string? SpeakerName { get; init; }
    public string? StyleName { get; init; }
    public VoiceTuning Tuning { get; init; } = new();
    public bool MinimizeToTray { get; init; } = true;
    public bool StartInTray { get; init; }
    public bool AutoConnect { get; init; }

    internal Uri Validate()
    {
        if (!Uri.TryCreate(FeedUrl?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != "wss" || uri.AbsolutePath != "/v1/eew"
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("接続先は wss://ホスト/v1/eew の形式で入力してください。");
        if (Environment != FeedEnvironment)
            throw new ArgumentException("QuickRelay の現在の配信環境は production です。");
        if (Regions is null || (!Nationwide && (Regions.Length == 0 || Regions.Length > 250
            || Regions.Any(region => region is null || region.Split(':') is not [var type, var code]
                || string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(code)))))
            throw new ArgumentException("対象地域をコード種別:コードの形式で指定してください。");
        if (ForecastThreshold is not ("0" or "1" or "2" or "3" or "4" or "5-" or "5+" or "6-" or "6+" or "7"))
            throw new ArgumentException("予報の最低震度を確認してください。");
        ValidateVoice();
        return uri;
    }

    internal void ValidateVoice()
    {
        ValidateAudio(AivisPort);
        if (SpeakerUuid is { Length: > 64 } || SpeakerName is { Length: > 200 } || StyleName is { Length: > 200 })
            throw new ArgumentException("AivisSpeech のモデルとスタイルを一覧から選び直してください。");
        if (Tuning is null || !Tuning.IsValid)
            throw new ArgumentException("AivisSpeech の音声パラメーターが範囲外です。");
    }

    /// <summary>Repairs values that a hand-edited or older settings file can leave missing.</summary>
    internal AppSettings Normalize() => Tuning is { IsValid: true } ? this : this with { Tuning = new() };

    /// <summary>Copies the AivisSpeech choices without touching the feed settings.</summary>
    internal AppSettings WithVoiceFrom(AppSettings source) => this with
    {
        AivisPort = source.AivisPort, StyleId = source.StyleId, SpeakerUuid = source.SpeakerUuid,
        SpeakerName = source.SpeakerName, StyleName = source.StyleName, Tuning = source.Tuning
    };

    /// <summary>Copies the window and startup choices without touching the feed settings.</summary>
    internal AppSettings WithResidencyFrom(AppSettings source) => this with
    {
        MinimizeToTray = source.MinimizeToTray, StartInTray = source.StartInTray, AutoConnect = source.AutoConnect
    };

    internal string Scope => Uri.TryCreate(FeedUrl?.Trim(), UriKind.Absolute, out var uri)
        ? Hash(uri.AbsoluteUri + "\n" + Environment)
        : "";

    internal static string Hash(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    internal static void ValidateAudio(int port)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentException("AivisSpeech のポート番号を確認してください。");
    }
}

/// <summary>
/// AivisSpeech の AudioQuery に上書きする合成パラメーター。範囲は AivisSpeech Engine の API 仕様に合わせる。
/// </summary>
public sealed record VoiceTuning
{
    public double Speed { get; init; } = 1.08;
    public double Intonation { get; init; } = 1;
    public double Tempo { get; init; } = 1;
    public double Pitch { get; init; }
    public double Volume { get; init; } = 1;
    public double PauseScale { get; init; } = 1;
    public double PrePhoneme { get; init; } = 0.1;
    public double PostPhoneme { get; init; } = 0.1;

    internal static IReadOnlyList<TuningParameter> Parameters { get; } =
    [
        new("話速", "speedScale", 0.5, 2, "全体の話す速さ。1.00 が標準です。",
            t => t.Speed, (t, v) => t with { Speed = v }),
        new("感情表現", "intonationScale", 0, 2,
            "選んだスタイルの感情表現の強さ。ノーマルスタイルでは自動で調整され、この値は使われません。",
            t => t.Intonation, (t, v) => t with { Intonation = v }),
        new("テンポの緩急", "tempoDynamicsScale", 0, 2, "大きいほど速さの緩急が付いた話し方になります。",
            t => t.Tempo, (t, v) => t with { Tempo = v }),
        new("音高", "pitchScale", -0.15, 0.15, "0.00 から変えると音質が下がることがあります。",
            t => t.Pitch, (t, v) => t with { Pitch = v }),
        new("音量", "volumeScale", 0, 2, "読み上げと内蔵の警報音の音量。",
            t => t.Volume, (t, v) => t with { Volume = v }),
        new("句読点の間", "pauseLengthScale", 0, 2, "句読点などで入る無音の長さの倍率。",
            t => t.PauseScale, (t, v) => t with { PauseScale = v }),
        new("前の無音", "prePhonemeLength", 0, 1.5, "話し始める前の無音 (秒)。長いほど警報の出音が遅れます。",
            t => t.PrePhoneme, (t, v) => t with { PrePhoneme = v }),
        new("後の無音", "postPhonemeLength", 0, 1.5, "話し終えた後の無音 (秒)。",
            t => t.PostPhoneme, (t, v) => t with { PostPhoneme = v }),
    ];

    internal bool IsValid => Parameters.All(parameter => parameter.Accepts(parameter.Read(this)));
}

internal sealed record TuningParameter(
    string Label, string QueryKey, double Min, double Max, string Hint,
    Func<VoiceTuning, double> Read, Func<VoiceTuning, double, VoiceTuning> Write)
{
    internal bool Accepts(double value) => double.IsFinite(value) && value >= Min && value <= Max;
}

public sealed record VoiceStyle(int Id, string SpeakerUuid, string SpeakerName, string StyleName);

public sealed record VoiceStatus(
    string Connection = "未接続",
    string Upstream = "未確認",
    string Capture = "未確認",
    string Engine = "未確認",
    string Output = "ミュート",
    string Clock = "未確認",
    string Cursor = "—");
