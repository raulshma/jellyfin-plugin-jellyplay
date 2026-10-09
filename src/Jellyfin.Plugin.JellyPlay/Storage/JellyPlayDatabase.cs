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
public sealed partial class JellyPlayDatabase : IDisposable
{
    public const string BaseProfile = "";

    /// <summary>The schema version produced by this build's DDL + migrations.</summary>
    public const int CurrentSchemaVersion = 9;

    private const string SettingsTable = "settings";
    private const string ChangeLogTable = "change_log";
    private const string DevicesTable = "devices";
    private const string PlaybackSessionsTable = "playback_sessions";
    private const string PlaybackRollupsTable = "playback_rollups";

    /// <summary>Canonical device column list (schema v4 + v7), shared by every device query.</summary>
    private const string DeviceColumns =
        $"select DeviceId, UserId, Name, Platform, AppVersion, LastSeen, PushKind, PushEndpoint, CreatedAt, Model, CapsJson, Revoked from {DevicesTable}";
    private const string MessagesTable = "messages";
    private const string MessageReadsTable = "message_reads";
    private const string BookmarksTable = "bookmarks";
    private const string SeerrSessionsTable = "seerr_sessions";
    private const string AdminDefaultsTable = "admin_defaults";
    private const string SyncHistoryTable = "sync_history";
    private const string SnapshotsTable = "user_snapshots";

