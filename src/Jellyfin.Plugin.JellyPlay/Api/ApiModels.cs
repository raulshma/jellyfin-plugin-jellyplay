using System.Collections.Generic;
using System.Text.Json;

namespace Jellyfin.Plugin.JellyPlay.Api;

public sealed record CapabilitiesResponse(
    int ContractVersion,
    string PluginVersion,
    IReadOnlyList<string> Features,
    long ServerNow,
    IReadOnlyList<string> DeviceProfiles);

public sealed class SettingsWriteDto
{
    public string Ns { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    public int SchemaVersion { get; set; } = 1;

    public long UpdatedAt { get; set; }

    public JsonElement Value { get; set; }
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

public sealed class SettingsSnapshotResponse
{
    public long Head { get; set; }

    public string Profile { get; set; } = string.Empty;

    public List<SettingsEntryDto> Settings { get; set; } = new();

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

public sealed record AppliedSettingDto(string Ns, string Key, long UpdatedAt, long Seq);

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
/// newest-first. Reset rows (and any row without a usable range) carry an
/// empty key list.
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

    /// <summary>
    /// Optional push registration ("generic" | "ntfy" + full publish URL).
    /// Present = validate and overwrite; absent = preserve any existing one.
    /// </summary>
    public DevicePushRegistration? Push { get; set; }
}

/// <summary>Inbound push registration block of <see cref="DeviceRegistrationRequest"/>.</summary>
public sealed class DevicePushRegistration
{
    /// <summary>"generic" (raw JSON POST) | "ntfy" (ntfy publish URL).</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Full publish endpoint URL (secret; only ever echoed to its owner).</summary>
    public string Endpoint { get; set; } = string.Empty;
}

/// <summary>The push block of a device row, only ever serialized to its owning user.</summary>
public sealed record DevicePushDto(string Kind, string Endpoint);

/// <summary>Device row as returned by GET jellyplay/devices (push omitted when unregistered).</summary>
public sealed record DeviceDto(
    string DeviceId,
    string UserId,
    string Name,
    string Platform,
    string AppVersion,
    long LastSeen,
    DevicePushDto? Push);

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
