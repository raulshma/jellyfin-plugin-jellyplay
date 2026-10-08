using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyPlay.Storage.Models;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.JellyPlay.Storage;

/// <summary>Settings, change log, sync history, footprints and admin defaults — the settings-store half of JellyPlayDatabase.</summary>
public sealed partial class JellyPlayDatabase
{
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
    /// Applies a batch with per-key last-write-wins (see <see cref="ApplyWriteLoop"/>
    /// for the LWW/tombstone/quota rules). The bare store API: no clock-skew
    /// ceiling, no device-revocation refusal — those are the service-facing
    /// composite's concerns.
    /// </summary>
    public UpsertResult UpsertSettings(
        string userId,
        string profile,
        IReadOnlyList<SettingWrite> writes,
        Quotas quotas)
    {
        using (_lock.Write())
        using (var connection = CreateConnection())
        using (var transaction = connection.BeginTransaction())
        {
            var (applied, rejected) = ApplyWriteLoop(connection, userId, profile, writes, quotas, maxWriteUpdatedAt: long.MaxValue, revokedDeviceId: null);
            transaction.Commit();
            return new UpsertResult(applied, rejected);
        }
    }

    /// <summary>The composite batch outcome: the change-log heads bracketing the batch plus the apply result.</summary>
    public sealed record SettingsBatchOutcome(
        long HeadBefore,
        long HeadAfter,
        IReadOnlyList<AppliedSetting> Applied,
        IReadOnlyList<RejectedSetting> Rejected);

    /// <summary>
    /// The service-facing batch composite: the device-revocation refusal, the
    /// clock-skew ceiling, the shared LWW/tombstone/quota write loop AND the
    /// sync-history record all land in ONE connection/transaction (the path
    /// this replaces opened five-plus). The history write is best-effort by
    /// contract — a failed observability write never fails the batch (the
    /// service still fans the operation out to the admin stream itself). The
    /// recorded bytes follow the service's own approximation: the serialized
    /// length of every batch write whose key applied.
    /// </summary>
    public SettingsBatchOutcome ApplyBatchWithHistory(
        string userId,
        string profile,
        IReadOnlyList<SettingWrite> writes,
        Quotas quotas,
        string deviceId,
        long maxWriteUpdatedAt,
        string op,
        long historyTs,
        Func<IReadOnlyList<RejectedSetting>, string?>? rejectsJsonBuilder)
    {
        long headBefore;
        long headAfter;
        List<AppliedSetting> applied;
        List<RejectedSetting> rejected;
        using (_lock.Write())
        using (var connection = CreateConnection())
        {
            using (var transaction = connection.BeginTransaction())
            {
                headBefore = GetChangeLogHead(connection, userId);
                (applied, rejected) = ApplyWriteLoop(connection, userId, profile, writes, quotas, maxWriteUpdatedAt, revokedDeviceId: deviceId);
                headAfter = GetChangeLogHead(connection, userId);

                try
                {
                    var appliedSet = applied.Select(a => (a.Ns, a.Key)).ToHashSet();
                    var appliedBytes = writes
                        .Where(write => appliedSet.Contains((write.Ns, write.Key)))
                        .Sum(write => (long)write.Value.Length);
                    InsertSyncHistory(
                        connection,
                        userId,
                        deviceId,
                        op,
                        applied.Count,
                        rejected.Count,
                        appliedBytes,
                        rejectsJsonBuilder?.Invoke(rejected),
                        historyTs,
                        headBefore,
                        headAfter);
                }
                catch (Exception)
                {
                    // Observability is best-effort by contract: never fail the batch.
                }

                transaction.Commit();
            }
        }

        return new SettingsBatchOutcome(headBefore, headAfter, applied, rejected);
    }

