namespace Jellyfin.Plugin.JellyPlay.Storage.Models;

/// <summary>One opaque settings blob. Value is client-owned JSON; server never parses it.</summary>
public sealed record SettingRow(
    string UserId,
    string Profile,
    string Ns,
    string Key,
    int SchemaVersion,
    long UpdatedAt,
    string DeviceId,
    byte[] Value);

/// <summary>
/// Incoming write from a client; <see cref="UpdatedAt"/> drives last-write-wins.
/// <see cref="IsDelete"/> marks a tombstone: applying it removes the stored row
/// and appends a <c>del</c> change-log row instead of writing a value (the
/// payload is ignored).
/// </summary>
public sealed record SettingWrite(
    string Ns,
    string Key,
    int SchemaVersion,
    long UpdatedAt,
    string DeviceId,
    byte[] Value,
    bool IsDelete = false);

public sealed record AppliedSetting(string Ns, string Key, long UpdatedAt, long Seq, bool Deleted = false);

public sealed record RejectedSetting(string Ns, string Key, string Reason);

public sealed record UpsertResult(IReadOnlyList<AppliedSetting> Applied, IReadOnlyList<RejectedSetting> Rejected);

/// <summary>
/// The ONE last-write-wins predicate, shared by the batch write pipeline and
/// the admin dry-run simulator so the two can never disagree: a write applies
/// only when strictly newer than the incumbent; an equal timestamp rejects
/// (deterministic, no oscillation). The same rule judges an absent row against
/// the key's latest change-log entry (put or tombstone) — the anti-resurrection
/// watermark.
/// </summary>
internal static class SettingsLww
{
    /// <summary>Whether a write stamped <paramref name="writeUpdatedAt"/> beats the incumbent stamped <paramref name="incumbentUpdatedAt"/>.</summary>
    public static bool WouldApply(long writeUpdatedAt, long incumbentUpdatedAt) => writeUpdatedAt > incumbentUpdatedAt;
}

/// <summary>A key tombstoned (deleted) at or after a delta cursor; the payload carried no value.</summary>
public sealed record DeletedSettingKey(string Profile, string Ns, string Key);

/// <summary>The delta answer: rows still present plus the keys deleted since the cursor.</summary>
public sealed record ChangedSettings(IReadOnlyList<SettingRow> Rows, IReadOnlyList<DeletedSettingKey> Deleted);

/// <summary>A change-log entry; <see cref="Seq"/> is the global cursor used by `changed?since=`.</summary>
public sealed record ChangeLogEntry(long Seq, string UserId, string Profile, string Ns, string Key, long UpdatedAt);

/// <summary>
/// One registered client device. The optional push registration
/// (<see cref="PushKind"/>: "generic" | "ntfy", <see cref="PushEndpoint"/>:
/// full publish URL — a secret, never echoed for another user) and
/// <see cref="CreatedAt"/> (unix ms; null on rows written before schema v4)
/// were added by the v4 migration. <see cref="Model"/> and
/// <see cref="CapsJson"/> (JSON array of self-reported capability strings,
/// e.g. ["silent-push"]) were added by the v7 migration;
/// <see cref="Revoked"/> marks a device the owner retired: it is excluded from
/// push fan-out and every settings write carrying its device id is rejected.
/// </summary>
public sealed record DeviceRow(
    string DeviceId,
    string UserId,
    string Name,
    string Platform,
    string AppVersion,
    long LastSeen,
    string? PushKind = null,
    string? PushEndpoint = null,
    long? CreatedAt = null,
    string? Model = null,
    string? CapsJson = null,
    bool Revoked = false);

/// <summary>
/// The device registration write input: everything
/// <see cref="JellyPlayDatabase.UpsertDevice"/> applies. Deliberately carries
/// NO revoked field — the upsert never touches the flag (a revoked device
/// cannot un-revoke itself by re-registering); only
/// <see cref="JellyPlayDatabase.SetDeviceRevoked"/> flips it. Read rows come
/// back as <see cref="DeviceRow"/>.
/// </summary>
public sealed record DeviceWrite(
    string DeviceId,
    string UserId,
    string Name,
    string Platform,
    string AppVersion,
    long LastSeen,
    string? PushKind = null,
    string? PushEndpoint = null,
    long? CreatedAt = null,
    string? Model = null,
    string? CapsJson = null);

/// <summary>
/// One stored settings snapshot (restore point): the full settings payload of
/// one user (all profiles) at <see cref="CreatedAt"/> (unix ms), captured by
/// <see cref="Origin"/> ("admin-push" | "profile-copy" | "manual"). Rolling
/// keep-last-N is enforced at insert time; age retention by the daily prune.
/// </summary>
public sealed record SnapshotRow(
    long Id,
    string UserId,
    long CreatedAt,
    string Origin,
    int Keys,
    long Bytes);

public sealed record MessageRow(
    string Id,
    string Title,
    string Body,
    string Color,
    string LinkUrl,
    string LinkLabel,
    string AudienceJson,
    long? StartsAt,
    long? EndsAt,
    int OrderIndex,
    long CreatedAt);

