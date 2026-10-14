using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Api;
using Jellyfin.Plugin.JellyPlay.Configuration;
using Jellyfin.Plugin.JellyPlay.Realtime;
using Jellyfin.Plugin.JellyPlay.Services.Analytics;
using Jellyfin.Plugin.JellyPlay.Services.Cache;
using Jellyfin.Plugin.JellyPlay.Services.Fetching;
using Jellyfin.Plugin.JellyPlay.Services.Ratings;
using Jellyfin.Plugin.JellyPlay.Services.Seerr;
using Jellyfin.Plugin.JellyPlay.Storage;
using Jellyfin.Plugin.JellyPlay.Storage.Models;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.QuickConnect;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.QuickConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.JellyPlay.Tests;

// ---------------------------------------------------------------------------
// Analytics consumers: null-tolerant adapters over the recorder.
// ---------------------------------------------------------------------------

public sealed class AnalyticsConsumerTests : TempDatabaseFixture
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    public AnalyticsConsumerTests()
        : base("cov-consumers")
    {
    }

    private AnalyticsService Service()
        => new(_db, () => new AnalyticsConfig { Enabled = true }, NullLogger<AnalyticsService>.Instance, new FakeTimeProvider(FixedNow));

    private static PlaybackStopEventArgs Stopped(BaseItem item, Guid userId, long? ticks)
        => new() { Item = item, Session = new SessionInfo(null!, null!) { UserId = userId }, PlaybackPositionTicks = ticks };

    private static PlaybackProgressEventArgs Progressed(BaseItem item, Guid userId, long? ticks)
        => new() { Item = item, Session = new SessionInfo(null!, null!) { UserId = userId }, PlaybackPositionTicks = ticks };

    [Fact]
    public async Task Consumers_ForwardToTheRecorder()
    {
        var service = Service();
        try
        {
            await new PlaybackStoppedAnalyticsConsumer(service).OnEvent(null);
            await new PlaybackProgressAnalyticsConsumer(service).OnEvent(null);

            var item = new Movie { Id = Guid.NewGuid(), Name = "M", RunTimeTicks = 40 * TimeSpan.TicksPerMinute };
            await new PlaybackStoppedAnalyticsConsumer(service)
                .OnEvent(Stopped(item, Guid.NewGuid(), 30 * TimeSpan.TicksPerMinute));
            await service.FlushPendingWritesAsync();
            Assert.Single(_db.GetPlaybackSessions(null, 0, 50));

            await new PlaybackProgressAnalyticsConsumer(service)
                .OnEvent(Progressed(item, Guid.NewGuid(), 30 * TimeSpan.TicksPerMinute));
        }
        finally
        {
            service.Dispose();
        }
    }
}

// ---------------------------------------------------------------------------
// FileCacheStore: expiry, invalidation, miss markers, sweeps.
// ---------------------------------------------------------------------------

public sealed class FileCacheExtraTests : FetcherTestBase
{
    [Fact]
    public async Task ExpiredEntry_ReadsAsMiss_OnAColdTable()
    {
        await Cache.SetAsync("old", "v");
        ExpireCacheEntries(TimeSpan.FromHours(1));

        var cold = new FileCacheStore(NullLogger<FileCacheStore>.Instance, TempDir, () => 256);
        Assert.Null(await cold.GetAsync<string>("old", TimeSpan.FromHours(1)));
    }

    [Fact]
    public void Invalidate_DropsValueAndMemory()
    {
        Cache.SetAsync("k", "v").GetAwaiter().GetResult();
        Cache.Invalidate("k");
        Assert.Null(Cache.GetAsync<string>("k", TimeSpan.FromHours(1)).GetAwaiter().GetResult());
    }

