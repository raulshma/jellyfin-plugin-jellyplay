using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Api;
using Jellyfin.Plugin.JellyPlay.Configuration;
using Jellyfin.Plugin.JellyPlay.Helpers;
using Jellyfin.Plugin.JellyPlay.Realtime;
using Jellyfin.Plugin.JellyPlay.Services.Admin;
using Jellyfin.Plugin.JellyPlay.Services.Push;
using Jellyfin.Plugin.JellyPlay.Storage;
using Jellyfin.Plugin.JellyPlay.Storage.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyPlay.Services.Settings;

/// <summary>
/// Settings-sync business logic: batch LWW application (with tombstones and
/// the clock-skew clamp), changed-since deltas (current values plus the
/// deleted[] half), resolved-profile merge (base + device profile + tri-state
/// admin defaults), restore points, export/import and SSE fan-out on change.
/// </summary>
public sealed class SettingsService
{
    public const string GlobalDefaultsScope = "global";

    /// <summary>Writes stamped further ahead than this are rejected with "clock-skew" — pure client-clock LWW stays in force otherwise.</summary>
    public const long MaxClockSkewMilliseconds = 5 * 60 * 1000;

    /// <summary>Pagination: ?limit= defaults here (far above any real store — current clients never see a second page) and clamps to <see cref="MaxPageLimit"/>.</summary>
    public const int DefaultPageLimit = 10_000;
    public const int MaxPageLimit = 100_000;

    private readonly JellyPlayDatabase _db;
    private readonly SseHub _hub;
    private readonly Func<SyncConfig> _config;
    private readonly ILogger<SettingsService> _logger;
    private readonly TimeProvider _clock;
    private readonly SnapshotService _snapshots;
    private readonly PushDispatcher? _push;

    public SettingsService(
        JellyPlayDatabase db,
        SseHub hub,
        Func<SyncConfig> config,
        ILogger<SettingsService> logger,
        SnapshotService snapshots,
        PushDispatcher? push = null,
        TimeProvider? clock = null)
    {
        _db = db;
        _hub = hub;
        _config = config;
        _logger = logger;
        _snapshots = snapshots;
        _push = push;
        _clock = clock ?? TimeProvider.System;
    }

    public JellyPlayDatabase.Quotas Quotas => new(
        _config().MaxKeyBytes,
        _config().MaxUserBytes,
        _config().MaxKeysPerUser,
        _config().NamespaceQuotaBytes);

    private long ServerNow => _clock.GetUtcNow().ToUnixTimeMilliseconds();

    public SettingsSnapshotResponse GetAll(string userId, string profile, long? cursor = null, int? limit = null)
    {
        var rows = PageRows(_db.GetSettings(userId, profile), cursor, limit, out var nextCursor);
        var response = ToSnapshot(userId, profile, _db.GetChangeLogHead(userId), rows);
        response.NextCursor = nextCursor;
        return response;
    }

    public SettingsSnapshotResponse GetChanged(string userId, string profile, long since, string? deviceId, long? cursor = null, int? limit = null)
    {
        // The delta pull pages IN SQL now (profile filter + offset window
        // pushed down to the store); the over-fetch-by-one detects a following
        // page with the exact NextCursor semantics PageRows used to produce.
        var clamped = RequestLimits.Clamp(limit ?? 0, DefaultPageLimit, MaxPageLimit);
        var start = (int)Math.Min(Math.Max(cursor ?? 0, 0), int.MaxValue);
        var fetched = _db.GetChangedSettings(userId, since, profile, start, clamped + 1);
        var rows = fetched.Count > clamped ? fetched.Take(clamped).ToList() : fetched.ToList();
        var nextCursor = fetched.Count > clamped ? start + (long)clamped : (long?)null;

        // Delta pulls are recorded (full GET settings reads are not): the
        // history shows which device observed which change. The recorded
        // range is (since, head] — exactly what the pull served (head can
        // only advance between the read and this line, never shrink), so a
        // zero-key pull still gets its (empty) range.
        var head = _db.GetChangeLogHead(userId);
        RecordOperation(userId, deviceId ?? string.Empty, "pull", rows.Count, 0, 0, null, since, head);
        var response = ToSnapshot(userId, profile, head, rows);
        response.NextCursor = nextCursor;
        var deleted = _db.GetDeletedSettings(userId, since, profile)
            .Select(key => new DeletedKeyDto(key.Ns, key.Key))
            .ToList();
        // Omitted (null) when nothing was deleted — additive like modes; old
        // clients ignore it, current clients treat absent as "no deletions".
        response.Deleted = deleted.Count > 0 ? deleted : null;
        return response;
    }

