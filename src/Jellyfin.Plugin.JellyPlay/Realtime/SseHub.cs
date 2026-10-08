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

    // Monotonic counter, lock-free: a plain increment under Interlocked is the
    // only discipline it needs (no other state is read alongside it).
    private long _sequence;

    // Per-user replay rings for the events stream. One global lock (the same
    // discipline as the sequence lock): ring operations are quick queue moves
    // and publish fan-out is the only writer. Rings exist only for users that
    // received at least one recorded event — a bounded set (the server's user
    // count), each ring bounded by ReplayRingCapacity.
    private readonly ConcurrentDictionary<string, Queue<SseEvent>> _eventsReplay = new();
    private readonly object _replayLock = new();

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
        return id;
    }

    public void Unsubscribe(Guid subscriberId)
    {
        if (_subscribers.TryRemove(subscriberId, out var subscriber))
        {
            subscriber.Channel.Writer.TryComplete();
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
        var targetSet = targets as HashSet<string> ?? new HashSet<string>(targets, StringComparer.Ordinal);
        return PublishWhere(stream, subscriber => targetSet.Contains(subscriber.UserId), eventName, jsonData, eventId, targetSet);
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
        // The replay pass needs the id enumerable, but only on the events
        // stream — this method's hot caller (settings) skips the ring, so no
        // per-call singleton array is built for it.
        IReadOnlyCollection<string>? replayTargets = string.Equals(stream, EventsStream, StringComparison.Ordinal)
            ? new[] { userId }
            : null;
        return PublishWhere(stream, subscriber => string.Equals(subscriber.UserId, userId, StringComparison.Ordinal), eventName, jsonData, eventId, replayTargets);
    }

    /// <summary>Publish to a set of users.</summary>
    public int PublishToUsers(string stream, IReadOnlyCollection<string> userIds, string eventName, string jsonData)
        => Publish(stream, userIds, eventName, jsonData);

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

        lock (_replayLock)
        {
            return ring.Where(evt => evt.Id > afterId).ToList();
        }
    }

    /// <summary>Records one published events-stream event into the target users' rings (every existing ring when targets is null = a broadcast).</summary>
    private void RecordReplay(IReadOnlyCollection<string>? replayTargets, SseEvent evt)
    {
        lock (_replayLock)
        {
            if (replayTargets is null)
            {
                foreach (var ring in _eventsReplay.Values)
                {
                    AppendReplay(ring, evt);
                }
            }
            else
            {
                foreach (var userId in replayTargets)
                {
                    AppendReplay(_eventsReplay.GetOrAdd(userId, _ => new Queue<SseEvent>()), evt);
                }
            }
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
