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
/// Where the pipeline hands an emission over: <see cref="EventService"/>
/// implements this (payload shaping + the SSE + push fan-out). The pipeline
/// decides WHEN; the sink decides HOW.
/// </summary>
public interface INewMediaSink
{
    int PublishNewMedia(EpisodeGroup group, IReadOnlyList<string>? adminUserIds = null);

    int PublishNewMovie(Guid itemId, string title, string? libraryId);
}

/// <summary>
/// The deep new-media pipeline behind <see cref="ItemAddedWatcher"/> and
/// <see cref="EventService"/>: episode grouping, the ONE emission gate
/// (enabled + library allow-list + dedup, decided exactly once per emission),
/// and the drain orchestration live here. The watcher is a thin adapter
/// (host event subscription + timer); <see cref="EventService"/> only shapes
/// payloads and fans out.
///
/// Locality: the emit decision used to ping-pong (dedup state here, the
/// decision to consult it in EventService, the enabled flag checked in three
/// layers); it is now one seam per emission kind — <see cref="ShouldEmit"/>
/// and <see cref="ShouldEmitMovie"/> — so a gating-rule change touches one
/// module. The dedup-mutating check runs EXACTLY once per emission: the
/// drain paths never pre-gate groups, they only fast-path on
/// <see cref="HasWork"/>.
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
        Func<EventsConfig> config,
        TimeProvider? clock = null,
        Func<IReadOnlyList<MediaBrowser.Model.Entities.VirtualFolderInfo>>? virtualFolders = null,
        ILogger<NewMediaPipeline>? logger = null,
        EpisodeGroupBuffer? buffer = null)
    {
        _buffer = buffer ?? new EpisodeGroupBuffer();
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
    /// The library allow-list gate: empty allow-list or null library id
    /// passes; otherwise the parsed guid must be listed (unparsable ids check
    /// against Guid.Empty, as before).
    /// </summary>
    internal bool IsLibraryAllowed(string? libraryId)
    {
        var allowed = _config().NewMediaEnabledLibraries;
        if (allowed.Count == 0 || libraryId is null)
        {
            return true;
        }

        return allowed.Contains(Guid.TryParse(libraryId, out var guid) ? guid : Guid.Empty);
    }

    /// <summary>Dedup key for a flushed episode group (first episode drives the key, as before).</summary>
    private static string GroupDedupKey(EpisodeGroup group)
        => $"new-media:{group.SeriesId}:{group.SeasonIndex}:{group.Episodes[0].ItemId}";

    /// <summary>Dedup key for a movie item.</summary>
    private static string MovieDedupKey(Guid itemId) => $"new-media:{itemId}";

    /// <summary>
    /// The ONE emission gate for a flushed episode group: enabled, library
    /// allow-list and dedup in one call — the dedup-mutating check runs here
    /// and only here, so the drain loop and EventService can never
    /// double-check (a second check would suppress).
    /// </summary>
    public bool ShouldEmit(EpisodeGroup group)
    {
        var config = _config();
        if (!config.NewMediaEnabled || !IsLibraryAllowed(group.LibraryId))
        {
            return false;
        }

        return ShouldEmit(GroupDedupKey(group), config.DedupThresholdSeconds);
    }

    /// <summary>The ONE emission gate for a movie item (the same three rules, movie shape).</summary>
    public bool ShouldEmitMovie(Guid itemId, string? libraryId)
    {
        var config = _config();
        if (!config.NewMediaEnabled || !IsLibraryAllowed(libraryId))
        {
            return false;
        }

        return ShouldEmit(MovieDedupKey(itemId), config.DedupThresholdSeconds);
    }

    /// <summary>Whether a flush tick could have work: enabled AND something buffered (the drain's fast path).</summary>
    public bool HasWork => _config().NewMediaEnabled && _buffer.PendingGroupCount > 0;

    /// <summary>
    /// Releases every due group to the sink (the flush tick's whole body):
    /// the self-gating fast path (disabled or nothing buffered skips the
    /// folder refresh and the drain), the per-tick virtual-folder snapshot,
    /// and the batch's shared admin audience. The sink's
    /// <c>PublishNewMedia</c> applies the emission gate (<see cref="ShouldEmit"/>)
    /// — this path never pre-gates groups.
    /// </summary>
    public void DrainDue(INewMediaSink sink, IReadOnlyList<string> adminUserIds)
    {
        if (!HasWork)
        {
            return;
        }

        RefreshVirtualFolders();
        foreach (var group in PopDue())
        {
            sink.PublishNewMedia(group, adminUserIds);
        }
    }

    /// <summary>Releases every buffered group to the sink (the shutdown drain), under the same no-pre-gate rule.</summary>
    public void DrainAll(INewMediaSink sink, IReadOnlyList<string> adminUserIds)
    {
        foreach (var group in FlushAll())
        {
            sink.PublishNewMedia(group, adminUserIds);
        }
    }

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
