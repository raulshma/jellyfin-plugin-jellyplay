using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyPlay.Configuration;

public static class ConfigurationExtensions
{
    /// <summary>Ratings module is on when any rating source is configured.</summary>
    public static bool Enabled(this RatingsConfig config)
        => !string.IsNullOrEmpty(config.MdbListApiKey) || !string.IsNullOrEmpty(config.TmdbApiKey);

    /// <summary>
    /// FCM transport is ready when both the project id and a service-account
    /// key are set. Thin adapter over the one usability seam
    /// (<see cref="Services.Push.PushEligibility.IsFcmUsable"/>) so the triple
    /// check collapses to a single home.
    /// </summary>
    public static bool FcmConfigured(this PushConfig config)
        => Services.Push.PushEligibility.IsFcmUsable(config);
}

/// <summary>
/// Capability gating in one place: the conditional feature keys reported by
/// GET jellyplay/capabilities appear only when their configuration is present
/// — one map instead of a removal cascade per toggle.
/// </summary>
public static class FeatureAvailability
{
    /// <summary>Feature key → is-this-module-configured (keys absent from the map are unconditionally available).</summary>
    public static readonly IReadOnlyDictionary<string, Func<PluginConfiguration, bool>> ConditionalFeatures =
        new Dictionary<string, Func<PluginConfiguration, bool>>
        {
            [JellyPlayContract.Features.SeerrBridge] = c =>
                !string.IsNullOrEmpty(c.Seerr.ServerUrl) && !string.IsNullOrEmpty(c.Seerr.ApiKey),
            [JellyPlayContract.Features.Ratings] = c => c.Ratings.Enabled(),
            [JellyPlayContract.Features.CustomRows] = c => c.Rows.Enabled,
            [JellyPlayContract.Features.SeasonalRows] = c => c.Rows.SeasonalEnabled,
            [JellyPlayContract.Features.AnimeMarkers] = c => c.Anime.Enabled,
            [JellyPlayContract.Features.Newsletter] = c => !string.IsNullOrEmpty(c.Newsletter.SmtpHost),
            [JellyPlayContract.Features.Push] = c => c.Push.Enabled,
            [JellyPlayContract.Features.Analytics] = c => c.Analytics.Enabled
        };

    /// <summary>A null configuration removes every conditional feature.</summary>
    public static bool IsAvailable(string feature, PluginConfiguration? config)
        => config is not null
            && (!ConditionalFeatures.TryGetValue(feature, out var isAvailable) || isAvailable(config));
}
