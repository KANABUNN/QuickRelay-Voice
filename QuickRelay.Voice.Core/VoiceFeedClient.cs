using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Text;
using QuickRelay.Voice.Core.Feed;

namespace QuickRelay.Voice.Core;

public enum FeedExit
{
    Disconnected,
    NormalClosure,
    ServiceRestart,
    Timeout,
    Protocol,
    Authentication,
    Incompatible,
    ForbiddenEnvironment,
    ResyncRequired,
    TokenRevoked,
    LimitExceeded,
    SlowClient,
    RateLimited
}

public sealed class VoiceFeedClient
{
    private static readonly HttpMessageInvoker Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        SslOptions = { EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }
    });
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly HttpMessageInvoker http;

    public VoiceFeedClient() : this(Http) { }

    internal VoiceFeedClient(HttpMessageInvoker http) => this.http = http;

    public async Task<FeedExit> RunAsync(
        Uri endpoint, string token, string environment, FeedState? prior,
        bool useCursor, Action connected, Action<FeedApplyResult> receive, CancellationToken ct)
    {
        using var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
        try
        {
            var reducer = new FeedReducer(environment, useCursor ? prior : null);
            using (var opening = Deadline(ct, FeedProtocol.SyncTimeout))
                await socket.ConnectAsync(endpoint, http, opening.Token).ConfigureAwait(false);
            var syncClock = Stopwatch.StartNew();
            using (var subscribe = Deadline(ct, TimeSpan.FromSeconds(5)))
                await socket.SendAsync(
                    FeedProtocol.CreateSubscribe(environment, useCursor ? prior?.Cursor : null),
                    WebSocketMessageType.Text, true, subscribe.Token).ConfigureAwait(false);

            connected();
            var buffer = ArrayPool<byte>.Shared.Rent(FeedProtocol.MaximumFrameBytes);
            try
            {
                var bytesDuringSync = 0;
                var heartbeatClock = Stopwatch.StartNew();
                while (!ct.IsCancellationRequested)
                {
                    var remaining = reducer.IsSynchronized
                        ? FeedProtocol.HealthTimeout - heartbeatClock.Elapsed
                        : FeedProtocol.SyncTimeout - syncClock.Elapsed;
                    if (remaining <= TimeSpan.Zero)
                        return FeedExit.Timeout;
                    using var packetDeadline = Deadline(ct, remaining);
                    var (length, exit) = await ReadFrameAsync(socket, buffer, packetDeadline.Token).ConfigureAwait(false);
                    if (exit is { } reason)
                        return reason;
                    if (!reducer.IsSynchronized)
                    {
                        bytesDuringSync += length;
                        if (bytesDuringSync > FeedProtocol.MaximumSyncBytes)
                            return FeedExit.LimitExceeded;
                    }
                    StrictUtf8.GetCharCount(buffer.AsSpan(0, length));
                    var message = FeedProtocol.Parse(buffer.AsSpan(0, length));
                    var applied = reducer.Apply(message);
                    if (applied.Health is not null || applied.CompletedSync)
                        heartbeatClock.Restart();
                    receive(applied);
                }
                return FeedExit.Disconnected;
            }
            finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return FeedExit.Timeout; }
        catch (FeedProtocolException) { return FeedExit.Protocol; }
        catch (DecoderFallbackException) { return FeedExit.Protocol; }
        catch (WebSocketException) when (socket.HttpStatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return FeedExit.Authentication;
        }
        catch (WebSocketException) when (socket.HttpStatusCode == HttpStatusCode.TooManyRequests)
        {
            return FeedExit.RateLimited;
        }
        catch (WebSocketException) { return FeedExit.Disconnected; }
        catch (HttpRequestException) { return FeedExit.Disconnected; }
    }

    private static async Task<(int Length, FeedExit? Exit)> ReadFrameAsync(
        ClientWebSocket socket, byte[] buffer, CancellationToken ct)
    {
        var length = 0;
        while (length < FeedProtocol.MaximumFrameBytes)
        {
            var result = await socket.ReceiveAsync(
                buffer.AsMemory(length, FeedProtocol.MaximumFrameBytes - length), ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return (0, CategorizeClose(socket.CloseStatus));
            if (result.MessageType != WebSocketMessageType.Text)
                throw new FeedProtocolException("テキストフレームが必要です。");
            length += result.Count;
            if (result.EndOfMessage)
                return (length, null);
        }
        // WebSocket fragmentation permits an empty last fragment at the size boundary.
        var ending = await socket.ReceiveAsync(buffer.AsMemory(0, 1), ct).ConfigureAwait(false);
        if (ending.MessageType == WebSocketMessageType.Text && ending.Count == 0 && ending.EndOfMessage)
            return (length, null);
        if (ending.MessageType == WebSocketMessageType.Close)
            return (0, CategorizeClose(socket.CloseStatus));
        return (0, FeedExit.LimitExceeded);
    }

    private static CancellationTokenSource Deadline(CancellationToken ct, TimeSpan duration)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(duration);
        return linked;
    }

    private static FeedExit CategorizeClose(WebSocketCloseStatus? closeCode) => (int?)closeCode switch
    {
        1000 => FeedExit.NormalClosure,
        1001 => FeedExit.ServiceRestart,
        4400 => FeedExit.Incompatible,
        4403 => FeedExit.ForbiddenEnvironment,
        4408 => FeedExit.Timeout,
        4409 => FeedExit.ResyncRequired,
        4410 => FeedExit.TokenRevoked,
        4413 => FeedExit.LimitExceeded,
        4429 => FeedExit.SlowClient,
        _ => FeedExit.Disconnected
    };
}
