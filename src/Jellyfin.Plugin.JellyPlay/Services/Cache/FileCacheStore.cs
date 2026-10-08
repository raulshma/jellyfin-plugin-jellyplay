using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Helpers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyPlay.Services.Cache;

/// <summary>
/// TTL file cache under DataDirectory/cache. Values are JSON blobs keyed by
/// cache key; reads are atomic via temp-file rename on write.
///
/// A small in-memory layer sits ahead of the files: hot keys skip the
/// read+deserialize for a few seconds, while the file's own write time stays
/// the one expiry truth — every memory hit re-validates it (a metadata stat,
/// not a data read), so backdating, invalidation and sweep evictions are
/// never served stale. The file store remains the durable truth.
///
/// The directory is size-capped: a sweep runs at init and — off the request
/// path, fire-and-forget — on every Nth write, purging expired entries and
/// then deleting oldest-last-written files until the total is back under the
/// configured cap (Cache:MaxSizeMegabytes, 256 MB default). The same sweep
/// backs the daily "cache maintenance" scheduled task.
/// </summary>
public sealed class FileCacheStore
{
    /// <summary>Cache ceiling when the configured cap is not positive.</summary>
    public const long DefaultMaxTotalBytes = 256L << 20;

    /// <summary>Hard age ceiling for any entry regardless of the reader-supplied TTL.</summary>
    public static readonly TimeSpan DefaultMaxEntryAge = TimeSpan.FromDays(30);

    private const int SweepEveryNWrites = 64;

    /// <summary>Minimum spacing between background sweeps, so a write burst collapses into one pass.</summary>
    private const long BackgroundSweepWindowMs = 60_000;

    /// <summary>Soft memory TTL for the memoized deserialized values (the file timestamp still gates every hit).</summary>
    private static readonly TimeSpan MemoryTtl = TimeSpan.FromSeconds(30);

    /// <summary>Memory-table cap: a full table is dropped wholesale and self-reloads from the files on the next read.</summary>
    private const int MemoryCap = 512;

    private readonly string _cacheDir;
    private readonly Func<int> _maxSizeMegabytes;
    private readonly ILogger<FileCacheStore> _logger;
    private readonly ConcurrentDictionary<string, MemoryEntry> _memory = new(StringComparer.Ordinal);
    private int _writeCount;
    private long _lastBackgroundSweepMs;

    /// <summary>DI/test constructor: explicit cache directory and size-cap source (ADR-0002 — no plugin-singleton fallbacks; the composition root passes both).</summary>
    public FileCacheStore(ILogger<FileCacheStore> logger, string cacheDirectory, Func<int> maxSizeMegabytes)
    {
        _logger = logger;
        _cacheDir = cacheDirectory;
        _maxSizeMegabytes = maxSizeMegabytes;
        Directory.CreateDirectory(_cacheDir);
        Sweep(MaxTotalBytes(), DefaultMaxEntryAge);
    }

    public string CacheDirectory => _cacheDir;

    /// <summary>Cap in bytes from the configured megabyte value, with the built-in default as fallback.</summary>
    public long MaxTotalBytes()
    {
        var megabytes = _maxSizeMegabytes();
        return megabytes <= 0 ? DefaultMaxTotalBytes : (long)megabytes << 20;
    }

    public T? Get<T>(string key, TimeSpan maxAge)
    {
        var path = PathFor(key);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (_memory.TryGetValue(key, out var hit) && now - hit.LoadedUtc <= MemoryTtl)
            {
                var memoInfo = new FileInfo(path);
                if (memoInfo.Exists && now - memoInfo.LastWriteTimeUtc <= maxAge)
                {
                    return (T)hit.Value!;
                }

                _memory.TryRemove(key, out _);
                if (!memoInfo.Exists)
                {
                    return default;
                }

                File.Delete(path);
                return default;
            }

            _memory.TryRemove(key, out _);

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
            var value = entry is null ? default : entry.Value;
            if (value is not null)
            {
                RememberHit(key, value);
            }

            return value;
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
            var payload = JsonSerializer.Serialize(new CachedEntry<T> { Value = value });
            var temp = path + ".tmp" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, payload);
            File.Move(temp, path, overwrite: true);
            if (value is not null)
            {
                RememberHit(key, value);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cache write failed for {Key}", key);
        }

        // Cheap counter: the directory walk only happens every Nth write —
        // and never on the request path (see MaybeSweepInBackground).
        if (Interlocked.Increment(ref _writeCount) % SweepEveryNWrites == 0)
        {
            MaybeSweepInBackground();
        }
    }

    public void Invalidate(string key)
    {
        _memory.TryRemove(key, out _);
        TryDelete(PathFor(key));
    }

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

    /// <summary>
    /// The periodic sweep is never on the request path: every Nth write spawns
    /// it fire-and-forget with full exception capture (a sweep must never
    /// throw into the writer), cadence-gated via the shared <see cref="SweepGate"/>
    /// so bursts of writes collapse into one background pass. The daily
    /// CacheMaintenanceTask still runs it in the foreground.
    /// </summary>
    private void MaybeSweepInBackground()
    {
        if (!SweepGate.Enter(ref _lastBackgroundSweepMs, Environment.TickCount64, BackgroundSweepWindowMs))
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                Sweep(MaxTotalBytes(), DefaultMaxEntryAge);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Background cache sweep failed");
            }
        });
    }

    /// <summary>Memoizes one deserialized value for the soft memory TTL (bounded by the wholesale cap).</summary>
    private void RememberHit<T>(string key, T value)
    {
        if (_memory.Count >= MemoryCap)
        {
            _memory.Clear();
        }

        _memory[key] = new MemoryEntry { LoadedUtc = DateTimeOffset.UtcNow, Value = value };
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

    /// <summary>One memoized deserialized cache value; the file's write time stays the expiry truth, this only bounds re-load frequency.</summary>
    private sealed class MemoryEntry
    {
        public DateTimeOffset LoadedUtc { get; init; }

        public object? Value { get; init; }
    }

    private sealed class CachedEntry<T>
    {
        public T Value { get; set; } = default!;
    }
}
