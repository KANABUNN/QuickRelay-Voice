using System.Collections.Immutable;

namespace QuickRelay.Voice.Core.Feed;

public sealed record FeedState(FeedCursor? Cursor, IReadOnlyDictionary<string, FeedEvent> Events);

public sealed record FeedApplyResult(
    FeedState? CommittedState = null,
    StateMessage? LiveUpdate = null,
    bool CompletedSync = false,
    HealthMessage? Health = null);

public sealed class FeedReducer
{
    private const int MaximumEvents = 10_000;
    private static readonly FeedApplyResult NoChange = new();
    private readonly string environment;
    private ImmutableDictionary<string, FeedEvent> committedEvents;
    private SyncBegin? boundary;
    private ImmutableDictionary<string, FeedEvent> stagedEvents = ImmutableDictionary<string, FeedEvent>.Empty;
    private ulong stagedSeq;
    private int? partCount;
    private int? firstPart;
    private long nextPart;

    public FeedReducer(string environment, FeedState? lastState = null)
    {
        if (!FeedProtocol.IsEnvironment(environment))
        {
            throw new ArgumentException("Unsupported EEW environment.", nameof(environment));
        }
        this.environment = environment;
        if (lastState?.Cursor is not null && lastState.Events is null)
            throw new FeedProtocolException("Saved EEW state is missing its event map.");
        committedEvents = lastState?.Cursor is null
            ? ImmutableDictionary<string, FeedEvent>.Empty
            : lastState.Events.ToImmutableDictionary(StringComparer.Ordinal);
        if (committedEvents.Count > MaximumEvents
            || committedEvents.Any(pair => pair.Value is null || pair.Key != pair.Value.EventId || pair.Value.Environment != environment))
        {
            throw new FeedProtocolException("Saved EEW state does not match the subscription.");
        }
        State = new FeedState(lastState?.Cursor, committedEvents);
    }

    public FeedState State { get; private set; }

    public bool IsSynchronized { get; private set; }

    public FeedApplyResult Apply(FeedMessage message) => message switch
    {
        HealthMessage health => new FeedApplyResult(Health: health),
        SyncBegin start => Begin(start),
        StateMessage update => Update(update),
        SnapshotMessage part => AddSnapshot(part),
        SyncComplete finish => Complete(finish),
        ErrorMessage => throw new FeedProtocolException("The server rejected the EEW subscription."),
        _ => throw new FeedProtocolException("Unexpected EEW message.")
    };

    private FeedApplyResult Begin(SyncBegin message)
    {
        if (IsSynchronized || boundary is not null)
        {
            throw new FeedProtocolException("Unexpected EEW sync_begin.");
        }

        var cursor = State.Cursor;
        var sameStream = cursor is not null && Matches(cursor, message.StreamId, message.Epoch);
        if (sameStream && message.AsOfSeq < cursor!.Seq)
        {
            throw new FeedProtocolException("EEW sync boundary moved backward within the same epoch.");
        }
        if (message.Mode == SyncMode.Resume)
        {
            if (!sameStream)
            {
                throw new FeedProtocolException("EEW resume does not match the saved cursor.");
            }
            stagedSeq = cursor!.Seq;
            stagedEvents = committedEvents;
        }

        boundary = message;
        return NoChange;
    }

    private FeedApplyResult Update(StateMessage message)
    {
        ValidateEnvironment(message.Event);
        if (IsSynchronized)
        {
            var cursor = State.Cursor!;
            if (message.Delivery != DeliveryKind.Live || !Matches(cursor, message.StreamId, message.Epoch))
            {
                throw new FeedProtocolException("Unexpected EEW stream or delivery after synchronization.");
            }
            if (message.Seq <= cursor.Seq)
            {
                return NoChange;
            }

            RequireNextSequence(cursor.Seq, message.Seq);
            committedEvents = ApplyUpdate(committedEvents, message);
            State = new FeedState(cursor with { Seq = message.Seq }, committedEvents);
            return new FeedApplyResult(State, message);
        }

        if (boundary is not { Mode: SyncMode.Resume } resume || message.Delivery != DeliveryKind.Replay ||
            message.StreamId != resume.StreamId || message.Epoch != resume.Epoch || message.Seq > resume.AsOfSeq)
        {
            throw new FeedProtocolException("Unexpected EEW state during synchronization.");
        }
        if (message.Seq <= stagedSeq)
        {
            return NoChange;
        }

        RequireNextSequence(stagedSeq, message.Seq);
        stagedEvents = ApplyUpdate(stagedEvents, message);
        stagedSeq = message.Seq;
        return NoChange;
    }

