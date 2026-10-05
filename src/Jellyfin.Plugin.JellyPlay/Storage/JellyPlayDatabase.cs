using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.JellyPlay.Storage.Models;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.JellyPlay.Storage;

/// <summary>
/// The plugin's SQLite persistence. One file, WAL mode, guarded by a
/// ReaderWriterLockSlim. All value payloads are opaque blobs; this class never
/// interprets settings content. Per-user quotas are enforced here so every write
/// path gets them for free.
/// </summary>
public sealed class JellyPlayDatabase : IDisposable
{
    public const string BaseProfile = "";

    private const string SettingsTable = "settings";
    private const string ChangeLogTable = "change_log";
    private const string DevicesTable = "devices";
    private const string MessagesTable = "messages";
    private const string MessageReadsTable = "message_reads";
    private const string BookmarksTable = "bookmarks";
    private const string SeerrSessionsTable = "seerr_sessions";
    private const string AdminDefaultsTable = "admin_defaults";

    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly string _dbFilePath;
    private bool _disposed;

    public JellyPlayDatabase(string dataPath)
    {
        var dir = Path.Combine(dataPath, "plugins", "JellyPlay");
        Directory.CreateDirectory(dir);
        _dbFilePath = Path.Combine(dir, "jellyplay_plugin.db");
        Initialize(File.Exists(_dbFilePath));
    }

    public string DbFilePath => _dbFilePath;

