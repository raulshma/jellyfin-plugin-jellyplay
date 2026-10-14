using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyPlay.Realtime;

/// <summary>
/// Thread-safe per-user SSE fan-out hub. Named streams ("settings", "events")
/// carry named events; delivery is per subscriber channel so slow consumers only
/// drop their own events, never another subscriber's.
///
/// Event ids: settings-stream events carry an EXPLICIT anchor — the user's
/// change-log head at publish time — so a reconnecting client can resume the
/// delta pull from that cursor via `Last-Event-ID`. Other streams use the
/// hub's monotonic sequence. The events stream additionally records the last
/// <see cref="ReplayRingCapacity"/> events per user (in-memory, best-effort)
/// so a reconnecting subscriber that presents `Last-Event-ID` gets the missed
/// events replayed before the live feed resumes.
/// </summary>
public sealed class SseHub
{
    /// <summary>The stream the replay ring serves (the settings stream resumes via its change-log cursor instead).</summary>
    public const string EventsStream = "events";

    /// <summary>
    /// The per-user settings-sync stream: carries the anchored
    /// <c>settings.changed</c> / <c>settings.reset</c> events — the SSE id IS
    /// the user's change-log head at publish time, so a reconnecting client
    /// resumes the delta pull from that cursor.
    /// </summary>
    public const string SettingsStream = "settings";

    /// <summary>
    /// The admin live-monitor stream (RequiresElevation subscribers only — the
    /// elevation gate lives on the subscribing endpoint, the hub is
    /// policy-agnostic). Carries one <c>sync.op</c> event per recorded
    /// settings-sync operation; fan-out is broadcast, ids ride the hub's
    /// monotonic sequence (no resume anchor — the monitor is a live view, the
    /// history endpoints are the durable record).
    /// </summary>
    public const string AdminStream = "admin";

    /// <summary>Per-user in-memory ring size for events-stream replay.</summary>
    public const int ReplayRingCapacity = 256;

    private sealed class Subscriber
    {
        public required string UserId { get; init; }
        public required string Stream { get; init; }
        public required Channel<SseEvent> Channel { get; init; }
    }

    private readonly ConcurrentDictionary<Guid, Subscriber> _subscribers = new();
    private readonly ILogger<SseHub> _logger;

    // Fan-out index: (stream, user) -> subscriber ids. PublishToUser and
    // targeted publishes resolve only their bucket instead of scanning every
    // subscriber; broadcast keeps the full scan. Maintained alongside
    // _subscribers on subscribe/unsubscribe.
    private readonly ConcurrentDictionary<(string Stream, string UserId), ConcurrentDictionary<Guid, byte>> _index = new();

    // Monotonic counter, lock-free: a plain increment under Interlocked is the
    // only discipline it needs (no other state is read alongside it).
    private long _sequence;

    // Per-user replay rings for the events stream. One global lock (the same
    // discipline as the sequence lock): ring operations are quick queue moves
    // and publish fan-out is the only writer. Rings exist only for users that
    // received at least one recorded event — a bounded set (the server's user
    // count), each ring bounded by ReplayRingCapacity.
    private readonly ConcurrentDictionary<string, Queue<SseEvent>> _eventsReplay = new();

    // Per-user replay locks (striped): targeted publishes lock only their own
    // users' rings instead of serializing every events publish on one gate.
    // Broadcast holds the creation gate (not the per-user locks) while
    // snapshotting, and ring creation takes the same gate — so a ring created
    // concurrently is either fully in the broadcast or legitimately post-dates
    // it. Steady-state targeted publishes (ring exists) never touch the gate.
    // Bounded by the user count, like the rings themselves.
    private readonly ConcurrentDictionary<string, object> _replayLocks = new(StringComparer.Ordinal);

    /// <summary>Serializes replay-ring creation against broadcast snapshots (steady-state reads/appends stay per-user).</summary>
    private readonly object _replayCreateLock = new();

