using Jellyfin.Plugin.JellyPlay.Helpers;
using Jellyfin.Plugin.JellyPlay.Services.Analytics;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyPlay.Api;

/// <summary>
/// Admin analytics reporting (v1): aggregate playback activity over the
/// server — per-day and per-user from the daily rollups (retained forever),
/// top items and the raw session feed (bounded by the raw retention window).
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route(JellyPlayContract.RoutePrefix + "/admin/analytics")]
public class AnalyticsAdminController : ControllerBase
{
    private readonly AnalyticsService _analytics;
    private readonly IUserManager _users;

    public AnalyticsAdminController(AnalyticsService analytics, IUserManager users)
    {
        _analytics = analytics;
        _users = users;
    }

    /// <summary>
    /// Aggregated playback activity for the last ?days= UTC days (default 30,
    /// clamped 1..365): totals, per-day, per-user (names resolved from the
    /// host's user manager, id fallback) and the top-10 most-played items.
    /// </summary>
    [HttpGet("overview")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetOverview([FromQuery] int days = AnalyticsService.DefaultOverviewDays)
        => JellyPlayResponses.Camel(_analytics.GetOverview(days, guid => _users.GetUserById(guid)?.Username));

    /// <summary>
    /// Raw finished playback sessions, newest-first. Optional userId filter
    /// and since (unix ms) filter; ?limit= defaults to 50, at most 200.
    /// </summary>
    [HttpGet("sessions")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetSessions(
        [FromQuery] string? userId,
        [FromQuery] long? since,
        [FromQuery] int limit = AnalyticsService.DefaultSessionLimit)
        => JellyPlayResponses.Camel(_analytics.GetSessions(userId, since, limit));
}

/// <summary>
/// Per-user analytics ("Your watching"): the admin overview's aggregation
/// minus perUser, scoped strictly to the caller's own rows. Deliberately NOT
/// elevation-gated — every authenticated user may read their own stats (the
/// established userratings/bookmarks caller-scoping pattern).
/// </summary>
[ApiController]
[Authorize]
[Route(JellyPlayContract.RoutePrefix + "/analytics")]
public class AnalyticsMeController : ControllerBase
{
    private readonly AnalyticsService _analytics;

    public AnalyticsMeController(AnalyticsService analytics)
    {
        _analytics = analytics;
    }

    /// <summary>
    /// The caller's own playback activity over the last ?days= UTC days
    /// (default 30, clamped 1..365): totals (plays, playSeconds,
    /// transcodeSeconds, uniqueItems), perDay and topItems.
    /// </summary>
    [HttpGet("me")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetMine([FromQuery] int days = AnalyticsService.DefaultOverviewDays)
        => JellyPlayResponses.Camel(_analytics.GetMyOverview(User.GetUserId().ToString(), days));
}
