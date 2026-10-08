using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Helpers;
using Jellyfin.Plugin.JellyPlay.Realtime;
using Jellyfin.Plugin.JellyPlay.Services.Admin;
using Jellyfin.Plugin.JellyPlay.Services.Events;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyPlay.Api;

/// <summary>Admin broadcasts and the live events SSE stream (the device registry lives in <see cref="DevicesController"/>).</summary>
[ApiController]
[Authorize]
[Route(JellyPlayContract.RoutePrefix)]
public class EventsController : JellyPlayControllerBase
{
    private readonly SseHub _hub;
    private readonly EventService _events;

    public EventsController(
        SseHub hub,
        EventService events)
    {
        _hub = hub;
        _events = events;
    }

    /// <summary>Admin broadcast to every connected client.</summary>
    [HttpPost("broadcast")]
    [Authorize(Policy = MediaBrowser.Common.Api.Policies.RequiresElevation)]
    [RateLimit(typeof(Services.Admin.BroadcastRateLimiter), "broadcast")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public IActionResult Broadcast([FromBody, Required] BroadcastRequest request)
    {
        var delivered = _events.PublishBroadcast(request.Title, request.Body, request.Url);
        return JellyPlayResponses.Accepted(new { delivered });
    }

    /// <summary>
    /// Live events stream: new-media, broadcast, session-started, playback-started, user-locked-out.
    /// Reconnects presenting `Last-Event-ID` get the events the per-user
    /// replay ring still holds replayed first (best-effort catch-up); what
    /// the ring no longer holds must be reconciled via the inbox.
    /// The subscription is taken BEFORE the ring is read: an event published
    /// in between lands in both (ring + live channel) and is delivered twice —
    /// harmless, clients dedup by id — where the reverse order could drop it.
    /// </summary>
    [HttpGet("events/stream")]
    public async Task StreamEvents(CancellationToken cancellationToken)
    {
        var subscriberId = _hub.Subscribe(User.GetUserId().ToString(), SseHub.EventsStream);

        IReadOnlyList<SseEvent>? replay = null;
        if (Request.Headers.TryGetValue("Last-Event-ID", out var lastEventId)
            && ulong.TryParse(lastEventId.ToString(), out var after))
        {
            replay = _hub.ReplayEvents(User.GetUserId().ToString(), after);
        }

        await SseStreamWriter.WriteAsync(HttpContext, _hub, subscriberId, cancellationToken, replay: replay);
    }
}
