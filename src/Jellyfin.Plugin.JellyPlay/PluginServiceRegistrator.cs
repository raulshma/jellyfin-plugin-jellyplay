using System;
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

        // Cache infrastructure
        serviceCollection.AddSingleton<FileCacheStore>();

        // Settings sync
        serviceCollection.AddSingleton<SettingsService>();

        // Events & messages
        serviceCollection.AddSingleton<EventService>();
        serviceCollection.AddSingleton<EpisodeGroupBuffer>();
        serviceCollection.AddSingleton<MessageService>();
        serviceCollection.AddHostedService<ItemAddedWatcher>();
        serviceCollection.AddScoped<IEventConsumer<global::MediaBrowser.Controller.Events.Session.SessionStartedEventArgs>, SessionStartedEvent>();
        serviceCollection.AddScoped<IEventConsumer<global::MediaBrowser.Controller.Library.PlaybackStartEventArgs>, PlaybackStartedEvent>();
        serviceCollection.AddScoped<IEventConsumer<global::Jellyfin.Data.Events.Users.UserLockedOutEventArgs>, UserLockedOutEvent>();

        // Seerr bridge
        serviceCollection.AddSingleton<SeerrSessionService>();
        serviceCollection.AddSingleton<SeerrProxyService>();
        serviceCollection.AddSingleton<SeerrWebhookProvisioner>();
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
    }
}
