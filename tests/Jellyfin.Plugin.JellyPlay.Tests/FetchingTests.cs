using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Configuration;
using Jellyfin.Plugin.JellyPlay.Services.Anime;
using Jellyfin.Plugin.JellyPlay.Services.Cache;
using Jellyfin.Plugin.JellyPlay.Services.Fetching;
using Jellyfin.Plugin.JellyPlay.Services.Ratings;
using Jellyfin.Plugin.JellyPlay.Services.Rows;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.JellyPlay.Tests;

/// <summary>Pins TimeProvider so breaker windows advance only when the test advances them.</summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan by) => _utcNow += by;

    public override DateTimeOffset GetUtcNow() => _utcNow;
}

public sealed class CaptureLogger : ILogger<ResilientFetcher>
{
    public List<(LogLevel Level, Exception? Exception, string Message)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, exception, formatter(state, exception)));
}

public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; }

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        Responder = responder;
    }

    public List<string> RequestedUrls { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestedUrls.Add(request.RequestUri?.ToString() ?? string.Empty);
        return Task.FromResult(Responder(request));
    }
}

public sealed class FakeHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public FakeHttpClientFactory(HttpMessageHandler handler)
    {
        _handler = handler;
    }

    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}

public sealed record SamplePayload(string Value);

/// <summary>Shared scaffolding: a real file cache under a temp dir plus a pinnable clock.</summary>
public abstract class FetcherTestBase : IDisposable
{
    protected readonly string TempDir = Path.Combine(Path.GetTempPath(), "jellyplay-fetch-" + Guid.NewGuid().ToString("N"));

    protected FetcherTestBase()
    {
        Directory.CreateDirectory(TempDir);
        Cache = new FileCacheStore(NullLogger<FileCacheStore>.Instance, TempDir, () => 256);
    }

    protected FileCacheStore Cache { get; }

    protected FakeTimeProvider Clock { get; } = new();

    /// <summary>Unix-milliseconds "now" from the test clock, matching ResilientFetcher.NowMs.</summary>
    protected long Now => Clock.GetUtcNow().ToUnixTimeMilliseconds();

    protected ResilientFetcher NewFetcher(HttpMessageHandler handler, ILogger<ResilientFetcher>? logger = null)
        => new(new FakeHttpClientFactory(handler), Cache, logger ?? NullLogger<ResilientFetcher>.Instance, Clock);

    /// <summary>
    /// A fetcher over a FRESH memory table on the same cache files: within its
    /// ~30s memory window a hot value is served on the entry's own timestamp,
    /// so tests that backdate files to prove TTL expiry must start from a cold
    /// table (the restart-equivalent) for the file time to become the truth.
    /// </summary>
    protected ResilientFetcher ColdFetcher(HttpMessageHandler handler)
        => new(
            new FakeHttpClientFactory(handler),
            new FileCacheStore(NullLogger<FileCacheStore>.Instance, TempDir, () => 256),
            NullLogger<ResilientFetcher>.Instance,
            Clock);

