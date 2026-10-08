using System;
using System.Collections.Generic;
using System.Text;
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
                    await context.Response.WriteAsync(FormatFrame(missed), requestAborted).ConfigureAwait(false);
                }

                await context.Response.Body.FlushAsync(requestAborted).ConfigureAwait(false);
            }

            while (!requestAborted.IsCancellationRequested)
            {
                var evt = await hub.WaitForEventAsync(subscriberId, keepAlive, requestAborted).ConfigureAwait(false);
                if (evt is not null)
                {
                    await context.Response.WriteAsync(FormatFrame(evt), requestAborted).ConfigureAwait(false);
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

    private static string FormatFrame(SseEvent evt)
    {
        var builder = new StringBuilder(256);
        builder.Append("id: ").Append(evt.Id).Append('\n');
        builder.Append("event: ").Append(evt.EventName).Append('\n');
        builder.Append("retry: ").Append(evt.RetrySeconds).Append('\n');
        // SSE carries one data: line per payload line — a raw
        // newline inside the payload would terminate the frame
        // early and corrupt the stream.
        foreach (var line in evt.Data.Replace("\r\n", "\n").Split('\n'))
        {
            builder.Append("data: ").Append(line).Append('\n');
        }

        builder.Append('\n');
        return builder.ToString();
    }
}
