namespace Jellyfin.Plugin.JellyPlay.Services.Rows;

/// <summary>
/// The fetch inputs the rows resolvers actually consume: TMDB / MDBList API
/// keys and the cache TTL. Composed at the registration root from the config
/// sections that own the values, so rows code never depends on the whole
/// ratings section (ADR-0002: config crosses a Func seam, honestly typed).
/// </summary>
public sealed record RowsFetchConfig(string TmdbApiKey, string MdbListApiKey, int CacheTtlHours);
