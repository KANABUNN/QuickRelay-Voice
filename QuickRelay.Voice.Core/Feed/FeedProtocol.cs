using System.Globalization;
using System.Text.Json;

namespace QuickRelay.Voice.Core.Feed;

public static partial class FeedProtocol
{
    public const int MaximumFrameBytes = 256 * 1024;
    public const int MaximumSyncBytes = 4 * 1024 * 1024;
    public static TimeSpan SyncTimeout { get; } = TimeSpan.FromSeconds(10);
    public static TimeSpan HealthTimeout { get; } = TimeSpan.FromSeconds(15);

    public static bool IsEnvironment(string environment) => environment is "production" or "training" or "test";

    public static byte[] CreateSubscribe(string environment, FeedCursor? resume)
    {
        if (!IsEnvironment(environment))
        {
            throw new ArgumentException("Unsupported EEW environment.", nameof(environment));
        }
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            type = "subscribe",
            environment,
            resume = resume is null ? null : new
            {
                streamId = resume.StreamId,
                epoch = resume.Epoch,
                seq = resume.Seq.ToString(CultureInfo.InvariantCulture)
            }
        });
    }

    public static FeedMessage Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length is 0 or > MaximumFrameBytes)
        {
            throw new FeedProtocolException("Invalid feed frame size.");
        }

        try
        {
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { MaxDepth = 32 });
            using var document = JsonDocument.ParseValue(ref reader);
            if (reader.Read())
            {
                throw new FeedProtocolException("An EEW frame must contain one JSON object.");
            }

            var root = Object(document.RootElement, "message");
            if (Integer(root, "schemaVersion") != 1)
            {
                throw new FeedProtocolException("Unsupported EEW schema version.");
            }

            return Text(root, "type") switch
            {
                "sync_begin" => new SyncBegin(
                    Text(root, "streamId"), Text(root, "epoch"),
                    Text(root, "mode") switch
                    {
                        "resume" => SyncMode.Resume,
                        "snapshot" => SyncMode.Snapshot,
                        _ => throw Invalid("mode")
                    },
                    Decimal(root, "asOfSeq")),
                "eew.state" => ParseState(root),
                "snapshot" => ParseSnapshot(root),
                "sync_complete" => new SyncComplete(ParseCursor(Required(root, "cursor"))),
                "health" => new HealthMessage(
                    ReadTime(Required(root, "serverTime"), "serverTime"), ParseHealth(root, "upstream"), ParseHealth(root, "capture")),
                _ => throw Invalid("type")
            };
        }
        catch (JsonException ex)
        {
            throw new FeedProtocolException("Malformed EEW JSON.", ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new FeedProtocolException("Invalid EEW JSON value.", ex);
        }
        catch (FormatException ex)
        {
            throw new FeedProtocolException("Invalid EEW JSON value.", ex);
        }
    }

    private static SnapshotMessage ParseSnapshot(JsonElement root)
    {
        var part = Integer(root, "part");
        var parts = Integer(root, "parts");
        if (part < 1 || part > parts)
            throw Invalid("part");
        return new SnapshotMessage(Decimal(root, "asOfSeq"), part, parts, ParseEvents(Required(root, "events")),
            Text(root, "streamId"), Text(root, "epoch"));
    }

    private static StateMessage ParseState(JsonElement root)
    {
        var changed = Text(root, "changedSeries") switch
        {
            "forecast" => SeriesKind.Forecast,
            "warning" => SeriesKind.Warning,
            _ => throw Invalid("changedSeries")
        };
        var feedEvent = ParseEvent(Required(root, "event"));
        if (feedEvent.GetSeries(changed) is null)
        {
            throw Invalid("changedSeries");
        }

        return new StateMessage(Text(root, "streamId"), Text(root, "epoch"), PositiveDecimal(root, "seq"),
            Text(root, "delivery") switch
            {
                "live" => DeliveryKind.Live,
                "replay" => DeliveryKind.Replay,
                _ => throw Invalid("delivery")
            },
            Text(root, "cause") switch
            {
                "telegram" => StateCause.Telegram,
                "reconcile" => StateCause.Reconcile,
                _ => throw Invalid("cause")
            },
            ReadTime(Required(root, "committedAt"), "committedAt"), changed, feedEvent);
    }

    private static FeedEvent ParseEvent(JsonElement value)
    {
        var obj = Object(value, "event");
        var environment = Text(obj, "environment");
        if (!IsEnvironment(environment))
        {
            throw Invalid("environment");
        }

        var forecast = ParseSeries(Required(obj, "forecast"), SeriesKind.Forecast);
        var warning = ParseSeries(Required(obj, "warning"), SeriesKind.Warning);
        if (forecast is null && warning is null)
        {
            throw Invalid("event");
        }

        return new FeedEvent(Text(obj, "eventId"), environment, PositiveDecimal(obj, "revision"), forecast, warning);
    }

    private static FeedSeries? ParseSeries(JsonElement value, SeriesKind kind)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var obj = Object(value, "series");
        var status = Text(obj, "status") switch
        {
            "active" => SeriesStatus.Active,
            "final" when kind == SeriesKind.Forecast => SeriesStatus.Final,
            "canceled" => SeriesStatus.Canceled,
            _ => throw Invalid("status")
        };
        var source = ParseSource(Required(obj, "source"), kind);
        var data = Required(obj, "data");
        var speechUntil = ReadNullableTime(Required(obj, "speechUntil"), "speechUntil");
        if (status == SeriesStatus.Canceled)
        {
            if (source.InfoType != InfoType.Cancel || data.ValueKind != JsonValueKind.Null)
            {
                throw Invalid("canceled series");
            }
            VerifySpeechDeadline(source, speechUntil, 30);
        }
        else
        {
            if (source.InfoType == InfoType.Cancel || data.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("data");
            }
            // A final forecast may come with or without a deadline; without one, QRV allows the same 10 seconds.
            VerifySpeechDeadline(source, speechUntil, kind == SeriesKind.Warning ? 15 : 10);
        }

        return new FeedSeries(status, PositiveDecimal(obj, "revision"), source,
            status != SeriesStatus.Canceled && kind == SeriesKind.Forecast ? ParseForecast(data) : null,
            status != SeriesStatus.Canceled && kind == SeriesKind.Warning ? ParseWarning(data, source) : null,
            speechUntil);
    }

    private static FeedSource ParseSource(JsonElement value, SeriesKind kind)
    {
        var source = Object(value, "source");
        var code = Text(source, "code");
        if (code != (kind == SeriesKind.Forecast ? "VXSE45" : "VXSE43"))
        {
            throw Invalid("source.code");
        }

        var serialValue = Required(source, "serial");
        var serial = serialValue.ValueKind == JsonValueKind.Null ? null : StringValue(serialValue, "serial");
        if (serial is not null)
        {
            ParseDecimal(serial, "serial");
        }

        var issuer = Text(source, "issuer");
        var provider = Text(source, "provider");
        if (issuer != "JMA" || provider != "DMDATA")
            throw Invalid("source.issuer/provider");

        return new FeedSource(issuer, provider, code, Text(source, "telegramId"), serial,
            Text(source, "infoType") switch
            {
                "issue" => InfoType.Issue,
                "correction" => InfoType.Correction,
                "cancel" => InfoType.Cancel,
                _ => throw Invalid("infoType")
            },
            ReadNullableTime(Required(source, "issuedAt"), "issuedAt"),
            ReadNullableTime(Required(source, "sentAt"), "sentAt"),
            ReadNullableTime(Required(source, "receivedAt"), "receivedAt"),
            ReadNullableTime(Required(source, "controlAt"), "controlAt"),
            Text(source, "origin") switch
            {
                "live" => SourceOrigin.Live,
                "recovered" => SourceOrigin.Recovered,
                "historical" => SourceOrigin.Historical,
                _ => throw Invalid("origin")
            });
    }

    private static IReadOnlyList<FeedEvent> ParseEvents(JsonElement source)
    {
        var list = new List<FeedEvent>();
        foreach (var value in Array(source, "events"))
        {
            list.Add(ParseEvent(value));
        }
        return list.AsReadOnly();
    }

    private static FeedCursor ParseCursor(JsonElement value)
    {
        var cursor = Object(value, "cursor");
        return new FeedCursor(Text(cursor, "streamId"), Text(cursor, "epoch"), Decimal(cursor, "seq"));
    }

    private static HealthStatus ParseHealth(JsonElement value, string key) => Text(value, key) switch
    {
        "healthy" => HealthStatus.Healthy,
        "degraded" => HealthStatus.Degraded,
        _ => throw Invalid(key)
    };

    private static void VerifySpeechDeadline(FeedSource source, DateTimeOffset? deadline, int maximumSeconds)
    {
        if (deadline is { } until &&
            (source.IssuedAt is not { } issued || until < issued || until - issued > TimeSpan.FromSeconds(maximumSeconds)))
        {
            throw Invalid("speechUntil");
        }
    }

    private static JsonElement Object(JsonElement value, string key)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(key);
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw Invalid(key);
            }
        }
        return value;
    }

    private static JsonElement.ArrayEnumerator Array(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : throw Invalid(key);

    private static JsonElement Required(JsonElement value, string key) =>
        value.TryGetProperty(key, out var property) ? property : throw Invalid(key);

    private static string Text(JsonElement value, string key) => StringValue(Required(value, key), key);

    private static string Label(JsonElement value, string key) => StringValue(Required(value, key), key, allowEmpty: true);

    private static string? OptionalLabel(JsonElement value, string key) =>
        value.TryGetProperty(key, out var property) ? StringValue(property, key, allowEmpty: true) : null;

    private static string? OptionalText(JsonElement value, string key) =>
        value.TryGetProperty(key, out var property) ? StringValue(property, key) : null;

    private static string StringValue(JsonElement value, string key, bool allowEmpty = false)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text ||
            (!allowEmpty && string.IsNullOrWhiteSpace(text)) || text.Any(char.IsControl))
        {
            throw Invalid(key);
        }
        return text;
    }

    private static int Integer(JsonElement obj, string key)
    {
        var value = Required(obj, key);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result) ? result : throw Invalid(key);
    }

    private static ulong PositiveDecimal(JsonElement obj, string key)
    {
        var value = Decimal(obj, key);
        return value > 0 ? value : throw Invalid(key);
    }

    private static ulong Decimal(JsonElement obj, string key) => ParseDecimal(Text(obj, key), key);

    private static ulong ParseDecimal(string value, string key) =>
        value.Length > 0 && (value.Length == 1 || value[0] != '0')
        && ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong parsed)
            ? parsed : throw Invalid(key);

    private static DateTimeOffset? ReadNullableTime(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Null ? null : ReadTime(value, key);

    private static DateTimeOffset ReadTime(JsonElement value, string key)
    {
        var text = StringValue(value, key);
        if (text.Length < 20 || text[16] != ':' ||
            !(text.EndsWith('Z') || text.EndsWith("+00:00", StringComparison.Ordinal)) ||
            !value.TryGetDateTimeOffset(out var result))
        {
            throw Invalid(key);
        }
        return result;
    }

    private static FeedProtocolException Invalid(string key) => new($"Invalid or missing EEW field {key}.");
}
