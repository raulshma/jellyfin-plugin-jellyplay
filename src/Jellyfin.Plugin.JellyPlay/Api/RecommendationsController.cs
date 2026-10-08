using System;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Services.Recommendations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyPlay.Api;

[ApiController]
[Authorize]
[Route(JellyPlayContract.RoutePrefix)]
public class RecommendationsController : JellyPlayControllerBase
{
    private readonly SimilarItemsService _similar;

    public RecommendationsController(SimilarItemsService similar)
    {
        _similar = similar;
    }

    [HttpGet("items/{itemId}/similar")]
    public async Task<IActionResult> GetSimilar([FromRoute, Required] Guid itemId, [FromQuery] int limit = 12)
    {
        var result = await _similar.GetSimilar(itemId, limit);
        return JellyPlayResponses.Camel(new { items = result });
    }
}
