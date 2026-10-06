using System;
using Jellyfin.Data;
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
        serviceCollection.AddHttpClient(
            "JellyPlayHttpClient",
            client =>
            {
                client.Timeout = TimeSpan.FromSeconds(15);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("jellyfin-plugin-jellyplay/1.0");
            });

        // Realtime
        serviceCollection.AddSingleton<SseHub>();
        serviceCollection.AddSingleton(_ => new Func<Configuration.EventsConfig>(() => JellyPlayPlugin.Instance!.Configuration.Events));
        serviceCollection.AddSingleton(_ => new Func<Configuration.SyncConfig>(() => JellyPlayPlugin.Instance!.Configuration.Sync));
        serviceCollection.AddSingleton(_ => new Func<Configuration.PushConfig>(() => JellyPlayPlugin.Instance!.Configuration.Push));
        serviceCollection.AddSingleton(_ => new Func<Configuration.AnalyticsConfig>(() => JellyPlayPlugin.Instance!.Configuration.Analytics));

        // Push notifications (plugin is the push server; fire-and-forget).
        // FcmTokenProvider mints OAuth2 tokens for the fcm transport from the
        // admin-pasted service-account key (never logged, never surfaced).
        serviceCollection.AddSingleton<Services.Push.FcmTokenProvider>();
        serviceCollection.AddSingleton<Services.Push.PushDispatcher>();

        // Persistence — plugin instance owns the lazy database singleton
        serviceCollection.AddSingleton(_ => JellyPlayPlugin.Instance!.Database);

        // Cache infrastructure
        serviceCollection.AddSingleton<FileCacheStore>();

        // Settings sync
        serviceCollection.AddSingleton<SettingsService>();
        serviceCollection.AddSingleton<SyncInsightsService>();

        // Events & messages
        serviceCollection.AddSingleton(sp => new EventService(
            sp.GetRequiredService<SseHub>(),
            () => JellyPlayPlugin.Instance!.Configuration.Events,
            () => sp.GetRequiredService<IUserManager>().GetUsers()
                .Where(user => user.HasPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsAdministrator))
                .Select(user => user.Id.ToString())
                .ToList(),
            sp.GetRequiredService<ILogger<EventService>>(),
            sp.GetRequiredService<Services.Push.PushDispatcher>()));
        serviceCollection.AddSingleton<EpisodeGroupBuffer>();
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
        serviceCollection.AddSingleton<SeerrSessionService>();
        serviceCollection.AddSingleton<SeerrProxyService>();
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
        // anonymous seerr webhook intake 30/min per remote client)
        serviceCollection.AddSingleton<Services.Admin.SettingsRateLimiter>();
        serviceCollection.AddSingleton<Services.Admin.BroadcastRateLimiter>();
        serviceCollection.AddSingleton<Services.Admin.WebhookRateLimiter>();

        // Jellyfin-12 similar-items pipeline registration (reflection-guarded; no-op on 10.11)
        serviceCollection.AddHostedService<Services.Recommendations.SimilarItemsProviderManager>();
    }
}
