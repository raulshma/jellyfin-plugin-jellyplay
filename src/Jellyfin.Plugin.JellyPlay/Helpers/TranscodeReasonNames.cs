using MediaBrowser.Model.Session;

namespace Jellyfin.Plugin.JellyPlay.Helpers;

/// <summary>
/// The host's [Flags] <see cref="TranscodeReason"/> decomposed into its named
/// bits — a raw numeric flag word is unreadable on a dashboard/analytics row.
/// Shared by the transcode monitor and the analytics recorder so both report
/// identical reason payloads.
/// </summary>
public static class TranscodeReasonNames
{
    /// <summary>Named bits for every reason set on <paramref name="reasons"/>; null when none.</summary>
    public static string[]? Decompose(TranscodeReason reasons)
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
}
