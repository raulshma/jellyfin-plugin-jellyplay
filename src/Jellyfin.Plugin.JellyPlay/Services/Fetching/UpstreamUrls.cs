using System;

namespace Jellyfin.Plugin.JellyPlay.Services.Fetching;

/// <summary>
/// URL builders for the MDBList API — shared by the ratings module (find /
/// key info) and the rows module (list items) so the wire shape lives in one
/// place. Builders only, no I/O; each module keeps its own breaker and key
/// handling.
/// </summary>
public static class MdbListUrls
{
    private const string BaseUrl = "https://api.mdblist.com";

    /// <summary>Aggregated ratings for one IMDb id.</summary>
    public static string Find(string imdbId, string apiKey) => $"{BaseUrl}/find/{imdbId}?apikey={apiKey}";

    /// <summary>Items of one official MDBList list.</summary>
    public static string ListItems(string listSlug, string apiKey) => $"{BaseUrl}/lists/{listSlug}/items?apikey={apiKey}";

    /// <summary>Key-holder account probe (key validity check).</summary>
    public static string User(string apiKey) => $"{BaseUrl}/user?apikey={apiKey}";
}

/// <summary>
/// URL builders for the TMDB v3 API — shared by the ratings module (season /
/// next-episode) and the rows module (official lists, seasonal keyword
/// discovery). Builders only, no I/O.
/// </summary>
public static class TmdbUrls
{
    private const string BaseUrl = "https://api.themoviedb.org/3";

    /// <summary>One season's episodes of a series.</summary>
    public static string Season(string tmdbId, int seasonNumber, string? apiKey) => $"{BaseUrl}/tv/{tmdbId}/season/{seasonNumber}?api_key={apiKey}";

    /// <summary>Series detail (next-episode-to-air lives here).</summary>
    public static string Series(string tmdbId, string? apiKey) => $"{BaseUrl}/tv/{tmdbId}?api_key={apiKey}";

    /// <summary>One official TMDB list's items.</summary>
    public static string List(string listId, string apiKey) => $"{BaseUrl}/list/{Uri.EscapeDataString(listId)}?api_key={apiKey}";

    /// <summary>Keyword-driven movie discovery, most-popular first (the seasonal rows source).</summary>
    public static string DiscoverMoviesByKeyword(string keyword, string apiKey)
        => $"{BaseUrl}/discover/movie?api_key={apiKey}&with_keywords={Uri.EscapeDataString(keyword)}&sort_by=popularity.desc&vote_count.gte=50";
}
