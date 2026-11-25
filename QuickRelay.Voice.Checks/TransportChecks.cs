using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using QuickRelay.Voice.Core;
using QuickRelay.Voice.Core.Feed;

internal static class TransportChecks
{
    private const string Token = "local-fixture-token";
    private const string LimitedToken = "rate-limited-fixture-token";
    private const string EventId = "試験地震";
    private const string Stream = "local-fixture";
    private const string Epoch = "initial";
    private static readonly (int Code, FeedExit Exit)[] CloseCases =
    [
        (1000, FeedExit.NormalClosure),
        (1001, FeedExit.ServiceRestart),
        (4400, FeedExit.Incompatible),
        (4403, FeedExit.ForbiddenEnvironment),
        (4408, FeedExit.Timeout),
        (4409, FeedExit.ResyncRequired),
        (4410, FeedExit.TokenRevoked),
        (4413, FeedExit.LimitExceeded),
        (4429, FeedExit.SlowClient)
    ];

    public static async Task RunAsync()
    {
        using var timeLimit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var certificate = CreateCertificate();
        var handlerErrors = new ConcurrentQueue<Exception>();
        var attempts = 0;
        var authenticationFailures = 0;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        await using var app = builder.Build();
        app.UseWebSockets();
        app.MapGet("/v1/eew", async (HttpContext context) =>
        {
            if (context.Request.Headers.Authorization == "Bearer " + LimitedToken)
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return;
            }
            if (context.Request.Headers.Authorization != "Bearer " + Token)
            {
                Interlocked.Increment(ref authenticationFailures);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var attempt = Interlocked.Increment(ref attempts);
            try
            {
                await ServeAsync(context, attempt, timeLimit.Token);
            }
            catch (Exception error)
            {
                handlerErrors.Enqueue(error);
                context.Abort();
            }
        });
        await app.StartAsync(timeLimit.Token);
        var endpoint = new UriBuilder(app.Urls.Single()) { Scheme = "wss", Path = "/v1/eew" }.Uri;

        var connected = false;
        var rejected = await new VoiceFeedClient().RunAsync(endpoint, Token, "production", null, false,
            () => connected = true, _ => { }, timeLimit.Token);
        Expect(rejected == FeedExit.Disconnected && !connected && attempts == 0,
            "Default TLS must reject the untrusted fixture certificate before subscribing.");

        SslPolicyErrors? certificateErrors = null;
        using var pinned = new HttpMessageInvoker(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = (_, actual, _, errors) =>
                {
                    certificateErrors = errors;
                    return (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None &&
                        actual is not null && actual.GetCertHashString(HashAlgorithmName.SHA256) ==
                        certificate.GetCertHashString(HashAlgorithmName.SHA256);
                }
            }
        });
        var client = new VoiceFeedClient(pinned);
        var unauthorized = await client.RunAsync(endpoint, "wrong-fixture-token", "production", null, false,
            () => connected = true, _ => { }, timeLimit.Token);
        Expect(unauthorized == FeedExit.Authentication && !connected && authenticationFailures == 1,
            $"TLS request without the fixture bearer token must surface HTTP 401 (exit {unauthorized}, unauthorized requests {authenticationFailures}, certificate errors {certificateErrors}).");
        var throttled = await client.RunAsync(endpoint, LimitedToken, "production", null, false,
            () => connected = true, _ => { }, timeLimit.Token);
        Expect(throttled == FeedExit.RateLimited && !connected, "HTTP 429 must surface rate limiting.");

