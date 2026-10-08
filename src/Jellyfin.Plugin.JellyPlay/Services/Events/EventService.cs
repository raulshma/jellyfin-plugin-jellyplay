using System;
using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.JellyPlay.Api;
using Jellyfin.Plugin.JellyPlay.Configuration;
using Jellyfin.Plugin.JellyPlay.Realtime;
using Jellyfin.Plugin.JellyPlay.Services.Push;
using Jellyfin.Plugin.JellyPlay.Services.Shared;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyPlay.Services.Events;

/// <summary>
/// Publishes typed events onto the "events" SSE stream: new media, admin
/// broadcasts, session/playback/lockout notices. New-media library gating and
/// dedup live in the required <see cref="NewMediaPipeline"/> (the one seam —
/// no inline fallbacks). When push is enabled, new-media and broadcast also
/// fan out to push-registered devices (same audiences as SSE;
/// fire-and-forget, never blocking the publish).
/// </summary>
public sealed class EventService
{
    private readonly SseHub _hub;
    private readonly Func<EventsConfig> _config;
    private readonly Func<IReadOnlyList<string>> _adminUserIds;
    private readonly NewMediaPipeline _pipeline;
    private readonly PushDispatcher? _push;
    private readonly ILogger<EventService> _logger;
    private readonly TimeProvider _clock;
    private readonly NotificationFanout _fanout;

    public EventService(
        SseHub hub,
        Func<EventsConfig> config,
        Func<IReadOnlyList<string>> adminUserIds,
        NewMediaPipeline pipeline,
        ILogger<EventService> logger,
        PushDispatcher? push = null,
        TimeProvider? clock = null,
        NotificationFanout? fanout = null)
    {
        _hub = hub;
        _config = config;
        _adminUserIds = adminUserIds;
        _pipeline = pipeline;
        _push = push;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _fanout = fanout ?? new NotificationFanout(hub, push);
    }

    public int PublishNewMedia(EpisodeGroup group, IReadOnlyList<string>? adminUserIds = null)
    {
        var config = _config();
        if (!config.NewMediaEnabled)
        {
            return 0;
        }

        // Library allow-listing and dedup are the pipeline's seams (their one
        // home): the pipeline's own recent-key store is the dedup truth.
        if (!_pipeline.IsLibraryAllowed(group.LibraryId))
        {
            return 0;
        }

        var first = group.Episodes[0];
        if (!_pipeline.ShouldEmit(NewMediaPipeline.GroupDedupKey(group), config.DedupThresholdSeconds))
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
            _clock.GetUtcNow().ToUnixTimeMilliseconds()));

        return DeliverNewMedia(config, payload, new PushMessage(
            PushKinds.NewMedia, title, "New media added", first.ItemId.ToString()), adminUserIds);
    }

    public int PublishNewMovie(Guid itemId, string title, string? libraryId)
    {
        var config = _config();
        if (!config.NewMediaEnabled)
        {
            return 0;
        }

        if (!_pipeline.IsLibraryAllowed(libraryId))
        {
            return 0;
        }

        if (!_pipeline.ShouldEmit(NewMediaPipeline.MovieDedupKey(itemId), config.DedupThresholdSeconds))
        {
            return 0;
        }

        var payload = JsonSerializer.Serialize(new NewMediaEventPayload(
            "new-media", itemId.ToString(), null, null, title, 1, libraryId,
            _clock.GetUtcNow().ToUnixTimeMilliseconds()));
        return DeliverNewMedia(config, payload, new PushMessage(PushKinds.NewMedia, title, "New media added"), adminUserIds: null);
    }

    public int PublishBroadcast(string title, string body, string? url)
    {
        var payload = JsonSerializer.Serialize(new BroadcastEventPayload(
            "broadcast", title, body, url, _clock.GetUtcNow().ToUnixTimeMilliseconds()));
        // The broadcast fan-out leverages the one SSE+push pairing: a single
        // broadcast-all target drives both legs.
        return _fanout.Publish(
            "events",
            "broadcast",
            payload,
            new PushMessage(PushKinds.Broadcast, title, body),
            Audience.BroadcastTargets.All);
    }

    public int PublishSessionStarted(string username)
    {
        if (!_config().SessionStartedEnabled)
        {
            return 0;
        }

        var payload = JsonSerializer.Serialize(new SimpleEventPayload("session-started", username, _clock.GetUtcNow().ToUnixTimeMilliseconds()));
        return _hub.PublishAll("events", "session-started", payload);
    }

    public int PublishPlaybackStarted(string username)
    {
        if (!_config().PlaybackStartedEnabled)
        {
            return 0;
        }

        var payload = JsonSerializer.Serialize(new SimpleEventPayload("playback-started", username, _clock.GetUtcNow().ToUnixTimeMilliseconds()));
        return _hub.PublishAll("events", "playback-started", payload);
    }

    public int PublishUserLockedOut(string username)
    {
        if (!_config().UserLockedOutEnabled)
        {
            return 0;
        }

        var payload = JsonSerializer.Serialize(new SimpleEventPayload("user-locked-out", username, _clock.GetUtcNow().ToUnixTimeMilliseconds()));
        return _hub.PublishAll("events", "user-locked-out", payload);
    }

    /// <summary>
    /// Audience gate for new-media events: "admins" delivers only to admin
    /// subscribers; anything else ("all") broadcasts. One unified
    /// <see cref="Audience.BroadcastTargets"/> resolution drives both the SSE
    /// and push legs through the <see cref="NotificationFanout"/> pairing.
    /// <paramref name="adminUserIds"/> lets a flush batch resolve the audience
    /// ONCE and share it across every item in the batch; null falls back to a
    /// per-event resolution (memoized briefly in <see cref="Admin.AdminUsers.AdminUserIds"/>).
    /// </summary>
    private int DeliverNewMedia(EventsConfig config, string payload, PushMessage push, IReadOnlyList<string>? adminUserIds)
    {
        var targets = Audience.ResolveAudience(config.NewMediaAudience, adminUserIds ?? _adminUserIds());
        return _fanout.Publish("events", "new-media", payload, push, targets);
    }
}
