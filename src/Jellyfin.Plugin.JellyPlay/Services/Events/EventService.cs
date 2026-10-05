using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Api;
using Jellyfin.Plugin.JellyPlay.Configuration;
using Jellyfin.Plugin.JellyPlay.Realtime;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyPlay.Services.Events;

/// <summary>
/// Publishes typed events onto the "events" SSE stream: new media, admin
/// broadcasts, session/playback/lockout notices. Deduplicates re-fired events
/// for the same logical key within the configured threshold.
/// </summary>
public sealed class EventService
{
    private readonly SseHub _hub;
    private readonly Func<EventsConfig> _config;
    private readonly ILogger<EventService> _logger;
    private readonly ConcurrentDictionary<string, long> _recentEventKeys = new();

    public EventService(SseHub hub, Func<EventsConfig> config, ILogger<EventService> logger)
    {
        _hub = hub;
        _config = config;
        _logger = logger;
    }

    public int PublishNewMedia(EpisodeGroup group)
    {
        var config = _config();
        if (!config.NewMediaEnabled)
        {
            return 0;
        }

        if (config.NewMediaEnabledLibraries.Count > 0
            && group.LibraryId is not null
            && !config.NewMediaEnabledLibraries.Contains(Guid.Parse(group.LibraryId)))
        {
            return 0;
        }

        var first = group.Episodes[0];
        var dedupKey = $"new-media:{group.SeriesId}:{group.SeasonIndex}:{group.Episodes[0].ItemId}";
        if (!ShouldEmit(dedupKey, config.DedupThresholdSeconds))
        {
            return 0;
        }

        var payload = JsonSerializer.Serialize(new NewMediaEventPayload(
            "new-media",
            first.ItemId.ToString(),
            group.SeriesId.ToString(),
            group.SeasonIndex,
            group.Episodes.Count == 1
                ? $"{group.SeriesName} — {first.Name}"
                : $"{group.SeriesName} — {group.Episodes.Count} new episodes",
            group.Episodes.Count,
            group.LibraryId,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

        return config.NewMediaAudience == "admins"
            ? _hub.PublishAll("events", "new-media", payload)
            : _hub.PublishAll("events", "new-media", payload);
    }

    public int PublishNewMovie(Guid itemId, string title, string? libraryId)
    {
        var config = _config();
        if (!config.NewMediaEnabled)
        {
            return 0;
        }

        if (config.NewMediaEnabledLibraries.Count > 0
            && libraryId is not null
            && !config.NewMediaEnabledLibraries.Contains(Guid.Parse(libraryId)))
        {
            return 0;
        }

        if (!ShouldEmit($"new-media:{itemId}", config.DedupThresholdSeconds))
        {
            return 0;
        }

        var payload = JsonSerializer.Serialize(new NewMediaEventPayload(
            "new-media", itemId.ToString(), null, null, title, 1, libraryId,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        return _hub.PublishAll("events", "new-media", payload);
    }

    public int PublishBroadcast(string title, string body, string? url)
    {
        var payload = JsonSerializer.Serialize(new BroadcastEventPayload(
            "broadcast", title, body, url, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        return _hub.PublishAll("events", "broadcast", payload);
    }

    public int PublishSessionStarted(string username)
    {
        if (!_config().SessionStartedEnabled)
        {
            return 0;
        }

        var payload = JsonSerializer.Serialize(new SimpleEventPayload("session-started", username, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        return _hub.PublishAll("events", "session-started", payload);
    }

    public int PublishPlaybackStarted(string username)
    {
        if (!_config().PlaybackStartedEnabled)
        {
            return 0;
        }

        var payload = JsonSerializer.Serialize(new SimpleEventPayload("playback-started", username, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        return _hub.PublishAll("events", "playback-started", payload);
    }

    public int PublishUserLockedOut(string username)
    {
        if (!_config().UserLockedOutEnabled)
        {
            return 0;
        }

        var payload = JsonSerializer.Serialize(new SimpleEventPayload("user-locked-out", username, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        return _hub.PublishAll("events", "user-locked-out", payload);
    }

    private bool ShouldEmit(string key, int thresholdSeconds)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var thresholdMs = thresholdSeconds * 1000L;
        while (true)
        {
            if (_recentEventKeys.TryGetValue(key, out var last))
            {
                if (now - last < thresholdMs)
                {
                    return false;
                }

                if (_recentEventKeys.TryUpdate(key, now, last))
                {
                    break;
                }
            }
            else if (_recentEventKeys.TryAdd(key, now))
            {
                break;
            }
        }

        if (_recentEventKeys.Count > 1024)
        {
            foreach (var (k, timestamp) in _recentEventKeys)
            {
                if (now - timestamp > thresholdMs + 300_000)
                {
                    _recentEventKeys.TryRemove(k, out _);
                }
            }
        }

        return true;
    }
}
