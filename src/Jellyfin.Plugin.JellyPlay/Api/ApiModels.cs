using System.Collections.Generic;
using System.Text.Json;

namespace Jellyfin.Plugin.JellyPlay.Api;

public sealed record CapabilitiesResponse(
    int ContractVersion,
    string PluginVersion,
    IReadOnlyList<string> Features,
    long ServerNow,
    IReadOnlyList<string> DeviceProfiles,
    bool ServerSimilarPipeline = false);

public sealed class SettingsWriteDto
{
    public string Ns { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    public int SchemaVersion { get; set; } = 1;

    public long UpdatedAt { get; set; }

    public JsonElement Value { get; set; }

    /// <summary>
    /// ADDITIVE (tombstones): true marks a delete — the key is removed, a
    /// 'del' change-log row is appended, and the deletion roams to peers via
    /// the delta's deleted[] list. The value is ignored; LWW still applies
    /// (a delete loses to a strictly newer put and vice versa).
    /// </summary>
    public bool Deleted { get; set; }
}

public sealed class SettingsBatchRequest
{
    /// <summary>Device profile: "", "desktop", "phone", "tv" (client-defined classes).</summary>
    public string? Profile { get; set; }

    public string? DeviceId { get; set; }

    public List<SettingsWriteDto> Writes { get; set; } = new();
}

public sealed class SettingsEntryDto
{
    public string Ns { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    public int SchemaVersion { get; set; }

    public long UpdatedAt { get; set; }

    public string DeviceId { get; set; } = string.Empty;

    public string Profile { get; set; } = string.Empty;

    public JsonElement Value { get; set; }
}

/// <summary>A key deleted since a delta cursor (the delta's deleted[] half).</summary>
public sealed record DeletedKeyDto(string Ns, string Key);

public sealed class SettingsSnapshotResponse
{
    public long Head { get; set; }

    public string Profile { get; set; } = string.Empty;

    public List<SettingsEntryDto> Settings { get; set; } = new();

    /// <summary>
    /// ADDITIVE (tombstones), populated only by the delta endpoint: keys
    /// deleted since the requested cursor. Old clients ignore it.
    /// </summary>
    public List<DeletedKeyDto>? Deleted { get; set; }

    /// <summary>
    /// ADDITIVE (pagination), populated only when more rows follow: the opaque
    /// cursor to pass as ?cursor= for the next page. Absent on the last page
    /// (and on unpaginated responses) — old clients ignore it.
    /// </summary>
    public long? NextCursor { get; set; }

    /// <summary>
    /// ADDITIVE, populated only by the resolved-profile endpoint: the
    /// tri-state mode per key ("ns/key") for this user after the user-scope
    /// merge — "forced" (the resolved value came from a forced default),
    /// "suggested" (filled by a suggested default), "unset" (user-owned:
    /// base or profile overlay, including a suggested default that lost to an
    /// existing user value). Enables client-side forced-lock UI. Omitted
    /// (null) on plain snapshots.
    /// </summary>
    public Dictionary<string, string>? Modes { get; set; }
}

public sealed class SettingsBatchResponse
{
    public long Head { get; set; }

    public List<AppliedSettingDto> Applied { get; set; } = new();