    private object ReplayLockFor(string userId) => _replayLocks.GetOrAdd(userId, _ => new object());

    public SseHub(ILogger<SseHub> logger)
    {
        _logger = logger;
    }

    public int SubscriberCount => _subscribers.Count;

    public Guid Subscribe(string userId, string stream)
    {
        var id = Guid.NewGuid();
        _subscribers[id] = new Subscriber
        {
            UserId = userId,
            Stream = stream,
            Channel = Channel.CreateBounded<SseEvent>(new BoundedChannelOptions(256)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            })
        };
        _index.GetOrAdd((stream, userId), _ => new ConcurrentDictionary<Guid, byte>())[id] = 0;
        return id;
    }

    public void Unsubscribe(Guid subscriberId)
    {
        if (_subscribers.TryRemove(subscriberId, out var subscriber))
        {
            subscriber.Channel.Writer.TryComplete();
            if (_index.TryGetValue((subscriber.Stream, subscriber.UserId), out var bucket))
            {
                bucket.TryRemove(subscriberId, out _);
                if (bucket.IsEmpty)
                {
                    _index.TryRemove((subscriber.Stream, subscriber.UserId), out _);
                }
            }
        }
    }

    /// <summary>
    /// The ONE publish seam behind the fan-out: a null target set broadcasts
    /// to every subscriber of the stream; a set delivers to its members only
    /// (an empty set delivers to nobody). <paramref name="eventId"/>, when
    /// given, is the wire event id verbatim (the settings stream anchors it
    /// at the user's change-log head); otherwise the hub's monotonic sequence
    /// is used. The named heads below delegate here for call-site readability.
    /// </summary>
    public int Publish(string stream, IReadOnlyCollection<string>? targets, string eventName, string jsonData, ulong? eventId = null)
    {
        if (targets is null)
        {
            return PublishWhere(stream, static _ => true, eventName, jsonData, eventId);
        }

        // One set built per publish: O(1) membership per subscriber instead of
        // a Contains scan over the raw list; the replay pass reads it too.
        // Targeted delivery resolves only the matching index buckets — no full
        // subscriber scan.
        var targetSet = targets as HashSet<string> ?? new HashSet<string>(targets, StringComparer.Ordinal);
        if (targetSet.Count == 0)
        {
            var empty = new SseEvent(eventName, jsonData, eventId ?? (ulong)Interlocked.Increment(ref _sequence));
            if (string.Equals(stream, EventsStream, StringComparison.Ordinal))
            {
                RecordReplay(targetSet, empty);
            }

            return 0;
        }

        var evt = CreateRecordedEvent(stream, eventName, jsonData, eventId, targetSet);
        var delivered = 0;
        foreach (var userId in targetSet)
        {
            delivered += DeliverToBucket(stream, userId, evt, eventName);
        }

        return delivered;
    }

    /// <summary>
    /// Broadcast-target overload (additive): the same fan-out behind a
    /// <see cref="Services.Shared.Audience.BroadcastTargets"/> value — null
    /// broadcasts, empty delivers to nobody. Delegates to the nullable-set
    /// core so the public interface only grows.
    /// </summary>
    public int Publish(string stream, Services.Shared.Audience.BroadcastTargets targets, string eventName, string jsonData, ulong? eventId = null)
        => Publish(stream, targets.UserIds, eventName, jsonData, eventId);

    /// <summary>Publish to every subscriber of the stream regardless of user.</summary>
    public int PublishAll(string stream, string eventName, string jsonData)
        => Publish(stream, (IReadOnlyCollection<string>?)null, eventName, jsonData);

    /// <summary>
    /// Publish to every subscriber of the stream belonging to one user.
    /// <paramref name="eventId"/>, when given, is the wire event id verbatim
    /// (the settings stream anchors it at the user's change-log head);
    /// otherwise the hub's monotonic sequence is used.
    /// </summary>
    public int PublishToUser(string stream, string userId, string eventName, string jsonData, ulong? eventId = null)
    {
        // Index fast path: only this user's bucket is visited — no scan over
        // every subscriber. The replay pass needs the id enumerable, but only
        // on the events stream — this method's hot caller (settings) skips the
        // ring, so no per-call singleton array is built for it.
        IReadOnlyCollection<string>? replayTargets = string.Equals(stream, EventsStream, StringComparison.Ordinal)
            ? new[] { userId }
            : null;
        var evt = CreateRecordedEvent(stream, eventName, jsonData, eventId, replayTargets);
        return DeliverToBucket(stream, userId, evt, eventName);
    }

    /// <summary>Builds the wire event (anchored id or monotonic sequence) and records the events-stream replay ring.</summary>
    private SseEvent CreateRecordedEvent(string stream, string eventName, string jsonData, ulong? eventId, IReadOnlyCollection<string>? replayTargets)
    {
        var seq = eventId ?? (ulong)Interlocked.Increment(ref _sequence);
        var evt = new SseEvent(eventName, jsonData, seq);
        if (string.Equals(stream, EventsStream, StringComparison.Ordinal))
        {
            RecordReplay(replayTargets, evt);
        }

        return evt;
    }

    /// <summary>Delivers one event to every subscriber in the (stream, user) index bucket.</summary>
    private int DeliverToBucket(string stream, string userId, SseEvent evt, string eventName)
    {
        if (!_index.TryGetValue((stream, userId), out var bucket))
        {
            return 0;
        }

        var delivered = 0;
        foreach (var subscriberId in bucket.Keys)
        {
            if (!_subscribers.TryGetValue(subscriberId, out var subscriber))
            {
                continue;
            }

            if (subscriber.Channel.Writer.TryWrite(evt))
            {
                delivered++;
            }
            else
            {
                _logger.LogWarning("SSE subscriber {SubscriberId} channel unavailable; dropping event {Event}", subscriber.GetHashCode(), eventName);
            }
        }

        return delivered;
    }

    private int PublishWhere(
        string stream,
        Func<Subscriber, bool> predicate,
        string eventName,
        string jsonData,
        ulong? eventId = null,
        IReadOnlyCollection<string>? replayTargets = null)
    {
        ulong seq;
        if (eventId is { } anchored)
        {
            seq = anchored;
        }
        else
        {
            seq = (ulong)Interlocked.Increment(ref _sequence);
        }

        var evt = new SseEvent(eventName, jsonData, seq);
        if (string.Equals(stream, EventsStream, StringComparison.Ordinal))
        {
            RecordReplay(replayTargets, evt);
        }

        var delivered = 0;
        foreach (var subscriber in _subscribers.Values)
        {
            if (!string.Equals(subscriber.Stream, stream, StringComparison.Ordinal) || !predicate(subscriber))
            {
                continue;
            }

            if (subscriber.Channel.Writer.TryWrite(evt))
            {
                delivered++;
            }
            else
            {
                _logger.LogWarning("SSE subscriber {SubscriberId} channel unavailable; dropping event {Event}", subscriber.GetHashCode(), eventName);
            }
        }

        return delivered;
    }

    /// <summary>
    /// The events missed by a reconnecting subscriber: the user's recorded
    /// events with an id greater than <paramref name="afterId"/> (the
    /// `Last-Event-ID` the client presented), oldest-first. Best-effort —
    /// events older than the ring window (or lost to a restart) are simply
    /// absent; clients reconcile via the inbox in that case.
    /// </summary>
    public IReadOnlyList<SseEvent> ReplayEvents(string userId, ulong afterId)
    {
        if (!_eventsReplay.TryGetValue(userId, out var ring))
        {
            return Array.Empty<SseEvent>();
        }

        lock (ReplayLockFor(userId))
        {
            return ring.Where(evt => evt.Id > afterId).ToList();
        }
    }

    /// <summary>Records one published events-stream event into the target users' rings (every existing ring when targets is null = a broadcast).</summary>
    private void RecordReplay(IReadOnlyCollection<string>? replayTargets, SseEvent evt)
    {
        if (replayTargets is null)
        {
            // Broadcast: the creation gate makes the snapshot atomic against
            // concurrent ring creation; per-ring appends then proceed under
            // their own locks (never nested inside the gate's reverse order).
            string[] users;
            lock (_replayCreateLock)
            {
                users = _eventsReplay.Keys.ToArray();
            }

            foreach (var userId in users)
            {
                if (_eventsReplay.TryGetValue(userId, out var ring))
                {
                    lock (ReplayLockFor(userId))
                    {
                        AppendReplay(ring, evt);
                    }
                }
            }
        }
        else
        {
            foreach (var userId in replayTargets)
            {
                var ring = GetOrCreateRing(userId);
                lock (ReplayLockFor(userId))
                {
                    AppendReplay(ring, evt);
                }
            }
        }
    }

    /// <summary>Fetches the user's replay ring, creating it under the creation gate so broadcast snapshots stay atomic.</summary>
    private Queue<SseEvent> GetOrCreateRing(string userId)
    {
        if (_eventsReplay.TryGetValue(userId, out var ring))
        {
            return ring;
        }

        lock (_replayCreateLock)
        {
            return _eventsReplay.GetOrAdd(userId, _ => new Queue<SseEvent>());
        }
    }

    private static void AppendReplay(Queue<SseEvent> ring, SseEvent evt)
    {
        ring.Enqueue(evt);
        while (ring.Count > ReplayRingCapacity)
        {
            ring.Dequeue();
        }
    }

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for the subscriber's next event.
    /// Returns null when the window elapses (writer should emit a keepalive
    /// frame) or when the subscriber is gone. Producer-side drop semantics of
    /// the bounded channel are untouched — this only reads.
    /// </summary>
    public async Task<SseEvent?> WaitForEventAsync(Guid subscriberId, TimeSpan timeout, System.Threading.CancellationToken cancellationToken)
    {
        if (!_subscribers.TryGetValue(subscriberId, out var subscriber))
        {
            return null;
        }

        using var linked = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        try
        {
            if (await subscriber.Channel.Reader.WaitToReadAsync(linked.Token).ConfigureAwait(false)
                && subscriber.Channel.Reader.TryRead(out var evt))
            {
                return evt;
            }

            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Keepalive window elapsed; the outer cancellation token is still live.
            return null;
        }
    }

    public bool IsSubscribed(Guid subscriberId) => _subscribers.ContainsKey(subscriberId);

    /// <summary>
    /// Drain variant of <see cref="WaitForEventAsync(Guid, TimeSpan, System.Threading.CancellationToken)"/>
    /// for the streaming writer: waits for at least one event, then reads
    /// EVERY event buffered at that moment, so a publish burst leaves the hub
    /// as one batch the writer can write and flush once. Returns null on the
    /// keepalive window elapsing or the subscriber being gone (the same
    /// contract as the single-event seam).
    /// </summary>
    public async Task<IReadOnlyList<SseEvent>?> WaitForEventsAsync(Guid subscriberId, TimeSpan timeout, System.Threading.CancellationToken cancellationToken)
    {
        if (!_subscribers.TryGetValue(subscriberId, out var subscriber))
        {
            return null;
        }

        using var linked = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        try
        {
            if (!await subscriber.Channel.Reader.WaitToReadAsync(linked.Token).ConfigureAwait(false)
                || !subscriber.Channel.Reader.TryRead(out var first))
            {
                return null;
            }

            var events = new List<SseEvent> { first };
            while (subscriber.Channel.Reader.TryRead(out var more))
            {
                events.Add(more);
            }

            return events;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Keepalive window elapsed; the outer cancellation token is still live.
            return null;
        }
    }
}
