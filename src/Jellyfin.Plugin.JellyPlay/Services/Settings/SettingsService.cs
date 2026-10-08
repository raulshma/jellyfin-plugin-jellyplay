using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Api;
using Jellyfin.Plugin.JellyPlay.Configuration;
using Jellyfin.Plugin.JellyPlay.Helpers;
using Jellyfin.Plugin.JellyPlay.Realtime;
using Jellyfin.Plugin.JellyPlay.Services.Admin;
using Jellyfin.Plugin.JellyPlay.Services.Push;
using Jellyfin.Plugin.JellyPlay.Services.Shared;
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
public sealed class SettingsService : IDisposable
{
    public const string GlobalDefaultsScope = "global";

    /// <summary>Writes stamped further ahead than this are rejected with "clock-skew" — pure client-clock LWW stays in force otherwise.</summary>
    public const long MaxClockSkewMilliseconds = 5 * 60 * 1000;

    /// <summary>Pagination: ?limit= defaults here (far above any real store — current clients never see a second page) and clamps to <see cref="MaxPageLimit"/>.</summary>
    public const int DefaultPageLimit = 10_000;
    public const int MaxPageLimit = 100_000;

    private readonly JellyPlayDatabase _db;
    private readonly Func<SyncConfig> _config;
    private readonly TimeProvider _clock;
    private readonly SnapshotService _snapshots;

    /// <summary>
    /// The sync-operation recording + fan-out module: every history insert
    /// and every SSE/admin-stream fan-out below routes through here (the
    /// service keeps no op literals or publish logic of its own).
    /// </summary>
    private readonly SyncOpRecorder _ops;

    private bool _disposed;

    public SettingsService(
        JellyPlayDatabase db,
        SseHub hub,
        Func<SyncConfig> config,
        ILogger<SettingsService> logger,
        SnapshotService snapshots,
        PushDispatcher? push = null,
        TimeProvider? clock = null,
        SyncOpRecorder? ops = null)
    {
        _db = db;
        _config = config;
        _snapshots = snapshots;
        _clock = clock ?? TimeProvider.System;
        // Composed, not injected (keeps the public constructor unchanged for
        // existing callers/tests): the recorder only needs what this service
        // already holds.
        _ops = ops ?? new SyncOpRecorder(db, hub, push, logger, _clock);
    }

