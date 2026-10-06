using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.JellyPlay.Realtime;
using Jellyfin.Plugin.JellyPlay.Services.Settings;
using Jellyfin.Plugin.JellyPlay.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.JellyPlay.Tests;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "jellyplay-svc-tests-" + Guid.NewGuid().ToString("N"));
    private readonly JellyPlayDatabase _db;
    private readonly SettingsService _service;
    private Configuration.SyncConfig _syncConfig = new();

    public SettingsServiceTests()
    {
        Directory.CreateDirectory(_tempDir);
        _db = new JellyPlayDatabase(_tempDir);
        _service = new SettingsService(_db, new SseHub(NullLogger<SseHub>.Instance), () => _syncConfig, NullLogger<SettingsService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static Api.SettingsWriteDto Dto(string ns, string key, long at, string json = "true")
        => new() { Ns = ns, Key = key, SchemaVersion = 1, UpdatedAt = at, Value = JsonDocument.Parse(json).RootElement };

    [Fact]
    public void ResolveProfile_BasePlusOverlay_OverlayWins()
    {
        _service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "theme", 1, "\"base\""), Dto("ui", "skip", 1, "30") });
        _service.ApplyBatch("u1", "tv", "d1", new[] { Dto("ui", "theme", 2, "\"tv\"") });

        var resolved = _service.ResolveProfile("u1", "tv");

        var theme = resolved.Settings.Single(entry => entry.Key == "theme");
        Assert.Equal("\"tv\"", theme.Value.GetRawText());
        Assert.Equal(2, resolved.Settings.Count); // skip inherited from base
    }

    [Fact]
    public void ResolveProfile_ForcedDefault_OverridesUser()
    {
        _service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "theme", 1, "\"user\"") });
        _service.SetAdminDefaults(
            SettingsService.GlobalDefaultsScope,
            JsonDocument.Parse("{\"ui/theme\":{\"mode\":\"forced\",\"value\":\"dark\"}}").RootElement);

        var resolved = _service.ResolveProfile("u1", "");

        Assert.Equal("\"dark\"", resolved.Settings.Single(entry => entry.Key == "theme").Value.GetRawText());
    }

    [Fact]
    public void ResolveProfile_SuggestedDefault_FillsMissingOnly()
    {
        _service.SetAdminDefaults(
            SettingsService.GlobalDefaultsScope,
            JsonDocument.Parse("{\"player/skip\":{\"mode\":\"suggested\",\"value\":15}}").RootElement);

        var resolved = _service.ResolveProfile("u2", "");

        Assert.Equal(15, resolved.Settings.Single(entry => entry.Key == "skip").Value.GetInt32());
    }

    [Fact]
    public void ResolveProfile_SuggestedDefault_DoesNotOverrideUserValue()
    {
        _service.ApplyBatch("u1", "", "d1", new[] { Dto("player", "skip", 1, "30") });
        _service.SetAdminDefaults(
            SettingsService.GlobalDefaultsScope,
            JsonDocument.Parse("{\"player/skip\":{\"mode\":\"suggested\",\"value\":15}}").RootElement);

        var resolved = _service.ResolveProfile("u1", "");

        Assert.Equal(30, resolved.Settings.Single(entry => entry.Key == "skip").Value.GetInt32());
    }

    [Fact]
    public void ApplyBatch_RejectsStale_AndReturnsHead()
    {
        var first = _service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "theme", 10) });
        var second = _service.ApplyBatch("u1", "", "d2", new[] { Dto("ui", "theme", 5) });

        Assert.Single(first.Applied);
        Assert.Single(second.Rejected);
        Assert.True(second.Head >= first.Head);
    }

    [Fact]
    public void ResetNamespace_RemovesAllKeysInNamespace()
    {
        _service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "a", 1), Dto("ui", "b", 1), Dto("player", "c", 1) });

        _service.ResetNamespace("u1", "", "ui", "d1");

        var all = _service.GetAll("u1", "");
        var remaining = Assert.Single(all.Settings);
        Assert.Equal("player", remaining.Ns);
    }

    // ------------------------------------------------------------------
    // Resolved-settings modes map (additive field, enables forced-lock UI)
    // ------------------------------------------------------------------

    [Fact]
    public void ResolveProfile_ModesMap_MixesForcedSuggestedUnset()
    {
        _service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "theme", 1, "\"user\""), Dto("ui", "locked", 1, "\"user\"") });
        _service.SetAdminDefaults(
            SettingsService.GlobalDefaultsScope,
            JsonDocument.Parse(
                "{\"ui/locked\":{\"mode\":\"forced\",\"value\":true},\"player/skip\":{\"mode\":\"suggested\",\"value\":15}}")
                .RootElement);

        var resolved = _service.ResolveProfile("u1", "");

        // forced replaces the user value; suggested fills the unset key; the
        // plain user key (and the user's locked value before the override)
        // reads as unset.
        Assert.True(resolved.Settings.Single(entry => entry.Key == "locked").Value.GetBoolean());
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["ui/locked"] = "forced",
                ["player/skip"] = "suggested",
                ["ui/theme"] = "unset"
            },
            resolved.Modes);
    }

    [Fact]
    public void ResolveProfile_SuggestedLosingToUserValue_ModeStaysUnset()
    {
        // User scope wins: the suggested default does not apply, so the
        // resolved value's provenance is the user's — "unset", not "suggested".
        _service.ApplyBatch("u1", "", "d1", new[] { Dto("player", "skip", 1, "30") });
        _service.SetAdminDefaults(
            SettingsService.GlobalDefaultsScope,
            JsonDocument.Parse("{\"player/skip\":{\"mode\":\"suggested\",\"value\":15}}").RootElement);

        var resolved = _service.ResolveProfile("u1", "");

        Assert.Equal(30, resolved.Settings.Single(entry => entry.Key == "skip").Value.GetInt32());
        Assert.Equal("unset", resolved.Modes!["player/skip"]);
    }

    [Fact]
    public void ResolveProfile_UserScopeDefaultWins_ModeComesFromUserScope()
    {
        // Global says suggested, the user-scope default says forced: the user
        // scope wins for BOTH the mode and the semantics.
        _service.SetAdminDefaults(
            SettingsService.GlobalDefaultsScope,
            JsonDocument.Parse("{\"ui/volume\":{\"mode\":\"suggested\",\"value\":50}}").RootElement);
        _service.SetAdminDefaults(
            "u1",
            JsonDocument.Parse("{\"ui/volume\":{\"mode\":\"forced\",\"value\":80}}").RootElement);

        var resolved = _service.ResolveProfile("u1", "");

        Assert.Equal(80, resolved.Settings.Single(entry => entry.Key == "volume").Value.GetInt32());
        Assert.Equal("forced", resolved.Modes!["ui/volume"]);

        // And the reverse: a user-scope suggested downgrade loses to nothing —
        // it replaces the global forced entry wholesale.
        _service.SetAdminDefaults(
            "u2",
            JsonDocument.Parse("{\"ui/volume\":{\"mode\":\"suggested\",\"value\":40}}").RootElement);
        _service.SetAdminDefaults(
            SettingsService.GlobalDefaultsScope,
            JsonDocument.Parse("{\"ui/volume\":{\"mode\":\"forced\",\"value\":90}}").RootElement);

        var u2 = _service.ResolveProfile("u2", "");
        Assert.Equal(40, u2.Settings.Single(entry => entry.Key == "volume").Value.GetInt32());
        Assert.Equal("suggested", u2.Modes!["ui/volume"]);
    }

    [Fact]
    public void ResolveProfile_ProfileOverlayKey_ModeIsUnset()
    {
        _service.ApplyBatch("u1", "tv", "d1", new[] { Dto("ui", "layout", 1, "\"tv\"") });

        var resolved = _service.ResolveProfile("u1", "tv");

        Assert.Equal("unset", resolved.Modes!["ui/layout"]);
    }

    [Fact]
    public void PlainSnapshots_CarryNoModes()
    {
        _service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "theme", 1) });
        _service.SetAdminDefaults(
            SettingsService.GlobalDefaultsScope,
            JsonDocument.Parse("{\"ui/locked\":{\"mode\":\"forced\",\"value\":true}}").RootElement);

        Assert.Null(_service.GetAll("u1", "").Modes);
        Assert.Null(_service.GetChanged("u1", "", 0, "d1").Modes);
    }
}
