using Jellyfin.Plugin.JellyPlay.Helpers;
using Jellyfin.Plugin.JellyPlay.Services.Push;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
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
public class PushAdminController : ControllerBase
{
    private readonly PushDispatcher _push;
    private readonly IUserManager _users;

    public PushAdminController(PushDispatcher push, IUserManager users)
    {
        _push = push;
        _users = users;
    }

    /// <summary>Whether push dispatching is enabled, plus every registered device.</summary>
    [HttpGet("overview")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetOverview()
        => JellyPlayResponses.Camel(_push.GetAdminOverview(guid => _users.GetUserById(guid)?.Username));
}