    /// <summary>Backdates every cache file beyond ttl — FileCacheStore expiry keys off file write time, so tests age files directly.</summary>
    protected void ExpireCacheEntries(TimeSpan ttl)
    {
        var stale = DateTimeOffset.UtcNow - ttl - TimeSpan.FromMinutes(1);
        foreach (var file in Directory.GetFiles(TempDir, "*.json"))
        {
            File.SetLastWriteTimeUtc(file, stale.UtcDateTime);
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(TempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private protected static HttpResponseMessage Text(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
}

public class CircuitBreakerTests
{
    [Fact]
    public void Opens_AfterThreshold_AndCloses_AfterWindow()
    {
        var clock = new FakeTimeProvider();
        var breaker = new CircuitBreaker(failureThreshold: 2, openWindow: TimeSpan.FromSeconds(1), clock: clock);

        breaker.RecordFailure();
        Assert.False(breaker.IsOpen());

        breaker.RecordFailure();
        Assert.True(breaker.IsOpen());

        // Still open inside the window.
        clock.Advance(TimeSpan.FromMilliseconds(900));
        Assert.True(breaker.IsOpen());

        // After the window, a trial is allowed.
        clock.Advance(TimeSpan.FromMilliseconds(600));
        Assert.False(breaker.IsOpen());

        // Success resets the counter.
        breaker.RecordSuccess();
        Assert.False(breaker.IsOpen());
    }
}

public class ResilientFetcherTests : FetcherTestBase
{
    private static async Task<SamplePayload?> FetchBodyAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var body = await client.GetStringAsync("http://unit.test/thing", cancellationToken);
        return new SamplePayload(body.Trim());
    }

    [Fact]
    public async Task CacheHit_SkipsFetch()
    {
        await Cache.SetAsync("k", new SamplePayload("cached"));
        var handler = new FakeHttpMessageHandler(_ => Text("fresh"));
        var fetcher = NewFetcher(handler);

        var result = await fetcher.GetOrFetchAsync("k", TimeSpan.FromHours(1), FetchBodyAsync);

        Assert.Equal("cached", result?.Value);
        Assert.Empty(handler.RequestedUrls);
    }

    [Fact]
    public async Task TtlExpiry_Refetches_AndRecaches()
    {
        var handler = new FakeHttpMessageHandler(_ => Text("first"));
        var fetcher = NewFetcher(handler);

        var first = await fetcher.GetOrFetchAsync("k", TimeSpan.FromHours(1), FetchBodyAsync);
        ExpireCacheEntries(TimeSpan.FromHours(1));
        var second = await ColdFetcher(handler).GetOrFetchAsync("k", TimeSpan.FromHours(1), FetchBodyAsync);

        Assert.Equal("first", first?.Value);
        Assert.Equal("first", second?.Value);
        Assert.Equal(2, handler.RequestedUrls.Count);
    }

    [Fact]
    public async Task FetchFailure_RecordsBreaker_ReturnsDefault_LogsWarning()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("boom"));
        var breaker = new CircuitBreaker(failureThreshold: 1, clock: Clock);
        var logger = new CaptureLogger();
        var fetcher = NewFetcher(handler, logger);

        var result = await fetcher.GetOrFetchAsync("k", TimeSpan.FromHours(1), FetchBodyAsync, breaker);

        Assert.Null(result);
        Assert.True(breaker.IsOpen());
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Exception is HttpRequestException);
    }

    [Fact]
    public async Task CallerCancellation_Propagates_WithoutBreakerRecord()
    {
        using var cts = new CancellationTokenSource();
        var handler = new FakeHttpMessageHandler(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        var breaker = new CircuitBreaker(failureThreshold: 1, clock: Clock);
        var logger = new CaptureLogger();
        var fetcher = NewFetcher(handler, logger);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => fetcher.GetOrFetchAsync("k", TimeSpan.FromHours(1), FetchBodyAsync, breaker, cancellationToken: cts.Token));

        // The caller's own cancellation is not an upstream failure: no breaker
        // record, nothing logged (and nothing cached, since the fetch never returns).
        Assert.False(breaker.IsOpen());
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task TimeoutStyleCancellation_WithoutCallerCancellation_CountsAsFailure()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new OperationCanceledException("simulated HttpClient timeout fault"));
        var breaker = new CircuitBreaker(failureThreshold: 1, clock: Clock);
        var logger = new CaptureLogger();
        var fetcher = NewFetcher(handler, logger);

        var result = await fetcher.GetOrFetchAsync("k", TimeSpan.FromHours(1), FetchBodyAsync, breaker, cancellationToken: CancellationToken.None);

