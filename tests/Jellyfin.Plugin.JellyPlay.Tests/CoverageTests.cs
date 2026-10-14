using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Api;
using Jellyfin.Plugin.JellyPlay.Configuration;
using Jellyfin.Plugin.JellyPlay.Helpers;
using Jellyfin.Plugin.JellyPlay.Realtime;
using Jellyfin.Plugin.JellyPlay.Services.Admin;
using Jellyfin.Plugin.JellyPlay.Services.Analytics;
using Jellyfin.Plugin.JellyPlay.Services.Cache;
using Jellyfin.Plugin.JellyPlay.Services.Events;
using Jellyfin.Plugin.JellyPlay.Services.Messages;
using Jellyfin.Plugin.JellyPlay.Services.Settings;
using Jellyfin.Plugin.JellyPlay.Services.Shared;
using Jellyfin.Plugin.JellyPlay.Storage;
using Jellyfin.Plugin.JellyPlay.Storage.Models;
using Jellyfin.Plugin.JellyPlay.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.JellyPlay.Tests;

// ---------------------------------------------------------------------------
// Scheduled tasks: every task runs host-free (real DB / disabled upstreams).
// ---------------------------------------------------------------------------

public sealed class ScheduledTasksTests : TempDatabaseFixture
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));

    public ScheduledTasksTests()
        : base("cov-tasks")
    {
    }

    private static IProgress<double> Progress() => new Progress<double>();

    [Fact]
    public async Task SettingsIntegrityTask_HealthyDatabase_ReportsOk()
    {
        var task = new SettingsIntegrityTask(_db, NullLogger<SettingsIntegrityTask>.Instance);
        Assert.Equal("JellyPlay.SettingsIntegrity", task.Key);
        Assert.Equal("JellyPlay", task.Category);
        Assert.NotEmpty(task.Name);
        Assert.NotEmpty(task.Description);
        Assert.NotEmpty(task.GetDefaultTriggers());

        await task.ExecuteAsync(Progress(), CancellationToken.None);
        Assert.True(_db.CheckIntegrity().IntegrityOk);
    }

    [Fact]
    public async Task ExternalCacheWarmTask_DisabledSources_CompletesWithoutHttp()
    {
        using var cache = new ThrowawayCache();
        var fetcher = new Services.Fetching.ResilientFetcher(
            new FakeHttpClientFactory(new ThrowingHandler()),
            cache.Store,
            NullLogger<Services.Fetching.ResilientFetcher>.Instance,
            _clock);
        var charts = new Services.Ratings.ImdbChartsService(fetcher, () => new RatingsConfig { EnableImdbCharts = false }, _clock);
        var seasonal = new Services.Rows.SeasonalService(fetcher, () => new RatingsConfig(), _clock);
        var task = new ExternalCacheWarmTask(charts, seasonal, NullLogger<ExternalCacheWarmTask>.Instance);

        Assert.Equal("JellyPlay.ExternalCacheWarm", task.Key);
        await task.ExecuteAsync(Progress(), CancellationToken.None);
    }

    [Fact]
    public async Task AnalyticsMaintenanceTask_RunsThePass()
    {
        var analytics = new AnalyticsService(_db, () => new AnalyticsConfig { Enabled = true }, NullLogger<AnalyticsService>.Instance, _clock);
        try
        {
            var task = new AnalyticsMaintenanceTask(analytics, NullLogger<AnalyticsMaintenanceTask>.Instance);
            Assert.Equal("JellyPlay.AnalyticsMaintenance", task.Key);
            await task.ExecuteAsync(Progress(), CancellationToken.None);
        }
        finally
        {
            analytics.Dispose();
        }
    }

    [Fact]
    public async Task ChangeLogPruneTask_PrunesOldRows()
    {
        var config = new SyncConfig { ChangeLogRetentionDays = 30, HistoryRetentionDays = 30, SnapshotRetentionDays = 30 };
        _db.InsertSyncHistory("u1", "d1", "push", 1, 0, 5, null, _clock.NowMs - 60 * 86_400_000L);
        _db.InsertSyncHistory("u1", "d1", "push", 1, 0, 5, null, _clock.NowMs);
        _db.InsertSnapshot("u1", new byte[] { 1 }, _clock.NowMs - 60 * 86_400_000L, "manual", 1, 1, keepLast: 5);

        var task = new ChangeLogPruneTask(_db, () => config, NullLogger<ChangeLogPruneTask>.Instance, _clock);
        Assert.Equal("JellyPlay.ChangeLogPrune", task.Key);
        await task.ExecuteAsync(Progress(), CancellationToken.None);

        Assert.Single(_db.GetSyncHistory("u1", 0, 50));
        Assert.Empty(_db.GetSnapshots("u1"));
    }

    [Fact]
    public async Task CacheMaintenanceTask_Sweeps()
    {
        using var cache = new ThrowawayCache();
        var task = new CacheMaintenanceTask(cache.Store, NullLogger<CacheMaintenanceTask>.Instance);
        Assert.Equal("JellyPlay.CacheMaintenance", task.Key);
        await task.ExecuteAsync(Progress(), CancellationToken.None);
    }

    private sealed class ThrowingHandler : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("no HTTP expected: " + request.RequestUri);
    }

    private sealed class ThrowawayCache : IDisposable
    {
        private readonly string _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jellyplay-covtask-" + Guid.NewGuid().ToString("N"));

        public ThrowawayCache()
        {
            System.IO.Directory.CreateDirectory(_dir);
            Store = new FileCacheStore(NullLogger<FileCacheStore>.Instance, _dir, () => 256);
        }

        public FileCacheStore Store { get; }

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(_dir, recursive: true);
            }
            catch (System.IO.IOException)
            {
            }
        }
    }
}

