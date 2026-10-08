using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.JellyPlay.Configuration;

namespace Jellyfin.Plugin.JellyPlay.Services.Fetching;

/// <summary>
/// Cache-key builders for the resilient-fetch seam — the one module where every
/// external-source cache identity lives, so key shapes have locality (fix once,
/// fixed everywhere) and callers get leverage (one static call per site, no
/// inline string surgery). Pure static: no config singleton reads (ADR-0002) —
/// callers pass the values they already hold; the builders only shape strings.
/// Every builder produces byte-identical keys to the inline strings it
/// replaces (pinned by the fetching tests' cache-hit assertions).
/// </summary>
public static class CacheKeys
{
    /// <summary>MDBList aggregated ratings for one IMDb id (was "mdblist:{imdbId}").</summary>
    public static string MdbFind(string imdbId) => $"mdblist:{imdbId}";

    /// <summary>TMDB one season's episode ratings (was "tmdb:season:{id}:{season}").</summary>
    public static string TmdbSeason(string tmdbId, int seasonNumber) => $"tmdb:season:{tmdbId}:{seasonNumber}";

    /// <summary>TMDB next-episode-to-air for one series (was "tmdb:next:{id}").</summary>
    public static string TmdbNext(string tmdbId) => $"tmdb:next:{tmdbId}";

    /// <summary>IMDb top-250 chart snapshot (was "imdb:top250").</summary>
    public static string ImdbChart() => ImdbChart("top250");

    /// <summary>IMDb chart snapshot for one chart name.</summary>
    public static string ImdbChart(string chart) => $"imdb:{chart}";

    /// <summary>Letterboxd list page scrape (was "letterboxd:{slug}").</summary>
    public static string Letterboxd(string listSlug) => $"letterboxd:{listSlug}";

    /// <summary>IMDb list page scrape (was "imdblist:{id}").</summary>
    public static string ImdbList(string listId) => $"imdblist:{listId}";

    /// <summary>MDBList official list items (was "mdblist-list:{slug}").</summary>
    public static string MdbList(string listSlug) => $"mdblist-list:{listSlug}";

    /// <summary>TMDB official list items (was "tmdb-list:{id}").</summary>
    public static string TmdbList(string listId) => $"tmdb-list:{listId}";

    /// <summary>Fully matched custom-row result incl. local ids (was "rowres:{source}:{listId}:{limit}").</summary>
    public static string RowResolved(string source, string listId, int limit) => $"rowres:{source}:{listId}:{limit}";

    /// <summary>Seasonal keyword discovery (was "seasonal:{keyword}").</summary>
    public static string Seasonal(string keyword) => $"seasonal:{keyword}";

    /// <summary>
    /// One anime series' markers: series id plus every input that changes the
    /// resolved result (source toggles, explicit hint, stable hash of admin
    /// overrides). Moved from AnimeMarkersService.BuildCacheKey — identical
    /// strings, one home.
    /// </summary>
    public static string AnimeSeries(string seriesId, string? providerSeriesId, AnimeConfig config)
        => AnimeSeries(seriesId, providerSeriesId, config.EnableFillerList, config.EnableTenrai, config.SeriesOverrides);

    /// <summary>Pure core: explicit flags + overrides, no config object — unit-testable host-free.</summary>
    public static string AnimeSeries(
        string seriesId,
        string? providerSeriesId,
        bool enableFillerList,
        bool enableTenrai,
        IReadOnlyList<AnimeSeriesOverride>? overrides)
    {
        var hint = string.IsNullOrWhiteSpace(providerSeriesId) ? "-" : providerSeriesId.Trim();
        var flags = (enableFillerList ? "1" : "0") + (enableTenrai ? "1" : "0");
        return $"animemarkers:{seriesId}:hint={Uri.EscapeDataString(hint)}:src={flags}:ov={OverridesHash(overrides)}";
    }

    /// <summary>
    /// Stable hash of admin override rows — FNV-1a 32-bit over normalized rows,
    /// stable across processes (unlike string.GetHashCode). Moved from
    /// AnimeMarkersService.OverridesHash unchanged.
    /// </summary>
    public static string OverridesHash(IReadOnlyList<AnimeSeriesOverride>? overrides)
    {
        if (overrides is null || overrides.Count == 0)
        {
            return "none";
        }

        unchecked
        {
            const uint offset = 2166136261;
            const uint prime = 16777619;
            var hash = offset;
            foreach (var o in overrides.OrderBy(o => o.SeriesId ?? string.Empty, StringComparer.Ordinal))
            {
                var row = (o.SeriesId ?? string.Empty).Trim() + "|" + (o.AniListId ?? string.Empty).Trim() + "|" + (o.MalId ?? string.Empty).Trim() + ";";
                foreach (var c in row)
                {
                    hash ^= c;
                    hash *= prime;
                }
            }

            return hash.ToString("x8", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// AnimeFillerList slugs are kebab-case titles — slugify the series NAME
    /// (never the Jellyfin id). Idempotent on valid slugs. Moved from
    /// AnimeMarkersService.SlugifyName unchanged.
    /// </summary>
    public static string SlugifyName(string name)
    {
        var slug = name.ToLowerInvariant().Replace(' ', '-');
        slug = new string(slug.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
        return slug.Trim('-');
    }
}

/// <summary>
/// The one TTL policy for the resilient-fetch seam — every cached external
/// source's freshness lives here (locality), callers pass their configured
/// hours where the TTL is admin-driven (no singleton reads). All durations
/// are identical to the inline TimeSpans they replace.
/// </summary>
public static class TtlPolicy
{
    /// <summary>TMDB next-episode lookup: air dates go stale hourly — 6h, never the configurable ratings TTL.</summary>
    public static readonly TimeSpan NextEpisode = TimeSpan.FromHours(6);

    /// <summary>Scraped IMDb chart snapshot: nightly-relevant — 24h, warmed by the refresh task.</summary>
    public static readonly TimeSpan Chart = TimeSpan.FromHours(24);

    /// <summary>Seasonal keyword discovery: holiday results move on a season scale — 2d.</summary>
    public static readonly TimeSpan Seasonal = TimeSpan.FromDays(2);

    /// <summary>Cached "no data" markers: the shared miss window (same as the fetch pipeline's own).</summary>
    public static readonly TimeSpan Miss = ResilientFetcher.DefaultMissTtl;

    /// <summary>Config-driven ratings TTL (MDBList find, TMDB season): admin hours.</summary>
    public static TimeSpan Ratings(int cacheTtlHours) => TimeSpan.FromHours(cacheTtlHours);

    /// <summary>Config-driven rows TTL (outer resolved + inner lists): admin hours.</summary>
    public static TimeSpan Rows(int cacheTtlHours) => TimeSpan.FromHours(cacheTtlHours);

    /// <summary>Anime markers TTL: admin refresh hours, floored at 1h.</summary>
    public static TimeSpan Anime(int refreshIntervalHours) => TimeSpan.FromHours(Math.Max(1, refreshIntervalHours));
}
