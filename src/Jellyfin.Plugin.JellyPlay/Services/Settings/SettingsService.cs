using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Api;
using Jellyfin.Plugin.JellyPlay.Configuration;
using Jellyfin.Plugin.JellyPlay.Realtime;
using Jellyfin.Plugin.JellyPlay.Storage;
using Jellyfin.Plugin.JellyPlay.Storage.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyPlay.Services.Settings;

/// <summary>
/// Settings-sync business logic: batch LWW application, changed-since deltas,
/// resolved-profile merge (base + device profile + tri-state admin defaults)
/// and SSE fan-out on change.
/// </summary>
public sealed class SettingsService
{
    public const string GlobalDefaultsScope = "global";

    private readonly JellyPlayDatabase _db;
    private readonly SseHub _hub;
    private readonly Func<SyncConfig> _config;
    private readonly ILogger<SettingsService> _logger;
    private readonly TimeProvider _clock;

    public SettingsService(JellyPlayDatabase db, SseHub hub, Func<SyncConfig> config, ILogger<SettingsService> logger)
    {
        _db = db;
        _hub = hub;
        _config = config;
        _logger = logger;
        _clock = TimeProvider.System;
    }

    public JellyPlayDatabase.Quotas Quotas => new(_config().MaxKeyBytes, _config().MaxUserBytes, _config().MaxKeysPerUser);

    public SettingsSnapshotResponse GetAll(string userId, string profile)
    {
        var rows = _db.GetSettings(userId, profile);
        return ToSnapshot(userId, profile, _db.GetChangeLogHead(userId), rows);
    }

    public SettingsSnapshotResponse GetChanged(string userId, string profile, long since)
    {
        var rows = _db.GetChangedSettings(userId, since)
            .Where(row => string.Equals(row.Profile, profile, StringComparison.Ordinal))
            .ToList();
        return ToSnapshot(userId, profile, _db.GetChangeLogHead(userId), rows);
    }

    public SettingsBatchResponse ApplyBatch(string userId, string? profile, string? deviceId, IReadOnlyList<SettingsWriteDto> writes)
    {
        profile ??= JellyPlayDatabase.BaseProfile;
        deviceId ??= "unknown";

        var mapped = new List<SettingWrite>(writes.Count);
        var serialized = new Dictionary<SettingsWriteDto, byte[]>(writes.Count);
        foreach (var write in writes)
        {
            if (string.IsNullOrWhiteSpace(write.Ns) || string.IsNullOrWhiteSpace(write.Key))
            {
                continue;
            }

            var bytes = write.Value.ValueKind is JsonValueKind.Undefined
                ? Array.Empty<byte>()
                : JsonSerializer.SerializeToUtf8Bytes(write.Value);
            serialized[write] = bytes;
            mapped.Add(new SettingWrite(write.Ns, write.Key, write.SchemaVersion, write.UpdatedAt, deviceId, bytes));
        }

        var result = _db.UpsertSettings(userId, profile, mapped, Quotas);

        var applied = result.Applied
            .Select(a => new AppliedSettingDto(a.Ns, a.Key, a.UpdatedAt, a.Seq))
            .ToList();
        var rejected = result.Rejected
            .Select(r => new RejectedSettingDto(r.Ns, r.Key, r.Reason))
            .ToList();

        if (applied.Count > 0)
        {
            var payload = JsonSerializer.Serialize(new
            {
                type = "settings.changed",
                profile,
                ns = string.Join(',', applied.Select(a => a.Ns).Distinct()),
                count = applied.Count,
                ts = _clock.GetTimestamp()
            });
            _hub.PublishToUser("settings", userId, "settings.changed", payload);
        }

        return new SettingsBatchResponse
        {
            Head = _db.GetChangeLogHead(userId),
            Applied = applied,
            Rejected = rejected
        };
    }

    public void ResetNamespace(string userId, string? profile, string ns)
    {
        _db.DeleteNamespace(userId, profile ?? JellyPlayDatabase.BaseProfile, ns);
        var payload = JsonSerializer.Serialize(new
        {
            type = "settings.reset",
            profile = profile ?? JellyPlayDatabase.BaseProfile,
            ns,
            ts = _clock.GetTimestamp()
        });
        _hub.PublishToUser("settings", userId, "settings.reset", payload);
    }

