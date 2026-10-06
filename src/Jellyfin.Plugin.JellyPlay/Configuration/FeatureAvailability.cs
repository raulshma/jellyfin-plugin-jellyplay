namespace Jellyfin.Plugin.JellyPlay.Configuration;

public static class ConfigurationExtensions
{
    /// <summary>Ratings module is on when any rating source is configured.</summary>
    public static bool Enabled(this RatingsConfig config)
        => !string.IsNullOrEmpty(config.MdbListApiKey) || !string.IsNullOrEmpty(config.TmdbApiKey);

    /// <summary>FCM transport is ready when both the project id and a service-account key are set.</summary>
    public static bool FcmConfigured(this PushConfig config)
        => !string.IsNullOrWhiteSpace(config.FcmProjectId)
           && !string.IsNullOrWhiteSpace(config.FcmServiceAccountJson);
}