    public List<RejectedSettingDto> Rejected { get; set; } = new();
}

public sealed record AppliedSettingDto(string Ns, string Key, long UpdatedAt, long Seq, bool Deleted = false);

public sealed record RejectedSettingDto(string Ns, string Key, string Reason);

// ---------------------------------------------------------------------------
// Sync observability (GET jellyplay/sync/*, GET jellyplay/admin/sync/overview)
// ---------------------------------------------------------------------------

public sealed record SyncStatusResponse(
    long Head,
    int Keys,
    long Bytes,
    int QuotaBytes,
    int QuotaKeys,
    int HistoryRetentionDays,
    IReadOnlyList<SyncNamespaceInfo> Namespaces,
    IReadOnlyList<SyncDeviceSummary> PerDevice);

public sealed record SyncNamespaceInfo(string Ns, int Keys, long Bytes);

public sealed record SyncDeviceSummary(string DeviceId, long LastSyncAt, string LastOp);

public sealed record SyncHistoryResponse(IReadOnlyList<SyncHistoryEntryDto> Entries);

public sealed record SyncHistoryEntryDto(
    long Seq,
    long Ts,
    string DeviceId,
    string Op,
    int KeysApplied,
    int KeysRejected,
    IReadOnlyList<SyncRejectDto>? Rejects,
    long? FromSeq = null,
    long? ToSeq = null);

public sealed record SyncRejectDto(string Ns, string Key, string Reason);

/// <summary>
/// GET jellyplay/sync/history/{seq}/keys — the per-key diff of one of the
/// caller's recorded operations: the change-log rows in (fromSeq, toSeq],
/// newest-first. Since tombstones (schema v7) a reset/wipe range lists the
/// keys it removed; only rows without a usable range (zero-width or pre-v7)
/// carry an empty key list.
/// </summary>
public sealed record SyncHistoryKeysResponse(long Seq, string Op, IReadOnlyList<SyncHistoryKeyDto> Keys);

public sealed record SyncHistoryKeyDto(string Ns, string Key, long UpdatedAt);

public sealed record AdminSyncOverviewResponse(IReadOnlyList<AdminSyncUserRow> Users);

public sealed record AdminSyncUserRow(
    string UserId,
    string UserName,
    int Keys,
    long Bytes,
    long? LastSyncAt,
    int DeviceCount);

// ---------------------------------------------------------------------------
// Admin drill-down / live monitor / preview / audit (Phase 4, additive)
// ---------------------------------------------------------------------------

/// <summary>One host user reference for the admin pickers (dashboard user + profile selectors).</summary>
public sealed record AdminUserRef(string UserId, string UserName);

public sealed record AdminUserListResponse(IReadOnlyList<AdminUserRef> Users);

/// <summary>
/// One device row as the ADMIN drill-down sees it: identity and liveness
/// only — the push endpoint URL (a secret) is structurally absent, unlike the
/// owner-scoped <see cref="DeviceDto"/>.
/// </summary>
public sealed record AdminDeviceRow(
    string DeviceId,
    string Name,
    string Platform,
    string AppVersion,
    long LastSeen,
    string? Model = null,
    IReadOnlyList<string>? Caps = null,
    bool Revoked = false);

/// <summary>Per-user admin drill-down: quota status plus the device list (no push secrets).</summary>
public sealed record AdminUserDrilldownResponse(
    string UserId,
    string UserName,
    SyncStatusResponse Status,
    IReadOnlyList<AdminDeviceRow> Devices);

/// <summary>One audit-exported operation with its per-key diff folded in.</summary>
public sealed record AuditEntryDto(
    long Seq,
    long Ts,
    string DeviceId,
    string Op,
    int KeysApplied,
    int KeysRejected,
    IReadOnlyList<SyncRejectDto>? Rejects,
    long? FromSeq,
    long? ToSeq,
    IReadOnlyList<SyncHistoryKeyDto> Keys);

/// <summary>The admin sync audit export (JSON shape): newest-first history, each entry carrying its key diff.</summary>
public sealed record AuditExportResponse(
    string UserId,
    long ExportedAt,
    IReadOnlyList<AuditEntryDto> History);

public sealed class BroadcastRequest
{
    public string Title { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public string? Url { get; set; }
}

public sealed class DeviceRegistrationRequest
{
    public string DeviceId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Platform { get; set; } = string.Empty;

    public string AppVersion { get; set; } = string.Empty;

    /// <summary>ADDITIVE (registry v7): self-reported hardware model (informational).</summary>
    public string? Model { get; set; }

    /// <summary>
    /// ADDITIVE (registry v7): self-reported capability strings, e.g.
    /// ["silent-push"] — the sync-nudge push is delivered only to devices
    /// whose caps include it. Overwritten on each registration.
    /// </summary>
    public List<string>? Caps { get; set; }