    private FeedApplyResult AddSnapshot(SnapshotMessage message)
    {
        if (boundary is not { Mode: SyncMode.Snapshot } snapshot || message.AsOfSeq != snapshot.AsOfSeq ||
            message.StreamId is not null && message.StreamId != snapshot.StreamId ||
            message.Epoch is not null && message.Epoch != snapshot.Epoch || message.Parts <= 0)
        {
            throw new FeedProtocolException("Unexpected EEW snapshot boundary.");
        }

        if (partCount is null)
        {
            if (message.Part is not (0 or 1))
            {
                throw new FeedProtocolException("Invalid first EEW snapshot part.");
            }
            nextPart = message.Part;
            firstPart = message.Part;
            partCount = message.Parts;
        }
        if (message.Parts != partCount || message.Part != nextPart || message.Part - firstPart >= partCount)
        {
            throw new FeedProtocolException("EEW snapshot part sequence is incomplete or inconsistent.");
        }

        var previous = State.Cursor;
        var sameStream = previous is not null && Matches(previous, snapshot.StreamId, snapshot.Epoch);
        foreach (var feedEvent in message.Events)
        {
            ValidateEnvironment(feedEvent);
            if (stagedEvents.Count >= MaximumEvents)
                throw new FeedProtocolException("EEW snapshot exceeds the event limit.");
            if (stagedEvents.ContainsKey(feedEvent.EventId))
            {
                throw new FeedProtocolException("EEW snapshot contains a duplicate event.");
            }
            if (sameStream && committedEvents.TryGetValue(feedEvent.EventId, out var prior))
            {
                RequireSnapshotProgress(prior, feedEvent);
            }
            stagedEvents = stagedEvents.Add(feedEvent.EventId, feedEvent);
        }
        nextPart++;
        return NoChange;
    }

    private FeedApplyResult Complete(SyncComplete message)
    {
        if (boundary is not { } start || !Matches(message.Cursor, start.StreamId, start.Epoch) ||
            message.Cursor.Seq != start.AsOfSeq ||
            (start.Mode == SyncMode.Resume ? stagedSeq != start.AsOfSeq : firstPart is null || nextPart - firstPart != partCount))
        {
            throw new FeedProtocolException("EEW synchronization ended before its boundary was complete.");
        }

        committedEvents = stagedEvents;
        State = new FeedState(message.Cursor, committedEvents);
        boundary = null;
        stagedEvents = ImmutableDictionary<string, FeedEvent>.Empty;
        IsSynchronized = true;
        return new FeedApplyResult(State, CompletedSync: true);
    }

    private static ImmutableDictionary<string, FeedEvent> ApplyUpdate(
        ImmutableDictionary<string, FeedEvent> events, StateMessage update)
    {
        var incoming = update.Event;
        var changed = incoming.GetSeries(update.ChangedSeries) ?? throw new FeedProtocolException("EEW update has no changed series.");
        if (events.Count >= MaximumEvents && !events.ContainsKey(incoming.EventId))
        {
            var retention = TimeSpan.FromHours(24);
            events = events.RemoveRange(events.Where(pair => update.CommittedAt -
                LatestReceivedAt(pair.Value) >= retention).Select(pair => pair.Key));
            if (events.Count >= MaximumEvents)
                throw new FeedProtocolException("EEW state exceeds the event limit.");
        }
        if (!events.TryGetValue(incoming.EventId, out var previous))
        {
            return events.Add(incoming.EventId, incoming);
        }

        if (incoming.Revision <= previous.Revision)
        {
            throw new FeedProtocolException("EEW event revision did not advance.");
        }

        var unchangedKind = update.ChangedSeries == SeriesKind.Forecast ? SeriesKind.Warning : SeriesKind.Forecast;
        if (!SeriesEqual(previous.GetSeries(unchangedKind), incoming.GetSeries(unchangedKind)))
        {
            throw new FeedProtocolException("EEW update changed the other series.");
        }
        if (previous.GetSeries(update.ChangedSeries) is { } old)
        {
            RequireSeriesProgress(old, changed);
            if (update.Cause == StateCause.Telegram && old.WarningData is { } oldWarnings && changed.WarningData is { } newWarnings)
            {
                RequireCumulativeWarnings(oldWarnings, newWarnings);
            }
        }

        return events.SetItem(incoming.EventId, incoming);
    }

