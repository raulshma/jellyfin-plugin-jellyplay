using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyPlay.Services.Admin;

/// <summary>
/// Per-key sliding-window rate limiter guarding the plugin's expensive
/// mutating routes (settings batch POST, broadcast, webhook intake). Fixed
/// window per key — precise enough for abuse containment, allocation-free in
/// the common case.
/// </summary>
public class RateLimiter
{
    private readonly ConcurrentDictionary<string, Queue<long>> _hits = new(StringComparer.Ordinal);
    private readonly int _limit;
    private readonly long _windowMs;
    private long _lastSweepMs;

    public RateLimiter(int limit, long windowMs)
    {
        _limit = limit;
        _windowMs = windowMs;
    }

    /// <summary>True when the call is allowed (and recorded); false = rate-limited.</summary>
    public bool Allow(string key, long nowMs)
    {
        var window = _hits.GetOrAdd(key, _ => new Queue<long>());
        lock (window)
        {
            while (window.Count > 0 && nowMs - window.Peek() >= _windowMs)
            {
                window.Dequeue();
            }

            if (window.Count >= _limit)
            {
                return false;
            }

            window.Enqueue(nowMs);
        }

        MaybeSweep(nowMs);
        return true;
    }

    /// <summary>Occasional O(n) sweep so abandoned keys do not accumulate forever.</summary>
    private void MaybeSweep(long nowMs)
    {
        if (nowMs - System.Threading.Interlocked.Read(ref _lastSweepMs) < _windowMs)
        {
            return;
        }

        System.Threading.Interlocked.Exchange(ref _lastSweepMs, nowMs);
        foreach (var (key, window) in _hits)
        {
            lock (window)
            {
                if (window.Count == 0 || nowMs - window.Peek() >= _windowMs)
                {
                    _hits.TryRemove(key, out _);
                }
            }
        }
    }
}

/// <summary>Settings batch POST: 30/min per user (a debounce-cycle burst must survive).</summary>
public sealed class SettingsRateLimiter : RateLimiter
{
    public SettingsRateLimiter() : base(limit: 30, windowMs: 60_000)
    {
    }
}

/// <summary>Admin broadcast: 10/min per admin.</summary>
public sealed class BroadcastRateLimiter : RateLimiter
{
    public BroadcastRateLimiter() : base(limit: 10, windowMs: 60_000)
    {
    }
}
