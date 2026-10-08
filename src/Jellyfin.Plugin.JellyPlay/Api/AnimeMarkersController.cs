using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Services.Anime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyPlay.Api;

[ApiController]
[Authorize]
[Route(JellyPlayContract.RoutePrefix)]
public class AnimeMarkersController : JellyPlayControllerBase
{
    private readonly AnimeMarkersService _markers;

    public AnimeMarkersController(AnimeMarkersService markers)
    {
        _markers = markers;
    }

    [HttpGet("animemarkers/series")]
    public async Task<IActionResult> GetSeriesMarkers([FromQuery, Required] string seriesId, [FromQuery] string? providerSeriesId)
    {
        // providerSeriesId (when given) is preferred; otherwise the service
        // derives the provider ids from the library item.
        var result = await _markers.GetSeriesMarkers(seriesId, providerSeriesId);
        return result is null ? NotFound() : JellyPlayResponses.Camel(result);
    }

    [HttpGet("animemarkers/items")]
    public async Task<IActionResult> GetEpisodeMarkers(
        [FromQuery, Required] string seriesId,
        [FromQuery, Required] int from,
        [FromQuery, Required] int to,
        [FromQuery] string? providerSeriesId)
    {
        var result = await _markers.GetEpisodeMarkers(seriesId, from, to, providerSeriesId);
        return JellyPlayResponses.Camel(new { markers = result });
    }
}
