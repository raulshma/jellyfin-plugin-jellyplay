using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Services.Admin;
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

    public AdminController(AdminDefaultsService defaults, ConfigBackupService backup)
    {
        _defaults = defaults;
        _backup = backup;
    }

    /// <summary>Push stored tri-state defaults into user base settings (all users or one).</summary>
    [HttpPost("pushDefaults/{userId?}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult PushDefaults([FromRoute] string? userId)
    {
        var outcome = _defaults.PushDefaults(string.IsNullOrEmpty(userId) ? null : userId);
        return JellyPlayResponses.Camel(outcome);
    }

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
