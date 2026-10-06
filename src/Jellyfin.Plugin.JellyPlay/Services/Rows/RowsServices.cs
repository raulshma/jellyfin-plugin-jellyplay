using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Configuration;
using Jellyfin.Plugin.JellyPlay.Services.Cache;
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
    private readonly IHttpClientFactory _httpFactory;
    private readonly FileCacheStore _cache;
    private readonly ILibraryManager _libraryManager;
    private readonly CircuitBreaker _breaker = new();
    private readonly ILogger<CustomRowsService> _logger;

    public CustomRowsService(IHttpClientFactory httpFactory, FileCacheStore cache, ILibraryManager libraryManager, ILogger<CustomRowsService> logger)
    {
        _httpFactory = httpFactory;
        _cache = cache;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public async Task<RowResult?> ResolveAsync(CustomRowDefinition row)
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

        var matched = items
            .Select(item => item with { LocalItemId = FindLocalItem(item) })
            .Take(row.Limit <= 0 ? 20 : row.Limit)
            .ToList();

        return new RowResult(row.Title, row.Source, matched);
    }

    /// <summary>Letterboxd list pages are scrapeable HTML; each entry has a poster with title/year in the film caption.</summary>
    private async Task<List<RowItem>?> FetchLetterboxdAsync(string listSlug)
    {
        var cacheKey = $"letterboxd:{listSlug}";
        var cached = _cache.Get<List<RowItem>>(cacheKey, CacheTtl);
        if (cached is not null)
        {
            return cached;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (_breaker.IsOpen(now))
        {
            _logger.LogDebug("Letterboxd circuit open; skipping {List}", listSlug);
            return null;
        }

        try
        {
            var client = _httpFactory.CreateClient("JellyPlayHttpClient");
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://letterboxd.com/{listSlug}/");
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            var html = await (await client.SendAsync(request)).Content.ReadAsStringAsync();
            var items = ParseLetterboxd(html);
            _cache.Set(cacheKey, items);
            _breaker.RecordSuccess(now);
            return items;
        }
        catch (Exception ex)
        {
            _breaker.RecordFailure(now);
            _logger.LogWarning(ex, "Letterboxd list fetch failed: {List}", listSlug);
            return null;
        }
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
        var cacheKey = $"imdblist:{listId}";
        var cached = _cache.Get<List<RowItem>>(cacheKey, CacheTtl);
        if (cached is not null)
        {
            return cached;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (_breaker.IsOpen(now))
        {
            _logger.LogDebug("IMDb list circuit open; skipping {List}", listId);
            return null;
        }

        try
        {
            var client = _httpFactory.CreateClient("JellyPlayHttpClient");
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://www.imdb.com/list/{listId}/");
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            var html = await (await client.SendAsync(request)).Content.ReadAsStringAsync();
            var items = ParseImdbList(html);
            _cache.Set(cacheKey, items);
            _breaker.RecordSuccess(now);
            return items;
        }
        catch (Exception ex)
        {
            _breaker.RecordFailure(now);
            _logger.LogWarning(ex, "IMDb list fetch failed: {List}", listId);
            return null;
        }
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
    private async Task<List<RowItem>?> FetchMdbListAsync(string listSlug)
    {
        var apiKey = JellyPlayPlugin.Instance!.Configuration.Ratings.MdbListApiKey;
        if (string.IsNullOrEmpty(apiKey))
        {
            return null;
        }

        var cacheKey = $"mdblist-list:{listSlug}";
        var cached = _cache.Get<List<RowItem>>(cacheKey, CacheTtl);
        if (cached is not null)
        {
            return cached;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (_breaker.IsOpen(now))
        {
            _logger.LogDebug("MDBList circuit open; skipping {List}", listSlug);
            return null;
        }

        try
        {
            var client = _httpFactory.CreateClient("JellyPlayHttpClient");
            var json = await client.GetStringAsync($"https://api.mdblist.com/lists/{listSlug}/items?apikey={apiKey}");
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

            _cache.Set(cacheKey, items);
            _breaker.RecordSuccess(now);
            return items;
        }
        catch (Exception ex)
        {
            _breaker.RecordFailure(now);
            _logger.LogWarning(ex, "MDBList list fetch failed: {List}", listSlug);
            return null;
        }
    }

    /// <summary>TMDB official lists (GET /list/{id}, v3 api key from the ratings config — same client/pattern as TmdbRatingsService).</summary>
    private async Task<List<RowItem>?> FetchTmdbListAsync(string listId)
    {
        var apiKey = JellyPlayPlugin.Instance!.Configuration.Ratings.TmdbApiKey;
        if (string.IsNullOrEmpty(apiKey))
        {
            return null;
        }

        var cacheKey = $"tmdb-list:{listId}";
        var cached = _cache.Get<List<RowItem>>(cacheKey, CacheTtl);
        if (cached is not null)
        {
            return cached;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (_breaker.IsOpen(now))
        {
            _logger.LogDebug("TMDB circuit open; skipping list {List}", listId);
            return null;
        }

        try
        {
            var client = _httpFactory.CreateClient("JellyPlayHttpClient");
            var json = await client.GetStringAsync($"https://api.themoviedb.org/3/list/{Uri.EscapeDataString(listId)}?api_key={apiKey}");
            var items = ParseTmdbList(json);
            _cache.Set(cacheKey, items);
            _breaker.RecordSuccess(now);
            return items;
        }
        catch (Exception ex)
        {
            _breaker.RecordFailure(now);
            _logger.LogWarning(ex, "TMDB list fetch failed: {List}", listId);
            return null;
        }
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

    private static TimeSpan CacheTtl => TimeSpan.FromHours(JellyPlayPlugin.Instance!.Configuration.Ratings.CacheTtlHours);
}

/// <summary>Seasonal holiday rows via TMDB keyword discovery, cached per keyword.</summary>
public sealed class SeasonalService
{
    private static readonly string[] DefaultKeywords = ["christmas", "halloween", "valentines-day", "summer", "thanksgiving"];

    private readonly IHttpClientFactory _httpFactory;
    private readonly FileCacheStore _cache;
    private readonly CircuitBreaker _breaker = new();
    private readonly ILogger<SeasonalService> _logger;

    public SeasonalService(IHttpClientFactory httpFactory, FileCacheStore cache, ILogger<SeasonalService> logger)
    {
        _httpFactory = httpFactory;
        _cache = cache;
        _logger = logger;
    }

    public async Task<RowResult?> GetSeasonalRow(string? keyword)
    {
        var tmdbKey = JellyPlayPlugin.Instance!.Configuration.Ratings.TmdbApiKey;
        if (string.IsNullOrEmpty(tmdbKey))
        {
            return null;
        }

        var kw = string.IsNullOrEmpty(keyword) ? PickSeasonalKeyword() : keyword!;
        if (kw is null)
        {
            return null;
        }

        var cacheKey = $"seasonal:{kw}";
        var cached = _cache.Get<List<RowItem>>(cacheKey, TimeSpan.FromDays(2));
        if (cached is not null)
        {
            return new RowResult(TitleFor(kw), "tmdb", cached);
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (_breaker.IsOpen(now))
        {
            _logger.LogDebug("TMDB circuit open; skipping seasonal row {Keyword}", kw);
            return null;
        }

        try
        {
            var client = _httpFactory.CreateClient("JellyPlayHttpClient");
            var json = await client.GetStringAsync(
                $"https://api.themoviedb.org/3/discover/movie?api_key={tmdbKey}&with_keywords={System.Uri.EscapeDataString(kw)}&sort_by=popularity.desc&vote_count.gte=50");
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

            _cache.Set(cacheKey, items);
            _breaker.RecordSuccess(now);
            return new RowResult(TitleFor(kw), "tmdb", items);
        }
        catch (Exception ex)
        {
            _breaker.RecordFailure(now);
            _logger.LogWarning(ex, "Seasonal row failed for keyword {Keyword}", kw);
            return null;
        }
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