    [Fact]
    public async Task TryGet_HitMissMarkerAndNotFound()
    {
        await Cache.SetAsync("hit", "v");
        var hit = await Cache.TryGetAsync<string>("hit", null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        Assert.Equal(CacheProbeKind.Hit, hit.Kind);
        Assert.Equal("v", hit.Value);

        var missing = await Cache.TryGetAsync<string>("missing", null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        Assert.Equal(CacheProbeKind.NotFound, missing.Kind);

        await Cache.SetAsync("gone:miss", true);
        var marker = await Cache.TryGetAsync<string>("gone", "gone:miss", TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        Assert.Equal(CacheProbeKind.MissMarker, marker.Kind);
    }

    [Fact]
    public async Task Sweep_PurgesAgedEntries_AndEvictsPastTheCap()
    {
        await Cache.SetAsync("a", "value-a");
        await Cache.SetAsync("b", "value-b");
        await Cache.SetAsync("c", "value-c");

        ExpireCacheEntries(TimeSpan.FromHours(1));
        var aged = Cache.Sweep(1 << 20, TimeSpan.FromHours(1));
        Assert.Equal(3, aged.DeletedFiles);
        Assert.True(aged.BytesReclaimed > 0);

        await Cache.SetAsync("x", "value-x");
        await Cache.SetAsync("y", "value-y");
        var evicted = Cache.Sweep(10, TimeSpan.FromDays(30));
        Assert.True(evicted.DeletedFiles > 0);
        var cold = new FileCacheStore(NullLogger<FileCacheStore>.Instance, TempDir, () => 256);
        Assert.Null(await cold.GetAsync<string>("x", TimeSpan.FromHours(1)));
    }
}

// ---------------------------------------------------------------------------
// CacheKeys: pure key shapes.
// ---------------------------------------------------------------------------

public sealed class CacheKeysTests
{
    [Fact]
    public void AnimeSeriesKey_Deterministic_AndSensitiveToInputs()
    {
        var config = new AnimeConfig();
        var a = CacheKeys.AnimeSeries("s1", "hint", true, true, null);
        Assert.Equal(a, CacheKeys.AnimeSeries("s1", "hint", true, true, null));
        Assert.Equal(a, CacheKeys.AnimeSeries("s1", "hint", config.EnableFillerList, config.EnableTenrai, config.SeriesOverrides));
        Assert.NotEqual(a, CacheKeys.AnimeSeries("s1", "other", true, true, null));
        Assert.NotEqual(a, CacheKeys.AnimeSeries("s1", "hint", false, true, null));
        Assert.NotEqual(a, CacheKeys.AnimeSeries("s2", "hint", true, true, null));
    }

    [Fact]
    public void OverridesHash_NoneWhenEmpty_OrderIndependentOtherwise()
    {
        Assert.Equal("none", CacheKeys.OverridesHash(null));
        Assert.Equal("none", CacheKeys.OverridesHash(new List<AnimeSeriesOverride>()));

        var first = new AnimeSeriesOverride { SeriesId = "a", AniListId = "1" };
        var second = new AnimeSeriesOverride { SeriesId = "b", MalId = "2" };
        Assert.Equal(
            CacheKeys.OverridesHash(new[] { first, second }),
            CacheKeys.OverridesHash(new[] { second, first }));
        Assert.NotEqual(
            CacheKeys.OverridesHash(new[] { first }),
            CacheKeys.OverridesHash(new[] { second }));
    }

    [Fact]
    public void SlugifyName_KebabCasesTitles()
    {
        Assert.Equal("hello-world", CacheKeys.SlugifyName("  Hello, World!  "));
        Assert.Equal("one-piece", CacheKeys.SlugifyName("one-piece"));
        Assert.Equal(string.Empty, CacheKeys.SlugifyName("!!!"));
    }
}

// ---------------------------------------------------------------------------
// RateLimitFilter: allow then 429, key derivation per strategy.
// ---------------------------------------------------------------------------

public sealed class RateLimitFilterTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));

    private DefaultHttpContext Context(IServiceProvider services, ClaimsPrincipal? user = null, string? forwardedFor = null)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        if (user is not null)
        {
            context.User = user;
        }

        if (forwardedFor is not null)
        {
            context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        }

