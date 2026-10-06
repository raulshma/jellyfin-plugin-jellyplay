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
