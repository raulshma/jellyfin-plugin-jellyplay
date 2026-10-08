using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Jellyfin.Plugin.JellyPlay.Helpers;

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

    /// <summary>Occasional O(n) sweep so abandoned keys do not accumulate forever (the shared <see cref="SweepGate"/>).</summary>
    private void MaybeSweep(long nowMs)
    {
        if (!SweepGate.Enter(ref _lastSweepMs, nowMs, _windowMs))
        {
            return;
        }

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

    /// <summary>Settings batch POST: 30/min per user (a debounce-cycle burst must survive).</summary>
    public static RateLimiter Settings() => new(limit: 30, windowMs: 60_000);

    /// <summary>Admin broadcast: 10/min per admin.</summary>
    public static RateLimiter Broadcast() => new(limit: 10, windowMs: 60_000);

    /// <summary>Anonymous Seerr webhook intake: 30/min per remote client.</summary>
    public static RateLimiter Webhook() => new(limit: 30, windowMs: 60_000);

    /// <summary>
    /// POST admin/pushDefaults: 5/min per admin — the route rewrites every user's
    /// base settings in one call, so the abuse budget is the tightest of the
    /// mutating routes.
    /// </summary>
    public static RateLimiter PushDefaults() => new(limit: 5, windowMs: 60_000);
}

/// <summary>
/// Which abuse-containment sliding-window a mutating route draws from. One enum
/// behind the single <see cref="RateLimiterRegistry"/> module — replacing the
/// four one-line subclasses with one seam (one adapter = hypothetical seam,
/// two = real; four single-use subclasses were four hypothetical seams).
/// </summary>
public enum RateLimiterKind
{
    Settings,
    Broadcast,
    Webhook,
    PushDefaults,
}

/// <summary>
/// The one abuse-containment module: owns the four mutating-route
/// sliding-windows behind a small interface, so controllers name a kind
/// instead of a type.
/// </summary>
public sealed class RateLimiterRegistry
{
    private readonly RateLimiter _settings = RateLimiter.Settings();
    private readonly RateLimiter _broadcast = RateLimiter.Broadcast();
    private readonly RateLimiter _webhook = RateLimiter.Webhook();
    private readonly RateLimiter _pushDefaults = RateLimiter.PushDefaults();

    public RateLimiter Get(RateLimiterKind kind) => kind switch
    {
        RateLimiterKind.Settings => _settings,
        RateLimiterKind.Broadcast => _broadcast,
        RateLimiterKind.Webhook => _webhook,
        RateLimiterKind.PushDefaults => _pushDefaults,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown rate-limiter kind."),
    };
}
