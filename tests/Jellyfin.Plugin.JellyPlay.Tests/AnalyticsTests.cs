using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyPlay.Api;
using Jellyfin.Plugin.JellyPlay.Configuration;
using Jellyfin.Plugin.JellyPlay.Services.Analytics;
using Jellyfin.Plugin.JellyPlay.Storage;
using Jellyfin.Plugin.JellyPlay.Storage.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.JellyPlay.Tests;

// ---------------------------------------------------------------------------
// Schema v5: playback_sessions + playback_rollups
// ---------------------------------------------------------------------------

/// <summary>The v4 → v5 migration creates the analytics tables without touching stored data, idempotently.</summary>
public sealed class AnalyticsMigrationTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "jellyplay-analytics-mig-" + Guid.NewGuid().ToString("N"));

    public AnalyticsMigrationTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string DbPath => Path.Combine(_tempDir, "plugins", "JellyPlay", "jellyplay_plugin.db");

    private static int RawUserVersion(string dbPath)
    {
        using var connection = new SqliteConnection($"Filename={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "pragma user_version";
        return Convert.ToInt32(command.ExecuteScalar()!);
    }

    private static bool ObjectExists(string dbPath, string type, string name)
    {
        using var connection = new SqliteConnection($"Filename={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"select count(*) from sqlite_master where type = '{type}' and name = '{name}'";
        return Convert.ToInt64(command.ExecuteScalar()!) > 0;
    }

    [Fact]
    public void V4Database_MigratesToV5_PreservesData_AndIsIdempotent()
    {
        // Build at the current version, store data, then roll the file back to
        // a v4 state: analytics objects dropped, user_version stamped 4.
        using (var first = new JellyPlayDatabase(_tempDir))
        {
            first.UpsertSettings(
                "user1",
                JellyPlayDatabase.BaseProfile,
                new[] { new SettingWrite("ui", "theme", 1, 100, "d1", System.Text.Encoding.UTF8.GetBytes("\"dark\"")) },
                new JellyPlayDatabase.Quotas(1024, 4096, 10));
            first.InsertSyncHistory("user1", "d1", "push", 1, 0, 5, null, 1234);
        }

        using (var raw = new SqliteConnection($"Filename={DbPath}"))
        {
            raw.Open();
            using var downgrade = raw.CreateCommand();
            downgrade.CommandText =
                "drop table if exists playback_sessions; drop table if exists playback_rollups; " +
                "drop index if exists idx_playback_sessions_user_started; drop index if exists idx_playback_sessions_started; " +
                "drop index if exists ux_playback_sessions_dedupe; drop index if exists idx_playback_rollups_day; " +
                "alter table sync_history drop column FromSeq; alter table sync_history drop column ToSeq; " +
                "pragma user_version = 4;";
            downgrade.ExecuteNonQuery();
        }

        Assert.False(ObjectExists(DbPath, "table", "playback_sessions"));

        using (var second = new JellyPlayDatabase(_tempDir))
        {
            Assert.Equal(JellyPlayDatabase.CurrentSchemaVersion, second.UserVersion);
            Assert.Equal(JellyPlayDatabase.CurrentSchemaVersion, RawUserVersion(DbPath));
            Assert.True(ObjectExists(DbPath, "table", "playback_sessions"));
            Assert.True(ObjectExists(DbPath, "table", "playback_rollups"));
            Assert.True(ObjectExists(DbPath, "index", "ux_playback_sessions_dedupe"));

            // The v4 data survived the migration untouched.
            var rows = second.GetSettings("user1", "");
            Assert.Single(rows);
            Assert.Equal("\"dark\"", System.Text.Encoding.UTF8.GetString(rows.Single(row => row.Key == "theme").Value));
            Assert.Single(second.GetSyncHistory("user1", 0, 50));

            // And the new tables are immediately usable.
            var id = second.InsertPlaybackSession(Row("u1", "item1", startedAt: 1000, endedAt: 90_000));
            Assert.True(id > 0);
            Assert.Single(second.GetPlaybackSessions(null, 0, 50));
        }

        // Re-open: nothing left to migrate.
        using (var third = new JellyPlayDatabase(_tempDir))
        {
            Assert.Equal(JellyPlayDatabase.CurrentSchemaVersion, third.UserVersion);
            Assert.True(third.CheckIntegrity().IntegrityOk);
        }
    }

    [Fact]
    public void FreshDatabase_CreatesAnalyticsTables_AtCurrentVersion()
    {
        using var db = new JellyPlayDatabase(_tempDir);
        Assert.Equal(JellyPlayDatabase.CurrentSchemaVersion, db.UserVersion);
        Assert.True(ObjectExists(DbPath, "table", "playback_sessions"));
        Assert.True(ObjectExists(DbPath, "table", "playback_rollups"));
        Assert.Empty(db.GetPlaybackSessions(null, 0, 50));
        Assert.Empty(db.GetPlaybackRollups("2000-01-01", "2999-01-01"));
    }

    internal static PlaybackSessionRow Row(
        string userId,
        string itemId,
        long startedAt,
        long endedAt,
        string playMethod = "DirectPlay",
        string itemName = "Item",
        string itemType = "Movie",
        string? seriesName = null,
        long positionTicks = 0,
        long? durationTicks = null,
        string? videoCodec = null,
        string? audioCodec = null,
        long? bitrate = null,
        string? reasonsJson = null)
        => new(
            Id: 0,
            UserId: userId,
            ItemId: itemId,
            ItemName: itemName,
            ItemType: itemType,
            SeriesName: seriesName,
            PlayMethod: playMethod,
            VideoCodec: videoCodec,
            AudioCodec: audioCodec,
            Bitrate: bitrate,
            TranscodeReasonsJson: reasonsJson,
            PositionTicks: positionTicks,
            DurationTicks: durationTicks,
            StartedAt: startedAt,
            EndedAt: endedAt,
            ClientName: "Web",
            DeviceName: "Chrome");
}

// ---------------------------------------------------------------------------
// Pure rules: anti-noise gate, dedup bucket, staleness
// ---------------------------------------------------------------------------

public sealed class PlaybackRecordingRulesTests
{
    [Theory]
    [InlineData(29, 60, false)] // wall just under the floor
    [InlineData(30, 60, true)]  // both exactly at the floors
    [InlineData(600, 59, false)] // position just under the floor
    [InlineData(600, 60, true)]
    [InlineData(10, 3600, false)] // long wall, scrubbed away
    [InlineData(3600, 3600, true)]
    public void IsRecordable_RequiresWallAndPositionFloors(long wallSeconds, long positionSeconds, bool expected)
        => Assert.Equal(expected, PlaybackRecordingRules.IsRecordable(wallSeconds, positionSeconds));

    [Fact]
    public void MinuteBucket_TruncatesToTheMinute()
    {
        Assert.Equal(0, PlaybackRecordingRules.MinuteBucket(0));
        Assert.Equal(60_000, PlaybackRecordingRules.MinuteBucket(60_000));
        Assert.Equal(60_000, PlaybackRecordingRules.MinuteBucket(119_999));
        Assert.Equal(60_000, PlaybackRecordingRules.MinuteBucket(119_001));
        Assert.Equal(120_000, PlaybackRecordingRules.MinuteBucket(120_000));
        // Same bucket for any two instants within one minute, different across.
        Assert.Equal(PlaybackRecordingRules.MinuteBucket(3_600_999), PlaybackRecordingRules.MinuteBucket(3_600_001));
        Assert.NotEqual(PlaybackRecordingRules.MinuteBucket(3_600_999), PlaybackRecordingRules.MinuteBucket(3_660_000));
    }

    [Fact]
    public void IsStale_AfterTheGraceWindowOnly()
    {
        Assert.False(PlaybackRecordingRules.IsStale(0, PlaybackRecordingRules.StaleSessionGraceMs));
        Assert.True(PlaybackRecordingRules.IsStale(0, PlaybackRecordingRules.StaleSessionGraceMs + 1));
    }
}

// ---------------------------------------------------------------------------
// Recording: insert dedup + event-driven rows
// ---------------------------------------------------------------------------

public sealed class AnalyticsRecordingTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "jellyplay-analytics-rec-" + Guid.NewGuid().ToString("N"));
    private readonly JellyPlayDatabase _db;

    public AnalyticsRecordingTests()
    {
        Directory.CreateDirectory(_tempDir);
        _db = new JellyPlayDatabase(_tempDir);
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private AnalyticsService Service(Func<AnalyticsConfig>? config = null, Func<DateTimeOffset>? clock = null)
        => new(
            _db,
            config ?? (() => new AnalyticsConfig { Enabled = true }),
            NullLogger<AnalyticsService>.Instance,
            clock ?? (() => FixedNow));

    private static PlaybackStopEventArgs Stop(BaseItem item, Guid userId, long? positionTicks, string playSessionId = "ps-1", string? playMethod = null)
        => new()
        {
            Item = item,
            Session = new SessionInfo(null!, null!)
            {
                UserId = userId,
                Client = "JellyPlay Web",
                DeviceName = "Chrome",
                PlayState = playMethod is null ? null : new PlayerStateInfo { PlayMethod = Enum.Parse<PlayMethod>(playMethod) },
            },
            PlaybackPositionTicks = positionTicks,
            PlaySessionId = playSessionId,
        };

    private static PlaybackProgressEventArgs Progress(BaseItem item, Guid userId, long? positionTicks, string playSessionId)
        => new()
        {
            Item = item,
            Session = new SessionInfo(null!, null!) { UserId = userId, Client = "JellyPlay Web", DeviceName = "Chrome" },
            PlaybackPositionTicks = positionTicks,
            PlaySessionId = playSessionId,
        };

    private static Movie Movie(string name = "Movie Night", long? runTimeTicks = null)
        => new() { Id = Guid.NewGuid(), Name = name, RunTimeTicks = runTimeTicks };

    private static long MinutesAgo(int minutes) => FixedNow.ToUnixTimeMilliseconds() - minutes * 60_000;

    [Fact]
    public void Insert_DedupesOnUserItemStartMinute_AllowsOtherBuckets()
    {
        var bucket = 1_800_000_000_000; // a minute-aligned instant
        var first = _db.InsertPlaybackSession(AnalyticsMigrationTests.Row("u1", "i1", bucket, bucket + 600_000));
        var duplicate = _db.InsertPlaybackSession(AnalyticsMigrationTests.Row("u1", "i1", bucket, bucket + 900_000));
        var otherMinute = _db.InsertPlaybackSession(AnalyticsMigrationTests.Row("u1", "i1", bucket + 60_000, bucket + 660_000));
        var otherUser = _db.InsertPlaybackSession(AnalyticsMigrationTests.Row("u2", "i1", bucket, bucket + 600_000));
        var otherItem = _db.InsertPlaybackSession(AnalyticsMigrationTests.Row("u1", "i2", bucket, bucket + 600_000));

        Assert.True(first > 0);
        Assert.Equal(0, duplicate); // the (user, item, start-minute) dedup is total
        Assert.True(otherMinute > 0);
        Assert.True(otherUser > 0);
        Assert.True(otherItem > 0);
        Assert.Equal(4, _db.GetPlaybackSessions(null, 0, 50).Count);
    }

    [Fact]
    public async Task Stop_RecordsRow_WithTranscodeDetailAndEstimatedStart()
    {
        var runTime = 40 * TimeSpan.TicksPerMinute;
        var service = Service();

        service.OnPlaybackStopped(Stop(
            Movie(runTimeTicks: runTime),
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            positionTicks: 30 * TimeSpan.TicksPerMinute,
            playMethod: "Transcode"));
        await service.FlushPendingWritesAsync(); // rows persist on the background writer

        var row = Assert.Single(_db.GetPlaybackSessions(null, 0, 50));
        Assert.Equal("11111111-1111-1111-1111-111111111111", row.UserId);
        Assert.Equal("Movie Night", row.ItemName);
        Assert.Equal("Movie", row.ItemType);
        Assert.Equal("Transcode", row.PlayMethod);
        // No tracked start: estimated from how far the play got (30 of 40 minutes).
        Assert.Equal(FixedNow.ToUnixTimeMilliseconds() - 30 * 60_000L, row.StartedAt);
        Assert.Equal(FixedNow.ToUnixTimeMilliseconds(), row.EndedAt);
        Assert.Equal(30 * TimeSpan.TicksPerMinute, row.PositionTicks);
        Assert.Equal(runTime, row.DurationTicks);
        Assert.Equal("JellyPlay Web", row.ClientName);
        Assert.Equal("Chrome", row.DeviceName);
    }

    [Fact]
    public async Task Stop_TranscodingSession_CarriesCodecsAndNamedReasons()
    {
        var item = Movie(runTimeTicks: 40 * TimeSpan.TicksPerMinute);
        var args = Stop(item, Guid.NewGuid(), positionTicks: 10 * TimeSpan.TicksPerMinute, playMethod: "Transcode");
        args.Session!.TranscodingInfo = new TranscodingInfo
        {
            IsVideoDirect = false,
            IsAudioDirect = true,
            VideoCodec = "h264",
            AudioCodec = "aac",
            Bitrate = 4_500_000,
            TranscodeReasons = TranscodeReason.VideoCodecNotSupported | TranscodeReason.ContainerNotSupported,
        };

        var service = Service();
        service.OnPlaybackStopped(args);
        await service.FlushPendingWritesAsync();

        var row = Assert.Single(_db.GetPlaybackSessions(null, 0, 50));
        Assert.Equal("h264", row.VideoCodec);
        Assert.Null(row.AudioCodec); // audio direct → no codec, exactly like the monitor fold
        Assert.Equal(4_500_000, row.Bitrate);
        Assert.NotNull(row.TranscodeReasonsJson);
        Assert.Equal(
            new[] { "ContainerNotSupported", "VideoCodecNotSupported" },
            System.Text.Json.JsonSerializer.Deserialize<string[]>(row.TranscodeReasonsJson!));
    }

    [Fact]
    public async Task Stop_Episode_RecordsItemTypeAndSeriesName()
    {
        var episode = new Episode { Id = Guid.NewGuid(), Name = "Pilot", SeriesName = "Brennan & Booth" };
        var service = Service();
        service.OnPlaybackStopped(Stop(episode, Guid.NewGuid(), positionTicks: 20 * TimeSpan.TicksPerMinute));
        await service.FlushPendingWritesAsync();

        var row = Assert.Single(_db.GetPlaybackSessions(null, 0, 50));
        Assert.Equal("Episode", row.ItemType);
        Assert.Equal("Brennan & Booth", row.SeriesName);
    }

    [Fact]
    public async Task Stop_SkipsSessionsWithoutUsableItems()
    {
        var service = Service();
        service.OnPlaybackStopped(new PlaybackStopEventArgs { Item = null, Session = new SessionInfo(null!, null!) { UserId = Guid.NewGuid() } });
        service.OnPlaybackStopped(Stop(new Movie { Id = Guid.Empty }, Guid.NewGuid(), positionTicks: 30 * TimeSpan.TicksPerMinute));
        await service.FlushPendingWritesAsync();

        Assert.Empty(_db.GetPlaybackSessions(null, 0, 50));
    }

    [Theory]
    [InlineData(59 * TimeSpan.TicksPerSecond, false)] // scrubbed away
    [InlineData(60 * TimeSpan.TicksPerSecond, true)]
    public async Task Stop_AppliesThePositionFloor(long positionTicks, bool expected)
    {
        var service = Service();
        service.OnPlaybackStopped(Stop(Movie(runTimeTicks: 100 * TimeSpan.TicksPerMinute), Guid.NewGuid(), positionTicks));
        await service.FlushPendingWritesAsync();

        Assert.Equal(expected, _db.GetPlaybackSessions(null, 0, 50).Count == 1);
    }

    [Fact]
    public async Task Stop_ShortWallTime_IsNoise()
    {
        // No tracked start: wall ≈ position, so a tiny position on a stop
        // without progress never records (wall and position floors coincide).
        var service = Service();
        service.OnPlaybackStopped(Stop(Movie(runTimeTicks: 40 * TimeSpan.TicksPerMinute), Guid.NewGuid(), positionTicks: 5 * TimeSpan.TicksPerSecond));
        await service.FlushPendingWritesAsync();
        Assert.Empty(_db.GetPlaybackSessions(null, 0, 50));
    }

    [Fact]
    public async Task ProgressThenStop_RecordsWithTrackedStart_AndDedupesReplays()
    {
        var clock = new FakeClock(FixedNow.AddMinutes(-30));
        var service = Service(clock: () => clock.Now);
        var item = Movie(runTimeTicks: 40 * TimeSpan.TicksPerMinute);
        var userId = Guid.NewGuid();

        service.OnPlaybackProgress(Progress(item, userId, positionTicks: 1 * TimeSpan.TicksPerMinute, playSessionId: "ps-1"));
        clock.Advance(TimeSpan.FromMinutes(29));
        service.OnPlaybackProgress(Progress(item, userId, positionTicks: 30 * TimeSpan.TicksPerMinute, playSessionId: "ps-1"));
        clock.Advance(TimeSpan.FromMinutes(1));
        service.OnPlaybackStopped(Stop(item, userId, positionTicks: 31 * TimeSpan.TicksPerMinute, playSessionId: "ps-1"));
        await service.FlushPendingWritesAsync();

        var row = Assert.Single(_db.GetPlaybackSessions(null, 0, 50));
        Assert.Equal(FixedNow.AddMinutes(-30).ToUnixTimeMilliseconds(), row.StartedAt);
        Assert.Equal(FixedNow.ToUnixTimeMilliseconds(), row.EndedAt);
        Assert.Equal(31 * TimeSpan.TicksPerMinute, row.PositionTicks); // the stop position wins

        // The same play re-fired (progress + stop replay in the same minute) → deduped.
        service.OnPlaybackProgress(Progress(item, userId, positionTicks: 31 * TimeSpan.TicksPerMinute, playSessionId: "ps-1"));
        clock.Advance(TimeSpan.FromSeconds(20));
        service.OnPlaybackStopped(Stop(item, userId, positionTicks: 31 * TimeSpan.TicksPerMinute, playSessionId: "ps-1"));
        await service.FlushPendingWritesAsync();
        Assert.Single(_db.GetPlaybackSessions(null, 0, 50));
    }

    [Fact]
    public async Task ProgressToAnotherItem_ClosesTheAbandonedSession()
    {
        var clock = new FakeClock(FixedNow.AddMinutes(-30));
        var service = Service(clock: () => clock.Now);
        var userId = Guid.NewGuid();
        var first = Movie("First", 40 * TimeSpan.TicksPerMinute);
        var second = Movie("Second", 40 * TimeSpan.TicksPerMinute);

        service.OnPlaybackProgress(Progress(first, userId, positionTicks: 1 * TimeSpan.TicksPerMinute, playSessionId: "ps-1"));
        clock.Advance(TimeSpan.FromMinutes(10));
        // The host moved on without a stop event: the first session ends now.
        service.OnPlaybackProgress(Progress(second, userId, positionTicks: 1 * TimeSpan.TicksPerMinute, playSessionId: "ps-2"));
        await service.FlushPendingWritesAsync();

        var row = _db.GetPlaybackSessions(null, 0, 50).Single(row => row.ItemName == "First");
        Assert.Equal(FixedNow.AddMinutes(-20).ToUnixTimeMilliseconds(), row.EndedAt);
        Assert.Equal(FixedNow.AddMinutes(-30).ToUnixTimeMilliseconds(), row.StartedAt);
    }

    [Fact]
    public async Task StaleSessions_CloseAtTheirLastSeenTime()
    {
        var clock = new FakeClock(FixedNow.AddMinutes(-30));
        var service = Service(clock: () => clock.Now);
        var userId = Guid.NewGuid();
        var item = Movie(runTimeTicks: 40 * TimeSpan.TicksPerMinute);

        // Two observed progress events (so the closed session has real wall time),
        // then silence — the sweep fires on the next unrelated progress event.
        service.OnPlaybackProgress(Progress(item, userId, positionTicks: 2 * TimeSpan.TicksPerMinute, playSessionId: "ps-1"));
        clock.Advance(TimeSpan.FromMinutes(1));
        service.OnPlaybackProgress(Progress(item, userId, positionTicks: 3 * TimeSpan.TicksPerMinute, playSessionId: "ps-1"));
        clock.Advance(TimeSpan.FromMinutes(11));
        service.OnPlaybackProgress(Progress(item, Guid.NewGuid(), positionTicks: 2 * TimeSpan.TicksPerMinute, playSessionId: "ps-2"));
        await service.FlushPendingWritesAsync();

        var row = _db.GetPlaybackSessions(null, 0, 50).Single(row => row.UserId == userId.ToString());
        Assert.Equal(FixedNow.AddMinutes(-29).ToUnixTimeMilliseconds(), row.EndedAt); // last seen, not sweep time
        Assert.Equal(FixedNow.AddMinutes(-30).ToUnixTimeMilliseconds(), row.StartedAt);
        Assert.Equal(3 * TimeSpan.TicksPerMinute, row.PositionTicks);
    }

    [Fact]
    public async Task DisabledConfig_RecordsNothing()
    {
        var service = Service(config: () => new AnalyticsConfig { Enabled = false });
        service.OnPlaybackStopped(Stop(Movie(runTimeTicks: 40 * TimeSpan.TicksPerMinute), Guid.NewGuid(), positionTicks: 30 * TimeSpan.TicksPerMinute));
        service.OnPlaybackProgress(Progress(Movie(), Guid.NewGuid(), positionTicks: 30 * TimeSpan.TicksPerMinute, playSessionId: "ps-x"));
        await service.FlushPendingWritesAsync();
        Assert.Empty(_db.GetPlaybackSessions(null, 0, 50));
    }

    [Fact]
    public void BrokenStore_NeverThrowsIntoTheEventPipeline()
    {
        // Simulate a broken analytics store: the event entry points must swallow.
        using (var raw = new SqliteConnection($"Filename={Path.Combine(_tempDir, "plugins", "JellyPlay", "jellyplay_plugin.db")};Pooling=False"))
        {
            raw.Open();
            using var drop = raw.CreateCommand();
            drop.CommandText = "drop table playback_sessions";
            drop.ExecuteNonQuery();
        }

        var service = Service();
        service.OnPlaybackStopped(Stop(Movie(runTimeTicks: 40 * TimeSpan.TicksPerMinute), Guid.NewGuid(), positionTicks: 30 * TimeSpan.TicksPerMinute));
        service.OnPlaybackProgress(Progress(Movie(), Guid.NewGuid(), positionTicks: 30 * TimeSpan.TicksPerMinute, playSessionId: "ps-x"));
    }

    private sealed class FakeClock(DateTimeOffset start)
    {
        public DateTimeOffset Now { get; private set; } = start;

        public void Advance(TimeSpan delta) => Now += delta;
    }
}

