using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyPlay.Realtime;

/// <summary>
/// Thread-safe per-user SSE fan-out hub. Named streams ("settings", "events")
/// carry named events; delivery is per subscriber channel so slow consumers only
/// drop their own events, never another subscriber's.
/// </summary>
public sealed class SseHub
{
    private sealed class Subscriber
    {
        public required string UserId { get; init; }
        public required string Stream { get; init; }
        public required Channel<SseEvent> Channel { get; init; }
    }

    private readonly ConcurrentDictionary<Guid, Subscriber> _subscribers = new();
    private readonly ILogger<SseHub> _logger;
    private ulong _sequence;
    private readonly object _sequenceLock = new();

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

    /// <summary>Publish to every subscriber of the stream regardless of user.</summary>
    public int PublishAll(string stream, string eventName, string jsonData)
        => PublishWhere(stream, static _ => true, eventName, jsonData);

    /// <summary>Publish to every subscriber of the stream belonging to one user.</summary>
    public int PublishToUser(string stream, string userId, string eventName, string jsonData)
        => PublishWhere(stream, sub => string.Equals(sub.UserId, userId, StringComparison.Ordinal), eventName, jsonData);

    /// <summary>Publish to a set of users.</summary>
    public int PublishToUsers(string stream, IReadOnlySet<string> userIds, string eventName, string jsonData)
        => PublishWhere(stream, sub => userIds.Contains(sub.UserId), eventName, jsonData);

    private int PublishWhere(string stream, Func<Subscriber, bool> predicate, string eventName, string jsonData)
    {
        ulong seq;
        lock (_sequenceLock)
        {
            seq = ++_sequence;
        }

        var evt = new SseEvent(eventName, jsonData, seq);
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
}
