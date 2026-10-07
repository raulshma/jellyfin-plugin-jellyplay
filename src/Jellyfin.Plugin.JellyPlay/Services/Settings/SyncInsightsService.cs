using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.JellyPlay.Api;
using Jellyfin.Plugin.JellyPlay.Configuration;
using Jellyfin.Plugin.JellyPlay.Helpers;
using Jellyfin.Plugin.JellyPlay.Services.Admin;
using Jellyfin.Plugin.JellyPlay.Services.Devices;
using Jellyfin.Plugin.JellyPlay.Storage;
using Jellyfin.Plugin.JellyPlay.Storage.Models;

namespace Jellyfin.Plugin.JellyPlay.Services.Settings;

/// <summary>
/// Read-side composition for sync observability: user status (footprint vs
/// quotas, namespace rollups, per-device fold), the per-user history query and
/// the admin cross-user overview. Pure aggregation over the database — no host
/// dependencies beyond the config accessor, so it is unit-testable.
/// </summary>
public sealed class SyncInsightsService
{
    /// <summary>History page size bounds: ?limit= defaults to 50, at most 200.</summary>
    public const int DefaultHistoryLimit = 50;
    public const int MaxHistoryLimit = 200;

    /// <summary>Per-key diff bounds: ?limit= defaults to 200, clamped 1..200.</summary>
    public const int DefaultKeysLimit = 200;
    public const int MaxKeysLimit = 200;

    private readonly JellyPlayDatabase _db;
    private readonly Func<SyncConfig> _config;

    public SyncInsightsService(JellyPlayDatabase db, Func<SyncConfig> config)
    {
        _db = db;
        _config = config;
    }

    /// <summary>Caller's sync status: change-log head, footprint vs quotas, namespaces, per-device fold.</summary>
    public SyncStatusResponse GetStatus(string userId)
    {
        var config = _config();
        var (keys, bytes) = _db.GetUserFootprint(userId);
        return new SyncStatusResponse(
            Head: _db.GetChangeLogHead(userId),
            Keys: keys,
            Bytes: bytes,
            QuotaBytes: config.MaxUserBytes,
            QuotaKeys: config.MaxKeysPerUser,
            HistoryRetentionDays: config.HistoryRetentionDays,
            Namespaces: _db.GetNamespaceFootprints(userId)
                .Select(row => new SyncNamespaceInfo(row.Ns, row.Keys, row.Bytes))
                .ToList(),
            PerDevice: _db.GetLatestSyncPerDevice(userId)
                .Select(row => new SyncDeviceSummary(row.DeviceId, row.LastSyncAt, row.LastOp))
                .ToList());
    }

    /// <summary>The caller's recorded sync operations, newest-first; seq is the sync_history Id.</summary>
    public SyncHistoryResponse GetHistory(string userId, long? since, int limit)
    {
        var clamped = RequestLimits.Clamp(limit, DefaultHistoryLimit, MaxHistoryLimit);
        var entries = _db.GetSyncHistory(userId, since ?? 0, clamped)
            .Select(ToEntryDto)
            .ToList();
        return new SyncHistoryResponse(entries);
    }