    /// <summary>
    /// Offset-based pagination over the ordered rows: the cursor is the opaque
    /// count of already-served rows (the store's (profile,) ns, key ordering
    /// is stable, so offsets are safe); <see cref="SettingsSnapshotResponse.NextCursor"/>
    /// is null on the last page.
    /// </summary>
    private static List<SettingRow> PageRows(IReadOnlyList<SettingRow> rows, long? cursor, int? limit, out long? nextCursor)
    {
        var clamped = RequestLimits.Clamp(limit ?? 0, DefaultPageLimit, MaxPageLimit);
        var start = (int)Math.Clamp(cursor ?? 0, 0, rows.Count);
        var page = rows.Skip(start).Take(clamped).ToList();
        nextCursor = start + page.Count < rows.Count ? start + page.Count : null;
        return page;
    }

    /// <summary>
    /// The wire path: a thin adapter over <see cref="ApplyRawWrites"/> — maps
    /// the Api DTOs to raw writes (serializing each value ONCE to its stored
    /// bytes) and hands them to the one apply pipeline. All LWW/tombstone/
    /// quota/history logic lives there, shared with restore, import and the
    /// admin defaults push.
    /// </summary>
    public SettingsBatchResponse ApplyBatch(string userId, string? profile, string? deviceId, IReadOnlyList<SettingsWriteDto> writes)
    {
        // Canonical tombstone is `deleted: true` — the flag ONLY. A JSON-null
        // value without the flag is a stored value like any other (serialized
        // "null" bytes, the pre-v7 behavior), not a delete: the null tolerance
        // was non-additive and is gone.
        var raw = new List<SettingWrite>(writes.Count);
        foreach (var write in writes)
        {
            if (string.IsNullOrWhiteSpace(write.Ns) || string.IsNullOrWhiteSpace(write.Key))
            {
                continue;
            }

            var bytes = write.Value.ValueKind is JsonValueKind.Undefined || write.Deleted
                ? Array.Empty<byte>()
                : JsonSerializer.SerializeToUtf8Bytes(write.Value);
            raw.Add(new SettingWrite(write.Ns, write.Key, write.SchemaVersion, write.UpdatedAt, deviceId ?? "unknown", bytes, write.Deleted));
        }

        return ApplyRawWrites(userId, profile, deviceId, raw);
    }

    /// <summary>
    /// The internal apply path over raw bytes — the deep module behind the DTO
    /// adapter. Restore, import and the admin defaults push call this directly
    /// with the bytes they already hold, skipping the JSON round-trip (and the
    /// reference-keyed byte side-table it needed) entirely. One composite owns
    /// the whole pipeline: revocation refusal, the clock-skew ceiling,
    /// LWW/tombstone/quota application, the change log, the history record —
    /// one connection/transaction — and the service adds the anchored SSE
    /// event, the admin live-monitor fan-out and the response fold. The
    /// ceiling defaults to the client-clamp (<see cref="MaxClockSkewMilliseconds"/>
    /// past server now); a server-initiated writer (the restore) passes its
    /// own stamp instead — its writes must not be judged by the client clamp.
    /// </summary>
    internal SettingsBatchResponse ApplyRawWrites(string userId, string? profile, string? deviceId, IReadOnlyList<SettingWrite> writes, long? maxWriteUpdatedAt = null)
    {
        profile ??= JellyPlayDatabase.BaseProfile;
        deviceId ??= "unknown";

        var outcome = _db.ApplyBatchWithHistory(
            userId,
            profile,
            writes,
            Quotas,
            deviceId,
            maxWriteUpdatedAt: maxWriteUpdatedAt ?? (ServerNow + MaxClockSkewMilliseconds),
            op: "push",
            historyTs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            rejectsJsonBuilder: static rejected => SyncRejectsCodec.Encode(rejected));

        if (outcome.Applied.Count > 0)
        {
            var payload = JsonSerializer.Serialize(new
            {
                type = "settings.changed",
                profile,
                ns = string.Join(',', outcome.Applied.Select(a => a.Ns).Distinct()),
                count = outcome.Applied.Count,
                ts = ServerNow
            });
            PublishChanged(userId, outcome.HeadAfter, "settings.changed", payload);
        }

        PublishAdminOp(userId, deviceId, "push", outcome.Applied.Count, outcome.Rejected.Count);

        return new SettingsBatchResponse
        {
            Head = outcome.HeadAfter,
            Applied = outcome.Applied
                .Select(a => new AppliedSettingDto(a.Ns, a.Key, a.UpdatedAt, a.Seq, a.Deleted))
                .ToList(),
            Rejected = outcome.Rejected
                .Select(r => new RejectedSettingDto(r.Ns, r.Key, r.Reason))
                .ToList()
        };
    }

