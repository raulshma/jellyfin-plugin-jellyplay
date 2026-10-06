using System;
using Jellyfin.Plugin.JellyPlay.Services.Transcodes;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Session;
using Xunit;

namespace Jellyfin.Plugin.JellyPlay.Tests;

/// <summary>
/// Pins the session→dashboard fold. The load-bearing rule: a DIRECT-PLAYING
/// session still folds to a row — the host's SessionInfo.TranscodingInfo is
/// null for every direct play (cleared on direct-play start/progress), so a
/// filter on it hides all direct streams and the dashboard reads empty during
/// exactly the playback it exists to watch. Transcode detail (codecs, bitrate,
/// named reasons) rides along only while the host reports a real transcode.
/// </summary>
public class TranscodeInsightsServiceTests
{
    private static SessionInfo Session(
        string id = "sess-1",
        string? userName = "alice",
        BaseItemDto? nowPlaying = null,
        PlayerStateInfo? playState = null,
        TranscodingInfo? transcoding = null)
        => new(null!, null!)
        {
            Id = id,
            UserName = userName,
            DeviceName = "dev-1",
            NowPlayingItem = nowPlaying,
            PlayState = playState,
            TranscodingInfo = transcoding,
        };

    private static BaseItemDto NowPlaying(string name = "Movie Night") => new() { Name = name };

    [Fact]
    public void DirectPlaySessionFoldsToARowWithNoTranscodeDetail()
    {
        var row = TranscodeInsightsService.Fold(Session(
            nowPlaying: NowPlaying(),
            playState: new PlayerStateInfo
            {
                PlayMethod = PlayMethod.DirectPlay,
                PositionTicks = 1_234_000_000,
                IsPaused = true,
            }));

        Assert.NotNull(row);
        Assert.Equal("sess-1", row.SessionId);
        Assert.Equal("alice", row.UserName);
        Assert.Equal("Movie Night", row.ItemName);
        Assert.Equal("DirectPlay", row.PlayMethod);
        Assert.Null(row.VideoCodec);
        Assert.Null(row.AudioCodec);
        Assert.Null(row.VideoBitrate);
        Assert.Null(row.TranscodeReasons);
        Assert.Equal(1_234_000_000, row.PositionTicks);
        Assert.True(row.IsPaused);
    }

    [Fact]
    public void TranscodingSessionCarriesCodecsBitrateAndNamedReasons()
    {
        var row = TranscodeInsightsService.Fold(Session(
            nowPlaying: NowPlaying(),
            playState: new PlayerStateInfo { PlayMethod = PlayMethod.Transcode },
            transcoding: new TranscodingInfo
            {
                IsVideoDirect = false,
                IsAudioDirect = true,
                VideoCodec = "h264",
                AudioCodec = "aac",
                Bitrate = 4_500_000,
                TranscodeReasons = TranscodeReason.VideoCodecNotSupported | TranscodeReason.AudioBitrateNotSupported,
            }));

        Assert.NotNull(row);
        Assert.Equal("h264", row.VideoCodec);
        // Audio direct → no audio codec, exactly like the video axis.
        Assert.Null(row.AudioCodec);
        Assert.Equal(4_500_000, row.VideoBitrate);
        Assert.NotNull(row.TranscodeReasons);
        Assert.Equal(new[] { "VideoCodecNotSupported", "AudioBitrateNotSupported" }, row.TranscodeReasons);
    }

    [Fact]
    public void IdleSessionDoesNotFold()
    {
        // An auth-only/idle session carries a lingering TranscodingInfo-shaped
        // nothing and no now-playing item — not a stream, no row.
        Assert.Null(TranscodeInsightsService.Fold(Session(nowPlaying: null)));
    }

    [Fact]
    public void TranscodeReasonsOfNoneYieldNull()
    {
        var row = TranscodeInsightsService.Fold(Session(
            nowPlaying: NowPlaying(),
            playState: new PlayerStateInfo { PlayMethod = PlayMethod.Transcode },
            transcoding: new TranscodingInfo
            {
                IsVideoDirect = false,
                IsAudioDirect = false,
                VideoCodec = "hevc",
                AudioCodec = "opus",
                TranscodeReasons = 0,
            }));

        Assert.NotNull(row);
        Assert.Null(row.TranscodeReasons);
    }
}
