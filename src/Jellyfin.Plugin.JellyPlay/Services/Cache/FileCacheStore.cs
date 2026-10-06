using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyPlay.Services.Cache;

/// <summary>
/// TTL file cache under DataDirectory/cache. Values are JSON blobs keyed by
/// cache key; reads are atomic via temp-file rename on write.
///
/// The directory is size-capped: a sweep runs at init and on every Nth write,
/// purging expired entries and then deleting oldest-last-written files until
/// the total is back under the configured cap (Cache:MaxSizeMegabytes, 256 MB
/// default). The same sweep backs the daily "cache maintenance" scheduled task.
/// </summary>
public sealed class FileCacheStore
{
    /// <summary>Cache ceiling when no configuration is reachable (unit tests, early init).</summary>
    public const long DefaultMaxTotalBytes = 256L << 20;

    /// <summary>Hard age ceiling for any entry regardless of the reader-supplied TTL.</summary>
    public static readonly TimeSpan DefaultMaxEntryAge = TimeSpan.FromDays(30);

    private const int SweepEveryNWrites = 64;

    private readonly string _cacheDir;
    private readonly ILogger<FileCacheStore> _logger;
    private int _writeCount;

    public FileCacheStore(ILogger<FileCacheStore> logger, string? cacheDirectory = null)
    {
        _logger = logger;
        _cacheDir = cacheDirectory ?? Path.Combine(JellyPlayPlugin.Instance?.DataDirectory ?? AppContext.BaseDirectory, "cache");
        Directory.CreateDirectory(_cacheDir);
        Sweep(ConfiguredMaxTotalBytes(), DefaultMaxEntryAge);
    }

    public string CacheDirectory => _cacheDir;

    /// <summary>Cap from plugin configuration, with the built-in default as fallback.</summary>
    public static long ConfiguredMaxTotalBytes()
    {
        var megabytes = JellyPlayPlugin.Instance?.Configuration.Cache.MaxSizeMegabytes ?? 256;
        return megabytes <= 0 ? DefaultMaxTotalBytes : (long)megabytes << 20;
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

        // Cheap counter: the directory walk only happens every Nth write.
        if (Interlocked.Increment(ref _writeCount) % SweepEveryNWrites == 0)
        {
            Sweep(ConfiguredMaxTotalBytes(), DefaultMaxEntryAge);
        }
    }

    public void Invalidate(string key) => TryDelete(PathFor(key));

    public sealed record SweepResult(int DeletedFiles, long BytesReclaimed);

    /// <summary>
    /// Walks the cache directory: first purges entries older than
    /// <paramref name="maxEntryAge"/>, then deletes oldest-last-written files
    /// until the total size is within <paramref name="maxTotalBytes"/>.
    /// </summary>
    public SweepResult Sweep(long maxTotalBytes, TimeSpan maxEntryAge)
    {
        var deleted = 0;
        var reclaimed = 0L;
        try
        {
            var candidates = new List<(string Path, long Size, DateTimeOffset Written)>();
            var cutoff = DateTimeOffset.UtcNow - maxEntryAge;
            foreach (var file in Directory.EnumerateFiles(_cacheDir, "*.json"))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.LastWriteTimeUtc < cutoff)
                    {
                        var size = info.Length;
                        File.Delete(file);
                        deleted++;
                        reclaimed += size;
                        continue;
                    }

                    candidates.Add((file, info.Length, info.LastWriteTimeUtc));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Cache sweep could not inspect {Path}", file);
                }
            }

            var total = 0L;
            foreach (var (_, size, _) in candidates)
            {
                total += size;
            }

            if (total > maxTotalBytes)
            {
                candidates.Sort((a, b) => a.Written.CompareTo(b.Written)); // oldest written first
                foreach (var (path, size, _) in candidates)
                {
                    if (total <= maxTotalBytes)
                    {
                        break;
                    }

                    try
                    {
                        File.Delete(path);
                        total -= size;
                        deleted++;
                        reclaimed += size;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Cache sweep could not evict {Path}", path);
                        break; // stuck file should not spin the rest of the sweep
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cache sweep failed");
        }

        if (deleted > 0)
        {
            _logger.LogInformation("Cache sweep removed {Count} entries, reclaimed {Bytes} bytes", deleted, reclaimed);
        }

        return new SweepResult(deleted, reclaimed);
    }

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