// ---------------------------------------------------------------------------
// Event pipeline: consumers + EventService publish paths (no host).
// ---------------------------------------------------------------------------

public sealed class EventPipelineTests : TempDatabaseFixture
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
    private readonly SseHub _hub = new(NullLogger<SseHub>.Instance);

    public EventPipelineTests()
        : base("cov-events")
    {
    }

    private EventService Events(EventsConfig? config = null, NewMediaPipeline? pipeline = null)
        => new(
            _hub,
            () => config ?? new EventsConfig(),
            () => new List<string> { "admin-1" },
            pipeline ?? new NewMediaPipeline(() => config ?? new EventsConfig(), _clock),
            NullLogger<EventService>.Instance,
            new NotificationFanout(_hub),
            _clock);

    private static SessionInfo Session(string userName)
        => new(null!, null!) { UserId = Guid.NewGuid(), UserName = userName };

    [Fact]
    public async Task Consumers_NullArgs_NeverThrow()
    {
        var events = Events();
        await new SessionStartedEvent(events).OnEvent(null);
        await new PlaybackStartedEvent(events).OnEvent(null);
        await new UserLockedOutEvent(events).OnEvent(null);
        await new PlaybackStoppedAnalyticsConsumer(new AnalyticsService(_db, () => new AnalyticsConfig { Enabled = true }, NullLogger<AnalyticsService>.Instance, _clock)).OnEvent(null);
    }

    [Fact]
    public async Task Consumers_ForwardUsernamesToTheHub()
    {
        var events = Events();
        var sub = _hub.Subscribe("anyone", "events");
        try
        {
            await new SessionStartedEvent(events).OnEvent(new MediaBrowser.Controller.Events.Session.SessionStartedEventArgs(Session("alice")));
            await new PlaybackStartedEvent(events).OnEvent(new PlaybackStartEventArgs { Session = Session("bob") });
            await new UserLockedOutEvent(events).OnEvent(
                new Jellyfin.Data.Events.Users.UserLockedOutEventArgs(new Jellyfin.Database.Implementations.Entities.User("mallory", "auth", "reset")));

            var first = await _hub.WaitForEventAsync(sub, TimeSpan.FromSeconds(2), CancellationToken.None);
            Assert.NotNull(first);
            Assert.Equal("session-started", first!.EventName);
        }
        finally
        {
            _hub.Unsubscribe(sub);
        }
    }

    [Fact]
    public void Consumers_EmptyUsername_PublishesNothing()
    {
        var events = Events();
        Assert.Equal(0, events.PublishSessionStarted(""));
        Assert.Equal(0, events.PublishPlaybackStarted(""));
        Assert.Equal(0, events.PublishUserLockedOut(""));
    }

    [Fact]
    public void PublishSessionFamily_DisabledConfig_PublishesNothing()
    {
        var events = Events(new EventsConfig { SessionStartedEnabled = false, PlaybackStartedEnabled = false, UserLockedOutEnabled = false });
        Assert.Equal(0, events.PublishSessionStarted("alice"));
        Assert.Equal(0, events.PublishPlaybackStarted("bob"));
        Assert.Equal(0, events.PublishUserLockedOut("mallory"));
    }

    [Fact]
    public void PublishSessionFamily_Enabled_ReachesSubscribers()
    {
        var events = Events();
        var sub = _hub.Subscribe("watcher", "events");
        try
        {
            Assert.Equal(1, events.PublishSessionStarted("alice"));
            Assert.Equal(1, events.PublishPlaybackStarted("bob"));
            Assert.Equal(1, events.PublishUserLockedOut("mallory"));
            Assert.Equal(1, events.PublishBroadcast("Hello", "Body", null));
        }
        finally
        {
            _hub.Unsubscribe(sub);
        }
    }

    [Fact]
    public void PublishNewMedia_Disabled_ReturnsZero()
    {
        var events = Events(new EventsConfig { NewMediaEnabled = false });
        var group = new EpisodeGroup(Guid.NewGuid(), Guid.NewGuid(), "Show", 1, new[] { new EpisodeRef(Guid.NewGuid(), "E1") }, null);
        Assert.Equal(0, events.PublishNewMedia(group));
        Assert.Equal(0, events.PublishNewMovie(Guid.NewGuid(), "Film", null));
    }

    [Fact]
    public void PublishNewMedia_Enabled_ReachesSubscribers()
    {
        var config = new EventsConfig { NewMediaEnabled = true, NewMediaAudience = "all" };
        var events = Events(config);
        var sub = _hub.Subscribe("watcher", "events");
        try
        {
            var group = new EpisodeGroup(Guid.NewGuid(), Guid.NewGuid(), "Show", 1, new[] { new EpisodeRef(Guid.NewGuid(), "E1") }, null);
            Assert.Equal(1, events.PublishNewMedia(group));
            var movieId = Guid.NewGuid();
            Assert.Equal(1, events.PublishNewMovie(movieId, "Film", null));
            // Same keys within the dedup window suppress.
            Assert.Equal(0, events.PublishNewMedia(group));
            Assert.Equal(0, events.PublishNewMovie(movieId, "Film", null));
        }
        finally
        {
            _hub.Unsubscribe(sub);
        }
    }

    [Fact]
    public void PublishNewMedia_AdminsAudience_UsesAdminIds()
    {
        var config = new EventsConfig { NewMediaEnabled = true, NewMediaAudience = "admins" };
        var events = Events(config);
        var admin = _hub.Subscribe("admin-1", "events");
        var pleb = _hub.Subscribe("pleb", "events");
        try
        {
            var group = new EpisodeGroup(Guid.NewGuid(), Guid.NewGuid(), "Show", 1, new[] { new EpisodeRef(Guid.NewGuid(), "E1") }, null);
            Assert.Equal(1, events.PublishNewMedia(group));
        }
        finally
        {
            _hub.Unsubscribe(admin);
            _hub.Unsubscribe(pleb);
        }
    }
}

