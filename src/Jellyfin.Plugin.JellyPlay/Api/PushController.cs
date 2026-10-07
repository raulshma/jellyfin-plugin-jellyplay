using Jellyfin.Plugin.JellyPlay.Helpers;
using Jellyfin.Plugin.JellyPlay.Services.Admin;
using Jellyfin.Plugin.JellyPlay.Services.Push;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyPlay.Api;

/// <summary>
/// Admin overview of the push fleet: every push-registered device across
/// users. Endpoint URLs are secrets — only the host is surfaced, never the
/// path (the topic).
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route(JellyPlayContract.RoutePrefix + "/admin/push")]
public class PushAdminController : JellyPlayControllerBase
{
    private readonly PushDispatcher _push;
    private readonly AdminUsers _adminUsers;

    public PushAdminController(PushDispatcher push, AdminUsers adminUsers)
    {
        _push = push;
        _adminUsers = adminUsers;
    }

    /// <summary>Whether push dispatching is enabled, plus every registered device.</summary>
    [HttpGet("overview")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetOverview()
        => JellyPlayResponses.Camel(_push.GetAdminOverview(_adminUsers.ResolveName));
}
