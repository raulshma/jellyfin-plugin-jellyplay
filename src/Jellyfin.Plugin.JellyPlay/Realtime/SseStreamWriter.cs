using System;
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

    public static Task WriteAsync(HttpContext context, SseHub hub, Guid subscriberId, CancellationToken requestAborted)
        => WriteAsync(context, hub, subscriberId, DefaultKeepAliveInterval, requestAborted);

    public static async Task WriteAsync(HttpContext context, SseHub hub, Guid subscriberId, TimeSpan keepAliveInterval, CancellationToken requestAborted)
    {
        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        try
        {
            while (!requestAborted.IsCancellationRequested)
            {
                var evt = await hub.WaitForEventAsync(subscriberId, keepAliveInterval, requestAborted).ConfigureAwait(false);
                if (evt is not null)
                {
                    var builder = new StringBuilder(256);
                    builder.Append("id: ").Append(evt.Id).Append('\n');
                    builder.Append("event: ").Append(evt.EventName).Append('\n');
                    builder.Append("retry: ").Append(evt.RetrySeconds).Append('\n');
                    builder.Append("data: ").Append(evt.Data).Append("\n\n");
                    await context.Response.WriteAsync(builder.ToString(), requestAborted).ConfigureAwait(false);
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
