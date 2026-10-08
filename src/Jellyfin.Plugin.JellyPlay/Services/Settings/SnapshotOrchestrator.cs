using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.JellyPlay.Api;
using Jellyfin.Plugin.JellyPlay.Storage;
using Jellyfin.Plugin.JellyPlay.Storage.Models;

namespace Jellyfin.Plugin.JellyPlay.Services.Settings;

/// <summary>
/// The service-level batch seam for snapshot orchestration: applies raw
/// writes through the one apply pipeline (LWW/tombstone/quota application,
/// change log, history record, SSE fan-out). <see cref="SettingsService"/>
/// routes its own <c>ApplyRawWrites</c> here as a method group — the
/// orchestrator never touches the database's batch signatures itself
/// (ADR-0003: new logic stays ABOVE the database).
/// </summary>
public delegate SettingsBatchResponse ApplySettingsBatch(
    string userId,
    string? profile,
    string? deviceId,
    IReadOnlyList<SettingWrite> writes,
    long? maxWriteUpdatedAt);

/// <summary>
/// The pure orchestration kernel behind restore points and export/import: it
/// owns the restore-plan math (snapshot key set, server stamp past the newest
/// overlapping live row, per-profile tombstone/restore groupings, write-set
/// builders) and the export-bundle/import write-set builders. No database, no
/// hub, no clock — every input arrives as a parameter, so the kernel is
/// deterministic and unit-testable. <see cref="SnapshotService"/> is the seam
/// that feeds this kernel (snapshot lookup, current rows, server now) plus the
/// apply delegate; <see cref="SettingsService"/> stays a thin adapter over
/// that seam. Single history entry + single SSE event semantics are preserved
/// by construction: every profile batch still rides the ordinary apply
/// pipeline, so the change log, the anchored SSE event and history recording
/// happen exactly as before — the wire contract does not move.
/// </summary>
public static class SnapshotOrchestrator
{
    /// <summary>Device id stamped on restore writes (server-initiated writer).</summary>
    public const string RestoreDeviceId = "restore";

    /// <summary>Device id fallback for import writes when the caller names none.</summary>
    public const string ImportDeviceIdFallback = "import";

    /// <summary>Maps a stored profile to the apply pipeline's profile argument (base profile is null).</summary>
    public static string? ApplyProfile(string profile)
        => profile == JellyPlayDatabase.BaseProfile ? null : profile;

    /// <summary>The snapshot's key set: (profile, ns, key) triples the restore must converge to.</summary>
    public static HashSet<(string Profile, string Ns, string Key)> SnapshotKeySet(IReadOnlyList<SnapshotEntry> entries)
        => entries
            .Select(entry => (entry.Profile, entry.Ns, entry.Key))
            .ToHashSet();

    /// <summary>
    /// The restore stamp: one millisecond past the NEWEST stamp any
    /// overlapping live row carries (a client clock may run up to the skew
    /// ceiling into the future), so the re-apply provably wins LWW regardless
    /// of client clocks. <c>now + 1</c> alone would silently lose to a row
    /// stamped into the future.
    /// </summary>
    public static long RestoreStamp(
        IReadOnlyList<SettingRow> current,
        HashSet<(string Profile, string Ns, string Key)> snapshotKeys,
        long now)
    {
        var maxOverlapped = current
            .Where(row => snapshotKeys.Contains((row.Profile, row.Ns, row.Key)))
            .Select(row => row.UpdatedAt)
            .DefaultIfEmpty(0)
            .Max();
        return Math.Max(now + 1, maxOverlapped + 1);
    }