    /// <summary>Canonical sync-history read surface, shared by every history select (a schema change touches this once).</summary>
    private const string SyncHistoryColumns =
        "Id, UserId, DeviceId, Ts, Op, KeysApplied, KeysRejected, Bytes, RejectsJson, FromSeq, ToSeq";

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
            connection =>
            {
                // Column adds are guarded: SQLite has no ADD COLUMN IF NOT
                // EXISTS, and a hand-rolled (or partially rolled-back) older
                // file may already carry some of the v4 columns.
                foreach (var (column, ddl) in new[]
                {
                    ("PushKind", $"alter table {DevicesTable} add column PushKind TEXT NULL"),
                    ("PushEndpoint", $"alter table {DevicesTable} add column PushEndpoint TEXT NULL"),
                    ("CreatedAt", $"alter table {DevicesTable} add column CreatedAt INTEGER NULL")
                })
                {
                    if (!HasColumn(connection, DevicesTable, column))
                    {
                        connection.RunQueries([ddl]);
                    }
                }
            }),
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
            connection =>
            {
                // Per-key diff support: each recorded operation brackets the
                // change-log range it covered (null on pre-v6 rows). Guarded
                // like the v4/v7 column adds: SQLite has no ADD COLUMN IF NOT
                // EXISTS, and a hand-rolled file may already carry them.
                foreach (var (column, ddl) in new[]
                {
                    ("FromSeq", $"alter table {SyncHistoryTable} add column FromSeq INTEGER NULL"),
                    ("ToSeq", $"alter table {SyncHistoryTable} add column ToSeq INTEGER NULL")
                })
                {
                    if (!HasColumn(connection, SyncHistoryTable, column))
                    {
                        connection.RunQueries([ddl]);
                    }
                }
            }),
        (7, "tombstones_registry_snapshots",
            connection =>
            {
                // Tombstones: every change-log row records whether it wrote a
                // value ('put') or deleted one ('del'). Pre-v7 rows are puts.
                // Column adds are guarded: SQLite has no ADD COLUMN IF NOT
                // EXISTS, and a hand-rolled (or partially rolled-back) older
                // file may already carry some of the v7 columns.
                if (!HasColumn(connection, ChangeLogTable, "Op"))
                {
                    connection.RunQueries([$"alter table {ChangeLogTable} add column Op TEXT NOT NULL DEFAULT 'put'"]);
                }

                connection.RunQueries(
                [
                    $"update {ChangeLogTable} set Op = 'put' where Op is null",
                    $"create index if not exists idx_{ChangeLogTable}_key on {ChangeLogTable}(UserId, Profile, Ns, Key, Seq)"
                ]);

                foreach (var (column, ddl) in new[]
                {
                    ("Model", $"alter table {DevicesTable} add column Model TEXT NULL"),
                    ("CapsJson", $"alter table {DevicesTable} add column CapsJson TEXT NULL"),
                    ("Revoked", $"alter table {DevicesTable} add column Revoked INTEGER NOT NULL DEFAULT 0")
                })
                {
                    if (!HasColumn(connection, DevicesTable, column))
                    {
                        connection.RunQueries([ddl]);
                    }
                }

                // Restore points: rolling full-settings snapshots per user.
                connection.RunQueries(
                [
                    $@"create table if not exists {SnapshotsTable} (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        UserId TEXT NOT NULL,
                        CreatedAt INTEGER NOT NULL,
                        Origin TEXT NOT NULL,
                        Keys INTEGER NOT NULL,
                        Bytes INTEGER NOT NULL,
                        Payload BLOB NOT NULL)",
                    $"create index if not exists idx_{SnapshotsTable}_user on {SnapshotsTable}(UserId, Id)"
                ]);
            }),
        (8, "idx_sync_history_user_device",
            connection => connection.RunQueries(
            [
                // GetLatestSyncPerDevice correlates max(Id) per device; without
                // this index the correlation scanned the whole history per
                // device on every sync/status poll.
                $"create index if not exists idx_{SyncHistoryTable}_user_device on {SyncHistoryTable}(UserId, DeviceId, Id)"
            ])),
        (9, "read_path_indexes",
            connection => connection.RunQueries(
            [
                // GetPlaybackSessions/GetTopPlaybackItems/CountDistinctPlaybackItems
                // all filter on EndedAt.
                $"create index if not exists idx_{PlaybackSessionsTable}_ended on {PlaybackSessionsTable}(EndedAt, UserId)",
                // The profile-scoped delta join (GetChangedSettings) filters
                // change_log by (UserId, Profile) with a Seq ordering.
                $"create index if not exists idx_{ChangeLogTable}_user_profile_seq on {ChangeLogTable}(UserId, Profile, Seq)"
            ]))
    ];

    /// <summary>Whether the table has the column (pragma table_info scan) — the guard behind the column-add migrations (4, 6 and 7).</summary>
    private static bool HasColumn(SqliteConnection connection, string table, string column)
    {
        using var statement = connection.Prepare($"pragma table_info({table})");
        foreach (var row in statement.Select(row => row.GetString(1)))
        {
            if (string.Equals(row, column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly string _dbFilePath;
    private readonly ILogger<JellyPlayDatabase>? _logger;

    /// <summary>Idle-connection pool cap; a return past it falls through to real disposal.</summary>
    private const int ConnectionPoolCapacity = 4;

    private readonly object _poolLock = new();
    private readonly Stack<PooledSqliteConnection> _idleConnections = new();
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
        // journal_mode is PERSISTENT in the file header: set once here (schema
        // init / quarantine rebuild), never per connection — every open used
        // to re-issue it. synchronous and foreign_keys are per-connection and
        // stay on CreateConnection.
        connection.RunQueries(["pragma journal_mode=WAL"]);

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
            // Under the WRITE lock no store method can hold a lease (every
            // one holds at least the read lock across its connection's
            // lifetime), so everything pooled is idle and safely closeable —
            // the drain below releases the file handles the probe and any
            // quarantine rename need.
            DrainIdleConnections();
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
        // The probe connection CheckIntegrity used has been returned to the
        // idle pool by now (still open, still holding the file); closing the
        // idles releases the handles the renames below need.
        DrainIdleConnections();
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
        DrainIdleConnections();
        _lock.Dispose();
    }

    // ------------------------------------------------------------------
    // Plumbing
    // ------------------------------------------------------------------

    /// <summary>
    /// Write quotas. <see cref="NamespaceBytes"/> (namespace → byte cap, schema
    /// v7's per-namespace quotas) bounds each listed namespace across the
    /// user's profiles on top of the per-user total; namespaces absent from the
    /// map are bounded only by <see cref="MaxUserBytes"/>.
    /// </summary>
    public sealed record Quotas(int MaxKeyBytes, int MaxUserBytes, int MaxKeysPerUser, IReadOnlyDictionary<string, int>? NamespaceBytes = null);

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
        row.IsDBNull(8) ? null : row.GetInt64(8),
        row.IsDBNull(9) ? null : row.GetString(9),
        row.IsDBNull(10) ? null : row.GetString(10),
        row.GetInt64(11) != 0);

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

    /// <summary>
    /// The batched bounded delete every retention prune shares: one prepared
    /// statement re-executed in 5000-row batches under the caller's held
    /// write lock (the batching bounds per-statement transaction/log size,
    /// not lock scope). The cutoff is always the caller's — retention tests
    /// pin it instead of racing the wall clock.
    /// </summary>
    private static int BatchedDelete(SqliteConnection connection, string table, string where, long cutoff)
    {
        using var statement = connection.Prepare(
            $"delete from {table} where rowid in (select rowid from {table} where {where} limit 5000)");
        statement.Bind("@Cutoff", cutoff);
        var total = 0;
        int removed;
        do
        {
            removed = statement.ExecuteNonQuery();
            total += removed;
        }
        while (removed > 0);
        return total;
    }

    private SqliteConnection CreateConnection()
    {
        // Lease from the idle pool first: a warm connection skips the open +
        // pragma round trip entirely (both pragmas ride the connection string
        // or were set when the connection was first opened).
        lock (_poolLock)
        {
            if (!_disposed && _idleConnections.TryPop(out var pooled))
            {
                pooled.InPool = false;
                return pooled;
            }
        }

        // Pooling stays off for Microsoft.Data.Sqlite's own pool: it would
        // hold file handles past Dispose, out of this class's reach — the
        // internal pool above keeps them releasable (see DrainIdleConnections).
        var connection = new PooledSqliteConnection($"Filename={_dbFilePath};Pooling=False;Foreign Keys=True", this);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "pragma synchronous=NORMAL";
        command.ExecuteNonQuery();
        return connection;
    }

    /// <summary>
    /// Takes a leased-out connection back into the idle pool; false means the
    /// caller must dispose for real. The flag and stack are only ever touched
    /// under <see cref="_poolLock"/>.
    /// </summary>
    private bool TryReturnToPool(PooledSqliteConnection connection)
    {
        lock (_poolLock)
        {
            if (_disposed || connection.InPool || _idleConnections.Count >= ConnectionPoolCapacity)
            {
                return false;
            }

            connection.InPool = true;
            _idleConnections.Push(connection);
            return true;
        }
    }

    /// <summary>
    /// Closes every idle pooled connection so nothing holds the database
    /// file. Safe by construction: the two callers run under the WRITE lock
    /// (quarantine/integrity) or past disposal, and every store method holds
    /// at least the read lock across its connection's lifetime — no lease
    /// can be outstanding while an idle connection is being closed here.
    /// </summary>
    private void DrainIdleConnections()
    {
        lock (_poolLock)
        {
            while (_idleConnections.TryPop(out var pooled))
            {
                // InPool is still true, so a late Close/Dispose on this
                // connection also falls through to real disposal.
                pooled.CloseForReal();
            }
        }
    }

    /// <summary>
    /// A pooled lease: Close/Dispose hand the still-open connection back to
    /// the owning database's idle stack instead of tearing it down. Falls
    /// through to real disposal when the pool is at capacity, the connection
    /// is mid-transaction or not open, or the database was disposed.
    /// </summary>
    private sealed class PooledSqliteConnection : SqliteConnection
    {
        private readonly JellyPlayDatabase _owner;

        /// <summary>Guarded by the owner's pool lock: true while the connection sits in the idle stack (drain pops without clearing it, so a late Close disposes for real).</summary>
        public bool InPool;

        public PooledSqliteConnection(string connectionString, JellyPlayDatabase owner)
            : base(connectionString)
        {
            _owner = owner;
        }

        public override void Close()
        {
            if (EligibleForPool() && _owner.TryReturnToPool(this))
            {
                return;
            }

            base.Close();
        }

        protected override void Dispose(bool disposing)
        {
            // Already returned via Close(): the instance still sits on the
            // idle stack, so its cleanup belongs to the owner (a later lease
            // or the drain) — disposing here would hand out a dead connection.
            if (disposing && InPool)
            {
                return;
            }

            if (disposing && EligibleForPool() && _owner.TryReturnToPool(this))
            {
                return;
            }

            base.Dispose(disposing);
        }

        /// <summary>Unconditional close for the drain path — bypasses the pool return.</summary>
        public void CloseForReal() => base.Close();

        private bool EligibleForPool()
            => State == System.Data.ConnectionState.Open && Transaction is null;
    }

    /// <summary>Binds a nullable scalar (long or string) as NULL when absent — the one binder both former overloads shared byte-for-byte.</summary>
    private static void BindNullable(SqliteCommand statement, string name, object? value)
    {
        var parameter = statement.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? System.DBNull.Value;
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
