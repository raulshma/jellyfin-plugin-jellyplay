using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Services.Analytics;
using Jellyfin.Plugin.JellyPlay.Storage;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyPlay.Tasks;

/// <summary>
/// Daily analytics pass. When the module is disabled it wipes ALL analytics
/// data (raw + rollups) — disabling is the data-erasure switch. When enabled
/// it recomputes the recent per-day/per-user rollups idempotently and prunes
/// raw playback sessions past their retention window (rollups are retained
/// forever).
/// </summary>
public sealed class AnalyticsMaintenanceTask : IScheduledTask
{
    private readonly AnalyticsService _analytics;
    private readonly ILogger<AnalyticsMaintenanceTask> _logger;

    public AnalyticsMaintenanceTask(AnalyticsService analytics, ILogger<AnalyticsMaintenanceTask> logger)
    {
        _analytics = analytics;
        _logger = logger;
    }

    public string Name => "JellyPlay: analytics maintenance";

    public string Key => "JellyPlay.AnalyticsMaintenance";

    public string Description => "Refreshes daily playback rollups and prunes raw playback sessions past their retention window; purges all analytics data when the module is disabled.";

    public string Category => "JellyPlay";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var result = _analytics.RunMaintenance();
        _logger.LogInformation(
            "JellyPlay analytics maintenance: disabledPurge={DisabledPurged}, daysRolled={DaysRolled}, rawPruned={RawPruned}",
            result.DisabledPurged, result.DaysRolled, result.RawPruned);
        progress.Report(100);
        return Task.CompletedTask;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        => new[] { new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromDays(1).Ticks } };
}