    /// <summary>
    /// Cross-user overview for admins: every user with settings rows, names
    /// resolved through <paramref name="userName"/> (falling back to the raw
    /// id when the user is unknown to the host — <see cref="AdminUsers.DisplayName"/>).
    /// </summary>
    public AdminSyncOverviewResponse GetAdminOverview(Func<Guid, string?> userName)
    {
        var summaries = _db.GetSyncSummariesByUser()
            .GroupBy(row => row.UserId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var users = _db.GetUserFootprints()
            .Select(footprint =>
            {
                summaries.TryGetValue(footprint.UserId, out var summary);
                return new AdminSyncUserRow(
                    footprint.UserId,
                    AdminUsers.DisplayName(footprint.UserId, userName),
                    footprint.Keys,
                    footprint.Bytes,
                    summary?.LastSyncAt,
                    summary?.DeviceCount ?? 0);
            })
            .ToList();

        return new AdminSyncOverviewResponse(users);
    }

    /// <summary>
    /// The per-key diff of one of the caller's recorded operations: the
    /// change-log rows in (FromSeq, ToSeq], newest-first. Returns null when
    /// the caller owns no history row with that seq (the endpoint 404s).
    /// Since tombstones (schema v7) a reset/wipe range carries the deleted
    /// keys; only rows with a zero-width or missing range (pre-v7 resets,
    /// no-op operations) carry an empty list.
    /// </summary>
    public SyncHistoryKeysResponse? GetHistoryKeys(string userId, long seq, int limit = DefaultKeysLimit)
    {
        var clamped = RequestLimits.Clamp(limit, DefaultKeysLimit, MaxKeysLimit);
        var row = _db.GetSyncHistoryRow(userId, seq);
        if (row is null)
        {
            return null;
        }

        if (row.FromSeq is null || row.ToSeq is null || row.FromSeq == row.ToSeq)
        {
            return new SyncHistoryKeysResponse(row.Id, row.Op, Array.Empty<SyncHistoryKeyDto>());
        }

        var keys = _db.GetChangeLogRange(userId, row.FromSeq.Value, row.ToSeq.Value, clamped)
            .Select(entry => new SyncHistoryKeyDto(entry.Ns, entry.Key, entry.UpdatedAt))
            .ToList();
        return new SyncHistoryKeysResponse(row.Id, row.Op, keys);
    }

    // ------------------------------------------------------------------
    // Admin drill-down + audit export (Phase 4, additive)
    // ------------------------------------------------------------------

    /// <summary>Audit export page size bounds: ?limit= defaults to 200, at most 1000 entries.</summary>
    public const int DefaultAuditLimit = 200;
    public const int MaxAuditLimit = 1000;

    /// <summary>
    /// The admin audit export for one user: the recorded operations
    /// newest-first, each with its per-key diff folded in (the change-log rows
    /// of its (fromSeq, toSeq] range — the same semantics the per-key diff
    /// endpoint serves). Pure read; the CSV rendering lives in
    /// <see cref="SyncAuditCsv"/> and the controller picks the wire format.
    /// </summary>
    public AuditExportResponse ExportAudit(string userId, int limit)
    {
        var clamped = RequestLimits.Clamp(limit, DefaultAuditLimit, MaxAuditLimit);
        var history = _db.GetSyncHistory(userId, 0, clamped)
            .Select(row =>
            {
                List<SyncRejectDto>? rejects = null;
                if (!string.IsNullOrEmpty(row.RejectsJson))
                {
                    try
                    {
                        rejects = JsonSerializer.Deserialize<List<SyncRejectDto>>(row.RejectsJson, RejectJsonOptions);
                    }
                    catch (JsonException)
                    {
                        // Same degradation as the history endpoint: counts stay authoritative.
                    }
                }

                IReadOnlyList<SyncHistoryKeyDto> keys = row.FromSeq is null || row.ToSeq is null || row.FromSeq == row.ToSeq
                    ? Array.Empty<SyncHistoryKeyDto>()
                    : _db.GetChangeLogRange(userId, row.FromSeq.Value, row.ToSeq.Value, MaxKeysLimit)
                        .Select(entry => new SyncHistoryKeyDto(entry.Ns, entry.Key, entry.UpdatedAt))
                        .ToList();
                return new AuditEntryDto(
                    row.Id,
                    row.Ts,
                    row.DeviceId,
                    row.Op,
                    row.KeysApplied,
                    row.KeysRejected,
                    rejects,
                    row.FromSeq,
                    row.ToSeq,
                    keys);
            })
            .ToList();

        return new AuditExportResponse(userId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), history);
    }