public sealed record BookmarkRow(
    string Id,
    string UserId,
    string ItemId,
    double Position,
    int? ChapterIndex,
    string Label,
    string Notes,
    long CreatedAt,
    long UpdatedAt);

/// <summary>
/// One stored Seerr session. <see cref="CookiesPayload"/> is the raw stored
/// column value: AES-GCM ciphertext with a version prefix (see
/// Services/Seerr/SecretBox) for current rows, or legacy bare UTF-8 JSON for
/// rows written before encryption-at-rest (re-encrypted on next write).
/// </summary>
public sealed record SeerrSessionRow(
    string UserId,
    byte[] CookiesPayload,
    long CreatedAt,
    long LastValidatedAt);

/// <summary>Tri-state admin defaults payload for one scope ("global" or a user id).</summary>
public sealed record AdminDefaultsRow(string Scope, byte[] Payload, long UpdatedAt);

/// <summary>
/// One recorded settings-sync operation (push/pull/reset/wipe) for
/// observability. <see cref="Id"/> doubles as the "seq" the history endpoint
/// reports; Ts is unix ms. RejectsJson is a capped JSON array of
/// <c>{ns,key,reason}</c>. <see cref="FromSeq"/>/ToSeq (schema v6) bracket the
/// change-log range the operation covers — for a push the head before/after
/// the batch, for a pull the requested <c>since</c> cursor through the served
/// head, for a reset/wipe the tombstone batch it appended (non-empty since
/// the change log records deletions too).
/// </summary>
public sealed record SyncHistoryRow(
    long Id,
    string UserId,
    string DeviceId,
    long Ts,
    string Op,
    int KeysApplied,
    int KeysRejected,
    long Bytes,
    string? RejectsJson,
    long? FromSeq = null,
    long? ToSeq = null)
{
    /// <summary>Whether the operation brackets a non-empty change-log range (schema v6+; false on legacy rows and no-op operations).</summary>
    public bool HasRange => FromSeq is not null && ToSeq is not null && FromSeq != ToSeq;
}

/// <summary>Latest sync operation per device, folded from sync_history.</summary>
public sealed record DeviceSyncSummary(string DeviceId, long LastSyncAt, string LastOp);

/// <summary>
/// The sync-status bundle: everything a status poll needs (change-log head,
/// footprint, namespace rollups, per-device fold) resolved in ONE connection
/// by <see cref="JellyPlayDatabase.GetSyncStatusBundle"/>.
/// </summary>
public sealed record SyncStatusBundle(
    long Head,
    int KeyCount,
    long TotalBytes,
    IReadOnlyList<NamespaceFootprint> Namespaces,
    IReadOnlyList<DeviceSyncSummary> PerDevice);

/// <summary>One recorded operation together with its resolved per-key diff (the audit export's fold).</summary>
public sealed record SyncHistoryWithKeys(SyncHistoryRow Row, IReadOnlyList<ChangeLogEntry> Keys);

/// <summary>Per-user rollup of sync_history: most recent operation and distinct device count.</summary>
public sealed record UserSyncSummary(string UserId, long LastSyncAt, int DeviceCount);

/// <summary>Key/byte footprint of one settings namespace (all profiles folded).</summary>
public sealed record NamespaceFootprint(string Ns, int Keys, long Bytes);

/// <summary>Key/byte footprint of one user's whole settings store.</summary>
public sealed record UserFootprint(string UserId, int Keys, long Bytes);

/// <summary>
/// One finished playback session (analytics v1). <see cref="StartedAt"/> is
/// the minute-bucketed start (unix ms) — the dedup key is (UserId, ItemId,
/// StartedAt), so at most one row per user/item/minute; EndedAt is the real
/// end time (unix ms). PlayMethod is the host enum name ("DirectPlay",
/// "DirectStream", "Transcode"); TranscodeReasonsJson is a JSON array of the
/// named [Flags] bits (see Helpers.TranscodeReasonNames).
/// </summary>
public sealed record PlaybackSessionRow(
    long Id,
    string UserId,
    string ItemId,
    string ItemName,
    string ItemType,
    string? SeriesName,
    string PlayMethod,
    string? VideoCodec,
    string? AudioCodec,
    long? Bitrate,
    string? TranscodeReasonsJson,
    long PositionTicks,
    long? DurationTicks,
    long StartedAt,
    long EndedAt,
    string? ClientName,
    string? DeviceName);

/// <summary>
/// One daily per-user aggregate over playback_sessions, keyed (Day, UserId)
/// with Day a UTC yyyy-MM-dd string. PlaySeconds counts wall time; the
/// counts split direct vs transcode by PlayMethod. Recomputed idempotently
/// for the recent window by the analytics maintenance task; retained forever
/// (raw sessions are pruned, rollups are not).
/// </summary>
public sealed record PlaybackRollupRow(
    string Day,
    string UserId,
    long ItemsPlayed,
    long PlaySeconds,
    long TranscodeSeconds,
    long DirectCount,
    long TranscodeCount);

/// <summary>One aggregated top-item row folded from raw playback sessions.</summary>
public sealed record PlaybackTopItemRow(
    string ItemId,
    string ItemName,
    string ItemType,
    long Plays,
    long PlaySeconds);
