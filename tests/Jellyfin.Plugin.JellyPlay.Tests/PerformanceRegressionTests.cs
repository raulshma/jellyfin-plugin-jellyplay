using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Api;
using Jellyfin.Plugin.JellyPlay.Configuration;
using Jellyfin.Plugin.JellyPlay.Realtime;
using Jellyfin.Plugin.JellyPlay.Services.Analytics;
using Jellyfin.Plugin.JellyPlay.Services.Cache;
using Jellyfin.Plugin.JellyPlay.Services.Events;
using Jellyfin.Plugin.JellyPlay.Storage;
using Jellyfin.Plugin.JellyPlay.Storage.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.JellyPlay.Tests;

// ---------------------------------------------------------------------------
// Performance-regression pins: every behavior-preserving optimization in the
// perf pass gets an equivalence test here (new seam vs. old semantics), so a
// future regression of the fast path fails loudly instead of silently
// degrading.
// ---------------------------------------------------------------------------

/// <summary>Batched session inserts carry the same minute-bucket dedup as the row-by-row seam.</summary>
public sealed class BatchInsertTests : TempDatabaseFixture
{
    public BatchInsertTests()
        : base("perf-batch")
    {
    }

    [Fact]
    public void BatchInsert_MatchesRowByRow_DedupPositionsPreserved()
    {
        var bucket = 1_800_000_000_000; // a minute-aligned instant
        var ids = _db.InsertPlaybackSessionsBatch(new[]
        {
            AnalyticsMigrationTests.Row("u1", "i1", bucket, bucket + 600_000),
            AnalyticsMigrationTests.Row("u1", "i1", bucket, bucket + 900_000), // same minute → deduped
            AnalyticsMigrationTests.Row("u1", "i1", bucket + 60_000, bucket + 660_000),
            AnalyticsMigrationTests.Row("u2", "i1", bucket, bucket + 600_000),
        });

        Assert.Equal(4, ids.Count);
        Assert.True(ids[0] > 0);
        Assert.Equal(0, ids[1]);
        Assert.True(ids[2] > 0);
        Assert.True(ids[3] > 0);
        Assert.Equal(3, _db.GetPlaybackSessions(null, 0, 50).Count);
    }

    [Fact]
    public void BatchInsert_Empty_ReturnsEmpty()
    {
        Assert.Empty(_db.InsertPlaybackSessionsBatch(Array.Empty<PlaybackSessionRow>()));
    }

    [Fact]
    public void BatchInsert_ThenRowByRowDuplicate_Dedupes()
    {
        var bucket = 1_800_000_000_000;
        var ids = _db.InsertPlaybackSessionsBatch(new[] { AnalyticsMigrationTests.Row("u1", "i1", bucket, bucket + 60_000) });
        Assert.True(ids[0] > 0);
        Assert.Equal(0, _db.InsertPlaybackSession(AnalyticsMigrationTests.Row("u1", "i1", bucket, bucket + 90_000)));
    }
}

/// <summary>Rollup recompute bounds come from the data (max EndedAt in range), never the wall clock.</summary>
public sealed class RollupBoundTests : TempDatabaseFixture
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    public RollupBoundTests()
        : base("perf-rollup")
    {
    }

    private static long DayMs(int daysAgo) => FixedNow.ToUnixTimeMilliseconds() - daysAgo * 86_400_000L;

    private long Seed(string userId, string itemId, int endedDaysAgo)
        => _db.InsertPlaybackSession(AnalyticsMigrationTests.Row(
            userId,
            itemId,
            startedAt: PlaybackRecordingRules.MinuteBucket(DayMs(endedDaysAgo) - 600_000),
            endedAt: DayMs(endedDaysAgo)));

    [Fact]
    public void Recompute_EmptyTable_ReturnsZero()
    {
        Assert.Equal(0, _db.RecomputePlaybackRollups(0));
        Assert.Equal(0, _db.RecomputePlaybackRollups(FixedNow.ToUnixTimeMilliseconds()));
    }

    [Fact]
    public void Recompute_FutureCutoff_ReturnsZero()
    {
        Seed("u1", "i1", 1);
        Assert.Equal(0, _db.RecomputePlaybackRollups(FixedNow.ToUnixTimeMilliseconds() + 86_400_000L));
    }

    [Fact]
    public void Recompute_RecentWindow_CountsOnlyNonEmptyDays()
    {
        Seed("u1", "i1", 0);
        Seed("u2", "i1", 1);
        Seed("u1", "i9", 100); // outside the window: rolled only by the DISTINCT path, never enumerated

        Assert.Equal(2, _db.RecomputePlaybackRollups(FixedNow.ToUnixTimeMilliseconds() - 3 * 86_400_000L));
    }
}

