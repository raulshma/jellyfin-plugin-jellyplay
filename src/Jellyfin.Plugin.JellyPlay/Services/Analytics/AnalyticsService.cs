using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Api;
using Jellyfin.Plugin.JellyPlay.Configuration;
using Jellyfin.Plugin.JellyPlay.Helpers;
using Jellyfin.Plugin.JellyPlay.Services.Admin;
using Jellyfin.Plugin.JellyPlay.Services.Shared;
using Jellyfin.Plugin.JellyPlay.Storage;
using Jellyfin.Plugin.JellyPlay.Storage.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyPlay.Services.Analytics;

/// <summary>
/// Playback activity recording + admin reporting (analytics v1). Finished
/// plays are recorded from the host's session pipeline — playback-stop events
/// close the row; playback-progress events keep in-flight state and gracefully
/// close sessions the host never reported as stopped (client dropped, replaced
/// by the next item). Recording is fire-and-forget: it never throws into the
/// host event pipeline and never blocks playback. Reporting is a pure
/// aggregation over the plugin database (per-day/per-user from the daily
/// rollups, top items from raw rows), so it is unit-testable without a host.
/// </summary>
public sealed class AnalyticsService : IDisposable
{
    public const int DefaultOverviewDays = 30;
    public const int MaxOverviewDays = 365;
    public const int MaxTopItems = 10;
    public const int DefaultSessionLimit = 50;
    public const int MaxSessionLimit = 200;

    /// <summary>How many recent UTC days of rollups the maintenance pass recomputes (idempotently).</summary>
    public const int RollupWindowDays = 3;

    private const long MsPerDay = 86_400_000;
    private const long TicksPerMs = 10_000;

    /// <summary>
    /// Pending-write bound: a burst past this drops the NEWEST row (counted —
    /// losing one analytics row is acceptable telemetry loss) so the host
    /// event thread never blocks on the store.
    /// </summary>
    internal const int PendingWriteCapacity = 4096;

    private readonly JellyPlayDatabase _db;
    private readonly Func<AnalyticsConfig> _config;
    private readonly TimeProvider _clock;
    private readonly ILogger<AnalyticsService> _logger;
    private readonly object _openLock = new();
    private readonly Dictionary<string, OpenPlayback> _open = new(StringComparer.Ordinal);

    private readonly Channel<PlaybackSessionRow> _pendingWrites = Channel.CreateBounded<PlaybackSessionRow>(
        new BoundedChannelOptions(PendingWriteCapacity) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly object _writeLock = new();
    private readonly Task _writeLoop;
    private int _droppedWrites;

    /// <summary>Gate timestamp for the cadence-bounded stale-session scan (shared SweepGate).</summary>
    private long _lastStaleSweepMs;

    public AnalyticsService(
        JellyPlayDatabase db,
        Func<AnalyticsConfig> config,
        ILogger<AnalyticsService> logger,
        TimeProvider? clock = null)
    {
        _db = db;
        _config = config;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _writeLoop = Task.Run(WriteLoopAsync);
    }

    /// <summary>Rows dropped by queue saturation (telemetry; dropped rows are acceptable loss by contract).</summary>
    internal int DroppedWrites => Volatile.Read(ref _droppedWrites);

    /// <summary>
    /// Stops the background writer after draining what is queued (the DI
    /// container disposes this singleton at shutdown; tests drive the queue
    /// synchronously through <see cref="FlushPendingWritesAsync"/> instead).
    /// </summary>
    public void Dispose()
    {
        _pendingWrites.Writer.TryComplete();
        try
        {
            _writeLoop.GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // The loop's own catch-all owns failure reporting; disposal never throws.
        }
    }

    // ------------------------------------------------------------------
    // Recording (event pipeline entry points)
    // ------------------------------------------------------------------

    /// <summary>Playback-stop events finalize the session row. Never throws.</summary>
    internal void OnPlaybackStopped(PlaybackStopEventArgs args)
    {
        if (!_config().Enabled)
        {
            return;
        }

        try
        {
            RecordStopped(args);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "JellyPlay analytics: playback-stop recording failed");
        }
    }

