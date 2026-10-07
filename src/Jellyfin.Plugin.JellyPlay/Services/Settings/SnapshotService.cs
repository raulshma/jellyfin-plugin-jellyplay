using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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

    /// <summary>Decodes one entry's value back to its stored bytes.</summary>
    public static byte[] DecodeValue(SnapshotEntry entry) => Convert.FromBase64String(entry.ValueBase64);
}
