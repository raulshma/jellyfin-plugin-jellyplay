using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyPlay.Configuration;
using Jellyfin.Plugin.JellyPlay.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.JellyPlay.Services.Events;

/// <summary>
/// The deep new-media pipeline behind <see cref="ItemAddedWatcher"/>: episode
/// grouping, library allow-listing, per-key dedup and virtual-folder
/// resolution live here, so the watcher stays a thin adapter
/// (OnItemAdded → Add, FlushDue → PopDue → EventService).
///
/// Locality: <see cref="EpisodeGroupBuffer"/> keeps the pure grouping;
/// this module adds the TimeProvider + VirtualFolderProvider Func seams, the
/// allow-list gate and the ShouldEmit dedup (moved from
/// <see cref="EventService"/> / the watcher byte-for-byte). The flush tick
/// stays <see cref="DefaultTick"/> (10s) unless the caller passes another.
/// </summary>
public sealed class NewMediaPipeline
{
    /// <summary>The flush-loop tick: every 10s, preserved from the watcher.</summary>
    public static readonly TimeSpan DefaultTick = TimeSpan.FromSeconds(10);

    private readonly EpisodeGroupBuffer _buffer;
    private readonly Func<EventsConfig> _config;
    private readonly TimeProvider _clock;
    private readonly Func<IReadOnlyList<MediaBrowser.Model.Entities.VirtualFolderInfo>>? _virtualFoldersProvider;
    private readonly ILogger _logger;

    private volatile IReadOnlyList<MediaBrowser.Model.Entities.VirtualFolderInfo> _virtualFoldersSnapshot
        = Array.Empty<MediaBrowser.Model.Entities.VirtualFolderInfo>();

    /// <summary>
    /// Directory → library id memo, scoped to one refresh cycle (cleared when
    /// the folder snapshot changes): episodes of one season share a directory,
    /// so a bulk import resolves each folder's O(folders × locations)
    /// substring scan once instead of once per episode.
    /// </summary>
    private readonly ConcurrentDictionary<string, string?> _libraryByFolder
        = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, long> _recentKeys = new();
    private long _lastSweepMs;

    public NewMediaPipeline(
        EpisodeGroupBuffer buffer,
        Func<EventsConfig> config,
        TimeProvider? clock = null,
        Func<IReadOnlyList<MediaBrowser.Model.Entities.VirtualFolderInfo>>? virtualFolders = null,
        ILogger<NewMediaPipeline>? logger = null)
    {
        _buffer = buffer;
        _config = config;
        _clock = clock ?? TimeProvider.System;
        _virtualFoldersProvider = virtualFolders;
        _logger = logger ?? NullLogger<NewMediaPipeline>.Instance;
    }

    /// <summary>The clock behind grouping timestamps and dedup windows (test seam).</summary>
    public TimeProvider Clock => _clock;

    /// <summary>
    /// Refreshes the virtual-folder snapshot from the provider Func (once per
    /// working flush tick and at startup — never per item, since the host walk is
    /// expensive). Failures keep the previous snapshot. A changed snapshot
    /// lapses the path-resolution memo (a path may resolve differently now).
    /// </summary>
    public void RefreshVirtualFolders()
    {
        if (_virtualFoldersProvider is null)
        {
            return;
        }

        try
        {
            _virtualFoldersSnapshot = _virtualFoldersProvider() ?? Array.Empty<MediaBrowser.Model.Entities.VirtualFolderInfo>();
            _libraryByFolder.Clear();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not snapshot virtual folders");
        }
    }

    /// <summary>
    /// Resolves the virtual-folder (library) id the item path lives in,
    /// mirroring streamyfin's path match — against the per-tick snapshot,
    /// never a fresh host walk. Memoized per item DIRECTORY for the snapshot's
    /// lifetime (see <see cref="_libraryByFolder"/>); a location match on the
    /// file path and on its directory are the same match because library
    /// locations are directories.
    /// </summary>
    public string? ResolveLibraryId(string? itemPath)
    {
        if (string.IsNullOrEmpty(itemPath))
        {
            return null;
        }

        try
        {
            var folderKey = System.IO.Path.GetDirectoryName(itemPath);
            if (string.IsNullOrEmpty(folderKey))
            {
                folderKey = itemPath;
            }

            if (_libraryByFolder.TryGetValue(folderKey, out var memoized))
            {
                return memoized;
            }

            var resolved = _virtualFoldersSnapshot
                .FirstOrDefault(vf => vf.Locations.Any(location => itemPath.Contains(location, StringComparison.OrdinalIgnoreCase)))
                ?.ItemId.ToString();
            _libraryByFolder[folderKey] = resolved;
            return resolved;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve library for {Path}", itemPath);
            return null;
        }
    }

