using System;
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
        var outcome = _devices.Register(
            User.GetUserId().ToString(),
            string.IsNullOrEmpty(request.DeviceId) ? User.GetDeviceId() : request.DeviceId,
            request.Name,
            request.Platform,
            request.AppVersion,
            ParsePushDirective(request.Push));
        return outcome switch
        {
            RegisterDeviceOutcome.Registered => NoContent(),
            RegisterDeviceOutcome.DeviceIdRequired => JellyPlayResponses.Error(StatusCodes.Status400BadRequest, "deviceId-required"),
            RegisterDeviceOutcome.InvalidPushRegistration => JellyPlayResponses.Error(StatusCodes.Status400BadRequest, "invalid-push-registration"),
            RegisterDeviceOutcome.PushKindUnavailable => JellyPlayResponses.Error(StatusCodes.Status400BadRequest, "push-kind-unavailable"),
            _ => JellyPlayResponses.Error(StatusCodes.Status400BadRequest, "invalid-push-registration")
        };
    }

    [HttpDelete("devices/{deviceId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult UnregisterDevice([FromRoute, Required] string deviceId)
        => _devices.Unregister(User.GetUserId().ToString(), deviceId) ? NoContent() : NotFound();

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

    /// <summary>Live events stream: new-media, broadcast, session-started, playback-started, user-locked-out.</summary>
    [HttpGet("events/stream")]
    public async Task StreamEvents(CancellationToken cancellationToken)
    {
        var subscriberId = _hub.Subscribe(User.GetUserId().ToString(), "events");
        await SseStreamWriter.WriteAsync(HttpContext, _hub, subscriberId, cancellationToken);
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
