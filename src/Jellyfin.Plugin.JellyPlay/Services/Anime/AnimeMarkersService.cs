using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Configuration;
using Jellyfin.Plugin.JellyPlay.Services.Cache;
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

    /// <summary>Finds the Fribb entry matching ANY known id (ordinal comparison).</summary>
    internal static FribbEntry? FindMapping(IEnumerable<FribbEntry> entries, AnimeProviderIds ids)
        => entries.FirstOrDefault(entry =>
            (ids.AniListId is not null && string.Equals(entry.AniListId, ids.AniListId, StringComparison.Ordinal))
            || (ids.MalId is not null && string.Equals(entry.MalId, ids.MalId, StringComparison.Ordinal))
            || (ids.TvdbId is not null && string.Equals(entry.TvdbId, ids.TvdbId, StringComparison.Ordinal))
            || (ids.TmdbId is not null && string.Equals(entry.TmdbId, ids.TmdbId, StringComparison.Ordinal)));

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
    private const string FillerListBase = "https://www.animefillerlist.com/shows/";
    private const string TenraiBase = "https://api.tenrai.org/v1/recaps";

    /// <summary>TTL for cached "no markers found" results — shorter than the positive cache so a fixed slug/mapping is picked up quickly.</summary>
    private static readonly TimeSpan MissCacheTtl = TimeSpan.FromHours(2);

    private readonly IHttpClientFactory _httpFactory;
    private readonly FileCacheStore _cache;
    private readonly ILibraryManager _libraryManager;
    private readonly CircuitBreaker _fillerBreaker = new();
    private readonly CircuitBreaker _tenraiBreaker = new();
    private readonly ILogger<AnimeMarkersService> _logger;
    private readonly Dictionary<string, FribbEntry> _fribbIndex = new(StringComparer.Ordinal);
    private readonly object _fribbLock = new();
    private DateTime _fribbLoadedUtc;

    public AnimeMarkersService(IHttpClientFactory httpFactory, FileCacheStore cache, ILibraryManager libraryManager, ILogger<AnimeMarkersService> logger)
    {
        _httpFactory = httpFactory;
        _cache = cache;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public bool IsEnabled => JellyPlayPlugin.Instance!.Configuration.Anime.Enabled;

    /// <summary>True when every marker source is circuit-open (the refresh task skips such series entirely).</summary>
    public bool AllFetchBreakersOpen
    {
        get
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return _fillerBreaker.IsOpen(now) && _tenraiBreaker.IsOpen(now);
        }
    }

    public async Task<SeriesMarkers?> GetSeriesMarkers(string seriesId, string? providerSeriesId)
    {
        var config = JellyPlayPlugin.Instance!.Configuration.Anime;
        if (!IsEnabled)
        {
            return null;
        }

        var cacheKey = $"animemarkers:{seriesId}";
        var cached = _cache.Get<SeriesMarkers>(cacheKey, TimeSpan.FromHours(Math.Max(1, config.RefreshIntervalHours)));
        if (cached is not null)
        {
            return cached;
        }

        var missCacheKey = $"animemarkers-miss:{seriesId}";
        if (_cache.Get<bool>(missCacheKey, MissCacheTtl))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var series = ResolveSeries(seriesId);
        // Precedence: explicit admin override (matched by seriesId) > the
        // library's provider ids + explicit hint > name-slug fallback.
        var (ids, overridden) = AnimeIdResolver.Resolve(config.SeriesOverrides, seriesId, AnimeIdResolver.FromProviderIds(series?.ProviderIds, providerSeriesId));
        var mapping = await ResolveMappingAsync(ids);

        // An explicit override pins the pair directly; otherwise the Fribb
        // cross-mapping stays authoritative. Tenrai keys on AniList ids: the
        // resolved one, then a library anilist id, then the caller's explicit
        // id (legacy behavior).
        var anilistId = overridden
            ? ids.AniListId ?? mapping?.AniListId ?? ids.ExplicitHint
            : mapping?.AniListId ?? ids.AniListId ?? ids.ExplicitHint;
        var malId = overridden ? ids.MalId ?? mapping?.MalId : mapping?.MalId;

        // AnimeFillerList addresses shows by name slug — never by Jellyfin id.
        // An explicit providerSeriesId is treated as the slug (that source's
        // identifier IS a slug); otherwise slugify the series name.
        var fillerSlug = SlugifyName(!string.IsNullOrEmpty(ids.ExplicitHint)
            ? ids.ExplicitHint!
            : series?.Name ?? string.Empty);

        var markers = new List<AnimeMarker>();
        var fillerAttempted = false;
        var tenraiAttempted = false;
        if (config.EnableFillerList && fillerSlug.Length > 0 && !_fillerBreaker.IsOpen(now))
        {
            fillerAttempted = true;
            markers.AddRange(await FetchFillerMarkers(fillerSlug));
        }

        if (config.EnableTenrai && !string.IsNullOrEmpty(anilistId) && !_tenraiBreaker.IsOpen(now))
        {
            tenraiAttempted = true;
            markers.AddRange(await FetchTenraiRecaps(anilistId));
        }

        if (markers.Count == 0)
        {
            // Only memoize the miss when a source was actually attempted — an
            // open breaker means nothing was tried, so don't poison the cache.
            if (fillerAttempted || tenraiAttempted)
            {
                _cache.Set(missCacheKey, true);
            }

            return null;
        }

        var result = new SeriesMarkers(seriesId, anilistId, mapping?.MalId, markers.OrderBy(marker => marker.EpisodeNumber).ToList());
        _cache.Set(cacheKey, result);
        return result;
    }

    /// <summary>Resolves episode marker rows for episode lists (client batches by series).</summary>
    public async Task<IReadOnlyList<AnimeMarker>> GetEpisodeMarkers(string seriesId, int fromEpisode, int toEpisode)
    {
        var markers = await GetSeriesMarkers(seriesId, providerSeriesId: null);
        return markers?.Markers
            .Where(marker => marker.EpisodeNumber >= fromEpisode && marker.EpisodeNumber <= toEpisode)
            .ToList() ?? new List<AnimeMarker>();
    }

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

    private async Task<List<AnimeMarker>> FetchFillerMarkers(string slug)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try
        {
            var client = _httpFactory.CreateClient("JellyPlayHttpClient");
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{FillerListBase}{Uri.EscapeDataString(slug)}");
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            var html = await (await client.SendAsync(request)).Content.ReadAsStringAsync();
            var markers = ParseFillerList(html);
            if (markers.Count == 0)
            {
                // A valid show page always lists episodes; zero rows means a bad
                // slug or a layout change — let the breaker count it and degrade
                // this series to Tenrai-only markers.
                throw new InvalidOperationException($"AnimeFillerList scrape produced no rows for '{slug}'.");
            }

            _fillerBreaker.RecordSuccess(now);
            return markers;
        }
        catch (Exception ex)
        {
            _fillerBreaker.RecordFailure(now);
            _logger.LogDebug(ex, "FillerList fetch failed for {Slug}", slug);
            return new List<AnimeMarker>();
        }
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

    private async Task<List<AnimeMarker>> FetchTenraiRecaps(string anilistId)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try
        {
            var client = _httpFactory.CreateClient("JellyPlayHttpClient");
            var json = await client.GetStringAsync($"{TenraiBase}?anilist_id={Uri.EscapeDataString(anilistId)}");
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

            _tenraiBreaker.RecordSuccess(now);
            return markers;
        }
        catch (Exception ex)
        {
            _tenraiBreaker.RecordFailure(now);
            _logger.LogDebug(ex, "Tenrai recaps failed for {AniListId}", anilistId);
            return new List<AnimeMarker>();
        }
    }

    /// <summary>Loads (and periodically refreshes) the Fribb anime-list mapping, then matches the series' ids against it.</summary>
    private async Task<FribbEntry?> ResolveMappingAsync(AnimeProviderIds ids)
    {
        await EnsureFribbIndexAsync();
        lock (_fribbLock)
        {
            foreach (var key in ids.LookupKeys())
            {
                if (_fribbIndex.TryGetValue(key, out var hit))
                {
                    return hit;
                }
            }

            return AnimeIdResolver.FindMapping(_fribbIndex.Values, ids);
        }
    }

    private async Task EnsureFribbIndexAsync()
    {
        lock (_fribbLock)
        {
            if (_fribbLoadedUtc > DateTime.UtcNow.AddHours(-Math.Max(1, JellyPlayPlugin.Instance!.Configuration.Anime.RefreshIntervalHours)))
            {
                return;
            }
        }

        try
        {
            var url = JellyPlayPlugin.Instance!.Configuration.Anime.FribbListUrl;
            var client = _httpFactory.CreateClient("JellyPlayHttpClient");
            var json = await client.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var index = new Dictionary<string, FribbEntry>(StringComparer.Ordinal);
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

            lock (_fribbLock)
            {
                foreach (var (key, entry) in index)
                {
                    _fribbIndex[key] = entry;
                }

                _fribbLoadedUtc = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Fribb anime-list refresh failed");
        }
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : (value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null);

    /// <summary>AnimeFillerList slugs are kebab-case titles — slugify the series NAME (never the Jellyfin id). Idempotent on valid slugs.</summary>
    internal static string SlugifyName(string name)
    {
        var slug = name.ToLowerInvariant().Replace(' ', '-');
        slug = new string(slug.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
        return slug.Trim('-');
    }
}
