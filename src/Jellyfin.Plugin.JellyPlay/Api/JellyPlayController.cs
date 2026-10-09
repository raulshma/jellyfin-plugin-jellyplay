using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.JellyPlay.Configuration;
using Jellyfin.Plugin.JellyPlay.Services.Settings;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;

namespace Jellyfin.Plugin.JellyPlay.Api;

/// <summary>Bootstrap probe, admin config introspection and the YAML editor endpoints.</summary>
[ApiController]
[Authorize]
[Route(JellyPlayContract.RoutePrefix)]
public class JellyPlayController : JellyPlayControllerBase
{
    /// <summary>
    /// Every feature key, derived from <see cref="JellyPlayContract.Features"/>
    /// by reflection (declaration order) — a new const flows into the capability
    /// probe automatically; the list can never silently drift from the contract.
    /// </summary>
    private static readonly string[] AllFeatures =
        typeof(JellyPlayContract.Features)
            .GetFields()
            .OrderBy(field => field.MetadataToken)
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();

    /// <summary>The plugin's wire version — assembly metadata, immutable once loaded; resolved once, not per request.</summary>
    private static readonly string PluginVersion
        = typeof(JellyPlayPlugin).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version
          ?? typeof(JellyPlayPlugin).Assembly.GetName().Version?.ToString()
          ?? string.Empty;

    private readonly SettingsService _settings;
    private readonly Func<Configuration.PluginConfiguration> _config;
    private readonly Services.Recommendations.SimilarItemsProviderManager _similarItems;

    public JellyPlayController(
        SettingsService settings,
        Func<Configuration.PluginConfiguration> config,
        Services.Recommendations.SimilarItemsProviderManager similarItems)
    {
        _settings = settings;
        _config = config;
        _similarItems = similarItems;
    }

    /// <summary>The single bootstrap probe. Clients tolerate 404 (plugin absent) and feature-gate on the response.</summary>
    [HttpGet("capabilities")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetCapabilities()
    {
        var config = _config();
        var features = AllFeatures
            .Where(f => FeatureAvailability.IsAvailable(f, config))
            .ToList();

        return JellyPlayResponses.Camel(new CapabilitiesResponse(
            JellyPlayContract.ContractVersion,
            PluginVersion,
            features,
            System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["", "desktop", "phone", "tv"],
            _similarItems.SimilarPipelineRegistered));
    }

    /// <summary>Admin tri-state defaults (global scope).</summary>
    [HttpGet("defaults")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetGlobalDefaults()
    {
        // The stored payload is already JSON — stream it through untouched.
        // Handing a System.Text.Json JsonElement to the plugin's Newtonsoft
        // gate would serialize the element's own properties ({"valueKind":1}).
        var raw = _settings.GetAdminDefaultsRaw(SettingsService.GlobalDefaultsScope);
        return raw is null
            ? JellyPlayResponses.Camel(new { })
            : new ContentResult
            {
                Content = raw.Value.GetRawText(),
                ContentType = "application/json; charset=utf-8",
                StatusCode = StatusCodes.Status200OK
            };
    }

    [HttpPost("defaults")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult SetGlobalDefaults([FromBody] System.Text.Json.JsonElement payload)
    {
        try
        {
            _settings.SetAdminDefaults(SettingsService.GlobalDefaultsScope, payload);
            return NoContent();
        }
        catch (Services.Settings.SettingsCatalogValidationException ex)
        {
            return JellyPlayResponses.Camel(new { error = true, message = ex.Message, problems = ex.Problems }, 400);
        }
    }

    /// <summary>
    /// The dashboard's localized string table (en embedded, ?lang= selects
    /// another culture with parent fallback) — consumed by the config pages'
    /// data-i18n pass, not feature-gated (cosmetic, no client behavior).
    /// </summary>
    [HttpGet("dashboard-strings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetDashboardStrings([FromQuery] string? lang)
        => JellyPlayResponses.Camel(Helpers.DashboardStrings.All(Helpers.DashboardStrings.ResolveCulture(lang)));

    [HttpGet("config/yaml")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetConfigYaml()
        => JellyPlayResponses.Camel(new { value = Services.Admin.ConfigYaml.Serialize(_config()) });

    [HttpPost("config/yaml")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult SetConfigYaml([FromBody] ConfigYamlRequest request)
    {
        try
        {
            var config = Services.Admin.ConfigYaml.Parse(request.Value);
            // The one remaining Instance read in a controller: persisting the
            // YAML round-trip is plugin-instance lifecycle (UpdateConfiguration),
            // not a config read — the documented ADR-0002 exception.
            JellyPlayPlugin.Instance!.UpdateConfiguration(config);
            return JellyPlayResponses.Camel(new { error = false, message = string.Empty });
        }
        catch (System.Exception ex)
        {
            return JellyPlayResponses.Camel(new { error = true, message = ex.Message });
        }
    }
}

public sealed record ConfigYamlRequest(string Value);
