using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Configuration;
using Jellyfin.Plugin.JellyPlay.Services.Cache;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyPlay.Services.Anime;

/// <summary>Marker kinds served to clients for episode badges.</summary>
public sealed record AnimeMarker(string Type, int EpisodeNumber, string? Note);

public sealed record SeriesMarkers(string SeriesId, string? AniListId, string? MalId, IReadOnlyList<AnimeMarker> Markers);

/// <summary>
/// Anime episode markers: filler/mixed/canon from AnimeFillerList, recap
/// markers from Tenrai, cross-source id mapping via the Fribb anime-list JSON
/// (anilist/mal/thetvdb/tmdb). Cached per series.
/// </summary>
public sealed partial class AnimeMarkersService
{
    private const string FillerListBase = "https://www.animefillerlist.com/shows/";
    private const string TenraiBase = "https://api.tenrai.org/v1/recaps";

    private readonly IHttpClientFactory _httpFactory;
    private readonly FileCacheStore _cache;
    private readonly ILogger<AnimeMarkersService> _logger;
    private readonly Dictionary<string, FribbEntry> _fribbIndex = new(StringComparer.Ordinal);
    private readonly object _fribbLock = new();
    private DateTime _fribbLoadedUtc;

    public AnimeMarkersService(IHttpClientFactory httpFactory, FileCacheStore cache, ILogger<AnimeMarkersService> logger)
    {
        _httpFactory = httpFactory;
        _cache = cache;
        _logger = logger;
    }

    public bool IsEnabled => JellyPlayPlugin.Instance!.Configuration.Anime.Enabled;

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

        var markers = new List<AnimeMarker>();

        if (config.EnableFillerList && !string.IsNullOrEmpty(providerSeriesId))
        {
            markers.AddRange(await FetchFillerMarkers(providerSeriesId));
        }

        if (config.EnableTenrai && !string.IsNullOrEmpty(providerSeriesId))
        {
            markers.AddRange(await FetchTenraiRecaps(providerSeriesId));
        }

        if (markers.Count == 0)
        {
            return null;
        }

        var mapping = await ResolveMappingAsync(providerSeriesId);
        var result = new SeriesMarkers(seriesId, mapping?.AniListId, mapping?.MalId, markers.OrderBy(marker => marker.EpisodeNumber).ToList());
        _cache.Set(cacheKey, result);
        return result;
    }

    /// <summary>Resolves episode marker rows for episode lists (client batches by series).</summary>
    public async Task<IReadOnlyList<AnimeMarker>> GetEpisodeMarkers(string seriesId, int fromEpisode, int toEpisode)
    {
        var markers = await GetSeriesMarkers(seriesId, seriesId);
        return markers?.Markers
            .Where(marker => marker.EpisodeNumber >= fromEpisode && marker.EpisodeNumber <= toEpisode)
            .ToList() ?? new List<AnimeMarker>();
    }

    /// <summary>Canon/episode count summary for a series (used for badges on series cards).</summary>
    public async Task<(int Filler, int Recaps, int Total)?> GetSeriesSummary(string seriesId)
    {
        var markers = await GetSeriesMarkers(seriesId, seriesId);
        if (markers is null)
        {
            return null;
        }

        var filler = markers.Markers.Count(marker => marker.Type == "filler" || marker.Type == "mixed");
        var recaps = markers.Markers.Count(marker => marker.Type == "recap");
        return (filler, recaps, markers.Markers.Count);
    }

    private async Task<List<AnimeMarker>> FetchFillerMarkers(string providerSeriesId)
    {
        try
        {
            var slug = SlugFromProvider(providerSeriesId);
            var client = _httpFactory.CreateClient("JellyPlayHttpClient");
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{FillerListBase}{slug}");
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            var html = await (await client.SendAsync(request)).Content.ReadAsStringAsync();
            return ParseFillerList(html);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FillerList fetch failed for {Series}", providerSeriesId);
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

    private async Task<List<AnimeMarker>> FetchTenraiRecaps(string providerSeriesId)
    {
        try
        {
            var client = _httpFactory.CreateClient("JellyPlayHttpClient");
            var json = await client.GetStringAsync($"{TenraiBase}?anilist_id={Uri.EscapeDataString(providerSeriesId)}");
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
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Tenrai recaps failed for {Series}", providerSeriesId);
            return new List<AnimeMarker>();
        }
    }

    /// <summary>Loads (and periodically refreshes) the Fribb anime-list mapping, then returns entries for a provider id.</summary>
    private async Task<FribbEntry?> ResolveMappingAsync(string providerSeriesId)
    {
        await EnsureFribbIndexAsync();
        lock (_fribbLock)
        {
            if (_fribbIndex.TryGetValue(providerSeriesId, out var hit))
            {
                return hit;
            }
        }

        // providerSeriesId may itself be a TVDB id — try tvdb key too.
        lock (_fribbLock)
        {
            return _fribbIndex.Values.FirstOrDefault(entry => entry.TvdbId == providerSeriesId);
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
                if (tvdb is not null)
                {
                    index[tvdb] = fribb;
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

    private static string SlugFromProvider(string providerSeriesId)
    {
        // AnimeFillerList uses kebab-case titles; admins map series via a config
        // name match. Strip digits and normalize.
        var slug = providerSeriesId.ToLowerInvariant().Replace(' ', '-');
        slug = new string(slug.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
        return slug.Trim('-');
    }

    private sealed record FribbEntry(string? AniListId, string? MalId, string? TvdbId, string? TmdbId);
}
