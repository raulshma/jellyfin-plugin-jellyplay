using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Helpers;
using Jellyfin.Plugin.JellyPlay.Services.Transcodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyPlay.Api;

[ApiController]
[Authorize]
[Route(JellyPlayContract.RoutePrefix)]
public class TranscodesController : JellyPlayControllerBase
{
    private readonly TranscodeInsightsService _transcodes;
    private readonly Services.Admin.AdminUsers _adminUsers;

    public TranscodesController(TranscodeInsightsService transcodes, Services.Admin.AdminUsers adminUsers)
    {
        _transcodes = transcodes;
        _adminUsers = adminUsers;
    }

    [HttpGet("transcodes/active")]
    [Authorize(Policy = MediaBrowser.Common.Api.Policies.RequiresElevation)]
    public IActionResult GetActive() => JellyPlayResponses.Camel(new { transcodes = _transcodes.GetActiveTranscodes() });

    [HttpDelete("transcodes/active/{sessionId}")]
    [Authorize(Policy = MediaBrowser.Common.Api.Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Cancel([FromRoute, Required] string sessionId)
        => await _transcodes.CancelAsync(sessionId) ? NoContent() : NotFound();

    [HttpGet("transcodes/mine")]
    public IActionResult GetMine()
    {
        // Sessions carry the username, not the id — resolve it through the one
        // admin-identity module (raw-id fallback when the host is missing the
        // user; Identity.Name is not the username contract).
        var name = _adminUsers.ResolveName(User.GetUserId());
        return JellyPlayResponses.Camel(new { transcodes = _transcodes.GetMine(name) });
    }
}
