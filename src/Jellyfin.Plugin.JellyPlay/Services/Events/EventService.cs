using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Api;
using Jellyfin.Plugin.JellyPlay.Configuration;
using Jellyfin.Plugin.JellyPlay.Realtime;
using Jellyfin.Plugin.JellyPlay.Services.Push;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyPlay.Services.Events;

/// <summary>
/// Publishes typed events onto the "events" SSE stream: new media, admin
/// broadcasts, session/playback/lockout notices. Deduplicates re-fired events
/// for the same logical key within the configured threshold. When push is
/// enabled, new-media and broadcast also fan out to push-registered devices
/// (same audiences as SSE; fire-and-forget, never blocking the publish).
/// </summary>
public sealed class EventService
{
    private readonly SseHub _hub;
    private readonly Func<EventsConfig> _config;
    private readonly Func<IReadOnlyList<string>> _adminUserIds;
    private readonly PushDispatcher? _push;
    private readonly ILogger<EventService> _logger;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, long> _recentEventKeys = new();

    public EventService(
        SseHub hub,
        Func<EventsConfig> config,
        Func<IReadOnlyList<string>> adminUserIds,
        ILogger<EventService> logger,
        PushDispatcher? push = null,
        TimeProvider? clock = null)
    {
        _hub = hub;
        _config = config;
        _adminUserIds = adminUserIds;
        _push = push;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
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

        var title = group.Episodes.Count == 1
            ? $"{group.SeriesName} — {first.Name}"
            : $"{group.SeriesName} — {group.Episodes.Count} new episodes";
        var payload = JsonSerializer.Serialize(new NewMediaEventPayload(
            "new-media",
            first.ItemId.ToString(),
            group.SeriesId.ToString(),
            group.SeasonIndex,
            title,
            group.Episodes.Count,
            group.LibraryId,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

        return DeliverNewMedia(config, payload, new PushMessage(
            PushKinds.NewMedia, title, "New media added", first.ItemId.ToString()));
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
        return DeliverNewMedia(_config(), payload, new PushMessage(PushKinds.NewMedia, title, "New media added"));
    }

    public int PublishBroadcast(string title, string body, string? url)
    {
        var payload = JsonSerializer.Serialize(new BroadcastEventPayload(
            "broadcast", title, body, url, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        _push?.DispatchToUsers(new PushMessage(PushKinds.Broadcast, title, body), null);
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

    /// <summary>
    /// Audience gate for new-media events: "admins" delivers only to admin
    /// subscribers via PublishToUsers; anything else ("all") broadcasts. Push
    /// fans out to the SAME resolved target set (admins' devices, or every
    /// user's devices when null) — one audience resolution drives both.
    /// </summary>
    private int DeliverNewMedia(EventsConfig config, string payload, PushMessage push)
    {
        var targets = ResolveAudienceTargets(config.NewMediaAudience, _adminUserIds());
        _push?.DispatchToUsers(push, targets);
        return targets is null
            ? _hub.PublishAll("events", "new-media", payload)
            : _hub.PublishToUsers("events", targets, "new-media", payload);
    }

    /// <summary>
    /// Resolves the new-media audience targets: null = broadcast to every
    /// subscriber ("all"); otherwise the admin user ids ("admins").
    /// Pure so the audience resolution is unit-testable without the host.
    /// </summary>
    internal static IReadOnlySet<string>? ResolveAudienceTargets(string? audience, IReadOnlyList<string> adminUserIds)
        => string.Equals(audience, "admins", StringComparison.OrdinalIgnoreCase)
            ? new HashSet<string>(adminUserIds, StringComparer.Ordinal)
            : null;

    private bool ShouldEmit(string key, int thresholdSeconds)
    {
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
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
