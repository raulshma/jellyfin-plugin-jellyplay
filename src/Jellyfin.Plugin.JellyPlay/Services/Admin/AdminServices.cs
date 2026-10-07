using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Configuration;
using Jellyfin.Plugin.JellyPlay.Services.Settings;
using Jellyfin.Plugin.JellyPlay.Storage;
using Jellyfin.Plugin.JellyPlay.Storage.Models;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Jellyfin.Plugin.JellyPlay.Services.Admin;

/// <summary>
/// Pushes tri-state defaults into user accounts. Forced keys overwrite whatever
/// the user has; suggested keys only fill gaps. Scope "global" merges with any
/// per-user overrides already stored.
/// </summary>
public sealed class AdminDefaultsService
{
    private readonly SettingsService _settings;
    private readonly JellyPlayDatabase _db;
    private readonly ILogger<AdminDefaultsService> _logger;

    public AdminDefaultsService(SettingsService settings, JellyPlayDatabase db, ILogger<AdminDefaultsService> logger)
    {
        _settings = settings;
        _db = db;
        _logger = logger;
    }

    /// <summary>Returns per-user (per-key) outcome counts.</summary>
    public PushOutcome PushDefaults(string? userId)
    {
        var targets = string.IsNullOrEmpty(userId)
            ? _db.GetDistinctSettingUserIds()
            : new List<string> { userId! };

        var globalDefaults = _settings.GetAdminDefaultsRaw(SettingsService.GlobalDefaultsScope);

        var pushed = 0;
        foreach (var target in targets)
        {
            pushed += PushMerged(target, globalDefaults, _settings.GetAdminDefaultsRaw(target));
        }

        return new PushOutcome(targets.Count, pushed);
    }

    public sealed record PushOutcome(int Users, int KeysPushed);

    /// <summary>
    /// Pushes the scope-merged defaults to one user. The precedence lives in
    /// <see cref="DefaultsEnvelope.MergeForPush"/> (forced overwrites always,
    /// suggested fills gaps only, per-user scope beats global, the user's own
    /// value beats suggested); this method only orchestrates the resulting
    /// writes through the settings pipeline (LWW batch, change log, SSE). The
    /// conservative write-path shadow rule is on: a malformed per-user
    /// override blocks the push for its key instead of letting the global
    /// default fall through.
    /// </summary>
    private int PushMerged(string userId, JsonElement? globalDefaults, JsonElement? perUserDefaults)
    {
        var baseSnapshot = _settings.GetAll(userId, JellyPlayDatabase.BaseProfile);
        var existing = baseSnapshot.Settings.ToDictionary(entry => DefaultsEnvelope.Join(entry.Ns, entry.Key), entry => entry);

        var writes = new List<Api.SettingsWriteDto>();
        foreach (var entry in DefaultsEnvelope.MergeForPush(
                     globalDefaults,
                     perUserDefaults,
                     existing.Keys.ToHashSet(StringComparer.Ordinal)))
        {
            var hasExisting = existing.TryGetValue(DefaultsEnvelope.Join(entry.Ns, entry.Key), out var current);
            writes.Add(new Api.SettingsWriteDto
            {
                Ns = entry.Ns,
                Key = entry.Key,
                SchemaVersion = hasExisting ? current.SchemaVersion : 1,
                UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Value = entry.Value
            });
        }

        if (writes.Count == 0)
        {
            return 0;
        }

        var result = _settings.ApplyBatch(userId, JellyPlayDatabase.BaseProfile, "admin-push", writes);
        _logger.LogInformation("Pushed {Applied}/{Total} defaults to user {UserId}", result.Applied.Count, writes.Count, userId);
        return result.Applied.Count;
    }
}

/// <summary>JSON backup/restore of plugin state: admin defaults, messages, and plugin XML config.</summary>
public sealed class ConfigBackupService
{
    private readonly JellyPlayDatabase _db;
    private readonly ILogger<ConfigBackupService> _logger;

    public ConfigBackupService(JellyPlayDatabase db, ILogger<ConfigBackupService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public byte[] CreateBackup()
    {
        var backup = new BackupPayload
        {
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            PluginVersion = typeof(JellyPlayPlugin).Assembly.GetName().Version?.ToString() ?? "0.11.3",
            AdminDefaults = _db.GetAllAdminDefaults().ToList(),
            Messages = _db.GetMessages().ToList()
        };
        return JsonSerializer.SerializeToUtf8Bytes(backup);
    }

    public RestoreOutcome Restore(byte[] payload)
    {
        try
        {
            var backup = JsonSerializer.Deserialize<BackupPayload>(payload)
                ?? throw new JsonException("Backup payload is empty.");
            _db.RestoreAdminDefaults(backup.AdminDefaults ?? new List<AdminDefaultsRow>());
            foreach (var message in backup.Messages ?? new List<MessageRow>())
            {
                _db.UpsertMessage(message);
            }

            return new RestoreOutcome(true, string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Config restore failed");
            return new RestoreOutcome(false, ex.Message);
        }
    }

    public sealed class BackupPayload
    {
        public long CreatedAt { get; set; }
        public string PluginVersion { get; set; } = string.Empty;
        public List<AdminDefaultsRow> AdminDefaults { get; set; } = new();
        public List<MessageRow> Messages { get; set; } = new();
    }

    public sealed record RestoreOutcome(bool Success, string Error);
}

/// <summary>
/// The dashboard's YAML editor format: the WHOLE plugin configuration as
/// camelCase YAML. Serialization and parse-validate are admin service logic;
/// persisting the round-trip stays a plugin-lifecycle concern at the caller.
/// </summary>
public static class ConfigYaml
{
    public static string Serialize(PluginConfiguration config)
        => new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build()
            .Serialize(config);

    /// <summary>Parses admin-edited YAML into a full configuration (unmatched keys ignored); throws on invalid YAML.</summary>
    public static PluginConfiguration Parse(string yaml)
        => new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build()
            .Deserialize<PluginConfiguration>(yaml);
}
