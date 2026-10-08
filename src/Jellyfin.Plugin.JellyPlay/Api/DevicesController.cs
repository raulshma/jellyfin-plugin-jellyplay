using System.ComponentModel.DataAnnotations;
using Jellyfin.Plugin.JellyPlay.Helpers;
using Jellyfin.Plugin.JellyPlay.Services.Devices;
using Jellyfin.Plugin.JellyPlay.Services.Push;
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
    private readonly DeviceRegistryService _devices;

    public DevicesController(DeviceRegistryService devices)
    {
        _devices = devices;
    }

    /// <summary>
    /// Registers (or re-registers) the caller's device. Push-wire parsing
    /// lives behind the Push policy module's seam
    /// (<see cref="PushPolicy.ParseDirective"/>): object = validate +
    /// overwrite (idempotent re-registration when the distributor rotates
    /// endpoints); absent = preserve; explicit JSON null = detach (clear the
    /// registration, keep the device row).
    /// </summary>
    [HttpPost("devices")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult RegisterDevice([FromBody, Required] DeviceRegistrationRequest request)
    {
        var outcome = _devices.Register(new DeviceRegistration(
            User.GetUserIdString(),
            User.ResolveDeviceId(request.DeviceId),
            request.Name,
            request.Platform,
            request.AppVersion,
            PushPolicy.ParseDirective(request.Push),
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
        => _devices.Rename(User.GetUserIdString(), deviceId, request.Name, request.Model)
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
        => _devices.RevokeAndWipe(User.GetUserIdString(), deviceId) == DeleteDeviceOutcome.NotFound
            ? NotFound()
            : NoContent();

    /// <summary>The caller's own devices; push blocks are included only here (never for another user).</summary>
    [HttpGet("devices")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetDevices()
        => JellyPlayResponses.Camel(_devices.ListForUser(User.GetUserIdString()));
}
