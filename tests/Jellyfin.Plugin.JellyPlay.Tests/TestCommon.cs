using System;
using System.IO;
using Jellyfin.Plugin.JellyPlay.Storage;

namespace Jellyfin.Plugin.JellyPlay.Tests;

/// <summary>
/// The one pinnable clock: a <see cref="TimeProvider"/> whose now the test
/// sets and advances explicitly. Every service that stamps sync ops, LWW
/// ceilings, history rows or TTL windows accepts it, so no test observes (or
/// races) the ambient wall clock.
/// </summary>
public sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _utcNow = start;

    /// <summary>Pins to the shared fetcher-test epoch when constructed bare.</summary>
    public FakeTimeProvider() : this(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))
    {
    }

    public void Advance(TimeSpan by) => _utcNow += by;

    public void Set(DateTimeOffset at) => _utcNow = at;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public long NowMs => _utcNow.ToUnixTimeMilliseconds();
}

/// <summary>
/// A tagged temp directory with recursive cleanup — the fixture base for
/// tests that construct (and possibly downgrade) the database per test, like
/// the migration suites.
/// </summary>
public abstract class TempDirFixture : IDisposable
{
    protected readonly string _tempDir;

    protected TempDirFixture(string tag)
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"jellyplay-{tag}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public virtual void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// A tagged temp directory plus one real SQLite store in it — the fixture
/// base every store-backed test copies instead of re-implementing the
/// create/dispose dance. The store is the real interface: no fake database,
/// ever.
/// </summary>
public abstract class TempDatabaseFixture : TempDirFixture
{
    protected readonly JellyPlayDatabase _db;

    protected TempDatabaseFixture(string tag)
        : base(tag)
    {
        _db = new JellyPlayDatabase(_tempDir);
    }

    public override void Dispose()
    {
        _db.Dispose();
        base.Dispose();
    }
}
