using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyPlay.Services.Events;

/// <summary>
/// Thin adapter over <see cref="NewMediaPipeline"/>: watches
/// ILibraryManager.ItemAdded (movies publish immediately, episodes buffer per
/// season and publish as one grouped event after the configured window) and
/// drains due groups on the flush tick. All grouping, library resolution,
/// allow-listing and dedup locality lives in the pipeline — this module keeps
/// no buffer, clock or folder logic of its own beyond the timer.
/// Flush loop ticks every 10s (<see cref="NewMediaPipeline.DefaultTick"/>);
/// each tick snapshots the host enumeration inputs (administrator ids,
/// virtual folders) once, so a 2000-item import resolves each once per tick
/// instead of once per item.
/// </summary>
public sealed class ItemAddedWatcher : IHostedService, IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly EventService _events;
    private readonly Services.Admin.AdminUsers _adminUsers;
    private readonly ILogger<ItemAddedWatcher> _logger;
    private readonly TimeProvider _clock;
    private readonly NewMediaPipeline _pipeline;
    private Timer? _flushTimer;
    private static readonly TimeSpan _tick = NewMediaPipeline.DefaultTick;

    public ItemAddedWatcher(
        ILibraryManager libraryManager,
        EpisodeGroupBuffer buffer,
        EventService events,
        Func<EventsConfig> config,
        Services.Admin.AdminUsers adminUsers,
        ILogger<ItemAddedWatcher> logger,
        TimeProvider? clock = null,
        NewMediaPipeline? pipeline = null)
    {
        _libraryManager = libraryManager;
        _events = events;
        _adminUsers = adminUsers;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _pipeline = pipeline ?? new NewMediaPipeline(
            config,
            _clock,
            () => libraryManager.GetVirtualFolders(),
            null,
            buffer);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        _pipeline.RefreshVirtualFolders();
        _flushTimer = new Timer(FlushDue, null, _tick, _tick);
        _logger.LogInformation("JellyPlay ItemAddedWatcher started");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        _flushTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _pipeline.DrainAll(_events, _adminUsers.AdminUserIds);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _flushTimer?.Dispose();
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs args)
    {
        var item = args.Item;
        if (item is null || item.IsVirtualItem || item.IsFolder)
        {
            return;
        }

        // No enabled check here — the pipeline owns the gate (intake and
        // emission alike).
        switch (item)
        {
            case Movie movie:
                try
                {
                    var movieLibraryId = _pipeline.ResolveLibraryId(movie.Path);
                    _events.PublishNewMovie(movie.Id, movie.Name ?? "New movie", movieLibraryId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to publish new-movie event for {Item}", movie.Name);
                }

                break;

            case Episode episode:
            {
                var seasonId = episode.FindSeasonId();
                if (seasonId == Guid.Empty || episode.SeriesId == Guid.Empty)
                {
                    return;
                }

                _pipeline.TryAddEpisode(
                    seasonId,
                    episode.SeriesId,
                    episode.SeriesName ?? string.Empty,
                    episode.ParentIndexNumber,
                    episode.Id,
                    episode.Name ?? string.Empty,
                    episode.Path);
                break;
            }
        }
    }

    private void FlushDue(object? state)
    {
        try
        {
            // The whole tick body (self-gating fast path, the per-tick folder
            // snapshot, the drain under the batch's shared admin audience)
            // lives in the pipeline.
            _pipeline.DrainDue(_events, _adminUsers.AdminUserIds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "New-media flush failed");
        }
    }
}
