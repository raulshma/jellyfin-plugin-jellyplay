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

    public CacheConfig Cache { get; set; } = new();

    public PushConfig Push { get; set; } = new();

    public AnalyticsConfig Analytics { get; set; } = new();
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

    /// <summary>
    /// Externally reachable Jellyfin base URL used for the Seerr webhook
    /// receiver (scheme://host[:port][/pathBase]). Empty disables startup
    /// auto-provision; POST jellyplay/seerr/reprovision derives and persists it
    /// from the incoming admin request.
    /// </summary>
    public string JellyfinBaseUrl { get; set; } = string.Empty;

    public bool AutoProvisionWebhook { get; set; } = true;

    /// <summary>Secret required on inbound webhook calls (header X-JellyPlay-Webhook-Secret).</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>
    /// Trust the X-Forwarded-For first hop when keying webhook rate limits.
    /// Only enable behind a reverse proxy that overwrites (not appends to) the
    /// header — otherwise clients can spoof their rate-limit identity.
    /// </summary>
    public bool TrustProxyHeaders { get; set; }

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

    /// <summary>
    /// Admin-managed provider-id mappings that override the automatic
    /// derivation for one series (resolution precedence: override &gt;
    /// provider-id auto-derive &gt; name-slug fallback).
    /// </summary>
    public List<AnimeSeriesOverride> SeriesOverrides { get; set; } = new();
}

/// <summary>
/// An admin-defined provider-id pin for one Jellyfin series (matched by item
/// id). At least one provider id must be set; <see cref="Label"/> is an
/// optional admin-facing note and never used in resolution. Modeled after
/// <see cref="CustomRowDefinition"/> so the YAML round-trip and the dashboard
/// editor both work unchanged.
/// </summary>
public class AnimeSeriesOverride
{
    /// <summary>The Jellyfin series item id this override applies to.</summary>
    public string SeriesId { get; set; } = string.Empty;

    public string? AniListId { get; set; }

    public string? MalId { get; set; }

    /// <summary>Optional admin-facing note (not used in resolution).</summary>
    public string? Label { get; set; }
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

    /// <summary>
    /// Per-namespace byte quotas (schema v7), enforced across the user's
    /// profiles on top of the per-user total — a single runaway namespace can
    /// no longer starve the others. Defaults leave headroom under the 5 MB
    /// user total. Namespaces absent from the map are bounded only by
    /// MaxUserBytes; raising MaxUserBytes above the namespace sums has no
    /// effect on these caps.
    /// </summary>
    /// <remarks>
    /// Jellyfin's XML config serializer cannot reflect <c>IDictionary</c>
    /// members — persist through <see cref="NamespaceQuotaBytesSerialized"/>.
    /// </remarks>
    [System.Xml.Serialization.XmlIgnore]
    public Dictionary<string, int> NamespaceQuotaBytes { get; set; } = new()
    {
        ["prefs"] = 3 * 1024 * 1024,
        ["reader"] = 1536 * 1024,
        ["search"] = 64 * 1024,
        ["cw"] = 64 * 1024,
        ["homelayout"] = 64 * 1024
    };

    /// <summary>XML persistence surrogate for <see cref="NamespaceQuotaBytes"/> — "ns=bytes" comma pairs.</summary>
    public string NamespaceQuotaBytesSerialized
    {
        get => string.Join(",", NamespaceQuotaBytes.Select(kv => kv.Key + "=" + kv.Value));
        set => NamespaceQuotaBytes = (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(pair => pair.Split('=', 2))
            .Where(parts => parts.Length == 2 && int.TryParse(parts[1], out _))
            .ToDictionary(p => p[0], p => int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture));
    }

    public int ChangeLogRetentionDays { get; set; } = 30;

    /// <summary>How long recorded sync operations (push/pull/reset) are kept before the daily prune deletes them.</summary>
    public int HistoryRetentionDays { get; set; } = 30;

    /// <summary>How old restore-point snapshots may get before the daily prune deletes them (a rolling keep-last window also applies at insert time).</summary>
    public int SnapshotRetentionDays { get; set; } = 30;
}

public class CacheConfig
{
    /// <summary>Total size cap (MB) for the plugin's file cache directory; the oldest entries are evicted first when exceeded.</summary>
    public int MaxSizeMegabytes { get; set; } = 256;
}

/// <summary>
/// Plugin-side push notifications: the plugin is the push server, fanning out
/// new-media/broadcast/message notifications to device-registered ntfy and
/// generic UnifiedPush HTTP endpoints, and (when configured) through Google
/// FCM for Play-Store client builds. Fire-and-forget, no delivery guarantee.
/// </summary>
public class PushConfig
{
    /// <summary>Master switch; off (default) removes the "push" capability key and skips every dispatch.</summary>
    public bool Enabled { get; set; }

    /// <summary>Firebase project id; the fcm push kind is only accepted when this AND the service-account key are set.</summary>
    public string FcmProjectId { get; set; } = string.Empty;

    /// <summary>
    /// RAW Firebase service-account key JSON (the full file content, pasted).
    /// Server-held secret: it must never appear in any log or API response —
    /// the admin overview reports only a boolean <c>fcmConfigured</c>.
    /// </summary>
    public string FcmServiceAccountJson { get; set; } = string.Empty;
}

/// <summary>
/// Server-side playback activity recording (from host session events) and the
/// admin reporting surface over it. The `analytics` feature key is only
/// advertised while enabled; disabling stops recording and the daily
/// maintenance pass purges all recorded history (raw + rollups).
/// </summary>
public class AnalyticsConfig
{
    /// <summary>Master switch; off (default) removes the "analytics" capability key and skips every recording pass.</summary>
    public bool Enabled { get; set; }

    /// <summary>Raw playback-session rows older than this are pruned daily; the derived daily rollups are retained forever.</summary>
    public int RawRetentionDays { get; set; } = 90;
}
