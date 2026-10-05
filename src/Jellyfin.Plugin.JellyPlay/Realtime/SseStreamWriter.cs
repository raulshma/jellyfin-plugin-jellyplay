using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.JellyPlay.Realtime;

/// <summary>Bridges an SseHub subscription onto an ASP.NET response body.</summary>
public static class SseStreamWriter
{
    public static async Task WriteAsync(HttpContext context, SseHub hub, Guid subscriberId, CancellationToken requestAborted)
    {
        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        try
        {
            await foreach (var evt in hub.ReadAllAsync(subscriberId, requestAborted))
            {
                var builder = new StringBuilder(256);
                builder.Append("id: ").Append(evt.Id).Append('\n');
                builder.Append("event: ").Append(evt.EventName).Append('\n');
                builder.Append("retry: ").Append(evt.RetrySeconds).Append('\n');
                builder.Append("data: ").Append(evt.Data).Append("\n\n");
                await context.Response.WriteAsync(builder.ToString(), requestAborted);
                await context.Response.Body.FlushAsync(requestAborted);
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
