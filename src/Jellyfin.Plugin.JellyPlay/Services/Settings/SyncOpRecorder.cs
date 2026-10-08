using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Realtime;
using Jellyfin.Plugin.JellyPlay.Services.Push;
using Jellyfin.Plugin.JellyPlay.Storage;
using Jellyfin.Plugin.JellyPlay.Storage.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyPlay.Services.Settings;

/// <summary>
/// The deep module behind sync-operation observability: it owns the op
/// vocabulary (<c>push</c>/<c>pull</c>/<c>reset</c>/<c>wipe</c>), the
/// change-log seq-range each op brackets, the applied-bytes approximation,
/// the best-effort history insert and the best-effort SSE fan-out
/// (anchored <c>settings.changed</c>/<c>settings.reset</c> plus the admin
/// <c>sync.op</c> live-monitor event). <see cref="SettingsService"/> is the
/// thin adapter over this module: it routes every recording and every fan-out
/// through here and keeps no op-string literals, no range-pair assembly and
/// no publish logic of its own.
///
/// Depth note (ADR-0003): this module sits ABOVE the database — the one
/// <see cref="JellyPlayDatabase"/> type keeps its public signatures. The
/// push path's history row is still written inside the batch composite's own
/// transaction (<see cref="JellyPlayDatabase.ApplyBatchWithHistory"/> computes
/// the same bytes approximation inline, where the applied set is known
/// atomically); this module owns that approximation as the canonical
/// service-side statement (<see cref="ApproximateAppliedBytes"/>) and owns the
/// push fan-out that follows the commit. Pull/reset/wipe recording (insert +
/// fan-out) lives here whole — pull history writes ride the background
/// flusher (see <see cref="RecordPull"/>), mutation-op recording stays
/// synchronous on the write path it observes.
/// </summary>
public sealed class SyncOpRecorder : IDisposable
{
    /// <summary>Op vocabulary: every recorded sync operation is one of these four.</summary>
    public const string OpPush = "push";

    /// <inheritdoc cref="OpPush"/>
    public const string OpPull = "pull";

    /// <inheritdoc cref="OpPush"/>
    public const string OpReset = "reset";

    /// <inheritdoc cref="OpPush"/>
    public const string OpWipe = "wipe";

    /// <summary>
    /// Pull-history queue bound: a burst past this drops the OLDEST queued
    /// pulls — losing a poll-history row is acceptable telemetry loss, and the
    /// delta poll path must never wait on the history store.
    /// </summary>
    internal const int PullQueueCapacity = 1024;

    /// <summary>
    /// The coalescing window: pulls queued within one window collapse to ONE
    /// row per (user, device) — the LATEST pull each device made — written
    /// when the window closes.
    /// </summary>
    private static readonly TimeSpan PullFlushWindow = TimeSpan.FromSeconds(3);

