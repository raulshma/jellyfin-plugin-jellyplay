using System.Collections.Concurrent;
using System.Linq;
using MediaBrowser.Model.Session;

namespace Jellyfin.Plugin.JellyPlay.Helpers;

/// <summary>
/// The host's [Flags] <see cref="TranscodeReason"/> decomposed into its named
/// bits — a raw numeric flag word is unreadable on a dashboard/analytics row.
/// Shared by the transcode monitor and the analytics recorder so both report
/// identical reason payloads. Called per session per poll: the enum values
/// and per-flag-word decompositions are computed once and memoized.
/// </summary>
public static class TranscodeReasonNames
{
    private static readonly TranscodeReason[] Values = Enum.GetValues<TranscodeReason>();

    /// <summary>
    /// Bounded by the enum's flag domain (realistic transcode-reason
    /// combinations), so it never needs eviction.
    /// </summary>
    private static readonly ConcurrentDictionary<TranscodeReason, string[]> Decomposed = new();

    /// <summary>
    /// Named bits for every reason set on <paramref name="reasons"/>; null
    /// when none. The returned array is the memoized instance — READ-ONLY by
    /// contract (every consumer serializes or carries it; none mutates).
    /// </summary>
    public static string[]? Decompose(TranscodeReason reasons)
    {
        if (reasons == 0)
        {
            return null;
        }

        return Decomposed.GetOrAdd(
            reasons,
            static value => Values
                .Where(flag => flag != 0 && value.HasFlag(flag))
                .Select(flag => flag.ToString())
                .ToArray());
    }
}
