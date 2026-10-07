using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Api;
using Jellyfin.Plugin.JellyPlay.Configuration;
using Jellyfin.Plugin.JellyPlay.Realtime;
using Jellyfin.Plugin.JellyPlay.Services.Admin;
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

    public SettingsService(JellyPlayDatabase db, SseHub hub, Func<SyncConfig> config, ILogger<SettingsService> logger, TimeProvider? clock = null)
    {
        _db = db;
        _hub = hub;
        _config = config;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public JellyPlayDatabase.Quotas Quotas => new(_config().MaxKeyBytes, _config().MaxUserBytes, _config().MaxKeysPerUser);

    public SettingsSnapshotResponse GetAll(string userId, string profile)
    {
        var rows = _db.GetSettings(userId, profile);
        return ToSnapshot(userId, profile, _db.GetChangeLogHead(userId), rows);
    }

    public SettingsSnapshotResponse GetChanged(string userId, string profile, long since, string? deviceId)
    {
        var rows = _db.GetChangedSettings(userId, since)
            .Where(row => string.Equals(row.Profile, profile, StringComparison.Ordinal))
            .ToList();
        // Delta pulls are recorded (full GET settings reads are not): the
        // history shows which device observed which change. The recorded
        // range is (since, head] — exactly what the pull served (head can
        // only advance between the read and this line, never shrink), so a
        // zero-key pull still gets its (empty) range.
        var head = _db.GetChangeLogHead(userId);
        RecordOperation(userId, deviceId ?? string.Empty, "pull", rows.Count, 0, 0, null, since, head);
        return ToSnapshot(userId, profile, head, rows);
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

        // The push's diff range: change-log head before/after the batch —
        // (fromSeq, toSeq] is exactly what this batch appended (plus any
        // concurrent write that landed inside the window).
        var headBefore = _db.GetChangeLogHead(userId);
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

        // Approximate byte size of the batch: the serialized length of the keys that applied.
        var appliedSet = result.Applied.Select(a => (a.Ns, a.Key)).ToHashSet();
        var appliedBytes = serialized
            .Where(pair => appliedSet.Contains((pair.Key.Ns, pair.Key.Key)))
            .Sum(pair => (long)pair.Value.Length);
        var head = _db.GetChangeLogHead(userId);
        RecordOperation(userId, deviceId, "push", result.Applied.Count, result.Rejected.Count, appliedBytes, BuildRejectsJson(result.Rejected), headBefore, head);

        return new SettingsBatchResponse
        {
            Head = head,
            Applied = applied,
            Rejected = rejected
        };
    }

    public void ResetNamespace(string userId, string? profile, string ns, string? deviceId)
    {
        var deleted = _db.DeleteNamespace(userId, profile ?? JellyPlayDatabase.BaseProfile, ns);
        // Resets get a zero-width range at the head after the delete: the
        // change log records no deletions, so there is no per-key diff — the
        // keys endpoint renders these rows as "namespace reset" with an empty
        // key list.
        var headAfter = _db.GetChangeLogHead(userId);
        RecordOperation(userId, deviceId ?? string.Empty, "reset", deleted, 0, 0, null, headAfter, headAfter);
        var payload = JsonSerializer.Serialize(new
        {
            type = "settings.reset",
            profile = profile ?? JellyPlayDatabase.BaseProfile,
            ns,
            ts = _clock.GetTimestamp()
        });
        _hub.PublishToUser("settings", userId, "settings.reset", payload);
    }

    // ------------------------------------------------------------------
    // Sync history recording (observability; never fails the operation)
    // ------------------------------------------------------------------

    /// <summary>RejectsJson is capped at this many entries per recorded operation.</summary>
    internal const int MaxRecordedRejects = 10;

    private static readonly System.Text.Json.JsonSerializerOptions RejectJsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
    };

    /// <summary>Capped <c>[{ns,key,reason}]</c> JSON for a recorded operation; null when nothing was rejected.</summary>
    internal static string? BuildRejectsJson(IReadOnlyList<RejectedSetting> rejected)
    {
        if (rejected.Count == 0)
        {
            return null;
        }

        return JsonSerializer.Serialize(
            rejected.Take(MaxRecordedRejects).Select(r => new SyncRejectDto(r.Ns, r.Key, r.Reason)),
            RejectJsonOptions);
    }

    /// <summary>
    /// Appends one sync_history row (with the operation's change-log range:
    /// push = head before/after the batch, pull = the requested since cursor
    /// through the served head, reset = a zero-width range at the head after).
    /// Best-effort by contract: an observability write must never fail (or
    /// even slow-path-fail) the sync operation it observes, so every exception
    /// is swallowed with a warning.
    /// </summary>
    private void RecordOperation(string userId, string deviceId, string op, int keysApplied, int keysRejected, long bytes, string? rejectsJson, long? fromSeq = null, long? toSeq = null)
    {
        try
        {
            _db.InsertSyncHistory(
                userId,
                deviceId,
                op,
                keysApplied,
                keysRejected,
                bytes,
                rejectsJson,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                fromSeq,
                toSeq);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JellyPlay sync-history recording failed (user {UserId}, op {Op}) — ignoring", userId, op);
        }
    }

    /// <summary>
    /// Merges base settings with a device-profile overlay (profile wins) and then
    /// applies tri-state admin defaults: forced overrides everything, suggested
    /// fills keys the user has not set. This is what a client booting on a new
    /// device asks for. The response additionally carries the ADDITIVE
    /// <c>modes</c> map: the tri-state provenance per key after the user-scope
    /// merge (forced / suggested / unset — see <see cref="SettingsSnapshotResponse.Modes"/>).
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

        var defaults = GetDefaultsMerged(
            userId,
            merged.Keys.Select(id => DefaultsEnvelope.Join(id.Item1, id.Item2)).ToHashSet(StringComparer.Ordinal));
        var modes = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var entry in defaults)
        {
            merged[(entry.Ns, entry.Key)] = DefaultsToRow(userId, profile, entry.Ns, entry.Key, entry.Value);
            modes[DefaultModeKey(entry.Ns, entry.Key)] = entry.Mode == AdminDefaultMode.Forced ? "forced" : "suggested";
        }

        // Every remaining resolved key is user-owned (base or profile overlay):
        // "unset" — including a suggested default that lost to an existing
        // user value (the user's value wins, so the provenance is the user's).
        foreach (var row in merged.Values)
        {
            modes.TryAdd(DefaultModeKey(row.Ns, row.Key), "unset");
        }

        var response = ToSnapshot(userId, profile, _db.GetChangeLogHead(userId), merged.Values.ToList());
        response.Modes = modes;
        return response;
    }

    private static string DefaultModeKey(string ns, string key) => DefaultsEnvelope.Join(ns, key);

    public void SetDeviceProfile(string userId, string profile, string? deviceId, IReadOnlyList<SettingsWriteDto> writes)
        => ApplyBatch(userId, profile, deviceId, writes);

    // ------------------------------------------------------------------
    // Admin defaults
    // ------------------------------------------------------------------

    public void SetAdminDefaults(string scope, JsonElement payload)
    {
        var problems = ValidateAgainstCatalog(payload);
        if (problems.Count > 0)
        {
            throw new SettingsCatalogValidationException(problems);
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        _db.SetAdminDefaults(scope, bytes, _clock.GetTimestamp());
    }

    /// <summary>
    /// Known keys are validated against the client settings catalog so the
    /// dashboard cannot persist values the client would reject (wrong type,
    /// out of range, off-enum). Unknown keys pass — the namespace is
    /// client-defined and forward-compatible.
    /// </summary>
    internal static List<string> ValidateAgainstCatalog(JsonElement payload)
    {
        var problems = new List<string>();
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return problems;
        }

        foreach (var property in payload.EnumerateObject())
        {
            var (ns, key) = DefaultsEnvelope.Split(property.Name);
            if (ns.Length == 0 || key.Length == 0)
            {
                continue;
            }

            var descriptor = ClientSettingsCatalog.Find(ns, key);
            if (descriptor is null
                || property.Value.ValueKind != JsonValueKind.Object
                || !property.Value.TryGetProperty("value", out var value))
            {
                continue;
            }

            var problem = ClientSettingsCatalog.ValidateValue(descriptor, value);
            if (problem is not null)
            {
                problems.Add(problem);
            }
        }

        return problems;
    }

    public JsonElement? GetAdminDefaultsRaw(string scope)
    {
        var row = _db.GetAdminDefaults(scope);
        return row is null ? null : JsonSerializer.Deserialize<JsonElement>(row.Payload);
    }

    /// <summary>
    /// The tri-state defaults that apply to one user after the scope merge and
    /// the gap filter — forced ones always, suggested ones only for keys the
    /// user has not set. The precedence itself lives in
    /// <see cref="DefaultsEnvelope.MergeForRead"/> (the one parser/merger, shared with
    /// the admin write side).
    /// </summary>
    private List<ResolvedDefault> GetDefaultsMerged(string userId, IReadOnlySet<string> existingKeys)
        => DefaultsEnvelope.MergeForRead(GetAdminDefaultsRaw(GlobalDefaultsScope), GetAdminDefaultsRaw(userId), existingKeys);

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
