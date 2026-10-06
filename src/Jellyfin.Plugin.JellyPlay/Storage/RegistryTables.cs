using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyPlay.Storage.Models;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.JellyPlay.Storage;

/// <summary>Devices, messages, bookmarks, Seerr sessions and backup restore — the per-user registry half of JellyPlayDatabase.</summary>
public sealed partial class JellyPlayDatabase
{
    // ------------------------------------------------------------------
    // Devices
    // ------------------------------------------------------------------

    public void UpsertDevice(DeviceRow device)
    {
        using (_lock.Write())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare(
                   $@"insert into {DevicesTable} (DeviceId, UserId, Name, Platform, AppVersion, LastSeen, PushKind, PushEndpoint, CreatedAt)
                      values (@DeviceId, @UserId, @Name, @Platform, @AppVersion, @LastSeen, @PushKind, @PushEndpoint, @CreatedAt)
                      on conflict (DeviceId) do update set
                          UserId = @UserId, Name = @Name, Platform = @Platform,
                          AppVersion = @AppVersion, LastSeen = @LastSeen,
                          PushKind = @PushKind, PushEndpoint = @PushEndpoint, CreatedAt = @CreatedAt"))
        {
            statement.Bind("@DeviceId", device.DeviceId);
            statement.Bind("@UserId", device.UserId);
            statement.Bind("@Name", device.Name);
            statement.Bind("@Platform", device.Platform);
            statement.Bind("@AppVersion", device.AppVersion);
            statement.Bind("@LastSeen", device.LastSeen);
            BindNullableText(statement, "@PushKind", device.PushKind);
            BindNullableText(statement, "@PushEndpoint", device.PushEndpoint);
            BindNullable(statement, "@CreatedAt", device.CreatedAt);
            statement.ExecuteNonQuery();
        }
    }

    public bool DeleteDevice(string userId, string deviceId)
    {
        using (_lock.Write())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare($"delete from {DevicesTable} where DeviceId = @DeviceId and UserId = @UserId"))
        {
            statement.Bind("@DeviceId", deviceId);
            statement.Bind("@UserId", userId);
            return statement.ExecuteNonQuery() > 0;
        }
    }

