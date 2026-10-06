using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.JellyPlay.Api;
using Jellyfin.Plugin.JellyPlay.Configuration;
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
        var clamped = Math.Clamp(limit == 0 ? DefaultHistoryLimit : limit, 1, MaxHistoryLimit);
        var entries = _db.GetSyncHistory(userId, since ?? 0, clamped)
            .Select(ToEntryDto)
            .ToList();
        return new SyncHistoryResponse(entries);
    }

    /// <summary>
    /// Cross-user overview for admins: every user with settings rows, names
    /// resolved through <paramref name="userName"/> (falling back to the raw
    /// id when the user is unknown to the host).
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
                var name = Guid.TryParse(footprint.UserId, out var guid) && guid != Guid.Empty
                    ? userName(guid)
                    : null;
                return new AdminSyncUserRow(
                    footprint.UserId,
                    string.IsNullOrWhiteSpace(name) ? footprint.UserId : name!,
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
    /// Reset rows — and any row with a zero-width or missing range — carry an
    /// empty key list: the change log records no deletions, so a reset is
    /// rendered as "namespace reset" without a key list.
    /// </summary>
    public SyncHistoryKeysResponse? GetHistoryKeys(string userId, long seq, int limit = DefaultKeysLimit)
    {
        var clamped = Math.Clamp(limit == 0 ? DefaultKeysLimit : limit, 1, MaxKeysLimit);
        var row = _db.GetSyncHistoryRow(userId, seq);
        if (row is null)
        {
            return null;
        }

        if (row.Op == "reset" || row.FromSeq is null || row.ToSeq is null || row.FromSeq == row.ToSeq)
        {
            return new SyncHistoryKeysResponse(row.Id, row.Op, Array.Empty<SyncHistoryKeyDto>());
        }

        var keys = _db.GetChangeLogRange(userId, row.FromSeq.Value, row.ToSeq.Value, clamped)
            .Select(entry => new SyncHistoryKeyDto(entry.Ns, entry.Key, entry.UpdatedAt))
            .ToList();
        return new SyncHistoryKeysResponse(row.Id, row.Op, keys);
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
