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
    private readonly object _lock = new();
    private int _consecutiveFailures;
    private long _openedAtMs;

    public CircuitBreaker(int failureThreshold = 3, TimeSpan? openWindow = null)
    {
        _failureThreshold = failureThreshold;
        _openWindow = openWindow ?? TimeSpan.FromMinutes(10);
    }

    public bool IsOpen(long nowMs)
    {
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

    public void RecordSuccess(long nowMs)
    {
        lock (_lock)
        {
            _consecutiveFailures = 0;
            _openedAtMs = 0;
        }
    }

    public void RecordFailure(long nowMs)
    {
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
