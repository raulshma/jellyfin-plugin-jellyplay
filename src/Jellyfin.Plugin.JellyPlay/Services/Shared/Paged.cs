using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyPlay.Helpers;

namespace Jellyfin.Plugin.JellyPlay.Services.Shared;

/// <summary>
/// The ONE paging seam for the read surfaces: page-size clamping
/// (<c>Clamp(limit, def, max)</c> — "?limit=" defaults when absent/0, clamps
/// 1..max), cursor-to-offset normalization, over-fetch-by-one detection and
/// <c>nextCursor</c> assignment. Every paged surface (settings snapshots,
/// deltas, history, diffs, audit exports) leverages this module instead of
/// re-implementing the idiom, so the wire pagination semantics
/// (<c>nextCursor</c> present ONLY when more rows follow, absent = last page)
/// stay identical at every site. The clamp itself delegates to
/// <see cref="RequestLimits"/> — the single limit idiom — this module adds
/// the offset/over-fetch/cursor locality above it.
/// </summary>
public static class Paged
{
    /// <summary>Page-size clamp for nullable wire limits: null/0 means the default, then 1..max.</summary>
    public static int Clamp(int? limit, int @default, int max)
        => RequestLimits.Clamp(limit ?? 0, @default, max);

    /// <summary>Page-size clamp for non-nullable limits: 0 means the default, then 1..max.</summary>
    public static int Clamp(int limit, int @default, int max)
        => RequestLimits.Clamp(limit, @default, max);

    /// <summary>
    /// Cursor-to-offset for in-memory slices: the cursor is the opaque count of
    /// already-served rows, normalized into [0, totalCount] (the store ordering
    /// is stable, so offsets are safe).
    /// </summary>
    public static int Offset(long? cursor, int totalCount)
        => (int)Math.Clamp(cursor ?? 0, 0, totalCount);

    /// <summary>
    /// Cursor-to-offset for SQL-paged reads: normalized into [0, int.MaxValue]
    /// (no total is known without a second aggregate scan — the over-fetch
    /// below detects a following page instead).
    /// </summary>
    public static int Offset(long? cursor)
        => (int)Math.Min(Math.Max(cursor ?? 0, 0), int.MaxValue);

    /// <summary>
    /// Offset-based slice over already-materialized rows: the cursor window
    /// [offset, offset + limit) with <c>nextCursor</c> null on the last page.
    /// </summary>
    public static (IReadOnlyList<T> Page, long? NextCursor) Slice<T>(IReadOnlyList<T> rows, long? cursor, int? limit, int @default, int max)
    {
        var clamped = Clamp(limit, @default, max);
        var start = Offset(cursor, rows.Count);
        var page = rows.Skip(start).Take(clamped).ToList();
        long? nextCursor = start + page.Count < rows.Count ? start + page.Count : null;
        return (page, nextCursor);
    }

    /// <summary>
    /// Over-fetch-by-one fold for SQL-paged reads: the caller fetches
    /// <c>clamped + 1</c> rows at <paramref name="offset"/>; a surplus row
    /// means a following page starting at <c>offset + clamped</c>, otherwise
    /// this page is the last. Produces the exact <c>nextCursor</c> semantics
    /// <see cref="Slice{T}"/> produces, without a total-count query.
    /// </summary>
    public static (IReadOnlyList<T> Page, long? NextCursor) FromOverFetch<T>(IReadOnlyList<T> fetched, int offset, int clamped)
    {
        if (fetched.Count > clamped)
        {
            return (fetched.Take(clamped).ToList(), (long?)offset + clamped);
        }

        return (fetched.ToList(), null);
    }
}