    public void ResetNamespace(string userId, string? profile, string ns, string? deviceId)
    {
        var resolvedProfile = profile ?? JellyPlayDatabase.BaseProfile;
        // Tombstone semantics (schema v7): the reset deletes the namespace's
        // rows AND appends one 'del' change-log row per key, so every peer's
        // next delta carries the deletions and older pushes cannot resurrect
        // the keys. The range brackets exactly those tombstone rows.
        var headBefore = _db.GetChangeLogHead(userId);
        var deleted = _db.DeleteNamespace(userId, resolvedProfile, ns, ServerNow);
        var headAfter = _db.GetChangeLogHead(userId);
        RecordOperation(userId, deviceId ?? string.Empty, "reset", deleted, 0, 0, null, headBefore, headAfter);
        var payload = JsonSerializer.Serialize(new
        {
            type = "settings.reset",
            profile = resolvedProfile,
            ns,
            ts = ServerNow
        });
        PublishChanged(userId, headAfter, "settings.reset", payload);
    }

    /// <summary>
    /// Device wipe (the revoke action's data half): tombstones every settings
    /// row written by the device (all profiles), records a 'wipe' operation
    /// and fans out — peers drop the wiped keys on their next delta.
    /// </summary>
    public void WipeDevice(string userId, string deviceId)
    {
        var headBefore = _db.GetChangeLogHead(userId);
        var wiped = _db.TombstoneDeviceSettings(userId, deviceId, ServerNow);
        if (wiped == 0)
        {
            return;
        }

        var head = _db.GetChangeLogHead(userId);
        RecordOperation(userId, deviceId, "wipe", wiped, 0, 0, null, headBefore, head);
        var payload = JsonSerializer.Serialize(new
        {
            type = "settings.changed",
            ns = string.Empty,
            count = wiped,
            ts = ServerNow
        });
        PublishChanged(userId, head, "settings.changed", payload);
    }

    /// <summary>
    /// The one fan-out for every settings mutation path (batch apply, reset,
    /// wipe): publishes the event anchored at the change-log head — the SSE id
    /// IS that head, so a reconnecting client resumes the delta pull from
    /// `changed?since=<id>` — plus the silent trigger: devices presumed
    /// offline for SSE (no stream subscriber) that registered the
    /// "silent-push" cap get a data-only sync-nudge so they flush promptly.
    /// </summary>
    private void PublishChanged(string userId, long head, string eventName, string payload)
    {
        // The delivered count IS the live-subscriber signal (zero = nobody is
        // streaming right now): no second O(n) hub scan. Devices presumed
        // offline for SSE that registered the "silent-push" cap get the
        // data-only sync-nudge so they flush promptly.
        var delivered = _hub.PublishToUser(SseHub.SettingsStream, userId, eventName, payload, (ulong)Math.Max(head, 1));
        if (delivered == 0)
        {
            _push?.DispatchSyncNudge(userId);
        }
    }

    // ------------------------------------------------------------------
    // Restore points
    // ------------------------------------------------------------------

