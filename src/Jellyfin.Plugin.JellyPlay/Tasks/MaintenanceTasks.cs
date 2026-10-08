using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Services.Anime;
using Jellyfin.Plugin.JellyPlay.Services.Cache;
using Jellyfin.Plugin.JellyPlay.Storage;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyPlay.Tasks;

/// <summary>
/// Prunes the settings change log, the recorded sync-operation history and
/// the restore-point snapshots beyond their retention windows.
/// </summary>
public sealed class ChangeLogPruneTask : IScheduledTask
{
    private readonly JellyPlayDatabase _db;
    private readonly Func<Configuration.SyncConfig> _config;
    private readonly ILogger<ChangeLogPruneTask> _logger;

    public ChangeLogPruneTask(JellyPlayDatabase db, Func<Configuration.SyncConfig> config, ILogger<ChangeLogPruneTask> logger)
    {
        _db = db;
        _config = config;
        _logger = logger;
    }

    public string Name => "JellyPlay: prune settings change log, sync history and snapshots";

    public string Key => "JellyPlay.ChangeLogPrune";

    public string Description => "Removes settings change-log entries, recorded sync operations and restore-point snapshots older than their retention windows.";

    public string Category => "JellyPlay";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = _config();
        var changeLogRows = await Task.Run(() => _db.PruneChangeLog(config.ChangeLogRetentionDays), cancellationToken);
        progress.Report(33);
        var historyRows = await Task.Run(() => _db.PruneSyncHistory(config.HistoryRetentionDays), cancellationToken);
        progress.Report(66);
        var snapshotRows = await Task.Run(() => _db.PruneSnapshots(config.SnapshotRetentionDays), cancellationToken);
        _logger.LogInformation(
            "JellyPlay prune finished: {ChangeLogRows} change-log rows, {HistoryRows} sync-history rows and {SnapshotRows} snapshots removed (retention {ChangeLogDays}/{HistoryDays}/{SnapshotDays} days)",
            changeLogRows, historyRows, snapshotRows, config.ChangeLogRetentionDays, config.HistoryRetentionDays, config.SnapshotRetentionDays);
        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        => new[] { new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(24).Ticks } };
}

/// <summary>
/// Refreshes the Fribb anime id-mapping index (and warms the marker caches for
/// library series). Capped per run with ordered, jittered iteration so a fresh
/// install doesn't stampede AnimeFillerList/Tenrai; series whose sources are
/// circuit-open are skipped. (The cap/delay are consts — the config object is a
/// shared surface.)
/// </summary>
public sealed class AnimeMarkersRefreshTask : IScheduledTask
{
    private const int MaxSeriesPerRun = 500;
    private const int PerSeriesDelayMs = 250;

    private readonly AnimeMarkersService _markers;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<AnimeMarkersRefreshTask> _logger;

    public AnimeMarkersRefreshTask(AnimeMarkersService markers, ILibraryManager libraryManager, ILogger<AnimeMarkersRefreshTask> logger)
    {
        _markers = markers;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public string Name => "JellyPlay: refresh anime markers";

    public string Key => "JellyPlay.AnimeMarkersRefresh";

    public string Description => "Refreshes the anime id mapping and marker caches used for filler/recap badges.";

    public string Category => "JellyPlay";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        // Deterministic slice of the library series so runs are reproducible.
        // Ordering contract: raw Guid order (structural comparison, no
        // ToString allocation). The host query's OrderBy maps to library
        // columns, not Guids, so Limit cannot be pushed down without changing
        // the slice. Raw Guid order is a DIFFERENT total order than the
        // canonical-string order this used to sort by (they disagree only for
        // ids whose first component has its high bit set), but equally
        // deterministic: the same 500 series are selected on every run.
        var series = _libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery(null)
        {
            IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Series }
        })
        .OrderBy(item => item.Id)
        .Take(MaxSeriesPerRun)
        .ToList();

        var warmed = 0;
        var missed = 0;
        var skipped = 0;
        var done = 0;
        foreach (var item in series)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_markers.IsEnabled || _markers.AllFetchBreakersOpen)
            {
                skipped++;
            }
            else
            {
                try
                {
                    if (await _markers.GetSeriesMarkers(item.Id.ToString(), providerSeriesId: null) is not null)
                    {
                        warmed++;
                    }
                    else
                    {
                        missed++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Marker warm failed for {Series}", item.Name);
                    missed++;
                }
            }

            done++;
            progress.Report(100.0 * done / Math.Max(1, series.Count));

            // Jitter between warm calls so the run paces the upstream sources.
            if (done < series.Count)
            {
                await Task.Delay(PerSeriesDelayMs, cancellationToken);
            }
        }

        _logger.LogInformation(
            "JellyPlay anime markers warm finished: {Warmed} warmed, {Missed} without markers, {Skipped} skipped (disabled/breakers open), {RunSize} series in run (cap {MaxSeriesPerRun})",
            warmed, missed, skipped, series.Count, MaxSeriesPerRun);
        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        => new[] { new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(24).Ticks } };
}

/// <summary>
/// Daily sweep of the plugin's file cache: purges entries past their hard age
/// ceiling and evicts oldest-last-written entries until the directory is back
/// under the configured size cap (Cache:MaxSizeMegabytes). The write path also
/// sweeps incrementally; this task guarantees a cap-compliance pass even on an
/// idle server.
/// </summary>
public sealed class CacheMaintenanceTask : IScheduledTask
{
    private readonly FileCacheStore _cache;
    private readonly ILogger<CacheMaintenanceTask> _logger;

    public CacheMaintenanceTask(FileCacheStore cache, ILogger<CacheMaintenanceTask> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public string Name => "JellyPlay: cache maintenance";

    public string Key => "JellyPlay.CacheMaintenance";

    public string Description => "Purges expired plugin cache entries and evicts the oldest until the cache directory is under its size cap.";

    public string Category => "JellyPlay";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var result = _cache.Sweep(_cache.MaxTotalBytes(), FileCacheStore.DefaultMaxEntryAge);
        _logger.LogInformation("JellyPlay cache maintenance finished: {Deleted} entries removed, {Bytes} bytes reclaimed",
            result.DeletedFiles, result.BytesReclaimed);
        progress.Report(100);
        return Task.CompletedTask;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        => new[] { new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromDays(1).Ticks } };
}
