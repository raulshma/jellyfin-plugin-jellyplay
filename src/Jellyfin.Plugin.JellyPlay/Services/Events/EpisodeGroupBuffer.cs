using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.JellyPlay.Services.Events;

/// <summary>A flushed group of episodes added to one season within the grouping window.</summary>
public sealed record EpisodeGroup(
    Guid SeasonId,
    Guid SeriesId,
    string SeriesName,
    int? SeasonIndex,
    IReadOnlyList<EpisodeRef> Episodes,
    string? LibraryId);

public sealed record EpisodeRef(Guid ItemId, string Name);

/// <summary>
/// Buffers episodes per season and releases a group once the whole window has
/// elapsed since the first episode arrived — collapses season drops into a
/// single notification. Pure logic, clock injected; no I/O.
/// </summary>
public sealed class EpisodeGroupBuffer
{
    private sealed class BufferEntry
    {
        public required Guid SeriesId { get; init; }
        public required string SeriesName { get; init; }
        public int? SeasonIndex { get; init; }
        public string? LibraryId { get; init; }
        public long? FirstAddedMs { get; set; }
        public long LastAddedMs { get; set; }
        public required Dictionary<Guid, EpisodeRef> Episodes { get; init; }
    }

    private readonly ConcurrentDictionary<Guid, BufferEntry> _buffers = new();
    private readonly object _lock = new();

    public void Add(Guid seasonId, Guid seriesId, string seriesName, int? seasonIndex, Guid itemId, string episodeName, string? libraryId, long nowMs)
    {
        lock (_lock)
        {
            if (!_buffers.TryGetValue(seasonId, out var entry))
            {
                entry = new BufferEntry
                {
                    SeriesId = seriesId,
                    SeriesName = seriesName,
                    SeasonIndex = seasonIndex,
                    LibraryId = libraryId,
                    Episodes = new Dictionary<Guid, EpisodeRef>()
                };
                _buffers[seasonId] = entry;
            }

            entry.FirstAddedMs ??= nowMs;
            entry.LastAddedMs = nowMs;
            entry.Episodes[itemId] = new EpisodeRef(itemId, episodeName);
        }
    }

    /// <summary>Releases every group whose window (measured from its first arrival) has fully elapsed.</summary>
    public IReadOnlyList<EpisodeGroup> PopDue(long nowMs, int windowSeconds)
    {
        var due = new List<EpisodeGroup>();
        var windowMs = windowSeconds * 1000L;
        lock (_lock)
        {
            foreach (var (seasonId, entry) in _buffers)
            {
                if ((nowMs - entry.FirstAddedMs.GetValueOrDefault(nowMs)) < windowMs)
                {
                    continue;
                }

                due.Add(new EpisodeGroup(
                    seasonId,
                    entry.SeriesId,
                    entry.SeriesName,
                    entry.SeasonIndex,
                    entry.Episodes.Values.ToList(),
                    entry.LibraryId));
                _buffers.TryRemove(seasonId, out _);
            }
        }

        return due;
    }

    public IReadOnlyList<EpisodeGroup> FlushAll()
    {
        var all = new List<EpisodeGroup>();
        lock (_lock)
        {
            foreach (var (seasonId, entry) in _buffers)
            {
                all.Add(new EpisodeGroup(
                    seasonId,
                    entry.SeriesId,
                    entry.SeriesName,
                    entry.SeasonIndex,
                    entry.Episodes.Values.ToList(),
                    entry.LibraryId));
                _buffers.TryRemove(seasonId, out _);
            }
        }

        return all;
    }

    public int PendingGroupCount
    {
        get
        {
            lock (_lock)
            {
                return _buffers.Count;
            }
        }
    }
}