    /// <summary>
    /// Restores one of the caller's snapshots: a diff-first tombstone pass
    /// (only keys present now but absent from the snapshot are tombstoned —
    /// re-tombstoning keys the re-apply would immediately recreate only
    /// doubled the change-log rows), then the snapshot re-applied per profile
    /// — server-stamped one millisecond past the NEWEST overlapping live row
    /// (a client clock up to the skew ceiling in the future may live there),
    /// so the restore provably wins LWW. That re-apply is bounded by its own
    /// server stamp (it IS the ceiling), not the client-skew clamp — an
    /// overlapping row AT the ceiling would otherwise reject the whole
    /// restore as clock-skew. Rides the raw apply pipeline, so the
    /// change log, the anchored SSE event and history recording all happen
    /// for free; the returned response folds every profile's re-apply batch
    /// (applied/rejected across all profiles, head = the final change-log
    /// head). Snapshot values are restored as their stored bytes — no JSON
    /// re-parse.
    /// Returns null when the id is not the caller's own snapshot.
    /// </summary>
    public SettingsBatchResponse? RestoreSnapshot(string userId, long id)
    {
        var stored = _snapshots.Get(userId, id);
        if (stored is null)
        {
            return null;
        }

        var now = ServerNow;
        var snapshotKeys = stored.Value.Entries
            .Select(entry => (Profile: entry.Profile, Ns: entry.Ns, Key: entry.Key))
            .ToHashSet();

        // Tombstone only the drift the snapshot cannot overwrite: keys that
        // exist now but are absent from the snapshot. One batch per profile
        // that has such keys, so each 'del' change-log row carries the profile
        // its key belongs to. The same pass reads the newest stamp among the
        // OVERLAPPING rows (the keys the re-apply must beat): now + 1 alone
        // would silently lose LWW to a row stamped into the future.
        var current = _db.GetAllSettingsRows(userId);
        var maxOverlapped = current
            .Where(row => snapshotKeys.Contains((row.Profile, row.Ns, row.Key)))
            .Select(row => row.UpdatedAt)
            .DefaultIfEmpty(0)
            .Max();
        var restoreStamp = Math.Max(now + 1, maxOverlapped + 1);
        foreach (var profileGroup in current
                     .Where(row => !snapshotKeys.Contains((row.Profile, row.Ns, row.Key)))
                     .GroupBy(row => row.Profile, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            ApplyRawWrites(
                userId,
                profileGroup.Key == JellyPlayDatabase.BaseProfile ? null : profileGroup.Key,
                "restore",
                profileGroup
                    .Select(row => new SettingWrite(row.Ns, row.Key, row.SchemaVersion, now, "restore", Array.Empty<byte>(), IsDelete: true))
                    .ToList());
        }

        var response = new SettingsBatchResponse();
        foreach (var profileGroup in stored.Value.Entries
                     .GroupBy(entry => entry.Profile, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var writes = profileGroup
                .GroupBy(entry => (entry.Ns, entry.Key))
                .Select(group => group.First())
                .Select(entry => new SettingWrite(
                    entry.Ns,
                    entry.Key,
                    entry.SchemaVersion,
                    restoreStamp,
                    "restore",
                    SnapshotService.DecodeValue(entry)))
                .ToList();
            var batch = ApplyRawWrites(
                userId,
                profileGroup.Key == JellyPlayDatabase.BaseProfile ? null : profileGroup.Key,
                "restore",
                writes,
                // The restore is server-initiated and bounded by its own
                // server stamp, not the client-skew clamp: the re-apply writes
                // are stamped restoreStamp itself, which the clamp would
                // reject as clock-skew whenever an overlapping live row sits
                // exactly AT the ceiling.
                maxWriteUpdatedAt: restoreStamp);
            response.Head = Math.Max(response.Head, batch.Head);
            response.Applied.AddRange(batch.Applied);
            response.Rejected.AddRange(batch.Rejected);
        }

        return response;
    }

    // ------------------------------------------------------------------
    // Export / import
    // ------------------------------------------------------------------

    /// <summary>
    /// The portable bundle: every stored profile's rows plus the resolved
    /// modes maps (per profile) and the settings-catalog stamp. Values are
    /// the user's own rows — admin defaults appear only through modes.
    /// </summary>
    public SettingsExportBundle Export(string userId)
    {
        var bundle = new SettingsExportBundle
        {
            ExportedAt = ServerNow,
            PluginVersion = typeof(JellyPlayPlugin).Assembly.GetName().Version?.ToString() ?? string.Empty,
            CatalogSchema = ClientSettingsCatalog.CatalogSchema,
            CatalogSettings = ClientSettingsCatalog.KnownSettings.Count
        };

        foreach (var profile in _db.GetDistinctSettingProfiles(userId))
        {
            var rows = _db.GetSettings(userId, profile);
            bundle.Profiles.Add(new SettingsExportProfile
            {
                Profile = profile,
                Settings = ToSnapshot(userId, profile, _db.GetChangeLogHead(userId), rows).Settings
            });
            bundle.Modes[profile] = ResolveProfile(userId, profile).Modes ?? new Dictionary<string, string>();
        }

        return bundle;
    }

    /// <summary>
    /// Imports a bundle: every row is re-applied through the raw apply path
    /// with a server-now timestamp (so the import beats anything older than
    /// now, per LWW) and per-profile batching preserved.
    /// </summary>
    public SettingsBatchResponse Import(string userId, string? deviceId, SettingsExportBundle bundle)
    {
        var now = ServerNow;
        var response = new SettingsBatchResponse();
        foreach (var profile in bundle.Profiles)
        {
            var writes = profile.Settings
                .Select(entry => new SettingWrite(
                    entry.Ns,
                    entry.Key,
                    entry.SchemaVersion,
                    now,
                    deviceId ?? "import",
                    JsonSerializer.SerializeToUtf8Bytes(entry.Value)))
                .ToList();
            if (writes.Count == 0)
            {
                continue;
            }

            var batch = ApplyRawWrites(userId, profile.Profile, deviceId ?? "import", writes);
            response.Head = Math.Max(response.Head, batch.Head);
            response.Applied.AddRange(batch.Applied);
            response.Rejected.AddRange(batch.Rejected);
        }

        return response;
    }

    // ------------------------------------------------------------------
    // Sync history recording (observability; never fails the operation)
    // ------------------------------------------------------------------

    /// <summary>RejectsJson is capped at this many entries per recorded operation (the codec owns the cap).</summary>
    internal const int MaxRecordedRejects = SyncRejectsCodec.MaxRecordedRejects;

    /// <summary>Capped <c>[{ns,key,reason}]</c> JSON for a recorded operation; null when nothing was rejected.</summary>
    internal static string? BuildRejectsJson(IReadOnlyList<RejectedSetting> rejected) => SyncRejectsCodec.Encode(rejected);

    /// <summary>
    /// Appends one sync_history row (with the operation's change-log range:
    /// push = head before/after the batch, pull = the requested since cursor
    /// through the served head, reset = a zero-width range at the head after).
    /// Best-effort by contract: an observability write must never fail (or
    /// even slow-path-fail) the sync operation it observes, so every exception
    /// is swallowed with a warning. Also fans the operation out to the admin
    /// live-monitor stream — the dashboard's "live" view of the same record.
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

        PublishAdminOp(userId, deviceId, op, keysApplied, keysRejected);
    }

    /// <summary>
    /// Live-monitor fan-out: one <c>sync.op</c> event on the admin stream per
    /// recorded operation (push/pull/reset/wipe). Broadcast delivery — every
    /// elevated dashboard subscriber sees every user's ops. Best-effort like
    /// the history write itself: a publish failure must never fail (or even
    /// slow-path-fail) the sync operation it observes.
    /// </summary>
    private void PublishAdminOp(string userId, string deviceId, string op, int keysApplied, int keysRejected)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                type = "sync.op",
                userId,
                op,
                deviceId,
                keysApplied,
                keysRejected,
                ts = ServerNow
            });
            _hub.PublishAll(SseHub.AdminStream, "sync.op", payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JellyPlay admin live-monitor publish failed (user {UserId}, op {Op}) — ignoring", userId, op);
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
            modes[DefaultsEnvelope.Join(entry.Ns, entry.Key)] = entry.Mode == AdminDefaultMode.Forced ? "forced" : "suggested";
        }

        // Every remaining resolved key is user-owned (base or profile overlay):
        // "unset" — including a suggested default that lost to an existing
        // user value (the user's value wins, so the provenance is the user's).
        foreach (var row in merged.Values)
        {
            modes.TryAdd(DefaultsEnvelope.Join(row.Ns, row.Key), "unset");
        }

        var response = ToSnapshot(userId, profile, _db.GetChangeLogHead(userId), merged.Values.ToList());
        response.Modes = modes;
        return response;
    }

    /// <summary>
    /// Batch into a device profile — the cross-profile copy/mutation surface.
    /// A restore point is captured first (best-effort): profile-level batch
    /// writes are the destructive-overwrite path the snapshots exist for.
    /// </summary>
    public void SetDeviceProfile(string userId, string profile, string? deviceId, IReadOnlyList<SettingsWriteDto> writes)
    {
        if (!string.IsNullOrEmpty(profile) && writes.Count > 0)
        {
            _snapshots.Create(userId, "profile-copy");
        }

        ApplyBatch(userId, profile, deviceId, writes);
    }

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
