using System;
using System.Collections.Generic;
using System.Linq;
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
    double? TranscodeReasons,
    long PositionTicks,
    bool IsPaused);

/// <summary>
/// Live transcode dashboard data folded from the host's session manager —
/// what stock Jellyfin keeps in memory; no extra persistence.
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
    {
        return _sessionManager.Sessions
            .Where(session => session.TranscodingInfo is not null && session.PlayState is not null)
            .Select(session =>
            {
                var transcode = session.TranscodingInfo!;
                return new ActiveTranscodeDto(
                    session.Id,
                    session.UserName,
                    session.DeviceName,
                    session.FullNowPlayingItem?.Name,
                    transcode.IsVideoDirect ? null : transcode.VideoCodec,
                    transcode.IsAudioDirect ? null : transcode.AudioCodec,
                    session.PlayState?.PlayMethod?.ToString(),
                    transcode.Bitrate,
                    null,
                    session.PlayState?.PositionTicks ?? 0,
                    session.PlayState?.IsPaused ?? false);
            })
            .ToList();
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
