using System.ComponentModel.DataAnnotations;
using Jellyfin.Plugin.JellyPlay.Helpers;
using Jellyfin.Plugin.JellyPlay.Services.Admin;
using Jellyfin.Plugin.JellyPlay.Services.Settings;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyPlay.Api;

/// <summary>
/// Sync observability for the authenticated user: footprint/quota status and
/// the recorded operation history (additive under the settings-sync feature).
/// </summary>
[ApiController]
[Authorize]
[Route(JellyPlayContract.RoutePrefix + "/sync")]
public class SyncController : JellyPlayControllerBase
{
    private readonly SyncInsightsService _insights;

    public SyncController(SyncInsightsService insights)
    {
        _insights = insights;
    }

    /// <summary>
    /// The caller's sync state: change-log head, key/byte totals against the
    /// configured quotas, per-namespace rollups and the latest recorded
    /// operation per device.
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetStatus()
        => JellyPlayResponses.Camel(_insights.GetStatus(User.GetUserId().ToString()));

    /// <summary>
    /// The caller's recorded sync operations (push/pull/reset), newest-first.
    /// seq is the sync-history id; since filters to entries newer than the
    /// given unix-ms timestamp. fromSeq/toSeq bracket the change-log range the
    /// operation covered (both null on pre-v6 rows; zero-width on resets).
    /// </summary>
    [HttpGet("history")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetHistory(
        [FromQuery] long? since,
        [FromQuery] int limit = SyncInsightsService.DefaultHistoryLimit)
        => JellyPlayResponses.Camel(_insights.GetHistory(User.GetUserId().ToString(), since, limit));

    /// <summary>
    /// The per-key diff of one of the caller's recorded operations: the
    /// change-log rows in (fromSeq, toSeq], newest-first (limit defaults to
    /// 200, clamped 1..200). Rows without a usable range (pre-v7 resets,
    /// no-op operations) return an empty key list. 404 when the seq is not
    /// the caller's own history row.
    /// </summary>
    [HttpGet("history/{seq}/keys")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetHistoryKeys(
        [FromRoute, Required] long seq,
        [FromQuery] int limit = SyncInsightsService.DefaultKeysLimit)
    {
        var response = _insights.GetHistoryKeys(User.GetUserId().ToString(), seq, limit);
        return response is null ? NotFound() : JellyPlayResponses.Camel(response);
    }
}

/// <summary>Cross-user sync overview for admins.</summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route(JellyPlayContract.RoutePrefix + "/admin/sync")]
public class SyncAdminController : JellyPlayControllerBase
{
    private readonly SyncInsightsService _insights;
    private readonly AdminUsers _adminUsers;
    private readonly Services.Devices.DeviceRegistryService _devices;

    public SyncAdminController(
        SyncInsightsService insights,
        AdminUsers adminUsers,
        Services.Devices.DeviceRegistryService devices)
    {
        _insights = insights;
        _adminUsers = adminUsers;
        _devices = devices;
    }

    /// <summary>
    /// Every user with settings rows: footprint vs nothing (raw totals),
    /// display name resolved from the host's user manager (falls back to the
    /// id), latest recorded sync time and distinct device count.
    /// </summary>
    [HttpGet("overview")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetOverview()
        => JellyPlayResponses.Camel(_insights.GetAdminOverview(_adminUsers.ResolveName));

    /// <summary>
    /// One user's drill-down: the same status fold their own sync/status
    /// endpoint serves (footprint vs quotas, namespaces, per-device latest
    /// ops) plus the device registry rows — push endpoint secrets stripped.
    /// </summary>
    [HttpGet("user/{userId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetDrilldown([FromRoute, Required] string userId)
        => JellyPlayResponses.Camel(_insights.GetAdminUserDrilldown(userId, _adminUsers.ResolveName));

    /// <summary>
    /// The admin revoke: identical semantics to the owner's DELETE
    /// jellyplay/devices/{id} — including the caps gate. A capped (v7)
    /// device's row survives flagged revoked and every settings row it wrote
    /// is tombstone-wiped; a capless legacy device is plain-unregistered (row
    /// removed). 404 when the user owns no such device.
    /// </summary>
    [HttpDelete("user/{userId}/devices/{deviceId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult RevokeDevice([FromRoute, Required] string userId, [FromRoute, Required] string deviceId)
        => _devices.RevokeAndWipe(userId, deviceId) == Services.Devices.DeleteDeviceOutcome.NotFound
            ? NotFound()
            : NoContent();

    /// <summary>
    /// The audit export (history + per-key diffs) for one user:
    /// <c>format=json</c> (default) returns the newest-first entries with the
    /// key diffs folded in; <c>format=csv</c> downloads an RFC 4180 file
    /// (one row per diff key). Anything else is 400 <c>unsupported-format</c>.
    /// </summary>
    [HttpGet("export")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult ExportAudit(
        [FromQuery, Required] string userId,
        [FromQuery] string? format,
        [FromQuery] int limit = SyncInsightsService.DefaultAuditLimit)
    {
        if (!string.Equals(format, "csv", StringComparison.Ordinal)
            && !string.Equals(format, "json", StringComparison.Ordinal))
        {
            return JellyPlayResponses.Error(StatusCodes.Status400BadRequest, "unsupported-format");
        }

        var export = _insights.ExportAudit(userId, limit);
        return string.Equals(format, "csv", StringComparison.Ordinal)
            ? File(
                System.Text.Encoding.UTF8.GetBytes(SyncAuditCsv.Build(export)),
                "text/csv",
                $"jellyplay-sync-{userId}-{System.DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.csv")
            : JellyPlayResponses.Camel(export);
    }
}
