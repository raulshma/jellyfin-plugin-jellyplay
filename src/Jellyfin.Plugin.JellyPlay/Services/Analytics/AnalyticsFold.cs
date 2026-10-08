using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.JellyPlay.Api;
using Jellyfin.Plugin.JellyPlay.Helpers;
using Jellyfin.Plugin.JellyPlay.Services.Admin;
using Jellyfin.Plugin.JellyPlay.Storage.Models;

namespace Jellyfin.Plugin.JellyPlay.Services.Analytics;

/// <summary>
/// Pure playback-reporting folds — the deep module behind the admin "overview"
/// and per-user "me" surfaces. Every aggregation over rollups/sessions lives
/// here (locality), behind a small interface (leverage: two rollup folds + two
/// session folds serve both reporting paths and their tests). No DB, no host,
/// no clock, no singleton reads (ADR-0002): callers pass already-fetched rows
/// and the unique-items count they resolved; the folds only shape. Byte-identical
/// to the inline LINQ they replace.
/// </summary>
public static class AnalyticsFold
{
    /// <summary>
    /// Admin overview fold: totals (incl. distinct users + caller-resolved
    /// unique items) plus per-day and per-user rows from daily rollups.
    /// </summary>
    public static (AnalyticsTotals Totals, IReadOnlyList<AnalyticsPerDayRow> PerDay, IReadOnlyList<AnalyticsPerUserRow> PerUser) FoldRollups(
        IReadOnlyList<PlaybackRollupRow> rollups,
        Func<Guid, string?> resolveUserName,
        long uniqueItems)
    {
        var totals = new AnalyticsTotals(
            Plays: rollups.Sum(row => row.ItemsPlayed),
            PlaySeconds: rollups.Sum(row => row.PlaySeconds),
            TranscodeSeconds: rollups.Sum(row => row.TranscodeSeconds),
            UniqueUsers: rollups.Select(row => row.UserId).Distinct().Count(),
            UniqueItems: uniqueItems);

        var perDay = FoldPerDay(rollups);

        var perUser = rollups
            .GroupBy(row => row.UserId, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new AnalyticsPerUserRow(
                group.Key,
                AdminUsers.DisplayName(group.Key, resolveUserName),
                group.Sum(row => row.ItemsPlayed),
                group.Sum(row => row.PlaySeconds),
                group.Sum(row => row.TranscodeSeconds)))
            .ToList();

        return (totals, perDay, perUser);
    }

    /// <summary>
    /// Per-user ("Your watching") fold: the admin aggregation minus perUser,
    /// totals minus uniqueUsers (always the caller), scoped by the caller to
    /// their own rows before folding.
    /// </summary>
    public static (AnalyticsMeTotals Totals, IReadOnlyList<AnalyticsPerDayRow> PerDay) FoldUserRollups(
        IReadOnlyList<PlaybackRollupRow> rollups,
        long uniqueItems)
    {
        var totals = new AnalyticsMeTotals(
            Plays: rollups.Sum(row => row.ItemsPlayed),
            PlaySeconds: rollups.Sum(row => row.PlaySeconds),
            TranscodeSeconds: rollups.Sum(row => row.TranscodeSeconds),
            UniqueItems: uniqueItems);

        return (totals, FoldPerDay(rollups));
    }

    private static IReadOnlyList<AnalyticsPerDayRow> FoldPerDay(IReadOnlyList<PlaybackRollupRow> rollups)
        => rollups
            .GroupBy(row => row.Day, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new AnalyticsPerDayRow(
                group.Key,
                group.Sum(row => row.ItemsPlayed),
                group.Sum(row => row.PlaySeconds),
                group.Sum(row => row.TranscodeSeconds)))
            .ToList();

    /// <summary>Top-items fold from raw session aggregates (deterministic plays-desc, id tiebreak lives in the query).</summary>
    public static IReadOnlyList<AnalyticsTopItemRow> FoldTopItems(IEnumerable<PlaybackTopItemRow> rows)
        => rows
            .Select(row => new AnalyticsTopItemRow(row.ItemId, row.ItemName, row.ItemType, row.Plays, row.PlaySeconds))
            .ToList();

    /// <summary>Raw finished sessions newest-first already ordered by the query → session DTOs (named reasons, malformed JSON degrades to null).</summary>
    public static IReadOnlyList<AnalyticsSessionDto> FoldSessions(IEnumerable<PlaybackSessionRow> rows)
        => rows.Select(ToDto).ToList();

    private static AnalyticsSessionDto ToDto(PlaybackSessionRow row) => new(
        row.Id,
        row.UserId,
        row.ItemId,
        row.ItemName,
        row.ItemType,
        row.SeriesName,
        row.PlayMethod,
        row.VideoCodec,
        row.AudioCodec,
        row.Bitrate,
        ParseReasons(row.TranscodeReasonsJson),
        row.PositionTicks,
        row.DurationTicks,
        row.StartedAt,
        row.EndedAt,
        row.ClientName,
        row.DeviceName);

    private static string[]? ParseReasons(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Pure transcode-detail fold — the one place the host's TranscodingInfo shape
/// (direct-vs-re-encode nulling + named reason bits) is interpreted, shared by
/// the live monitor and the analytics recorder so both report identical
/// payloads (locality). Small interface: one nullable info in, one detail out
/// (leverage: two services + their tests fold through it). Leverages
/// <see cref="TranscodeReasonNames"/> for the flag-word decomposition.
/// </summary>
public static class TranscodeFold
{
    public sealed record TranscodeDetail(string? VideoCodec, string? AudioCodec, int? Bitrate, string[]? Reasons);

    /// <summary>
    /// One TranscodingInfo → detail. Null info (every direct play) folds to all
    /// nulls; otherwise codecs null out per direct axis, bitrate rides along,
    /// reasons decompose to named bits (null when none).
    /// </summary>
    public static TranscodeDetail Fold(MediaBrowser.Model.Session.TranscodingInfo? transcodingInfo)
    {
        if (transcodingInfo is null)
        {
            return new TranscodeDetail(null, null, null, null);
        }

        return new TranscodeDetail(
            transcodingInfo.IsVideoDirect ? null : transcodingInfo.VideoCodec,
            transcodingInfo.IsAudioDirect ? null : transcodingInfo.AudioCodec,
            transcodingInfo.Bitrate,
            TranscodeReasonNames.Decompose(transcodingInfo.TranscodeReasons));
    }
}