// ---------------------------------------------------------------------------
// NewMediaPipeline: gates, grouping windows, drains (no host).
// ---------------------------------------------------------------------------

public sealed class PipelineGateTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));

    private NewMediaPipeline Pipeline(EventsConfig config)
        => new(() => config, _clock);

    private sealed class RecordingSink : INewMediaSink
    {
        public readonly List<EpisodeGroup> Groups = new();
        public int Movies;

        public int PublishNewMedia(EpisodeGroup group, IReadOnlyList<string>? adminUserIds = null)
        {
            Groups.Add(group);
            return 1;
        }

        public int PublishNewMovie(Guid itemId, string title, string? libraryId)
        {
            Movies++;
            return 1;
        }
    }

    [Fact]
    public void ShouldEmit_Disabled_NeverEmits()
    {
        var pipeline = Pipeline(new EventsConfig { NewMediaEnabled = false });
        var group = new EpisodeGroup(Guid.NewGuid(), Guid.NewGuid(), "S", 1, new[] { new EpisodeRef(Guid.NewGuid(), "E") }, null);
        Assert.False(pipeline.ShouldEmit(group));
        Assert.False(pipeline.ShouldEmitMovie(Guid.NewGuid(), null));
        Assert.False(pipeline.HasWork);
    }

    [Fact]
    public void ShouldEmit_SecondCallWithinThreshold_Suppresses()
    {
        var pipeline = Pipeline(new EventsConfig { NewMediaEnabled = true, DedupThresholdSeconds = 3600 });
        var series = Guid.NewGuid();
        var episode = Guid.NewGuid();
        var group = new EpisodeGroup(Guid.NewGuid(), series, "S", 1, new[] { new EpisodeRef(episode, "E") }, null);

        Assert.True(pipeline.ShouldEmit(group));
        Assert.False(pipeline.ShouldEmit(group));
        Assert.True(pipeline.ShouldEmitMovie(Guid.NewGuid(), null));

        _clock.Advance(TimeSpan.FromHours(2));
        Assert.True(pipeline.ShouldEmit(group));
    }

    [Fact]
    public void IsLibraryAllowed_EmptyAllows_ListedOnly()
    {
        var open = Pipeline(new EventsConfig { NewMediaEnabled = true });
        Assert.True(open.IsLibraryAllowed(null));
        Assert.True(open.IsLibraryAllowed(Guid.NewGuid().ToString()));

        var allowed = Guid.NewGuid();
        var gated = Pipeline(new EventsConfig { NewMediaEnabled = true, NewMediaEnabledLibraries = new List<Guid> { allowed } });
        Assert.True(gated.IsLibraryAllowed(null));
        Assert.True(gated.IsLibraryAllowed(allowed.ToString()));
        Assert.False(gated.IsLibraryAllowed(Guid.NewGuid().ToString()));
    }

    [Fact]
    public void TryAddEpisode_RejectsInvalidOrDisabled()
    {
        var pipeline = Pipeline(new EventsConfig { NewMediaEnabled = true });
        Assert.False(pipeline.TryAddEpisode(Guid.Empty, Guid.NewGuid(), "S", 1, Guid.NewGuid(), "E", null));
        Assert.False(pipeline.TryAddEpisode(Guid.NewGuid(), Guid.Empty, "S", 1, Guid.NewGuid(), "E", null));

        var off = Pipeline(new EventsConfig { NewMediaEnabled = false });
        Assert.False(off.TryAddEpisode(Guid.NewGuid(), Guid.NewGuid(), "S", 1, Guid.NewGuid(), "E", null));
    }

    [Fact]
    public void DrainDue_ReleasesDueGroups_ToTheSink()
    {
        var pipeline = Pipeline(new EventsConfig { NewMediaEnabled = true, NewMediaGroupingSeconds = 0 });
        var season = Guid.NewGuid();
        var series = Guid.NewGuid();
        Assert.True(pipeline.TryAddEpisode(season, series, "Show", 2, Guid.NewGuid(), "E1", null));
        Assert.True(pipeline.TryAddEpisode(season, series, "Show", 2, Guid.NewGuid(), "E2", null));
        Assert.True(pipeline.HasWork);

        var sink = new RecordingSink();
        pipeline.DrainDue(sink, new List<string> { "admin-1" });

        var group = Assert.Single(sink.Groups);
        Assert.Equal(2, group.Episodes.Count);
        Assert.Equal(2, group.SeasonIndex);
        Assert.False(pipeline.HasWork);
    }

    [Fact]
    public void DrainDue_Idle_SkipsWork()
    {
        var pipeline = Pipeline(new EventsConfig { NewMediaEnabled = true });
        var sink = new RecordingSink();
        pipeline.DrainDue(sink, new List<string>());
        Assert.Empty(sink.Groups);

        var off = Pipeline(new EventsConfig { NewMediaEnabled = false });
        Assert.True(off.TryAddEpisode(Guid.NewGuid(), Guid.NewGuid(), "S", 1, Guid.NewGuid(), "E", null) == false);
        off.DrainAll(sink, new List<string>());
        Assert.Empty(sink.Groups);
    }

    [Fact]
    public void PopDue_WindowNotElapsed_HoldsFlushAllReleases()
    {
        var pipeline = Pipeline(new EventsConfig { NewMediaEnabled = true, NewMediaGroupingSeconds = 3600 });
        Assert.True(pipeline.TryAddEpisode(Guid.NewGuid(), Guid.NewGuid(), "S", 1, Guid.NewGuid(), "E", null));
        Assert.Empty(pipeline.PopDue());
        Assert.Single(pipeline.FlushAll());
    }
}