    /// <summary>
    /// Optional push registration, bound raw so the three wire shapes stay
    /// distinguishable: object = validate and overwrite; absent = preserve any
    /// existing one; explicit JSON null = detach (clear the registration, keep
    /// the device row).
    /// </summary>
    public JsonElement? Push { get; set; }
}

/// <summary>Body of POST jellyplay/devices/{id} (registry v7 rename); null fields keep their stored value.</summary>
public sealed class DeviceRenameRequest
{
    public string? Name { get; set; }

    public string? Model { get; set; }
}

/// <summary>Inbound push registration block of <see cref="DeviceRegistrationRequest"/>.</summary>
public sealed class DevicePushRegistration
{
    /// <summary>"generic" (raw JSON POST) | "ntfy" (ntfy publish URL) | "fcm".</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Full publish endpoint URL (secret; only ever echoed to its owner).</summary>
    public string Endpoint { get; set; } = string.Empty;
}

/// <summary>The push block of a device row, only ever serialized to its owning user.</summary>
public sealed record DevicePushDto(string Kind, string Endpoint);

/// <summary>
/// Device row as returned by GET jellyplay/devices (push omitted when
/// unregistered). Model/caps/revoked are additive (registry v7): caps is the
/// parsed self-reported capability list, revoked marks a device whose
/// settings writes are rejected (the DELETE route revokes + wipes instead of
/// removing the row).
/// </summary>
public sealed record DeviceDto(
    string DeviceId,
    string UserId,
    string Name,
    string Platform,
    string AppVersion,
    long LastSeen,
    DevicePushDto? Push,
    string? Model = null,
    IReadOnlyList<string>? Caps = null,
    bool Revoked = false);

/// <summary>Admin push-overview row: endpoint URLs are secrets — only the host is ever surfaced.</summary>
public sealed record AdminPushDeviceDto(
    string DeviceId,
    string UserName,
    string DeviceName,
    string Kind,
    string EndpointHost,
    long RegisteredAt);

/// <summary>Response shape for GET jellyplay/admin/push/overview. FCM readiness is reported as a boolean only — the service-account key is never surfaced.</summary>
public sealed record AdminPushOverviewResponse(bool Enabled, bool FcmConfigured, IReadOnlyList<AdminPushDeviceDto> Devices);

// ---------------------------------------------------------------------------
// Restore points (GET/POST jellyplay/settings/snapshots, POST …/{id}/restore)
// ---------------------------------------------------------------------------

/// <summary>One restore point (metadata; payloads ride the restore action).</summary>
public sealed record SnapshotDto(long Id, long CreatedAt, string Origin, int Keys, long Bytes);

public sealed record SnapshotCreateResponse(long Id);

// ---------------------------------------------------------------------------
// Settings export / import (GET jellyplay/settings/export, POST …/import)
// ---------------------------------------------------------------------------

/// <summary>
/// The portable settings bundle: every stored profile's rows plus the
/// tri-state modes maps (per profile) and the settings-catalog stamp the
/// export was produced against. The import route takes the same shape back.
/// </summary>
public sealed class SettingsExportBundle
{
    public long ExportedAt { get; set; }

    public string PluginVersion { get; set; } = string.Empty;

    public int CatalogSchema { get; set; }

    public int CatalogSettings { get; set; }

    public List<SettingsExportProfile> Profiles { get; set; } = new();

    /// <summary>Per-profile modes map ("ns/key" → "forced"|"suggested"|"unset") from the resolve merge.</summary>
    public Dictionary<string, Dictionary<string, string>> Modes { get; set; } = new();
}

public sealed class SettingsExportProfile
{
    public string Profile { get; set; } = string.Empty;

