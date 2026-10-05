namespace Jellyfin.Plugin.JellyPlay.Configuration;

public static class ConfigurationExtensions
{
    /// <summary>Ratings module is on when any rating source is configured.</summary>
    public static bool Enabled(this RatingsConfig config)
        => !string.IsNullOrEmpty(config.MdbListApiKey) || !string.IsNullOrEmpty(config.TmdbApiKey);
}
