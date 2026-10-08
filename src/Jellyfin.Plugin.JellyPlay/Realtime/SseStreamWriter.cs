using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.JellyPlay.Realtime;

/// <summary>
/// Bridges an SseHub subscription onto an ASP.NET response body. Emits an SSE
/// comment frame (<c>: keepalive</c>) after each quiet interval so idle proxies
/// (nginx default 60s) do not reap silent streams — comments are ignored by
/// EventSource but reset proxy idle timers.
/// </summary>
public static class SseStreamWriter
{
    public static readonly TimeSpan DefaultKeepAliveInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// SSE subscription seam: subscribes <paramref name="userId"/> to
    /// <paramref name="stream"/>, reads the replay window, then streams.
    /// <paramref name="keepAliveInterval"/> default falls back to <see cref="DefaultKeepAliveInterval"/>.
    /// </summary>
    public static async Task WriteSubscribedAsync(
        HttpContext context,
        SseHub hub,
        string userId,
        string stream,
        CancellationToken requestAborted,
        TimeSpan keepAliveInterval = default)
    {
        // SSE subscription seam: this module owns subscribe → replay-read →
        // stream so controllers leverage one entry point. The subscription is
        // taken BEFORE the replay ring is read: an event published in between
        // lands in both (ring + live channel) and is delivered twice —
        // harmless, clients dedup by id — where the reverse order could drop
        // it. Only the events stream carries a replay ring (Last-Event-ID);
        // other streams skip the replay read entirely.
        var subscriberId = hub.Subscribe(userId, stream);

        IReadOnlyList<SseEvent>? replay = null;
        if (string.Equals(stream, SseHub.EventsStream, StringComparison.Ordinal)
            && context.Request.Headers.TryGetValue("Last-Event-ID", out var lastEventId)
            && ulong.TryParse(lastEventId.ToString(), out var after))
        {
            replay = hub.ReplayEvents(userId, after);
        }

        await WriteAsync(context, hub, subscriberId, requestAborted, keepAliveInterval, replay).ConfigureAwait(false);
    }

    /// <summary>
    /// Streams the subscription, replaying <paramref name="replay"/> (reconnect catch-up) before the live feed.
    /// <paramref name="keepAliveInterval"/> default falls back to <see cref="DefaultKeepAliveInterval"/>.
    /// </summary>
    public static async Task WriteAsync(
        HttpContext context,
        SseHub hub,
        Guid subscriberId,
        CancellationToken requestAborted,
        TimeSpan keepAliveInterval = default,
        IReadOnlyList<SseEvent>? replay = null)
    {
        var keepAlive = keepAliveInterval == default ? DefaultKeepAliveInterval : keepAliveInterval;
        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        try
        {
            // An ASP.NET response only reaches the client on first flush —
            // without this frame a fresh subscriber's 200 sits invisible for
            // up to a keepalive interval (EventSource/fetch readers stall).
            await context.Response.WriteAsync(": connected\n\n", requestAborted).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(requestAborted).ConfigureAwait(false);

            // Reconnect catch-up: the missed events (from the hub's per-user
            // replay ring) go out first, in id order, before the live loop.
            if (replay is { Count: > 0 })
            {
                foreach (var missed in replay)
                {
                    await context.Response.WriteAsync(missed.Frame, requestAborted).ConfigureAwait(false);
                }

                await context.Response.Body.FlushAsync(requestAborted).ConfigureAwait(false);
            }

            while (!requestAborted.IsCancellationRequested)
            {
                var events = await hub.WaitForEventsAsync(subscriberId, keepAlive, requestAborted).ConfigureAwait(false);
                if (events is { Count: > 0 })
                {
                    // One drain = one write pass + ONE flush: a burst of N
                    // buffered events leaves as a single batch, not N flushes
                    // (each event's frame was already built once at publish).
                    foreach (var evt in events)
                    {
                        await context.Response.WriteAsync(evt.Frame, requestAborted).ConfigureAwait(false);
                    }

                    await context.Response.Body.FlushAsync(requestAborted).ConfigureAwait(false);
                }
                else if (!hub.IsSubscribed(subscriberId))
                {
                    // Subscription removed — nothing left to stream.
                    break;
                }
                else
                {
                    // Quiet interval elapsed: comment frame, invisible to EventSource.
                    await context.Response.WriteAsync(": keepalive\n\n", requestAborted).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(requestAborted).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnect — normal SSE lifecycle.
        }
        finally
        {
            hub.Unsubscribe(subscriberId);
        }
    }
}
