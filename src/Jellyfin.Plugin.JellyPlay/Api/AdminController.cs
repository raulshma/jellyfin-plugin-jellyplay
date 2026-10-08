using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Helpers;
using Jellyfin.Plugin.JellyPlay.Realtime;
using Jellyfin.Plugin.JellyPlay.Services.Admin;
using Jellyfin.Plugin.JellyPlay.Services.Settings;
using Jellyfin.Plugin.JellyPlay.Storage;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyPlay.Api;

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route(JellyPlayContract.RoutePrefix + "/admin")]
public class AdminController : JellyPlayControllerBase
{
    private readonly AdminDefaultsService _defaults;
    private readonly ConfigBackupService _backup;
    private readonly SettingsService _settings;
    private readonly AdminUsers _adminUsers;
    private readonly SseHub _hub;

    public AdminController(
        AdminDefaultsService defaults,
        ConfigBackupService backup,
        SettingsService settings,
        AdminUsers adminUsers,
        SseHub hub)
    {
        _defaults = defaults;
        _backup = backup;
        _settings = settings;
        _adminUsers = adminUsers;
        _hub = hub;
    }

    /// <summary>
    /// Push stored tri-state defaults into user base settings (all users or
    /// one). The additive <c>?dryRun=true</c> simulates the push: it reports
    /// would-apply/would-reject (and catalog problems) and writes NOTHING —
    /// no rows, no restore points, no history. Rate-limited per admin — the
    /// non-dry-run form rewrites every user's base settings.
    /// </summary>
    [HttpPost("pushDefaults/{userId?}")]
    [RateLimit(Services.Admin.RateLimiterKind.PushDefaults, "pushDefaults")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public IActionResult PushDefaults([FromRoute] string? userId, [FromQuery] bool dryRun = false)
    {
        var outcome = _defaults.PushDefaults(string.IsNullOrEmpty(userId) ? null : userId, dryRun);
        return JellyPlayResponses.Camel(outcome);
    }

    /// <summary>
    /// Resolved-settings preview for ANY user (admin simulator): the same
    /// pure merge the user's own resolved endpoint serves — base + profile
    /// overlay + tri-state defaults with the additive modes map. Reads only;
    /// nothing is written, no restore point or history entry is created.
    /// </summary>
    [HttpGet("settings/preview")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult PreviewResolved(
        [FromQuery, Required] string userId,
        [FromQuery] string? profile)
        => JellyPlayResponses.Camel(_settings.ResolveProfile(userId, profile ?? JellyPlayDatabase.BaseProfile));

    /// <summary>Every host user (the admin pickers' data source).</summary>
    [HttpGet("users")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetUsers()
        => JellyPlayResponses.Camel(new AdminUserListResponse(_adminUsers.AllUsers()));

    /// <summary>
    /// Live monitor stream (elevated subscribers only): one <c>sync.op</c>
    /// event per recorded settings-sync operation — push/pull/reset/wipe,
    /// every user. Broadcast delivery, hub-monotonic ids (a live view; the
    /// history endpoints are the durable record).
    /// </summary>
    [HttpGet("stream")]
    public Task Stream(CancellationToken cancellationToken)
        // Leverage the SSE subscription seam: no replay on the admin monitor stream.
        => SseStreamWriter.WriteSubscribedAsync(HttpContext, _hub, User.GetUserIdString(), SseHub.AdminStream, cancellationToken);

    [HttpGet("configBackup")]
    public IActionResult DownloadBackup()
        => File(_backup.CreateBackup(), "application/json", $"jellyplay-backup-{System.DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json");

    [HttpPost("configBackup")]
    [RequestSizeLimit(20_000_000)]
    public async Task<IActionResult> RestoreBackup()
    {
        using var ms = new System.IO.MemoryStream();
        await Request.Body.CopyToAsync(ms);
        var outcome = _backup.Restore(ms.ToArray());
        return outcome.Success
            ? JellyPlayResponses.Camel(outcome)
            : JellyPlayResponses.Camel(outcome, StatusCodes.Status400BadRequest);
    }
}
