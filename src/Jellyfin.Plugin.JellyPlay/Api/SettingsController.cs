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
public class SettingsController : JellyPlayControllerBase
{
    private readonly SettingsService _settings;
    private readonly SnapshotService _snapshots;
    private readonly SseHub _hub;
    private readonly ILogger<SettingsController> _logger;

    public SettingsController(SettingsService settings, SnapshotService snapshots, SseHub hub, ILogger<SettingsController> logger)
    {
        _settings = settings;
        _snapshots = snapshots;
        _hub = hub;
        _logger = logger;
    }

    /// <summary>
    /// The caller's settings snapshot, paged. The cursor/limit pair rides the
    /// shared <c>Paged</c> seam in the settings service (opaque cursor,
    /// over-fetch detection): <c>nextCursor</c> is present ONLY when more rows
    /// follow — absent means last page.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetAll([FromQuery] string? profile, [FromQuery] long? cursor, [FromQuery] int? limit)
        => JellyPlayResponses.Camel(_settings.GetAll(
            User.GetUserIdString(),
            profile ?? JellyPlayDatabase.BaseProfile,
            cursor,
            limit));

    /// <summary>
    /// The delta since a change-log cursor: current values plus the
    /// <c>deleted[]</c> half. Rows page through the shared <c>Paged</c> seam
    /// (SQL offset window + over-fetch); <c>deleted[]</c> is never paginated.
    /// </summary>
    [HttpGet("changed")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetChanged(
        [FromQuery, Required] long since,
        [FromQuery] string? profile,
        [FromQuery] string? deviceId,
        [FromQuery] long? cursor,
        [FromQuery] int? limit)
        => JellyPlayResponses.Camel(_settings.GetChanged(
            User.GetUserIdString(),
            profile ?? JellyPlayDatabase.BaseProfile,
            since,
            deviceId,
            cursor,
            limit));

    [HttpPost]
    [RateLimit(Services.Admin.RateLimiterKind.Settings, "settings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public IActionResult ApplyBatch([FromBody, Required] SettingsBatchRequest request)
        => ApplyBatchCore(request.Profile, request);

    [HttpDelete("{ns}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult ResetNamespace([FromRoute, Required] string ns, [FromQuery] string? profile)
    {
        _settings.ResetNamespace(User.GetUserIdString(), profile, ns, User.GetDeviceId());
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
        => JellyPlayResponses.Camel(_settings.ResolveProfile(User.GetUserIdString(), profile ?? JellyPlayDatabase.BaseProfile));

    /// <summary>Both mutating batch routes carry the same limiter — a per-profile route without it would be an open bypass.</summary>
    [HttpPost("profile/{profile}")]
    [RateLimit(Services.Admin.RateLimiterKind.Settings, "settings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public IActionResult SaveDeviceProfile(
        [FromRoute, Required] string profile,
        [FromBody, Required] SettingsBatchRequest request)
        => ApplyBatchCore(profile, request);

    private IActionResult ApplyBatchCore(string? profile, SettingsBatchRequest request)
        => JellyPlayResponses.Camel(_settings.ApplyBatch(
            User.GetUserIdString(),
            profile,
            User.ResolveDeviceId(request.DeviceId),
            request.Writes));

    // ------------------------------------------------------------------
    // Restore points (schema v7)
    // ------------------------------------------------------------------

    /// <summary>The caller's restore points, newest-first (metadata only).</summary>
    [HttpGet("snapshots")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetSnapshots()
        => JellyPlayResponses.Camel(_snapshots.List(User.GetUserIdString()).Select(row => new SnapshotDto(
            row.Id,
            row.CreatedAt,
            row.Origin,
            row.Keys,
            row.Bytes)));

    /// <summary>Captures a manual restore point of the caller's whole settings store (full-store copy — rate-limited like the batch routes).</summary>
    [HttpPost("snapshots")]
    [RateLimit(Services.Admin.RateLimiterKind.Settings, "settings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public IActionResult CreateSnapshot()
    {
        var id = _snapshots.Create(User.GetUserIdString(), "manual");
        return id is null
            ? JellyPlayResponses.Error(StatusCodes.Status500InternalServerError, "snapshot-failed")
            : JellyPlayResponses.Camel(new SnapshotCreateResponse(id.Value));
    }

    /// <summary>
    /// Restores one of the caller's snapshots: tombstone batch over every
    /// current row, then the snapshot re-applied with a server-stamped LWW
    /// clock (so it wins), riding the ordinary batch pipeline (change log,
    /// anchored SSE event, history). 404 when the id is not the caller's own.
    /// Rate-limited like the batch routes — a restore is a whole-store write.
    /// </summary>
    [HttpPost("snapshots/{id}/restore")]
    [RateLimit(Services.Admin.RateLimiterKind.Settings, "settings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public IActionResult RestoreSnapshot([FromRoute, Required] long id)
    {
        var response = _settings.RestoreSnapshot(User.GetUserIdString(), id);
        return response is null ? NotFound() : JellyPlayResponses.Camel(response);
    }

    // ------------------------------------------------------------------
    // Export / import (schema v7)
    // ------------------------------------------------------------------

    /// <summary>The caller's whole settings store as a portable bundle (all profiles + modes + catalog stamp).</summary>
    [HttpGet("export")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Export()
        => JellyPlayResponses.Camel(_settings.Export(User.GetUserIdString()));

    /// <summary>Re-applies an exported bundle for the caller with server-now timestamps (LWW: beats anything older). Rate-limited like the batch routes — an import is a whole-store write.</summary>
    [HttpPost("import")]
    [RateLimit(Services.Admin.RateLimiterKind.Settings, "settings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public IActionResult Import([FromBody, Required] SettingsExportBundle bundle, [FromQuery] string? deviceId)
        => JellyPlayResponses.Camel(_settings.Import(User.GetUserIdString(), User.ResolveDeviceId(deviceId), bundle));

    /// <summary>Live settings stream for the authenticated user (event: settings.changed / settings.reset).</summary>
    [HttpGet("stream")]
    public Task Stream(CancellationToken cancellationToken)
        // Leverage the SSE subscription seam: no replay on the settings stream.
        => SseStreamWriter.WriteSubscribedAsync(HttpContext, _hub, User.GetUserIdString(), SseHub.SettingsStream, cancellationToken);
}