// ---------------------------------------------------------------------------
// Rollups, retention, disable purge
// ---------------------------------------------------------------------------

public sealed class AnalyticsMaintenanceTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "jellyplay-analytics-mnt-" + Guid.NewGuid().ToString("N"));
    private readonly JellyPlayDatabase _db;

    public AnalyticsMaintenanceTests()
    {
        Directory.CreateDirectory(_tempDir);
        _db = new JellyPlayDatabase(_tempDir);
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private AnalyticsService Service(AnalyticsConfig config)
        => new(_db, () => config, NullLogger<AnalyticsService>.Instance, () => FixedNow);

    private static long DayMs(int daysAgo) => FixedNow.ToUnixTimeMilliseconds() - daysAgo * 86_400_000L;

    private long Seed(string userId, string itemId, int endedDaysAgo, long wallSeconds, string playMethod = "DirectPlay")
        => _db.InsertPlaybackSession(AnalyticsMigrationTests.Row(
            userId,
            itemId,
            startedAt: PlaybackRecordingRules.MinuteBucket(DayMs(endedDaysAgo) - wallSeconds * 1000),
            endedAt: DayMs(endedDaysAgo),
            playMethod: playMethod));

    [Fact]
    public void RollupRecompute_AggregatesPerDayPerUser_AndIsIdempotent()
    {
        Seed("u1", "i1", 0, wallSeconds: 600);                        // today, direct
        Seed("u1", "i2", 0, wallSeconds: 300, playMethod: "Transcode"); // today, transcode
        Seed("u2", "i1", 1, wallSeconds: 1200);                       // yesterday
        Seed("u1", "i1", 100, wallSeconds: 60);                       // ancient

        var rolled = _db.RecomputePlaybackRollups(FixedNow.ToUnixTimeMilliseconds() - 3 * 86_400_000L);
        Assert.Equal(2, rolled); // today + yesterday (the ancient day is outside the cutoff)

        var today = _db.GetPlaybackRollups(DateTime.UtcNow.ToString("yyyy-MM-dd"), DateTime.UtcNow.ToString("yyyy-MM-dd"));
        // UTC day of "now": FixedNow is UTC noon → today.
        var rollups = _db.GetPlaybackRollups("2000-01-01", "2999-01-01");
        var byKey = rollups.ToDictionary(row => (row.Day, row.UserId));

        var todayKey = (Day: FixedNow.UtcDateTime.ToString("yyyy-MM-dd"), UserId: "u1");
        Assert.Equal(2, byKey[todayKey].ItemsPlayed);
        Assert.Equal(900, byKey[todayKey].PlaySeconds);
        Assert.Equal(300, byKey[todayKey].TranscodeSeconds);
        Assert.Equal(1, byKey[todayKey].DirectCount);
        Assert.Equal(1, byKey[todayKey].TranscodeCount);

        var yesterdayKey = (Day: FixedNow.UtcDateTime.AddDays(-1).ToString("yyyy-MM-dd"), UserId: "u2");
        Assert.Equal(1, byKey[yesterdayKey].ItemsPlayed);
        Assert.Equal(1200, byKey[yesterdayKey].PlaySeconds);
        Assert.Equal(0, byKey[yesterdayKey].TranscodeSeconds);

        // Recompute is idempotent: same numbers, no duplicates.
        _db.RecomputePlaybackRollups(FixedNow.ToUnixTimeMilliseconds());
        var again = _db.GetPlaybackRollups("2000-01-01", "2999-01-01");
        Assert.Equal(rollups, again);
    }

    [Fact]
    public void RollupRecompute_LateArrivalSessions_ArePickedUp()
    {
        Seed("u1", "i1", 0, wallSeconds: 600);
        _db.RecomputePlaybackRollups(FixedNow.ToUnixTimeMilliseconds());

        // A late-arriving session for a day already rolled (ended before the last recompute cutoff).
        Seed("u2", "i9", 0, wallSeconds: 100);
        _db.RecomputePlaybackRollups(FixedNow.ToUnixTimeMilliseconds() - 3 * 86_400_000L);

        var todayKey = (Day: FixedNow.UtcDateTime.ToString("yyyy-MM-dd"), UserId: "u2");
        var row = _db.GetPlaybackRollups("2000-01-01", "2999-01-01").Single(candidate => candidate.UserId == "u2");
        Assert.Equal(todayKey.Day, row.Day);
        // StartedAt is stored minute-bucketed (the dedup key), so a session
        // starting mid-minute plays up to 60s longer by EndedAt-StartedAt.
        Assert.Equal(120, row.PlaySeconds);
    }

    [Fact]
    public void RunMaintenance_PrunesRawPastRetention_KeepsRollups()
    {
        var ancient = Seed("u1", "i1", 200, wallSeconds: 60);
        var recent = Seed("u1", "i2", 1, wallSeconds: 600);
        _db.RecomputePlaybackRollups(0);

        var result = Service(new AnalyticsConfig { Enabled = true, RawRetentionDays = 90 }).RunMaintenance();

        Assert.False(result.DisabledPurged);
        var remaining = _db.GetPlaybackSessions(null, 0, 50).ToList();
        Assert.Single(remaining);
        Assert.Equal(recent, remaining[0].Id);
        Assert.NotEqual(ancient, remaining[0].Id);
        // The rollup for the pruned ancient day survives (rollups are forever).
        Assert.Equal(2, _db.GetPlaybackRollups("2000-01-01", "2999-01-01").Count);
    }

    [Fact]
    public void RunMaintenance_Disabled_PurgesAllAnalyticsData()
    {
        Seed("u1", "i1", 1, wallSeconds: 600);
        _db.RecomputePlaybackRollups(0);
        Assert.NotEmpty(_db.GetPlaybackSessions(null, 0, 50));
        Assert.NotEmpty(_db.GetPlaybackRollups("2000-01-01", "2999-01-01"));

        var result = Service(new AnalyticsConfig { Enabled = false }).RunMaintenance();

        Assert.True(result.DisabledPurged);
        Assert.Empty(_db.GetPlaybackSessions(null, 0, 50));
        Assert.Empty(_db.GetPlaybackRollups("2000-01-01", "2999-01-01"));

        // Re-enabling does not resurrect anything; recording works again.
        Seed("u1", "i1", 0, wallSeconds: 600);
        Assert.Single(_db.GetPlaybackSessions(null, 0, 50));
    }
}