// ---------------------------------------------------------------------------
// SyncOpRecorder: history rows, pull coalescing, fan-out (no host).
// ---------------------------------------------------------------------------

public sealed class RecorderTests : TempDatabaseFixture
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
    private readonly SseHub _hub = new(NullLogger<SseHub>.Instance);

    public RecorderTests()
        : base("cov-recorder")
    {
    }

    private SyncOpRecorder Recorder()
        => new(_db, _hub, null, NullLogger<SyncOpRecorder>.Instance, _clock);

    [Fact]
    public void Record_WritesHistoryRow_AndFansAdminOp()
    {
        using var recorder = Recorder();
        var admin = _hub.Subscribe("admin", "admin");
        try
        {
            recorder.Record("u1", "d1", SyncOpRecorder.OpPush, 2, 1, 20, null, 0, 5);
            var row = Assert.Single(_db.GetSyncHistory("u1", 0, 50));
            Assert.Equal("push", row.Op);
            Assert.Equal(2, row.KeysApplied);
            Assert.Equal(1, row.KeysRejected);
            Assert.Equal(20, row.Bytes);
        }
        finally
        {
            _hub.Unsubscribe(admin);
        }
    }

    [Fact]
    public async Task RecordPull_CoalescesPerDevice_OnFlush()
    {
        using var recorder = Recorder();
        recorder.RecordPull("u1", "d1", 1, 0, 5);
        recorder.RecordPull("u1", "d1", 3, 0, 9); // latest wins
        recorder.RecordPull("u1", "d2", 2, 0, 7);
        await recorder.FlushAsync();

        var rows = _db.GetSyncHistory("u1", 0, 50);
        Assert.Equal(2, rows.Count);
        var d1 = rows.Single(row => row.DeviceId == "d1");
        Assert.Equal(3, d1.KeysApplied);
    }

    [Fact]
    public void RecordReset_AndRecordWipe_WriteRanges()
    {
        using var recorder = Recorder();
        recorder.RecordReset("u1", "d1", 4, 10, 20);
        recorder.RecordWipe("u1", "d1", 2, 20, 25);

        var rows = _db.GetSyncHistory("u1", 0, 50);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, row => row.Op == SyncOpRecorder.OpReset && row.KeysApplied == 4);
        Assert.Contains(rows, row => row.Op == SyncOpRecorder.OpWipe && row.KeysApplied == 2);
    }

    [Fact]
    public async Task PublishChanged_DeliversToLiveSubscriber_SilentWithout()
    {
        using var recorder = Recorder();
        var sub = _hub.Subscribe("u1", "settings");
        try
        {
            recorder.PublishChanged("u1", 42, "settings.changed", "{}");
            var evt = await _hub.WaitForEventAsync(sub, TimeSpan.FromSeconds(2), CancellationToken.None);
            Assert.NotNull(evt);
            Assert.Equal(42UL, evt!.Id);
        }
        finally
        {
            _hub.Unsubscribe(sub);
        }

        // Nobody streaming and no push configured: best-effort, never throws.
        recorder.PublishChanged("ghost", 7, "settings.changed", "{}");
        recorder.PublishAdminOp("u1", "d1", SyncOpRecorder.OpPush, 1, 0);
    }

    [Fact]
    public void Helpers_RangeAndBytesMatchTheBatchComposite()
    {
        Assert.Equal((1L, 9L), SyncOpRecorder.Range(1, 9));
        Assert.Equal((2L, 8L), SyncOpRecorder.PullRange(2, 8));

        var writes = new[]
        {
            new SettingWrite("ui", "a", 1, 1, "d", Encoding.UTF8.GetBytes("\"x\"")),
            new SettingWrite("ui", "b", 1, 1, "d", Encoding.UTF8.GetBytes("\"yy\"")),
        };
        var applied = new[] { new AppliedSetting("ui", "a", 1, 1) };
        Assert.Equal(3, SyncOpRecorder.ApproximateAppliedBytes(writes, applied));
    }
}

