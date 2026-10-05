using System;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Api;
using Jellyfin.Plugin.JellyPlay.Helpers;
using Jellyfin.Plugin.JellyPlay.Realtime;
using Jellyfin.Plugin.JellyPlay.Services.Events;
using Jellyfin.Plugin.JellyPlay.Storage;
using Jellyfin.Plugin.JellyPlay.Storage.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MediaBrowser.Common.Api;

namespace Jellyfin.Plugin.JellyPlay.Api;

/// <summary>Device registry, admin broadcasts and the live events SSE stream.</summary>
[ApiController]
[Authorize]
[Route(JellyPlayContract.RoutePrefix)]
public class EventsController : ControllerBase
{
    private readonly SseHub _hub;
    private readonly EventService _events;
    private readonly JellyPlayDatabase _db;
    private readonly TimeProvider _clock;

    public EventsController(SseHub hub, EventService events, JellyPlayDatabase db)
    {
        _hub = hub;
        _events = events;
        _db = db;
        _clock = TimeProvider.System;
    }

    [HttpPost("devices")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult RegisterDevice([FromBody, Required] DeviceRegistrationRequest request)
    {
        var deviceId = string.IsNullOrEmpty(request.DeviceId) ? User.GetDeviceId() : request.DeviceId;
        if (string.IsNullOrEmpty(deviceId))
        {
            return BadRequest(new { error = "deviceId-required" });
        }

        _db.UpsertDevice(new DeviceRow(
            deviceId,
            User.GetUserId().ToString(),
            request.Name,
            request.Platform,
            request.AppVersion,
            _clock.GetUtcNow().ToUnixTimeMilliseconds()));
        return NoContent();
    }

    [HttpDelete("devices/{deviceId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult UnregisterDevice([FromRoute, Required] string deviceId)
        => _db.DeleteDevice(User.GetUserId().ToString(), deviceId) ? NoContent() : NotFound();

    [HttpGet("devices")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetDevices() => Ok(_db.GetDevices(User.GetUserId().ToString()));

    /// <summary>Admin broadcast to every connected client.</summary>
    [HttpPost("broadcast")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public IActionResult Broadcast([FromBody, Required] BroadcastRequest request)
    {
        var delivered = _events.PublishBroadcast(request.Title, request.Body, request.Url);
        return Accepted(new { delivered });
    }

    /// <summary>Live events stream: new-media, broadcast, session-started, playback-started, user-locked-out.</summary>
    [HttpGet("events/stream")]
    public async Task StreamEvents(CancellationToken cancellationToken)
    {
        var subscriberId = _hub.Subscribe(User.GetUserId().ToString(), "events");
        await SseStreamWriter.WriteAsync(HttpContext, _hub, subscriberId, cancellationToken);
    }
}