    /// <summary>
    /// The ONE batch write loop — every path that applies settings writes
    /// (wire pushes, restores, imports, admin defaults) funnels through here.
    /// Per-key last-write-wins: an incoming write applies only when its
    /// timestamp is strictly newer than the stored one (see
    /// <see cref="SettingsLww.WouldApply"/>); equal timestamps reject with
    /// stale-write (deterministic, no oscillation). Applied writes append one
    /// change-log row each, stamped with its op ('put' | 'del' — schema v7
    /// tombstones).
    ///
    /// A tombstone (write.IsDelete) removes the stored row and appends a 'del'
    /// change-log row; when the row is already absent the tombstone is still
    /// recorded (newer than the key's latest change-log entry), so in-flight
    /// older puts cannot resurrect the key. Anti-resurrection for puts: when
    /// no row exists, the write must beat the key's latest change-log entry —
    /// a live row always implies it is the newest entry.
    ///
    /// Quotas: the user's footprint and per-namespace byte totals are read
    /// ONCE and kept as running counters as writes apply (deletes release
    /// their bytes, overwrites adjust by their delta — a shrink included),
    /// so a batch costs two aggregate scans total instead of up to three per
    /// key under the write lock. When <paramref name="revokedDeviceId"/> is
    /// non-null, a revoked device's writes are refused wholesale (its keys were
    /// wiped at revoke time — re-pushing them must not resurrect); writes
    /// stamped past <paramref name="maxWriteUpdatedAt"/> reject with
    /// clock-skew (long.MaxValue disables the ceiling).
    /// </summary>
    private (List<AppliedSetting> Applied, List<RejectedSetting> Rejected) ApplyWriteLoop(
        SqliteConnection connection,
        string userId,
        string profile,
        IReadOnlyList<SettingWrite> writes,
        Quotas quotas,
        long maxWriteUpdatedAt,
        string? revokedDeviceId)
    {
        var applied = new List<AppliedSetting>();
        var rejected = new List<RejectedSetting>();
        if (writes.Count == 0)
        {
            return (applied, rejected);
        }

        var (keyCount, totalBytes) = GetUserSettingsFootprint(connection, userId);
        var namespaceBytes = LoadNamespaceBytes(connection, userId);
        var deviceRevoked = revokedDeviceId is not null && IsDeviceRevoked(connection, revokedDeviceId);

        foreach (var write in writes)
        {
            if (deviceRevoked)
            {
                rejected.Add(new RejectedSetting(write.Ns, write.Key, "device-revoked"));
                continue;
            }

            if (write.UpdatedAt > maxWriteUpdatedAt)
            {
                rejected.Add(new RejectedSetting(write.Ns, write.Key, "clock-skew"));
                continue;
            }

            if (!write.IsDelete && write.Value.Length > quotas.MaxKeyBytes)
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

            if (existingValue is null)
            {
                // No live row: the key's latest change-log entry (put or
                // del) is the watermark the write must beat.
                var latest = GetLatestChangeFor(connection, userId, profile, write.Ns, write.Key);
                if (latest is not null && !SettingsLww.WouldApply(write.UpdatedAt, latest.Value.UpdatedAt))
                {
                    rejected.Add(new RejectedSetting(write.Ns, write.Key, "stale-write"));
                    continue;
                }
            }
            else if (!SettingsLww.WouldApply(write.UpdatedAt, existingUpdatedAt))
            {
                rejected.Add(new RejectedSetting(write.Ns, write.Key, "stale-write"));
                continue;
            }

            if (write.IsDelete)
            {
                if (existingValue is not null)
                {
                    using (var delete = connection.Prepare(
                               $"delete from {SettingsTable} where UserId = @UserId and Profile = @Profile and Ns = @Ns and Key = @Key"))
                    {
                        delete.Bind("@UserId", userId);
                        delete.Bind("@Profile", profile);
                        delete.Bind("@Ns", write.Ns);
                        delete.Bind("@Key", write.Key);
                        delete.ExecuteNonQuery();
                    }

                    keyCount--;
                    totalBytes -= existingValue.Length;
                    namespaceBytes[write.Ns] = NamespaceBytesOf(namespaceBytes, write.Ns) - existingValue.Length;
                }

                var seq = AppendChangeLog(connection, userId, profile, write.Ns, write.Key, write.UpdatedAt, "del");
                applied.Add(new AppliedSetting(write.Ns, write.Key, write.UpdatedAt, seq, Deleted: true));
                continue;
            }

            if (existingValue is null)
            {
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

                if (ExceedsNamespaceQuota(namespaceBytes, write.Ns, write.Value.Length, quotas))
                {
                    rejected.Add(new RejectedSetting(write.Ns, write.Key, "ns-quota-exceeded"));
                    continue;
                }

                keyCount++;
                totalBytes += write.Value.Length;
                namespaceBytes[write.Ns] = NamespaceBytesOf(namespaceBytes, write.Ns) + write.Value.Length;
            }
            else
            {
                var growth = write.Value.Length - existingValue!.Length;
                if (growth > 0)
                {
                    if (totalBytes + growth > quotas.MaxUserBytes)
                    {
                        rejected.Add(new RejectedSetting(write.Ns, write.Key, "quota-exceeded"));
                        continue;
                    }

                    if (ExceedsNamespaceQuota(namespaceBytes, write.Ns, growth, quotas))
                    {
                        rejected.Add(new RejectedSetting(write.Ns, write.Key, "ns-quota-exceeded"));
                        continue;
                    }
                }

                // Overwrites adjust by the delta — negative included — so an
                // in-batch shrink credits the freed bytes back and a later
                // grow in the same batch is judged against the real total
                // (the fresh-per-key totals this loop replaced never
                // double-counted a shrink).
                totalBytes += growth;
                namespaceBytes[write.Ns] = NamespaceBytesOf(namespaceBytes, write.Ns) + growth;
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

            var putSeq = AppendChangeLog(connection, userId, profile, write.Ns, write.Key, write.UpdatedAt, "put");
            applied.Add(new AppliedSetting(write.Ns, write.Key, write.UpdatedAt, putSeq));
        }

        return (applied, rejected);
    }

    private static long NamespaceBytesOf(Dictionary<string, long> namespaceBytes, string ns)
        => namespaceBytes.TryGetValue(ns, out var current) ? current : 0;

    /// <summary>Total stored bytes per namespace across the user's profiles, read once per batch (the running quota counters' baseline).</summary>
    private Dictionary<string, long> LoadNamespaceBytes(SqliteConnection connection, string userId)
    {
        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        using var statement = connection.Prepare(
            $"select Ns, coalesce(sum(length(Value)), 0) from {SettingsTable} where UserId = @UserId group by Ns");
        statement.Bind("@UserId", userId);
        foreach (var row in statement.Select(row => (Ns: row.GetString(0), Bytes: row.GetInt64(1))))
        {
            totals[row.Ns] = row.Bytes;
        }

        return totals;
    }

    /// <summary>The key's latest change-log entry (any op), or null when the key was never touched.</summary>
    private static (long Seq, long UpdatedAt, string Op)? GetLatestChangeFor(SqliteConnection connection, string userId, string profile, string ns, string key)
    {
        using var statement = connection.Prepare(
            $@"select Seq, UpdatedAt, Op from {ChangeLogTable}
               where UserId = @UserId and Profile = @Profile and Ns = @Ns and Key = @Key
               order by Seq desc limit 1");
        statement.Bind("@UserId", userId);
        statement.Bind("@Profile", profile);
        statement.Bind("@Ns", ns);
        statement.Bind("@Key", key);
        foreach (var row in statement.Select(row => (row.GetInt64(0), row.GetInt64(1), row.GetString(2))))
        {
            return row;
        }

        return null;
    }

    /// <summary>Appends one change-log row and returns its seq.</summary>
    private static long AppendChangeLog(SqliteConnection connection, string userId, string profile, string ns, string key, long updatedAt, string op)
    {
        using var logInsert = connection.Prepare(
            $@"insert into {ChangeLogTable} (UserId, Profile, Ns, Key, UpdatedAt, Op)
               values (@UserId, @Profile, @Ns, @Key, @UpdatedAt, @Op);
               select last_insert_rowid();");
        logInsert.Bind("@UserId", userId);
        logInsert.Bind("@Profile", profile);
        logInsert.Bind("@Ns", ns);
        logInsert.Bind("@Key", key);
        logInsert.Bind("@UpdatedAt", updatedAt);
        logInsert.Bind("@Op", op);
        return (long)(logInsert.ExecuteScalar() ?? 0L);
    }

    /// <summary>
    /// Namespace byte-quota check: does adding <paramref name="growth"/> bytes
    /// to the namespace cross its configured cap? Namespaces without a cap are
    /// bounded only by the per-user total (checked by the caller). Reads the
    /// batch's running <paramref name="namespaceBytes"/> counters — the
    /// per-connection aggregate scan this used to re-issue per key is gone.
    /// </summary>
    private static bool ExceedsNamespaceQuota(Dictionary<string, long> namespaceBytes, string ns, int growth, Quotas quotas)
    {
        if (quotas.NamespaceBytes is not { } caps
            || !caps.TryGetValue(ns, out var cap)
            || cap <= 0)
        {
            return false;
        }

        var current = namespaceBytes.TryGetValue(ns, out var bytes) ? bytes : 0;
        return current + growth > cap;
    }

    /// <summary>
    /// Reset: atomically tombstones every settings row of one namespace — rows
    /// are removed and one 'del' change-log row per key is appended, so peers'
    /// deltas carry the deletions and stale pushes cannot resurrect the keys.
    /// The change log is never wiped. Returns the number of rows removed.
    /// </summary>
    public int DeleteNamespace(string userId, string profile, string ns, long tombstoneAt)
    {
        int deleted;
        using (_lock.Write())
        using (var connection = CreateConnection())
        using (var transaction = connection.BeginTransaction())
        {
            deleted = TombstoneRows(connection, userId, tombstoneAt, profile: profile, ns: ns);
            transaction.Commit();
        }

        return deleted;
    }

    /// <summary>
    /// Device wipe: atomically tombstones every settings row written by one
    /// device id (all profiles) — the revoke action's data half. Returns the
    /// number of rows removed.
    /// </summary>
    public int TombstoneDeviceSettings(string userId, string deviceId, long tombstoneAt)
    {
        int deleted;
        using (_lock.Write())
        using (var connection = CreateConnection())
        using (var transaction = connection.BeginTransaction())
        {
            deleted = TombstoneRows(connection, userId, tombstoneAt, deviceId: deviceId);
            transaction.Commit();
        }

        return deleted;
    }

    /// <summary>
    /// The shared tombstone batch (namespace reset, device wipe, restore
    /// diff): appends one 'del' change-log row per matched row under the row's
    /// own profile/ns (anti-resurrection — the change log is never wiped) via
    /// a single INSERT … SELECT, then bulk-deletes the matched rows, all in
    /// the caller's transaction. Returns the number of rows removed.
    /// </summary>
    private int TombstoneRows(SqliteConnection connection, string userId, long tombstoneAt, string? profile = null, string? ns = null, string? deviceId = null)
    {
        var predicate = "UserId = @UserId"
            + (profile is null ? string.Empty : " and Profile = @Profile")
            + (ns is null ? string.Empty : " and Ns = @Ns")
            + (deviceId is null ? string.Empty : " and DeviceId = @DeviceId");

        using (var logInsert = connection.Prepare(
                   $@"insert into {ChangeLogTable} (UserId, Profile, Ns, Key, UpdatedAt, Op)
                      select UserId, Profile, Ns, Key, @UpdatedAt, 'del' from {SettingsTable} where {predicate}"))
        {
            BindTombstoneFilters(logInsert, userId, profile, ns, deviceId);
            logInsert.Bind("@UpdatedAt", tombstoneAt);
            logInsert.ExecuteNonQuery();
        }

        using (var statement = connection.Prepare($"delete from {SettingsTable} where {predicate}"))
        {
            BindTombstoneFilters(statement, userId, profile, ns, deviceId);
            return statement.ExecuteNonQuery();
        }
    }

    private static void BindTombstoneFilters(SqliteCommand statement, string userId, string? profile, string? ns, string? deviceId)
    {
        statement.Bind("@UserId", userId);
        if (profile is not null)
        {
            statement.Bind("@Profile", profile);
        }

        if (ns is not null)
        {
            statement.Bind("@Ns", ns);
        }

        if (deviceId is not null)
        {
            statement.Bind("@DeviceId", deviceId);
        }
    }

    /// <summary>
    /// Current values for keys touched after <paramref name="sinceSeq"/> in the
    /// change log, scoped to one profile and paged IN SQL (the delta pull used
    /// to materialize the whole correlation to slice it in C#). The page is
    /// the ordered window [offset, offset + limit); callers over-fetch by one
    /// to detect a following page.
    /// </summary>
    public IReadOnlyList<SettingRow> GetChangedSettings(string userId, long sinceSeq, string profile, int offset, int limit)
    {
        using (_lock.Read())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare(
                   $@"select s.UserId, s.Profile, s.Ns, s.Key, s.SchemaVersion, s.UpdatedAt, s.DeviceId, s.Value
                      from {ChangeLogTable} c
                      join {SettingsTable} s
                        on s.UserId = c.UserId and s.Profile = c.Profile and s.Ns = c.Ns and s.Key = c.Key
                      where c.UserId = @UserId and c.Seq > @SinceSeq and c.Profile = @Profile
                      group by s.UserId, s.Profile, s.Ns, s.Key
                      order by max(c.Seq)
                      limit @Limit offset @Offset"))
        {
            statement.Bind("@UserId", userId);
            statement.Bind("@SinceSeq", sinceSeq);
            statement.Bind("@Profile", profile);
            statement.Bind("@Limit", limit);
            statement.Bind("@Offset", offset);
            return statement.Select(ReadSettingRow).ToList();
        }
    }

    /// <summary>
    /// Keys tombstoned (deleted) at or after <paramref name="sinceSeq"/> and
    /// still absent — the delta's <c>deleted[]</c> half, scoped to one profile.
    /// A key deleted and re-created after the cursor appears only in the live
    /// rows, never here.
    /// </summary>
    public IReadOnlyList<DeletedSettingKey> GetDeletedSettings(string userId, long sinceSeq, string profile)
    {
        using (_lock.Read())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare(
                   $@"select distinct c.Profile, c.Ns, c.Key
                      from {ChangeLogTable} c
                      where c.UserId = @UserId and c.Seq > @SinceSeq and c.Profile = @Profile and c.Op = 'del'
                        and not exists (
                            select 1 from {SettingsTable} s
                            where s.UserId = c.UserId and s.Profile = c.Profile and s.Ns = c.Ns and s.Key = c.Key)
                      order by c.Profile, c.Ns, c.Key"))
        {
            statement.Bind("@UserId", userId);
            statement.Bind("@SinceSeq", sinceSeq);
            statement.Bind("@Profile", profile);
            return statement.Select(row => new DeletedSettingKey(row.GetString(0), row.GetString(1), row.GetString(2))).ToList();
        }
    }

    public long GetChangeLogHead(string userId)
    {
        using (_lock.Read())
        using (var connection = CreateConnection())
        {
            return GetChangeLogHead(connection, userId);
        }
    }

    /// <summary>Connection-scoped change-log head (the batch composite reads it inside its own transaction).</summary>
    private static long GetChangeLogHead(SqliteConnection connection, string userId)
    {
        using var statement = connection.Prepare($"select coalesce(max(Seq), 0) from {ChangeLogTable} where UserId = @UserId");
        statement.Bind("@UserId", userId);
        return (long)(statement.ExecuteScalar() ?? 0L);
    }

    // ------------------------------------------------------------------
    // Restore points (user_snapshots, schema v7)
    // ------------------------------------------------------------------

    /// <summary>Every settings row of one user across all profiles (snapshot capture, restore).</summary>
    public IReadOnlyList<SettingRow> GetAllSettingsRows(string userId)
    {
        using (_lock.Read())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare(
                   $"select UserId, Profile, Ns, Key, SchemaVersion, UpdatedAt, DeviceId, Value from {SettingsTable} where UserId = @UserId order by Profile, Ns, Key"))
        {
            statement.Bind("@UserId", userId);
            return statement.Select(ReadSettingRow).ToList();
        }
    }

    /// <summary>The distinct profiles one user has settings rows for.</summary>
    public IReadOnlyList<string> GetDistinctSettingProfiles(string userId)
    {
        using (_lock.Read())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare($"select distinct Profile from {SettingsTable} where UserId = @UserId order by Profile"))
        {
            statement.Bind("@UserId", userId);
            return statement.Select(row => row.GetString(0)).ToList();
        }
    }

    /// <summary>
    /// Inserts one snapshot and enforces the rolling keep-last window in the
    /// same transaction, so a capture storm can never grow the store.
    /// </summary>
    public long InsertSnapshot(string userId, byte[] payload, long createdAt, string origin, int keys, long bytes, int keepLast)
    {
        long id;
        using (_lock.Write())
        using (var connection = CreateConnection())
        using (var transaction = connection.BeginTransaction())
        {
            using (var statement = connection.Prepare(
                       $@"insert into {SnapshotsTable} (UserId, CreatedAt, Origin, Keys, Bytes, Payload)
                          values (@UserId, @CreatedAt, @Origin, @Keys, @Bytes, @Payload);
                          select last_insert_rowid();"))
            {
                statement.Bind("@UserId", userId);
                statement.Bind("@CreatedAt", createdAt);
                statement.Bind("@Origin", origin);
                statement.Bind("@Keys", keys);
                statement.Bind("@Bytes", bytes);
                statement.Bind("@Payload", payload);
                id = (long)(statement.ExecuteScalar() ?? 0L);
            }

            using (var trim = connection.Prepare(
                       $@"delete from {SnapshotsTable} where UserId = @UserId and Id not in (
                              select Id from {SnapshotsTable} where UserId = @UserId order by Id desc limit @KeepLast)"))
            {
                trim.Bind("@UserId", userId);
                trim.Bind("@KeepLast", keepLast);
                trim.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        return id;
    }

    /// <summary>The caller's snapshots, newest-first (metadata only — payloads are fetched on restore).</summary>
    public IReadOnlyList<SnapshotRow> GetSnapshots(string userId)
    {
        using (_lock.Read())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare(
                   $"select Id, UserId, CreatedAt, Origin, Keys, Bytes from {SnapshotsTable} where UserId = @UserId order by Id desc"))
        {
            statement.Bind("@UserId", userId);
            return statement.Select(row => new SnapshotRow(
                row.GetInt64(0),
                row.GetString(1),
                row.GetInt64(2),
                row.GetString(3),
                (int)row.GetInt64(4),
                row.GetInt64(5))).ToList();
        }
    }

    /// <summary>One snapshot with its payload — only when owned by the user (the restore path's ownership check).</summary>
    public (SnapshotRow Row, byte[] Payload)? GetSnapshot(string userId, long id)
    {
        using (_lock.Read())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare(
                   $"select Id, UserId, CreatedAt, Origin, Keys, Bytes, Payload from {SnapshotsTable} where Id = @Id and UserId = @UserId"))
        {
            statement.Bind("@Id", id);
            statement.Bind("@UserId", userId);
            foreach (var row in statement.Select(row => (
                         Row: new SnapshotRow(
                             row.GetInt64(0),
                             row.GetString(1),
                             row.GetInt64(2),
                             row.GetString(3),
                             (int)row.GetInt64(4),
                             row.GetInt64(5)),
                         Payload: (byte[])row.GetValue(6))))
            {
                return (row.Row, row.Payload);
            }

            return null;
        }
    }

    public int PruneSnapshots(int retentionDays)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays).ToUnixTimeMilliseconds();
        using (_lock.Write())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare($"delete from {SnapshotsTable} where CreatedAt < @Cutoff"))
        {
            statement.Bind("@Cutoff", cutoff);
            return statement.ExecuteNonQuery();
        }
    }

    /// <summary>Change-log rows with Seq in (fromSeq, toSeq] for one user, newest-first — the per-key diff of a recorded sync operation.</summary>
    public IReadOnlyList<ChangeLogEntry> GetChangeLogRange(string userId, long fromSeq, long toSeq, int limit)
    {
        using (_lock.Read())
        using (var connection = CreateConnection())
        {
            return GetChangeLogRange(connection, userId, fromSeq, toSeq, limit);
        }
    }

    /// <summary>Connection-scoped change-log range (the audit composite resolves every bracket on one connection).</summary>
    private static IReadOnlyList<ChangeLogEntry> GetChangeLogRange(SqliteConnection connection, string userId, long fromSeq, long toSeq, int limit)
    {
        using var statement = connection.Prepare(
                   $@"select Seq, UserId, Profile, Ns, Key, UpdatedAt from {ChangeLogTable}
                      where UserId = @UserId and Seq > @FromSeq and Seq <= @ToSeq
                      order by Seq desc
                      limit @Limit");
        statement.Bind("@UserId", userId);
        statement.Bind("@FromSeq", fromSeq);
        statement.Bind("@ToSeq", toSeq);
        statement.Bind("@Limit", limit);
        return statement.Select(ReadChangeLogEntry).ToList();
    }

    private static ChangeLogEntry ReadChangeLogEntry(SqliteRow row) => new(
        row.GetInt64(0),
        row.GetString(1),
        row.GetString(2),
        row.GetString(3),
        row.GetString(4),
        row.GetInt64(5));

    /// <summary>
    /// The audit export's composite: a page of recorded operations with every
    /// usable (FromSeq, ToSeq] bracket resolved to its change-log rows on ONE
    /// connection — the export used to open one connection per bracket, up to
    /// the page cap per export. Rows without a usable range (pre-v7, no-op)
    /// carry an empty key list, exactly like the per-key diff endpoint.
    /// </summary>
    public IReadOnlyList<SyncHistoryWithKeys> GetSyncHistoryWithKeys(string userId, long sinceTs, int limit, int keysLimit)
    {
        using (_lock.Read())
        using (var connection = CreateConnection())
        {
            IReadOnlyList<SyncHistoryRow> rows;
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
                rows = statement.Select(ReadSyncHistoryRow).ToList();
            }

            var result = new List<SyncHistoryWithKeys>(rows.Count);
            foreach (var row in rows)
            {
                if (!row.HasRange)
                {
                    result.Add(new SyncHistoryWithKeys(row, Array.Empty<ChangeLogEntry>()));
                    continue;
                }

                result.Add(new SyncHistoryWithKeys(row, GetChangeLogRange(connection, userId, row.FromSeq!.Value, row.ToSeq!.Value, keysLimit)));
            }

            return result;
        }
    }

    /// <summary>
    /// Retention prune for the change log. Tombstones (op='del') are EXEMPT:
    /// they are the anti-resurrection watermark for their key (the absent-row
    /// LWW check reads the latest entry — put OR del), so pruning one would
    /// let a stale offline put resurrect a long-deleted key. Tombstone rows
    /// are tiny; they are kept forever, only 'put' rows age out.
    /// </summary>
    public int PruneChangeLog(int retentionDays)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays).ToUnixTimeMilliseconds();
        using (_lock.Write())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare($"delete from {ChangeLogTable} where UpdatedAt < @Cutoff and Op != 'del'"))
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
        {
            return InsertSyncHistory(connection, userId, deviceId, op, keysApplied, keysRejected, bytes, rejectsJson, ts, fromSeq, toSeq);
        }
    }

    /// <summary>Connection-scoped history insert (the batch composite records inside its own transaction).</summary>
    private static long InsertSyncHistory(
        SqliteConnection connection,
        string userId,
        string deviceId,
        string op,
        int keysApplied,
        int keysRejected,
        long bytes,
        string? rejectsJson,
        long ts,
        long? fromSeq,
        long? toSeq)
    {
        using var statement = connection.Prepare(
                   $@"insert into {SyncHistoryTable} (UserId, DeviceId, Ts, Op, KeysApplied, KeysRejected, Bytes, RejectsJson, FromSeq, ToSeq)
                      values (@UserId, @DeviceId, @Ts, @Op, @KeysApplied, @KeysRejected, @Bytes, @RejectsJson, @FromSeq, @ToSeq);
                      select last_insert_rowid();");
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
        {
            return GetLatestSyncPerDevice(connection, userId);
        }
    }

    /// <summary>Connection-scoped per-device fold (the status composite reads it on its own connection).</summary>
    private static IReadOnlyList<DeviceSyncSummary> GetLatestSyncPerDevice(SqliteConnection connection, string userId)
    {
        using var statement = connection.Prepare(
                   $@"select DeviceId, Ts, Op from {SyncHistoryTable} h
                      where UserId = @UserId
                        and Id = (select max(Id) from {SyncHistoryTable}
                                  where UserId = h.UserId and DeviceId = h.DeviceId)
                      order by Ts desc");
        statement.Bind("@UserId", userId);
        return statement.Select(row => new DeviceSyncSummary(
            row.GetString(0), row.GetInt64(1), row.GetString(2))).ToList();
    }

    /// <summary>
    /// The status poll's composite: change-log head, user footprint, namespace
    /// rollups and the per-device fold resolved in ONE connection — the four
    /// separate connections this read used to open on every sync/status hit.
    /// </summary>
    public SyncStatusBundle GetSyncStatusBundle(string userId)
    {
        using (_lock.Read())
        using (var connection = CreateConnection())
        {
            var (keys, bytes) = GetUserSettingsFootprint(connection, userId);
            return new SyncStatusBundle(
                GetChangeLogHead(connection, userId),
                keys,
                bytes,
                GetNamespaceFootprints(connection, userId),
                GetLatestSyncPerDevice(connection, userId));
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
        {
            return GetNamespaceFootprints(connection, userId);
        }
    }

    /// <summary>Connection-scoped namespace rollups (the status composite reads them on its own connection).</summary>
    private static IReadOnlyList<NamespaceFootprint> GetNamespaceFootprints(SqliteConnection connection, string userId)
    {
        using var statement = connection.Prepare(
                   $"select Ns, count(*), coalesce(sum(length(Value)), 0) from {SettingsTable} where UserId = @UserId group by Ns order by Ns");
        statement.Bind("@UserId", userId);
        return statement.Select(row => new NamespaceFootprint(
            row.GetString(0), (int)row.GetInt64(1), row.GetInt64(2))).ToList();
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

}
