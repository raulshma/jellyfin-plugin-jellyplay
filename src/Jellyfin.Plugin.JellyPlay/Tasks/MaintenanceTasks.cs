using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Services.Anime;
using Jellyfin.Plugin.JellyPlay.Storage;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyPlay.Tasks;

/// <summary>Prunes the settings change log beyond its retention window.</summary>
public sealed class ChangeLogPruneTask : IScheduledTask
{
    private readonly JellyPlayDatabase _db;

    public ChangeLogPruneTask(JellyPlayDatabase db)
    {
        _db = db;
    }

    public string Name => "JellyPlay: prune settings change log";

    public string Key => "JellyPlay.ChangeLogPrune";

    public string Description => "Removes settings change-log entries older than the retention window.";

    public string Category => "JellyPlay";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var retention = JellyPlayPlugin.Instance!.Configuration.Sync.ChangeLogRetentionDays;
        await Task.Run(() => _db.PruneChangeLog(retention), cancellationToken);
        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        => new[] { new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(24).Ticks } };
}

/// <summary>Refreshes the Fribb anime id-mapping index (and warms the filler cache for configured series).</summary>
public sealed class AnimeMarkersRefreshTask : IScheduledTask
{
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
        // Warm the mapping index for series in anime-flavored libraries.
        var series = _libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery(null)
        {
            IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Series },
            Limit = 2000
        });

        var done = 0;
        foreach (var item in series)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (_markers.IsEnabled)
                {
                    await _markers.GetSeriesMarkers(item.Id.ToString(), item.Id.ToString());
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Marker warm failed for {Series}", item.Name);
            }

            done++;
            progress.Report(100.0 * done / Math.Max(1, series.Count));
        }

        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        => new[] { new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(24).Ticks } };
}