    /// <summary>
    /// Stops the composed recorder's background pull-flusher after draining
    /// what is queued — the DI container disposes this singleton at shutdown
    /// (tests drive the recorder's queue synchronously through
    /// <see cref="FlushPullHistoryAsync"/> instead).
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ops.Dispose();
    }

    public JellyPlayDatabase.Quotas Quotas => new(
        _config().MaxKeyBytes,
        _config().MaxUserBytes,
        _config().MaxKeysPerUser,
        _config().NamespaceQuotaBytes);

    private long ServerNow => _clock.GetUtcNow().ToUnixTimeMilliseconds();

    public SettingsSnapshotResponse GetAll(string userId, string profile, long? cursor = null, int? limit = null)
    {
        // The snapshot pages IN SQL like its delta sibling (GetChanged): the
        // store's stable ns, key ordering makes the offset window exact, and
        // the over-fetch-by-one detects a following page with the same
        // NextCursor semantics the in-memory slice produced.
        var clamped = Paged.Clamp(limit, DefaultPageLimit, MaxPageLimit);
        var start = Paged.Offset(cursor);
        var fetched = _db.GetSettings(userId, profile, start, clamped + 1);
        var (rows, nextCursor) = Paged.FromOverFetch(fetched, start, clamped);
        var response = ToSnapshot(profile, _db.GetChangeLogHead(userId), rows);
        response.NextCursor = nextCursor;
        return response;
    }

    public SettingsSnapshotResponse GetChanged(string userId, string profile, long since, string? deviceId, long? cursor = null, int? limit = null)
    {
        // The whole delta pull — changed rows, deleted keys and the head —
        // resolves in ONE store composite; the rows page IN SQL (profile
        // filter + offset window pushed down) and the over-fetch-by-one
        // detects a following page with the exact NextCursor semantics the
        // in-memory slice produces — both halves route through the shared
        // Paged seam.
        var clamped = Paged.Clamp(limit, DefaultPageLimit, MaxPageLimit);
        var start = Paged.Offset(cursor);
        var bundle = _db.GetChangedSettingsBundle(userId, since, profile, start, clamped + 1);
        var (rows, nextCursor) = Paged.FromOverFetch(bundle.Rows, start, clamped);

        // Delta pulls are recorded (full GET settings reads are not): the
        // history shows which device observed which change. The recorded
        // range is (since, head] — exactly what the pull served (head comes
        // from the same locked read as the rows, and can only advance
        // between requests, never shrink), so a zero-key pull still gets its
        // (empty) range.
        var head = bundle.Head;
        RecordOperation(userId, deviceId ?? string.Empty, SyncOpRecorder.OpPull, rows.Count, 0, 0, null, since, head);
        var response = ToSnapshot(profile, head, rows);
        response.NextCursor = nextCursor;
        var deleted = bundle.Deleted
            .Select(key => new DeletedKeyDto(key.Ns, key.Key))
            .ToList();
        // Omitted (null) when nothing was deleted — additive like modes; old
        // clients ignore it, current clients treat absent as "no deletions".
        response.Deleted = deleted.Count > 0 ? deleted : null;
        return response;
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
            raw.Add(new SettingWrite(write.Ns, write.Key, write.SchemaVersion, write.UpdatedAt, ClaimsExtensions.NormalizeDeviceId(deviceId), bytes, write.Deleted));
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
        deviceId = ClaimsExtensions.NormalizeDeviceId(deviceId);

        var outcome = _db.ApplyBatchWithHistory(
            userId,
            profile,
            writes,
            Quotas,
            deviceId,
            maxWriteUpdatedAt: maxWriteUpdatedAt ?? (ServerNow + MaxClockSkewMilliseconds),
            op: SyncOpRecorder.OpPush,
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

        PublishAdminOp(userId, deviceId, SyncOpRecorder.OpPush, outcome.Applied.Count, outcome.Rejected.Count);

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
        // the keys. The range brackets exactly those tombstone rows — head
        // bracket, tombstone batch and history record land in ONE store
        // composite (the head this path used to re-read on a fourth
        // connection could interleave with other writers).
        var outcome = _db.ResetNamespaceWithHistory(userId, resolvedProfile, ns, ServerNow, deviceId ?? string.Empty, SyncOpRecorder.OpReset, ServerNow);
        PublishAdminOp(userId, deviceId ?? string.Empty, SyncOpRecorder.OpReset, outcome.Deleted, 0);
        var payload = JsonSerializer.Serialize(new
        {
            type = "settings.reset",
            profile = resolvedProfile,
            ns,
            ts = ServerNow
        });
        PublishChanged(userId, outcome.HeadAfter, "settings.reset", payload);
    }

    /// <summary>
    /// Device wipe (the revoke action's data half): tombstones every settings
    /// row written by the device (all profiles), records a 'wipe' operation
    /// and fans out — peers drop the wiped keys on their next delta. The
    /// whole bracket (head reads, tombstone batch, history record) is ONE
    /// store composite; an empty wipe records and publishes nothing.
    /// </summary>
    public void WipeDevice(string userId, string deviceId)
    {
        var outcome = _db.WipeDeviceWithHistory(userId, deviceId, ServerNow, SyncOpRecorder.OpWipe, ServerNow);
        if (outcome.Deleted == 0)
        {
            return;
        }

        PublishAdminOp(userId, deviceId, SyncOpRecorder.OpWipe, outcome.Deleted, 0);
        var payload = JsonSerializer.Serialize(new
        {
            type = "settings.changed",
            ns = string.Empty,
            count = outcome.Deleted,
            ts = ServerNow
        });
        PublishChanged(userId, outcome.HeadAfter, "settings.changed", payload);
    }

    /// <summary>
    /// The one fan-out for every settings mutation path (batch apply, reset,
    /// wipe): publishes the event anchored at the change-log head — the SSE id
    /// IS that head, so a reconnecting client resumes the delta pull from
    /// `changed?since=&lt;id&gt;` — plus the silent sync-nudge for offline
    /// devices. Thin adapter over <see cref="SyncOpRecorder.PublishChanged"/>,
    /// where the fan-out locality lives.
    /// </summary>
    private void PublishChanged(string userId, long head, string eventName, string payload)
        => _ops.PublishChanged(userId, head, eventName, payload);

    // ------------------------------------------------------------------
    // Restore points
    // ------------------------------------------------------------------

    /// <summary>
    /// Restores one of the caller's snapshots. Thin delegate over the snapshot
    /// seam (<see cref="SnapshotService.Restore"/> above the
    /// <see cref="SnapshotOrchestrator"/> kernel): the diff-first tombstone
    /// pass, the server-stamped re-apply and the per-profile response fold all
    /// live there; the single history entry + single SSE event semantics per
    /// batch are preserved and the wire contract does not move.
    /// Returns null when the id is not the caller's own snapshot.
    /// </summary>
    public SettingsBatchResponse? RestoreSnapshot(string userId, long id)
        => _snapshots.Restore(userId, id, ApplyRawWrites, () => _db.GetAllSettingsRows(userId), ServerNow);

    // ------------------------------------------------------------------
    // Export / import
    // ------------------------------------------------------------------

    /// <summary>
    /// The portable bundle: every stored profile's rows plus the resolved
    /// modes maps (per profile) and the settings-catalog stamp. Values are
    /// the user's own rows — admin defaults appear only through modes. Thin
    /// delegate over the snapshot seam (<see cref="SnapshotService.Export"/>):
    /// the snapshot fold keeps its locality, the resolve merge keeps its own.
    /// </summary>
    public SettingsExportBundle Export(string userId)
        => _snapshots.Export(
            userId,
            ServerNow,
            profile => ToSnapshot(profile, _db.GetChangeLogHead(userId), _db.GetSettings(userId, profile)).Settings,
            profile => ResolveProfile(userId, profile).Modes ?? new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>
    /// Imports a bundle: every row is re-applied through the raw apply path
    /// with a server-now timestamp (so the import beats anything older than
    /// now, per LWW) and per-profile batching preserved. Thin delegate over
    /// the snapshot seam (<see cref="SnapshotService.Import"/>).
    /// </summary>
    public SettingsBatchResponse Import(string userId, string? deviceId, SettingsExportBundle bundle)
        => _snapshots.Import(userId, deviceId, bundle, ServerNow, ApplyRawWrites);

    // ------------------------------------------------------------------
    // Sync history recording (observability; never fails the operation)
    // ------------------------------------------------------------------

    /// <summary>
    /// Appends one sync_history row (with the operation's change-log range:
    /// push = head before/after the batch, pull = the requested since cursor
    /// through the served head, reset = a zero-width range at the head after).
    /// Best-effort by contract: an observability write must never fail (or
    /// even slow-path-fail) the sync operation it observes, so every exception
    /// is swallowed with a warning. Also fans the operation out to the admin
    /// live-monitor stream — the dashboard's "live" view of the same record.
    /// Thin adapter over <see cref="SyncOpRecorder.Record"/>, where the
    /// recording locality lives.
    /// </summary>
    private void RecordOperation(string userId, string deviceId, string op, int keysApplied, int keysRejected, long bytes, string? rejectsJson, long? fromSeq = null, long? toSeq = null)
        => _ops.Record(userId, deviceId, op, keysApplied, keysRejected, bytes, rejectsJson, fromSeq, toSeq);

    /// <summary>
    /// Drains the pull-history recorder's queue into the store — the test
    /// seam for the async half of <see cref="GetChanged"/> (pull rows land
    /// in sync_history only after a flush window or this call).
    /// </summary>
    internal Task FlushPullHistoryAsync() => _ops.FlushAsync();

    /// <summary>
    /// Live-monitor fan-out: one <c>sync.op</c> event on the admin stream per
    /// recorded operation (push/pull/reset/wipe). Broadcast delivery — every
    /// elevated dashboard subscriber sees every user's ops. Best-effort like
    /// the history write itself: a publish failure must never fail (or even
    /// slow-path-fail) the sync operation it observes. Thin adapter over
    /// <see cref="SyncOpRecorder.PublishAdminOp"/>, where the fan-out
    /// locality lives.
    /// </summary>
    private void PublishAdminOp(string userId, string deviceId, string op, int keysApplied, int keysRejected)
        => _ops.PublishAdminOp(userId, deviceId, op, keysApplied, keysRejected);

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

        // Insertion-ordered merge (an overlay write replaces in place, keeping
        // the base entry's position): the entry order the resolved snapshot
        // has always served. Entries are built straight from the stored bytes
        // — no parse, no clone.
        var merged = new Dictionary<(string Ns, string Key), SettingsEntryDto>();
        foreach (var row in baseRows)
        {
            merged[(row.Ns, row.Key)] = ToEntry(row);
        }

        foreach (var row in overlayRows)
        {
            merged[(row.Ns, row.Key)] = ToEntry(row);
        }

        var defaults = GetDefaultsMerged(
            userId,
            merged.Keys.Select(id => DefaultsEnvelope.Join(id.Item1, id.Item2)).ToHashSet(StringComparer.Ordinal));
        var modes = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var entry in defaults)
        {
            merged[(entry.Ns, entry.Key)] = DefaultEntry(profile, entry);
            modes[DefaultsEnvelope.Join(entry.Ns, entry.Key)] = entry.Mode == AdminDefaultMode.Forced ? "forced" : "suggested";
        }

        // Every remaining resolved key is user-owned (base or profile overlay):
        // "unset" — including a suggested default that lost to an existing
        // user value (the user's value wins, so the provenance is the user's).
        foreach (var row in merged.Values)
        {
            modes.TryAdd(DefaultsEnvelope.Join(row.Ns, row.Key), "unset");
        }

        var response = new SettingsSnapshotResponse
        {
            Head = _db.GetChangeLogHead(userId),
            Profile = profile,
            Settings = merged.Values.ToList()
        };
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
    /// client-defined and forward-compatible. Delegates to the catalog
    /// module's seam, where the validation locality lives.
    /// </summary>
    internal static List<string> ValidateAgainstCatalog(JsonElement payload)
        => ClientSettingsCatalog.ValidateEnvelope(payload);

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

    /// <summary>
    /// One default as a resolved-snapshot entry: the parsed default's raw text
    /// passes through verbatim (defaults stay parsed — the envelope merge
    /// needs values — but their text never re-serializes).
    /// </summary>
    private static SettingsEntryDto DefaultEntry(string profile, ResolvedDefault entry)
        => new()
        {
            Ns = entry.Ns,
            Key = entry.Key,
            SchemaVersion = 1,
            UpdatedAt = 0,
            DeviceId = "admin-default",
            Profile = profile,
            Value = new RawJson(entry.Value.GetRawText())
        };

    /// <summary>
    /// One stored row as a snapshot entry. The value crosses as
    /// <see cref="RawJson"/> — no JsonDocument per value per request: every
    /// write path stores serialized JSON (the wire adapter, restore, import
    /// and the admin push all serialize before store), so the bytes are valid
    /// JSON by construction and the gate writes them verbatim. An empty blob
    /// is the stored-null shape (a push of an unbound value).
    /// </summary>
    private static SettingsEntryDto ToEntry(SettingRow row)
        => new()
        {
            Ns = row.Ns,
            Key = row.Key,
            SchemaVersion = row.SchemaVersion,
            UpdatedAt = row.UpdatedAt,
            DeviceId = row.DeviceId,
            Profile = row.Profile,
            Value = row.Value.Length == 0 ? RawJson.Null : new RawJson(Encoding.UTF8.GetString(row.Value))
        };

    private static SettingsSnapshotResponse ToSnapshot(string profile, long head, IReadOnlyList<SettingRow> rows)
    {
        var response = new SettingsSnapshotResponse
        {
            Head = head,
            Profile = profile
        };

        foreach (var row in rows)
        {
            response.Settings.Add(ToEntry(row));
        }

        return response;
    }
}
