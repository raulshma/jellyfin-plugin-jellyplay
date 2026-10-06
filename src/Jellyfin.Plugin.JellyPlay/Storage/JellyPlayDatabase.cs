using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Jellyfin.Plugin.JellyPlay.Storage.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyPlay.Storage;

/// <summary>
/// The plugin's SQLite persistence. One file, WAL mode, guarded by a
/// ReaderWriterLockSlim. All value payloads are opaque blobs; this class never
/// interprets settings content. Per-user quotas are enforced here so every write
/// path gets them for free.
///
/// Schema evolution: the version integer lives in <c>PRAGMA user_version</c>.
/// The DDL below always builds the version-1 baseline (idempotent); a stepwise
/// runner then applies each migration in order inside the init path, one
/// transaction per step. A failed integrity check quarantines the file and
/// rebuilds fresh — settings sync re-populates from clients, so data loss is
/// bounded and recovery is automatic.
/// </summary>
public sealed class JellyPlayDatabase : IDisposable
{
    public const string BaseProfile = "";

    /// <summary>The schema version produced by this build's DDL + migrations.</summary>
    public const int CurrentSchemaVersion = 6;

    private const string SettingsTable = "settings";
    private const string ChangeLogTable = "change_log";
    private const string DevicesTable = "devices";
    private const string PlaybackSessionsTable = "playback_sessions";
    private const string PlaybackRollupsTable = "playback_rollups";

    /// <summary>Canonical device column list (schema v4), shared by every device query.</summary>
    private const string DeviceColumns =
        $"select DeviceId, UserId, Name, Platform, AppVersion, LastSeen, PushKind, PushEndpoint, CreatedAt from {DevicesTable}";
    private const string MessagesTable = "messages";
    private const string MessageReadsTable = "message_reads";
    private const string BookmarksTable = "bookmarks";
    private const string SeerrSessionsTable = "seerr_sessions";
    private const string AdminDefaultsTable = "admin_defaults";
    private const string SyncHistoryTable = "sync_history";