    private readonly JellyPlayDatabase _db;
    private readonly SseHub _hub;
    private readonly PushDispatcher? _push;
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;
    private readonly Channel<PullOp> _pullQueue = Channel.CreateBounded<PullOp>(
        new BoundedChannelOptions(PullQueueCapacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly object _flushLock = new();
    private readonly Task _flushLoop;

    /// <summary>One queued delta-pull observation, stamped when the pull was served.</summary>
    private readonly record struct PullOp(string UserId, string DeviceId, int KeysReturned, long FromSeq, long ToSeq, long Ts);

    public SyncOpRecorder(
        JellyPlayDatabase db,
        SseHub hub,
        PushDispatcher? push,
        ILogger logger,
        TimeProvider? clock = null)
    {
        _db = db;
        _hub = hub;
        _push = push;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _flushLoop = Task.Run(FlushPullLoopAsync);
    }

    /// <summary>
    /// Stops the background flusher after draining what is queued (the DI
    /// container disposes the recorder's owning singleton; tests drive the
    /// queue synchronously through <see cref="FlushAsync"/> instead).
    /// </summary>
    public void Dispose()
    {
        _pullQueue.Writer.TryComplete();
        try
        {
            _flushLoop.GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // The loop's own catch-all owns failure reporting; disposal never throws.
        }
    }

    private long ServerNow => _clock.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>
    /// The applied-bytes approximation for a recorded push: the serialized
    /// length of every batch write whose key applied. The batch composite
    /// computes exactly this inside its transaction (the applied set is only
    /// known atomically there); this static is the canonical service-side
    /// statement of the rule for anything that needs the number outside one.
    /// </summary>
    public static long ApproximateAppliedBytes(IReadOnlyList<SettingWrite> writes, IReadOnlyCollection<AppliedSetting> applied)
    {
        var appliedSet = applied.Select(a => (a.Ns, a.Key)).ToHashSet();
        return writes
            .Where(write => appliedSet.Contains((write.Ns, write.Key)))
            .Sum(write => (long)write.Value.Length);
    }

    /// <summary>Seq range bracketing a head-to-head batch: push + tombstone reset/wipe share the shape (before/after heads).</summary>
    public static (long? FromSeq, long? ToSeq) Range(long headBefore, long headAfter) => (headBefore, headAfter);

    /// <summary>Pull range: the requested <c>since</c> cursor through the served head.</summary>
    public static (long? FromSeq, long? ToSeq) PullRange(long since, long head) => (since, head);

    /// <summary>
    /// Appends one sync_history row and fans the operation out to the admin
    /// live-monitor stream. Best-effort by contract: an observability write
    /// must never fail (or even slow-path-fail) the sync operation it
    /// observes, so every exception is swallowed with a warning.
    /// </summary>
    public void Record(
        string userId,
        string deviceId,
        string op,
        int keysApplied,
        int keysRejected,
        long bytes,
        string? rejectsJson,
        long? fromSeq = null,
        long? toSeq = null)
    {
        try
        {
            _db.InsertSyncHistory(
                userId,
                deviceId,
                op,
                keysApplied,
                keysRejected,
                bytes,
                rejectsJson,
                ServerNow,
                fromSeq,
                toSeq);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JellyPlay sync-history recording failed (user {UserId}, op {Op}) — ignoring", userId, op);
        }

        PublishAdminOp(userId, deviceId, op, keysApplied, keysRejected);
    }

    /// <summary>
    /// Records a delta-pull serve: the history shows which device observed
    /// which change. The range is (since, head] — exactly what the pull
    /// served — so a zero-key pull still gets its (empty) range. Full
    /// <c>GET settings</c> reads are NOT recorded (only delta pulls).
    /// The history write is OFF the response path: the observation is queued
    /// (bounded, drop-oldest) and a single background flusher coalesces it
    /// per (user, device) per window — a poll response never takes the
    /// history store's write lock inline. The admin live-monitor fan-out
    /// stays synchronous (an in-memory publish, no store).
    /// </summary>
    public void RecordPull(string userId, string? deviceId, int keysReturned, long since, long head)
    {
        var device = deviceId ?? string.Empty;
        var (fromSeq, toSeq) = PullRange(since, head);
        // DropOldest makes room under saturation; TryWrite never throws and
        // never blocks the pull.
        _pullQueue.Writer.TryWrite(new PullOp(userId, device, keysReturned, fromSeq ?? 0, toSeq ?? 0, ServerNow));
        PublishAdminOp(userId, device, OpPull, keysReturned, 0);
    }

    /// <summary>
    /// Drains every queued pull observation NOW — the test seam. Drain-completes:
    /// when it returns, everything queued before the call is in the store (the
    /// flusher's in-flight drain and this share one lock, so neither can hold
    /// rows past the other's return).
    /// </summary>
    internal Task FlushAsync()
    {
        try
        {
            lock (_flushLock)
            {
                FlushPulls();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JellyPlay pull-history flush failed — ignoring");
        }

        return Task.CompletedTask;
    }

    /// <summary>The flusher: waits for the first queued pull, closes one coalesce window, drains, repeats. Never throws.</summary>
    private async Task FlushPullLoopAsync()
    {
        var reader = _pullQueue.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            try
            {
                await Task.Delay(PullFlushWindow, _clock).ConfigureAwait(false);
                lock (_flushLock)
                {
                    FlushPulls();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "JellyPlay pull-history flush failed — ignoring");
            }
        }
    }

    /// <summary>Drains the queue into one history row per (user, device) — the latest pull each device made. Caller holds <see cref="_flushLock"/>.</summary>
    private void FlushPulls()
    {
        // Insertion-ordered: the first-seen (user, device) fixes the row
        // order; a re-pull within the window only refreshes its own entry.
        var latest = new Dictionary<(string UserId, string DeviceId), PullOp>();
        while (_pullQueue.Reader.TryRead(out var pull))
        {
            latest[(pull.UserId, pull.DeviceId)] = pull;
        }

        foreach (var pull in latest.Values)
        {
            try
            {
                _db.InsertSyncHistory(
                    pull.UserId,
                    pull.DeviceId,
                    OpPull,
                    pull.KeysReturned,
                    0,
                    0,
                    null,
                    pull.Ts,
                    pull.FromSeq,
                    pull.ToSeq);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "JellyPlay sync-history recording failed (user {UserId}, op pull) — ignoring", pull.UserId);
            }
        }
    }

    /// <summary>Records a namespace reset (tombstone batch): the range brackets the appended <c>del</c> rows.</summary>
    public void RecordReset(string userId, string? deviceId, int deleted, long headBefore, long headAfter)
    {
        var (fromSeq, toSeq) = Range(headBefore, headAfter);
        Record(userId, deviceId ?? string.Empty, OpReset, deleted, 0, 0, null, fromSeq, toSeq);
    }

    /// <summary>Records a device wipe (the revoke action's data half): the range brackets the tombstoned rows.</summary>
    public void RecordWipe(string userId, string deviceId, int wiped, long headBefore, long headAfter)
    {
        var (fromSeq, toSeq) = Range(headBefore, headAfter);
        Record(userId, deviceId, OpWipe, wiped, 0, 0, null, fromSeq, toSeq);
    }

    /// <summary>
    /// The one fan-out for every settings mutation path (batch apply, reset,
    /// wipe): publishes the event anchored at the change-log head — the SSE id
    /// IS that head, so a reconnecting client resumes the delta pull from
    /// <c>changed?since=&lt;id&gt;</c> — plus the silent trigger: devices presumed
    /// offline for SSE (no stream subscriber) that registered the
    /// "silent-push" cap get a data-only sync-nudge so they flush promptly.
    /// Single call path: <see cref="PushDispatcher.DispatchSyncNudgeAsync"/>
    /// already filters via <see cref="PushDispatcher.GetSyncNudgeDevices"/>
    /// (no-op when nobody is eligible), so no pre-check scan here.
    /// </summary>
    public void PublishChanged(string userId, long head, string eventName, string payload)
    {
        // The delivered count IS the live-subscriber signal (zero = nobody is
        // streaming right now): no second O(n) hub scan. Devices presumed
        // offline for SSE that registered the "silent-push" cap get the
        // data-only sync-nudge so they flush promptly.
        var delivered = _hub.PublishToUser(SseHub.SettingsStream, userId, eventName, payload, (ulong)Math.Max(head, 1));
        if (delivered == 0 && _push is not null)
        {
            _push.DispatchSyncNudge(userId);
        }
    }

    /// <summary>
    /// Live-monitor fan-out: one <c>sync.op</c> event on the admin stream per
    /// recorded operation (push / pull / reset / wipe). Broadcast delivery —
    /// every elevated dashboard subscriber sees every user's ops. Best-effort
    /// like the history write itself: a publish failure must never fail (or
    /// even slow-path-fail) the sync operation it observes.
    /// </summary>
    public void PublishAdminOp(string userId, string deviceId, string op, int keysApplied, int keysRejected)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                type = "sync.op",
                userId,
                op,
                deviceId,
                keysApplied,
                keysRejected,
                ts = ServerNow
            });
            _hub.PublishAll(SseHub.AdminStream, "sync.op", payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JellyPlay admin live-monitor publish failed (user {UserId}, op {Op}) — ignoring", userId, op);
        }
    }
}