    public List<SettingsEntryDto> Settings { get; set; } = new();
}

public sealed record MessageDto(
    string Id,
    string Title,
    string Body,
    string Color,
    string LinkUrl,
    string LinkLabel,
    long? StartsAt,
    long? EndsAt,
    int OrderIndex,
    long CreatedAt,
    bool Read);

/// <summary>
/// Message row as the admin registry (GET jellyplay/admin/messages) returns
/// it: the storage record's fields verbatim — audienceJson is the stored
/// audience payload text and startsAt/endsAt are omitted from the wire when
/// null. Byte-identical to the pre-projection raw-row shape, by design: this
/// record exists only so the row→wire projection lives in the service (the
/// inbox's <see cref="MessageDto"/> is the user-facing projection).
/// </summary>
public sealed record AdminMessageDto(
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

public sealed class MessageAdminRequest
{
    public string? Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public string Color { get; set; } = string.Empty;

    public string? LinkUrl { get; set; }

    public string? LinkLabel { get; set; }

    /// <summary>{"type":"all"|"admins"|"users","userIds":[..]}</summary>
    public AudiencePayload Audience { get; set; } = new();

    public long? StartsAt { get; set; }

    public long? EndsAt { get; set; }

    public int OrderIndex { get; set; }
}

public sealed class AudiencePayload
{
    public string Type { get; set; } = "all";

    public List<string> UserIds { get; set; } = new();
}

public sealed record NewMediaEventPayload(
    string Type,
    string ItemId,
    string? SeriesId,
    int? SeasonIndex,
    string Title,
    int EpisodeCount,
    string? LibraryId,
    long Ts);

public sealed record BroadcastEventPayload(string Type, string Title, string Body, string? Url, long Ts);

public sealed record SimpleEventPayload(string Type, string Username, long Ts);

public sealed record BookmarkDto(
    string Id,
    string ItemId,
    double Position,
    int? ChapterIndex,
    string Label,
    string Notes,
    long CreatedAt,
    long UpdatedAt);

public sealed class BookmarkRequest
{
    public string? Id { get; set; }

    public double Position { get; set; }

    public int? ChapterIndex { get; set; }

    public string Label { get; set; } = string.Empty;

    public string Notes { get; set; } = string.Empty;
}

// ---------------------------------------------------------------------------
// Analytics (admin-only reporting surface, v1)
// ---------------------------------------------------------------------------

public sealed record AnalyticsOverviewResponse(
    int Days,
    AnalyticsTotals Totals,
    IReadOnlyList<AnalyticsPerDayRow> PerDay,
    IReadOnlyList<AnalyticsPerUserRow> PerUser,
    IReadOnlyList<AnalyticsTopItemRow> TopItems);

public sealed record AnalyticsTotals(
    long Plays,
    long PlaySeconds,
    long TranscodeSeconds,
    long UniqueUsers,
    long UniqueItems);

public sealed record AnalyticsPerDayRow(string Day, long Plays, long PlaySeconds, long TranscodeSeconds);

public sealed record AnalyticsPerUserRow(string UserId, string UserName, long Plays, long PlaySeconds, long TranscodeSeconds);

public sealed record AnalyticsTopItemRow(string ItemId, string ItemName, string ItemType, long Plays, long PlaySeconds);

public sealed record AnalyticsSessionsResponse(IReadOnlyList<AnalyticsSessionDto> Sessions);

public sealed record AnalyticsSessionDto(
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
    string[]? TranscodeReasons,
    long PositionTicks,
    long? DurationTicks,
    long StartedAt,
    long EndedAt,
    string? ClientName,
    string? DeviceName);

// ---------------------------------------------------------------------------
// Analytics: per-user surface ("Your watching", GET jellyplay/analytics/me)
// ---------------------------------------------------------------------------

/// <summary>
/// The caller's own playback activity — the admin overview's shape minus
/// perUser, scoped strictly to the caller's rows. Totals drop uniqueUsers
/// (it is always the caller).
/// </summary>
public sealed record AnalyticsMeResponse(
    int Days,
    AnalyticsMeTotals Totals,
    IReadOnlyList<AnalyticsPerDayRow> PerDay,
    IReadOnlyList<AnalyticsTopItemRow> TopItems);

public sealed record AnalyticsMeTotals(
    long Plays,
    long PlaySeconds,
    long TranscodeSeconds,
    long UniqueItems);
