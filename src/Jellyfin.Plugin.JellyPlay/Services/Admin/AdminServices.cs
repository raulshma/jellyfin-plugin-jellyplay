using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Services.Settings;
using Jellyfin.Plugin.JellyPlay.Storage;
using Jellyfin.Plugin.JellyPlay.Storage.Models;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

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
            var userDefaults = _settings.GetAdminDefaultsRaw(target);
            var merged = MergeDefaults(globalDefaults, userDefaults);
            pushed += PushMerged(target, merged);
        }

        return new PushOutcome(targets.Count, pushed);
    }

    public sealed record PushOutcome(int Users, int KeysPushed);

    private static Dictionary<string, JsonElement> MergeDefaults(JsonElement? global, JsonElement? perUser)
    {
        var merged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in EnumerateDefaults(global))
        {
            merged[key] = value;
        }

        foreach (var (key, value) in EnumerateDefaults(perUser))
        {
            merged[key] = value;
        }

        return merged;
    }

    private static IEnumerable<(string Key, JsonElement Value)> EnumerateDefaults(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } element)
        {
            yield break;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                yield return (property.Name, property.Value);
            }
        }
    }

    private int PushMerged(string userId, Dictionary<string, JsonElement> merged)
    {
        var baseSnapshot = _settings.GetAll(userId, JellyPlayDatabase.BaseProfile);
        var existing = baseSnapshot.Settings.ToDictionary(entry => entry.Ns + "/" + entry.Key, entry => entry);

        var writes = new List<Api.SettingsWriteDto>();
        foreach (var (compositeKey, entry) in merged)
        {
            var separator = compositeKey.IndexOf('/');
            if (separator <= 0)
            {
                continue;
            }

            var mode = entry.TryGetProperty("mode", out var modeElement) ? modeElement.GetString() : null;
            if (mode is not ("forced" or "suggested") || !entry.TryGetProperty("value", out var value))
            {
                continue;
            }

            var hasExisting = existing.TryGetValue(compositeKey, out var current);
            if (mode == "forced" || !hasExisting)
            {
                writes.Add(new Api.SettingsWriteDto
                {
                    Ns = compositeKey[..separator],
                    Key = compositeKey[(separator + 1)..],
                    SchemaVersion = hasExisting ? current.SchemaVersion : 1,
                    UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Value = value
                });
            }
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
            PluginVersion = typeof(JellyPlayPlugin).Assembly.GetName().Version?.ToString() ?? "1.0.0",
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
