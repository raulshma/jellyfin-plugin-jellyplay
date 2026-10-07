using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Events.Session;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.JellyPlay.Services.Events;

public sealed class SessionStartedEvent : IEventConsumer<SessionStartedEventArgs>
{
    private readonly EventService _events;

    public SessionStartedEvent(EventService events)
    {
        _events = events;
    }

    public Task OnEvent(SessionStartedEventArgs? eventArgs)
    {
        var username = eventArgs?.Argument?.UserName;
        if (!string.IsNullOrEmpty(username))
        {
            _events.PublishSessionStarted(username);
        }

        return Task.CompletedTask;
    }
}

public sealed class PlaybackStartedEvent : IEventConsumer<PlaybackStartEventArgs>
{
    private readonly EventService _events;

    public PlaybackStartedEvent(EventService events)
    {
        _events = events;
    }

    public Task OnEvent(PlaybackStartEventArgs? eventArgs)
    {
        var username = eventArgs?.Session?.UserName;
        if (!string.IsNullOrEmpty(username))
        {
            _events.PublishPlaybackStarted(username);
        }

        return Task.CompletedTask;
    }
}

public sealed class UserLockedOutEvent : IEventConsumer<UserLockedOutEventArgs>
{
    private readonly EventService _events;

    public UserLockedOutEvent(EventService events)
    {
        _events = events;
    }

    public Task OnEvent(UserLockedOutEventArgs? eventArgs)
    {
        var username = eventArgs?.Argument?.Username;
        if (!string.IsNullOrEmpty(username))
        {
            _events.PublishUserLockedOut(username);
        }

        return Task.CompletedTask;
    }
}
