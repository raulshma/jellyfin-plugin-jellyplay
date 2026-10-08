using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Helpers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyPlay.Services.Cache;

/// <summary>Outcome of the collapsed value+miss probe.</summary>
public enum CacheProbeKind
{
    /// <summary>A fresh cached value (carries it).</summary>
    Hit,

    /// <summary>A live miss marker: the caller short-circuits as "no data".</summary>
    MissMarker,

    /// <summary>Nothing cached — the caller runs its fetch.</summary>
    NotFound,
}

/// <summary>The collapsed probe's result: the kind plus the value on a hit.</summary>
public readonly record struct CacheProbe<T>(CacheProbeKind Kind, T? Value = default);

/// <summary>
/// TTL file cache under DataDirectory/cache. Values are JSON blobs keyed by
/// cache key; reads are atomic via temp-file rename on write.
///
/// A small in-memory layer sits ahead of the files: hot keys skip the
/// read+deserialize for a few seconds. Within that memory window the memoized
/// entry is authoritative for its own age (the write time captured when the
/// entry was memoized) — no metadata stat per hit — so a hot value is at most
/// one memory window stale; the file's own write time returns to being the
/// one expiry truth on the cold path, so backdating, invalidation and sweep
/// evictions apply as soon as the memo lapses. The file store remains the
/// durable truth.
///
/// The directory is size-capped: a sweep runs in the background at init and —
/// off the request path, fire-and-forget — on every Nth write, purging expired
/// entries and then deleting oldest-last-written files until the total is back
/// under the configured cap (Cache:MaxSizeMegabytes, 256 MB default). The same
/// sweep backs the daily "cache maintenance" scheduled task.
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

    /// <summary>Soft memory TTL for the memoized deserialized values (the entry's captured write time still gates every hit).</summary>
    private static readonly TimeSpan MemoryTtl = TimeSpan.FromSeconds(30);

    /// <summary>Memory-table cap: at the cap the oldest-inserted quarter is evicted (a wholesale clear would stampede every hot key back to the files).</summary>
    private const int MemoryCap = 512;

    /// <summary>Path-memo ceiling: it caches a pure hash, so a full table simply drops new entries.</summary>
    private const int PathMemoCap = 2048;

    private readonly string _cacheDir;
    private readonly Func<int> _maxSizeMegabytes;
    private readonly ILogger<FileCacheStore> _logger;
    private readonly ConcurrentDictionary<string, MemoryEntry> _memory = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _pathMemo = new(StringComparer.Ordinal);
    private int _writeCount;
    private long _lastBackgroundSweepMs;

    /// <summary>DI/test constructor: explicit cache directory and size-cap source (ADR-0002 — no plugin-singleton fallbacks; the composition root passes both). Construction only creates the directory; the init sweep runs in the background.</summary>
    public FileCacheStore(ILogger<FileCacheStore> logger, string cacheDirectory, Func<int> maxSizeMegabytes)
    {
        _logger = logger;
        _cacheDir = cacheDirectory;
        _maxSizeMegabytes = maxSizeMegabytes;
        Directory.CreateDirectory(_cacheDir);
        _ = Task.Run(() =>
        {
            try
            {
                Sweep(MaxTotalBytes(), DefaultMaxEntryAge);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Startup cache sweep failed");
            }
        });
    }

    public string CacheDirectory => _cacheDir;

    /// <summary>Cap in bytes from the configured megabyte value, with the built-in default as fallback.</summary>
    public long MaxTotalBytes()
    {
        var megabytes = _maxSizeMegabytes();
        return megabytes <= 0 ? DefaultMaxTotalBytes : (long)megabytes << 20;
    }

    public async Task<T?> GetAsync<T>(string key, TimeSpan maxAge)
    {
        var path = PathFor(key);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (_memory.TryGetValue(key, out var hit) && now - hit.LoadedUtc <= MemoryTtl)
            {
                // Hot path: the entry is authoritative for its own window —
                // no file stat per read; the captured write time decides age.
                if (now - hit.WrittenUtc <= maxAge)
                {
                    return (T)hit.Value!;
                }

                _memory.TryRemove(key, out _);
                TryDelete(path);
                return default;
            }

            _memory.TryRemove(key, out _); // a memo past its window lapses; the file is the truth again

            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists)
            {
                return default;
            }

            if (now - fileInfo.LastWriteTimeUtc > maxAge)
            {
                TryDelete(path);
                return default;
            }

            var entry = JsonSerializer.Deserialize<CachedEntry<T>>(await File.ReadAllTextAsync(path).ConfigureAwait(false));
            var value = entry is null ? default : entry.Value;
            if (value is not null)
            {
                RememberHit(key, value, fileInfo.LastWriteTimeUtc);
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

    /// <summary>
    /// The fetcher's one-probe seam: probes the value key and — when a miss
    /// key is given — the miss marker in a single call, so an uncached request
    /// resolves each path once instead of the fetcher probing the two keys
    /// separately. TTL semantics are exactly the two reads it replaces.
    /// </summary>
    public async Task<CacheProbe<T>> TryGetAsync<T>(string key, string? missKey, TimeSpan maxAge, TimeSpan missMaxAge)
    {
        var value = await GetAsync<T>(key, maxAge).ConfigureAwait(false);
        if (value is not null)
        {
            return new CacheProbe<T>(CacheProbeKind.Hit, value);
        }

        if (missKey is not null && await GetAsync<bool>(missKey, missMaxAge).ConfigureAwait(false))
        {
            return new CacheProbe<T>(CacheProbeKind.MissMarker);
        }

        return new CacheProbe<T>(CacheProbeKind.NotFound);
    }

    public async Task SetAsync<T>(string key, T value)
    {
        var path = PathFor(key);
        try
        {
            var payload = JsonSerializer.Serialize(new CachedEntry<T> { Value = value });
            var temp = path + ".tmp" + Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(temp, payload).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
            if (value is not null)
            {
                RememberHit(key, value, DateTimeOffset.UtcNow);
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

    /// <summary>Memoizes one deserialized value for the soft memory TTL (bounded by the partial-eviction cap).</summary>
    private void RememberHit<T>(string key, T value, DateTimeOffset writtenUtc)
    {
        if (_memory.Count >= MemoryCap)
        {
            var toEvict = Math.Max(MemoryCap / 4, 1);
            foreach (var oldest in _memory.ToArray().OrderBy(entry => entry.Value.LoadedUtc).Take(toEvict))
            {
                _memory.TryRemove(oldest.Key, out _);
            }
        }

        _memory[key] = new MemoryEntry { LoadedUtc = DateTimeOffset.UtcNow, WrittenUtc = writtenUtc, Value = value };
    }

    private string PathFor(string key)
    {
        if (_pathMemo.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var safe = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
        var path = Path.Combine(_cacheDir, safe + ".json");
        if (_pathMemo.Count < PathMemoCap)
        {
            _pathMemo.TryAdd(key, path);
        }

        return path;
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

    /// <summary>One memoized deserialized cache value; <see cref="WrittenUtc"/> is the file's write time captured at memoization and stays the age truth for the memory window.</summary>
    private sealed class MemoryEntry
    {
        public DateTimeOffset LoadedUtc { get; init; }

        public DateTimeOffset WrittenUtc { get; init; }

        public object? Value { get; init; }
    }

    private sealed class CachedEntry<T>
    {
        public T Value { get; set; } = default!;
    }
}
