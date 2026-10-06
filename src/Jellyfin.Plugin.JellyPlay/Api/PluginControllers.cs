using System;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Api;
using Jellyfin.Plugin.JellyPlay.Helpers;
using Jellyfin.Plugin.JellyPlay.Services.Anime;
using Jellyfin.Plugin.JellyPlay.Services.Recommendations;
using Jellyfin.Plugin.JellyPlay.Services.Rows;
using Jellyfin.Plugin.JellyPlay.Services.Transcodes;
using Jellyfin.Plugin.JellyPlay.Services.UserData;
using Jellyfin.Plugin.JellyPlay.Storage.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyPlay.Api;

[ApiController]
[Authorize]
[Route(JellyPlayContract.RoutePrefix)]
public class RowsController : ControllerBase
{
    private readonly CustomRowsService _rows;
    private readonly SeasonalService _seasonal;

    public RowsController(CustomRowsService rows, SeasonalService seasonal)
    {
        _rows = rows;
        _seasonal = seasonal;
    }

    /// <summary>
    /// The admin-defined custom row titles (catalog for clients that render
    /// one plugin row per configured list — the seasonal row is separate).
    /// </summary>
    [HttpGet("rows")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetRowTitles()
    {
        var rows = JellyPlayPlugin.Instance!.Configuration.Rows.CustomRows
            .Select(row => new { row.Title, row.Source, row.Limit })
            .ToList();
        return Ok(new { rows });
    }

    /// <summary>Resolves one admin-defined custom row.</summary>
    [HttpGet("rows/items")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRow([FromQuery, Required] string title)
    {
        var definition = JellyPlayPlugin.Instance!.Configuration.Rows.CustomRows
            .FirstOrDefault(row => string.Equals(row.Title, title, StringComparison.OrdinalIgnoreCase));
        if (definition is null)
        {
            return NotFound();
        }

        var result = await _rows.ResolveAsync(definition);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet("seasonal/row")]
    public async Task<IActionResult> GetSeasonal([FromQuery] string? keyword)
    {
        var result = await _seasonal.GetSeasonalRow(keyword);
        return result is null ? NotFound() : Ok(result);
    }
}

[ApiController]
[Authorize]
[Route(JellyPlayContract.RoutePrefix)]
public class AnimeMarkersController : ControllerBase
{
    private readonly AnimeMarkersService _markers;

    public AnimeMarkersController(AnimeMarkersService markers)
    {
        _markers = markers;
    }

    [HttpGet("animemarkers/series")]
    public async Task<IActionResult> GetSeriesMarkers([FromQuery, Required] string seriesId, [FromQuery] string? providerSeriesId)
    {
        var result = await _markers.GetSeriesMarkers(seriesId, providerSeriesId ?? seriesId);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet("animemarkers/items")]
    public async Task<IActionResult> GetEpisodeMarkers(
        [FromQuery, Required] string seriesId,
        [FromQuery, Required] int from,
        [FromQuery, Required] int to)
    {
        var result = await _markers.GetEpisodeMarkers(seriesId, from, to);
        return Ok(new { markers = result });
    }
}

[ApiController]
[Authorize]
[Route(JellyPlayContract.RoutePrefix)]
public class RecommendationsController : ControllerBase
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
        return Ok(new { items = result });
    }
}

[ApiController]
[Authorize]
[Route(JellyPlayContract.RoutePrefix)]
public class UserDataController : ControllerBase
{
    private readonly UserRatingsService _ratings;
    private readonly BookmarkService _bookmarks;

    public UserDataController(UserRatingsService ratings, BookmarkService bookmarks)
    {
        _ratings = ratings;
        _bookmarks = bookmarks;
    }

    [HttpGet("userratings/mine")]
    public IActionResult GetMyRatings([FromQuery] string? filter)
        => Ok(new { ratings = _ratings.GetMyRatings(User.GetUserId(), filter) });

    [HttpGet("bookmarks/{itemId}")]
    public IActionResult GetBookmarks([FromRoute, Required] string itemId)
        => Ok(new { bookmarks = _bookmarks.GetBookmarks(User.GetUserId().ToString(), itemId).Select(ToDto) });

    [HttpPost("bookmarks/{itemId}")]
    public IActionResult UpsertBookmark([FromRoute, Required] string itemId, [FromBody, Required] BookmarkRequest request)
        => Ok(ToDto(_bookmarks.Upsert(User.GetUserId().ToString(), itemId, request)));

    [HttpDelete("bookmarks/{itemId}/{bookmarkId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult DeleteBookmark([FromRoute, Required] string itemId, [FromRoute, Required] string bookmarkId)
        => _bookmarks.Delete(User.GetUserId().ToString(), bookmarkId) ? NoContent() : NotFound();

    private static BookmarkDto ToDto(BookmarkRow row) => new(
        row.Id, row.ItemId, row.Position, row.ChapterIndex, row.Label, row.Notes, row.CreatedAt, row.UpdatedAt);
}

[ApiController]
[Authorize]
[Route(JellyPlayContract.RoutePrefix)]
public class TranscodesController : ControllerBase
{
    private readonly TranscodeInsightsService _transcodes;

    public TranscodesController(TranscodeInsightsService transcodes)
    {
        _transcodes = transcodes;
    }

    [HttpGet("transcodes/active")]
    [Authorize(Policy = MediaBrowser.Common.Api.Policies.RequiresElevation)]
    public IActionResult GetActive() => Ok(new { transcodes = _transcodes.GetActiveTranscodes() });

    [HttpDelete("transcodes/active/{sessionId}")]
    [Authorize(Policy = MediaBrowser.Common.Api.Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Cancel([FromRoute, Required] string sessionId)
        => await _transcodes.CancelAsync(sessionId) ? NoContent() : NotFound();

    [HttpGet("transcodes/mine")]
    public IActionResult GetMine()
        => Ok(new { transcodes = _transcodes.GetMine(User.Identity?.Name ?? string.Empty) });
}
