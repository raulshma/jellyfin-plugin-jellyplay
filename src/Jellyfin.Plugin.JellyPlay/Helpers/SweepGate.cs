using System.Threading;

namespace Jellyfin.Plugin.JellyPlay.Helpers;

/// <summary>
/// The occasional-sweep gate shared by the caches that own one (the rate
/// limiter's abandoned keys, the Seerr validation cache's abandoned users):
/// a cheap Interlocked check usually lets a single caller past per window —
/// the check-then-exchange is not atomic, so a racing caller can slip through
/// alongside the winner and run a benign duplicate sweep. Correctness never
/// depends on exclusivity: a sweep is idempotent, only occasionally O(n).
/// </summary>
internal static class SweepGate
{
    /// <summary>Usually true for one caller per <paramref name="windowMs"/> (the caller that passes also advances the gate to <paramref name="nowMs"/>); false otherwise. Not atomic — a racing caller may also get true and sweep twice.</summary>
    public static bool Enter(ref long lastSweepMs, long nowMs, long windowMs)
    {
        if (nowMs - Interlocked.Read(ref lastSweepMs) < windowMs)
        {
            return false;
        }

        Interlocked.Exchange(ref lastSweepMs, nowMs);
        return true;
    }
}
