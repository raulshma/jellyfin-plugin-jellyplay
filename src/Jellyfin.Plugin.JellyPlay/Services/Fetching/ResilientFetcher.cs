using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Services.Cache;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyPlay.Services.Fetching;

/// <summary>
/// Resilient fetch — the one pipeline every external HTTP source (TMDB,
/// MDBList, IMDb charts, Letterboxd, Fribb) goes through: file cache →
/// circuit breaker → fetch → parse, with one TTL policy (per call), one
/// spoofed browser user agent, and failure shaped as "no data" (null) —
/// never an exception leaking to a route. The one exception that propagates
/// is the caller's own cancellation (an <see cref="OperationCanceledException"/>
/// raised while <c>cancellationToken</c> is cancelled); a cancellation NOT
/// requested by the caller (e.g. an HttpClient timeout fault) is an ordinary
/// failure. Time is one <see cref="TimeProvider"/> (breaker timestamps), so
/// tests can pin it.
/// Services keep only their config Func, their parse functions and result
/// shaping; the fetch/cache/breaker choreography lives here.
/// </summary>
public sealed class ResilientFetcher
{
    /// <summary>User agent for sources that reject the default plugin client (scraped HTML pages).</summary>
    public const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36";

    /// <summary>TTL for cached "no data" markers when miss-caching is enabled.</summary>
    public static readonly TimeSpan DefaultMissTtl = TimeSpan.FromHours(2);

    private const string HttpClientName = "JellyPlayHttpClient";

    private readonly IHttpClientFactory _httpFactory;
    private readonly FileCacheStore _cache;
    private readonly ILogger<ResilientFetcher> _logger;
    private readonly TimeProvider _clock;

    public ResilientFetcher(IHttpClientFactory httpFactory, FileCacheStore cache, ILogger<ResilientFetcher> logger, TimeProvider? clock = null)
    {
        _httpFactory = httpFactory;
        _cache = cache;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Unix-milliseconds "now" from the fetcher's clock, for call sites that pre-check breakers.</summary>
    public long NowMs => _clock.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>
    /// Cached resilient fetch: returns the cached value while fresh (within
    /// <paramref name="ttl"/>), otherwise runs the fetch and caches a non-null
    /// result. With <paramref name="missCache"/> enabled, an attempted fetch
    /// that comes back empty additionally stores a short-lived miss marker so
    /// a failing source is not hammered on every request; a successful fetch
    /// clears the marker. A null fetch result is "no data, not a failure":
    /// nothing is cached.
    /// </summary>
    public async Task<T?> GetOrFetchAsync<T>(
        string cacheKey,
        TimeSpan ttl,
        Func<HttpClient, CancellationToken, Task<T?>> fetch,
        CircuitBreaker? breaker = null,
        bool missCache = false,
        TimeSpan? missTtl = null,
        LogLevel failureLogLevel = LogLevel.Warning,
        CancellationToken cancellationToken = default)
    {
        var cached = _cache.Get<T>(cacheKey, ttl);
        if (cached is not null)
        {
            return cached;
        }

        var missKey = $"{cacheKey}:miss";
        if (missCache && _cache.Get<bool>(missKey, missTtl ?? DefaultMissTtl))
        {
            return default;
        }

        var attempt = await TryFetchAsync(cacheKey, fetch, breaker, failureLogLevel, cancellationToken);
        if (missCache && attempt.Attempted)
        {
            if (attempt.Value is null)
            {
                _cache.Set(missKey, true);
            }
            else
            {
                _cache.Invalidate(missKey);
            }
        }

        if (attempt.Value is not null)
        {
            _cache.Set(cacheKey, attempt.Value);
        }

        return attempt.Value;
    }

    /// <summary>
    /// Uncached resilient fetch: breaker check → HTTP → error shaping. Returns
    /// default on failure or when the breaker is open — the caller decides what
    /// "no data" means. Use <see cref="GetOrFetchAsync{T}"/> when the result
    /// should be cached.
    /// </summary>
    public async Task<T?> FetchAsync<T>(
        string source,
        Func<HttpClient, CancellationToken, Task<T?>> fetch,
        CircuitBreaker? breaker = null,
        LogLevel failureLogLevel = LogLevel.Warning,
        CancellationToken cancellationToken = default)
        => (await TryFetchAsync(source, fetch, breaker, failureLogLevel, cancellationToken)).Value;

    private async Task<FetchAttempt<T>> TryFetchAsync<T>(
        string source,
        Func<HttpClient, CancellationToken, Task<T?>> fetch,
        CircuitBreaker? breaker,
        LogLevel failureLogLevel,
        CancellationToken cancellationToken)
    {
        var now = NowMs;
        if (breaker is not null && breaker.IsOpen(now))
        {
            _logger.LogDebug("{Source} circuit open; skipping fetch", source);
            return new FetchAttempt<T>(Attempted: false, Value: default);
        }

        try
        {
            var client = _httpFactory.CreateClient(HttpClientName);
            var value = await fetch(client, cancellationToken);
            // Re-read the clock after the round trip: the breaker open window
            // starts at the failure/success time, not before the request.
            breaker?.RecordSuccess(NowMs);
            return new FetchAttempt<T>(Attempted: true, Value: value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller gave up — propagate as-is: not an upstream failure,
            // so no breaker record, no log, and nothing cached downstream.
            throw;
        }
        catch (Exception ex)
        {
            breaker?.RecordFailure(NowMs);
            _logger.Log(failureLogLevel, ex, "{Source} fetch failed", source);
            return new FetchAttempt<T>(Attempted: true, Value: default);
        }
    }

    /// <summary>Whether a fetch was really attempted, so miss-markers are never written from an open breaker.</summary>
    private readonly record struct FetchAttempt<T>(bool Attempted, T? Value);

    /// <summary>GET with the spoofed browser user agent the scraped pages require (IMDb, Letterboxd, AnimeFillerList).</summary>
    public static HttpRequestMessage BrowserGetRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
        return request;
    }
}
