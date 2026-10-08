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
/// Watches ILibraryManager.ItemAdded: movies publish immediately, episodes
/// buffer per season and publish as one grouped event after the configured
/// window. Flush loop ticks every 10 seconds; each tick snapshots the host
/// enumeration inputs (administrator ids, virtual folders) once, so a
/// 2000-item import resolves each once per tick instead of once per item.
/// </summary>
public sealed class ItemAddedWatcher : IHostedService, IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly EpisodeGroupBuffer _buffer;
    private readonly EventService _events;
    private readonly Func<EventsConfig> _config;
    private readonly Services.Admin.AdminUsers _adminUsers;
    private readonly ILogger<ItemAddedWatcher> _logger;
    private Timer? _flushTimer;
    private static readonly TimeSpan _tick = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The virtual-folder snapshot the per-item path match reads. Refreshed
    /// once per flush tick (and at startup) — GetVirtualFolders walks the
    /// whole library tree, so it must not run per item.
    /// </summary>
    private volatile IReadOnlyList<MediaBrowser.Model.Entities.VirtualFolderInfo> _virtualFolders
        = Array.Empty<MediaBrowser.Model.Entities.VirtualFolderInfo>();

    public ItemAddedWatcher(
        ILibraryManager libraryManager,
        EpisodeGroupBuffer buffer,
        EventService events,
        Func<EventsConfig> config,
        Services.Admin.AdminUsers adminUsers,
        ILogger<ItemAddedWatcher> logger)
    {
        _libraryManager = libraryManager;
        _buffer = buffer;
        _events = events;
        _config = config;
        _adminUsers = adminUsers;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        RefreshVirtualFolders();
        _flushTimer = new Timer(FlushDue, null, _tick, _tick);
        _logger.LogInformation("JellyPlay ItemAddedWatcher started");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        _flushTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        var adminUserIds = _adminUsers.AdminUserIds;
        foreach (var group in _buffer.FlushAll())
        {
            _events.PublishNewMedia(group, adminUserIds);
        }

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

        var config = _config();
        if (!config.NewMediaEnabled)
        {
            return;
        }

        var libraryId = ResolveLibraryId(item);
        switch (item)
        {
            case Movie movie:
                try
                {
                    _events.PublishNewMovie(movie.Id, movie.Name ?? "New movie", libraryId);
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

                _buffer.Add(
                    seasonId,
                    episode.SeriesId,
                    episode.SeriesName ?? string.Empty,
                    episode.ParentIndexNumber,
                    episode.Id,
                    episode.Name ?? string.Empty,
                    libraryId,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                break;
            }
        }
    }

    private void FlushDue(object? state)
    {
        try
        {
            // One snapshot per tick: the admin audience for the whole batch and
            // the virtual folders the per-item library match reads.
            RefreshVirtualFolders();
            var adminUserIds = _adminUsers.AdminUserIds;
            var window = _config().NewMediaGroupingSeconds;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var group in _buffer.PopDue(now, window))
            {
                _events.PublishNewMedia(group, adminUserIds);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "New-media flush failed");
        }
    }

    private void RefreshVirtualFolders()
    {
        try
        {
            _virtualFolders = _libraryManager.GetVirtualFolders();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not snapshot virtual folders");
        }
    }

    /// <summary>
    /// Resolves the virtual-folder (library) id the item lives in, mirroring
    /// streamyfin's path match — against the per-tick snapshot, never a fresh
    /// GetVirtualFolders walk.
    /// </summary>
    private string? ResolveLibraryId(BaseItem item)
    {
        try
        {
            var folder = _virtualFolders
                .FirstOrDefault(vf => !string.IsNullOrEmpty(item.Path)
                    && vf.Locations.Any(location => item.Path.Contains(location, StringComparison.OrdinalIgnoreCase)));
            return folder?.ItemId.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve library for {Path}", item.Path);
            return null;
        }
    }
}