/// <summary>The resolve composite answers exactly what the five separate reads did.</summary>
public sealed class ResolveBundleTests : TempDatabaseFixture
{
    public ResolveBundleTests()
        : base("perf-resolve")
    {
    }

    [Fact]
    public void GetResolveBundle_MatchesSeparateReads()
    {
        var quotas = new JellyPlayDatabase.Quotas(1024, 1 << 20, 100);
        _db.UpsertSettings("u1", "", new[] { new SettingWrite("ui", "theme", 1, 100, "d1", Encoding.UTF8.GetBytes("\"dark\"")) }, quotas);
        _db.UpsertSettings("u1", "tv", new[] { new SettingWrite("ui", "theme", 1, 200, "d1", Encoding.UTF8.GetBytes("\"tv\"")) }, quotas);
        _db.SetAdminDefaults("global", Encoding.UTF8.GetBytes("{\"a\":1}"), 1);
        _db.SetAdminDefaults("u1", Encoding.UTF8.GetBytes("{\"b\":2}"), 2);

        var bundle = _db.GetResolveBundle("u1", "tv");

        // SettingRow carries a byte[] payload (record equality is
        // reference-based), so compare element-wise.
        Assert.Equal(Shape(_db.GetSettings("u1", "")), Shape(bundle.BaseRows));
        Assert.Equal(Shape(_db.GetSettings("u1", "tv")), Shape(bundle.OverlayRows));
        Assert.Equal(Shape(_db.GetAdminDefaults("global")), Shape(bundle.GlobalDefaults));
        Assert.Equal(Shape(_db.GetAdminDefaults("u1")), Shape(bundle.UserDefaults));
        Assert.Equal(_db.GetChangeLogHead("u1"), bundle.Head);
    }

    private static (string Scope, string Payload, long UpdatedAt)? Shape(AdminDefaultsRow? row)
        => row is null ? null : (row.Scope, Encoding.UTF8.GetString(row.Payload), row.UpdatedAt);

    private static List<(string Profile, string Ns, string Key, int SchemaVersion, long UpdatedAt, string DeviceId, string Value)> Shape(
        IReadOnlyList<SettingRow> rows)
        => rows.Select(row => (row.Profile, row.Ns, row.Key, row.SchemaVersion, row.UpdatedAt, row.DeviceId, Encoding.UTF8.GetString(row.Value))).ToList();

    [Fact]
    public void GetResolveBundle_BaseProfile_CarriesEmptyOverlay()
    {
        var bundle = _db.GetResolveBundle("ghost", "");
        Assert.Empty(bundle.BaseRows);
        Assert.Empty(bundle.OverlayRows);
        Assert.Null(bundle.GlobalDefaults);
        Assert.Null(bundle.UserDefaults);
        Assert.Equal(0, bundle.Head);
    }
}

/// <summary>The (stream, user) fan-out index delivers exactly the old scan's audience.</summary>
public sealed class SseIndexTests
{
    [Fact]
    public async Task PublishToUser_TwoSubscribersSameBucket_BothReceive()
    {
        var hub = new SseHub(NullLogger<SseHub>.Instance);
        var first = hub.Subscribe("alice", "settings");
        var second = hub.Subscribe("alice", "settings");
        var bob = hub.Subscribe("bob", "settings");

        try
        {
            var delivered = hub.PublishToUser("settings", "alice", "settings.changed", "{}");
            Assert.Equal(2, delivered);
            Assert.NotNull(await hub.WaitForEventAsync(first, TimeSpan.FromSeconds(2), CancellationToken.None));
            Assert.NotNull(await hub.WaitForEventAsync(second, TimeSpan.FromSeconds(2), CancellationToken.None));
        }
        finally
        {
            hub.Unsubscribe(first);
            hub.Unsubscribe(second);
            hub.Unsubscribe(bob);
        }
    }