    private void Initialize(bool fileExists)
    {
        using var connection = CreateConnection();
        connection.RunQueries(
        [
            $@"create table if not exists {SettingsTable} (
                UserId TEXT NOT NULL,
                Profile TEXT NOT NULL,
                Ns TEXT NOT NULL,
                Key TEXT NOT NULL,
                SchemaVersion INTEGER NOT NULL,
                UpdatedAt INTEGER NOT NULL,
                DeviceId TEXT NOT NULL,
                Value BLOB NOT NULL,
                primary key (UserId, Profile, Ns, Key))",
            $"create index if not exists idx_{SettingsTable}_user on {SettingsTable}(UserId, Profile)",
            $@"create table if not exists {ChangeLogTable} (
                Seq INTEGER PRIMARY KEY AUTOINCREMENT,
                UserId TEXT NOT NULL,
                Profile TEXT NOT NULL,
                Ns TEXT NOT NULL,
                Key TEXT NOT NULL,
                UpdatedAt INTEGER NOT NULL)",
            $"create index if not exists idx_{ChangeLogTable}_user on {ChangeLogTable}(UserId, Seq)",
            $@"create table if not exists {DevicesTable} (
                DeviceId TEXT PRIMARY KEY,
                UserId TEXT NOT NULL,
                Name TEXT NOT NULL,
                Platform TEXT NOT NULL,
                AppVersion TEXT NOT NULL,
                LastSeen INTEGER NOT NULL)",
            $"create index if not exists idx_{DevicesTable}_user on {DevicesTable}(UserId)",
            $@"create table if not exists {MessagesTable} (
                Id TEXT PRIMARY KEY,
                Title TEXT NOT NULL,
                Body TEXT NOT NULL,
                Color TEXT NOT NULL,
                LinkUrl TEXT NOT NULL,
                LinkLabel TEXT NOT NULL,
                AudienceJson TEXT NOT NULL,
                StartsAt INTEGER,
                EndsAt INTEGER,
                OrderIndex INTEGER NOT NULL,
                CreatedAt INTEGER NOT NULL)",
            $@"create table if not exists {MessageReadsTable} (
                UserId TEXT NOT NULL,
                MessageId TEXT NOT NULL,
                ReadAt INTEGER NOT NULL,
                primary key (UserId, MessageId))",
            $@"create table if not exists {BookmarksTable} (
                Id TEXT PRIMARY KEY,
                UserId TEXT NOT NULL,
                ItemId TEXT NOT NULL,
                Position REAL NOT NULL,
                ChapterIndex INTEGER,
                Label TEXT NOT NULL,
                Notes TEXT NOT NULL,
                CreatedAt INTEGER NOT NULL,
                UpdatedAt INTEGER NOT NULL)",
            $"create index if not exists idx_{BookmarksTable}_user on {BookmarksTable}(UserId, ItemId)",
            $@"create table if not exists {SeerrSessionsTable} (
                UserId TEXT PRIMARY KEY,
                CookiesJson TEXT NOT NULL,
                CreatedAt INTEGER NOT NULL,
                LastValidatedAt INTEGER NOT NULL)",
            $@"create table if not exists {AdminDefaultsTable} (
                Scope TEXT PRIMARY KEY,
                Payload BLOB NOT NULL,
                UpdatedAt INTEGER NOT NULL)"
        ]);
    }

    // ------------------------------------------------------------------
    // Settings
    // ------------------------------------------------------------------

    public IReadOnlyList<SettingRow> GetSettings(string userId, string profile)
    {
        using (_lock.Read())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare(
                   $"select UserId, Profile, Ns, Key, SchemaVersion, UpdatedAt, DeviceId, Value from {SettingsTable} where UserId = @UserId and Profile = @Profile order by Ns, Key"))
        {
            statement.Bind("@UserId", userId);
            statement.Bind("@Profile", profile);
            return statement.Select(ReadSettingRow).ToList();
        }
    }

    public IReadOnlyList<string> GetDistinctSettingUserIds()
    {
        using (_lock.Read())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare($"select distinct UserId from {SettingsTable}"))
        {
            return statement.Select(row => row.GetString(0)).ToList();
        }
    }

    public IReadOnlyList<SettingRow> GetSettingsForUsers(IEnumerable<string> userIds)
    {
        var rows = new List<SettingRow>();
        using (_lock.Read())
        using (var connection = CreateConnection())
        {
            foreach (var userId in userIds)
            {
                using (var statement = connection.Prepare(
                           $"select UserId, Profile, Ns, Key, SchemaVersion, UpdatedAt, DeviceId, Value from {SettingsTable} where UserId = @UserId"))
                {
                    statement.Bind("@UserId", userId);
                    rows.AddRange(statement.Select(ReadSettingRow));
                }
            }
        }

        return rows;
    }

    /// <summary>
    /// Applies a batch with per-key last-write-wins: an incoming write applies
    /// when its timestamp is strictly newer than the stored one; equal
    /// timestamps apply the incoming value so retries converge. Returns applied
    /// and rejected lists. Applied writes append one change-log row each.
    /// </summary>
    public UpsertResult UpsertSettings(
        string userId,
        string profile,
        IReadOnlyList<SettingWrite> writes,
        Quotas quotas)
    {
        var applied = new List<AppliedSetting>();
        var rejected = new List<RejectedSetting>();

        using (_lock.Write())
        using (var connection = CreateConnection())
        {
            using (var transaction = connection.BeginTransaction())
            {
                foreach (var write in writes)
                {
                    if (write.Value.Length > quotas.MaxKeyBytes)
                    {
                        rejected.Add(new RejectedSetting(write.Ns, write.Key, "key-too-large"));
                        continue;
                    }

                    byte[]? existingValue = null;
                    long existingUpdatedAt = 0;
                    using (var select = connection.Prepare(
                               $"select UpdatedAt, Value from {SettingsTable} where UserId = @UserId and Profile = @Profile and Ns = @Ns and Key = @Key"))
                    {
                        select.Bind("@UserId", userId);
                        select.Bind("@Profile", profile);
                        select.Bind("@Ns", write.Ns);
                        select.Bind("@Key", write.Key);
                        foreach (var row in select.Select(row => (UpdatedAt: row.GetInt64(0), Value: (byte[]?)row.GetValue(1))))
                        {
                            existingUpdatedAt = row.UpdatedAt;
                            existingValue = row.Value;
                        }
                    }

                    if (existingValue is not null && write.UpdatedAt <= existingUpdatedAt)
                    {
                        rejected.Add(new RejectedSetting(write.Ns, write.Key, "stale-write"));
                        continue;
                    }

                    if (existingValue is null)
                    {
                        var (keyCount, totalBytes) = GetUserSettingsFootprint(connection, userId, quotas.MaxUserBytes);
                        if (keyCount >= quotas.MaxKeysPerUser)
                        {
                            rejected.Add(new RejectedSetting(write.Ns, write.Key, "key-limit-reached"));
                            continue;
                        }

                        if (totalBytes + write.Value.Length > quotas.MaxUserBytes)
                        {
                            rejected.Add(new RejectedSetting(write.Ns, write.Key, "quota-exceeded"));
                            continue;
                        }
                    }
                    else
                    {
                        var growth = write.Value.Length - existingValue!.Length;
                        if (growth > 0 && totalBytesFor(connection, userId) + growth > quotas.MaxUserBytes)
                        {
                            rejected.Add(new RejectedSetting(write.Ns, write.Key, "quota-exceeded"));
                            continue;
                        }
                    }

                    using (var upsert = connection.Prepare(
                               $@"insert into {SettingsTable} (UserId, Profile, Ns, Key, SchemaVersion, UpdatedAt, DeviceId, Value)
                                  values (@UserId, @Profile, @Ns, @Key, @SchemaVersion, @UpdatedAt, @DeviceId, @Value)
                                  on conflict (UserId, Profile, Ns, Key) do update set
                                      SchemaVersion = @SchemaVersion,
                                      UpdatedAt = @UpdatedAt,
                                      DeviceId = @DeviceId,
                                      Value = @Value"))
                    {
                        upsert.Bind("@UserId", userId);
                        upsert.Bind("@Profile", profile);
                        upsert.Bind("@Ns", write.Ns);
                        upsert.Bind("@Key", write.Key);
                        upsert.Bind("@SchemaVersion", write.SchemaVersion);
                        upsert.Bind("@UpdatedAt", write.UpdatedAt);
                        upsert.Bind("@DeviceId", write.DeviceId);
                        upsert.Bind("@Value", write.Value);
                        upsert.ExecuteNonQuery();
                    }

                    long seq;
                    using (var logInsert = connection.Prepare(
                               $@"insert into {ChangeLogTable} (UserId, Profile, Ns, Key, UpdatedAt)
                                  values (@UserId, @Profile, @Ns, @Key, @UpdatedAt);
                                  select last_insert_rowid();"))
                    {
                        logInsert.Bind("@UserId", userId);
                        logInsert.Bind("@Profile", profile);
                        logInsert.Bind("@Ns", write.Ns);
                        logInsert.Bind("@Key", write.Key);
                        logInsert.Bind("@UpdatedAt", write.UpdatedAt);
                        seq = (long)(logInsert.ExecuteScalar() ?? 0L);
                    }

                    applied.Add(new AppliedSetting(write.Ns, write.Key, write.UpdatedAt, seq));
                }

                transaction.Commit();
            }
        }

        return new UpsertResult(applied, rejected);
    }

    public void DeleteNamespace(string userId, string profile, string ns)
    {
        using (_lock.Write())
        using (var connection = CreateConnection())
        using (var transaction = connection.BeginTransaction())
        {
            using (var statement = connection.Prepare($"delete from {SettingsTable} where UserId = @UserId and Profile = @Profile and Ns = @Ns"))
            {
                statement.Bind("@UserId", userId);
                statement.Bind("@Profile", profile);
                statement.Bind("@Ns", ns);
                statement.ExecuteNonQuery();
            }

            using (var statement = connection.Prepare($"delete from {ChangeLogTable} where UserId = @UserId and Profile = @Profile and Ns = @Ns"))
            {
                statement.Bind("@UserId", userId);
                statement.Bind("@Profile", profile);
                statement.Bind("@Ns", ns);
                statement.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    /// <summary>Current values for keys touched after <paramref name="sinceSeq"/> in the change log.</summary>
    public IReadOnlyList<SettingRow> GetChangedSettings(string userId, long sinceSeq)
    {
        using (_lock.Read())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare(
                   $@"select s.UserId, s.Profile, s.Ns, s.Key, s.SchemaVersion, s.UpdatedAt, s.DeviceId, s.Value
                      from {ChangeLogTable} c
                      join {SettingsTable} s
                        on s.UserId = c.UserId and s.Profile = c.Profile and s.Ns = c.Ns and s.Key = c.Key
                      where c.UserId = @UserId and c.Seq > @SinceSeq
                      group by s.UserId, s.Profile, s.Ns, s.Key
                      order by max(c.Seq)"))
        {
            statement.Bind("@UserId", userId);
            statement.Bind("@SinceSeq", sinceSeq);
            return statement.Select(ReadSettingRow).ToList();
        }
    }

    public long GetChangeLogHead(string userId)
    {
        using (_lock.Read())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare($"select coalesce(max(Seq), 0) from {ChangeLogTable} where UserId = @UserId"))
        {
            statement.Bind("@UserId", userId);
            return (long)(statement.ExecuteScalar() ?? 0L);
        }
    }

    public int PruneChangeLog(int retentionDays)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays).ToUnixTimeMilliseconds();
        using (_lock.Write())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare($"delete from {ChangeLogTable} where UpdatedAt < @Cutoff"))
        {
            statement.Bind("@Cutoff", cutoff);
            return statement.ExecuteNonQuery();
        }
    }

    // ------------------------------------------------------------------
    // Devices
    // ------------------------------------------------------------------

    public void UpsertDevice(DeviceRow device)
    {
        using (_lock.Write())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare(
                   $@"insert into {DevicesTable} (DeviceId, UserId, Name, Platform, AppVersion, LastSeen)
                      values (@DeviceId, @UserId, @Name, @Platform, @AppVersion, @LastSeen)
                      on conflict (DeviceId) do update set
                          UserId = @UserId, Name = @Name, Platform = @Platform,
                          AppVersion = @AppVersion, LastSeen = @LastSeen"))
        {
            statement.Bind("@DeviceId", device.DeviceId);
            statement.Bind("@UserId", device.UserId);
            statement.Bind("@Name", device.Name);
            statement.Bind("@Platform", device.Platform);
            statement.Bind("@AppVersion", device.AppVersion);
            statement.Bind("@LastSeen", device.LastSeen);
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
                   $"select DeviceId, UserId, Name, Platform, AppVersion, LastSeen from {DevicesTable} where UserId = @UserId order by LastSeen desc"))
        {
            statement.Bind("@UserId", userId);
            return statement.Select(row => new DeviceRow(
                row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3), row.GetString(4), row.GetInt64(5))).ToList();
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

    public SeerrSessionRow? GetSeerrSession(string userId)
    {
        using (_lock.Read())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare(
                   $"select UserId, CookiesJson, CreatedAt, LastValidatedAt from {SeerrSessionsTable} where UserId = @UserId"))
        {
            statement.Bind("@UserId", userId);
            foreach (var row in statement.Select(row => new SeerrSessionRow(
                         row.GetString(0), row.GetString(1), row.GetInt64(2), row.GetInt64(3))))
            {
                return row;
            }

            return null;
        }
    }

    public void UpsertSeerrSession(SeerrSessionRow session)
    {
        using (_lock.Write())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare(
                   $@"insert into {SeerrSessionsTable} (UserId, CookiesJson, CreatedAt, LastValidatedAt)
                      values (@UserId, @CookiesJson, @CreatedAt, @LastValidatedAt)
                      on conflict (UserId) do update set
                          CookiesJson = @CookiesJson, CreatedAt = @CreatedAt, LastValidatedAt = @LastValidatedAt"))
        {
            statement.Bind("@UserId", session.UserId);
            statement.Bind("@CookiesJson", session.CookiesJson);
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
    // Admin defaults
    // ------------------------------------------------------------------

    public AdminDefaultsRow? GetAdminDefaults(string scope)
    {
        using (_lock.Read())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare($"select Scope, Payload, UpdatedAt from {AdminDefaultsTable} where Scope = @Scope"))
        {
            statement.Bind("@Scope", scope);
            foreach (var row in statement.Select(row => new AdminDefaultsRow(
                         row.GetString(0), (byte[])row.GetValue(1), row.GetInt64(2))))
            {
                return row;
            }

            return null;
        }
    }

    public void SetAdminDefaults(string scope, byte[] payload, long updatedAt)
    {
        using (_lock.Write())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare(
                   $@"insert into {AdminDefaultsTable} (Scope, Payload, UpdatedAt) values (@Scope, @Payload, @UpdatedAt)
                      on conflict (Scope) do update set Payload = @Payload, UpdatedAt = @UpdatedAt"))
        {
            statement.Bind("@Scope", scope);
            statement.Bind("@Payload", payload);
            statement.Bind("@UpdatedAt", updatedAt);
            statement.ExecuteNonQuery();
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lock.Dispose();
    }

    // ------------------------------------------------------------------
    // Plumbing
    // ------------------------------------------------------------------

    public sealed record Quotas(int MaxKeyBytes, int MaxUserBytes, int MaxKeysPerUser);

    private (int KeyCount, long TotalBytes) GetUserSettingsFootprint(SqliteConnection connection, string userId, int maxBytes)
    {
        using var statement = connection.Prepare(
            $"select count(*), coalesce(sum(length(Value)), 0) from {SettingsTable} where UserId = @UserId");
        statement.Bind("@UserId", userId);
        foreach (var row in statement.Select(row => (Count: row.GetInt64(0), Bytes: row.GetInt64(1))))
        {
            return ((int)Math.Min(row.Count, maxBytes), row.Bytes);
        }

        return (0, 0);
    }

    private long totalBytesFor(SqliteConnection connection, string userId)
    {
        using var statement = connection.Prepare(
            $"select coalesce(sum(length(Value)), 0) from {SettingsTable} where UserId = @UserId");
        statement.Bind("@UserId", userId);
        return (long)(statement.ExecuteScalar() ?? 0L);
    }

    private static SettingRow ReadSettingRow(SqliteRow row) => new(
        row.GetString(0),
        row.GetString(1),
        row.GetString(2),
        row.GetString(3),
        (int)row.GetInt64(4),
        row.GetInt64(5),
        row.GetString(6),
        (byte[])row.GetValue(7));

    private static MessageRow ReadMessageRow(SqliteRow row) => new(
        row.GetString(0),
        row.GetString(1),
        row.GetString(2),
        row.GetString(3),
        row.GetString(4),
        row.GetString(5),
        row.GetString(6),
        row.IsDBNull(7) ? null : row.GetInt64(7),
        row.IsDBNull(8) ? null : row.GetInt64(8),
        (int)row.GetInt64(9),
        row.GetInt64(10));

    private static BookmarkRow ReadBookmarkRow(SqliteRow row) => new(
        row.GetString(0),
        row.GetString(1),
        row.GetString(2),
        row.GetDouble(3),
        row.IsDBNull(4) ? null : (int)row.GetInt64(4),
        row.GetString(5),
        row.GetString(6),
        row.GetInt64(7),
        row.GetInt64(8));

    private SqliteConnection CreateConnection()
    {
        var connection = new SqliteConnection($"Filename={_dbFilePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "pragma journal_mode=WAL; pragma synchronous=NORMAL; pragma foreign_keys=ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void BindNullable(SqliteCommand statement, string name, long? value)
    {
        var parameter = statement.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = (object?)value ?? System.DBNull.Value;
        statement.Parameters.Add(parameter);
    }
}

internal static class SqliteConnectionExtensions
{
    public static void RunQueries(this SqliteConnection connection, IReadOnlyList<string> queries)
    {
        foreach (var query in queries)
        {
            using var command = connection.CreateCommand();
            command.CommandText = query;
            command.ExecuteNonQuery();
        }
    }

    public static SqliteCommand Prepare(this SqliteConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    public static void Bind(this SqliteCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    public static IEnumerable<T> Select<T>(this SqliteCommand command, Func<SqliteRow, T> mapper)
    {
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            yield return mapper(new SqliteRow(reader));
        }
    }
}

/// <summary>Thin wrapper so row accessors read uniformly; not kept alive after enumeration.</summary>
internal sealed class SqliteRow
{
    private readonly SqliteDataReader _reader;

    public SqliteRow(SqliteDataReader reader)
    {
        _reader = reader;
    }

    public string GetString(int ordinal) => _reader.IsDBNull(ordinal) ? string.Empty : _reader.GetString(ordinal);

    public long GetInt64(int ordinal)
    {
        var value = _reader.GetValue(ordinal);
        return value switch
        {
            long l => l,
            int i => i,
            _ => Convert.ToInt64(value)
        };
    }

    public int GetInt32(int ordinal) => Convert.ToInt32(_reader.GetValue(ordinal));

    public double GetDouble(int ordinal) => Convert.ToDouble(_reader.GetValue(ordinal));

    public bool IsDBNull(int ordinal) => _reader.IsDBNull(ordinal);

    public object? GetValue(int ordinal) => _reader.IsDBNull(ordinal) ? null : _reader.GetValue(ordinal);
}

internal static class LockExtensions
{
    public static IDisposable Read(this ReaderWriterLockSlim @lock)
    {
        @lock.EnterReadLock();
        return new LockReleaser(@lock, write: false);
    }

    public static IDisposable Write(this ReaderWriterLockSlim @lock)
    {
        @lock.EnterWriteLock();
        return new LockReleaser(@lock, write: true);
    }

    private readonly struct LockReleaser : IDisposable
    {
        private readonly ReaderWriterLockSlim _lock;
        private readonly bool _write;

        public LockReleaser(ReaderWriterLockSlim @lock, bool write)
        {
            _lock = @lock;
            _write = write;
        }

        public void Dispose()
        {
            if (_write)
            {
                _lock.ExitWriteLock();
            }
            else
            {
                _lock.ExitReadLock();
            }
        }
    }
}