    /// <summary>
    /// The library allow-list gate (moved from EventService byte-for-byte):
    /// empty allow-list or null library id passes; otherwise the parsed guid
    /// must be listed (unparsable ids check against Guid.Empty, as before).
    /// </summary>
    public bool IsLibraryAllowed(string? libraryId)
    {
        var allowed = _config().NewMediaEnabledLibraries;
        if (allowed.Count == 0 || libraryId is null)
        {
            return true;
        }

        return allowed.Contains(Guid.TryParse(libraryId, out var guid) ? guid : Guid.Empty);
    }

    /// <summary>Dedup key for a flushed episode group (first episode drives the key, as before).</summary>
    public static string GroupDedupKey(EpisodeGroup group)
        => $"new-media:{group.SeriesId}:{group.SeasonIndex}:{group.Episodes[0].ItemId}";

    /// <summary>Dedup key for a movie item.</summary>
    public static string MovieDedupKey(Guid itemId) => $"new-media:{itemId}";

    /// <summary>
    /// Whether the logical key may emit now (moved from EventService
    /// byte-for-byte): first-seen passes, repeats within the threshold
    /// suppress, with the occasional stale-key sweep. Mutates the recent-key
    /// store — the ONE dedup seam both the pipeline flush and EventService
    /// delegate to, so double-checking can never suppress.
    /// </summary>
    public bool ShouldEmit(string key, int thresholdSeconds)
    {
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        var thresholdMs = thresholdSeconds * 1000L;
        while (true)
        {
            if (_recentKeys.TryGetValue(key, out var last))
            {
                if (now - last < thresholdMs)
                {
                    return false;
                }

                if (_recentKeys.TryUpdate(key, now, last))
                {
                    break;
                }
            }
            else if (_recentKeys.TryAdd(key, now))
            {
                break;
            }
        }

        if (SweepGate.Enter(ref _lastSweepMs, now, Math.Max(thresholdMs, 60_000)))
        {
            foreach (var (k, timestamp) in _recentKeys)
            {
                if (now - timestamp > thresholdMs + 300_000)
                {
                    _recentKeys.TryRemove(k, out _);
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Episode intake (the watcher's OnItemAdded leg): invalid season/series
    /// ids drop, otherwise the episode buffers with its resolved library id.
    /// Returns false when nothing buffered (disabled or invalid ids).
    /// </summary>
    public bool TryAddEpisode(
        Guid seasonId,
        Guid seriesId,
        string seriesName,
        int? seasonIndex,
        Guid itemId,
        string episodeName,
        string? itemPath)
    {
        if (seasonId == Guid.Empty || seriesId == Guid.Empty)
        {
            return false;
        }

        if (!_config().NewMediaEnabled)
        {
            return false;
        }

        var libraryId = ResolveLibraryId(itemPath);
        _buffer.Add(
            seasonId,
            seriesId,
            seriesName,
            seasonIndex,
            itemId,
            episodeName,
            libraryId,
            _clock.GetUtcNow().ToUnixTimeMilliseconds());
        return true;
    }

    /// <summary>
    /// Releases every group whose window (from config) has fully elapsed,
    /// measured on the pipeline clock. Library + dedup gating stays in
    /// EventService (which leverages this module's seams), so the flush
    /// itself never double-mutates dedup.
    /// </summary>
    public IReadOnlyList<EpisodeGroup> PopDue()
    {
        var window = _config().NewMediaGroupingSeconds;
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        return _buffer.PopDue(now, window);
    }

    /// <summary>Releases every buffered group (shutdown drain).</summary>
    public IReadOnlyList<EpisodeGroup> FlushAll() => _buffer.FlushAll();

    /// <summary>Pending season-group count (observability; delegates to the buffer).</summary>
    public int PendingGroupCount => _buffer.PendingGroupCount;
}
