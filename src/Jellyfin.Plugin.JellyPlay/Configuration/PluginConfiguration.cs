using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyPlay.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public EventsConfig Events { get; set; } = new();

    public SeerrConfig Seerr { get; set; } = new();

    public RatingsConfig Ratings { get; set; } = new();

    public RowsConfig Rows { get; set; } = new();

    public AnimeConfig Anime { get; set; } = new();

    public NewsletterConfig Newsletter { get; set; } = new();

    public SyncConfig Sync { get; set; } = new();
}

public class EventsConfig
{
    public bool NewMediaEnabled { get; set; } = true;

    /// <summary>Empty = all libraries.</summary>
    public List<Guid> NewMediaEnabledLibraries { get; set; } = new();

    /// <summary>"all" or "admins".</summary>
    public string NewMediaAudience { get; set; } = "all";

    /// <summary>Episodes of one season added within this window collapse into a single event.</summary>
    public int NewMediaGroupingSeconds { get; set; } = 60;

    /// <summary>Suppress duplicate events for the same logical key within this window.</summary>
    public int DedupThresholdSeconds { get; set; } = 5;

    public bool SessionStartedEnabled { get; set; } = true;

    public bool PlaybackStartedEnabled { get; set; } = true;

    public bool UserLockedOutEnabled { get; set; } = true;
}

public class SeerrConfig
{
    /// <summary>Base URL of the Jellyseerr/Overseerr instance, e.g. http://seerr:5055.</summary>
    public string ServerUrl { get; set; } = string.Empty;

    /// <summary>Seerr API key. Server-held so clients never need it.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Jellyfin-side API key used to provision the Seerr webhook (optional).</summary>
    public string JellyfinApiKey { get; set; } = string.Empty;

    public bool AutoProvisionWebhook { get; set; } = true;

    /// <summary>Secret required on inbound webhook calls (header X-JellyPlay-Webhook-Secret).</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    public int SessionTtlHours { get; set; } = 24 * 30;
}

public class RatingsConfig
{
    public string MdbListApiKey { get; set; } = string.Empty;

    public string TmdbApiKey { get; set; } = string.Empty;

    public bool EnableImdbCharts { get; set; } = true;

    public int CacheTtlHours { get; set; } = 24;
}

public class RowsConfig
{
    public bool Enabled { get; set; } = true;

    public bool SeasonalEnabled { get; set; } = true;

    public List<CustomRowDefinition> CustomRows { get; set; } = new();
}

/// <summary>An admin-defined home row resolved from an external list.</summary>
public class CustomRowDefinition
{
    public string Title { get; set; } = string.Empty;

    /// <summary>letterboxd | imdb | mdblist | tmdb.</summary>
    public string Source { get; set; } = "letterboxd";

    /// <summary>Source-specific list identifier (slug, chart name, list id, keyword).</summary>
    public string ListId { get; set; } = string.Empty;

    public int Limit { get; set; } = 20;
}

public class AnimeConfig
{
    public bool Enabled { get; set; } = true;

    public bool EnableFillerList { get; set; } = true;

    public bool EnableTenrai { get; set; } = true;

    public string FribbListUrl { get; set; } =
        "https://raw.githubusercontent.com/Fribb/anime-lists/master/anime-list-full.json";

    public int RefreshIntervalHours { get; set; } = 24;
}

public class NewsletterConfig
{
    public string SmtpHost { get; set; } = string.Empty;

    public int SmtpPort { get; set; } = 587;

    public string SmtpUsername { get; set; } = string.Empty;

    public string SmtpPassword { get; set; } = string.Empty;

    public string FromAddress { get; set; } = string.Empty;

    public string FromName { get; set; } = "JellyPlay";

    public bool UseSsl { get; set; } = true;

    public string TestRecipient { get; set; } = string.Empty;

    /// <summary>Semicolon-separated newsletter recipients (10.11 User entities carry no email).</summary>
    public string Recipients { get; set; } = string.Empty;
}

public class SyncConfig
{
    public int MaxKeyBytes { get; set; } = 256 * 1024;

    public int MaxUserBytes { get; set; } = 5 * 1024 * 1024;

    public int MaxKeysPerUser { get; set; } = 2000;

    public int ChangeLogRetentionDays { get; set; } = 30;
}