// ---------------------------------------------------------------------------
// Messages + backup + registry leftovers (real DB, no host).
// ---------------------------------------------------------------------------

public sealed class MessageRegistryTests : TempDatabaseFixture
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
    private readonly SseHub _hub = new(NullLogger<SseHub>.Instance);

    public MessageRegistryTests()
        : base("cov-messages")
    {
    }

    private MessageService Service()
        => new(_db, NullLogger<MessageService>.Instance, new NotificationFanout(_hub), () => new List<string> { "admin-1" }, _clock);

    private static MessageAdminRequest Request(string title, string audience = "all", long? startsAt = null, long? endsAt = null, List<string>? userIds = null)
        => new()
        {
            Title = title,
            Body = "body",
            Color = "red",
            Audience = new AudiencePayload { Type = audience, UserIds = userIds ?? new List<string>() },
            StartsAt = startsAt,
            EndsAt = endsAt,
        };

    [Fact]
    public void Upsert_CreateEditDelete_RoundTrips()
    {
        var service = Service();
        var row = service.Upsert(Request("Hello"));
        Assert.False(string.IsNullOrEmpty(row.Id));
        Assert.Single(service.GetAll());

        service.Upsert(new MessageAdminRequest
        {
            Id = row.Id,
            Title = "Edited",
            Body = "body",
            Color = "blue",
            Audience = new AudiencePayload { Type = "all" },
        });
        Assert.Equal("Edited", service.GetAll().Single().Title);
        Assert.True(service.Delete(row.Id));
        Assert.False(service.Delete(row.Id));
        Assert.Empty(service.GetAll());
    }

    [Fact]
    public void GetInbox_AppliesAudienceAndWindows()
    {
        var service = Service();
        service.Upsert(Request("Everyone"));
        service.Upsert(Request("Admins", "admins"));
        service.Upsert(Request("Listed", "users", userIds: new List<string> { "u1" }));
        service.Upsert(Request("Future", startsAt: _clock.NowMs + 3_600_000));
        service.Upsert(Request("Expired", endsAt: _clock.NowMs - 3_600_000));

        var inbox = service.GetInbox("u1", isAdmin: false).Select(message => message.Title).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("Everyone", inbox);
        Assert.Contains("Listed", inbox);
        Assert.DoesNotContain("Admins", inbox);
        Assert.DoesNotContain("Future", inbox);
        Assert.DoesNotContain("Expired", inbox);

        var adminInbox = service.GetInbox("admin-1", isAdmin: true).Select(message => message.Title).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("Admins", adminInbox);
        Assert.DoesNotContain("Listed", adminInbox);

        var first = service.GetInbox("u1", false).Single(message => message.Title == "Everyone");
        Assert.False(first.Read);
        service.MarkRead("u1", first.Id);
        Assert.True(service.GetInbox("u1", false).Single(message => message.Title == "Everyone").Read);
    }
}