        // A cancellation the caller did not request (e.g. an HttpClient timeout
        // fault) stays an ordinary failure: breaker record + log + null.
        Assert.Null(result);
        Assert.True(breaker.IsOpen());
        Assert.Equal(1, handler.RequestedUrls.Count);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Exception is OperationCanceledException);
    }

    [Fact]
    public async Task BreakerOpen_ShortCircuits_WithoutHttpCall()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("boom"));
        var breaker = new CircuitBreaker(failureThreshold: 1, clock: Clock);
        var fetcher = NewFetcher(handler);

        Assert.Null(await fetcher.GetOrFetchAsync("k", TimeSpan.FromHours(1), FetchBodyAsync, breaker));
        Assert.Equal(1, handler.RequestedUrls.Count);

        // Inside the open window: "no data" without touching the dead upstream.
        Assert.Null(await fetcher.GetOrFetchAsync("k", TimeSpan.FromHours(1), FetchBodyAsync, breaker));
        Assert.Equal(1, handler.RequestedUrls.Count);

        // After the window elapses the trial call goes out again.
        Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Null(await fetcher.GetOrFetchAsync("k", TimeSpan.FromHours(1), FetchBodyAsync, breaker));
        Assert.Equal(2, handler.RequestedUrls.Count);
    }

    [Fact]
    public async Task MissCache_ShortCircuits_AfterFailure_UntilExpiry()
    {
        var handler = new FakeHttpMessageHandler(_ => Text("payload"));
        var fetcher = NewFetcher(handler);

        // Success populates the cache and keeps no miss marker.
        var hit = await fetcher.GetOrFetchAsync("k", TimeSpan.FromHours(1), FetchBodyAsync, missCache: true, missTtl: TimeSpan.FromHours(2));
        Assert.Equal("payload", hit?.Value);

        // Expire the positive entry, then fail: the miss marker absorbs the retries.
        ExpireCacheEntries(TimeSpan.FromHours(1));
        handler.Responder = _ => throw new HttpRequestException("down");
        var cold = ColdFetcher(handler);
        Assert.Null(await cold.GetOrFetchAsync("k", TimeSpan.FromHours(1), FetchBodyAsync, missCache: true, missTtl: TimeSpan.FromHours(2)));
        Assert.Equal(2, handler.RequestedUrls.Count);

        handler.Responder = _ => Text("recovered");
        Assert.Null(await cold.GetOrFetchAsync("k", TimeSpan.FromHours(1), FetchBodyAsync, missCache: true, missTtl: TimeSpan.FromHours(2)));
        Assert.Equal(2, handler.RequestedUrls.Count); // miss marker — no HTTP call

        // After the miss TTL passes, the source is tried again and recovers.
        ExpireCacheEntries(TimeSpan.FromHours(2));
        var recovered = await ColdFetcher(handler).GetOrFetchAsync("k", TimeSpan.FromHours(1), FetchBodyAsync, missCache: true, missTtl: TimeSpan.FromHours(2));
        Assert.Equal("recovered", recovered?.Value);
        Assert.Equal(3, handler.RequestedUrls.Count);
    }

    [Fact]
    public async Task MissCache_EmptyResult_AlsoCachesMiss()
    {
        var handler = new FakeHttpMessageHandler(_ => Text("200 but no data"));
        var fetcher = NewFetcher(handler);

        // A source can answer "no data" (null) on a successful HTTP round trip.
        async Task<SamplePayload?> NoData(HttpClient client, CancellationToken cancellationToken)
        {
            await client.GetStringAsync("http://unit.test/nothing", cancellationToken);
            return null;
        }

        Assert.Null(await fetcher.GetOrFetchAsync("k", TimeSpan.FromHours(1), NoData, missCache: true));
        Assert.Null(await fetcher.GetOrFetchAsync("k", TimeSpan.FromHours(1), NoData, missCache: true));

        // One HTTP call: the second request short-circuited on the miss marker.
        Assert.Equal(1, handler.RequestedUrls.Count);
    }

    [Fact]
    public async Task ConcurrentColdFetches_CoalesceIntoOneUpstreamHit()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHttpMessageHandler(_ =>
        {
            entered.TrySetResult();
            release.Task.Wait(); // test's main thread releases after the second caller joins
            return Text("shared");
        });
        var fetcher = NewFetcher(handler);

        // The responder blocks on `release`; run the first caller on the pool so
        // the test's main thread stays free to release it (the sync handler would
        // otherwise run inline on this thread and deadlock the Wait).
        var first = Task.Run(() => fetcher.GetOrFetchAsync("k", TimeSpan.FromHours(1), FetchBodyAsync));
        await entered.Task; // the fetch is in flight
        var second = fetcher.GetOrFetchAsync("k", TimeSpan.FromHours(1), FetchBodyAsync);
        await Task.Delay(50); // let the second caller join the shared flight
        release.TrySetResult();

        var results = await Task.WhenAll(first, second);

        Assert.Equal("shared", results[0]?.Value);
        Assert.Equal("shared", results[1]?.Value);
        Assert.Equal(1, handler.RequestedUrls.Count); // two cold callers, one upstream hit
    }

    [Fact]
    public async Task FailedFetch_ClearsInFlightSlot_NextCallerRetries()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("down"));
        var fetcher = NewFetcher(handler);

        // A faulted flight must not stick: the failure is shaped as null and
        // the slot clears, so the next cold caller retries the source.
        Assert.Null(await fetcher.GetOrFetchAsync("k", TimeSpan.FromHours(1), FetchBodyAsync));
        Assert.Equal(1, handler.RequestedUrls.Count);

        handler.Responder = _ => Text("recovered");
        var recovered = await fetcher.GetOrFetchAsync("k", TimeSpan.FromHours(1), FetchBodyAsync);

        Assert.Equal("recovered", recovered?.Value);
        Assert.Equal(2, handler.RequestedUrls.Count);
    }
}