    /// <summary>
    /// Playback-progress events track the in-flight session and gracefully
    /// close abandoned ones (the same user/device moving to another item, or
    /// sessions gone quiet past the stale grace). Never throws.
    /// </summary>
    internal void OnPlaybackProgress(PlaybackProgressEventArgs args)
    {
        if (!_config().Enabled)
        {
            return;
        }

        try
        {
            TrackProgress(args);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "JellyPlay analytics: playback-progress tracking failed");
        }
    }

    private void RecordStopped(PlaybackStopEventArgs args)
    {
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        var (itemId, itemName, itemType, seriesName, runTimeTicks) = ResolveItem(args.Item);
        if (itemId is null)
        {
            return;
        }

        if (ResolveUserId(args.Session, args.Users) is not { } resolvedUser)
        {
            return;
        }

        var session = args.Session;
        var key = SessionKey(args.PlaySessionId, resolvedUser, itemId, session?.Client, session?.DeviceName);

        OpenPlayback? open;
        lock (_openLock)
        {
            if (_open.Remove(key, out var tracked))
            {
                open = tracked;
            }
            else
            {
                open = null;
            }
        }

        // No tracked progress (stop without any prior progress event, or a
        // server restart): estimate the start from how far the play got.
        var positionTicks = args.PlaybackPositionTicks ?? open?.PositionTicks ?? session?.PlayState?.PositionTicks ?? 0;
        var durationTicks = runTimeTicks ?? open?.DurationTicks;
        var startedAtMs = open?.StartedAtMs ?? EstimateStartedAt(now, positionTicks, durationTicks);

        var candidate = Materialize(
            key, resolvedUser, itemId, itemName!, itemType!, seriesName,
            open, session, positionTicks, durationTicks, startedAtMs,
            session?.Client, session?.DeviceName);
        Persist(candidate, now);
    }

    private void TrackProgress(PlaybackProgressEventArgs args)
    {
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        var (itemId, itemName, itemType, seriesName, runTimeTicks) = ResolveItem(args.Item);
        if (itemId is null)
        {
            return;
        }

        if (ResolveUserId(args.Session, args.Users) is not { } resolvedUser)
        {
            return;
        }

        var session = args.Session;
        var key = SessionKey(args.PlaySessionId, resolvedUser, itemId, session?.Client, session?.DeviceName);

        // The transcode detail depends only on the event args — fold and
        // serialize it BEFORE the lock; the lock block stays the pure
        // map-shaping critical section.
        var transcode = session?.TranscodingInfo;
        string? transcodeVideoCodec = null;
        string? transcodeAudioCodec = null;
        long? transcodeBitrate = null;
        string? transcodeReasonsJson = null;
        if (transcode is not null)
        {
            // Deepening: transcode detail folds through the shared module (same payload as the monitor).
            var detail = TranscodeFold.Fold(transcode);
            transcodeVideoCodec = detail.VideoCodec;
            transcodeAudioCodec = detail.AudioCodec;
            transcodeBitrate = detail.Bitrate;
            transcodeReasonsJson = detail.Reasons is null ? null : JsonSerializer.Serialize(detail.Reasons);
        }

        // ONE atomic lock block: collecting-and-removing the replaced sessions
        // and upserting the current one. Two acquisitions would let a stop
        // event land in between and observe replaced rows removed from the
        // map but not yet flushed — half-applied state.
        List<OpenPlayback> replaced;
        lock (_openLock)
        {
            // The host moved on to a new item without a stop event: any other
            // in-flight session for the same user/client/device is over now.
            replaced = _open.Values
                .Where(candidate => candidate.Key != key
                    && string.Equals(candidate.UserId, resolvedUser, StringComparison.Ordinal)
                    && string.Equals(candidate.ClientName, session?.Client, StringComparison.Ordinal)
                    && string.Equals(candidate.DeviceName, session?.DeviceName, StringComparison.Ordinal))
                .ToList();
            foreach (var candidate in replaced)
            {
                _open.Remove(candidate.Key);
            }

            if (!_open.TryGetValue(key, out var open))
            {
                open = new OpenPlayback(key, resolvedUser, itemId)
                {
                    StartedAtMs = now,
                    ItemName = itemName!,
                    ItemType = itemType!,
                    SeriesName = seriesName,
                    PlayMethod = "DirectPlay",
                };
                _open[key] = open;
            }

            open.ItemName = itemName!;
            open.ItemType = itemType!;
            open.SeriesName = seriesName;
            open.DurationTicks = runTimeTicks ?? open.DurationTicks;
            open.ClientName = session?.Client;
            open.DeviceName = session?.DeviceName;
            open.LastSeenMs = now;
            if (session?.PlayState?.PlayMethod is { } method)
            {
                open.PlayMethod = method.ToString();
            }

            if (transcode is not null)
            {
                open.VideoCodec = transcodeVideoCodec;
                open.AudioCodec = transcodeAudioCodec;
                open.Bitrate = transcodeBitrate;
                open.TranscodeReasonsJson = transcodeReasonsJson;
            }

            if (args.PlaybackPositionTicks is { } progressTicks)
            {
                open.PositionTicks = progressTicks;
            }
            else if (session?.PlayState?.PositionTicks is { } sessionTicks)
            {
                open.PositionTicks = sessionTicks;
            }
        }

        foreach (var candidate in replaced)
        {
            Persist(candidate, now);
        }

        CloseStaleSessions(now);
    }

    /// <summary>
    /// Sessions with no progress past the stale grace are closed at their
    /// last-seen time. The O(n) map scan is cadence-bounded through the shared
    /// <see cref="Helpers.SweepGate"/> — at most one scan per grace window
    /// (an idle-later session is only detectable after that long anyway), so
    /// steady-progress events never pay the scan.
    /// </summary>
    private void CloseStaleSessions(long now)
    {
        if (!SweepGate.Enter(ref _lastStaleSweepMs, now, PlaybackRecordingRules.StaleSessionGraceMs))
        {
            return;
        }

        List<OpenPlayback> stale;
        lock (_openLock)
        {
            stale = _open.Values
                .Where(candidate => PlaybackRecordingRules.IsStale(candidate.LastSeenMs, now))
                .ToList();
            foreach (var candidate in stale)
            {
                _open.Remove(candidate.Key);
            }
        }

        foreach (var candidate in stale)
        {
            Persist(candidate, candidate.LastSeenMs);
        }
    }

    /// <summary>
    /// The anti-noise gate + row build; the finished row leaves the host
    /// event thread via the bounded pending-write queue (never blocks, never
    /// throws into the host pipeline — a full queue drops the row, counted).
    /// The insert itself is the background writer's job.
    /// </summary>
    private void Persist(OpenPlayback open, long endedAtMs)
    {
        var wallSeconds = Math.Max(0, (endedAtMs - open.StartedAtMs) / 1000);
        var positionSeconds = Math.Max(0, open.PositionTicks / TimeSpan.TicksPerSecond);
        if (!PlaybackRecordingRules.IsRecordable(wallSeconds, positionSeconds))
        {
            return;
        }

        var row = new PlaybackSessionRow(
            Id: 0,
            UserId: open.UserId,
            ItemId: open.ItemId,
            ItemName: open.ItemName,
            ItemType: open.ItemType,
            SeriesName: open.SeriesName,
            PlayMethod: open.PlayMethod,
            VideoCodec: open.VideoCodec,
            AudioCodec: open.AudioCodec,
            Bitrate: open.Bitrate,
            TranscodeReasonsJson: open.TranscodeReasonsJson,
            PositionTicks: open.PositionTicks,
            DurationTicks: open.DurationTicks,
            // StartedAt is the minute bucket — the dedup key component.
            StartedAt: PlaybackRecordingRules.MinuteBucket(open.StartedAtMs),
            EndedAt: endedAtMs,
            ClientName: open.ClientName,
            DeviceName: open.DeviceName);

        if (!_pendingWrites.Writer.TryWrite(row))
        {
            Interlocked.Increment(ref _droppedWrites);
            _logger.LogDebug("JellyPlay analytics: pending-write queue full; dropped a playback row for user {UserId} item {ItemId}", open.UserId, open.ItemId);
        }
    }

    /// <summary>
    /// Drains every queued row NOW — the test seam. Drain-completes: when it
    /// returns, everything queued before the call is in the store (the
    /// background writer's in-flight drain and this share one lock, so
    /// neither can hold rows past the other's return).
    /// </summary>
    internal Task FlushPendingWritesAsync()
    {
        try
        {
            lock (_writeLock)
            {
                DrainPendingWrites();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "JellyPlay analytics: pending-write flush failed");
        }

        return Task.CompletedTask;
    }

    /// <summary>The background writer: drains the queue, one store write per row. Never throws.</summary>
    private async Task WriteLoopAsync()
    {
        var reader = _pendingWrites.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            try
            {
                lock (_writeLock)
                {
                    DrainPendingWrites();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "JellyPlay analytics: background persist failed");
            }
        }
    }

    /// <summary>Row-by-row deduped inserts. Caller holds <see cref="_writeLock"/>.</summary>
    private void DrainPendingWrites()
    {
        while (_pendingWrites.Reader.TryRead(out var row))
        {
            if (_db.InsertPlaybackSession(row) == 0)
            {
                // Same (user, item, start-minute) already recorded — replay of the
                // same play through both the progress and stop paths.
                _logger.LogDebug("JellyPlay analytics: deduplicated playback row for user {UserId} item {ItemId}", row.UserId, row.ItemId);
            }
        }
    }

    private static OpenPlayback Materialize(
        string key,
        string userId,
        string itemId,
        string itemName,
        string itemType,
        string? seriesName,
        OpenPlayback? open,
        SessionInfo? session,
        long positionTicks,
        long? durationTicks,
        long startedAtMs,
        string? clientName,
        string? deviceName)
    {
        var transcode = session?.TranscodingInfo;
        // Deepening: transcode detail folds through the shared module (same payload as the monitor); open-tracked values win.
        var detail = TranscodeFold.Fold(transcode);
        return new OpenPlayback(key, userId, itemId)
        {
            ItemName = open?.ItemName ?? itemName,
            ItemType = open?.ItemType ?? itemType,
            SeriesName = open?.SeriesName ?? seriesName,
            PlayMethod = open?.PlayMethod ?? session?.PlayState?.PlayMethod?.ToString() ?? "DirectPlay",
            VideoCodec = open?.VideoCodec ?? detail.VideoCodec,
            AudioCodec = open?.AudioCodec ?? detail.AudioCodec,
            Bitrate = open?.Bitrate ?? (long?)detail.Bitrate,
            TranscodeReasonsJson = open?.TranscodeReasonsJson ?? (detail.Reasons is null ? null : JsonSerializer.Serialize(detail.Reasons)),
            PositionTicks = positionTicks,
            DurationTicks = durationTicks,
            StartedAtMs = startedAtMs,
            LastSeenMs = startedAtMs,
            ClientName = open?.ClientName ?? clientName,
            DeviceName = open?.DeviceName ?? deviceName,
        };
    }

    /// <summary>No tracked start: the play got as far as its position, so it started roughly that long ago.</summary>
    private static long EstimateStartedAt(long endedAtMs, long positionTicks, long? durationTicks)
    {
        var capped = durationTicks is { } runtime && runtime > 0 ? Math.Min(positionTicks, runtime) : positionTicks;
        var playedMs = Math.Max(0, capped / TicksPerMs);
        return endedAtMs - playedMs;
    }

    private static (string? ItemId, string ItemName, string ItemType, string? SeriesName, long? RunTimeTicks) ResolveItem(BaseItem? item)
    {
        if (item is null || item.Id == Guid.Empty)
        {
            return (null, string.Empty, string.Empty, null, null);
        }

        var seriesName = item is MediaBrowser.Controller.Entities.TV.Episode episode && !string.IsNullOrEmpty(episode.SeriesName)
            ? episode.SeriesName
            : null;
        return (
            item.Id.ToString("N"),
            item.Name ?? string.Empty,
            item.GetType().Name,
            seriesName,
            item.RunTimeTicks);
    }

    private static string? ResolveUserId(SessionInfo? session, List<Jellyfin.Database.Implementations.Entities.User>? users)
    {
        if (session is not null && session.UserId != Guid.Empty)
        {
            return session.UserId.ToString();
        }

        var first = users?.FirstOrDefault();
        return first is null || first.Id == Guid.Empty ? null : first.Id.ToString();
    }

    private static string SessionKey(string? playSessionId, string userId, string itemId, string? client, string? device)
        => string.IsNullOrEmpty(playSessionId)
            ? $"{userId}|{itemId}|{client ?? string.Empty}|{device ?? string.Empty}"
            : $"ps:{playSessionId}";

    // ------------------------------------------------------------------
    // Reporting (admin)
    // ------------------------------------------------------------------

    /// <summary>
    /// The admin overview over the last <paramref name="days"/> UTC days
    /// (default 30, clamped 1..365). perDay/perUser/totals fold the daily
    /// rollups (retained forever); topItems and uniqueItems come from raw
    /// rows, so they degrade to empty/0 once raw data is pruned.
    /// </summary>
    public AnalyticsOverviewResponse GetOverview(int days, Func<Guid, string?> resolveUserName)
    {
        var clampedDays = Paged.Clamp(days, DefaultOverviewDays, MaxOverviewDays);
        var todayUtc = _clock.GetUtcNow().UtcDateTime.Date;
        var fromDayDate = todayUtc.AddDays(-(clampedDays - 1));
        var fromMs = ToUnixMs(fromDayDate);
        var toMs = ToUnixMs(todayUtc.AddDays(1));

        // DB/host wiring stays here; the pure aggregation lives in the fold module.
        var rollups = _db.GetPlaybackRollups(DayString(fromDayDate), DayString(todayUtc));
        var uniqueItems = _db.CountDistinctPlaybackItems(fromMs, toMs);
        var (totals, perDay, perUser) = AnalyticsFold.FoldRollups(rollups, resolveUserName, uniqueItems);
        var topItems = AnalyticsFold.FoldTopItems(_db.GetTopPlaybackItems(fromMs, toMs, MaxTopItems));

        return new AnalyticsOverviewResponse(clampedDays, totals, perDay, perUser, topItems);
    }

    /// <summary>Raw finished sessions newest-first, optional user/since (unix ms) filters.</summary>
    public AnalyticsSessionsResponse GetSessions(string? userId, long? since, int limit)
    {
        var clamped = Paged.Clamp(limit, DefaultSessionLimit, MaxSessionLimit);
        var sessions = AnalyticsFold.FoldSessions(_db.GetPlaybackSessions(
                string.IsNullOrWhiteSpace(userId) ? null : userId,
                since ?? 0,
                clamped));
        return new AnalyticsSessionsResponse(sessions);
    }

    /// <summary>
    /// The caller's own activity ("Your watching") over the last
    /// <paramref name="days"/> UTC days (default 30, clamped 1..365): the
    /// admin overview's aggregation minus perUser, scoped strictly to the
    /// caller's rows — per-day from the rollups, topItems/uniqueItems from raw
    /// rows (degrade to empty/0 once pruned, never an error).
    /// </summary>
    public AnalyticsMeResponse GetMyOverview(string userId, int days)
    {
        var clampedDays = Paged.Clamp(days, DefaultOverviewDays, MaxOverviewDays);
        var todayUtc = _clock.GetUtcNow().UtcDateTime.Date;
        var fromDayDate = todayUtc.AddDays(-(clampedDays - 1));
        var fromMs = ToUnixMs(fromDayDate);
        var toMs = ToUnixMs(todayUtc.AddDays(1));

        // DB/host wiring stays here; the pure aggregation lives in the fold module.
        var rollups = _db.GetPlaybackRollups(DayString(fromDayDate), DayString(todayUtc), userId);
        var uniqueItems = _db.CountDistinctPlaybackItems(fromMs, toMs, userId);
        var (totals, perDay) = AnalyticsFold.FoldUserRollups(rollups, uniqueItems);
        var topItems = AnalyticsFold.FoldTopItems(_db.GetTopPlaybackItems(fromMs, toMs, MaxTopItems, userId));

        return new AnalyticsMeResponse(clampedDays, totals, perDay, topItems);
    }

    // ------------------------------------------------------------------
    // Maintenance (daily task; also the disable purge)
    // ------------------------------------------------------------------

    public sealed record AnalyticsMaintenanceResult(bool DisabledPurged, int DaysRolled, int RawPruned);

    /// <summary>
    /// One maintenance pass. When analytics is disabled this wipes ALL
    /// analytics data (raw + rollups) — disabling is a data-erasure switch.
    /// When enabled: idempotent rollup recompute over the recent window, then
    /// raw retention. The raw cutoff never removes rows belonging to a day the
    /// recompute window still covers, so retained rollups stay exact even with
    /// very short retention settings.
    /// </summary>
    public AnalyticsMaintenanceResult RunMaintenance()
    {
        var config = _config();
        if (!config.Enabled)
        {
            var (purgedSessions, purgedRollups) = _db.PurgeAnalyticsData();
            if (purgedSessions + purgedRollups > 0)
            {
                _logger.LogInformation(
                    "JellyPlay analytics disabled: purged {Sessions} raw playback sessions and {Rollups} rollup rows",
                    purgedSessions, purgedRollups);
            }

            return new AnalyticsMaintenanceResult(DisabledPurged: true, DaysRolled: 0, RawPruned: purgedSessions);
        }

        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        var retentionDays = Math.Max(0, config.RawRetentionDays);

        long pruneCutoff;
        int daysRolled;
        if (retentionDays == 0)
        {
            // Explicit zero retention: raw is wiped; nothing can be rolled.
            daysRolled = 0;
            pruneCutoff = long.MaxValue;
        }
        else
        {
            var windowDays = Math.Min(RollupWindowDays, retentionDays);
            daysRolled = _db.RecomputePlaybackRollups(now - windowDays * MsPerDay);
            // Never prune inside the recompute window (start of its oldest
            // day), so a recomputed day always rebuilds from complete raw data.
            var recomputeFloor = ToUnixMs(_clock.GetUtcNow().UtcDateTime.Date.AddDays(-windowDays));
            pruneCutoff = Math.Min(now - retentionDays * MsPerDay, recomputeFloor);
        }

        var pruned = _db.PrunePlaybackSessions(pruneCutoff);
        _logger.LogInformation(
            "JellyPlay analytics maintenance finished: {DaysRolled} days rolled up, {Pruned} raw sessions pruned (retention {RetentionDays} days)",
            daysRolled, pruned, retentionDays);
        return new AnalyticsMaintenanceResult(DisabledPurged: false, DaysRolled: daysRolled, RawPruned: pruned);
    }

    private static string DayString(DateTime utcDate)
        => utcDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static long ToUnixMs(DateTime utcDate)
        => (long)(utcDate - DateTime.UnixEpoch).TotalMilliseconds;

    /// <summary>In-flight session state (memory only — a restart abandons unfinished sessions).</summary>
    private sealed class OpenPlayback
    {
        public OpenPlayback(string key, string userId, string itemId)
        {
            Key = key;
            UserId = userId;
            ItemId = itemId;
        }

        public string Key { get; }

        public string UserId { get; }

        public string ItemId { get; }

        public string ItemName { get; set; } = string.Empty;

        public string ItemType { get; set; } = string.Empty;

        public string? SeriesName { get; set; }

        public string PlayMethod { get; set; } = "DirectPlay";

        public string? VideoCodec { get; set; }

        public string? AudioCodec { get; set; }

        public long? Bitrate { get; set; }

        public string? TranscodeReasonsJson { get; set; }

        public long PositionTicks { get; set; }

        public long? DurationTicks { get; set; }

        public long StartedAtMs { get; set; }

        public long LastSeenMs { get; set; }

        public string? ClientName { get; set; }

        public string? DeviceName { get; set; }
    }
}
