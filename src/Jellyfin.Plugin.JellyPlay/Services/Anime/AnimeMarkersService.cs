using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Configuration;
using Jellyfin.Plugin.JellyPlay.Services.Cache;
using Jellyfin.Plugin.JellyPlay.Services.Fetching;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyPlay.Services.Anime;

/// <summary>Marker kinds served to clients for episode badges.</summary>
public sealed record AnimeMarker(string Type, int EpisodeNumber, string? Note);

public sealed record SeriesMarkers(string SeriesId, string? AniListId, string? MalId, IReadOnlyList<AnimeMarker> Markers);

/// <summary>
/// One Fribb anime-list row: the cross-source id mapping (anilist/mal/thetvdb/tmdb).
/// </summary>
internal sealed record FribbEntry(string? AniListId, string? MalId, string? TvdbId, string? TmdbId);

/// <summary>
/// By-kind alias index over the Fribb anime list (anilist/mal/tvdb/tmdb id →
/// entry), built once per fetched list: the O(1) replacement for the linear
/// four-way scan <see cref="AnimeIdResolver.FindMapping"/> used to run per
/// request over tens of thousands of rows. Duplicate ids keep the FIRST
/// entry — the linear scan's FirstOrDefault semantics.
/// </summary>
internal sealed class FribbAliasIndex
{
    private readonly Dictionary<string, FribbEntry> _anilist = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FribbEntry> _mal = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FribbEntry> _tvdb = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FribbEntry> _tmdb = new(StringComparer.Ordinal);

    public FribbAliasIndex(IEnumerable<FribbEntry> entries)
    {
        foreach (var entry in entries)
        {
            Add(_anilist, entry.AniListId);
            Add(_mal, entry.MalId);
            Add(_tvdb, entry.TvdbId);
            Add(_tmdb, entry.TmdbId);

            void Add(Dictionary<string, FribbEntry> map, string? id)
            {
                if (id is not null)
                {
                    map.TryAdd(id, entry);
                }
            }
        }
    }

    /// <summary>Probes by id kind in resolution order (anilist &gt; mal &gt; tvdb &gt; tmdb — the LookupKeys order).</summary>
    public FribbEntry? Find(AnimeProviderIds ids)
        => Probe(_anilist, ids.AniListId)
            ?? Probe(_mal, ids.MalId)
            ?? Probe(_tvdb, ids.TvdbId)
            ?? Probe(_tmdb, ids.TmdbId);

    private static FribbEntry? Probe(Dictionary<string, FribbEntry> map, string? id)
        => id is not null && map.TryGetValue(id, out var entry) ? entry : null;
}

/// <summary>
/// Provider ids of one anime series: whatever the library's metadata providers
/// attached (anilist/mal/tvdb/tmdb keys, any casing) plus the endpoint's
/// explicit providerSeriesId hint, if the caller passed one.
/// </summary>
public sealed record AnimeProviderIds(
    string? AniListId,
    string? MalId,
    string? TvdbId,
    string? TmdbId,
    string? ExplicitHint)
{
    /// <summary>Ids usable as Fribb lookup keys, in resolution order (the hint last — it is untyped).</summary>
    public IEnumerable<string> LookupKeys()
    {
        if (AniListId is not null)
        {
            yield return AniListId;
        }

        if (MalId is not null)
        {
            yield return MalId;
        }

        if (TvdbId is not null)
        {
            yield return TvdbId;
        }

        if (TmdbId is not null)
        {
            yield return TmdbId;
        }

        if (ExplicitHint is not null)
        {
            yield return ExplicitHint;
        }
    }
}

/// <summary>
/// Pure provider-id resolution for anime markers: the Jellyfin series id is
/// NOT a provider id — the real ids come from the item's ProviderIds (any of
/// anilist/mal/tvdb/tmdb) and are cross-mapped via the Fribb list.
/// </summary>
public static class AnimeIdResolver
{
    /// <summary>Collects provider ids from a Jellyfin ProviderIds dictionary plus the explicit endpoint hint.</summary>
    public static AnimeProviderIds FromProviderIds(IReadOnlyDictionary<string, string>? providerIds, string? explicitHint)
    {
        static string? Get(IReadOnlyDictionary<string, string>? ids, string key)
            => ids is not null && ids.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value.Trim()
                : null;

        return new AnimeProviderIds(
            Get(providerIds, "anilist"),
            Get(providerIds, "mal"),
            Get(providerIds, "tvdb"),
            Get(providerIds, "tmdb"),
            string.IsNullOrWhiteSpace(explicitHint) ? null : explicitHint.Trim());
    }