    /// <summary>
    /// The drift groups: current rows absent from the snapshot, grouped per
    /// profile (ordered) — the ONLY keys the restore tombstones.
    /// Re-tombstoning keys the re-apply would immediately recreate is pure
    /// change-log noise, so the diff-first pass skips them.
    /// </summary>
    public static IReadOnlyList<IGrouping<string, SettingRow>> DriftGroups(
        IReadOnlyList<SettingRow> current,
        HashSet<(string Profile, string Ns, string Key)> snapshotKeys)
        => current
            .Where(row => !snapshotKeys.Contains((row.Profile, row.Ns, row.Key)))
            .GroupBy(row => row.Profile, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToList();

    /// <summary>Tombstone writes for one drift group (server-stamped deletes).</summary>
    public static IReadOnlyList<SettingWrite> TombstoneWrites(IEnumerable<SettingRow> profileGroup, long now)
        => profileGroup
            .Select(row => new SettingWrite(row.Ns, row.Key, row.SchemaVersion, now, RestoreDeviceId, Array.Empty<byte>(), IsDelete: true))
            .ToList();

    /// <summary>The snapshot's entries grouped per profile (ordered) for the re-apply pass.</summary>
    public static IReadOnlyList<IGrouping<string, SnapshotEntry>> RestoreGroups(IReadOnlyList<SnapshotEntry> entries)
        => entries
            .GroupBy(entry => entry.Profile, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Re-apply writes for one snapshot profile group: deduped by (ns, key)
    /// (first entry wins), stamped at <paramref name="restoreStamp"/> with the
    /// stored bytes — no JSON re-parse. The caller bounds these writes by the
    /// restore stamp itself (not the client-skew clamp): the re-apply writes
    /// ARE stamped <paramref name="restoreStamp"/>, which the clamp would
    /// reject as clock-skew whenever an overlapping live row sits exactly AT
    /// the ceiling.
    /// </summary>
    public static IReadOnlyList<SettingWrite> RestoreWrites(IEnumerable<SnapshotEntry> profileGroup, long restoreStamp)
        => profileGroup
            .GroupBy(entry => (entry.Ns, entry.Key))
            .Select(group => group.First())
            .Select(entry => new SettingWrite(
                entry.Ns,
                entry.Key,
                entry.SchemaVersion,
                restoreStamp,
                RestoreDeviceId,
                Convert.FromBase64String(entry.ValueBase64)))
            .ToList();

    /// <summary>
    /// Import writes for one bundle profile: every row re-applied with a
    /// server-now timestamp — it beats anything older than now (per LWW) but
    /// never clobbers a legitimately newer change pushed after the import.
    /// </summary>
    public static IReadOnlyList<SettingWrite> ImportWrites(IEnumerable<SettingsEntryDto> settings, long now, string deviceId)
        => settings
            .Select(entry => new SettingWrite(
                entry.Ns,
                entry.Key,
                entry.SchemaVersion,
                now,
                deviceId,
                JsonSerializer.SerializeToUtf8Bytes(entry.Value)))
            .ToList();

    /// <summary>
    /// The portable export bundle: every stored profile's rows plus the
    /// resolved modes maps (per profile) and the settings-catalog stamp.
    /// Values are the user's own rows — admin defaults appear only through
    /// modes. The row and mode projections arrive as delegates (service-level
    /// routing): the corrupt-blob skip lives in the snapshot fold, the
    /// tri-state merge in the resolve path — this kernel only assembles.
    /// </summary>
    public static SettingsExportBundle BuildExportBundle(
        long exportedAt,
        IReadOnlyList<string> profiles,
        Func<string, IReadOnlyList<SettingsEntryDto>> settingsForProfile,
        Func<string, IReadOnlyDictionary<string, string>> modesForProfile)
    {
        var bundle = new SettingsExportBundle
        {
            ExportedAt = exportedAt,
            PluginVersion = typeof(JellyPlayPlugin).Assembly.GetName().Version?.ToString() ?? string.Empty,
            CatalogSchema = ClientSettingsCatalog.CatalogSchema,
            CatalogSettings = ClientSettingsCatalog.KnownSettings.Count
        };

        foreach (var profile in profiles)
        {
            bundle.Profiles.Add(new SettingsExportProfile
            {
                Profile = profile,
                Settings = settingsForProfile(profile).ToList()
            });
            bundle.Modes[profile] = new Dictionary<string, string>(modesForProfile(profile), StringComparer.Ordinal);
        }

        return bundle;
    }

    /// <summary>Folds one profile's re-apply batch into the restore/import response (applied/rejected across all profiles, head = the final head).</summary>
    public static void FoldBatch(SettingsBatchResponse response, SettingsBatchResponse batch)
    {
        response.Head = Math.Max(response.Head, batch.Head);
        response.Applied.AddRange(batch.Applied);
        response.Rejected.AddRange(batch.Rejected);
    }
}
