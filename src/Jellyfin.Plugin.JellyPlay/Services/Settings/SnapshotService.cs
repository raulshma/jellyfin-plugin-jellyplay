using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.JellyPlay.Api;
using Jellyfin.Plugin.JellyPlay.Configuration;
using Jellyfin.Plugin.JellyPlay.Storage;
using Jellyfin.Plugin.JellyPlay.Storage.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyPlay.Services.Settings;

/// <summary>
/// One key inside a snapshot payload. Values are stored base64 — the payload
/// must round-trip opaque blobs byte-exactly (the server never interprets
/// settings content).
/// </summary>
public sealed record SnapshotEntry(
    string Profile,
    string Ns,
    string Key,
    int SchemaVersion,
    long UpdatedAt,
    string DeviceId,
    string ValueBase64);

/// <summary>
/// Restore points: captures the user's full settings store (all profiles)
/// into user_snapshots and serves the list/lookup halves of the snapshot
/// routes. Rolling keep-last is enforced at insert; age retention is the
/// daily prune's job. Capture is best-effort by contract — callers snapshot
/// BEFORE destructive operations (admin push, cross-profile copy) and a
/// failed capture must never fail the operation it protects, so Create
/// swallows storage faults with a warning.
/// </summary>
public sealed class SnapshotService
{
    /// <summary>Rolling window enforced at insert time (besides the age-based prune).</summary>
    public const int KeepLast = 5;

    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly JellyPlayDatabase _db;
    private readonly Func<SyncConfig> _config;
    private readonly TimeProvider _clock;
    private readonly ILogger<SnapshotService>? _logger;

    public SnapshotService(JellyPlayDatabase db, Func<SyncConfig> config, TimeProvider? clock = null, ILogger<SnapshotService>? logger = null)
    {
        _db = db;
        _config = config;
        _clock = clock ?? TimeProvider.System;
        _logger = logger;
    }