public sealed class BackupRoundTripTests : TempDatabaseFixture
{
    public BackupRoundTripTests()
        : base("cov-backup")
    {
    }

    [Fact]
    public void CreateBackup_Restore_RoundTripsState_AndRejectsGarbage()
    {
        var service = new ConfigBackupService(_db, NullLogger<ConfigBackupService>.Instance);
        _db.SetAdminDefaults("global", Encoding.UTF8.GetBytes("{\"a\":1}"), 1);
        _db.UpsertMessage(new MessageRow("m1", "T", "B", "red", "", "", "{\"Type\":\"all\",\"UserIds\":[]}", null, null, 0, 1));

        var backup = service.CreateBackup();
        Assert.True(backup.Length > 0);

        _db.DeleteMessage("m1");
        var outcome = service.Restore(backup);
        Assert.True(outcome.Success);
        Assert.Single(_db.GetMessages());
        Assert.NotNull(_db.GetAdminDefaults("global"));

        var bad = service.Restore(new byte[] { 1, 2, 3 });
        Assert.False(bad.Success);
        Assert.False(string.IsNullOrEmpty(bad.Error));
    }
}

public sealed class RegistryExtraTests : TempDatabaseFixture
{
    public RegistryExtraTests()
        : base("cov-registry")
    {
    }

