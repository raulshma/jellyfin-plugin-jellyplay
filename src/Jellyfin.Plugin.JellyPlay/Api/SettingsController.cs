using System;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Helpers;
using Jellyfin.Plugin.JellyPlay.Realtime;
using Jellyfin.Plugin.JellyPlay.Services.Settings;
using Jellyfin.Plugin.JellyPlay.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyPlay.Api;

/// <summary>Per-user settings sync API (opaque blobs, per-key LWW, SSE live stream).</summary>
[ApiController]
[Authorize]
[Route(JellyPlayContract.RoutePrefix + "/settings")]
public class SettingsController : ControllerBase
{
    private readonly SettingsService _settings;
    private readonly SseHub _hub;
    private readonly ILogger<SettingsController> _logger;

    public SettingsController(SettingsService settings, SseHub hub, ILogger<SettingsController> logger)
    {
        _settings = settings;
        _hub = hub;
        _logger = logger;
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<SettingsSnapshotResponse> GetAll([FromQuery] string? profile)
        => Ok(_settings.GetAll(User.GetUserId().ToString(), profile ?? JellyPlayDatabase.BaseProfile));

    [HttpGet("changed")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<SettingsSnapshotResponse> GetChanged(
        [FromQuery, Required] long since,
        [FromQuery] string? profile)
        => Ok(_settings.GetChanged(User.GetUserId().ToString(), profile ?? JellyPlayDatabase.BaseProfile, since));

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<SettingsBatchResponse> ApplyBatch([FromBody, Required] SettingsBatchRequest request)
        => Ok(_settings.ApplyBatch(
            User.GetUserId().ToString(),
            request.Profile,
            request.DeviceId ?? User.GetDeviceId(),
            request.Writes));

    [HttpDelete("{ns}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult ResetNamespace([FromRoute, Required] string ns, [FromQuery] string? profile)
    {
        _settings.ResetNamespace(User.GetUserId().ToString(), profile, ns);
        return NoContent();
    }

    [HttpGet("resolved/{profile?}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<SettingsSnapshotResponse> Resolve([FromRoute] string? profile)
        => Ok(_settings.ResolveProfile(User.GetUserId().ToString(), profile ?? JellyPlayDatabase.BaseProfile));

    [HttpPost("profile/{profile}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<SettingsBatchResponse> SaveDeviceProfile(
        [FromRoute, Required] string profile,
        [FromBody, Required] SettingsBatchRequest request)
        => Ok(_settings.ApplyBatch(
            User.GetUserId().ToString(),
            profile,
            request.DeviceId ?? User.GetDeviceId(),
            request.Writes));

    /// <summary>Live settings stream for the authenticated user (event: settings.changed / settings.reset).</summary>
    [HttpGet("stream")]
    public async Task Stream(CancellationToken cancellationToken)
    {
        var subscriberId = _hub.Subscribe(User.GetUserId().ToString(), "settings");
        await SseStreamWriter.WriteAsync(HttpContext, _hub, subscriberId, cancellationToken);
    }
}