// ---------------------------------------------------------------------------
// Reporting: overview aggregation + session query
// ---------------------------------------------------------------------------

public sealed class AnalyticsReportingTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Today = FixedNow.UtcDateTime.ToString("yyyy-MM-dd");
    private static readonly string Yesterday = FixedNow.UtcDateTime.AddDays(-1).ToString("yyyy-MM-dd");

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "jellyplay-analytics-rep-" + Guid.NewGuid().ToString("N"));
    private readonly JellyPlayDatabase _db;

    public AnalyticsReportingTests()
    {
        Directory.CreateDirectory(_tempDir);
        _db = new JellyPlayDatabase(_tempDir);
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private AnalyticsService Service(int retentionDays = 90)
        => new(
            _db,
            () => new AnalyticsConfig { Enabled = true, RawRetentionDays = retentionDays },
            NullLogger<AnalyticsService>.Instance,
            () => FixedNow);

    private static long DayMs(int daysAgo) => FixedNow.ToUnixTimeMilliseconds() - daysAgo * 86_400_000L;

    private void Seed(string userId, string itemId, string itemName, int endedDaysAgo, long wallSeconds, string playMethod = "DirectPlay", string itemType = "Movie")
        => _db.InsertPlaybackSession(AnalyticsMigrationTests.Row(
            userId,
            itemId,
            startedAt: PlaybackRecordingRules.MinuteBucket(DayMs(endedDaysAgo) - wallSeconds * 1000),
            endedAt: DayMs(endedDaysAgo),
            playMethod: playMethod,
            itemName: itemName,
            itemType: itemType));

    private static readonly Guid AliceGuid = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid BobGuid = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly string Alice = AliceGuid.ToString();
    private static readonly string Bob = BobGuid.ToString();

    private AnalyticsOverviewResponse Overview(int days = 30)
        => Service().GetOverview(days, guid => guid == AliceGuid ? "Alice" : null);

    [Fact]
    public void Overview_FoldsTotalsPerDayPerUserTopItems()
    {
        // Alice: 3 plays across 2 days (one transcode), Bob = resolved name too.
        Seed(Alice, "i1", "Movie One", 0, wallSeconds: 600);
        Seed(Alice, "i1", "Movie One", 0, wallSeconds: 300, playMethod: "Transcode");
        Seed(Alice, "i2", "Movie Two", 1, wallSeconds: 100);
        Seed(Bob, "i3", "Movie Three", 0, wallSeconds: 60);
        _db.RecomputePlaybackRollups(0);

        var overview = Overview(30);

        Assert.Equal(30, overview.Days);
        Assert.Equal(4, overview.Totals.Plays);
        Assert.Equal(1080, overview.Totals.PlaySeconds);
        Assert.Equal(300, overview.Totals.TranscodeSeconds);
        Assert.Equal(2, overview.Totals.UniqueUsers);
        Assert.Equal(3, overview.Totals.UniqueItems);

        Assert.Equal(2, overview.PerDay.Count);
        Assert.Equal(Yesterday, overview.PerDay[0].Day);
        Assert.Equal(1, overview.PerDay[0].Plays);
        Assert.Equal(120, overview.PerDay[0].PlaySeconds); // minute-bucketed StartedAt stretches a 100s session to 120s
        Assert.Equal(Today, overview.PerDay[1].Day);
        Assert.Equal(3, overview.PerDay[1].Plays);
        Assert.Equal(960, overview.PerDay[1].PlaySeconds);
        Assert.Equal(300, overview.PerDay[1].TranscodeSeconds);

        var users = overview.PerUser.ToDictionary(row => row.UserId);
        Assert.Equal(2, users.Count);
        Assert.Equal("Alice", users[Alice].UserName); // resolved via the manager
        Assert.Equal(3, users[Alice].Plays);
        Assert.Equal(1020, users[Alice].PlaySeconds); // minute-bucketed StartedAt stretch
        Assert.Equal(300, users[Alice].TranscodeSeconds);
        Assert.Equal(Bob, users[Bob].UserName); // unresolved guid falls back to the id
        Assert.Equal(1, users[Bob].Plays);

        var top = overview.TopItems;
        Assert.Equal("i1", top[0].ItemId); // 2 plays, tops the list
        Assert.Equal("Movie One", top[0].ItemName);
        Assert.Equal(2, top[0].Plays);
        Assert.Equal(900, top[0].PlaySeconds);
        Assert.Equal(3, top.Count); // deterministic, capped at MaxTopItems
    }

    [Fact]
    public void Overview_DaysClampedTo1To365()
    {
        Assert.Equal(30, Overview(days: 0).Days);
        Assert.Equal(1, Overview(days: -5).Days);
        Assert.Equal(1, Overview(days: 1).Days);
        Assert.Equal(365, Overview(days: 5000).Days);
    }

    [Fact]
    public void Overview_TopItemsDegradeWhenRawIsPruned()
    {
        // Rollups are retained forever but raw rows are pruned at retention:
        // perDay/perUser still aggregate from rollups; topItems/uniqueItems
        // (raw-derived) degrade to empty/0 for the pruned days.
        Seed(Alice, "i1", "Movie One", endedDaysAgo: 100, wallSeconds: 600);
        _db.RecomputePlaybackRollups(0);

        Service(retentionDays: 90).RunMaintenance();
        Assert.Empty(_db.GetPlaybackSessions(null, 0, 50)); // raw is gone

        var overview = Service(retentionDays: 90).GetOverview(365, guid => guid == AliceGuid ? "Alice" : null);

        Assert.Equal(1, overview.Totals.Plays); // from the rollup
        Assert.Single(overview.PerDay);
        Assert.Single(overview.PerUser);
        Assert.Equal("Alice", overview.PerUser[0].UserName);
        Assert.Empty(overview.TopItems);
        Assert.Equal(0, overview.Totals.UniqueItems);
    }

    [Fact]
    public void Overview_EmptyDatabase_YieldsEmptyShell()
    {
        var overview = Overview();
        Assert.Equal(0, overview.Totals.Plays);
        Assert.Equal(0, overview.Totals.UniqueUsers);
        Assert.Equal(0, overview.Totals.UniqueItems);
        Assert.Empty(overview.PerDay);
        Assert.Empty(overview.PerUser);
        Assert.Empty(overview.TopItems);
    }

    [Fact]
    public void Sessions_NewestFirst_WithUserAndSinceFilters()
    {
        var ids = new List<long>();
        for (var index = 0; index < 5; index++)
        {
            ids.Add(_db.InsertPlaybackSession(AnalyticsMigrationTests.Row(
                "u1",
                $"i{index}",
                startedAt: 1000 + index,
                endedAt: 2000 + index)));
        }

        var otherUserId = _db.InsertPlaybackSession(AnalyticsMigrationTests.Row("u2", "ix", startedAt: 5000, endedAt: 6000));

        // Newest first, limit respected (across users when unfiltered).
        var page = Service().GetSessions(null, null, 3);
        Assert.Equal(new[] { otherUserId, ids[4], ids[3] }, page.Sessions.Select(row => row.Id).ToList());

        // since filters to sessions ending after the timestamp (u1 ended 2002+ and u2).
        var since = Service().GetSessions(null, 2001, 50);
        Assert.Equal(new[] { otherUserId, ids[4], ids[3], ids[2] }, since.Sessions.Select(row => row.Id).ToList());

        // User isolation.
        var u2 = Service().GetSessions("u2", null, 50);
        Assert.Single(u2.Sessions);
        Assert.Equal("u2", u2.Sessions[0].UserId);

        // Dto projection carries the named reasons (malformed JSON degrades to null).
        var withReasons = _db.InsertPlaybackSession(AnalyticsMigrationTests.Row(
            "u1", "ir", startedAt: 100, endedAt: 200,
            playMethod: "Transcode",
            videoCodec: "h264",
            reasonsJson: "[\"ContainerNotSupported\"]"));
        var dto = Service().GetSessions(null, null, 50).Sessions.Single(row => row.Id == withReasons);
        Assert.Equal(new[] { "ContainerNotSupported" }, dto.TranscodeReasons);
        Assert.Equal("h264", dto.VideoCodec);

        _db.InsertPlaybackSession(AnalyticsMigrationTests.Row("u1", "ib", startedAt: 100, endedAt: 200, reasonsJson: "not-json"));
        Assert.Null(Service().GetSessions(null, null, 50).Sessions.Single(row => row.ItemId == "ib").TranscodeReasons);
    }

    [Fact]
    public void Sessions_LimitClampsToBounds_AndZeroMeansDefault()
    {
        for (var index = 0; index < 210; index++)
        {
            _db.InsertPlaybackSession(AnalyticsMigrationTests.Row("u1", $"i{index}", startedAt: index, endedAt: index + 1));
        }

        Assert.Equal(AnalyticsService.MaxSessionLimit, Service().GetSessions(null, null, 100_000).Sessions.Count);
        Assert.Equal(1, Service().GetSessions(null, null, 1).Sessions.Count);
        Assert.Equal(1, Service().GetSessions(null, null, -5).Sessions.Count);
        Assert.Equal(AnalyticsService.DefaultSessionLimit, Service().GetSessions(null, null, 0).Sessions.Count);
    }
}