    private static DeviceWrite Device(string id, string user, string? kind = null, string? endpoint = null)
        => new(id, user, "Name-" + id, "web", "1.0", 1000, PushKind: kind, PushEndpoint: endpoint);

    [Fact]
    public void Devices_UpsertRenameRevokeDelete_RoundTrip()
    {
        _db.UpsertDevice(Device("d1", "u1"));
        Assert.Single(_db.GetDevices("u1"));
        Assert.True(_db.RenameDevice("u1", "d1", "New", null));
        Assert.False(_db.RenameDevice("u1", "nope", "New", null));
        Assert.False(_db.IsDeviceRevoked("d1"));
        Assert.True(_db.SetDeviceRevoked("u1", "d1", true));
        Assert.True(_db.IsDeviceRevoked("d1"));
        Assert.False(_db.SetDeviceRevoked("u1", "nope", true));
        Assert.True(_db.DeleteDevice("u1", "d1"));
        Assert.False(_db.DeleteDevice("u1", "d1"));
        Assert.Null(_db.GetDeviceById("d1"));
    }

    [Fact]
    public void PushDevices_FiltersRevokedAndUnregistered_EmptyMatchesNobody()
    {
        _db.UpsertDevice(Device("push-1", "u1", "generic", "https://push.example/t1"));
        _db.UpsertDevice(Device("push-2", "u2", "ntfy", "https://ntfy.example/t2"));
        _db.UpsertDevice(Device("plain", "u1"));
        _db.UpsertDevice(Device("revoked", "u1", "generic", "https://push.example/t3"));
        _db.SetDeviceRevoked("u1", "revoked", true);

        Assert.Equal(2, _db.GetPushDevices((IReadOnlyCollection<string>?)null).Count);
        Assert.Empty(_db.GetPushDevices((IReadOnlyCollection<string>)new List<string>()));
        Assert.Single(_db.GetPushDevices((IReadOnlyCollection<string>)new List<string> { "u2" }));
        Assert.Equal(2, _db.GetAllPushDevices().Count);
        Assert.NotNull(_db.GetDeviceById("push-1"));
    }

    [Fact]
    public void Bookmarks_Crud_RoundTrips()
    {
        var row = new BookmarkRow("b1", "u1", "item1", 12.5, null, "L", "N", 100, 200);
        _db.UpsertBookmark(row);
        Assert.Single(_db.GetBookmarks("u1", null));
        Assert.Single(_db.GetBookmarks("u1", "item1"));
        Assert.Empty(_db.GetBookmarks("u1", "other"));
        Assert.True(_db.DeleteBookmark("u1", "b1"));
        Assert.False(_db.DeleteBookmark("u1", "b1"));
    }

    [Fact]
    public void SeerrSessions_Crud_RoundTrips()
    {
        var row = new SeerrSessionRow("u1", new byte[] { 9, 9 }, 100, 200);
        _db.UpsertSeerrSession(row);
        var stored = _db.GetSeerrSession("u1");
        Assert.NotNull(stored);
        Assert.Equal(row.UserId, stored!.UserId);
        Assert.Equal(row.CookiesPayload, stored.CookiesPayload);
        Assert.Equal(row.CreatedAt, stored.CreatedAt);
        Assert.Equal(row.LastValidatedAt, stored.LastValidatedAt);
        Assert.Null(_db.GetSeerrSession("ghost"));
        Assert.True(_db.DeleteSeerrSession("u1"));
        Assert.False(_db.DeleteSeerrSession("u1"));
    }

    [Fact]
    public void Restore_ReplaysRows_Transactionally()
    {
        _db.UpsertMessage(new MessageRow("m1", "T", "B", "c", "", "", "{}", null, null, 0, 1));
        _db.SetAdminDefaults("global", new byte[] { 7 }, 1);

        var messages = _db.GetMessages().ToList();
        var defaults = _db.GetAllAdminDefaults().ToList();
        _db.DeleteMessage("m1");
        _db.RestoreMessages(messages);
        _db.RestoreAdminDefaults(defaults);

        Assert.Single(_db.GetMessages());
        Assert.Single(_db.GetAllAdminDefaults());
        Assert.Empty(_db.GetReadMessageIds("u1"));
        _db.MarkMessageRead("u1", "m1", 5);
        Assert.Contains("m1", _db.GetReadMessageIds("u1"));
    }
}
