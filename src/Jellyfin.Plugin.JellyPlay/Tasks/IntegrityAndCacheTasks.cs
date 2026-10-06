using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Services.Cache;
using Jellyfin.Plugin.JellyPlay.Services.Ratings;
using Jellyfin.Plugin.JellyPlay.Services.Rows;
using Jellyfin.Plugin.JellyPlay.Storage;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyPlay.Tasks;

/// <summary>
/// Runs SQLite integrity_check + a WAL checkpoint on the plugin database and
/// reports the outcome to the activity log — the settings-store integrity
/// check, scheduled rather than ad-hoc so corruption surfaces early (the
/// atomic-write/quarantine hygiene moonfin ships, database-flavoured).
/// </summary>
public sealed class SettingsIntegrityTask : IScheduledTask
{
    private readonly JellyPlayDatabase _db;
    private readonly ILogger<SettingsIntegrityTask> _logger;

    public SettingsIntegrityTask(JellyPlayDatabase db, ILogger<SettingsIntegrityTask> logger)
    {
        _db = db;
        _logger = logger;
    }

    public string Name => "JellyPlay: check settings store integrity";

    public string Key => "JellyPlay.SettingsIntegrity";

    public string Description => "Runs an SQLite integrity check and WAL checkpoint on the JellyPlay plugin database.";

    public string Category => "JellyPlay";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var result = _db.CheckIntegrity();
        if (result.IntegrityOk)
        {
            _logger.LogInformation("JellyPlay database integrity OK ({Checked}), checkpointed {WalFrames} WAL frames",
                result.Details, result.WalCheckpointedFrames);
        }
        else
        {
            _logger.LogError("JellyPlay database INTEGRITY FAILURE: {Details}", result.Details);
        }

        progress.Report(100);
        return Task.CompletedTask;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        => new[] { new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromDays(1).Ticks } };
}

/// <summary>
/// Warms the external caches (IMDb top250 chart, seasonal keyword row) so the
/// first client request of the day is served from cache instead of paying the
/// upstream latency.
/// </summary>
public sealed class ExternalCacheWarmTask : IScheduledTask
{
    private readonly ImdbChartsService _imdb;
    private readonly SeasonalService _seasonal;
    private readonly ILogger<ExternalCacheWarmTask> _logger;

    public ExternalCacheWarmTask(ImdbChartsService imdb, SeasonalService seasonal, ILogger<ExternalCacheWarmTask> logger)
    {
        _imdb = imdb;
        _seasonal = seasonal;
        _logger = logger;
    }

    public string Name => "JellyPlay: warm external caches";

    public string Key => "JellyPlay.ExternalCacheWarm";

    public string Description => "Refreshes the IMDb chart and seasonal row caches used by JellyPlay clients.";

    public string Category => "JellyPlay";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        try
        {
            await _imdb.GetTop250();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Chart warm skipped");
        }

        progress.Report(50);

        try
        {
            await _seasonal.GetSeasonalRow(null);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Seasonal warm skipped");
        }

        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        => new[] { new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(12).Ticks } };
}
