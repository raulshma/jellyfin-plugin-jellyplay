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

/// <summary>Incoming write from a client; <see cref="UpdatedAt"/> drives last-write-wins.</summary>
public sealed record SettingWrite(
    string Ns,
    string Key,
    int SchemaVersion,
    long UpdatedAt,
    string DeviceId,
    byte[] Value);

public sealed record AppliedSetting(string Ns, string Key, long UpdatedAt, long Seq);

public sealed record RejectedSetting(string Ns, string Key, string Reason);

public sealed record UpsertResult(IReadOnlyList<AppliedSetting> Applied, IReadOnlyList<RejectedSetting> Rejected);

/// <summary>A change-log entry; <see cref="Seq"/> is the global cursor used by `changed?since=`.</summary>
public sealed record ChangeLogEntry(long Seq, string UserId, string Profile, string Ns, string Key, long UpdatedAt);

public sealed record DeviceRow(
    string DeviceId,
    string UserId,
    string Name,
    string Platform,
    string AppVersion,
    long LastSeen);

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

public sealed record SeerrSessionRow(
    string UserId,
    string CookiesJson,
    long CreatedAt,
    long LastValidatedAt);

/// <summary>Tri-state admin defaults payload for one scope ("global" or a user id).</summary>
public sealed record AdminDefaultsRow(string Scope, byte[] Payload, long UpdatedAt);