        return context;
    }

    private static ClaimsPrincipal User(string id)
        => new(new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", id) }));

    private IServiceProvider Provider(bool trustProxy = false)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new Services.Admin.RateLimiterRegistry());
        services.AddSingleton<TimeProvider>(_clock);
        services.AddSingleton<Func<SeerrConfig>>(() => new SeerrConfig { TrustProxyHeaders = trustProxy });
        return services.BuildServiceProvider();
    }

    private static ActionExecutingContext Executing(DefaultHttpContext httpContext)
        => new(new ActionContext(httpContext, new RouteData(), new ActionDescriptor()), new List<IFilterMetadata>(), new Dictionary<string, object?>(), new object());

    [Fact]
    public async Task Filter_AllowsThenReturns429_WithCamelErrorBody()
    {
        var provider = Provider();
        var filter = new RateLimitFilter(Services.Admin.RateLimiterKind.PushDefaults, "test", RateLimitKeyStrategy.User);
        var invocations = 0;
        ActionExecutionDelegate next = () =>
        {
            invocations++;
            return Task.FromResult<ActionExecutedContext>(null!);
        };

        var user = User("11111111-1111-1111-1111-111111111111");
        for (var index = 0; index < 5; index++)
        {
            var allowed = Executing(Context(provider, user));
            await filter.OnActionExecutionAsync(allowed, next);
            Assert.Null(allowed.Result);
        }

        Assert.Equal(5, invocations);
        var blocked = Executing(Context(provider, user));
        await filter.OnActionExecutionAsync(blocked, next);
        Assert.Equal(5, invocations);
        var result = Assert.IsType<ContentResult>(blocked.Result);
        Assert.Equal(429, result.StatusCode);
        Assert.Contains("rate-limited", result.Content);
    }

    [Fact]
    public void BuildKey_UserAndClientStrategies()
    {
        var provider = Provider();
        var id = "11111111-1111-1111-1111-111111111111";
        Assert.Equal("settings:" + id, RateLimitFilter.BuildKey(RateLimitKeyStrategy.User, "settings", Context(provider, User(id)), provider));

        var ipContext = Context(provider);
        ipContext.Connection.RemoteIpAddress = IPAddress.Parse("1.2.3.4");
        Assert.Equal("webhook:ip:1.2.3.4", RateLimitFilter.BuildKey(RateLimitKeyStrategy.ClientIdentity, "webhook", ipContext, provider));

        var proxied = Context(Provider(trustProxy: true), forwardedFor: "9.9.9.9, 1.1.1.1");
        Assert.Equal(
            "webhook:xff:9.9.9.9",
            RateLimitFilter.BuildKey(RateLimitKeyStrategy.ClientIdentity, "webhook", proxied, Provider(trustProxy: true)));
    }
}

// ---------------------------------------------------------------------------
// TMDB season ratings / next episode + IMDb charts through the stub transport.
// ---------------------------------------------------------------------------

public sealed class TmdbImdbTests : FetcherTestBase
{
    private static FakeHttpMessageHandler Json(string body)
        => new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

    [Fact]
    public async Task SeasonRatings_ParsesEpisodes_AndCaches()
    {
        var service = new TmdbRatingsService(NewFetcher(Json("""{"episodes":[{"episode_number":1,"vote_average":8.5,"vote_count":100}]}""")), () => new RatingsConfig { TmdbApiKey = "k", CacheTtlHours = 24 }, Clock);
        Assert.False(new TmdbRatingsService(NewFetcher(Json("{}")), () => new RatingsConfig()).IsConfigured);

        var ratings = await service.GetSeasonRatings("123", 1);
        Assert.NotNull(ratings);
        Assert.Equal(8.5, ratings![1].TmdbScore);
        Assert.Equal(100, ratings[1].TmdbVotes);
        Assert.Single(ratings[1].Entries);
    }

    [Fact]
    public async Task NextEpisode_PresentAndAbsent()
    {
        var service = new TmdbRatingsService(NewFetcher(Json("""{"next_episode_to_air":{"name":"Finale","air_date":"2026-05-01"}}""")), () => new RatingsConfig { TmdbApiKey = "k", CacheTtlHours = 24 }, Clock);
        var next = await service.GetNextEpisode("123");
        Assert.NotNull(next);
        Assert.Equal("Finale", next!.Name);
        Assert.Equal("2026-05-01", next.AirDate);

        var empty = new TmdbRatingsService(ColdFetcher(Json("""{}""")), () => new RatingsConfig { TmdbApiKey = "k", CacheTtlHours = 24 }, Clock);
        ExpireCacheEntries(TimeSpan.FromHours(6));
        Assert.Null(await empty.GetNextEpisode("123"));
    }

