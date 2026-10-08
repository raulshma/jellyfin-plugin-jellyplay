namespace Jellyfin.Plugin.JellyPlay.Services.Fetching;

/// <summary>
/// Circuit breaker per external source: after N consecutive failures the source
/// is skipped for an open window, then allowed one trial call. Prevents one
/// dead upstream from stalling request paths.
/// </summary>
public sealed class CircuitBreaker
{
    private readonly int _failureThreshold;
    private readonly TimeSpan _openWindow;
    private readonly TimeProvider _clock;
    private readonly object _lock = new();
    private int _consecutiveFailures;
    private long _openedAtMs;

    public CircuitBreaker(int failureThreshold = 3, TimeSpan? openWindow = null, TimeProvider? clock = null)
    {
        _failureThreshold = failureThreshold;
        _openWindow = openWindow ?? TimeSpan.FromMinutes(10);
        _clock = clock ?? TimeProvider.System;
    }

    private long NowMs => _clock.GetUtcNow().ToUnixTimeMilliseconds();

    public bool IsOpen()
    {
        // Fast path for the common closed answer: a coherent read below the
        // threshold decides without the lock. The transition race is benign —
        // the locked path below stays the only writer of state changes.
        if (Volatile.Read(ref _consecutiveFailures) < _failureThreshold)
        {
            return false;
        }

        var nowMs = NowMs;
        lock (_lock)
        {
            if (_consecutiveFailures < _failureThreshold)
            {
                return false;
            }

            if (nowMs - _openedAtMs >= (long)_openWindow.TotalMilliseconds)
            {
                _consecutiveFailures = _failureThreshold - 1;
                return false;
            }

            return true;
        }
    }

    public void RecordSuccess()
    {
        lock (_lock)
        {
            _consecutiveFailures = 0;
            _openedAtMs = 0;
        }
    }

    public void RecordFailure()
    {
        var nowMs = NowMs;
        lock (_lock)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= _failureThreshold)
            {
                _openedAtMs = nowMs;
            }
        }
    }
}
