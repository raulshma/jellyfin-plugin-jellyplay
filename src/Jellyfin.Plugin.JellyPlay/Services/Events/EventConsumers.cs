using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Events.Session;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyPlay.Services.Events;

public sealed class SessionStartedEvent : IEventConsumer<SessionStartedEventArgs>
{
    private readonly EventService _events;
    private readonly ILogger<SessionStartedEvent> _logger;

    public SessionStartedEvent(EventService events, ILogger<SessionStartedEvent> logger)
    {
        _events = events;
        _logger = logger;
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
    private readonly ILogger<PlaybackStartedEvent> _logger;

    public PlaybackStartedEvent(EventService events, ILogger<PlaybackStartedEvent> logger)
    {
        _events = events;
        _logger = logger;
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
    private readonly ILogger<UserLockedOutEvent> _logger;

    public UserLockedOutEvent(EventService events, ILogger<UserLockedOutEvent> logger)
    {
        _events = events;
        _logger = logger;
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