    private static DateTimeOffset LatestReceivedAt(FeedEvent value)
    {
        var forecast = value.Forecast?.Source.ReceivedAt ?? DateTimeOffset.MinValue;
        var warning = value.Warning?.Source.ReceivedAt ?? DateTimeOffset.MinValue;
        return forecast > warning ? forecast : warning;
    }

    private static void RequireSnapshotProgress(FeedEvent previous, FeedEvent incoming)
    {
        if (incoming.Revision < previous.Revision)
        {
            throw new FeedProtocolException("EEW snapshot event revision moved backward.");
        }
        RequireSnapshotSeriesProgress(previous.Forecast, incoming.Forecast);
        RequireSnapshotSeriesProgress(previous.Warning, incoming.Warning);
    }

    private static void RequireSnapshotSeriesProgress(FeedSeries? previous, FeedSeries? incoming)
    {
        if (previous is null || incoming is null)
        {
            return;
        }
        if (incoming.Revision == previous.Revision && SeriesEqual(previous, incoming))
        {
            return;
        }
        RequireSeriesProgress(previous, incoming);
    }

    private static void RequireSeriesProgress(FeedSeries previous, FeedSeries changed)
    {
        if (changed.Revision <= previous.Revision)
        {
            throw new FeedProtocolException("EEW series revision did not advance.");
        }
        if (previous.Status == SeriesStatus.Canceled || previous.Status == SeriesStatus.Final && changed.Status != SeriesStatus.Canceled)
        {
            throw new FeedProtocolException("EEW update conflicts with a terminal series.");
        }
    }

    private static void RequireCumulativeWarnings(WarningData previous, WarningData incoming)
    {
        var active = incoming.ActiveRegions.ToDictionary(region => region.Key);
        var additions = incoming.AddedRegions.Select(region => region.Key).ToHashSet();
        foreach (var region in previous.ActiveRegions)
        {
            if (!active.TryGetValue(region.Key, out var updated) || region.SourceRef is not null &&
                (updated.SourceRef != region.SourceRef || updated.SpeechUntil != region.SpeechUntil || additions.Contains(region.Key)))
            {
                throw new FeedProtocolException("EEW warning update conflicts with cumulative region state.");
            }
        }
    }

    private static bool SeriesEqual(FeedSeries? first, FeedSeries? second)
    {
        if (first is null || second is null)
        {
            return first is null && second is null;
        }
        return first.Revision == second.Revision && first.Status == second.Status && first.Source == second.Source &&
               first.SpeechUntil == second.SpeechUntil && ForecastEqual(first.ForecastData, second.ForecastData) &&
               WarningsEqual(first.WarningData, second.WarningData);
    }

    private static bool ForecastEqual(ForecastData? first, ForecastData? second)
    {
        if (first is null || second is null)
        {
            return first is null && second is null;
        }
        return first.Hypocenter == second.Hypocenter && first.Magnitude == second.Magnitude && first.DepthKm == second.DepthKm &&
            first.MaxIntensity == second.MaxIntensity && first.MaxLongPeriodClass == second.MaxLongPeriodClass &&
            first.RegionForecasts.ToHashSet().SetEquals(second.RegionForecasts);
    }

    private static bool WarningsEqual(WarningData? first, WarningData? second)
    {
        if (first is null || second is null)
        {
            return first is null && second is null;
        }
        return first.Coverage == second.Coverage && first.ReportRegions.ToHashSet().SetEquals(second.ReportRegions) &&
            first.AddedRegions.ToHashSet().SetEquals(second.AddedRegions) &&
            first.ActiveRegions.ToHashSet().SetEquals(second.ActiveRegions) &&
            (first.RegionSources ?? ImmutableDictionary<string, FeedSource>.Empty).ToHashSet()
                .SetEquals(second.RegionSources ?? ImmutableDictionary<string, FeedSource>.Empty);
    }

    private void ValidateEnvironment(FeedEvent feedEvent)
    {
        if (feedEvent.Environment != environment)
        {
            throw new FeedProtocolException("EEW event does not match the subscribed environment.");
        }
    }

    private static bool Matches(FeedCursor cursor, string stream, string epoch) => cursor.StreamId == stream && cursor.Epoch == epoch;

    private static void RequireNextSequence(ulong last, ulong next)
    {
        if (next - last != 1)
        {
            throw new FeedProtocolException("EEW delivery sequence has a gap.");
        }
    }
}
