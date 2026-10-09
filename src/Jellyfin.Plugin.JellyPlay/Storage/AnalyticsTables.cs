using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyPlay.Storage.Models;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.JellyPlay.Storage;

/// <summary>Playback sessions, rollups and analytics maintenance — the playback analytics half of JellyPlayDatabase.</summary>
public sealed partial class JellyPlayDatabase
{
    // ------------------------------------------------------------------
    // Playback analytics (playback_sessions + playback_rollups)
    // ------------------------------------------------------------------

    /// <summary>Canonical playback-session column list (schema v5), shared by every session query.</summary>
    private const string PlaybackSessionColumns =
        "Id, UserId, ItemId, ItemName, ItemType, SeriesName, PlayMethod, VideoCodec, AudioCodec, Bitrate, " +
        "TranscodeReasonsJson, PositionTicks, DurationTicks, StartedAt, EndedAt, ClientName, DeviceName";

    /// <summary>StartedAt is stored truncated to this bucket; the unique (UserId, ItemId, StartedAt) index makes the dedup rule ("one row per user/item/start-minute") total.</summary>
    public const long MinuteBucketMs = 60_000;

    /// <summary>
    /// Inserts one finished playback session. The dedup invariant lives HERE,
    /// where the unique index does: StartedAt is truncated to its minute
    /// bucket on the way in (idempotent for already-bucketed callers), so a
    /// duplicate (user, item, start-minute) insert is a no-op. Returns the new
    /// row id, or 0 when the row was deduped away.
    /// </summary>
    public long InsertPlaybackSession(PlaybackSessionRow session)
    {
        var startedAt = session.StartedAt - (session.StartedAt % MinuteBucketMs);
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
            BindNullable(statement, "@SeriesName", session.SeriesName);
            statement.Bind("@PlayMethod", session.PlayMethod);
            BindNullable(statement, "@VideoCodec", session.VideoCodec);
            BindNullable(statement, "@AudioCodec", session.AudioCodec);
            BindNullable(statement, "@Bitrate", session.Bitrate);
            BindNullable(statement, "@TranscodeReasonsJson", session.TranscodeReasonsJson);
            statement.Bind("@PositionTicks", session.PositionTicks);
            BindNullable(statement, "@DurationTicks", session.DurationTicks);
            statement.Bind("@StartedAt", startedAt);
            statement.Bind("@EndedAt", session.EndedAt);
            BindNullable(statement, "@ClientName", session.ClientName);
            BindNullable(statement, "@DeviceName", session.DeviceName);
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
    /// number of days recomputed.
    ///
    /// The write lock is taken per DAY, not for the whole rebuild: each day's
    /// delete+re-derive is one atomic transaction, but a long rebuild no longer
    /// starves settings sync for the whole window. A session landing for a day
    /// after its chunk ran is picked up by the next rebuild (the cutoff keeps
    /// the day eligible), so the fold stays eventually exact.
    /// </summary>
    public int RecomputePlaybackRollups(long cutoffMs)
    {
        var dayExpr = $"date({PlaybackSessionsTable}.EndedAt / 1000, 'unixepoch')";
        List<string> days;
        using (_lock.Read())
        using (var connection = CreateConnection())
        using (var select = connection.Prepare(
                   $"select distinct {dayExpr} from {PlaybackSessionsTable} where {PlaybackSessionsTable}.EndedAt >= @CutoffMs order by 1"))
        {
            select.Bind("@CutoffMs", cutoffMs);
            days = select.Select(row => row.GetString(0)).ToList();
        }

        foreach (var day in days)
        {
            using (_lock.Write())
            using (var connection = CreateConnection())
            using (var transaction = connection.BeginTransaction())
            {
                using (var delete = connection.Prepare($"delete from {PlaybackRollupsTable} where Day = @Day"))
                {
                    delete.Bind("@Day", day);
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
                              where {dayExpr} = @Day
                              group by {dayExpr}, UserId"))
                {
                    insert.Bind("@Day", day);
                    insert.ExecuteNonQuery();
                }

                transaction.Commit();
            }
        }

        return days.Count;
    }

    /// <summary>
    /// Raw session rows (and only those) ending before <paramref name="cutoffMs"/>
    /// (unix ms) are deleted; rollups are never touched. Batched (see
    /// <c>BatchedDelete</c>).
    /// </summary>
    public int PrunePlaybackSessions(long cutoffMs)
    {
        using (_lock.Write())
        using (var connection = CreateConnection())
        {
            return BatchedDelete(connection, PlaybackSessionsTable, "EndedAt < @Cutoff", cutoffMs);
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

    /// <summary>
    /// Per-user daily rollups within the UTC day range (inclusive). Null
    /// <paramref name="userId"/> = all users (admin surface); a user id scopes
    /// the fold to that user's rows — mirroring
    /// <see cref="GetTopPlaybackItems"/> and <see cref="CountDistinctPlaybackItems"/>.
    /// </summary>
    public IReadOnlyList<PlaybackRollupRow> GetPlaybackRollups(string fromDayUtc, string toDayUtc, string? userId = null)
    {
        var filters = new List<string>(2) { "Day >= @FromDay", "Day <= @ToDay" };
        if (userId is not null)
        {
            filters.Add("UserId = @UserId");
        }

        using (_lock.Read())
        using (var connection = CreateConnection())
        using (var statement = connection.Prepare(
                   $@"select Day, UserId, ItemsPlayed, PlaySeconds, TranscodeSeconds, DirectCount, TranscodeCount
                      from {PlaybackRollupsTable}
                      where {string.Join(" and ", filters)}
                      order by Day, UserId"))
        {
            statement.Bind("@FromDay", fromDayUtc);
            statement.Bind("@ToDay", toDayUtc);
            if (userId is not null)
            {
                statement.Bind("@UserId", userId);
            }

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

}
