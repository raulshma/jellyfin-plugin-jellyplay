using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Configuration;
using Jellyfin.Plugin.JellyPlay.Services.Cache;
using Jellyfin.Plugin.JellyPlay.Services.Fetching;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyPlay.Services.Ratings;

/// <summary>Aggregated ratings for one item, as consumed by client detail screens.</summary>
public sealed record RatingEntry(string Source, double? Score, int? Votes, string? Url);

public sealed record RatingsResult(string ImdbId, IReadOnlyList<RatingEntry> Ratings);

public sealed record EpisodeRatingsResult(
    string? ImdbId,
    double? TmdbScore,
    int? TmdbVotes,
    IReadOnlyList<RatingEntry> Entries);

public sealed record ChartEntry(string Rank, string Title, string? Year, string? ImdbId, string? Rating);

public sealed record ImdbChart(string Chart, IReadOnlyList<ChartEntry> Entries, long FetchedAt);

/// <summary>
/// MDBList aggregated ratings (IMDb, TMDB, RT, Metacritic, Trakt, Letterboxd…)
/// with server-held API key. Cached per IMDb id.
/// </summary>
public sealed class MdbListService
{
    private readonly ResilientFetcher _fetcher;
    private readonly FileCacheStore _cache;
    private readonly CircuitBreaker _breaker = new();
    private readonly Func<RatingsConfig> _config;

    public MdbListService(ResilientFetcher fetcher, FileCacheStore cache, Func<RatingsConfig> config)
    {
        _fetcher = fetcher;
        _cache = cache;
        _config = config;
    }

    public bool IsConfigured => !string.IsNullOrEmpty(ApiKey);

    private string ApiKey => _config().MdbListApiKey;

    public async Task<RatingsResult?> GetRatings(string imdbId)
    {
        if (!IsConfigured || string.IsNullOrEmpty(imdbId))
        {
            return null;
        }

        return await _fetcher.GetOrFetchAsync(
            $"mdblist:{imdbId}",
            TimeSpan.FromHours(TtlHours),
            (client, cancellationToken) => FetchRatingsAsync(client, imdbId, ApiKey, cancellationToken),
            _breaker);
    }

    private static async Task<RatingsResult?> FetchRatingsAsync(HttpClient client, string imdbId, string apiKey, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(MdbListUrls.Find(imdbId, apiKey), cancellationToken);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var ratings = new List<RatingEntry>();
        var root = doc.RootElement;
        if (root.TryGetProperty("ratings", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in list.EnumerateArray())
            {
                ratings.Add(new RatingEntry(
                    entry.TryGetProperty("source", out var source) ? source.GetString() ?? "unknown" : "unknown",
                    entry.TryGetProperty("value", out var value) && value.TryGetDouble(out var score) ? score : null,
                    entry.TryGetProperty("votes", out var votes) && votes.TryGetInt32(out var voteCount) ? voteCount : null,
                    entry.TryGetProperty("url", out var url) ? url.GetString() : null));
            }
        }

        return new RatingsResult(imdbId, ratings);
    }

    public async Task<string?> GetKeyInfo()
    {
        if (!IsConfigured)
        {
            return null;
        }

        return await _fetcher.FetchAsync(
            "mdblist:keyinfo",
            async Task<string?> (client, cancellationToken) => await client.GetStringAsync(MdbListUrls.User(ApiKey), cancellationToken),
            failureLogLevel: LogLevel.Debug);
    }

    public void ClearCache(string? imdbId)
    {
        if (string.IsNullOrEmpty(imdbId))
        {
            return;
        }

        _cache.Invalidate($"mdblist:{imdbId}");
    }

    private int TtlHours => _config().CacheTtlHours;
}

/// <summary>TMDB episode/season ratings + next-episode air date.</summary>
public sealed class TmdbRatingsService
{
    /// <summary>
    /// Fixed freshness for the next-episode lookup — deliberately NOT the
    /// configurable CacheTtlHours: air dates go stale hourly, and a stale
    /// "next episode" is worse than none.
    /// </summary>
    private static readonly TimeSpan NextEpisodeTtl = TimeSpan.FromHours(6);

    private readonly ResilientFetcher _fetcher;
    private readonly CircuitBreaker _breaker = new();
    private readonly Func<RatingsConfig> _config;