public class MdbListServiceTests : FetcherTestBase
{
    private MdbListService NewService(FakeHttpMessageHandler handler, string apiKey = "key")
        => new(NewFetcher(handler), Cache, () => new RatingsConfig { MdbListApiKey = apiKey, CacheTtlHours = 24 });

    [Fact]
    public async Task GetRatings_Unconfigured_ReturnsNull_WithoutHttp()
    {
        var handler = new FakeHttpMessageHandler(_ => Text("{}"));
        var service = NewService(handler, apiKey: string.Empty);

        Assert.Null(await service.GetRatings("tt0111161"));
        Assert.Empty(handler.RequestedUrls);
    }

    [Fact]
    public async Task GetRatings_GoodJson_ParsesEntries_AndCaches()
    {
        const string json = """{"ratings":[{"source":"imdb","value":8.0,"votes":100,"url":"https://imdb.com/title/tt0111161"}]}""";
        var handler = new FakeHttpMessageHandler(_ => Text(json));
        var service = NewService(handler);

        var result = await service.GetRatings("tt0111161");

        Assert.NotNull(result);
        var entry = Assert.Single(result.Ratings);
        Assert.Equal("imdb", entry.Source);
        Assert.Equal(8.0, entry.Score);
        Assert.Equal(100, entry.Votes);
        Assert.Equal("https://imdb.com/title/tt0111161", entry.Url);

        // Second call is served from the file cache — one HTTP request total.
        await service.GetRatings("tt0111161");
        Assert.Equal(1, handler.RequestedUrls.Count);
    }

    [Fact]
    public async Task GetRatings_BadJson_TripsBreaker_AfterThreshold()
    {
        var handler = new FakeHttpMessageHandler(_ => Text("<html>not json</html>"));
        var service = NewService(handler); // default breaker: 3 consecutive failures

        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.Null(await service.GetRatings("tt0111161"));
        }

        Assert.Equal(3, handler.RequestedUrls.Count);

        // Breaker open: "no data" without touching the dead upstream.
        Assert.Null(await service.GetRatings("tt0111161"));
        Assert.Equal(3, handler.RequestedUrls.Count);
    }
}

public class CustomRowsServiceTests : FetcherTestBase
{
    private CustomRowsService NewService(FakeHttpMessageHandler handler)
        => new(
            NewFetcher(handler),
            Cache,
            libraryManager: null!, // FindLocalItem catches the null-deref and degrades to LocalItemId = null
            () => new RatingsConfig { TmdbApiKey = "tmdb-key", MdbListApiKey = "mdblist-key", CacheTtlHours = 24 },
            NullLogger<CustomRowsService>.Instance);

