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
public class NewsletterController : JellyPlayControllerBase
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
        if (!_newsletter.IsConfigured)
        {
            return Unconfigured();
        }

        await _newsletter.SendAsync();
        return NoContent();
    }

    [HttpPost("test")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SendTest()
    {
        if (!_newsletter.IsConfigured)
        {
            return Unconfigured();
        }

        await _newsletter.SendTestAsync();
        return NoContent();
    }

    /// <summary>The CONTRACT.md 400 body for an unconfigured SMTP setup — through the error seam, not a local shape.</summary>
    private ContentResult Unconfigured()
        => JellyPlayResponses.Error(StatusCodes.Status400BadRequest, "smtp-unconfigured");
}
