using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyPlay.Api;
using Jellyfin.Plugin.JellyPlay.Configuration;
using Jellyfin.Plugin.JellyPlay.Services.Admin;
using Jellyfin.Plugin.JellyPlay.Services.Devices;
using Jellyfin.Plugin.JellyPlay.Services.Shared;
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
    private readonly TimeProvider _clock;

    public SyncInsightsService(JellyPlayDatabase db, Func<SyncConfig> config, TimeProvider? clock = null)
    {
        _db = db;
        _config = config;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Caller's sync status: change-log head, footprint vs quotas, namespaces, per-device fold.</summary>
    public SyncStatusResponse GetStatus(string userId)
    {
        var config = _config();
        var bundle = _db.GetSyncStatusBundle(userId);
        return new SyncStatusResponse(
            Head: bundle.Head,
            Keys: bundle.KeyCount,
            Bytes: bundle.TotalBytes,
            QuotaBytes: config.MaxUserBytes,
            QuotaKeys: config.MaxKeysPerUser,
            HistoryRetentionDays: config.HistoryRetentionDays,
            Namespaces: bundle.Namespaces
                .Select(row => new SyncNamespaceInfo(row.Ns, row.Keys, row.Bytes))
                .ToList(),
            PerDevice: bundle.PerDevice
                .Select(row => new SyncDeviceSummary(row.DeviceId, row.LastSyncAt, row.LastOp))
                .ToList());
    }

    /// <summary>The caller's recorded sync operations, newest-first; seq is the sync_history Id.</summary>
    public SyncHistoryResponse GetHistory(string userId, long? since, int limit)
    {
        var clamped = Paged.Clamp(limit, DefaultHistoryLimit, MaxHistoryLimit);
        var entries = _db.GetSyncHistory(userId, since ?? 0, clamped)
            .Select(ToEntryDto)
            .ToList();
        return new SyncHistoryResponse(entries);
    }

    /// <summary>
    /// Cross-user overview for admins: every user with settings rows, names
    /// resolved through <paramref name="userName"/> (falling back to the raw
    /// id when the user is unknown to the host — <see cref="AdminUsers.DisplayName"/>).
    /// The footprint/sync rollup merge resolves in ONE store composite.
    /// </summary>
    public AdminSyncOverviewResponse GetAdminOverview(Func<Guid, string?> userName)
    {
        var users = _db.GetSyncAdminOverviewRows()
            .Select(row => new AdminSyncUserRow(
                row.UserId,
                AdminUsers.DisplayName(row.UserId, userName),
                row.Keys,
                row.Bytes,
                row.LastSyncAt,
                row.DeviceCount))
            .ToList();

        return new AdminSyncOverviewResponse(users);
    }

    /// <summary>
    /// The per-key diff of one of the caller's recorded operations: the
    /// change-log rows in (FromSeq, ToSeq], newest-first. Returns null when
    /// the caller owns no history row with that seq (the endpoint 404s).
    /// Since tombstones (schema v7) a reset/wipe range carries the deleted
    /// keys; only rows with a zero-width or missing range (pre-v7 resets,
    /// no-op operations) carry an empty list. The row and its range resolve
    /// in ONE store composite.
    /// </summary>
    public SyncHistoryKeysResponse? GetHistoryKeys(string userId, long seq, int limit = DefaultKeysLimit)
    {
        var clamped = Paged.Clamp(limit, DefaultKeysLimit, MaxKeysLimit);
        var entry = _db.GetSyncHistoryEntryWithKeys(userId, seq, clamped);
        return entry is null
            ? null
            : new SyncHistoryKeysResponse(
                entry.Row.Id,
                entry.Row.Op,
                entry.Keys.Select(key => new SyncHistoryKeyDto(key.Ns, key.Key, key.UpdatedAt)).ToList());
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
        var clamped = Paged.Clamp(limit, DefaultAuditLimit, MaxAuditLimit);
        var history = _db.GetSyncHistoryWithKeys(userId, 0, clamped, MaxKeysLimit)
            .Select(row => new AuditEntryDto(
                row.Row.Id,
                row.Row.Ts,
                row.Row.DeviceId,
                row.Row.Op,
                row.Row.KeysApplied,
                row.Row.KeysRejected,
                SyncRejectsCodec.Decode(row.Row.RejectsJson),
                row.Row.FromSeq,
                row.Row.ToSeq,
                row.Keys.Select(entry => new SyncHistoryKeyDto(entry.Ns, entry.Key, entry.UpdatedAt)).ToList()))
            .ToList();

        return new AuditExportResponse(userId, _clock.GetUtcNow().ToUnixTimeMilliseconds(), history);
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
                Services.Push.PushEligibility.ParseCaps(row.CapsJson),
                row.Revoked))
            .ToList();
        return new AdminUserDrilldownResponse(userId, AdminUsers.DisplayName(userId, userName), status, devices);
    }

    private static SyncHistoryEntryDto ToEntryDto(SyncHistoryRow row)
        => new(row.Id, row.Ts, row.DeviceId, row.Op, row.KeysApplied, row.KeysRejected, SyncRejectsCodec.Decode(row.RejectsJson), row.FromSeq, row.ToSeq);
}
