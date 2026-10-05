using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyPlay.Services.Cache;

/// <summary>
/// TTL file cache under DataDirectory/cache. Values are JSON blobs keyed by
/// cache key; reads are atomic via temp-file rename on write.
/// </summary>
public sealed class FileCacheStore
{
    private readonly string _cacheDir;
    private readonly ILogger<FileCacheStore> _logger;

    public FileCacheStore(ILogger<FileCacheStore> logger)
    {
        _logger = logger;
        _cacheDir = Path.Combine(JellyPlayPlugin.Instance?.DataDirectory ?? AppContext.BaseDirectory, "cache");
        Directory.CreateDirectory(_cacheDir);
    }

    public T? Get<T>(string key, TimeSpan maxAge)
    {
        var path = PathFor(key);
        try
        {
            if (!File.Exists(path))
            {
                return default;
            }

            var fileInfo = new FileInfo(path);
            if (DateTimeOffset.UtcNow - fileInfo.LastWriteTimeUtc > maxAge)
            {
                File.Delete(path);
                return default;
            }

            var entry = JsonSerializer.Deserialize<CachedEntry<T>>(File.ReadAllText(path));
            return entry is null ? default : entry.Value;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cache read failed for {Key}; treating as miss", key);
            TryDelete(path);
            return default;
        }
    }

    public void Set<T>(string key, T value)
    {
        var path = PathFor(key);
        try
        {
            var payload = JsonSerializer.Serialize(new CachedEntry<T> { Value = value, StoredAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
            var temp = path + ".tmp" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, payload);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cache write failed for {Key}", key);
        }
    }

    public void Invalidate(string key) => TryDelete(PathFor(key));

    private string PathFor(string key)
    {
        var safe = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
        return Path.Combine(_cacheDir, safe + ".json");
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cache delete failed for {Path}", path);
        }
    }

    private sealed class CachedEntry<T>
    {
        public T Value { get; set; } = default!;
        public long StoredAt { get; set; }
    }
}

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
