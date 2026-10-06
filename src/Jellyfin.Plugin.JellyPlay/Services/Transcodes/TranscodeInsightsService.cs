using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyPlay.Services.Transcodes;

public sealed record ActiveTranscodeDto(
    string SessionId,
    string? UserName,
    string? DeviceName,
    string? ItemName,
    string? VideoCodec,
    string? AudioCodec,
    string? PlayMethod,
    int? VideoBitrate,
    string[]? TranscodeReasons,
    long PositionTicks,
    bool IsPaused);

/// <summary>
/// Live stream dashboard data folded from the host's session manager — what
/// stock Jellyfin keeps in memory; no extra persistence. The monitor lists
/// every actively playing session (direct play included) and carries the
/// re-encode detail — codecs, bitrate, reasons — when
/// <see cref="SessionInfo.TranscodingInfo"/> is present; the host only
/// populates that property for real transcodes (and clears it the moment a
/// direct play starts), so filtering on it would hide every direct-playing
/// stream and render an always-empty dashboard on the most common playback
/// path.
/// </summary>
public sealed class TranscodeInsightsService
{
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<TranscodeInsightsService> _logger;

    public TranscodeInsightsService(ISessionManager sessionManager, ILogger<TranscodeInsightsService> logger)
    {
        _sessionManager = sessionManager;
        _logger = logger;
    }

    public IReadOnlyList<ActiveTranscodeDto> GetActiveTranscodes()
        => _sessionManager.Sessions
            .Select(Fold)
            .Where(transcode => transcode is not null)
            .Cast<ActiveTranscodeDto>()
            .ToList();

    /// <summary>
    /// One live session → a dashboard row. Sessions without a now-playing item
    /// are not streams (auth-only / idle sessions) and fold to null; everything
    /// else folds, with the transcode fields null when the play is direct.
    /// </summary>
    internal static ActiveTranscodeDto? Fold(SessionInfo session)
    {
        var item = session.NowPlayingItem;
        if (item is null)
        {
            return null;
        }

        var transcode = session.TranscodingInfo;
        return new ActiveTranscodeDto(
            session.Id,
            session.UserName,
            session.DeviceName,
            item.Name,
            transcode is null || transcode.IsVideoDirect ? null : transcode.VideoCodec,
            transcode is null || transcode.IsAudioDirect ? null : transcode.AudioCodec,
            session.PlayState?.PlayMethod?.ToString(),
            transcode?.Bitrate,
            Reasons(transcode?.TranscodeReasons ?? default),
            session.PlayState?.PositionTicks ?? 0,
            session.PlayState?.IsPaused ?? false);
    }

    /// <summary>
    /// The host's [Flags] TranscodeReason decomposed into its named bits —
    /// a raw numeric flag word is unreadable on a dashboard row.
    /// </summary>
    private static string[]? Reasons(TranscodeReason reasons)
    {
        if (reasons == 0)
        {
            return null;
        }

        return Enum.GetValues<TranscodeReason>()
            .Where(value => value != 0 && reasons.HasFlag(value))
            .Select(value => value.ToString())
            .ToArray();
    }

    public IReadOnlyList<ActiveTranscodeDto> GetMine(string userName)
        => GetActiveTranscodes().Where(transcode => string.Equals(transcode.UserName, userName, StringComparison.OrdinalIgnoreCase)).ToList();

    public async Task<bool> CancelAsync(string sessionId)
    {
        var session = _sessionManager.Sessions.FirstOrDefault(candidate => string.Equals(candidate.Id, sessionId, StringComparison.Ordinal));
        if (session is null)
        {
            return false;
        }

        await _sessionManager.ReportSessionEnded(sessionId);
        return true;
    }
}
