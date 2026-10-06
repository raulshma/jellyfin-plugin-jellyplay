using System.ComponentModel.DataAnnotations;
using Jellyfin.Plugin.JellyPlay.Helpers;
using Jellyfin.Plugin.JellyPlay.Services.Settings;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
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
public class SyncController : ControllerBase
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
    public ActionResult<SyncStatusResponse> GetStatus()
        => JellyPlayResponses.Camel(_insights.GetStatus(User.GetUserId().ToString()));

    /// <summary>
    /// The caller's recorded sync operations (push/pull/reset), newest-first.
    /// seq is the sync-history id; since filters to entries newer than the
    /// given unix-ms timestamp. fromSeq/toSeq bracket the change-log range the
    /// operation covered (both null on pre-v6 rows; zero-width on resets).
    /// </summary>
    [HttpGet("history")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<SyncHistoryResponse> GetHistory(
        [FromQuery] long? since,
        [FromQuery] int limit = SyncInsightsService.DefaultHistoryLimit)
        => JellyPlayResponses.Camel(_insights.GetHistory(User.GetUserId().ToString(), since, limit));

    /// <summary>
    /// The per-key diff of one of the caller's recorded operations: the
    /// change-log rows in (fromSeq, toSeq], newest-first (limit defaults to
    /// 200, clamped 1..200). Resets — and any row without a usable range —
    /// return an empty key list ("namespace reset"). 404 when the seq is not
    /// the caller's own history row.
    /// </summary>
    [HttpGet("history/{seq}/keys")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<SyncHistoryKeysResponse> GetHistoryKeys(
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
public class SyncAdminController : ControllerBase
{
    private readonly SyncInsightsService _insights;
    private readonly IUserManager _users;

    public SyncAdminController(SyncInsightsService insights, IUserManager users)
    {
        _insights = insights;
        _users = users;
    }

    /// <summary>
    /// Every user with settings rows: footprint vs nothing (raw totals),
    /// display name resolved from the host's user manager (falls back to the
    /// id), latest recorded sync time and distinct device count.
    /// </summary>
    [HttpGet("overview")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<AdminSyncOverviewResponse> GetOverview()
        => JellyPlayResponses.Camel(_insights.GetAdminOverview(guid => _users.GetUserById(guid)?.Username));
}