        var applied = new List<FeedApplyResult>();
        var disconnected = await client.RunAsync(endpoint, Token, "production", null, false,
            () => connected = true, applied.Add, timeLimit.Token);
        CheckHandler(handlerErrors);
        Expect(disconnected == FeedExit.NormalClosure && connected && attempts == 1,
            "First authenticated websocket must complete, then close at the server's request.");
        Expect(applied.Any(result => result.CompletedSync) && applied.Any(result => result.Health is not null),
            "Websocket must deliver the snapshot boundary and health control frame.");
        var last = applied.Last();
        var prior = last.CommittedState ?? throw new InvalidOperationException("Missing committed state after live delivery.");
        Expect(prior.Cursor == new FeedCursor(Stream, Epoch, 4) && prior.Events.ContainsKey(EventId),
            "UTF-8 split inside a Japanese character must reconstruct the same event at seq 4.");

        applied.Clear();
        var gap = await client.RunAsync(endpoint, Token, "production", prior, true, () => { }, applied.Add, timeLimit.Token);
        CheckHandler(handlerErrors);
        Expect(gap == FeedExit.Protocol && attempts == 2 && applied.Any(result => result.CompletedSync),
            "Resume must complete at seq 4 and reject the jump to seq 6.");
        Expect(applied.All(result => result.CommittedState?.Cursor?.Seq is null or 4),
            "The skipped log position must not commit as a live update.");

        applied.Clear();
        var recovered = await client.RunAsync(endpoint, Token, "production", prior, false, () => { }, applied.Add, timeLimit.Token);
        CheckHandler(handlerErrors);
        Expect(recovered == FeedExit.NormalClosure && attempts == 3 &&
            applied.Single(result => result.CompletedSync).CommittedState?.Cursor?.Seq == 6,
            "A cursor reset after the gap must accept a replacement snapshot and commit its boundary.");

