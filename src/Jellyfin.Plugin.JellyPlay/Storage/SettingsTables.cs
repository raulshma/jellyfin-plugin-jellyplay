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
