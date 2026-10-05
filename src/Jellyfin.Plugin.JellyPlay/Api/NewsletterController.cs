using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Services.Newsletter;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MediaBrowser.Common.Api;

namespace Jellyfin.Plugin.JellyPlay.Api;

/// <summary>
/// Root-level routes matching the pre-existing JellyPlay client stubs
/// (raw POST /newsletter/send, POST /newsletter/test — no body).
/// </summary>
[ApiController]
[Authorize]
[Route("newsletter")]
public class NewsletterController : ControllerBase
{
    private readonly NewsletterService _newsletter;

    public NewsletterController(NewsletterService newsletter)
    {
        _newsletter = newsletter;
    }

    [HttpPost("send")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Send()
    {
        await _newsletter.SendAsync();
        return NoContent();
    }

    [HttpPost("test")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> SendTest()
    {
        await _newsletter.SendTestAsync();
        return NoContent();
    }
}