    /// <summary>Finds the Fribb entry matching any known id (ordinal, by-kind dictionary probes in the LookupKeys resolution order).</summary>
    internal static FribbEntry? FindMapping(FribbAliasIndex index, AnimeProviderIds ids)
        => index.Find(ids);

    /// <summary>The admin override matching the series id (first match wins; trimmed ordinal comparison), or null.</summary>
    public static Configuration.AnimeSeriesOverride? FindOverride(IReadOnlyList<Configuration.AnimeSeriesOverride>? overrides, string seriesId)
        => overrides is null || overrides.Count == 0
            ? null
            : overrides.FirstOrDefault(candidate =>
                string.Equals((candidate.SeriesId ?? string.Empty).Trim(), seriesId, StringComparison.Ordinal));

    /// <summary>
    /// Resolution precedence, tier 1: an explicit admin override for the series
    /// wins over the derived provider ids. An override without any usable
    /// provider id (or a non-matching series id) degrades to the derived ids
    /// unchanged (tier 2: auto-derive), which then fall back to the name slug
    /// (tier 3). Pure — the config lookup is a parameter, so the precedence is
    /// unit-testable host-free.
    /// </summary>
    public static (AnimeProviderIds Ids, bool Overridden) Resolve(IReadOnlyList<Configuration.AnimeSeriesOverride>? overrides, string seriesId, AnimeProviderIds derived)
    {
        var match = FindOverride(overrides, seriesId);
        var anilist = NormalizedId(match?.AniListId);
        var mal = NormalizedId(match?.MalId);
        if (match is null || (anilist is null && mal is null))
        {
            return (derived, false);
        }

        // The override pins anilist/mal; library tvdb/tmdb ids and the caller's
        // explicit hint stay available (hint keeps its filler-slug role).
        return (new AnimeProviderIds(anilist, mal, derived.TvdbId, derived.TmdbId, derived.ExplicitHint), true);
    }

