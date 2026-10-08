using System;
using System.Collections.Generic;
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
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyPlay.Services.Rows;

/// <summary>One externally-resolved item, matched against local library ids where possible.</summary>
public sealed record RowItem(string Title, string? Year, string? ImdbId, string? TmdbId, string? LocalItemId);

public sealed record RowResult(string Title, string Source, IReadOnlyList<RowItem> Items);

/// <summary>
/// Resolves admin-defined home rows from Letterboxd / IMDb / MDBList / TMDB
/// lists into local library items. Titles are matched locally so the client can
/// render a native row; unresolved entries pass through for deep-linking.
/// </summary>
public sealed partial class CustomRowsService
{
    private readonly ResilientFetcher _fetcher;
    private readonly FileCacheStore _cache;
    private readonly ILibraryManager _libraryManager;
    private readonly Func<RatingsConfig> _config;
    // Deepening: per-source breaker locality — each upstream module gets its own
    // breaker seam so one slow source cannot trip the interface for the others.
    private readonly CircuitBreaker _letterboxdBreaker;
    private readonly CircuitBreaker _imdbBreaker;
    private readonly CircuitBreaker _mdbListBreaker;
    private readonly CircuitBreaker _tmdbBreaker;
    private readonly ILogger<CustomRowsService> _logger;

    public CustomRowsService(ResilientFetcher fetcher, FileCacheStore cache, ILibraryManager libraryManager, Func<RatingsConfig> config, ILogger<CustomRowsService> logger, TimeProvider? clock = null)
    {
        _fetcher = fetcher;
        _cache = cache;
        _libraryManager = libraryManager;
        _config = config;
        _logger = logger;
        _letterboxdBreaker = new(clock: clock);
        _imdbBreaker = new(clock: clock);
        _mdbListBreaker = new(clock: clock);
        _tmdbBreaker = new(clock: clock);
    }

    public async Task<RowResult?> ResolveAsync(CustomRowDefinition row, CancellationToken cancellationToken = default)
    {
        var limit = row.Limit <= 0 ? 20 : row.Limit;

        // Resolved-result cache: the local-match walk below costs one library
        // search per entry, so the fully matched result (LocalItemIds included)
        // is memoized under the same rows TTL. The external list keeps its own
        // fetcher cache beneath this; the short TTL bounds staleness across
        // library scans — the same tradeoff the external list already accepts.
        // Deepening: the outer cache lives behind the fetcher's resolved seam
        // (single probe + single-flight), not a hand-rolled Get/Set pair.
        var resolvedKey = CacheKeys.RowResolved(row.Source, row.ListId, limit);
        var resolved = await _fetcher.GetOrFetchResolvedAsync<RowResult>(
            resolvedKey,
            CacheTtl,
            async cancellation =>
            {
                var items = row.Source switch
                {
                    "letterboxd" => await FetchLetterboxdAsync(row.ListId),
                    "imdb" => await FetchImdbListAsync(row.ListId),
                    "mdblist" => await FetchMdbListAsync(row.ListId),
                    "tmdb" => await FetchTmdbListAsync(row.ListId),
                    _ => null
                };

                if (items is null)
                {
                    return null;
                }

                // Take-before-match + per-resolve title memo (same outputs, fewer
                // host crossings): attaching LocalItemId never affects ordering,
                // so matching only the taken slice issues at most `limit` host
                // queries instead of one per external entry; repeated titles
                // within the slice share one query via the request-scoped memo.
                // A full single-query batch (prefetch + in-memory fold) is
                // deliberately NOT built: the host's SearchTerm ranking is not
                // reproducible in memory, so that fold would drift on
                // fuzzy/substring matches.
                var titleMemo = new Dictionary<string, string?>(StringComparer.Ordinal);
                var matched = items
                    .Take(limit)
                    .Select(item => item with { LocalItemId = FindLocalItem(item, titleMemo) })
                    .ToList();

                return new RowResult(row.Title, row.Source, matched);
            },
            cancellationToken: cancellationToken);

        if (resolved is null)
        {
            return null;
        }

        // The title is admin-editable state, not fetched data — always the current one.
        return resolved with { Title = row.Title };
    }

    /// <summary>Letterboxd list pages are scrapeable HTML with the lazy-poster data attributes below.</summary>
    private async Task<List<RowItem>?> FetchLetterboxdAsync(string listSlug)
    {
        // Leverage the shared fetch seam with this source's own breaker; the
        // miss cache keeps a failing list from being re-scraped every request.
        return await _fetcher.GetOrFetchAsync(
            CacheKeys.Letterboxd(listSlug),
            CacheTtl,
            async Task<List<RowItem>?> (client, cancellationToken) =>
            {
                using var request = ResilientFetcher.BrowserGetRequest(ScrapedListUrls.LetterboxdList(listSlug));
                using var response = await client.SendAsync(request, cancellationToken);
                var html = await response.Content.ReadAsStringAsync(cancellationToken);
                return ParseLetterboxd(html);
            },
            _letterboxdBreaker,
            missCache: true);
    }