    /// <summary>
    /// One user's drill-down for the admin dashboard: the same status fold the
    /// user's own sync/status endpoint serves (footprint vs quotas,
    /// namespaces, per-device latest ops) plus the registry rows — push
    /// endpoint secrets stripped at the type level
    /// (<see cref="AdminDeviceRow"/> has no endpoint field).
    /// </summary>
    public AdminUserDrilldownResponse GetAdminUserDrilldown(string userId, Func<Guid, string?> userName)
    {
        var status = GetStatus(userId);
        var devices = _db.GetDevices(userId)
            .Select(row => new AdminDeviceRow(
                row.DeviceId,
                row.Name,
                row.Platform,
                row.AppVersion,
                row.LastSeen,
                row.Model,
                DeviceRegistryService.ParseCaps(row.CapsJson),
                row.Revoked))
            .ToList();
        return new AdminUserDrilldownResponse(userId, AdminUsers.DisplayName(userId, userName), status, devices);
    }

    private static SyncHistoryEntryDto ToEntryDto(SyncHistoryRow row)
    {
        List<SyncRejectDto>? rejects = null;
        if (!string.IsNullOrEmpty(row.RejectsJson))
        {
            try
            {
                rejects = JsonSerializer.Deserialize<List<SyncRejectDto>>(row.RejectsJson, RejectJsonOptions);
            }
            catch (JsonException)
            {
                // A malformed cap array degrades to "no rejects listed" — the
                // counts remain authoritative.
            }
        }

        return new SyncHistoryEntryDto(row.Id, row.Ts, row.DeviceId, row.Op, row.KeysApplied, row.KeysRejected, rejects, row.FromSeq, row.ToSeq);
    }

    private static readonly JsonSerializerOptions RejectJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

/// <summary>
/// RFC 4180 CSV rendering of an <see cref="AuditExportResponse"/> — one row
/// per per-key diff entry; operations without a usable range (pre-v7 rows,
/// no-op pushes, empty pulls) render as a single row with empty key columns
/// so every recorded operation still appears. Values are quoted only when
/// they must be (comma, quote, CR/LF); a literal quote doubles.
/// </summary>
public static class SyncAuditCsv
{
    /// <summary>The header row — column order is the contract for spreadsheet consumers.</summary>
    public const string Header = "seq,ts,deviceId,op,keysApplied,keysRejected,fromSeq,toSeq,ns,key,keyUpdatedAt";

    public static string Build(AuditExportResponse export)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine(Header);
        foreach (var entry in export.History)
        {
            if (entry.Keys.Count == 0)
            {
                AppendRow(builder, entry.Seq, entry.Ts, entry.DeviceId, entry.Op, entry.KeysApplied, entry.KeysRejected, entry.FromSeq, entry.ToSeq, string.Empty, string.Empty, null);
                continue;
            }

            foreach (var key in entry.Keys)
            {
                AppendRow(builder, entry.Seq, entry.Ts, entry.DeviceId, entry.Op, entry.KeysApplied, entry.KeysRejected, entry.FromSeq, entry.ToSeq, key.Ns, key.Key, key.UpdatedAt);
            }
        }

        return builder.ToString();
    }

    private static void AppendRow(
        System.Text.StringBuilder builder,
        long seq,
        long ts,
        string deviceId,
        string op,
        int keysApplied,
        int keysRejected,
        long? fromSeq,
        long? toSeq,
        string ns,
        string key,
        long? keyUpdatedAt)
    {
        builder.Append(seq).Append(',');
        builder.Append(ts).Append(',');
        builder.Append(Field(deviceId)).Append(',');
        builder.Append(Field(op)).Append(',');
        builder.Append(keysApplied).Append(',');
        builder.Append(keysRejected).Append(',');
        builder.Append(fromSeq is { } from ? from.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty).Append(',');
        builder.Append(toSeq is { } to ? to.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty).Append(',');
        builder.Append(Field(ns)).Append(',');
        builder.Append(Field(key)).Append(',');
        builder.Append(keyUpdatedAt is { } stamp
            ? stamp.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty);
        builder.Append("\r\n");
    }

    /// <summary>Minimal RFC 4180 quoting: quote only when required, doubling embedded quotes.</summary>
    private static string Field(string value)
    {
        if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
