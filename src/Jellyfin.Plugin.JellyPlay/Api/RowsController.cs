using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Services.Rows;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyPlay.Api;

[ApiController]
[Authorize]
[Route(JellyPlayContract.RoutePrefix)]
public class RowsController : JellyPlayControllerBase
{
    /// <summary>Serve-order cap for the curated hero — the hero is a small rotation pool, not a row.</summary>
    private const int MaxHeroCandidates = 10;

    private readonly CustomRowsService _rows;
    private readonly SeasonalService _seasonal;
    private readonly Func<Configuration.RowsConfig> _config;
    private readonly Func<Configuration.HeroConfig> _hero;

    public RowsController(CustomRowsService rows, SeasonalService seasonal, Func<Configuration.RowsConfig> config, Func<Configuration.HeroConfig> hero)
    {
        _rows = rows;
        _seasonal = seasonal;
        _config = config;
        _hero = hero;
    }

    /// <summary>
    /// The admin-defined custom row titles (catalog for clients that render
    /// one plugin row per configured list — the seasonal row is separate).
    /// </summary>
    [HttpGet("rows")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetRowTitles()
    {
        var rows = _config().CustomRows
            .Select(row => new { row.Title, row.Source, row.Limit })
            .ToList();
        return JellyPlayResponses.Camel(new { rows });
    }

    /// <summary>Resolves one admin-defined custom row.</summary>
    [HttpGet("rows/items")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRow([FromQuery, Required] string title)
    {
        var definition = _config().CustomRows
            .FirstOrDefault(row => string.Equals(row.Title, title, StringComparison.OrdinalIgnoreCase));
        if (definition is null)
        {
            return NotFound();
        }

        var result = await _rows.ResolveAsync(definition);
        return result is null ? NotFound() : JellyPlayResponses.Camel(result);
    }

    [HttpGet("seasonal/row")]
    public async Task<IActionResult> GetSeasonal([FromQuery] string? keyword)
    {
        var result = await _seasonal.GetSeasonalRow(keyword);
        return result is null ? NotFound() : JellyPlayResponses.Camel(result);
    }

    /// <summary>
    /// The admin-curated hero billboards (the `hero` feature): candidates in
    /// serve order — weight descending, config order breaking ties (the LINQ
    /// sort is stable, so an equal-weight list is exactly the authored order).
    /// Item ids only, no image payload: clients resolve them locally via
    /// `/Items?Ids=`, so art and metadata are always the client's own library
    /// read and an id the library no longer knows simply drops client-side.
    /// Blank ids never serve — they can never resolve.
    /// </summary>
    [HttpGet("hero")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetHero()
    {
        var candidates = _hero().Candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.ItemId))
            .OrderByDescending(candidate => candidate.Weight)
            .Take(MaxHeroCandidates)
            .Select(candidate => new { candidate.ItemId, candidate.Headline, candidate.Subhead, candidate.Weight })
            .ToList();
        return JellyPlayResponses.Camel(new { candidates });
    }
}
