using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
/// Cold fetches are single-flight: concurrent callers for the same cache key
/// share one upstream round trip (the in-flight slot is cleared when the
/// shared task settles so a failure always lets the next caller retry).
/// Services keep only their config Func, their parse functions and result
/// shaping; the fetch/cache/breaker choreography lives here.
/// </summary>
public sealed class ResilientFetcher
{
    /// <summary>User agent for sources that reject the default plugin client (scraped HTML pages).</summary>
    public const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36";

    /// <summary>TTL for cached "no data" markers when miss-caching is enabled. Shared with services that hand-roll aggregate miss markers.</summary>
    public static readonly TimeSpan DefaultMissTtl = TimeSpan.FromHours(2);

    private const string HttpClientName = "JellyPlayHttpClient";

    private readonly IHttpClientFactory _httpFactory;
    private readonly FileCacheStore _cache;
    private readonly ILogger<ResilientFetcher> _logger;
    private readonly TimeProvider _clock;

    /// <summary>Single-flight slots: cache key → the one in-flight fetch. Joiners await the flight's completion gate — never a lock, never a blocked thread.</summary>
    private readonly ConcurrentDictionary<string, Flight> _inFlight = new(StringComparer.Ordinal);

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
    /// Concurrent cold callers for the same key coalesce into one fetch.
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
        var probe = await ProbeCacheAsync<T>(cacheKey, ttl, missCache, missTtl).ConfigureAwait(false);
        if (probe.Kind != CacheProbeKind.NotFound)
        {
            return probe.Value; // Hit carries the value; MissMarker shapes as null
        }

