using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Helpers;
using Jellyfin.Plugin.JellyPlay.Realtime;
using Jellyfin.Plugin.JellyPlay.Services.Admin;
using Jellyfin.Plugin.JellyPlay.Services.Devices;
using Jellyfin.Plugin.JellyPlay.Services.Events;
using Microsoft.AspNetCore.Authorization;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyPlay.Api;

/// <summary>Device registry, admin broadcasts and the live events SSE stream.</summary>
[ApiController]
[Authorize]
[Route(JellyPlayContract.RoutePrefix)]
public class EventsController : JellyPlayControllerBase
{
    /// <summary>Camel-case-insensitive binding for the raw push element (matches ASP.NET's body binding).</summary>
    internal static class DeviceJson
    {
        public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    }

    private readonly SseHub _hub;
    private readonly EventService _events;
    private readonly DeviceRegistryService _devices;

    public EventsController(
        SseHub hub,
        EventService events,
        DeviceRegistryService devices)
    {
        _hub = hub;
        _events = events;
        _devices = devices;
    }

    [HttpPost("devices")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult RegisterDevice([FromBody, Required] DeviceRegistrationRequest request)
    {
        var outcome = _devices.Register(new DeviceRegistration(
            User.GetUserId().ToString(),
            string.IsNullOrEmpty(request.DeviceId) ? User.GetDeviceId() : request.DeviceId,
            request.Name,
            request.Platform,
            request.AppVersion,
            ParsePushDirective(request.Push),
            request.Model,
            request.Caps));
        return outcome switch
        {
            RegisterDeviceOutcome.Registered => NoContent(),
            RegisterDeviceOutcome.DeviceIdRequired => JellyPlayResponses.Error(StatusCodes.Status400BadRequest, "deviceId-required"),
            RegisterDeviceOutcome.InvalidPushRegistration => JellyPlayResponses.Error(StatusCodes.Status400BadRequest, "invalid-push-registration"),
            RegisterDeviceOutcome.PushKindUnavailable => JellyPlayResponses.Error(StatusCodes.Status400BadRequest, "push-kind-unavailable"),
            RegisterDeviceOutcome.DeviceRevoked => JellyPlayResponses.Error(StatusCodes.Status400BadRequest, "device-revoked"),
            _ => JellyPlayResponses.Error(StatusCodes.Status400BadRequest, "invalid-push-registration")
        };
    }

    /// <summary>Registry v7 rename: the owner updates a device's display name (and/or model); unknown fields keep their value.</summary>
    [HttpPost("devices/{deviceId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult RenameDevice([FromRoute, Required] string deviceId, [FromBody, Required] DeviceRenameRequest request)
        => _devices.Rename(User.GetUserId().ToString(), deviceId, request.Name, request.Model)
            ? NoContent()
            : NotFound();

    /// <summary>
    /// Registry v7: DELETE is CAPS-GATED. A device that registered caps (the
    /// app always sends at least "silent-push") is REVOKED — the row survives
    /// (flagged, excluded from push, writes rejected) and every settings row
    /// the device wrote is tombstone-wiped so its keys do not resurrect on
    /// other devices. A device that never registered caps (a legacy pre-v7
    /// client) gets the OLD semantics: a plain unregister — the row (and its
    /// push registration) is removed, so the routine push-detach /
    /// re-register cycle with the same deviceId keeps working.
    /// </summary>
    [HttpDelete("devices/{deviceId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult RevokeDevice([FromRoute, Required] string deviceId)
        => _devices.RevokeAndWipe(User.GetUserId().ToString(), deviceId) == DeleteDeviceOutcome.NotFound
            ? NotFound()
            : NoContent();

    /// <summary>The caller's own devices; push blocks are included only here (never for another user).</summary>
    [HttpGet("devices")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetDevices()
        => JellyPlayResponses.Camel(_devices.ListForUser(User.GetUserId().ToString()));

    /// <summary>Admin broadcast to every connected client.</summary>
    [HttpPost("broadcast")]
    [Authorize(Policy = Policies.RequiresElevation)]
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
    /// </summary>
    [HttpGet("events/stream")]
    public async Task StreamEvents(CancellationToken cancellationToken)
    {
        IReadOnlyList<SseEvent>? replay = null;
        if (Request.Headers.TryGetValue("Last-Event-ID", out var lastEventId)
            && ulong.TryParse(lastEventId.ToString(), out var after))
        {
            replay = _hub.ReplayEvents(User.GetUserId().ToString(), after);
        }

        var subscriberId = _hub.Subscribe(User.GetUserId().ToString(), SseHub.EventsStream);
        await SseStreamWriter.WriteAsync(HttpContext, _hub, subscriberId, cancellationToken, replay);
    }

    /// <summary>
    /// Push wire shapes (see DeviceRegistrationRequest.Push): object = validate
    /// + overwrite (idempotent re-registration when the distributor rotates
    /// endpoints); absent = preserve; explicit JSON null = detach (clear the
    /// registration, keep the device row). Binding the tri-state is transport
    /// concern — the registry module normalizes it to a PushDirective.
    /// </summary>
    private static PushDirective? ParsePushDirective(JsonElement? push)
        => push switch
        {
            null => null,
            { ValueKind: JsonValueKind.Object } element
                => element.Deserialize<DevicePushRegistration>(DeviceJson.Options) is { } parsed
                    ? new PushDirective.Attach(parsed.Kind, parsed.Endpoint)
                    : new PushDirective.Attach(string.Empty, string.Empty),
            _ => new PushDirective.Detach()
        };
}