    private static string? NormalizedId(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// Anime episode markers: filler/mixed/canon from AnimeFillerList, recap
/// markers from Tenrai, cross-source id mapping via the Fribb anime-list JSON
/// (anilist/mal/thetvdb/tmdb). Cached per series, negative lookups included
/// (shorter TTL) so a bad slug doesn't re-scrape every request.
/// </summary>
public sealed partial class AnimeMarkersService
{
    private readonly ResilientFetcher _fetcher;
    private readonly FileCacheStore _cache;
    private readonly ILibraryManager _libraryManager;
    private readonly Func<AnimeConfig> _config;
    private readonly CircuitBreaker _fillerBreaker;
    private readonly CircuitBreaker _tenraiBreaker;
    // Deepening: Fribb gets its own breaker locality at the fetch seam so the
    // mapping module degrades independently of the marker sources.
    private readonly CircuitBreaker _fribbBreaker;
    private readonly ILogger<AnimeMarkersService> _logger;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, FribbEntry> _fribbIndex = new(StringComparer.Ordinal);
    private readonly object _fribbLock = new();
    private Task? _fribbFetch;
    private DateTime _fribbLoadedUtc;

    /// <summary>By-kind lookup over the latest fetched list (the flat index's kind-aware sibling). Guarded by <see cref="_fribbLock"/>.</summary>
    private FribbAliasIndex? _fribbAliases;

    public AnimeMarkersService(ResilientFetcher fetcher, FileCacheStore cache, ILibraryManager libraryManager, Func<AnimeConfig> config, ILogger<AnimeMarkersService> logger, TimeProvider? clock = null)
    {
        _fetcher = fetcher;
        _cache = cache;
        _libraryManager = libraryManager;
        _config = config;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _fillerBreaker = new(clock: _clock);
        _tenraiBreaker = new(clock: _clock);
        _fribbBreaker = new(clock: _clock);
    }

    public bool IsEnabled => _config().Enabled;

    /// <summary>True when every marker source is circuit-open (the refresh task skips such series entirely).</summary>
    public bool AllFetchBreakersOpen
    {
        get
        {
            return _fillerBreaker.IsOpen() && _tenraiBreaker.IsOpen() && _fribbBreaker.IsOpen();
        }
    }

    public async Task<SeriesMarkers?> GetSeriesMarkers(string seriesId, string? providerSeriesId, CancellationToken cancellationToken = default)
    {
        var config = _config();
        if (!IsEnabled)
        {
            return null;
        }

        var cacheKey = CacheKeys.AnimeSeries(seriesId, providerSeriesId, config);
        var ttl = TtlPolicy.Anime(config.RefreshIntervalHours);

        // Deepening: the outer cache lives behind the fetcher's resolved seam
        // (single probe + single-flight), not a hand-rolled Get before the
        // multi-fetch for the same key (the old double-probe). The inner multi
        // still owns the Attempted-gated miss choreography for this key; the
        // outer only short-circuits it (positive or miss-marker hit) and
        // coalesces the mapping + source-building work.
        return await _fetcher.GetOrFetchResolvedAsync<SeriesMarkers>(
            cacheKey,
            ttl,
            async cancellation =>
            {
                var series = ResolveSeries(seriesId);
                // Precedence: explicit admin override (matched by seriesId) > the
                // library's provider ids + explicit hint > name-slug fallback.
                var (ids, overridden) = AnimeIdResolver.Resolve(config.SeriesOverrides, seriesId, AnimeIdResolver.FromProviderIds(series?.ProviderIds, providerSeriesId));
                var mapping = await ResolveMappingAsync(ids, cancellation);

                // An explicit override pins the pair directly; otherwise the Fribb
                // cross-mapping stays authoritative. Tenrai keys on AniList ids: the
                // resolved one, then a library anilist id, then the caller's explicit
                // id (legacy behavior).
                var anilistId = overridden
                    ? ids.AniListId ?? mapping?.AniListId ?? ids.ExplicitHint
                    : mapping?.AniListId ?? ids.AniListId ?? ids.ExplicitHint;

                // AnimeFillerList addresses shows by name slug — never by Jellyfin id.
                // An explicit providerSeriesId is treated as the slug (that source's
                // identifier IS a slug); otherwise slugify the series name.
                var fillerSlug = CacheKeys.SlugifyName(!string.IsNullOrEmpty(ids.ExplicitHint)
                    ? ids.ExplicitHint!
                    : series?.Name ?? string.Empty);

                // Source selection only: each candidate carries its own breaker, so an
                // open circuit means that source is skipped — never a poisoned cache.
                var sources = new List<ResilientFetcher.MultiSource<List<AnimeMarker>>>();
                if (config.EnableFillerList && fillerSlug.Length > 0)
                {
                    sources.Add(new ResilientFetcher.MultiSource<List<AnimeMarker>>(
                        $"filler:{fillerSlug}",
                        (client, ct) => ScrapeFillerListAsync(client, fillerSlug, ct),
                        _fillerBreaker,
                        LogLevel.Debug));
                }

                if (config.EnableTenrai && !string.IsNullOrEmpty(anilistId))
                {
                    sources.Add(new ResilientFetcher.MultiSource<List<AnimeMarker>>(
                        $"tenrai:{anilistId}",
                        (client, ct) => ScrapeTenraiRecapsAsync(client, anilistId!, ct),
                        _tenraiBreaker,
                        LogLevel.Debug));
                }

                // The independent sources run concurrently; the fetch pipeline owns the
                // cache/miss choreography (a miss is memoized only when a source was
                // really attempted) and coalesces concurrent cold callers per series.
                return await _fetcher.GetOrFetchMultiAsync(
                    cacheKey,
                    ttl,
                    sources,
                    merge: values =>
                    {
                        var markers = new List<AnimeMarker>();
                        foreach (var value in values)
                        {
                            markers.AddRange(value ?? new List<AnimeMarker>());
                        }

                        return markers.Count == 0
                            ? null
                            : new SeriesMarkers(seriesId, anilistId, mapping?.MalId, markers.OrderBy(marker => marker.EpisodeNumber).ToList());
                    },
                    missCache: true,
                    missTtl: TtlPolicy.Miss,
                    cancellationToken: cancellation);
            },
            missCache: true,
            missTtl: TtlPolicy.Miss,
            cancellationToken: cancellationToken);
    }

    /// <summary>Resolves episode marker rows for episode lists (client batches by series).</summary>
    public async Task<IReadOnlyList<AnimeMarker>> GetEpisodeMarkers(string seriesId, int fromEpisode, int toEpisode, string? providerSeriesId = null, CancellationToken cancellationToken = default)
    {
        var markers = await GetSeriesMarkers(seriesId, providerSeriesId: providerSeriesId, cancellationToken);
        return markers?.Markers
            .Where(marker => marker.EpisodeNumber >= fromEpisode && marker.EpisodeNumber <= toEpisode)
            .ToList() ?? new List<AnimeMarker>();
    }

    /// <summary>
    /// Cache identity for one series' markers: the series id plus every input
    /// that changes the resolved result (source toggles, the explicit hint,
    /// and a stable hash of the admin overrides). A toggle/override edit must
    /// miss the old entry instead of serving stale markers until TTL.
    /// Deepening: the canonical shape lives in the CacheKeys module (one
    /// seam); this stays as a thin adapter so existing callers/tests keep
    /// working byte-identically.
    /// </summary>
    internal static string BuildCacheKey(string seriesId, string? providerSeriesId, Configuration.AnimeConfig config)
        => CacheKeys.AnimeSeries(seriesId, providerSeriesId, config);

    internal static string OverridesHash(IReadOnlyList<Configuration.AnimeSeriesOverride>? overrides)
        => CacheKeys.OverridesHash(overrides);

    private BaseItem? ResolveSeries(string seriesId)
    {
        try
        {
            return _libraryManager.GetItemById(Guid.Parse(seriesId));
        }
        catch (FormatException ex)
        {
            _logger.LogDebug(ex, "Series id {SeriesId} is not a guid; provider ids must come from the explicit hint", seriesId);
            return null;
        }
    }

    private static async Task<List<AnimeMarker>?> ScrapeFillerListAsync(HttpClient client, string slug, CancellationToken cancellationToken)
    {
        using var request = ResilientFetcher.BrowserGetRequest(AnimeSourceUrls.FillerShow(slug));
        using var response = await client.SendAsync(request, cancellationToken);
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        var markers = ParseFillerList(html);
        if (markers.Count == 0)
        {
            // A valid show page always lists episodes; zero rows means a bad
            // slug or a layout change — let the breaker count it and degrade
            // this series to Tenrai-only markers.
            throw new InvalidOperationException($"AnimeFillerList scrape produced no rows for '{slug}'.");
        }

        return markers;
    }

    internal static List<AnimeMarker> ParseFillerList(string html)
    {
        var markers = new List<AnimeMarker>();
        // Episode rows: <td><a>1</a></td><td class="Type Filler">Filler</td> — table rows carry episode + type cells.
        foreach (var match in FillerRowRegex().Matches(html))
        {
            if (match is not System.Text.RegularExpressions.Match m)
            {
                continue;
            }

            var episode = int.Parse(m.Groups["ep"].Value, CultureInfo.InvariantCulture);
            var typeCell = m.Groups["type"].Value.Trim().ToLowerInvariant();
            var type = typeCell.Contains("mixed") ? "mixed" : typeCell.Contains("filler") ? "filler" : "canon";
            markers.Add(new AnimeMarker(type, episode, null));
        }

        return markers;
    }

    [System.Text.RegularExpressions.GeneratedRegex(
        @"<td[^>]*>\s*(?:<a[^>]*>)?\s*(?<ep>\d{1,4})\s*(?:</a>)?\s*</td>\s*<td[^>]*class=""[^""]*Type[^""]*""[^>]*>\s*(?<type>[^<]+?)\s*</td>",
        System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex FillerRowRegex();

    private static async Task<List<AnimeMarker>?> ScrapeTenraiRecapsAsync(HttpClient client, string anilistId, CancellationToken cancellationToken)
        => ParseTenraiRecaps(await client.GetStringAsync(AnimeSourceUrls.TenraiRecaps(anilistId), cancellationToken));

    private static List<AnimeMarker> ParseTenraiRecaps(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var markers = new List<AnimeMarker>();
        foreach (var recap in doc.RootElement.EnumerateArray())
        {
            if (recap.TryGetProperty("episode_number", out var episode)
                && episode.TryGetInt32(out var number))
            {
                markers.Add(new AnimeMarker("recap", number, null));
            }
        }

        return markers;
    }

    /// <summary>Loads (and periodically refreshes) the Fribb anime-list mapping, then matches the series' ids against it.</summary>
    private async Task<FribbEntry?> ResolveMappingAsync(AnimeProviderIds ids, CancellationToken cancellationToken)
    {
        await EnsureFribbIndexAsync(cancellationToken);
        lock (_fribbLock)
        {
            foreach (var key in ids.LookupKeys())
            {
                if (_fribbIndex.TryGetValue(key, out var hit))
                {
                    return hit;
                }
            }

            return _fribbAliases?.Find(ids);
        }
    }

    /// <summary>
    /// Loads (and periodically refreshes) the Fribb anime-list mapping.
    /// Single-flight: concurrent cold callers share ONE fetch task (the
    /// in-flight slot is guarded by the lock and cleared on failure so the
    /// next caller retries); a loaded index re-fetches only once stale. The
    /// shared slot's fetch runs with <see cref="CancellationToken.None"/> —
    /// the slot is process-wide, so no single caller's abort may cancel the
    /// load out from under every other concurrent caller.
    /// </summary>
    private async Task EnsureFribbIndexAsync(CancellationToken cancellationToken)
    {
        // Config read hoisted out of the lock (the delegate re-reads per
        // call — ADR-0002): no cold caller serializes behind it.
        var staleBefore = _clock.GetUtcNow().UtcDateTime.AddHours(-Math.Max(1, _config().RefreshIntervalHours));
        Task fetch;
        lock (_fribbLock)
        {
            if (_fribbLoadedUtc > staleBefore)
            {
                return;
            }

            fetch = _fribbFetch ??= FetchFribbIndexAsync(CancellationToken.None);
        }

        await fetch;
    }

    private async Task FetchFribbIndexAsync(CancellationToken cancellationToken)
    {
        try
        {
            var url = _config().FribbListUrl;
            // Leverage the shared fetch interface seam: breaker + logging live in
            // the fetcher module, so this caller only shapes "no data" (null).
            var json = await _fetcher.FetchAsync<string>("fribb", async (client, ct) => (string?)await client.GetStringAsync(url, ct), _fribbBreaker, LogLevel.Debug, cancellationToken);
            if (json is null)
            {
                lock (_fribbLock)
                {
                    _fribbFetch = null; // cleared so the next caller retries
                }

                return;
            }

            using var doc = JsonDocument.Parse(json);
            var index = new Dictionary<string, FribbEntry>(StringComparer.Ordinal);
            var entries = new List<FribbEntry>();
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                var anilist = GetString(entry, "anilist_id");
                var mal = GetString(entry, "mal_id");
                var tvdb = GetString(entry, "tvdb_id");
                var tmdb = GetString(entry, "tmdb_id") ?? GetString(entry, "tmdb_show_id");
                var key = anilist ?? mal ?? tvdb ?? tmdb;
                if (key is null)
                {
                    continue;
                }

                var fribb = new FribbEntry(anilist, mal, tvdb, tmdb);
                entries.Add(fribb);
                index[key] = fribb;
                // Alias every known id so the lookup works from whichever id
                // the library carries (anilist/mal/tvdb/tmdb).
                if (tvdb is not null)
                {
                    index[tvdb] = fribb;
                }

                if (mal is not null)
                {
                    index[mal] = fribb;
                }

                if (tmdb is not null)
                {
                    index[tmdb] = fribb;
                }
            }

            // Built in document order (first entry wins a duplicate id); the
            // flat index above stays last-wins per alias.
            var aliases = new FribbAliasIndex(entries);
            lock (_fribbLock)
            {
                foreach (var (key, entry) in index)
                {
                    _fribbIndex[key] = entry;
                }

                _fribbAliases = aliases;
                _fribbLoadedUtc = _clock.GetUtcNow().UtcDateTime;
                _fribbFetch = null;
            }
        }
        catch (Exception ex)
        {
            lock (_fribbLock)
            {
                _fribbFetch = null; // cleared so the next caller retries
            }

            _logger.LogDebug(ex, "Fribb anime-list refresh failed");
        }
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : (value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null);

    /// <summary>AnimeFillerList slugs are kebab-case titles — slugify the series NAME (never the Jellyfin id). Idempotent on valid slugs. Canonical shape lives in CacheKeys; this stays as a thin adapter.</summary>
    internal static string SlugifyName(string name)
        => CacheKeys.SlugifyName(name);
}
