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
}

public sealed class SettingsBatchResponse
{
    public long Head { get; set; }

    public List<AppliedSettingDto> Applied { get; set; } = new();

    public List<RejectedSettingDto> Rejected { get; set; } = new();
}

public sealed record AppliedSettingDto(string Ns, string Key, long UpdatedAt, long Seq);

public sealed record RejectedSettingDto(string Ns, string Key, string Reason);

public sealed class ChangedSettingsRequest
{
    public long Since { get; set; }

    public string? Profile { get; set; }
}

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
