using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.JellyPlay.Services.Settings;
using Xunit;

namespace Jellyfin.Plugin.JellyPlay.Tests;

/// <summary>
/// Catalog integrity: the artifact is GENERATED from the client's
/// PreferenceSpec declarations (see ClientSettingsCatalog's load path), and
/// the dashboard renders its editors from these descriptors — so the embedded
/// copy must load, describe only the real synced keys, and stay
/// self-consistent. Real keys used below come from the client's spec files;
/// if a rename breaks these lookups the artifact was regenerated without
/// updating the tests (or vice versa).
/// </summary>
public sealed class ClientSettingsCatalogTests
{
    [Fact]
    public void Embedded_artifact_loads_with_the_full_spec_surface()
    {
        Assert.True(ClientSettingsCatalog.KnownSettings.Count > 100,
            "the catalog should describe the full client spec surface, not a handful of keys");
        Assert.All(ClientSettingsCatalog.KnownSettings, descriptor =>
            Assert.Equal("prefs", descriptor.Ns));
    }

    [Fact]
    public void Secrets_and_identity_are_structurally_absent()
    {
        Assert.Null(ClientSettingsCatalog.Find("prefs", "pin_hash"));
        Assert.Null(ClientSettingsCatalog.Find("prefs", "active_server_id"));
        Assert.Null(ClientSettingsCatalog.Find("prefs", "active_user_id"));
        Assert.Null(ClientSettingsCatalog.Find("prefs", "device_id"));
    }

    [Fact]
    public void Ids_are_unique()
    {
        var duplicates = ClientSettingsCatalog.KnownSettings
            .GroupBy(s => s.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void Enums_have_options()
    {
        Assert.All(ClientSettingsCatalog.KnownSettings.Where(s => s.ValueType == "enum"), descriptor =>
        {
            Assert.NotNull(descriptor.Options);
            Assert.NotEmpty(descriptor.Options!);
        });
    }

    [Fact]
    public void Ranges_are_sane()
    {
        Assert.All(ClientSettingsCatalog.KnownSettings.Where(s => s.Min is not null && s.Max is not null), descriptor =>
            Assert.True(descriptor.Min <= descriptor.Max, descriptor.Id + ": min above max"));
    }

    [Fact]
    public void Defaults_match_declared_type()
    {
        Assert.All(ClientSettingsCatalog.KnownSettings.Where(s => s.DefaultValue is not null), descriptor =>
        {
            var value = descriptor.DefaultValue!.Value;
            var ok = descriptor.ValueType switch
            {
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "number" => value.ValueKind == JsonValueKind.Number,
                "enum" => value.ValueKind == JsonValueKind.String,
                "string" => value.ValueKind == JsonValueKind.String,
                _ => true
            };
            Assert.True(ok, descriptor.Id + ": default value does not match " + descriptor.ValueType);
        });
    }

    [Fact]
    public void Known_settings_validate_against_their_own_default()
    {
        Assert.All(ClientSettingsCatalog.KnownSettings.Where(s => s.DefaultValue is not null), descriptor =>
            Assert.Null(ClientSettingsCatalog.ValidateValue(descriptor, descriptor.DefaultValue!.Value)));
    }

    [Fact]
    public void Enum_constraints_are_enforced()
    {
        var theme = ClientSettingsCatalog.Find("prefs", "theme_mode");
        Assert.NotNull(theme);
        Assert.Equal(["SYSTEM", "LIGHT", "DARK", "SCHEDULED"], theme!.Options);

        Assert.Null(ClientSettingsCatalog.ValidateValue(theme!, JsonSerializer.SerializeToElement("DARK")));
        Assert.NotNull(ClientSettingsCatalog.ValidateValue(theme!, JsonSerializer.SerializeToElement("purple")));
        Assert.NotNull(ClientSettingsCatalog.ValidateValue(theme!, JsonSerializer.SerializeToElement(3)));
    }

    [Fact]
    public void Boolean_constraints_are_enforced()
    {
        var theming = ClientSettingsCatalog.Find("prefs", "dynamic_theming");
        Assert.NotNull(theming);

        Assert.Null(ClientSettingsCatalog.ValidateValue(theming!, JsonSerializer.SerializeToElement(true)));
        Assert.NotNull(ClientSettingsCatalog.ValidateValue(theming!, JsonSerializer.SerializeToElement(1)));
    }

    [Fact]
    public void Number_type_is_enforced()
    {
        var hour = ClientSettingsCatalog.Find("prefs", "scheduled_theme_start_hour");
        Assert.NotNull(hour);

        Assert.Null(ClientSettingsCatalog.ValidateValue(hour!, JsonSerializer.SerializeToElement(20)));
        Assert.NotNull(ClientSettingsCatalog.ValidateValue(hour!, JsonSerializer.SerializeToElement("twenty")));
    }

    [Fact]
    public void Unknown_keys_have_no_descriptor()
    {
        Assert.Null(ClientSettingsCatalog.Find("prefs", "not_a_real_key"));
        Assert.Null(ClientSettingsCatalog.Find("custom", "thing"));
    }

    [Fact]
    public void Payload_validation_flags_known_bad_values_and_skips_unknown_keys()
    {
        var payload = JsonSerializer.Deserialize<JsonElement>("""
            {
                "prefs/theme_mode": { "mode": "forced", "value": "purple" },
                "custom/thing": { "mode": "suggested", "value": "whatever" },
                "prefs/dynamic_theming": { "mode": "suggested", "value": true }
            }
            """);

        var problems = SettingsService.ValidateAgainstCatalog(payload);

        var problem = Assert.Single(problems);
        Assert.Contains("prefs/theme_mode", problem);
    }

    [Fact]
    public void Payload_validation_accepts_valid_known_values()
    {
        var payload = JsonSerializer.Deserialize<JsonElement>("""
            {
                "prefs/theme_mode": { "mode": "forced", "value": "DARK" },
                "prefs/dynamic_theming": { "mode": "suggested", "value": false }
            }
            """);

        Assert.Empty(SettingsService.ValidateAgainstCatalog(payload));
    }
}
