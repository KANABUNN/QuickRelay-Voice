namespace QuickRelay.Voice.Core.Feed;

/// <summary>Where the time went between QuickRelay receiving a telegram and QRV receiving the update it caused.</summary>
/// <param name="AtServer">From QuickRelay's reception of the telegram to the committed update, both on the server's
/// clock. Null when the update did not come from a telegram or the telegram has no reception time.</param>
/// <param name="Delivery">From the commit to QRV's reception of the update. Null until a heartbeat has arrived.</param>
public sealed record FeedDelay(TimeSpan? AtServer, TimeSpan? Delivery);

/// <summary>
/// Times feed updates without trusting this PC's clock, which may be off by up to the two seconds QRV tolerates.
/// QuickRelay stamps each heartbeat with its own clock just before sending it. The heartbeat's arrival on the monotonic
/// clock, less that stamp, is the offset between the clocks plus the network delay, and the smallest value among recent
/// heartbeats is the baseline. A delivery time is therefore the delay beyond the network's fastest one-way trip, which is
/// about half the round trip to the server and is not included.
/// </summary>
public sealed class FeedLatency
{
    // Heartbeats come every five seconds; five minutes of them include a fast one, and the clocks drift little meanwhile.
    private const int Window = 60;
    private readonly Queue<TimeSpan> offsets = new();

    /// <summary>Records a heartbeat stamped <paramref name="serverTime"/> that arrived at <paramref name="arrival"/>
    /// on the monotonic clock.</summary>
    public void Heartbeat(DateTimeOffset serverTime, TimeSpan arrival)
    {
        if (offsets.Count == Window)
            offsets.Dequeue();
        offsets.Enqueue(arrival - (serverTime - DateTimeOffset.UnixEpoch));
    }

    /// <summary>Forgets the heartbeats, since a new connection may take another path.</summary>
    public void Reset() => offsets.Clear();

    /// <summary>Times <paramref name="update"/>, which arrived at <paramref name="arrival"/> on the monotonic clock.</summary>
    public FeedDelay Measure(StateMessage update, TimeSpan arrival)
    {
        var received = update.Cause == StateCause.Telegram
            ? update.Event.GetSeries(update.ChangedSeries)?.Source.ReceivedAt : null;
        TimeSpan? delivery = offsets.Count == 0 ? null
            : arrival - (update.CommittedAt - DateTimeOffset.UnixEpoch) - offsets.Min();
        return new FeedDelay(update.CommittedAt - received, delivery);
    }
}
