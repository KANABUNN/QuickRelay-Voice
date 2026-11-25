using System.Security.Cryptography;
using System.Text;

namespace QuickRelay.Voice;

public sealed record AppSettings
{
    public string FeedUrl { get; init; } = "";
    public string Environment { get; init; } = "production";
    public bool Muted { get; init; } = true;
    public bool Nationwide { get; init; } = true;
    public string[] Regions { get; init; } = [];
    public bool ForecastEnabled { get; init; }
    public string ForecastThreshold { get; init; } = "4";
    public int ForecastStep { get; init; } = 1;
    public int AivisPort { get; init; } = 10101;
    public int? StyleId { get; init; }

    internal Uri Validate()
    {
        if (!Uri.TryCreate(FeedUrl?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != "wss" || uri.AbsolutePath != "/v1/eew"
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("接続先は wss://ホスト/v1/eew の形式で入力してください。");
        if (Environment is not ("production" or "training" or "test"))
            throw new ArgumentException("受信環境を選択してください。");
        if (Regions is null || (!Nationwide && (Regions.Length == 0 || Regions.Length > 250
            || Regions.Any(region => region is null || region.Split(':') is not [var type, var code]
                || string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(code)))))
            throw new ArgumentException("対象地域をコード種別:コードの形式で指定してください。");
        if (ForecastThreshold is not ("0" or "1" or "2" or "3" or "4" or "5-" or "5+" or "6-" or "6+" or "7")
            || ForecastStep is < 1 or > 9)
            throw new ArgumentException("予報の震度しきい値と続報の差分を確認してください。");
        ValidateAudio(AivisPort);
        return uri;
    }

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

public sealed record VoiceStyle(int Id, string Name);

public sealed record VoiceStatus(
    string Connection = "未接続",
    string Upstream = "未確認",
    string Capture = "未確認",
    string Engine = "未確認",
    string Output = "ミュート",
    string Clock = "未確認",
    string Cursor = "—");
