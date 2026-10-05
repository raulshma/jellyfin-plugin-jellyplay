using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Services.Ratings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyPlay.Api;

[ApiController]
[Authorize]
[Route(JellyPlayContract.RoutePrefix)]
public class RatingsController : ControllerBase
{
    private readonly MdbListService _mdbList;
    private readonly TmdbRatingsService _tmdb;
    private readonly ImdbChartsService _imdb;

    public RatingsController(MdbListService mdbList, TmdbRatingsService tmdb, ImdbChartsService imdb)
    {
        _mdbList = mdbList;
        _tmdb = tmdb;
        _imdb = imdb;
    }

    [HttpGet("mdblist/ratings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRatings([FromQuery, Required] string imdbId)
    {
        var result = await _mdbList.GetRatings(imdbId);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet("mdblist/keyInfo")]
    public async Task<IActionResult> GetKeyInfo()
    {
        var json = await _mdbList.GetKeyInfo();
        return json is null ? NotFound() : Content(json, "application/json");
    }

    [HttpPost("mdblist/clearCache")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult ClearCache([FromQuery] string? imdbId)
    {
        _mdbList.ClearCache(imdbId);
        return NoContent();
    }

    [HttpGet("tmdb/seasonRatings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSeasonRatings([FromQuery, Required] string tmdbId, [FromQuery, Required] int seasonNumber)
    {
        var result = await _tmdb.GetSeasonRatings(tmdbId, seasonNumber);
        return result is null ? NotFound() : Ok(new { season = seasonNumber, episodes = result });
    }

    [HttpGet("tmdb/nextEpisode")]
    public async Task<IActionResult> GetNextEpisode([FromQuery, Required] string tmdbId)
    {
        var result = await _tmdb.GetNextEpisode(tmdbId);
        return result is null ? NotFound() : Ok(new { name = result.Name, airDate = result.AirDate });
    }

    [HttpGet("imdb/charts")]
    public async Task<IActionResult> GetTop250()
    {
        var chart = await _imdb.GetTop250();
        return chart is null ? NotFound() : Ok(chart);
    }
}
