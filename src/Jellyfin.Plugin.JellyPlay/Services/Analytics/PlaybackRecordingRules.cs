namespace Jellyfin.Plugin.JellyPlay.Services.Analytics;

using Jellyfin.Plugin.JellyPlay.Storage;

/// <summary>
/// The pure anti-noise/dedup rules for playback recording. Every decision is
/// expressed here so tests can pin them without a host: a session must have
/// actually been watched (>= MinPositionSeconds at the stop point) and lasted
/// a real while (>= MinWallSeconds) to be worth a row, rows dedup on the
/// (user, item, start-minute) bucket, and abandoned in-flight sessions close
/// after the idle grace window.
/// </summary>
public static class PlaybackRecordingRules
{
    /// <summary>Sessions shorter than this wall time are noise (channel flips, failed starts).</summary>
    public const long MinWallSeconds = 30;

    /// <summary>Sessions with less watch progress than this are scrub-away noise.</summary>
    public const long MinPositionSeconds = 60;

    /// <summary>StartedAt is truncated to this bucket; one row max per (UserId, ItemId, bucket). Canonical value lives with the unique index (JellyPlayDatabase.MinuteBucketMs).</summary>
    public const long MinuteBucketMs = JellyPlayDatabase.MinuteBucketMs;

    /// <summary>An in-flight session with no progress for longer than this is abandoned and closed on the next observed event.</summary>
    public const long StaleSessionGraceMs = 10 * MinuteBucketMs;

    /// <summary>The anti-noise gate: both the wall time and the watch position must clear their floors.</summary>
    public static bool IsRecordable(long wallSeconds, long positionSeconds)
        => wallSeconds >= MinWallSeconds && positionSeconds >= MinPositionSeconds;

    /// <summary>Truncates a unix-ms timestamp to its minute bucket (the dedup component of StartedAt).</summary>
    public static long MinuteBucket(long unixMs)
        => unixMs - (unixMs % MinuteBucketMs);

    /// <summary>Whether an in-flight session last seen at <paramref name="lastSeenMs"/> is abandoned by <paramref name="nowMs"/>.</summary>
    public static bool IsStale(long lastSeenMs, long nowMs)
        => nowMs - lastSeenMs > StaleSessionGraceMs;
}
