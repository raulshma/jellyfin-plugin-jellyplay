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
    private readonly Func<RowsFetchConfig> _config;
    private readonly CircuitBreaker _breaker = new();
    private readonly ILogger<CustomRowsService> _logger;

    public CustomRowsService(ResilientFetcher fetcher, FileCacheStore cache, ILibraryManager libraryManager, Func<RowsFetchConfig> config, ILogger<CustomRowsService> logger)
    {
        _fetcher = fetcher;
        _cache = cache;
        _libraryManager = libraryManager;
        _config = config;
        _logger = logger;
    }

    public async Task<RowResult?> ResolveAsync(CustomRowDefinition row)
    {
        var limit = row.Limit <= 0 ? 20 : row.Limit;

        // Resolved-result cache: the local-match walk below costs one library
        // search per entry, so the fully matched result (LocalItemIds included)
        // is memoized under the same rows TTL. The external list keeps its own
        // fetcher cache beneath this; the short TTL bounds staleness across
        // library scans — the same tradeoff the external list already accepts.
        var resolvedKey = $"rowres:{row.Source}:{row.ListId}:{limit}";
        var resolved = _cache.Get<RowResult>(resolvedKey, CacheTtl);
        if (resolved is not null)
        {
            // The title is admin-editable state, not fetched data — always the current one.
            return resolved with { Title = row.Title };
        }

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

        var matched = items
            .Select(item => item with { LocalItemId = FindLocalItem(item) })
            .Take(limit)
            .ToList();

        var result = new RowResult(row.Title, row.Source, matched);
        _cache.Set(resolvedKey, result);
        return result;
    }

    /// <summary>Letterboxd list pages are scrapeable HTML; each entry has a poster with title/year in the film caption.</summary>
    private async Task<List<RowItem>?> FetchLetterboxdAsync(string listSlug)
    {
        return await _fetcher.GetOrFetchAsync(
            $"letterboxd:{listSlug}",
            CacheTtl,
            async Task<List<RowItem>?> (client, cancellationToken) =>
            {
                using var request = ResilientFetcher.BrowserGetRequest($"https://letterboxd.com/{listSlug}/");
                using var response = await client.SendAsync(request, cancellationToken);
                var html = await response.Content.ReadAsStringAsync(cancellationToken);
                return ParseLetterboxd(html);
            },
            _breaker);
    }

    internal static List<RowItem> ParseLetterboxd(string html)
    {
        var items = new List<RowItem>();
        // poster container: <li class="posteritem" ... data-film-id="..." ...> ... alt="Film title (year)" ... href="/film/title/"
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
        @"alt=""(?<name>[^""]+?)(?:\s\((?<year>\d{4})\))?""[\s\S]{0,200}?href=""/film/(?<slug>[^/""]+)/""",
        System.Text.RegularExpressions.RegexOptions.Singleline)]
    private static partial System.Text.RegularExpressions.Regex PosterItemRegex();

    /// <summary>IMDb find endpoint via MDBList-compatible IMDb list page scrape (watchlist / ls<id> lists).</summary>
    private async Task<List<RowItem>?> FetchImdbListAsync(string listId)
    {
        return await _fetcher.GetOrFetchAsync(
            $"imdblist:{listId}",
            CacheTtl,
            async Task<List<RowItem>?> (client, cancellationToken) =>
            {
                using var request = ResilientFetcher.BrowserGetRequest($"https://www.imdb.com/list/{listId}/");
                using var response = await client.SendAsync(request, cancellationToken);
                var html = await response.Content.ReadAsStringAsync(cancellationToken);
                return ParseImdbList(html);
            },
            _breaker);
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
            $"mdblist-list:{listSlug}",
            CacheTtl,
            async Task<List<RowItem>?> (client, cancellationToken) =>
            {
                var json = await client.GetStringAsync(MdbListUrls.ListItems(listSlug, apiKey), cancellationToken);
                return ParseMdbListItems(json);
            },
            _breaker);
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
            $"tmdb-list:{listId}",
            CacheTtl,
            async Task<List<RowItem>?> (client, cancellationToken) =>
                ParseTmdbList(await client.GetStringAsync(TmdbUrls.List(listId, apiKey), cancellationToken)),
            _breaker);
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

    private string? FindLocalItem(RowItem item)
    {
        try
        {
            var local = _libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery(null)
            {
                SearchTerm = item.Title,
                IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Movie, Jellyfin.Data.Enums.BaseItemKind.Series },
                Limit = 1
            }).FirstOrDefault();

            return local?.Id.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Local match failed for {Title}", item.Title);
            return null;
        }
    }

    private TimeSpan CacheTtl => TimeSpan.FromHours(_config().CacheTtlHours);
}

/// <summary>Seasonal holiday rows via TMDB keyword discovery, cached per keyword.</summary>
public sealed class SeasonalService
{
    /// <summary>Fixed freshness for the seasonal discovery — deliberately not config-driven: holiday keyword results move on a season scale, not a ratings cycle.</summary>
    private static readonly TimeSpan SeasonalTtl = TimeSpan.FromDays(2);

    private static readonly string[] DefaultKeywords = ["christmas", "halloween", "valentines-day", "summer", "thanksgiving"];

    private readonly ResilientFetcher _fetcher;
    private readonly Func<RowsFetchConfig> _config;
    private readonly CircuitBreaker _breaker = new();

    public SeasonalService(ResilientFetcher fetcher, Func<RowsFetchConfig> config)
    {
        _fetcher = fetcher;
        _config = config;
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
            $"seasonal:{kw}",
            SeasonalTtl,
            (client, cancellationToken) => FetchSeasonalItemsAsync(client, tmdbKey, kw, cancellationToken),
            _breaker);
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

    private static string? PickSeasonalKeyword()
    {
        var today = DateTime.UtcNow;
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
