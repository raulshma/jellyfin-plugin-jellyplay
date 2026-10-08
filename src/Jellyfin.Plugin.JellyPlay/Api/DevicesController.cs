using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Jellyfin.Plugin.JellyPlay.Helpers;
using Jellyfin.Plugin.JellyPlay.Services.Devices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyPlay.Api;

/// <summary>
/// The device registry routes (carved out of EventsController — identical
/// route attributes/paths/verbs; the events stream and admin broadcast remain
/// in <see cref="EventsController"/>).
/// </summary>
[ApiController]
[Authorize]
[Route(JellyPlayContract.RoutePrefix)]
public class DevicesController : JellyPlayControllerBase
{
    /// <summary>Camel-case-insensitive binding for the raw push element (matches ASP.NET's body binding).</summary>
    internal static class DeviceJson
    {
        public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    }

    private readonly DeviceRegistryService _devices;

    public DevicesController(DeviceRegistryService devices)
    {
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