    /// <summary>
    /// Letterboxd list pages carry each entry as a lazy-poster React stub:
    /// <c>data-item-name="Title (year)"</c> on the posteritem component (the
    /// old img-alt + /film/ anchor markup is gone — posters hydrate client
    /// side, so the data attributes are the only server-rendered metadata).
    /// </summary>
    internal static List<RowItem> ParseLetterboxd(string html)
    {
        var items = new List<RowItem>();
        foreach (var match in PosterItemRegex().Matches(html))
        {
            if (match is not System.Text.RegularExpressions.Match m)
            {
                continue;
            }

            var name = System.Net.WebUtility.HtmlDecode(m.Groups["name"].Value);
            var year = m.Groups["year"].Success ? m.Groups["year"].Value : null;
            items.Add(new RowItem(name, year, null, null, null));
        }

        return items;
    }

    [System.Text.RegularExpressions.GeneratedRegex(
        @"data-item-name=""(?<name>[^""]+?)(?:\s\((?<year>\d{4})\))?""",
        System.Text.RegularExpressions.RegexOptions.Singleline)]
    private static partial System.Text.RegularExpressions.Regex PosterItemRegex();

    /// <summary>IMDb find endpoint via MDBList-compatible IMDb list page scrape (watchlist / ls<id> lists).</summary>
    private async Task<List<RowItem>?> FetchImdbListAsync(string listId)
    {
        return await _fetcher.GetOrFetchAsync(
            CacheKeys.ImdbList(listId),
            CacheTtl,
            async Task<List<RowItem>?> (client, cancellationToken) =>
            {
                using var request = ResilientFetcher.BrowserGetRequest(ScrapedListUrls.ImdbList(listId));
                using var response = await client.SendAsync(request, cancellationToken);
                var html = await response.Content.ReadAsStringAsync(cancellationToken);
                return ParseImdbList(html);
            },
            _imdbBreaker,
            missCache: true);
    }

    internal static List<RowItem> ParseImdbList(string html)
    {
        var items = new List<RowItem>();
        foreach (var match in ImdbItemRegex().Matches(html))
        {
            if (match is not System.Text.RegularExpressions.Match m)
            {
                continue;
            }

            items.Add(new RowItem(
                System.Net.WebUtility.HtmlDecode(m.Groups["name"].Value),
                m.Groups["year"].Success ? m.Groups["year"].Value : null,
                $"tt{m.Groups["id"].Value}",
                null,
                null));
        }

        return items;
    }

    [System.Text.RegularExpressions.GeneratedRegex(
        @"/title/tt(?<id>\d+)[/\?""][\s\S]{0,400}?ipc-title__text[^>]*>(?<name>[^<]+?)(?:\s*\((?<year>\d{4})\))?\s*<",
        System.Text.RegularExpressions.RegexOptions.Singleline)]
    private static partial System.Text.RegularExpressions.Regex ImdbItemRegex();

    /// <summary>MDBList official lists (needs key; top list endpoint returns structured JSON).</summary>
    private Task<List<RowItem>?> FetchMdbListAsync(string listSlug)
    {
        var apiKey = _config().MdbListApiKey;
        if (string.IsNullOrEmpty(apiKey))
        {
            return Task.FromResult<List<RowItem>?>(null);
        }

        return _fetcher.GetOrFetchAsync(
            CacheKeys.MdbList(listSlug),
            CacheTtl,
            async Task<List<RowItem>?> (client, cancellationToken) =>
            {
                var json = await client.GetStringAsync(MdbListUrls.ListItems(listSlug, apiKey), cancellationToken);
                return ParseMdbListItems(json);
            },
            _mdbListBreaker,
            missCache: true);
    }

    private static List<RowItem> ParseMdbListItems(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var items = new List<RowItem>();
        foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            items.Add(new RowItem(
                item.TryGetProperty("title", out var title) ? title.GetString() ?? string.Empty : string.Empty,
                item.TryGetProperty("release_year", out var year) && year.TryGetInt32(out var y) ? y.ToString() : null,
                item.TryGetProperty("imdb_id", out var imdb) && imdb.ValueKind == JsonValueKind.String ? imdb.GetString() : null,
                item.TryGetProperty("tmdb_id", out var tmdb) && tmdb.TryGetInt32(out var t) ? t.ToString() : null,
                null));
        }

