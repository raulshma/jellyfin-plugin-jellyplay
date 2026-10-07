using System;

namespace Jellyfin.Plugin.JellyPlay.Helpers;

/// <summary>
/// The shared page-size idiom of the reporting surfaces ("?limit=" defaults
/// when absent/0, clamps 1..max): one helper so the semantics stay identical
/// at every site.
/// </summary>
public static class RequestLimits
{
    public static int Clamp(int value, int @default, int max)
        => Math.Clamp(value == 0 ? @default : value, 1, max);
}
