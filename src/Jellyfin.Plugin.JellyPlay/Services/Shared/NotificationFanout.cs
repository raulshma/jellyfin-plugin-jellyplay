using System.Collections.Generic;
using Jellyfin.Plugin.JellyPlay.Realtime;
using Jellyfin.Plugin.JellyPlay.Services.Push;

namespace Jellyfin.Plugin.JellyPlay.Services.Shared;

/// <summary>
/// The ONE SSE + push pairing behind every broadcast: one
/// <see cref="Audience.BroadcastTargets"/> resolution drives both legs, so the
/// SSE audience and the push audience cannot drift.
///
/// Depth: callers leverage this interface instead of pairing
/// <c>SseHub.Publish*</c> with <c>PushDispatcher.DispatchToUsers</c> inline.
/// The opt-out flags cover the two legs that are not always paired:
/// <list type="bullet">
/// <item>message-created-only — inbox messages fan push on creation only and
/// have no SSE stream (<c>skipSse: true</c>, push-only);</item>
/// <item>sync-nudge — the settings-sync SSE publish is SSE-only
/// (<c>skipPush: true</c>); the silent nudge rides a separate conditional leg
/// through the shared <see cref="PushEligibility"/> filter.</item>
/// </list>
/// </summary>
public sealed class NotificationFanout
{
    private readonly SseHub _hub;
    private readonly PushDispatcher? _push;

    public NotificationFanout(SseHub hub, PushDispatcher? push = null)
    {
        _hub = hub;
        _push = push;
    }

    /// <summary>
    /// Publishes one event to the SSE stream and fans the paired push message
    /// to the same targets. Returns the SSE delivered count (the
    /// live-subscriber signal); push is fire-and-forget.
    /// </summary>
    public int Publish(
        string stream,
        string eventName,
        string payload,
        PushMessage? pushMessage,
        Audience.BroadcastTargets targets,
        bool skipSse = false,
        bool skipPush = false,
        ulong? eventId = null)
    {
        if (!skipPush && pushMessage is not null)
        {
            _push?.DispatchToUsers(pushMessage, targets);
        }

        if (skipSse)
        {
            return 0;
        }

        return _hub.Publish(stream, targets, eventName, payload, eventId);
    }

    /// <summary>
    /// Raw-set convenience over the <see cref="Audience.BroadcastTargets"/>
    /// core (null = broadcast-all, empty = nobody).
    /// </summary>
    public int Publish(
        string stream,
        string eventName,
        string payload,
        PushMessage? pushMessage,
        IReadOnlyCollection<string>? targets,
        bool skipSse = false,
        bool skipPush = false,
        ulong? eventId = null)
        => Publish(stream, eventName, payload, pushMessage, Audience.BroadcastTargets.From(targets), skipSse, skipPush, eventId);
}
