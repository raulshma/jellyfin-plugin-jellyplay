using System;
using System.Net.Http;
using Jellyfin.Plugin.JellyPlay.Realtime;
using Jellyfin.Plugin.JellyPlay.Services.Admin;
using Jellyfin.Plugin.JellyPlay.Services.Anime;
using Jellyfin.Plugin.JellyPlay.Services.Cache;
using Jellyfin.Plugin.JellyPlay.Services.Events;
using Jellyfin.Plugin.JellyPlay.Services.Messages;
using Jellyfin.Plugin.JellyPlay.Services.Newsletter;
using Jellyfin.Plugin.JellyPlay.Services.Ratings;
using Jellyfin.Plugin.JellyPlay.Services.Recommendations;
using Jellyfin.Plugin.JellyPlay.Services.Rows;
using Jellyfin.Plugin.JellyPlay.Services.Seerr;
using Jellyfin.Plugin.JellyPlay.Services.Settings;
using Jellyfin.Plugin.JellyPlay.Services.Transcodes;
using Jellyfin.Plugin.JellyPlay.Services.UserData;
using Jellyfin.Plugin.JellyPlay.Storage;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyPlay;

/// <summary>Registers every plugin service with the host's DI container.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Resilient-fetch transport (every TMDB/MDBList/IMDb/Letterboxd/anime
        // fetch + the Seerr proxy): the same connection policy the push client
        // below uses — pooled connections recycle on a 10-minute lifetime and
        // connects fail fast at 5s — with the 15s overall request budget and
        // the plugin UA on the client itself.
        serviceCollection.AddHttpClient(
            "JellyPlayHttpClient",
            client =>
            {
                client.Timeout = TimeSpan.FromSeconds(15);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("jellyfin-plugin-jellyplay/1.0");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                ConnectTimeout = TimeSpan.FromSeconds(5)
            });

        // Push transports (dispatcher + FCM token exchange): one named pooled
        // client with the connection budgets the push fire-and-forget expects —
        // each request is bounded by its own 10s CTS, so the client timeout
        // itself stays infinite.
        serviceCollection.AddHttpClient(
            Services.Push.PushPayloads.HttpClientName,
            client =>
            {
                client.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.UserAgent.ParseAdd("jellyfin-plugin-jellyplay/1.0");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                ConnectTimeout = TimeSpan.FromSeconds(Services.Push.PushDispatcher.TimeoutSeconds)
            });

        // Realtime
        serviceCollection.AddSingleton<SseHub>();
        // Config seam: services receive the config section they need as a
        // Func — never by reading JellyPlayPlugin.Instance inline. The
        // composition root (this registrar) is the only place that touches
        // the singleton besides Plugin.cs itself.
        serviceCollection.AddSingleton(_ => new Func<Configuration.PluginConfiguration>(() => JellyPlayPlugin.Instance!.Configuration));
        serviceCollection.AddSingleton(_ => new Func<Configuration.EventsConfig>(() => JellyPlayPlugin.Instance!.Configuration.Events));
        serviceCollection.AddSingleton(_ => new Func<Configuration.SyncConfig>(() => JellyPlayPlugin.Instance!.Configuration.Sync));
        serviceCollection.AddSingleton(_ => new Func<Configuration.PushConfig>(() => JellyPlayPlugin.Instance!.Configuration.Push));
        serviceCollection.AddSingleton(_ => new Func<Configuration.AnalyticsConfig>(() => JellyPlayPlugin.Instance!.Configuration.Analytics));
        serviceCollection.AddSingleton(_ => new Func<Configuration.SeerrConfig>(() => JellyPlayPlugin.Instance!.Configuration.Seerr));
        serviceCollection.AddSingleton(_ => new Func<Configuration.RatingsConfig>(() => JellyPlayPlugin.Instance!.Configuration.Ratings));
        serviceCollection.AddSingleton(_ => new Func<Configuration.NewsletterConfig>(() => JellyPlayPlugin.Instance!.Configuration.Newsletter));
        serviceCollection.AddSingleton(_ => new Func<Configuration.AnimeConfig>(() => JellyPlayPlugin.Instance!.Configuration.Anime));
        serviceCollection.AddSingleton(_ => new Func<Configuration.CacheConfig>(() => JellyPlayPlugin.Instance!.Configuration.Cache));
        serviceCollection.AddSingleton(_ => new Func<Configuration.RowsConfig>(() => JellyPlayPlugin.Instance!.Configuration.Rows));

        // Push notifications (plugin is the push server; fire-and-forget).
        // FcmTokenProvider mints OAuth2 tokens for the fcm transport from the
        // admin-pasted service-account key (never logged, never surfaced).
        serviceCollection.AddSingleton<Services.Push.FcmTokenProvider>();
        serviceCollection.AddSingleton<Services.Push.PushDispatcher>();
        // The one SSE+push pairing every broadcast leverages (see
        // Services/Shared/NotificationFanout): needs only the hub + dispatcher.
        serviceCollection.AddSingleton<Services.Shared.NotificationFanout>();

        // Device registry: registration contract (push attach/preserve/detach,
        // FCM gate), the caps-gated revoke+wipe orchestration and owner-scoped
        // listing behind one module (the wipe's data half is the settings
        // service's — history op and SSE fan-out live there).
        serviceCollection.AddSingleton(sp => new Services.Devices.DeviceRegistryService(
            sp.GetRequiredService<JellyPlayDatabase>(),
            sp.GetRequiredService<Func<Configuration.PushConfig>>(),
            sp.GetRequiredService<Services.Settings.SettingsService>()));

        // Persistence — plugin instance owns the lazy database singleton
        serviceCollection.AddSingleton(_ => JellyPlayPlugin.Instance!.Database);

        // Cache infrastructure
        serviceCollection.AddSingleton(sp => new FileCacheStore(
            sp.GetRequiredService<ILogger<Services.Cache.FileCacheStore>>(),
            cacheDirectory: System.IO.Path.Combine(JellyPlayPlugin.Instance!.DataDirectory, "cache"),
            maxSizeMegabytes: () => sp.GetRequiredService<Func<Configuration.CacheConfig>>().Invoke().MaxSizeMegabytes));

        // Resilient fetch: the one pipeline for external HTTP sources —
        // file cache → circuit breaker → fetch → parse (see GLOSSARY.md).
        serviceCollection.AddSingleton<Services.Fetching.ResilientFetcher>();

        // Settings sync
        serviceCollection.AddSingleton<SnapshotService>();
        serviceCollection.AddSingleton<SettingsService>();
        serviceCollection.AddSingleton<SyncInsightsService>();

        // Admin identity: display names (raw-id fallback) for the admin
        // overviews and the administrator set behind audience "admins" — one
        // module, so the resolution rule and the "who is an admin" query each
        // have a single home.
        serviceCollection.AddSingleton<Services.Admin.AdminUsers>();
        // The admin-id set crosses to EventService as the same Func seam the
        // config sections use (ADR-0002): EventService itself is constructed
        // by DI — no factory here reads the plugin singleton inline.
        serviceCollection.AddSingleton(sp => new Func<IReadOnlyList<string>>(() => sp.GetRequiredService<Services.Admin.AdminUsers>().AdminUserIds));

        // Events & messages — constructor-injected (hub, config Func, admin-ids
        // Func, logger, dispatcher) straight from this container. The
        // pipeline owns the grouping buffer internally (TimeProvider +
        // virtual-folder Func seams, 10s tick preserved); the services that
        // leverage its seams follow.
        serviceCollection.AddSingleton(sp => new NewMediaPipeline(
            sp.GetRequiredService<Func<Configuration.EventsConfig>>(),
            TimeProvider.System,
            () => sp.GetRequiredService<ILibraryManager>().GetVirtualFolders(),
            sp.GetRequiredService<ILogger<NewMediaPipeline>>()));
        serviceCollection.AddSingleton<Services.Shared.NotificationFanout>();
        serviceCollection.AddSingleton<EventService>();
        serviceCollection.AddSingleton<MessageService>();
        serviceCollection.AddHostedService<ItemAddedWatcher>();
        serviceCollection.AddScoped<IEventConsumer<global::MediaBrowser.Controller.Events.Session.SessionStartedEventArgs>, SessionStartedEvent>();
        serviceCollection.AddScoped<IEventConsumer<global::MediaBrowser.Controller.Library.PlaybackStartEventArgs>, PlaybackStartedEvent>();
        serviceCollection.AddScoped<IEventConsumer<global::Jellyfin.Data.Events.Users.UserLockedOutEventArgs>, UserLockedOutEvent>();

        // Playback analytics (recording is fire-and-forget; see AnalyticsService)
        serviceCollection.AddSingleton<Services.Analytics.AnalyticsService>();
        serviceCollection.AddScoped<IEventConsumer<global::MediaBrowser.Controller.Library.PlaybackStopEventArgs>, Services.Analytics.PlaybackStoppedAnalyticsConsumer>();
        serviceCollection.AddScoped<IEventConsumer<global::MediaBrowser.Controller.Library.PlaybackProgressEventArgs>, Services.Analytics.PlaybackProgressAnalyticsConsumer>();

        // Seerr bridge
        serviceCollection.AddSingleton<Services.Seerr.HttpClientSeerrSender>();
        serviceCollection.AddSingleton<Services.Seerr.SeerrSender>(sp =>
        {
            var impl = sp.GetRequiredService<Services.Seerr.HttpClientSeerrSender>();
            return (request, cookieContainer, cancellationToken) => impl.SendAsync(request, cookieContainer, cancellationToken);
        });
        serviceCollection.AddSingleton<SeerrSessionService>();
        serviceCollection.AddSingleton<SeerrProxyService>();
        serviceCollection.AddSingleton<SeerrWebhookIntake>();
        serviceCollection.AddSingleton<SeerrWebhookProvisioner>();
        serviceCollection.AddSingleton(sp => Services.Seerr.SecretBox.LoadOrCreate(JellyPlayPlugin.Instance!.DataDirectory));
        serviceCollection.AddHostedService<SeerrProvisioningHostedService>();

        // Newsletter
        serviceCollection.AddSingleton<INewsletterSender, SmtpNewsletterSender>();
        serviceCollection.AddSingleton<NewsletterService>();

        // Ratings
        serviceCollection.AddSingleton<MdbListService>();
        serviceCollection.AddSingleton<TmdbRatingsService>();
        serviceCollection.AddSingleton<ImdbChartsService>();

        // Rows
        serviceCollection.AddSingleton<CustomRowsService>();
        serviceCollection.AddSingleton<SeasonalService>();

        // Anime
        serviceCollection.AddSingleton<AnimeMarkersService>();

        // Recommendations
        serviceCollection.AddSingleton<SimilarItemsService>();

        // User data
        serviceCollection.AddSingleton<BookmarkService>();
        serviceCollection.AddSingleton<UserRatingsService>();

        // Transcodes
        serviceCollection.AddSingleton<TranscodeInsightsService>();

        // Admin
        serviceCollection.AddSingleton<AdminDefaultsService>();
        serviceCollection.AddSingleton<ConfigBackupService>();
        // Mutating-route abuse containment (settings POST 30/min per user, broadcast 10/min per admin,
        // anonymous seerr webhook intake 30/min per remote client, admin pushDefaults 5/min per admin):
        // one registry module behind a RateLimiterKind seam, not four one-line subclasses.
        serviceCollection.AddSingleton<Services.Admin.RateLimiterRegistry>();

        // Jellyfin-12 similar-items pipeline registration (reflection-guarded; no-op on 10.11).
        // One singleton shared by both faces: the controller injects the concrete
        // type, the host starts it through IHostedService.
        serviceCollection.AddSingleton<Services.Recommendations.SimilarItemsProviderManager>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<Services.Recommendations.SimilarItemsProviderManager>());
    }
}
