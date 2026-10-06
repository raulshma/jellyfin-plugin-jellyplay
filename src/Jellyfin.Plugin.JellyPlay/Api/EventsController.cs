using System;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Api;
using Jellyfin.Plugin.JellyPlay.Configuration;
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
    private readonly Services.Admin.BroadcastRateLimiter _rateLimiter;
    private readonly Func<PushConfig>? _pushConfig;
    private readonly TimeProvider _clock;

    /// <summary>Camel-case-insensitive binding for the raw push element (matches ASP.NET's body binding).</summary>
    internal static class DeviceJson
    {
        public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    }

    public EventsController(
        SseHub hub,
        EventService events,
        JellyPlayDatabase db,
        Services.Admin.BroadcastRateLimiter rateLimiter,
        Func<PushConfig>? pushConfig = null)
    {
        _hub = hub;
        _events = events;
        _db = db;
        _rateLimiter = rateLimiter;
        _pushConfig = pushConfig;
        _clock = TimeProvider.System;
    }

    [HttpPost("devices")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult RegisterDevice([FromBody, Required] DeviceRegistrationRequest request)
    {
        var deviceId = string.IsNullOrEmpty(request.DeviceId) ? User.GetDeviceId() : request.DeviceId;
        if (string.IsNullOrEmpty(deviceId))
        {
            return BadRequest(new { error = "deviceId-required" });
        }

        // Push wire shapes (see DeviceRegistrationRequest.Push): object =
        // validate + overwrite (idempotent re-registration when the distributor
        // rotates endpoints); absent = preserve; explicit JSON null = detach
        // (clear the registration, keep the device row). Endpoint URLs are
        // secrets and only ever round-trip to their owner.
        string? pushKind = null;
        string? pushEndpoint = null;
        if (request.Push is { ValueKind: JsonValueKind.Object } pushElement)
        {
            var push = pushElement.Deserialize<DevicePushRegistration>(DeviceJson.Options);
            if (push is null
                || !Services.Push.PushDispatcher.IsValidRegistration(push.Kind, push.Endpoint))
            {
                return BadRequest(new { error = "invalid-push-registration" });
            }

            // fcm needs configured FCM credentials (project id + service-account key).
            if (Services.Push.PushDispatcher.IsFcmKind(push.Kind)
                && (_pushConfig is null || !_pushConfig().FcmConfigured()))
            {
                return BadRequest(new { error = "push-kind-unavailable" });
            }

            pushKind = push.Kind;
            pushEndpoint = push.Endpoint.Trim();
        }

        var existing = _db.GetDeviceById(deviceId);
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        _db.UpsertDevice(new DeviceRow(
            deviceId,
            User.GetUserId().ToString(),
            request.Name,
            request.Platform,
            request.AppVersion,
            now,
            request.Push.HasValue ? pushKind : existing?.PushKind,
            request.Push.HasValue ? pushEndpoint : existing?.PushEndpoint,
            existing?.CreatedAt ?? now));
        return NoContent();
    }

    [HttpDelete("devices/{deviceId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult UnregisterDevice([FromRoute, Required] string deviceId)
        => _db.DeleteDevice(User.GetUserId().ToString(), deviceId) ? NoContent() : NotFound();

    /// <summary>The caller's own devices; push blocks are included only here (never for another user).</summary>
    [HttpGet("devices")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetDevices()
        => JellyPlayResponses.Camel(_db.GetDevices(User.GetUserId().ToString())
            .Select(row => new DeviceDto(
                row.DeviceId,
                row.UserId,
                row.Name,
                row.Platform,
                row.AppVersion,
                row.LastSeen,
                row.PushKind is null || row.PushEndpoint is null
                    ? null
                    : new DevicePushDto(row.PushKind, row.PushEndpoint)))
            .ToList());

    /// <summary>Admin broadcast to every connected client.</summary>
    [HttpPost("broadcast")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public IActionResult Broadcast([FromBody, Required] BroadcastRequest request)
    {
        if (!_rateLimiter.Allow("broadcast:" + User.GetUserId(), _clock.GetUtcNow().ToUnixTimeMilliseconds()))
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = "rate-limited" });
        }

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