    /// <summary>
    /// Stepwise migrations, each moving <c>user_version</c> to its version.
    /// Version 1 is the create-if-not-exists baseline above; never edit it —
    /// add a new step here instead.
    /// </summary>
    private static readonly IReadOnlyList<(int Version, string Name, Action<SqliteConnection> Apply)> Migrations =
    [
        (2, "idx_change_log_updated",
            connection => connection.RunQueries(
                [$"create index if not exists idx_{ChangeLogTable}_updated on {ChangeLogTable}(UpdatedAt)"])),
        (3, "sync_history",
            connection => connection.RunQueries(
            [
                $@"create table if not exists {SyncHistoryTable} (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    UserId TEXT NOT NULL,
                    DeviceId TEXT NOT NULL,
                    Ts INTEGER NOT NULL,
                    Op TEXT NOT NULL,
                    KeysApplied INTEGER NOT NULL,
                    KeysRejected INTEGER NOT NULL,
                    Bytes INTEGER NOT NULL,
                    RejectsJson TEXT NULL)",
                $"create index if not exists idx_{SyncHistoryTable}_user on {SyncHistoryTable}(UserId, Id)"
            ])),
        (4, "device_push_registration",
            connection => connection.RunQueries(
            [
                $"alter table {DevicesTable} add column PushKind TEXT NULL",
                $"alter table {DevicesTable} add column PushEndpoint TEXT NULL",
                $"alter table {DevicesTable} add column CreatedAt INTEGER NULL"
            ])),
        (5, "playback_analytics",
            connection => connection.RunQueries(
            [
                $@"create table if not exists {PlaybackSessionsTable} (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    UserId TEXT NOT NULL,
                    ItemId TEXT NOT NULL,
                    ItemName TEXT NOT NULL,
                    ItemType TEXT NOT NULL,
                    SeriesName TEXT NULL,
                    PlayMethod TEXT NOT NULL,
                    VideoCodec TEXT NULL,
                    AudioCodec TEXT NULL,
                    Bitrate INTEGER NULL,
                    TranscodeReasonsJson TEXT NULL,
                    PositionTicks INTEGER NOT NULL,
                    DurationTicks INTEGER NULL,
                    StartedAt INTEGER NOT NULL,
                    EndedAt INTEGER NOT NULL,
                    ClientName TEXT NULL,
                    DeviceName TEXT NULL)",
                $"create index if not exists idx_{PlaybackSessionsTable}_user_started on {PlaybackSessionsTable}(UserId, StartedAt)",
                $"create index if not exists idx_{PlaybackSessionsTable}_started on {PlaybackSessionsTable}(StartedAt)",
                // Dedup: at most one row per (user, item, start-minute bucket).
                $"create unique index if not exists ux_{PlaybackSessionsTable}_dedupe on {PlaybackSessionsTable}(UserId, ItemId, StartedAt)",
                $@"create table if not exists {PlaybackRollupsTable} (
                    Day TEXT NOT NULL,
                    UserId TEXT NOT NULL,
                    ItemsPlayed INTEGER NOT NULL,
                    PlaySeconds INTEGER NOT NULL,
                    TranscodeSeconds INTEGER NOT NULL,
                    DirectCount INTEGER NOT NULL,
                    TranscodeCount INTEGER NOT NULL,
                    primary key (Day, UserId))",
                $"create index if not exists idx_{PlaybackRollupsTable}_day on {PlaybackRollupsTable}(Day)"
            ])),
        (6, "sync_history_diff_range",
            connection => connection.RunQueries(
            [
                // Per-key diff support: each recorded operation brackets the
                // change-log range it covered (null on pre-v6 rows).
                $"alter table {SyncHistoryTable} add column FromSeq INTEGER NULL",
                $"alter table {SyncHistoryTable} add column ToSeq INTEGER NULL"
            ]))
    ];

    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly string _dbFilePath;
    private readonly ILogger<JellyPlayDatabase>? _logger;
    private bool _disposed;

    public JellyPlayDatabase(string dataPath, ILogger<JellyPlayDatabase>? logger = null)
    {
        _logger = logger;
        var dir = Path.Combine(dataPath, "plugins", "JellyPlay");
        Directory.CreateDirectory(dir);
        _dbFilePath = Path.Combine(dir, "jellyplay_plugin.db");
        Initialize();
    }

    public string DbFilePath => _dbFilePath;

    /// <summary>Current <c>PRAGMA user_version</c> (0 on a fresh, never-migrated file).</summary>
    public int UserVersion
    {
        get
        {
            using (_lock.Read())
            using (var connection = CreateConnection())
            {
                return ReadUserVersion(connection);
            }
        }
    }

    private void Initialize()
    {
        using (_lock.Write())
        using (var connection = CreateConnection())
        {
            InitializeCore(connection);
        }
    }

    /// <summary>Version-1 baseline DDL (idempotent) followed by the migration runner.</summary>
    private void InitializeCore(SqliteConnection connection)
    {
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

        ApplyMigrations(connection);
    }    /// <summary>Applies pending migrations in order; each step commits with its version stamp.</summary>
    private void ApplyMigrations(SqliteConnection connection)
    {
        var current = ReadUserVersion(connection);
        if (current == 0)
        {
            // Fresh file (or a pre-versioning baseline): the DDL above is exactly v1.
            current = 1;
        }

        foreach (var (version, name, apply) in Migrations)
        {
            if (version <= current)
            {
                continue;
            }

            using var transaction = connection.BeginTransaction();
            apply(connection);
            SetUserVersion(connection, version);
            transaction.Commit();
            _logger?.LogInformation("JellyPlay database migrated to schema version {Version} ({Name})", version, name);
        }
    }

    private static int ReadUserVersion(SqliteConnection connection)
    {
        using var statement = connection.Prepare("pragma user_version");
        return (int)(long)(statement.ExecuteScalar() ?? 0L);
    }

    private static void SetUserVersion(SqliteConnection connection, int version)
    {
        using var statement = connection.Prepare($"pragma user_version = {version}");
        statement.ExecuteNonQuery();
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

    /// <summary>
    /// Applies a batch with per-key last-write-wins: an incoming write applies
    /// only when its timestamp is strictly newer than the stored one; equal
    /// timestamps reject with stale-write (deterministic, no oscillation).
    /// Returns applied and rejected lists. Applied writes append one change-log
    /// row each.
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
                        var (keyCount, totalBytes) = GetUserSettingsFootprint(connection, userId);
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

    /// <summary>Deletes one namespace; returns the number of settings rows removed.</summary>
    public int DeleteNamespace(string userId, string profile, string ns)
    {
        int deleted;
        using (_lock.Write())
        using (var connection = CreateConnection())
        using (var transaction = connection.BeginTransaction())
        {
            using (var count = connection.Prepare(
                       $"select count(*) from {SettingsTable} where UserId = @UserId and Profile = @Profile and Ns = @Ns"))
            {
                count.Bind("@UserId", userId);
                count.Bind("@Profile", profile);
                count.Bind("@Ns", ns);
                deleted = (int)(long)(count.ExecuteScalar() ?? 0L);
            }

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

        return deleted;
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

    /// <summary>Change-log rows with Seq in (fromSeq, toSeq] for one user, newest-first — the per-key diff of a recorded sync operation.</summary>
    public IReadOnlyList<ChangeLogEntry> GetChangeLogRange(string userId, long fromSeq, long toSeq, int limit)
    {
        using (_lock.Read())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare(
                   $@"select Seq, UserId, Profile, Ns, Key, UpdatedAt from {ChangeLogTable}
                      where UserId = @UserId and Seq > @FromSeq and Seq <= @ToSeq
                      order by Seq desc
                      limit @Limit"))
        {
            statement.Bind("@UserId", userId);
            statement.Bind("@FromSeq", fromSeq);
            statement.Bind("@ToSeq", toSeq);
            statement.Bind("@Limit", limit);
            return statement.Select(row => new ChangeLogEntry(
                row.GetInt64(0),
                row.GetString(1),
                row.GetString(2),
                row.GetString(3),
                row.GetString(4),
                row.GetInt64(5))).ToList();
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
    // Sync history (observability)
    // ------------------------------------------------------------------

    /// <summary>
    /// Appends one recorded sync operation (op: 'push'|'pull'|'reset'); the
    /// generated Id doubles as the "seq" the history endpoint reports.
    /// FromSeq/ToSeq (schema v6) bracket the change-log range the operation
    /// covered — null on rows written before the range was captured.
    /// </summary>
    public long InsertSyncHistory(
        string userId,
        string deviceId,
        string op,
        int keysApplied,
        int keysRejected,
        long bytes,
        string? rejectsJson,
        long ts,
        long? fromSeq = null,
        long? toSeq = null)
    {
        using (_lock.Write())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare(
                   $@"insert into {SyncHistoryTable} (UserId, DeviceId, Ts, Op, KeysApplied, KeysRejected, Bytes, RejectsJson, FromSeq, ToSeq)
                      values (@UserId, @DeviceId, @Ts, @Op, @KeysApplied, @KeysRejected, @Bytes, @RejectsJson, @FromSeq, @ToSeq);
                      select last_insert_rowid();"))
        {
            statement.Bind("@UserId", userId);
            statement.Bind("@DeviceId", deviceId);
            statement.Bind("@Ts", ts);
            statement.Bind("@Op", op);
            statement.Bind("@KeysApplied", keysApplied);
            statement.Bind("@KeysRejected", keysRejected);
            statement.Bind("@Bytes", bytes);
            statement.Bind("@RejectsJson", (object?)rejectsJson ?? System.DBNull.Value);
            BindNullable(statement, "@FromSeq", fromSeq);
            BindNullable(statement, "@ToSeq", toSeq);
            return (long)(statement.ExecuteScalar() ?? 0L);
        }
    }

    /// <summary>Recorded operations for one user, newest-first, optionally filtered to entries newer than <paramref name="sinceTs"/> (unix ms).</summary>
    public IReadOnlyList<SyncHistoryRow> GetSyncHistory(string userId, long sinceTs, int limit)
    {
        using (_lock.Read())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare(
                   $@"select Id, UserId, DeviceId, Ts, Op, KeysApplied, KeysRejected, Bytes, RejectsJson, FromSeq, ToSeq
                      from {SyncHistoryTable}
                      where UserId = @UserId and Ts > @SinceTs
                      order by Id desc
                      limit @Limit"))
        {
            statement.Bind("@UserId", userId);
            statement.Bind("@SinceTs", sinceTs);
            statement.Bind("@Limit", limit);
            return statement.Select(ReadSyncHistoryRow).ToList();
        }
    }

    /// <summary>One recorded operation by id — only when owned by the user (the per-key diff endpoint's ownership check).</summary>
    public SyncHistoryRow? GetSyncHistoryRow(string userId, long id)
    {
        using (_lock.Read())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare(
                   $@"select Id, UserId, DeviceId, Ts, Op, KeysApplied, KeysRejected, Bytes, RejectsJson, FromSeq, ToSeq
                      from {SyncHistoryTable}
                      where Id = @Id and UserId = @UserId"))
        {
            statement.Bind("@Id", id);
            statement.Bind("@UserId", userId);
            foreach (var row in statement.Select(ReadSyncHistoryRow))
            {
                return row;
            }

            return null;
        }
    }

    /// <summary>Latest recorded operation per device, folded from the history.</summary>
    public IReadOnlyList<DeviceSyncSummary> GetLatestSyncPerDevice(string userId)
    {
        using (_lock.Read())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare(
                   $@"select DeviceId, Ts, Op from {SyncHistoryTable} h
                      where UserId = @UserId
                        and Id = (select max(Id) from {SyncHistoryTable}
                                  where UserId = h.UserId and DeviceId = h.DeviceId)
                      order by Ts desc"))
        {
            statement.Bind("@UserId", userId);
            return statement.Select(row => new DeviceSyncSummary(
                row.GetString(0), row.GetInt64(1), row.GetString(2))).ToList();
        }
    }

    /// <summary>Per-user rollup over the whole history: most recent operation time and distinct device count.</summary>
    public IReadOnlyList<UserSyncSummary> GetSyncSummariesByUser()
    {
        using (_lock.Read())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare(
                   $"select UserId, max(Ts), count(distinct DeviceId) from {SyncHistoryTable} group by UserId order by UserId"))
        {
            return statement.Select(row => new UserSyncSummary(
                row.GetString(0), row.GetInt64(1), (int)row.GetInt64(2))).ToList();
        }
    }

    public int PruneSyncHistory(int retentionDays)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays).ToUnixTimeMilliseconds();
        using (_lock.Write())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare($"delete from {SyncHistoryTable} where Ts < @Cutoff"))
        {
            statement.Bind("@Cutoff", cutoff);
            return statement.ExecuteNonQuery();
        }
    }

    // ------------------------------------------------------------------
    // Playback analytics (playback_sessions + playback_rollups)
    // ------------------------------------------------------------------

    /// <summary>Canonical playback-session column list (schema v5), shared by every session query.</summary>
    private const string PlaybackSessionColumns =
        "Id, UserId, ItemId, ItemName, ItemType, SeriesName, PlayMethod, VideoCodec, AudioCodec, Bitrate, " +
        "TranscodeReasonsJson, PositionTicks, DurationTicks, StartedAt, EndedAt, ClientName, DeviceName";

    /// <summary>
    /// Inserts one finished playback session. StartedAt must already be
    /// minute-bucketed — the unique (UserId, ItemId, StartedAt) index makes
    /// the dedup rule ("one row per user/item/start-minute") total, so the
    /// insert of a duplicate is a no-op. Returns the new row id, or 0 when
    /// the row was deduped away.
    /// </summary>
    public long InsertPlaybackSession(PlaybackSessionRow session)
    {
        using (_lock.Write())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare(
                   $@"insert into {PlaybackSessionsTable}
                      (UserId, ItemId, ItemName, ItemType, SeriesName, PlayMethod, VideoCodec, AudioCodec, Bitrate, TranscodeReasonsJson, PositionTicks, DurationTicks, StartedAt, EndedAt, ClientName, DeviceName)
                      values (@UserId, @ItemId, @ItemName, @ItemType, @SeriesName, @PlayMethod, @VideoCodec, @AudioCodec, @Bitrate, @TranscodeReasonsJson, @PositionTicks, @DurationTicks, @StartedAt, @EndedAt, @ClientName, @DeviceName)
                      on conflict(UserId, ItemId, StartedAt) do nothing;
                      select case when changes() = 0 then 0 else last_insert_rowid() end;"))
        {
            statement.Bind("@UserId", session.UserId);
            statement.Bind("@ItemId", session.ItemId);
            statement.Bind("@ItemName", session.ItemName);
            statement.Bind("@ItemType", session.ItemType);
            BindNullableText(statement, "@SeriesName", session.SeriesName);
            statement.Bind("@PlayMethod", session.PlayMethod);
            BindNullableText(statement, "@VideoCodec", session.VideoCodec);
            BindNullableText(statement, "@AudioCodec", session.AudioCodec);
            BindNullable(statement, "@Bitrate", session.Bitrate);
            BindNullableText(statement, "@TranscodeReasonsJson", session.TranscodeReasonsJson);
            statement.Bind("@PositionTicks", session.PositionTicks);
            BindNullable(statement, "@DurationTicks", session.DurationTicks);
            statement.Bind("@StartedAt", session.StartedAt);
            statement.Bind("@EndedAt", session.EndedAt);
            BindNullableText(statement, "@ClientName", session.ClientName);
            BindNullableText(statement, "@DeviceName", session.DeviceName);
            return (long)(statement.ExecuteScalar() ?? 0L);
        }
    }

    /// <summary>
    /// Finished sessions newest-first, optionally filtered to one user and/or
    /// ends after <paramref name="sinceMs"/> (unix ms).
    /// </summary>
    public IReadOnlyList<PlaybackSessionRow> GetPlaybackSessions(string? userId, long sinceMs, int limit)
    {
        var filters = new List<string>(2);
        if (userId is not null)
        {
            filters.Add("UserId = @UserId");
        }

        if (sinceMs > 0)
        {
            filters.Add("EndedAt > @SinceMs");
        }

        var where = filters.Count == 0 ? string.Empty : $" where {string.Join(" and ", filters)}";
        using (_lock.Read())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare(
                   $"select {PlaybackSessionColumns} from {PlaybackSessionsTable}{where} order by Id desc limit @Limit"))
        {
            if (userId is not null)
            {
                statement.Bind("@UserId", userId);
            }

            if (sinceMs > 0)
            {
                statement.Bind("@SinceMs", sinceMs);
            }

            statement.Bind("@Limit", limit);
            return statement.Select(ReadPlaybackSessionRow).ToList();
        }
    }

    /// <summary>
    /// Idempotent rollup rebuild: every UTC day that still has raw session rows
    /// ending at/after <paramref name="cutoffMs"/> has its rollup rows deleted
    /// and re-derived from ALL raw rows of that day (the cutoff selects the
    /// days; the aggregation always covers the whole day, so rows just outside
    /// the cutoff are never dropped from their own day's totals). Returns the
    /// number of days recomputed. Runs in one transaction.
    /// </summary>
    public int RecomputePlaybackRollups(long cutoffMs)
    {
        var dayExpr = $"date({PlaybackSessionsTable}.EndedAt / 1000, 'unixepoch')";
        using (_lock.Write())
        using (var connection = CreateConnection())
        using (var transaction = connection.BeginTransaction())
        {
            var days = new List<string>();
            using (var select = connection.Prepare(
                       $"select distinct {dayExpr} from {PlaybackSessionsTable} where {PlaybackSessionsTable}.EndedAt >= @CutoffMs order by 1"))
            {
                select.Bind("@CutoffMs", cutoffMs);
                days.AddRange(select.Select(row => row.GetString(0)));
            }

            if (days.Count > 0)
            {
                var placeholders = new StringBuilder(days.Count * 6);
                for (var index = 0; index < days.Count; index++)
                {
                    if (index > 0)
                    {
                        placeholders.Append(", ");
                    }

                    placeholders.Append("@Day").Append(index);
                }

                var dayList = placeholders.ToString();
                using (var delete = connection.Prepare($"delete from {PlaybackRollupsTable} where Day in ({dayList})"))
                {
                    for (var index = 0; index < days.Count; index++)
                    {
                        delete.Bind($"@Day{index}", days[index]);
                    }

                    delete.ExecuteNonQuery();
                }

                using (var insert = connection.Prepare(
                           $@"insert into {PlaybackRollupsTable} (Day, UserId, ItemsPlayed, PlaySeconds, TranscodeSeconds, DirectCount, TranscodeCount)
                              select {dayExpr},
                                     UserId,
                                     count(*),
                                     sum(max(0, (EndedAt - StartedAt) / 1000)),
                                     sum(case when PlayMethod = 'Transcode' then max(0, (EndedAt - StartedAt) / 1000) else 0 end),
                                     sum(case when PlayMethod != 'Transcode' then 1 else 0 end),
                                     sum(case when PlayMethod = 'Transcode' then 1 else 0 end)
                              from {PlaybackSessionsTable}
                              where {dayExpr} in ({dayList})
                              group by {dayExpr}, UserId"))
                {
                    for (var index = 0; index < days.Count; index++)
                    {
                        insert.Bind($"@Day{index}", days[index]);
                    }

                    insert.ExecuteNonQuery();
                }
            }

            transaction.Commit();
            return days.Count;
        }
    }

    /// <summary>Raw session rows (and only those) ending before <paramref name="cutoffMs"/> (unix ms) are deleted; rollups are never touched.</summary>
    public int PrunePlaybackSessions(long cutoffMs)
    {
        using (_lock.Write())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare($"delete from {PlaybackSessionsTable} where EndedAt < @Cutoff"))
        {
            statement.Bind("@Cutoff", cutoffMs);
            return statement.ExecuteNonQuery();
        }
    }

    /// <summary>The disable wipe: removes every analytics row (raw sessions + rollups).</summary>
    public (int Sessions, int Rollups) PurgeAnalyticsData()
    {
        using (_lock.Write())
        using (var connection = CreateConnection())
        {
            int sessions;
            int rollups;
            using (var statement = connection.Prepare($"delete from {PlaybackSessionsTable}"))
            {
                sessions = statement.ExecuteNonQuery();
            }

            using (var statement = connection.Prepare($"delete from {PlaybackRollupsTable}"))
            {
                rollups = statement.ExecuteNonQuery();
            }

            return (sessions, rollups);
        }
    }

    /// <summary>Per-user daily rollups within the UTC day range (inclusive).</summary>
    public IReadOnlyList<PlaybackRollupRow> GetPlaybackRollups(string fromDayUtc, string toDayUtc)
    {
        using (_lock.Read())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare(
                   $@"select Day, UserId, ItemsPlayed, PlaySeconds, TranscodeSeconds, DirectCount, TranscodeCount
                      from {PlaybackRollupsTable}
                      where Day >= @FromDay and Day <= @ToDay
                      order by Day, UserId"))
        {
            statement.Bind("@FromDay", fromDayUtc);
            statement.Bind("@ToDay", toDayUtc);
            return statement.Select(row => new PlaybackRollupRow(
                row.GetString(0),
                row.GetString(1),
                row.GetInt64(2),
                row.GetInt64(3),
                row.GetInt64(4),
                row.GetInt64(5),
                row.GetInt64(6))).ToList();
        }
    }

    /// <summary>
    /// Most-played items folded from raw sessions within the end-time window;
    /// deterministic tiebreak by item id. Null <paramref name="userId"/> = all
    /// users (admin surface); a user id scopes the fold to that user's rows.
    /// </summary>
    public IReadOnlyList<PlaybackTopItemRow> GetTopPlaybackItems(long fromMs, long toMs, int limit, string? userId = null)
    {
        var filters = new List<string>(2) { "EndedAt >= @FromMs", "EndedAt < @ToMs" };
        if (userId is not null)
        {
            filters.Add("UserId = @UserId");
        }

        using (_lock.Read())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare(
                   $@"select ItemId, ItemName, ItemType, count(*), sum(max(0, (EndedAt - StartedAt) / 1000))
                      from {PlaybackSessionsTable}
                      where {string.Join(" and ", filters)}
                      group by ItemId, ItemName, ItemType
                      order by count(*) desc, ItemId
                      limit @Limit"))
        {
            statement.Bind("@FromMs", fromMs);
            statement.Bind("@ToMs", toMs);
            if (userId is not null)
            {
                statement.Bind("@UserId", userId);
            }

            statement.Bind("@Limit", limit);
            return statement.Select(row => new PlaybackTopItemRow(
                row.GetString(0),
                row.GetString(1),
                row.GetString(2),
                row.GetInt64(3),
                row.GetInt64(4))).ToList();
        }
    }

    /// <summary>
    /// Distinct items played within the end-time window (raw-derived; 0 once
    /// raw is pruned). Null <paramref name="userId"/> = all users (admin
    /// surface); a user id scopes the count to that user's rows.
    /// </summary>
    public long CountDistinctPlaybackItems(long fromMs, long toMs, string? userId = null)
    {
        var filters = new List<string>(2) { "EndedAt >= @FromMs", "EndedAt < @ToMs" };
        if (userId is not null)
        {
            filters.Add("UserId = @UserId");
        }

        using (_lock.Read())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare(
                   $"select count(distinct ItemId) from {PlaybackSessionsTable} where {string.Join(" and ", filters)}"))
        {
            statement.Bind("@FromMs", fromMs);
            statement.Bind("@ToMs", toMs);
            if (userId is not null)
            {
                statement.Bind("@UserId", userId);
            }

            return (long)(statement.ExecuteScalar() ?? 0L);
        }
    }

    private static PlaybackSessionRow ReadPlaybackSessionRow(SqliteRow row) => new(
        row.GetInt64(0),
        row.GetString(1),
        row.GetString(2),
        row.GetString(3),
        row.GetString(4),
        row.IsDBNull(5) ? null : row.GetString(5),
        row.GetString(6),
        row.IsDBNull(7) ? null : row.GetString(7),
        row.IsDBNull(8) ? null : row.GetString(8),
        row.IsDBNull(9) ? null : row.GetInt64(9),
        row.IsDBNull(10) ? null : row.GetString(10),
        row.GetInt64(11),
        row.IsDBNull(12) ? null : row.GetInt64(12),
        row.GetInt64(13),
        row.GetInt64(14),
        row.IsDBNull(15) ? null : row.GetString(15),
        row.IsDBNull(16) ? null : row.GetString(16));

    // ------------------------------------------------------------------
    // Footprints (settings-store size accounting)
    // ------------------------------------------------------------------

    /// <summary>Count of stored keys and their total byte size for one user (public wrapper for the status endpoint).</summary>
    public (int KeyCount, long TotalBytes) GetUserFootprint(string userId)
    {
        using (_lock.Read())
        using (var connection = CreateConnection())
        {
            return GetUserSettingsFootprint(connection, userId);
        }
    }

    /// <summary>Key/byte totals per namespace across the user's profiles, ordered by namespace.</summary>
    public IReadOnlyList<NamespaceFootprint> GetNamespaceFootprints(string userId)
    {
        using (_lock.Read())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare(
                   $"select Ns, count(*), coalesce(sum(length(Value)), 0) from {SettingsTable} where UserId = @UserId group by Ns order by Ns"))
        {
            statement.Bind("@UserId", userId);
            return statement.Select(row => new NamespaceFootprint(
                row.GetString(0), (int)row.GetInt64(1), row.GetInt64(2))).ToList();
        }
    }

    /// <summary>Key/byte totals for every user with settings rows, ordered by user id.</summary>
    public IReadOnlyList<UserFootprint> GetUserFootprints()
    {
        using (_lock.Read())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare(
                   $"select UserId, count(*), coalesce(sum(length(Value)), 0) from {SettingsTable} group by UserId order by UserId"))
        {
            return statement.Select(row => new UserFootprint(
                row.GetString(0), (int)row.GetInt64(1), row.GetInt64(2))).ToList();
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

    public sealed record IntegrityResult(bool IntegrityOk, string Details, long WalCheckpointedFrames, bool Repaired);

    /// <summary>
    /// pragma integrity_check + WAL checkpoint — the scheduled integrity task's
    /// probe. On failure the corrupt file is quarantined (renamed with a
    /// timestamp suffix, WAL alongside) and a fresh database is created in its
    /// place so the plugin keeps working; settings re-sync from clients.
    /// </summary>
    public IntegrityResult CheckIntegrity()
    {
        using (_lock.Write())
        {
            var details = string.Empty;
            long frames;
            using (var connection = CreateConnection())
            {
                try
                {
                    using (var check = connection.Prepare("pragma integrity_check"))
                    {
                        var rows = new List<string>();
                        foreach (var row in check.Select(row => row.GetString(0)))
                        {
                            rows.Add(row);
                            if (rows.Count >= 8)
                            {
                                break;
                            }
                        }

                        details = string.Join("; ", rows);
                    }
                }
                catch (SqliteException ex)
                {
                    // A badly damaged file can fail mid-check; that is a failure, not a crash.
                    details = ex.Message;
                }

                frames = 0;
                try
                {
                    using var checkpoint = connection.Prepare("pragma wal_checkpoint(TRUNCATE)");
                    using var reader = checkpoint.ExecuteReader();
                    if (reader.Read() && !reader.IsDBNull(1))
                    {
                        frames = Convert.ToInt64(reader.GetValue(1));
                    }
                }
                catch (SqliteException)
                {
                    // Checkpointing a corrupt database is best-effort.
                }
            }

            if (details.Equals("ok", StringComparison.OrdinalIgnoreCase))
            {
                return new IntegrityResult(true, details, frames, Repaired: false);
            }

            _logger?.LogError("JellyPlay database INTEGRITY FAILURE: {Details}. Quarantining the file and recreating a fresh database; clients will re-sync settings.", details);
            QuarantineAndRecreate();
            return new IntegrityResult(false, details, frames, Repaired: true);
        }
    }

    /// <summary>Renames the corrupt database (and WAL/SHM siblings) aside, then rebuilds the schema fresh.</summary>
    private void QuarantineAndRecreate()
    {
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var quarantineSuffix = $".corrupt-{stamp}";
        RenameAside(_dbFilePath, quarantineSuffix);
        RenameAside(_dbFilePath + "-wal", quarantineSuffix);
        RenameAside(_dbFilePath + "-shm", quarantineSuffix);

        try
        {
            using var connection = CreateConnection();
            InitializeCore(connection);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "JellyPlay database rebuild after quarantine failed");
            throw;
        }
    }

    private void RenameAside(string path, string suffix)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Move(path, path + suffix);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not quarantine {Path}", path);
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

    /// <summary>Count of stored keys and their total byte size for one user.</summary>
    private (int KeyCount, long TotalBytes) GetUserSettingsFootprint(SqliteConnection connection, string userId)
    {
        using var statement = connection.Prepare(
            $"select count(*), coalesce(sum(length(Value)), 0) from {SettingsTable} where UserId = @UserId");
        statement.Bind("@UserId", userId);
        foreach (var row in statement.Select(row => (Count: row.GetInt64(0), Bytes: row.GetInt64(1))))
        {
            return ((int)row.Count, row.Bytes);
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

    private static SyncHistoryRow ReadSyncHistoryRow(SqliteRow row) => new(
        row.GetInt64(0),
        row.GetString(1),
        row.GetString(2),
        row.GetInt64(3),
        row.GetString(4),
        (int)row.GetInt64(5),
        (int)row.GetInt64(6),
        row.GetInt64(7),
        row.IsDBNull(8) ? null : row.GetString(8),
        row.IsDBNull(9) ? null : row.GetInt64(9),
        row.IsDBNull(10) ? null : row.GetInt64(10));

    private static DeviceRow ReadDeviceRow(SqliteRow row) => new(
        row.GetString(0),
        row.GetString(1),
        row.GetString(2),
        row.GetString(3),
        row.GetString(4),
        row.GetInt64(5),
        row.IsDBNull(6) ? null : row.GetString(6),
        row.IsDBNull(7) ? null : row.GetString(7),
        row.IsDBNull(8) ? null : row.GetInt64(8));

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
        // Pooling off: pooled connections hold the file handle open after
        // Dispose, which would block integrity-quarantine file renames.
        var connection = new SqliteConnection($"Filename={_dbFilePath};Pooling=False");
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

    private static void BindNullableText(SqliteCommand statement, string name, string? value)
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