        var missKey = $"{cacheKey}:miss";
        return await RunSingleFlightAsync<T>(
            cacheKey,
            cancellationToken,
            cancellation => FetchAndCacheAsync(cacheKey, fetch, breaker, missCache, missKey, failureLogLevel, cancellation)).ConfigureAwait(false);
    }

    /// <summary>
    /// Multi-source cached fetch: several upstreams whose results merge into
    /// ONE cached aggregate under a single key (the anime markers shape).
    /// Each source runs through its own breaker — an open breaker means that
    /// source is simply not attempted, never a poisoned cache. The merged
    /// result is cached when non-null; an empty merge memoizes the miss only
    /// when at least one source was really attempted — the same rule
    /// <see cref="GetOrFetchAsync{T}"/> encodes as <c>FetchAttempt.Attempted</c>,
    /// so services hand-rolling the dance is a thing of the past. Sources run
    /// concurrently; the merge sees them in the order given.
    /// </summary>
    /// <typeparam name="TSource">One upstream's raw result.</typeparam>
    /// <typeparam name="TValue">The merged, cached aggregate.</typeparam>
    /// <param name="merge">Pure result shaping: per-source values (null = that source failed or was skipped) → the aggregate, or null for "no data".</param>
    public async Task<TValue?> GetOrFetchMultiAsync<TSource, TValue>(
        string cacheKey,
        TimeSpan ttl,
        IReadOnlyList<MultiSource<TSource>> sources,
        Func<IReadOnlyList<TSource?>, TValue?> merge,
        bool missCache = false,
        TimeSpan? missTtl = null,
        CancellationToken cancellationToken = default)
    {
        var probe = await ProbeCacheAsync<TValue>(cacheKey, ttl, missCache, missTtl).ConfigureAwait(false);
        if (probe.Kind != CacheProbeKind.NotFound)
        {
            return probe.Value;
        }

        var missKey = $"{cacheKey}:miss";
        return await RunSingleFlightAsync<TValue>(
            cacheKey,
            cancellationToken,
            cancellation => FetchMultiAndCacheAsync(cacheKey, sources, merge, missCache, missKey, cancellation)).ConfigureAwait(false);
    }

    /// <summary>
    /// Single-flight outer helper for already-resolved aggregates (custom-row
    /// matches, anime marker sets): the outer cache lives here, behind the same
    /// probe + single-flight seam as the HTTP fetches, so services no longer
    /// hand-roll Get-then-compute-then-Set (and its double-probe for a shared
    /// key). The flight slot is namespaced ("resolved:") apart from the inner
    /// fetch flights for the same cache key — the resolve closure may itself
    /// call <see cref="GetOrFetchMultiAsync{TSource,TValue}"/> for that key
    /// (the anime shape) without reentering its own slot. A null resolve is
    /// "no data": nothing cached, and — unlike the HTTP fetches — no miss
    /// marker is written here (the inner multi owns the Attempted rule, so an
    /// all-breakers-open outer never poisons the miss window); a non-null
    /// resolve caches and clears any miss marker the inner left behind.
    /// Failure shaping matches the pipeline: caller cancellation propagates,
    /// any other fault is "no data" (null).
    /// </summary>
    public async Task<T?> GetOrFetchResolvedAsync<T>(
        string cacheKey,
        TimeSpan ttl,
        Func<CancellationToken, Task<T?>> resolve,
        bool missCache = false,
        TimeSpan? missTtl = null,
        CancellationToken cancellationToken = default)
    {
        var probe = await ProbeCacheAsync<T>(cacheKey, ttl, missCache, missTtl).ConfigureAwait(false);
        if (probe.Kind != CacheProbeKind.NotFound)
        {
            return probe.Value;
        }

        var flightKey = $"resolved:{cacheKey}";
        var missKey = $"{cacheKey}:miss";
        return await RunSingleFlightAsync<T>(
            flightKey,
            cancellationToken,
            async cancellation =>
            {
                var value = await resolve(cancellation);
                if (value is not null)
                {
                    await _cache.SetAsync(cacheKey, value).ConfigureAwait(false);
                    if (missCache)
                    {
                        _cache.Invalidate(missKey);
                    }
                }

                return value;
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// The shared cache-probe prologue of <see cref="GetOrFetchAsync{T}"/> and
    /// <see cref="GetOrFetchMultiAsync{TSource,TValue}"/>: one collapsed
    /// value+miss probe per request (the store resolves each key once), where
    /// a fresh cached value or a live miss marker short-circuits the fetch.
    /// NotFound means the caller runs the single-flight fetch.
    /// </summary>
    private Task<CacheProbe<TValue>> ProbeCacheAsync<TValue>(
        string cacheKey,
        TimeSpan ttl,
        bool missCache,
        TimeSpan? missTtl)
        => _cache.TryGetAsync<TValue>(cacheKey, missCache ? $"{cacheKey}:miss" : null, ttl, missTtl ?? DefaultMissTtl);

    /// <summary>One upstream in a <see cref="GetOrFetchMultiAsync{TSource,TValue}"/> call: its own logging/breaker identity, factory and failure level.</summary>
    public sealed record MultiSource<TSource>(
        string Name,
        Func<HttpClient, CancellationToken, Task<TSource?>> Fetch,
        CircuitBreaker? Breaker = null,
        LogLevel FailureLogLevel = LogLevel.Warning);

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

    /// <summary>
    /// Joins (or starts) the single in-flight fetch for <paramref name="cacheKey"/>
    /// and shapes its outcome per caller. The winner runs the pipeline with its
    /// own token; joiners await a completion gate (asynchronously — the
    /// pipeline's synchronous prefix must never make a joiner block a thread).
    /// The shared outcome carries its own exception: a caller whose token was
    /// cancelled propagates as-is; any other fault (e.g. the first caller's
    /// cancellation) is this caller's "no data" — never an exception leaking
    /// to a route. The slot is removed once the shared task settles and only
    /// while it is still the registered one: a success keeps its result in the
    /// cache until TTL expiry, a fault must clear so the next caller retries.
    /// </summary>
    private async Task<T?> RunSingleFlightAsync<T>(string cacheKey, CancellationToken cancellationToken, Func<CancellationToken, Task<T?>> start)
    {
        while (true)
        {
            if (_inFlight.TryGetValue(cacheKey, out var flight))
            {
                return await AwaitSharedFlightAsync<T>(cacheKey, flight, cancellationToken);
            }

            var mine = new Flight();
            if (!_inFlight.TryAdd(cacheKey, mine))
            {
                continue; // lost the start race — join the winner on the next round
            }

            try
            {
                var value = await start(cancellationToken);
                mine.Settled.SetResult(value);
                return value;
            }
            catch (OperationCanceledException ex)
            {
                mine.Settled.SetException(ex);
                throw;
            }
            catch (Exception ex)
            {
                mine.Settled.SetException(ex);
                return default;
            }
            finally
            {
                _inFlight.TryRemove(new KeyValuePair<string, Flight>(cacheKey, mine));
            }
        }
    }

    private async Task<T?> AwaitSharedFlightAsync<T>(string cacheKey, Flight flight, CancellationToken cancellationToken)
    {
        try
        {
            var shared = await flight.Settled.Task.ConfigureAwait(false);
            return (T?)shared;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return default;
        }
        finally
        {
            _inFlight.TryRemove(new KeyValuePair<string, Flight>(cacheKey, flight));
        }
    }

    /// <summary>One in-flight fetch: the winner's outcome, fanned out to joiners asynchronously.</summary>
    private sealed class Flight
    {
        public readonly TaskCompletionSource<object?> Settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private async Task<T?> FetchAndCacheAsync<T>(
        string cacheKey,
        Func<HttpClient, CancellationToken, Task<T?>> fetch,
        CircuitBreaker? breaker,
        bool missCache,
        string missKey,
        LogLevel failureLogLevel,
        CancellationToken cancellationToken)
    {
        var attempt = await TryFetchAsync(cacheKey, fetch, breaker, failureLogLevel, cancellationToken);
        if (missCache && attempt.Attempted)
        {
            if (attempt.Value is null)
            {
                await _cache.SetAsync(missKey, true).ConfigureAwait(false);
            }
            else
            {
                _cache.Invalidate(missKey);
            }
        }

        if (attempt.Value is not null)
        {
            await _cache.SetAsync(cacheKey, attempt.Value).ConfigureAwait(false);
        }

        return attempt.Value;
    }

    private async Task<TValue?> FetchMultiAndCacheAsync<TSource, TValue>(
        string cacheKey,
        IReadOnlyList<MultiSource<TSource>> sources,
        Func<IReadOnlyList<TSource?>, TValue?> merge,
        bool missCache,
        string missKey,
        CancellationToken cancellationToken)
    {
        var flights = new Task<FetchAttempt<TSource>>[sources.Count];
        for (var index = 0; index < sources.Count; index++)
        {
            var source = sources[index];
            flights[index] = TryFetchAsync(source.Name, source.Fetch, source.Breaker, source.FailureLogLevel, cancellationToken);
        }

        var attempts = await Task.WhenAll(flights);
        var attempted = false;
        var values = new List<TSource?>(attempts.Length);
        foreach (var attempt in attempts)
        {
            attempted |= attempt.Attempted;
            values.Add(attempt.Value);
        }

        var merged = merge(values);
        if (missCache)
        {
            if (merged is null)
            {
                // Never memoize a miss when no source was really attempted —
                // an open breaker must not poison the cache.
                if (attempted)
                {
                    await _cache.SetAsync(missKey, true).ConfigureAwait(false);
                }
            }
            else
            {
                _cache.Invalidate(missKey);
            }
        }

        if (merged is not null)
        {
            await _cache.SetAsync(cacheKey, merged).ConfigureAwait(false);
        }

        return merged;
    }

    private async Task<FetchAttempt<T>> TryFetchAsync<T>(
        string source,
        Func<HttpClient, CancellationToken, Task<T?>> fetch,
        CircuitBreaker? breaker,
        LogLevel failureLogLevel,
        CancellationToken cancellationToken)
    {
        if (breaker is not null && breaker.IsOpen())
        {
            _logger.LogDebug("{Source} circuit open; skipping fetch", source);
            return new FetchAttempt<T>(Attempted: false, Value: default);
        }

        try
        {
            var client = _httpFactory.CreateClient(HttpClientName);
            var value = await fetch(client, cancellationToken);
            // The breaker timestamps the success itself (its own clock), so
            // the open window starts at completion time, not before the request.
            breaker?.RecordSuccess();
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
            breaker?.RecordFailure();
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