    public TmdbRatingsService(ResilientFetcher fetcher, Func<RatingsConfig> config)
    {
        _fetcher = fetcher;
        _config = config;
    }

    private string? ApiKey => _config().TmdbApiKey;

    public bool IsConfigured => !string.IsNullOrEmpty(ApiKey);

    /// <summary>Ratings for one season's episodes; <paramref name="seasonNumber"/> 0 = specials.</summary>
    public async Task<IReadOnlyDictionary<int, EpisodeRatingsResult>?> GetSeasonRatings(string tmdbId, int seasonNumber)
    {
        if (!IsConfigured)
        {
            return null;
        }

        return await _fetcher.GetOrFetchAsync(
            $"tmdb:season:{tmdbId}:{seasonNumber}",
            TimeSpan.FromHours(Ttl),
            (client, cancellationToken) => FetchSeasonRatingsAsync(client, tmdbId, seasonNumber, ApiKey, cancellationToken),
            _breaker);
    }

    private static async Task<IReadOnlyDictionary<int, EpisodeRatingsResult>?> FetchSeasonRatingsAsync(HttpClient client, string tmdbId, int seasonNumber, string? apiKey, CancellationToken cancellationToken)
    {
        var json = await client.GetStringAsync(TmdbUrls.Season(tmdbId, seasonNumber, apiKey), cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var results = new Dictionary<int, EpisodeRatingsResult>();
        foreach (var episode in doc.RootElement.GetProperty("episodes").EnumerateArray())
        {
            var number = episode.TryGetProperty("episode_number", out var n) ? n.GetInt32() : 0;
            var score = episode.TryGetProperty("vote_average", out var v) && v.TryGetDouble(out var d) ? d : (double?)null;
            var votes = episode.TryGetProperty("vote_count", out var vc) ? vc.GetInt32() : (int?)null;
            results[number] = new EpisodeRatingsResult(
                null,
                score,
                votes,
                score is null
                    ? Array.Empty<RatingEntry>()
                    : new[] { new RatingEntry("TMDB", score, votes, null) });
        }

        return results;
    }

    /// <summary>Next unaired episode info for a series, from TMDB.</summary>
    public sealed record NextEpisodeInfo(string? Name, string? AirDate);

    public async Task<NextEpisodeInfo?> GetNextEpisode(string tmdbId)
    {
        if (!IsConfigured)
        {
            return null;
        }

        return await _fetcher.GetOrFetchAsync(
            $"tmdb:next:{tmdbId}",
            NextEpisodeTtl,
            (client, cancellationToken) => FetchNextEpisodeAsync(client, tmdbId, ApiKey, cancellationToken),
            _breaker,
            failureLogLevel: LogLevel.Debug); // per-episode background lookup — a dead upstream is Debug-worthy
    }

    private static async Task<NextEpisodeInfo?> FetchNextEpisodeAsync(HttpClient client, string tmdbId, string? apiKey, CancellationToken cancellationToken)
    {
        var json = await client.GetStringAsync(TmdbUrls.Series(tmdbId, apiKey), cancellationToken);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("next_episode_to_air", out var next) && next.ValueKind == JsonValueKind.Object)
        {
            var name = next.TryGetProperty("name", out var n) ? n.GetString() : null;
            var airDate = next.TryGetProperty("air_date", out var a) ? a.GetString() : null;
            return new NextEpisodeInfo(name, airDate);
        }

        return null;
    }

    private int Ttl => _config().CacheTtlHours;
}

/// <summary>
/// IMDb charts (top250 movies/TV, most popular) scraped from IMDb's public
/// chart endpoint, cached. Unofficial — the circuit breaker auto-disables the
/// source when IMDb changes layout.
/// </summary>
public sealed partial class ImdbChartsService
{
    /// <summary>Fixed freshness for the scraped chart — deliberately not config-driven: the chart is one nightly-relevant snapshot, and the refresh task warms it on that cadence.</summary>
    private static readonly TimeSpan ChartTtl = TimeSpan.FromHours(24);

    private const string ChartUrl = "https://www.imdb.com/chart/top/?ref_=nv_tp_250";
    private readonly ResilientFetcher _fetcher;
    private readonly CircuitBreaker _breaker = new(failureThreshold: 2, openWindow: TimeSpan.FromHours(6));
    private readonly Func<RatingsConfig> _config;