    /// <summary>Captures the user's current settings (all profiles); returns the snapshot id, or null when the capture failed (never throws).</summary>
    public long? Create(string userId, string origin)
    {
        try
        {
            var rows = _db.GetAllSettingsRows(userId);
            var payload = JsonSerializer.SerializeToUtf8Bytes(
                rows.Select(row => new SnapshotEntry(
                    row.Profile,
                    row.Ns,
                    row.Key,
                    row.SchemaVersion,
                    row.UpdatedAt,
                    row.DeviceId,
                    Convert.ToBase64String(row.Value))).ToList(),
                PayloadOptions);
            return _db.InsertSnapshot(
                userId,
                payload,
                _clock.GetUtcNow().ToUnixTimeMilliseconds(),
                origin,
                rows.Count,
                rows.Sum(row => (long)row.Value.Length),
                KeepLast);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "JellyPlay snapshot capture failed (user {UserId}, origin {Origin}) — continuing without a restore point", userId, origin);
            return null;
        }
    }

    /// <summary>The caller's snapshots, newest-first (metadata only).</summary>
    public IReadOnlyList<SnapshotRow> List(string userId) => _db.GetSnapshots(userId);

    /// <summary>One snapshot with its decoded payload — null when the caller owns no such id.</summary>
    public (SnapshotRow Row, IReadOnlyList<SnapshotEntry> Entries)? Get(string userId, long id)
    {
        var stored = _db.GetSnapshot(userId, id);
        if (stored is null)
        {
            return null;
        }

        try
        {
            var entries = JsonSerializer.Deserialize<List<SnapshotEntry>>(stored.Value.Payload, PayloadOptions) ?? new List<SnapshotEntry>();
            return (stored.Value.Row, entries);
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "JellyPlay snapshot {Id} payload is corrupt — refusing to restore", id);
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Restore / export / import orchestration (the SnapshotService seam
    // over the SnapshotOrchestrator kernel)
    // ------------------------------------------------------------------

    /// <summary>
    /// Restores one of the caller's snapshots: a diff-first tombstone pass
    /// (only keys present now but absent from the snapshot are tombstoned)
    /// followed by the snapshot re-applied per profile, server-stamped past
    /// the newest overlapping live row so the restore provably wins LWW.
    /// The re-apply rides the ordinary batch pipeline through
    /// <paramref name="apply"/> (service-level routing — the store's batch
    /// signatures do not move), so the change log, the anchored SSE event
    /// and history recording happen per batch exactly as before: single
    /// history entry + single SSE event semantics per batch are preserved and
    /// the wire contract does not move. The returned response folds every
    /// profile's re-apply batch (head = the final change-log head).
    /// The create-before-destructive-write guard lives here: the snapshot is
    /// looked up (ownership and corruption checked) BEFORE any destructive
    /// write is issued, so a foreign, unknown or corrupt id writes nothing.
    /// Returns null when the id is not the caller's own intact snapshot.
    /// </summary>
    public SettingsBatchResponse? Restore(
        string userId,
        long snapshotId,
        ApplySettingsBatch apply,
        Func<IReadOnlyList<SettingRow>> getCurrentRows,
        long serverNow)
    {
        var stored = Get(userId, snapshotId);
        if (stored is null)
        {
            return null;
        }

        var snapshotKeys = SnapshotOrchestrator.SnapshotKeySet(stored.Value.Entries);
        var current = getCurrentRows();
        var restoreStamp = SnapshotOrchestrator.RestoreStamp(current, snapshotKeys, serverNow);

        // Tombstone only the drift the snapshot cannot overwrite: keys that
        // exist now but are absent from the snapshot. One batch per profile
        // that has such keys, so each 'del' change-log row carries the profile
        // its key belongs to.
        foreach (var profileGroup in SnapshotOrchestrator.DriftGroups(current, snapshotKeys))
        {
            apply(
                userId,
                SnapshotOrchestrator.ApplyProfile(profileGroup.Key),
                SnapshotOrchestrator.RestoreDeviceId,
                SnapshotOrchestrator.TombstoneWrites(profileGroup, serverNow),
                null);
        }

        var response = new SettingsBatchResponse();
        foreach (var profileGroup in SnapshotOrchestrator.RestoreGroups(stored.Value.Entries))
        {
            SnapshotOrchestrator.FoldBatch(
                response,
                apply(
                    userId,
                    SnapshotOrchestrator.ApplyProfile(profileGroup.Key),
                    SnapshotOrchestrator.RestoreDeviceId,
                    SnapshotOrchestrator.RestoreWrites(profileGroup, restoreStamp),
                    // The restore is server-initiated and bounded by its own
                    // server stamp, not the client-skew clamp.
                    restoreStamp));
        }

        return response;
    }

    /// <summary>
    /// The portable export bundle: every stored profile's rows plus the
    /// resolved modes maps and the settings-catalog stamp. The row and mode
    /// projections arrive as delegates (service-level routing): the snapshot
    /// fold and the resolve merge keep their locality, this seam only
    /// assembles. Pure read: nothing is written, no history entry.
    /// </summary>
    public SettingsExportBundle Export(
        string userId,
        long serverNow,
        Func<string, IReadOnlyList<SettingsEntryDto>> settingsForProfile,
        Func<string, IReadOnlyDictionary<string, string>> modesForProfile)
        => SnapshotOrchestrator.BuildExportBundle(
            serverNow,
            _db.GetDistinctSettingProfiles(userId),
            settingsForProfile,
            modesForProfile);

    /// <summary>
    /// Imports a bundle: every row is re-applied through the ordinary batch
    /// pipeline (via <paramref name="apply"/>) with a server-now timestamp —
    /// it beats anything older than now (per LWW) but never clobbers a
    /// legitimately newer change — and per-profile batching preserved, so
    /// history recording and SSE fan-out happen per batch exactly as before.
    /// </summary>
    public SettingsBatchResponse Import(
        string userId,
        string? deviceId,
        SettingsExportBundle bundle,
        long serverNow,
        ApplySettingsBatch apply)
    {
        var resolvedDeviceId = deviceId ?? SnapshotOrchestrator.ImportDeviceIdFallback;
        var response = new SettingsBatchResponse();
        foreach (var profile in bundle.Profiles)
        {
            var writes = SnapshotOrchestrator.ImportWrites(profile.Settings, serverNow, resolvedDeviceId);
            if (writes.Count == 0)
            {
                continue;
            }

            SnapshotOrchestrator.FoldBatch(
                response,
                apply(userId, profile.Profile, resolvedDeviceId, writes, null));
        }

        return response;
    }
}
