using System;

namespace Jellyfin.Plugin.JellyPlay;

/// <summary>
/// Versioning + feature contract between the plugin and JellyPlay clients.
/// Bump <see cref="ContractVersion"/> on any wire-format change; clients must
/// refuse contracts they do not understand and feature-gate on <c>features[]</c>.
/// </summary>
public static class JellyPlayContract
{
    public const int ContractVersion = 1;

    public const string PluginId = "d3f1a6c8-5b2e-4d7f-9a0c-6e8b1f4d2a7c";

    /// <summary>Route prefix for every plugin route except the root-level newsletter stubs.</summary>
    public const string RoutePrefix = "jellyplay";

    /// <summary>Stable feature keys reported by <c>GET jellyplay/capabilities</c>.</summary>
    public static class Features
    {
        public const string SettingsSync = "settings-sync";
        public const string DeviceProfiles = "device-profiles";
        public const string AdminDefaults = "admin-defaults";
        public const string ConfigBackup = "config-backup";
        public const string Events = "events";
        public const string Messages = "messages";
        public const string SeerrBridge = "seerr-bridge";
        public const string Newsletter = "newsletter";
        public const string Ratings = "ratings";
        public const string CustomRows = "custom-rows";
        public const string SeasonalRows = "seasonal-rows";
        public const string AnimeMarkers = "anime-markers";
        public const string Recommendations = "recommendations";
        public const string UserRatings = "user-ratings";
        public const string Bookmarks = "bookmarks";
        public const string Transcodes = "transcodes";
        public const string Push = "push";
        public const string Analytics = "analytics";
    }
}
