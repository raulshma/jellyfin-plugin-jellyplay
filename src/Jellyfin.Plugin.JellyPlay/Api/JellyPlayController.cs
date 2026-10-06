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
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Jellyfin.Plugin.JellyPlay.Api;

/// <summary>Bootstrap probe, admin config introspection and the YAML editor endpoints.</summary>
[ApiController]
[Authorize]
[Route(JellyPlayContract.RoutePrefix)]
public class JellyPlayController : ControllerBase
{
    private static readonly string[] AllFeatures =
    [
        JellyPlayContract.Features.SettingsSync,
        JellyPlayContract.Features.DeviceProfiles,
        JellyPlayContract.Features.AdminDefaults,
        JellyPlayContract.Features.ConfigBackup,
        JellyPlayContract.Features.Events,
        JellyPlayContract.Features.Messages,
        JellyPlayContract.Features.SeerrBridge,
        JellyPlayContract.Features.Newsletter,
        JellyPlayContract.Features.Ratings,
        JellyPlayContract.Features.CustomRows,
        JellyPlayContract.Features.SeasonalRows,
        JellyPlayContract.Features.AnimeMarkers,
        JellyPlayContract.Features.Recommendations,
        JellyPlayContract.Features.UserRatings,
        JellyPlayContract.Features.Bookmarks,
        JellyPlayContract.Features.Transcodes,
        JellyPlayContract.Features.Push,
        JellyPlayContract.Features.Analytics
    ];

    private readonly SettingsService _settings;

    public JellyPlayController(SettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>The single bootstrap probe. Clients tolerate 404 (plugin absent) and feature-gate on the response.</summary>
    [HttpGet("capabilities")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<CapabilitiesResponse> GetCapabilities()
    {
        var config = JellyPlayPlugin.Instance?.Configuration;
        var features = new List<string>(AllFeatures);
        if (string.IsNullOrEmpty(config?.Seerr.ServerUrl) || string.IsNullOrEmpty(config.Seerr.ApiKey))
        {
            features.Remove(JellyPlayContract.Features.SeerrBridge);
        }

        if (config is null || !config.Ratings.Enabled())
        {
            features.Remove(JellyPlayContract.Features.Ratings);
        }

        if (config is null || !config.Rows.Enabled)
        {
            features.Remove(JellyPlayContract.Features.CustomRows);
        }

        if (config is null || !config.Rows.SeasonalEnabled)
        {
            features.Remove(JellyPlayContract.Features.SeasonalRows);
        }

        if (config is null || !config.Anime.Enabled)
        {
            features.Remove(JellyPlayContract.Features.AnimeMarkers);
        }

        if (string.IsNullOrEmpty(config?.Newsletter.SmtpHost))
        {
            features.Remove(JellyPlayContract.Features.Newsletter);
        }

        if (config is null || !config.Push.Enabled)
        {
            features.Remove(JellyPlayContract.Features.Push);
        }

        if (config is null || !config.Analytics.Enabled)
        {
            features.Remove(JellyPlayContract.Features.Analytics);
        }

        return JellyPlayResponses.Camel(new CapabilitiesResponse(
            JellyPlayContract.ContractVersion,
            typeof(JellyPlayPlugin).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? "0.11.3",
            features,
            System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["", "desktop", "phone", "tv"]));
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
    public ActionResult<IReadOnlyDictionary<string, string>> GetDashboardStrings([FromQuery] string? lang)
        => JellyPlayResponses.Camel(Helpers.DashboardStrings.All(Helpers.DashboardStrings.ResolveCulture(lang)));

    [HttpGet("config/yaml")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetConfigYaml()
    {
        var config = JellyPlayPlugin.Instance!.Configuration;
        var yaml = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build()
            .Serialize(config);
        return JellyPlayResponses.Camel(new { value = yaml });
    }

    [HttpPost("config/yaml")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> SetConfigYaml([FromBody] ConfigYamlRequest request)
    {
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            var config = deserializer.Deserialize<PluginConfiguration>(request.Value);
            var instance = JellyPlayPlugin.Instance!;
            instance.UpdateConfiguration(config);
            return JellyPlayResponses.Camel(new { error = false, message = string.Empty });
        }
        catch (System.Exception ex)
        {
            return JellyPlayResponses.Camel(new { error = true, message = ex.Message });
        }
    }
}

public sealed record ConfigYamlRequest(string Value);

/// <summary>Marker alias so GetGlobalDefaults can express JsonElement results cleanly.</summary>
public sealed class JsonElementCompat;
