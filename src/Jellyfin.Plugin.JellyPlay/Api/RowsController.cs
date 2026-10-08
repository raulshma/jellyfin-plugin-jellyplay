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
    private readonly CustomRowsService _rows;
    private readonly SeasonalService _seasonal;
    private readonly Func<Configuration.RowsConfig> _config;

    public RowsController(CustomRowsService rows, SeasonalService seasonal, Func<Configuration.RowsConfig> config)
    {
        _rows = rows;
        _seasonal = seasonal;
        _config = config;
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
}