        foreach (var (code, expected) in CloseCases)
        {
            applied.Clear();
            var exit = await client.RunAsync(endpoint, Token, "production", prior, true, () => { }, applied.Add, timeLimit.Token);
            CheckHandler(handlerErrors);
            Expect(exit == expected && applied.Count == 0, $"QuickRelay close code {code} must return {expected} before committing state (received {exit}).");
        }
        await app.StopAsync(timeLimit.Token);
        Console.WriteLine("PASS local TLS websocket: certificate rejection, Bearer/401, HTTP 429, subscribe, resume, gap reset and all documented close codes");
    }

    private static async Task ServeAsync(HttpContext context, int attempt, CancellationToken cancellationToken)
    {
        Expect(context.WebSockets.IsWebSocketRequest, "The authorized request must be a WebSocket upgrade.");
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        await CheckSubscriptionAsync(socket, attempt == 2 || attempt > 3, cancellationToken);
        if (attempt > 3)
        {
            var status = (WebSocketCloseStatus)CloseCases[attempt - 4].Code;
            await socket.CloseOutputAsync(status, "fixture close", cancellationToken);
            return;
        }
        var boundary = attempt switch { 1 => 3, 2 => 4, 3 => 6, _ => throw new InvalidOperationException("Unexpected upgrade.") };
        var mode = attempt == 2 ? "resume" : "snapshot";
        await SendAsync(socket, $$"""
            {"schemaVersion":1,"type":"sync_begin","streamId":"{{Stream}}","epoch":"{{Epoch}}","mode":"{{mode}}","asOfSeq":"{{boundary}}"}
            """, cancellationToken);
        if (attempt != 2)
        {
            await SendAsync(socket, $$"""
                {"schemaVersion":1,"type":"snapshot","streamId":"{{Stream}}","epoch":"{{Epoch}}","asOfSeq":"{{boundary}}","part":1,"parts":1,"events":[]}
                """, cancellationToken);
        }
        await SendAsync(socket, $$$"""
            {"schemaVersion":1,"type":"sync_complete","cursor":{"streamId":"{{{Stream}}}","epoch":"{{{Epoch}}}","seq":"{{{boundary}}}"}}
            """, cancellationToken);
        if (attempt < 3)
        {
            await SendAsync(socket, """
                {"schemaVersion":1,"type":"health","serverTime":"2026-11-25T05:00:00Z","upstream":"healthy","capture":"healthy"}
                """, cancellationToken);
            await SendFragmentedStateAsync(socket, attempt == 1 ? 4 : 6, attempt, cancellationToken);
        }
        if (attempt != 2)
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "fixture complete", cancellationToken);
        }
    }

    private static async Task CheckSubscriptionAsync(WebSocket socket, bool expectResume, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var bytes = 0;
        while (bytes < buffer.Length)
        {
            var fragment = await socket.ReceiveAsync(buffer.AsMemory(bytes), cancellationToken);
            Expect(fragment.MessageType == WebSocketMessageType.Text, "Subscribe must be a text message.");
            bytes += fragment.Count;
            if (fragment.EndOfMessage)
            {
                using var document = JsonDocument.Parse(buffer.AsMemory(0, bytes));
                var subscribe = document.RootElement;
                Expect(subscribe.GetProperty("schemaVersion").GetInt32() == 1 &&
                    subscribe.GetProperty("type").GetString() == "subscribe" &&
                    subscribe.GetProperty("environment").GetString() == "production", "Subscribe header mismatch.");
                var cursor = subscribe.GetProperty("resume");
                Expect(expectResume
                    ? cursor.GetProperty("streamId").GetString() == Stream && cursor.GetProperty("epoch").GetString() == Epoch &&
                        cursor.GetProperty("seq").GetString() == "4"
                    : cursor.ValueKind == JsonValueKind.Null, "Subscribe resume cursor mismatch.");
                return;
            }
        }
        throw new InvalidOperationException("Subscribe frame is larger than the fixture limit.");
    }

    private static async Task SendFragmentedStateAsync(WebSocket socket, int seq, int revision, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes($$"""
            {"schemaVersion":1,"type":"eew.state","streamId":"{{Stream}}","epoch":"{{Epoch}}","seq":"{{seq}}",
             "delivery":"live","cause":"telegram","changedSeries":"warning","committedAt":"2026-11-25T05:00:00Z",
             "event":{"eventId":"{{EventId}}","environment":"production","revision":"{{revision}}","forecast":null,
                      "warning":{"status":"canceled","revision":"{{revision}}","data":null,"speechUntil":null,
                                 "source":{"issuer":"JMA","provider":"DMDATA","code":"VXSE43","telegramId":"test-{{seq}}",
                                           "serial":"1","infoType":"cancel","issuedAt":"2026-11-25T05:00:00Z",
                                           "sentAt":null,"receivedAt":"2026-11-25T05:00:00Z","controlAt":null,"origin":"live"}
                                }
                     }
            }
            """);
        var boundary = bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(EventId)) + 1;
        await socket.SendAsync(bytes.AsMemory(0, boundary), WebSocketMessageType.Text, false, cancellationToken);
        await socket.SendAsync(bytes.AsMemory(boundary), WebSocketMessageType.Text, true, cancellationToken);
    }

    private static ValueTask SendAsync(WebSocket socket, string frame, CancellationToken cancellationToken) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(frame).AsMemory(), WebSocketMessageType.Text, true, cancellationToken);

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=QuickRelay Voice Fixture", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(30));
        if (!OperatingSystem.IsWindows())
        {
            return certificate;
        }

        // Schannel requires an imported private key, even when this fixture never joins a certificate store.
        using (certificate)
        {
            var exported = certificate.Export(X509ContentType.Pkcs12, string.Empty);
            try
            {
                return X509CertificateLoader.LoadPkcs12(exported, string.Empty, X509KeyStorageFlags.UserKeySet);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(exported);
            }
        }
    }

    private static void CheckHandler(ConcurrentQueue<Exception> errors)
    {
        if (errors.TryDequeue(out var error))
        {
            throw new InvalidOperationException("TLS websocket fixture rejected an unexpected request.", error);
        }
    }

    private static void Expect(bool expected, string message)
    {
        if (!expected)
        {
            throw new InvalidOperationException(message);
        }
    }
}
