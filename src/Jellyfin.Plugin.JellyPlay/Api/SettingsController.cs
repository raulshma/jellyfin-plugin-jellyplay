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
    public IActionResult GetAll([FromQuery] string? profile)
        => JellyPlayResponses.Camel(_settings.GetAll(User.GetUserId().ToString(), profile ?? JellyPlayDatabase.BaseProfile));

    [HttpGet("changed")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetChanged(
        [FromQuery, Required] long since,
        [FromQuery] string? profile,
        [FromQuery] string? deviceId)
        => JellyPlayResponses.Camel(_settings.GetChanged(User.GetUserId().ToString(), profile ?? JellyPlayDatabase.BaseProfile, since, deviceId));

    [HttpPost]
    [RateLimit(typeof(Services.Admin.SettingsRateLimiter), "settings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public IActionResult ApplyBatch([FromBody, Required] SettingsBatchRequest request)
        => ApplyBatchCore(request.Profile, request);

    [HttpDelete("{ns}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult ResetNamespace([FromRoute, Required] string ns, [FromQuery] string? profile)
    {
        _settings.ResetNamespace(User.GetUserId().ToString(), profile, ns, User.GetDeviceId());
        return NoContent();
    }

    /// <summary>
    /// The known client settings catalog — GENERATED from the client's
    /// PreferenceSpec declarations (embedded artifact; regenerate via the
    /// client repo's :shared:core:datastore:generateSettingsCatalog task).
    /// The dashboard renders its defaults editor from this (keys, types,
    /// ranges, enum options) so admins never need to memorize setting ids;
    /// clients may consume it too. Advisory — unknown keys remain legal on
    /// the sync surface.
    /// </summary>
    [HttpGet("catalog")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetCatalog()
        => JellyPlayResponses.Camel(new
        {
            catalogSchema = Services.Settings.ClientSettingsCatalog.CatalogSchema,
            settings = Services.Settings.ClientSettingsCatalog.KnownSettings
        });

    [HttpGet("resolved/{profile?}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Resolve([FromRoute] string? profile)
        => JellyPlayResponses.Camel(_settings.ResolveProfile(User.GetUserId().ToString(), profile ?? JellyPlayDatabase.BaseProfile));

    /// <summary>Both mutating batch routes carry the same limiter — a per-profile route without it would be an open bypass.</summary>
    [HttpPost("profile/{profile}")]
    [RateLimit(typeof(Services.Admin.SettingsRateLimiter), "settings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public IActionResult SaveDeviceProfile(
        [FromRoute, Required] string profile,
        [FromBody, Required] SettingsBatchRequest request)
        => ApplyBatchCore(profile, request);

    private IActionResult ApplyBatchCore(string? profile, SettingsBatchRequest request)
        => JellyPlayResponses.Camel(_settings.ApplyBatch(
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