// ---------------------------------------------------------------------------
// Per-user reporting ("Your watching", GET jellyplay/analytics/me)
// ---------------------------------------------------------------------------

public sealed class AnalyticsMeTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Today = FixedNow.UtcDateTime.ToString("yyyy-MM-dd");
    private static readonly string Yesterday = FixedNow.UtcDateTime.AddDays(-1).ToString("yyyy-MM-dd");

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "jellyplay-analytics-me-" + Guid.NewGuid().ToString("N"));
    private readonly JellyPlayDatabase _db;

    public AnalyticsMeTests()
    {
        Directory.CreateDirectory(_tempDir);
        _db = new JellyPlayDatabase(_tempDir);
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private AnalyticsService Service()
        => new(
            _db,
            () => new AnalyticsConfig { Enabled = true },
            NullLogger<AnalyticsService>.Instance,
            () => FixedNow);

    private static long DayMs(int daysAgo) => FixedNow.ToUnixTimeMilliseconds() - daysAgo * 86_400_000L;

    private void Seed(string userId, string itemId, string itemName, int endedDaysAgo, long wallSeconds, string playMethod = "DirectPlay")
        => _db.InsertPlaybackSession(AnalyticsMigrationTests.Row(
            userId,
            itemId,
            startedAt: PlaybackRecordingRules.MinuteBucket(DayMs(endedDaysAgo) - wallSeconds * 1000),
            endedAt: DayMs(endedDaysAgo),
            playMethod: playMethod,
            itemName: itemName));

    private const string Alice = "11111111-1111-1111-1111-111111111111";
    private const string Bob = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public void Me_AggregatesOnlyTheCallerRows()
    {
        // Alice: 3 plays across 2 days (one transcode, two distinct items).
        Seed(Alice, "i1", "Movie One", 0, wallSeconds: 600);
        Seed(Alice, "i1", "Movie One", 0, wallSeconds: 300, playMethod: "Transcode");
        Seed(Alice, "i2", "Movie Two", 1, wallSeconds: 100);
        // Bob's rows must not leak into Alice's numbers.
        Seed(Bob, "i3", "Movie Three", 0, wallSeconds: 60);
        Seed(Bob, "i4", "Movie Four", 0, wallSeconds: 600);
        _db.RecomputePlaybackRollups(0);

        var me = Service().GetMyOverview(Alice, 30);

        Assert.Equal(30, me.Days);
        Assert.Equal(3, me.Totals.Plays);
        Assert.Equal(1020, me.Totals.PlaySeconds);
        Assert.Equal(300, me.Totals.TranscodeSeconds);
        Assert.Equal(2, me.Totals.UniqueItems); // i1 + i2, never Bob's

        Assert.Equal(2, me.PerDay.Count);
        Assert.Equal(Yesterday, me.PerDay[0].Day);
        Assert.Equal(1, me.PerDay[0].Plays);
        Assert.Equal(120, me.PerDay[0].PlaySeconds); // minute-bucketed StartedAt stretches a 100s session to 120s
        Assert.Equal(Today, me.PerDay[1].Day);
        Assert.Equal(2, me.PerDay[1].Plays);
        Assert.Equal(900, me.PerDay[1].PlaySeconds);
        Assert.Equal(300, me.PerDay[1].TranscodeSeconds);

        // Top items are Alice's only, still ranked plays-desc with the id tiebreak.
        Assert.Equal(2, me.TopItems.Count);
        Assert.Equal("i1", me.TopItems[0].ItemId);
        Assert.Equal(2, me.TopItems[0].Plays);
        Assert.Equal(900, me.TopItems[0].PlaySeconds);
        Assert.Equal("i2", me.TopItems[1].ItemId);

        // And Bob sees strictly his own rows (two plays of two distinct items).
        var bob = Service().GetMyOverview(Bob, 30);
        Assert.Equal(2, bob.Totals.Plays);
        Assert.Equal(2, bob.Totals.UniqueItems);
        Assert.All(bob.TopItems, row => Assert.NotEqual("i1", row.ItemId));
    }

    [Fact]
    public void Me_DaysClampedTo1To365_AndZeroMeansDefault()
    {
        Assert.Equal(30, Service().GetMyOverview(Alice, 0).Days);
        Assert.Equal(1, Service().GetMyOverview(Alice, -5).Days);
        Assert.Equal(1, Service().GetMyOverview(Alice, 1).Days);
        Assert.Equal(365, Service().GetMyOverview(Alice, 5000).Days);
    }

    [Fact]
    public void Me_EmptyDatabase_YieldsEmptyShell()
    {
        var me = Service().GetMyOverview(Alice, 30);

        Assert.Equal(0, me.Totals.Plays);
        Assert.Equal(0, me.Totals.PlaySeconds);
        Assert.Equal(0, me.Totals.TranscodeSeconds);
        Assert.Equal(0, me.Totals.UniqueItems);
        Assert.Empty(me.PerDay);
        Assert.Empty(me.TopItems);
    }

    [Fact]
    public void Me_TopItemsDegradeWhenRawIsPruned_WhileRollupsPersist()
    {
        Seed(Alice, "i1", "Movie One", endedDaysAgo: 100, wallSeconds: 600);
        _db.RecomputePlaybackRollups(0);
        Service().RunMaintenance(); // default retention prunes the 100-day-old raw row
        Assert.Empty(_db.GetPlaybackSessions(null, 0, 50));

        var me = Service().GetMyOverview(Alice, 365);

        Assert.Equal(1, me.Totals.Plays); // from the forever-retained rollup
        Assert.Single(me.PerDay);
        Assert.Empty(me.TopItems); // raw-derived, degraded not errored
        Assert.Equal(0, me.Totals.UniqueItems);
    }
}