    [Fact]
    public async Task PublishToUser_AfterOneUnsubscribes_SurvivorStillReceives()
    {
        var hub = new SseHub(NullLogger<SseHub>.Instance);
        var gone = hub.Subscribe("alice", "settings");
        var survivor = hub.Subscribe("alice", "settings");
        hub.Unsubscribe(gone);

        try
        {
            var delivered = hub.PublishToUser("settings", "alice", "settings.changed", "{}");
            Assert.Equal(1, delivered);
            Assert.NotNull(await hub.WaitForEventAsync(survivor, TimeSpan.FromSeconds(2), CancellationToken.None));
        }
        finally
        {
            hub.Unsubscribe(survivor);
        }
    }

    [Fact]
    public void Publish_TargetedSet_ReachesOnlyMembers_EmptySetDeliversNobody()
    {
        var hub = new SseHub(NullLogger<SseHub>.Instance);
        var a = hub.Subscribe("a", "events");
        var b = hub.Subscribe("b", "events");

        try
        {
            Assert.Equal(1, hub.Publish("events", new HashSet<string>(new[] { "a" }, StringComparer.Ordinal), "broadcast", "{}"));
            Assert.Equal(0, hub.Publish("events", new HashSet<string>(StringComparer.Ordinal), "broadcast", "{}"));
        }
        finally
        {
            hub.Unsubscribe(a);
            hub.Unsubscribe(b);
        }
    }
}

/// <summary>Inline history bytes + in-batch watermark memo match the documented rules.</summary>
public sealed class BatchBytesAndWatermarkTests : TempDatabaseFixture
{
    public BatchBytesAndWatermarkTests()
        : base("perf-batch-bytes")
    {
    }

    private static Api.SettingsWriteDto Dto(string ns, string key, long at, string json = "true")
        => new() { Ns = ns, Key = key, SchemaVersion = 1, UpdatedAt = at, Value = JsonDocument.Parse(json).RootElement };

    [Fact]
    public void ApplyBatch_DuplicateKey_BytesCountOnlyAppliedWrites()
    {
        var hub = new SseHub(NullLogger<SseHub>.Instance);
        var service = SettingsServiceFactory.Create(_db, hub);

        var response = service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "k", 200, "\"new\""), Dto("ui", "k", 100, "\"old\"") });
        Assert.Single(response.Applied);
        Assert.Single(response.Rejected);

        var entry = Assert.Single(_db.GetSyncHistory("u1", 0, 50));
        Assert.Equal(Encoding.UTF8.GetBytes("\"new\"").Length, entry.Bytes);
    }

    [Fact]
    public void ApplyBatch_DeleteThenStalePut_AntiResurrectionHoldsWithinBatch()
    {
        var quotas = new JellyPlayDatabase.Quotas(1024, 1 << 20, 100);
        var writes = new[]
        {
            new SettingWrite("ui", "k", 1, 200, "d1", Encoding.UTF8.GetBytes("\"v\"")),
            new SettingWrite("ui", "k", 1, 300, "d1", Array.Empty<byte>(), IsDelete: true),
            new SettingWrite("ui", "k", 1, 250, "d1", Encoding.UTF8.GetBytes("\"resurrect\"")),
        };

        var outcome = _db.ApplyBatchWithHistory("u1", "", writes, quotas, "d1", long.MaxValue, "push", 1234, null);

        Assert.Equal(2, outcome.Applied.Count);
        var rejected = Assert.Single(outcome.Rejected);
        Assert.Equal("stale-write", rejected.Reason);
        Assert.Empty(_db.GetSettings("u1", ""));
    }
}