    [Fact]
    public async Task ImdbCharts_DisabledNull_ParsesNextData_EmptyDegradesToNull()
    {
        var disabled = new ImdbChartsService(NewFetcher(Json(string.Empty)), () => new RatingsConfig { EnableImdbCharts = false }, Clock);
        Assert.False(disabled.IsEnabled);
        Assert.Null(await disabled.GetTop250());

        const string html = """<html><script id="__NEXT_DATA__" type="application/json">{"props":{"pageProps":{"pageData":{"chartTitles":{"edges":[{"node":{"id":"tt0111161","titleText":{"text":"Drama"},"releaseYear":{"year":1994},"ratingsSummary":{"aggregateRating":9.3}}}]}}}}}</script></html>""";
        var enabled = new ImdbChartsService(NewFetcher(Json(html)), () => new RatingsConfig { EnableImdbCharts = true }, Clock);
        Assert.True(enabled.IsEnabled);
        var chart = await enabled.GetTop250();
        Assert.NotNull(chart);
        var entry = Assert.Single(chart!.Entries);
        Assert.Equal("1", entry.Rank);
        Assert.Equal("Drama", entry.Title);

        var broken = new ImdbChartsService(ColdFetcher(Json("<html>no chart here</html>")), () => new RatingsConfig { EnableImdbCharts = true }, Clock);
        ExpireCacheEntries(TimeSpan.FromHours(24));
        Assert.Null(await broken.GetTop250());
    }
}

// ---------------------------------------------------------------------------
// Seerr sessions: login flows, cookie application, expiry, deletion.
// ---------------------------------------------------------------------------

public sealed class SeerrSessionTests : TempDatabaseFixture
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));

    public SeerrSessionTests()
        : base("cov-seerr")
    {
    }

    private sealed class FakeQuickConnect : IQuickConnect
    {
        private readonly bool _authorized;

        public FakeQuickConnect(bool authorized = false)
        {
            _authorized = authorized;
        }

        public bool IsEnabled => true;

        public QuickConnectResult TryConnect(AuthorizationInfo authorizationInfo) => throw new NotSupportedException();

        public QuickConnectResult CheckRequestStatus(string secret) => throw new NotSupportedException();

        public Task<bool> AuthorizeRequest(Guid userId, string code) => Task.FromResult(_authorized);

        public AuthenticationResult GetAuthorizedRequest(string secret) => throw new NotSupportedException();
    }

    private sealed class QueuedSender
    {
        private readonly Queue<HttpResponseMessage> _responses = new();

        public QueuedSender Responds(HttpStatusCode status)
        {
            _responses.Enqueue(new HttpResponseMessage(status));
            return this;
        }

        public SeerrSender Sender() => (request, _, _) => Task.FromResult(
            _responses.TryDequeue(out var response)
                ? response
                : throw new InvalidOperationException("unexpected Seerr request to " + request.RequestUri));
    }

    private static SeerrConfig Config(int sessionTtlHours = 24)
        => new() { ServerUrl = "http://seerr:5055/", ApiKey = "seerr-key", SessionTtlHours = sessionTtlHours };

    private SeerrSessionService Service(QueuedSender sender, SecretBox box, SeerrConfig? config = null, IQuickConnect? quickConnect = null)
        => new(
            sender.Sender(),
            _db,
            quickConnect ?? new FakeQuickConnect(),
            box,
            () => config ?? Config(),
            NullLogger<SeerrSessionService>.Instance,
            _clock);

    private static SecretBox Box() => new(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void IsConfigured_RequiresUrlAndKey_ServerUrlTrimsSlash()
    {
        var sender = new QueuedSender();
        Assert.False(Service(sender, Box(), new SeerrConfig()).IsConfigured);
        var service = Service(sender, Box());
        Assert.True(service.IsConfigured);
        Assert.Equal("http://seerr:5055", service.ServerUrl);
    }

    [Fact]
    public async Task LoginWithPassword_RejectedAndAccepted()
    {
        var rejected = Service(new QueuedSender().Responds(HttpStatusCode.Unauthorized), Box());
        var denied = await rejected.LoginWithPassword("u1", "user", "wrong");
        Assert.False(denied.Success);
        Assert.Equal("seerr-rejected (401)", denied.Error);
        Assert.Null(rejected.GetSession("u1"));

        var accepted = Service(new QueuedSender().Responds(HttpStatusCode.OK).Responds(HttpStatusCode.OK), Box());
        var ok = await accepted.LoginWithPassword("u1", "user", "right");
        Assert.True(ok.Success);
        Assert.NotNull(accepted.GetSession("u1"));
    }

    [Fact]
    public async Task LoginWithQuickConnect_PendingWhenUnauthorized()
    {
        var sender = new QueuedSender();
        var service = Service(sender, Box(), quickConnect: new FakeQuickConnect(authorized: false));
        var result = await service.LoginWithQuickConnect(Guid.NewGuid().ToString(), "secret");
        Assert.False(result.Success);
        Assert.Equal("quickconnect-pending", result.Error);
    }

    [Fact]
    public void TryApplySession_AppliesDecryptsExpiresAndCaches()
    {
        var box = Box();
        var service = Service(new QueuedSender(), box);
        var payload = box.Protect("[{\"Name\":\"sid\",\"Value\":\"abc\"}]");
        Assert.NotNull(payload);
        _db.UpsertSeerrSession(new SeerrSessionRow("u1", payload!, _clock.NowMs, _clock.NowMs));

        using var first = new HttpRequestMessage();
        Assert.True(service.TryApplySession("u1", first));
        Assert.Equal("sid=abc", string.Join(";", first.Headers.GetValues("Cookie")));

        // Second hit rides the cookie cache (same header, no row read needed).
        using var second = new HttpRequestMessage();
        Assert.True(service.TryApplySession("u1", second));

        using var unknown = new HttpRequestMessage();
        Assert.False(service.TryApplySession("ghost", unknown));

        Assert.True(service.DeleteSession("u1"));
        Assert.False(service.DeleteSession("u1"));
        Assert.Null(service.GetSession("u1"));
    }

    [Fact]
    public void TryApplySession_ExpiredSession_IsDropped()
    {
        var box = Box();
        var service = Service(new QueuedSender(), box, Config(sessionTtlHours: 1));
        var payload = box.Protect("[{\"Name\":\"sid\",\"Value\":\"abc\"}]");
        Assert.NotNull(payload);
        _db.UpsertSeerrSession(new SeerrSessionRow("u1", payload!, _clock.NowMs, _clock.NowMs));

        _clock.Advance(TimeSpan.FromHours(2));
        using var request = new HttpRequestMessage();
        Assert.False(service.TryApplySession("u1", request));
        Assert.Null(service.GetSession("u1"));
    }

    [Fact]
    public void TryApplySession_UndecryptablePayload_IsDropped()
    {
        var service = Service(new QueuedSender(), Box());
        var otherBox = Box();
        var payload = otherBox.Protect("[{\"Name\":\"sid\",\"Value\":\"abc\"}]");
        Assert.NotNull(payload);
        _db.UpsertSeerrSession(new SeerrSessionRow("u1", payload!, _clock.NowMs, _clock.NowMs));

        using var request = new HttpRequestMessage();
        Assert.False(service.TryApplySession("u1", request));
        Assert.Null(service.GetSession("u1"));
    }
}

