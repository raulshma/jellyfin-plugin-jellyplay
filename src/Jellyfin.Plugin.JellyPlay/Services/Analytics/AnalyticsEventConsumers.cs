using System.Threading.Tasks;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.JellyPlay.Services.Analytics;

/// <summary>
/// Feeds playback-stop events to the analytics recorder. Registered as
/// IEventConsumer exactly like the session-started/playback-started consumers —
/// the host's event manager dispatches by exact event type, so stop events
/// reach IEventConsumer&lt;PlaybackStopEventArgs&gt; only. The recorder swallows
/// its own failures; this class never throws into the host pipeline either.
/// </summary>
public sealed class PlaybackStoppedAnalyticsConsumer : IEventConsumer<PlaybackStopEventArgs>
{
    private readonly AnalyticsService _analytics;

    public PlaybackStoppedAnalyticsConsumer(AnalyticsService analytics)
    {
        _analytics = analytics;
    }

    public Task OnEvent(PlaybackStopEventArgs? eventArgs)
    {
        if (eventArgs is not null)
        {
            _analytics.OnPlaybackStopped(eventArgs);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Feeds playback-progress events to the analytics recorder (in-flight tracking + graceful close).</summary>
public sealed class PlaybackProgressAnalyticsConsumer : IEventConsumer<PlaybackProgressEventArgs>
{
    private readonly AnalyticsService _analytics;

    public PlaybackProgressAnalyticsConsumer(AnalyticsService analytics)
    {
        _analytics = analytics;
    }

    public Task OnEvent(PlaybackProgressEventArgs? eventArgs)
    {
        if (eventArgs is not null)
        {
            _analytics.OnPlaybackProgress(eventArgs);
        }

        return Task.CompletedTask;
    }
}