    /// <summary>
    /// Merges base settings with a device-profile overlay (profile wins) and then
    /// applies tri-state admin defaults: forced overrides everything, suggested
    /// fills keys the user has not set. This is what a client booting on a new
    /// device asks for.
    /// </summary>
    public SettingsSnapshotResponse ResolveProfile(string userId, string profile)
    {
        var baseRows = _db.GetSettings(userId, JellyPlayDatabase.BaseProfile);
        var overlayRows = profile == JellyPlayDatabase.BaseProfile
            ? new List<SettingRow>()
            : _db.GetSettings(userId, profile);

        var merged = new Dictionary<(string Ns, string Key), SettingRow>();
        foreach (var row in baseRows)
        {
            merged[(row.Ns, row.Key)] = row;
        }

        foreach (var row in overlayRows)
        {
            merged[(row.Ns, row.Key)] = row;
        }

        var defaults = GetDefaultsMerged(userId);
        foreach (var (ns, key, entry) in defaults.Forced)
        {
            merged[(ns, key)] = DefaultsToRow(userId, profile, ns, key, entry);
        }

        foreach (var (ns, key, entry) in defaults.Suggested)
        {
            if (!merged.ContainsKey((ns, key)))
            {
                merged[(ns, key)] = DefaultsToRow(userId, profile, ns, key, entry);
            }
        }

        return ToSnapshot(userId, profile, _db.GetChangeLogHead(userId), merged.Values.ToList());
    }

    public void SetDeviceProfile(string userId, string profile, string? deviceId, IReadOnlyList<SettingsWriteDto> writes)
        => ApplyBatch(userId, profile, deviceId, writes);

    // ------------------------------------------------------------------
    // Admin defaults
    // ------------------------------------------------------------------

    public void SetAdminDefaults(string scope, JsonElement payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        _db.SetAdminDefaults(scope, bytes, _clock.GetTimestamp());
    }

    public JsonElement? GetAdminDefaultsRaw(string scope)
    {
        var row = _db.GetAdminDefaults(scope);
        return row is null ? null : JsonSerializer.Deserialize<JsonElement>(row.Payload);
    }

    private (List<(string Ns, string Key, JsonElement Value)> Forced, List<(string Ns, string Key, JsonElement Value)> Suggested) GetDefaultsMerged(string userId)
    {
        var forced = new List<(string, string, JsonElement)>();
        var suggested = new List<(string, string, JsonElement)>();

        foreach (var scope in new[] { GlobalDefaultsScope, userId })
        {
            var raw = GetAdminDefaultsRaw(scope);
            if (raw is null || raw.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var property in raw.Value.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object
                    || !property.Value.TryGetProperty("mode", out var modeElement))
                {
                    continue;
                }

                var mode = modeElement.GetString();
                // User scope wins over global scope for the same key: the second
                // occurrence replaces the first via remove+add.
                forced.RemoveAll(f => f.Item1 == NsOf(property.Name) && f.Item2 == KeyOf(property.Name));
                suggested.RemoveAll(s => s.Item1 == NsOf(property.Name) && s.Item2 == KeyOf(property.Name));

                if (mode == "forced" && property.Value.TryGetProperty("value", out var forcedValue))
                {
                    forced.Add((NsOf(property.Name), KeyOf(property.Name), forcedValue));
                }
                else if (mode == "suggested" && property.Value.TryGetProperty("value", out var suggestedValue))
                {
                    suggested.Add((NsOf(property.Name), KeyOf(property.Name), suggestedValue));
                }
            }
        }

        return (forced, suggested);
    }

    private SettingRow DefaultsToRow(string userId, string profile, string ns, string key, JsonElement value)
        => new(
            userId,
            profile,
            ns,
            key,
            1,
            0,
            "admin-default",
            JsonSerializer.SerializeToUtf8Bytes(value));

    private static string NsOf(string compositeKey)
    {
        var index = compositeKey.IndexOf('/', StringComparison.Ordinal);
        return index < 0 ? compositeKey : compositeKey[..index];
    }

    private static string KeyOf(string compositeKey)
    {
        var index = compositeKey.IndexOf('/', StringComparison.Ordinal);
        return index < 0 ? string.Empty : compositeKey[(index + 1)..];
    }

    private SettingsSnapshotResponse ToSnapshot(string userId, string profile, long head, IReadOnlyList<SettingRow> rows)
    {
        var response = new SettingsSnapshotResponse
        {
            Head = head,
            Profile = profile
        };

        foreach (var row in rows)
        {
            JsonElement value;
            try
            {
                value = row.Value.Length == 0
                    ? JsonDocument.Parse("null").RootElement.Clone()
                    : JsonDocument.Parse(row.Value).RootElement.Clone();
            }
            catch (JsonException)
            {
                _logger.LogWarning("Skipping corrupt settings blob for user {UserId} key {Ns}/{Key}", userId, row.Ns, row.Key);
                continue;
            }

            response.Settings.Add(new SettingsEntryDto
            {
                Ns = row.Ns,
                Key = row.Key,
                SchemaVersion = row.SchemaVersion,
                UpdatedAt = row.UpdatedAt,
                DeviceId = row.DeviceId,
                Profile = row.Profile,
                Value = value
            });
        }

        return response;
    }
}