// ---------------------------------------------------------------------------
// SSE wait seams: timeouts, batch drains, subscription state.
// ---------------------------------------------------------------------------

public sealed class SseWaitTests
{
    [Fact]
    public async Task WaitForEvent_UnknownSubscriberOrTimeout_ReturnsNull()
    {
        var hub = new SseHub(NullLogger<SseHub>.Instance);
        Assert.False(hub.IsSubscribed(Guid.NewGuid()));
        Assert.Null(await hub.WaitForEventAsync(Guid.NewGuid(), TimeSpan.FromMilliseconds(10), CancellationToken.None));

        var sub = hub.Subscribe("u1", "events");
        try
        {
            Assert.True(hub.IsSubscribed(sub));
            Assert.Null(await hub.WaitForEventAsync(sub, TimeSpan.FromMilliseconds(20), CancellationToken.None));
        }
        finally
        {
            hub.Unsubscribe(sub);
        }

        Assert.False(hub.IsSubscribed(sub));
    }

    [Fact]
    public async Task WaitForEvents_DrainsBufferedBatch()
    {
        var hub = new SseHub(NullLogger<SseHub>.Instance);
        var sub = hub.Subscribe("u1", "events");
        try
        {
            hub.PublishAll("events", "a", "{}");
            hub.PublishAll("events", "b", "{}");
            var batch = await hub.WaitForEventsAsync(sub, TimeSpan.FromSeconds(2), CancellationToken.None);
            Assert.NotNull(batch);
            Assert.Equal(2, batch!.Count);
            Assert.Null(await hub.WaitForEventsAsync(sub, TimeSpan.FromMilliseconds(20), CancellationToken.None));
        }
        finally
        {
            hub.Unsubscribe(sub);
        }
    }
}