    [Fact]
    public async Task ResolveAsync_Letterboxd_ParsesThroughFetcher_AndCaches()
    {
        const string html = """
            <ul class="film-list">
              <li class="posteritem">
                <img alt="Seven Samurai (1954)" src="/x.jpg"><a href="/film/seven-samurai/"></a>
              </li>
            </ul>
            """;
        var handler = new FakeHttpMessageHandler(_ => Text(html));
        var service = NewService(handler);
        var row = new CustomRowDefinition { Title = "Essentials", Source = "letterboxd", ListId = "wishlist/essentials", Limit = 20 };

        var result = await service.ResolveAsync(row);

        Assert.NotNull(result);
        var item = Assert.Single(result.Items);
        Assert.Equal("Seven Samurai", item.Title);
        Assert.Equal("1954", item.Year);
        Assert.Null(item.LocalItemId); // no library in the test host — unresolved entries pass through
        Assert.Equal("https://letterboxd.com/wishlist/essentials/", handler.RequestedUrls[0]);

        // Second resolve is served from the cache — one HTTP request total.
        await service.ResolveAsync(row);
        Assert.Equal(1, handler.RequestedUrls.Count);
    }

    [Fact]
    public async Task ResolveAsync_RepeatedFailures_MemoizeMiss_AndStopHttpCalls()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("refused"));
        var service = NewService(handler);
        var row = new CustomRowDefinition { Title = "Essentials", Source = "letterboxd", ListId = "essentials", Limit = 20 };

        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.Null(await service.ResolveAsync(row));
        }

        // Miss-cached: the first failure memoizes a miss marker, so the
        // retries never leave the machine — one upstream hit total.
        Assert.Equal(1, handler.RequestedUrls.Count);

        Assert.Null(await service.ResolveAsync(row));
        Assert.Equal(1, handler.RequestedUrls.Count); // miss marker holds — the fourth resolve never leaves the machine
    }
}

public class AnimeMarkersServiceTests : FetcherTestBase
{
    private static HttpResponseMessage FribbOrFiller(HttpRequestMessage request, string fillerHtml)
        => request.RequestUri?.Host == "raw.githubusercontent.com" ? Text("[]") : Text(fillerHtml);

    private AnimeMarkersService NewService(FakeHttpMessageHandler handler, AnimeConfig config)
        => new(
            NewFetcher(handler),
            Cache,
            libraryManager: null!, // series id "not-a-guid" fails Guid.Parse first; the library is never touched
            () => config,
            NullLogger<AnimeMarkersService>.Instance);

    [Fact]
    public async Task GetSeriesMarkers_FillerSource_Parses_AndCaches()
    {
        const string html = """
            <table class="episode-table">
              <tr><td><a>1</a></td><td class="episode-table-type">Canon</td></tr>
              <tr><td><a>2</a></td><td class="episode-table-type">Filler</td></tr>
            </table>
            """;
        var handler = new FakeHttpMessageHandler(request => FribbOrFiller(request, html));
        var service = NewService(handler, new AnimeConfig { EnableFillerList = true, EnableTenrai = false, RefreshIntervalHours = 1 });

        var markers = await service.GetSeriesMarkers("not-a-guid", "one-piece");

        Assert.NotNull(markers);
        Assert.Equal("one-piece", markers.AniListId); // the explicit hint is the source id when nothing maps
        Assert.Equal(2, markers.Markers.Count);
        Assert.Equal(("canon", 1), (markers.Markers[0].Type, markers.Markers[0].EpisodeNumber));
        Assert.Equal(("filler", 2), (markers.Markers[1].Type, markers.Markers[1].EpisodeNumber));

        // Second call is served from the markers cache — fribb + one scrape only.
        await service.GetSeriesMarkers("not-a-guid", "one-piece");
        Assert.Equal(2, handler.RequestedUrls.Count);
    }

    [Fact]
    public async Task GetSeriesMarkers_EmptyResults_CacheMiss_SkipsRefetch()
    {
        var handler = new FakeHttpMessageHandler(request => FribbOrFiller(request, "<html><body>no episode table here</body></html>"));
        var service = NewService(handler, new AnimeConfig { EnableFillerList = true, EnableTenrai = false, RefreshIntervalHours = 1 });

        // The scrape yields zero rows → counted as a failure → the aggregate "miss" is cached.
        Assert.Null(await service.GetSeriesMarkers("not-a-guid", "obscure-show"));
        Assert.Null(await service.GetSeriesMarkers("not-a-guid", "obscure-show"));

        // Fribb + one scrape: the miss marker absorbed the second request.
        Assert.Equal(2, handler.RequestedUrls.Count);
    }
}