/// <summary>The progress-path index closes replaced sessions with null client/device identities too.</summary>
public sealed class ProgressIndexTests : TempDatabaseFixture
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    public ProgressIndexTests()
        : base("perf-progress-index")
    {
    }

    private AnalyticsService Service(TimeProvider clock)
        => new(_db, () => new Configuration.AnalyticsConfig { Enabled = true }, NullLogger<AnalyticsService>.Instance, clock);

    private static PlaybackProgressEventArgs ProgressNullClient(BaseItem item, Guid userId, long? positionTicks, string playSessionId)
        => new()
        {
            Item = item,
            Session = new MediaBrowser.Controller.Session.SessionInfo(null!, null!) { UserId = userId },
            PlaybackPositionTicks = positionTicks,
            PlaySessionId = playSessionId,
        };

    [Fact]
    public async Task ProgressToAnotherItem_WithNullClient_ClosesTheAbandonedSession()
    {
        var clock = new FakeTimeProvider(FixedNow.AddMinutes(-30));
        var service = Service(clock);
        var userId = Guid.NewGuid();
        var first = new Movie { Id = Guid.NewGuid(), Name = "First", RunTimeTicks = 40 * TimeSpan.TicksPerMinute };
        var second = new Movie { Id = Guid.NewGuid(), Name = "Second", RunTimeTicks = 40 * TimeSpan.TicksPerMinute };

        service.OnPlaybackProgress(ProgressNullClient(first, userId, 1 * TimeSpan.TicksPerMinute, "ps-1"));
        clock.Advance(TimeSpan.FromMinutes(10));
        service.OnPlaybackProgress(ProgressNullClient(second, userId, 1 * TimeSpan.TicksPerMinute, "ps-2"));
        await service.FlushPendingWritesAsync();

        var row = _db.GetPlaybackSessions(null, 0, 50).Single(row => row.ItemName == "First");
        Assert.Equal(FixedNow.AddMinutes(-20).ToUnixTimeMilliseconds(), row.EndedAt);
    }
}

/// <summary>Streaming file-cache IO round-trips values that dwarf the old string buffer.</summary>
public sealed class FileCacheStreamingTests : TempDirFixture
{
    public FileCacheStreamingTests()
        : base("perf-cache-stream")
    {
    }

    [Fact]
    public async Task SetThenGet_LargeValue_RoundTrips()
    {
        var store = new FileCacheStore(NullLogger<FileCacheStore>.Instance, _tempDir, () => 256);
        var value = new string('x', 256 * 1024);

        await store.SetAsync("big", value);

        Assert.Equal(value, await store.GetAsync<string>("big", TimeSpan.FromHours(1)));
    }
}

/// <summary>Prefix-first library resolution matches exactly the legacy substring matches.</summary>
public sealed class LibraryResolutionTests
{
    private static NewMediaPipeline Pipeline(params MediaBrowser.Model.Entities.VirtualFolderInfo[] folders)
        => new(() => new EventsConfig(), virtualFolders: () => folders);

    private static MediaBrowser.Model.Entities.VirtualFolderInfo Folder(string id, params string[] locations)
        => new() { ItemId = id, Locations = locations, Name = "Lib" };

    [Fact]
    public void ResolveLibraryId_PrefixHit_MissAndLegacySubstringMatch()
    {
        var movies = "11111111-1111-1111-1111-111111111111";
        var pipeline = Pipeline(Folder(movies, "/media/movies"));
        pipeline.RefreshVirtualFolders();

        Assert.Equal(movies, pipeline.ResolveLibraryId("/media/movies/film.mkv"));
        Assert.Null(pipeline.ResolveLibraryId("/media/music/song.flac"));

        // Legacy quirk preserved: the old Contains scan matched mid-path
        // occurrences, so the fallback keeps matching them.
        var archive = Pipeline(Folder(movies, "/movies"));
        archive.RefreshVirtualFolders();
        Assert.Equal(movies, archive.ResolveLibraryId("/archive/movies-backup/f.mkv"));
    }

    [Fact]
    public void ResolveLibraryId_PrefixBoundary_DoesNotBleedAcrossSiblings()
    {
        var movies = "11111111-1111-1111-1111-111111111111";
        var other = "22222222-2222-2222-2222-222222222222";
        var pipeline = Pipeline(Folder(movies, "/media/movies"), Folder(other, "/media/movies2"));
        pipeline.RefreshVirtualFolders();

        Assert.Equal(other, pipeline.ResolveLibraryId("/media/movies2/film.mkv"));
    }
}