        return items;
    }

    /// <summary>TMDB official lists (GET /list/{id}, v3 api key from the rows fetch config — same client/pattern as TmdbRatingsService).</summary>
    private Task<List<RowItem>?> FetchTmdbListAsync(string listId)
    {
        var apiKey = _config().TmdbApiKey;
        if (string.IsNullOrEmpty(apiKey))
        {
            return Task.FromResult<List<RowItem>?>(null);
        }

        return _fetcher.GetOrFetchAsync(
            CacheKeys.TmdbList(listId),
            CacheTtl,
            async Task<List<RowItem>?> (client, cancellationToken) =>
                ParseTmdbList(await client.GetStringAsync(TmdbUrls.List(listId, apiKey), cancellationToken)),
            _tmdbBreaker,
            missCache: true);
    }

    /// <summary>
    /// TMDB v3 list payload → row items. Entries carry movie fields (title /
    /// release_date) or TV fields (name / first_air_date); both map onto the
    /// same shape with the TMDB id for deep-linking.
    /// </summary>
    internal static List<RowItem> ParseTmdbList(string json)
    {
        var items = new List<RowItem>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("items", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return items;
        }

        foreach (var entry in list.EnumerateArray())
        {
            var title = entry.TryGetProperty("title", out var titleElement) && titleElement.ValueKind == JsonValueKind.String
                ? titleElement.GetString()
                : entry.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString()
                    : null;
            var date = entry.TryGetProperty("release_date", out var releaseDate) && releaseDate.ValueKind == JsonValueKind.String
                ? releaseDate.GetString()
                : entry.TryGetProperty("first_air_date", out var airDate) && airDate.ValueKind == JsonValueKind.String
                    ? airDate.GetString()
                    : null;
            var tmdbId = entry.TryGetProperty("id", out var id) && id.TryGetInt32(out var tmdb) ? tmdb.ToString() : null;
            if (string.IsNullOrEmpty(title))
            {
                continue;
            }

            items.Add(new RowItem(title, date is { Length: >= 4 } year ? year[..4] : null, null, tmdbId, null));
        }

        return items;
    }

    private string? FindLocalItem(RowItem item, Dictionary<string, string?> titleMemo)
    {
        if (titleMemo.TryGetValue(item.Title, out var cached))
        {
            return cached;
        }

        try
        {
            var local = _libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery(null)
            {
                SearchTerm = item.Title,
                IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Movie, Jellyfin.Data.Enums.BaseItemKind.Series },
                Limit = 1
            }).FirstOrDefault();

            var match = local?.Id.ToString();
            titleMemo[item.Title] = match;
            return match;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Local match failed for {Title}", item.Title);
            titleMemo[item.Title] = null;
            return null;
        }
    }

    private TimeSpan CacheTtl => TtlPolicy.Rows(_config().CacheTtlHours);
}

/// <summary>Seasonal holiday rows via TMDB keyword discovery, cached per keyword.</summary>
public sealed class SeasonalService
{
    private static readonly string[] DefaultKeywords = ["christmas", "halloween", "valentines-day", "summer", "thanksgiving"];

    private readonly ResilientFetcher _fetcher;
    private readonly Func<RatingsConfig> _config;
    private readonly CircuitBreaker _breaker;
    private readonly TimeProvider _clock;

    public SeasonalService(ResilientFetcher fetcher, Func<RatingsConfig> config, TimeProvider? clock = null)
    {
        _fetcher = fetcher;
        _config = config;
        _clock = clock ?? TimeProvider.System;
        _breaker = new(clock: _clock);
    }

    public async Task<RowResult?> GetSeasonalRow(string? keyword)
    {
        var tmdbKey = _config().TmdbApiKey;
        if (string.IsNullOrEmpty(tmdbKey))
        {
            return null;
        }

        var kw = string.IsNullOrEmpty(keyword) ? PickSeasonalKeyword() : keyword!;
        if (kw is null)
        {
            return null;
        }

        var items = await _fetcher.GetOrFetchAsync(
            CacheKeys.Seasonal(kw),
            TtlPolicy.Seasonal,
            (client, cancellationToken) => FetchSeasonalItemsAsync(client, tmdbKey, kw, cancellationToken),
            _breaker,
            missCache: true);
        return items is null ? null : new RowResult(TitleFor(kw), "tmdb", items);
    }

    private static async Task<List<RowItem>?> FetchSeasonalItemsAsync(HttpClient client, string tmdbKey, string keyword, CancellationToken cancellationToken)
    {
        var json = await client.GetStringAsync(TmdbUrls.DiscoverMoviesByKeyword(keyword, tmdbKey), cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var items = new List<RowItem>();
        foreach (var result in doc.RootElement.GetProperty("results").EnumerateArray())
        {
            items.Add(new RowItem(
                result.TryGetProperty("title", out var title) ? title.GetString() ?? string.Empty : string.Empty,
                result.TryGetProperty("release_date", out var date) && date.ValueKind == JsonValueKind.String
                    ? date.GetString() is { Length: >= 4 } release ? release[..4] : null
                    : null,
                null,
                result.TryGetProperty("id", out var id) && id.TryGetInt32(out var i) ? i.ToString() : null,
                null));
            if (items.Count >= 20)
            {
                break;
            }
        }

        return items;
    }

    private string? PickSeasonalKeyword()
    {
        var today = _clock.GetUtcNow();
        return today.Month switch
        {
            10 => "halloween",
            11 => "thanksgiving",
            12 or 1 => "christmas",
            2 => "valentines-day",
            6 or 7 => "summer",
            _ => null
        };
    }

    private static string TitleFor(string keyword) => keyword switch
    {
        "christmas" => "Holiday picks",
        "halloween" => "Spooky season",
        "valentines-day" => "For date night",
        "thanksgiving" => "Thanksgiving picks",
        "summer" => "Summer vibes",
        _ => "Seasonal"
    };
}