    public IReadOnlyList<DeviceRow> GetDevices(string userId)
    {
        using (_lock.Read())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare(
                   $"{DeviceColumns} where UserId = @UserId order by LastSeen desc"))
        {
            statement.Bind("@UserId", userId);
            return statement.Select(ReadDeviceRow).ToList();
        }
    }

    /// <summary>One device row by id, any owner (registration re-POST resolves preservation itself).</summary>
    public DeviceRow? GetDeviceById(string deviceId)
    {
        using (_lock.Read())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare($"{DeviceColumns} where DeviceId = @DeviceId"))
        {
            statement.Bind("@DeviceId", deviceId);
            foreach (var row in statement.Select(ReadDeviceRow))
            {
                return row;
            }

            return null;
        }
    }

    /// <summary>
    /// Push-registered devices (PushKind + PushEndpoint both set). Null
    /// <paramref name="userIds"/> = every user ("all" audience); an empty
    /// collection matches nobody — callers treat null and [] differently.
    /// </summary>
    public IReadOnlyList<DeviceRow> GetPushDevices(IReadOnlyCollection<string>? userIds)
    {
        if (userIds is { Count: 0 })
        {
            return Array.Empty<DeviceRow>();
        }

        var filters = new List<string> { "PushKind is not null", "PushEndpoint is not null", "PushEndpoint != ''" };
        if (userIds is not null)
        {
            var placeholders = new List<string>(userIds.Count);
            var index = 0;
            foreach (var userId in userIds)
            {
                placeholders.Add($"@User{index}");
                index++;
            }

            filters.Add($"UserId in ({string.Join(", ", placeholders)})");
        }

        using (_lock.Read())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare($"{DeviceColumns} where {string.Join(" and ", filters)} order by LastSeen desc"))
        {
            if (userIds is not null)
            {
                var index = 0;
                foreach (var userId in userIds)
                {
                    statement.Bind($"@User{index}", userId);
                    index++;
                }
            }

            return statement.Select(ReadDeviceRow).ToList();
        }
    }

    /// <summary>Every push-registered device across users (admin overview).</summary>
    public IReadOnlyList<DeviceRow> GetAllPushDevices()
    {
        using (_lock.Read())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare(
                   $"{DeviceColumns} where PushKind is not null and PushEndpoint is not null and PushEndpoint != '' order by LastSeen desc"))
        {
            return statement.Select(ReadDeviceRow).ToList();
        }
    }

    // ------------------------------------------------------------------
    // Messages
    // ------------------------------------------------------------------

    public void UpsertMessage(MessageRow message)
    {
        using (_lock.Write())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare(
                   $@"insert into {MessagesTable}
                      (Id, Title, Body, Color, LinkUrl, LinkLabel, AudienceJson, StartsAt, EndsAt, OrderIndex, CreatedAt)
                      values (@Id, @Title, @Body, @Color, @LinkUrl, @LinkLabel, @AudienceJson, @StartsAt, @EndsAt, @OrderIndex, @CreatedAt)
                      on conflict (Id) do update set
                          Title = @Title, Body = @Body, Color = @Color, LinkUrl = @LinkUrl, LinkLabel = @LinkLabel,
                          AudienceJson = @AudienceJson, StartsAt = @StartsAt, EndsAt = @EndsAt, OrderIndex = @OrderIndex"))
        {
            statement.Bind("@Id", message.Id);
            statement.Bind("@Title", message.Title);
            statement.Bind("@Body", message.Body);
            statement.Bind("@Color", message.Color);
            statement.Bind("@LinkUrl", message.LinkUrl);
            statement.Bind("@LinkLabel", message.LinkLabel);
            statement.Bind("@AudienceJson", message.AudienceJson);
            BindNullable(statement, "@StartsAt", message.StartsAt);
            BindNullable(statement, "@EndsAt", message.EndsAt);
            statement.Bind("@OrderIndex", message.OrderIndex);
            statement.Bind("@CreatedAt", message.CreatedAt);
            statement.ExecuteNonQuery();
        }
    }

    public bool DeleteMessage(string messageId)
    {
        using (_lock.Write())
        using (var connection = CreateConnection())
        {
            using (var statement = connection.Prepare($"delete from {MessageReadsTable} where MessageId = @Id"))
            {
                statement.Bind("@Id", messageId);
                statement.ExecuteNonQuery();
            }

            using (var statement = connection.Prepare($"delete from {MessagesTable} where Id = @Id"))
            {
                statement.Bind("@Id", messageId);
                return statement.ExecuteNonQuery() > 0;
            }
        }
    }

    public IReadOnlyList<MessageRow> GetMessages()
    {
        using (_lock.Read())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare(
                   $"select Id, Title, Body, Color, LinkUrl, LinkLabel, AudienceJson, StartsAt, EndsAt, OrderIndex, CreatedAt from {MessagesTable} order by OrderIndex desc, CreatedAt desc"))
        {
            return statement.Select(ReadMessageRow).ToList();
        }
    }

    public void MarkMessageRead(string userId, string messageId, long readAt)
    {
        using (_lock.Write())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare(
                   $@"insert into {MessageReadsTable} (UserId, MessageId, ReadAt) values (@UserId, @MessageId, @ReadAt)
                      on conflict (UserId, MessageId) do nothing"))
        {
            statement.Bind("@UserId", userId);
            statement.Bind("@MessageId", messageId);
            statement.Bind("@ReadAt", readAt);
            statement.ExecuteNonQuery();
        }
    }

    public IReadOnlySet<string> GetReadMessageIds(string userId)
    {
        using (_lock.Read())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare($"select MessageId from {MessageReadsTable} where UserId = @UserId"))
        {
            statement.Bind("@UserId", userId);
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in statement.Select(row => row.GetString(0)))
            {
                set.Add(id);
            }

            return set;
        }
    }

    // ------------------------------------------------------------------
    // Bookmarks
    // ------------------------------------------------------------------

    public void UpsertBookmark(BookmarkRow bookmark)
    {
        using (_lock.Write())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare(
                   $@"insert into {BookmarksTable}
                      (Id, UserId, ItemId, Position, ChapterIndex, Label, Notes, CreatedAt, UpdatedAt)
                      values (@Id, @UserId, @ItemId, @Position, @ChapterIndex, @Label, @Notes, @CreatedAt, @UpdatedAt)
                      on conflict (Id) do update set
                          Position = @Position, ChapterIndex = @ChapterIndex, Label = @Label, Notes = @Notes, UpdatedAt = @UpdatedAt
                      where {BookmarksTable}.UserId = @UserId"))
        {
            statement.Bind("@Id", bookmark.Id);
            statement.Bind("@UserId", bookmark.UserId);
            statement.Bind("@ItemId", bookmark.ItemId);
            statement.Bind("@Position", bookmark.Position);
            BindNullable(statement, "@ChapterIndex", bookmark.ChapterIndex);
            statement.Bind("@Label", bookmark.Label);
            statement.Bind("@Notes", bookmark.Notes);
            statement.Bind("@CreatedAt", bookmark.CreatedAt);
            statement.Bind("@UpdatedAt", bookmark.UpdatedAt);
            statement.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<BookmarkRow> GetBookmarks(string userId, string? itemId)
    {
        using (_lock.Read())
        using (var connection = CreateConnection())
        {
            if (itemId is null)
            {
                using var statement = connection.Prepare(
                    $"select Id, UserId, ItemId, Position, ChapterIndex, Label, Notes, CreatedAt, UpdatedAt from {BookmarksTable} where UserId = @UserId order by ItemId, UpdatedAt desc");
                statement.Bind("@UserId", userId);
                return statement.Select(ReadBookmarkRow).ToList();
            }

            using var byItem = connection.Prepare(
                $"select Id, UserId, ItemId, Position, ChapterIndex, Label, Notes, CreatedAt, UpdatedAt from {BookmarksTable} where UserId = @UserId and ItemId = @ItemId order by UpdatedAt desc");
            byItem.Bind("@UserId", userId);
            byItem.Bind("@ItemId", itemId);
            return byItem.Select(ReadBookmarkRow).ToList();
        }
    }

    public bool DeleteBookmark(string userId, string bookmarkId)
    {
        using (_lock.Write())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare($"delete from {BookmarksTable} where Id = @Id and UserId = @UserId"))
        {
            statement.Bind("@Id", bookmarkId);
            statement.Bind("@UserId", userId);
            return statement.ExecuteNonQuery() > 0;
        }
    }

    // ------------------------------------------------------------------
    // Seerr sessions
    // ------------------------------------------------------------------

    /// <summary>
    /// Raw stored session payload: BLOB for encrypted rows, the legacy TEXT
    /// row surfaces as UTF-8 bytes (detection is the payload's version byte).
    /// </summary>
    public SeerrSessionRow? GetSeerrSession(string userId)
    {
        using (_lock.Read())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare(
                   $"select UserId, CookiesJson, CreatedAt, LastValidatedAt from {SeerrSessionsTable} where UserId = @UserId"))
        {
            statement.Bind("@UserId", userId);
            foreach (var row in statement.Select(row => new SeerrSessionRow(
                         row.GetString(0),
                         PayloadBytes(row.GetValue(1)),
                         row.GetInt64(2),
                         row.GetInt64(3))))
            {
                return row;
            }

            return null;
        }
    }

    private static byte[] PayloadBytes(object? value) => value switch
    {
        byte[] bytes => bytes,
        string text => System.Text.Encoding.UTF8.GetBytes(text),
        _ => []
    };

    public void UpsertSeerrSession(SeerrSessionRow session)
    {
        using (_lock.Write())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare(
                   $@"insert into {SeerrSessionsTable} (UserId, CookiesJson, CreatedAt, LastValidatedAt)
                      values (@UserId, @CookiesPayload, @CreatedAt, @LastValidatedAt)
                      on conflict (UserId) do update set
                          CookiesJson = @CookiesPayload, CreatedAt = @CreatedAt, LastValidatedAt = @LastValidatedAt"))
        {
            statement.Bind("@UserId", session.UserId);
            statement.Bind("@CookiesPayload", session.CookiesPayload);
            statement.Bind("@CreatedAt", session.CreatedAt);
            statement.Bind("@LastValidatedAt", session.LastValidatedAt);
            statement.ExecuteNonQuery();
        }
    }

    public bool DeleteSeerrSession(string userId)
    {
        using (_lock.Write())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare($"delete from {SeerrSessionsTable} where UserId = @UserId"))
        {
            statement.Bind("@UserId", userId);
            return statement.ExecuteNonQuery() > 0;
        }
    }

    // ------------------------------------------------------------------
    // Backup / maintenance
    // ------------------------------------------------------------------

    public IReadOnlyList<AdminDefaultsRow> GetAllAdminDefaults()
    {
        using (_lock.Read())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare($"select Scope, Payload, UpdatedAt from {AdminDefaultsTable}"))
        {
            return statement.Select(row => new AdminDefaultsRow(row.GetString(0), (byte[])row.GetValue(1), row.GetInt64(2))).ToList();
        }
    }

    public void RestoreAdminDefaults(IReadOnlyList<AdminDefaultsRow> rows)
    {
        using (_lock.Write())
        using (var connection = CreateConnection())
        using (var transaction = connection.BeginTransaction())
        {
            using (var clear = connection.Prepare($"delete from {AdminDefaultsTable}"))
            {
                clear.ExecuteNonQuery();
            }

            foreach (var row in rows)
            {
                using var statement = connection.Prepare(
                    "insert into admin_defaults (Scope, Payload, UpdatedAt) values (@Scope, @Payload, @UpdatedAt)");
                statement.Bind("@Scope", row.Scope);
                statement.Bind("@Payload", row.Payload);
                statement.Bind("@UpdatedAt", row.UpdatedAt);
                statement.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

}