    public ImdbChartsService(ResilientFetcher fetcher, Func<RatingsConfig> config)
    {
        _fetcher = fetcher;
        _config = config;
    }

    public bool IsEnabled => _config().EnableImdbCharts;

    public async Task<ImdbChart?> GetTop250()
    {
        if (!IsEnabled)
        {
            return null;
        }

        return await _fetcher.GetOrFetchAsync(
            "imdb:top250",
            ChartTtl,
            FetchTop250Async,
            _breaker);
    }

    private async Task<ImdbChart?> FetchTop250Async(HttpClient client, CancellationToken cancellationToken)
    {
        using var request = ResilientFetcher.BrowserGetRequest(ChartUrl);
        using var response = await client.SendAsync(request, cancellationToken);
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        var entries = ParseTop250(html);
        if (entries.Count == 0)
        {
            throw new InvalidOperationException("Chart parse produced no entries; layout may have changed.");
        }

        return new ImdbChart("top250", entries, _fetcher.NowMs);
    }

    /// <summary>
    /// Primary: IMDb embeds chart data as __NEXT_DATA__ JSON
    /// (props.pageProps.pageData.chartTitles.edges[].node). Fallback: legacy
    /// table markup regex. Both fail loudly so the breaker can disable the source.
    /// </summary>
    internal static List<ChartEntry> ParseTop250(string html)
    {
        var fromJson = ParseNextData(html);
        if (fromJson.Count > 0)
        {
            return fromJson;
        }

        var entries = new List<ChartEntry>();
        foreach (var match in TitleBlockRegex().Matches(html))
        {
            if (match is not Match m)
            {
                continue;
            }

            entries.Add(new ChartEntry(
                m.Groups["rank"].Success ? m.Groups["rank"].Value : string.Empty,
                System.Net.WebUtility.HtmlDecode(m.Groups["title"].Value),
                m.Groups["year"].Success ? m.Groups["year"].Value : null,
                m.Groups["id"].Value,
                m.Groups["rating"].Success ? m.Groups["rating"].Value : null));
        }

        return entries;
    }

    private static List<ChartEntry> ParseNextData(string html)
    {
        var entries = new List<ChartEntry>();
        var startMarker = "<script id=\"__NEXT_DATA__\" type=\"application/json\">";
        var start = html.IndexOf(startMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            return entries;
        }

        var jsonStart = start + startMarker.Length;
        var jsonEnd = html.IndexOf("</script>", jsonStart, StringComparison.Ordinal);
        if (jsonEnd < 0)
        {
            return entries;
        }

        try
        {
            using var doc = JsonDocument.Parse(html[jsonStart..jsonEnd]);
            if (!doc.RootElement.TryGetProperty("props", out var props)
                || !props.TryGetProperty("pageProps", out var pageProps)
                || !pageProps.TryGetProperty("pageData", out var pageData)
                || !pageData.TryGetProperty("chartTitles", out var chartTitles)
                || !chartTitles.TryGetProperty("edges", out var edges))
            {
                return entries;
            }

            var rank = 0;
            foreach (var edge in edges.EnumerateArray())
            {
                if (!edge.TryGetProperty("node", out var node))
                {
                    continue;
                }

                rank++;
                var title = node.TryGetProperty("titleText", out var titleText)
                    && titleText.TryGetProperty("text", out var text) ? text.GetString() : null;
                var year = node.TryGetProperty("releaseYear", out var releaseYear)
                    && releaseYear.TryGetProperty("year", out var y) ? y.GetInt32().ToString() : null;
                var rating = node.TryGetProperty("ratingsSummary", out var rs)
                    && rs.TryGetProperty("aggregateRating", out var ar)
                    && ar.TryGetDouble(out var d) ? d.ToString("0.0") : null;
                var id = node.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                if (!string.IsNullOrEmpty(title))
                {
                    entries.Add(new ChartEntry(rank.ToString(), title, year, id, rating));
                }
            }
        }
        catch (JsonException)
        {
            // fall through to regex path
        }

        return entries;
    }

    [GeneratedRegex(
        @"titleColumn""[\s\S]*?/title/tt(?<id>\d+)[/\?""]*[^>]*>(?<title>[^<]+)</a>[\s\S]*?secondaryText""\s*>(?<year>\d{4})<[\s\S]*?ratingColumn""\s*>(?<rating>\d\.\d)<",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TitleBlockRegex();
}
