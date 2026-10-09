using System;
using System.IO;
using Jellyfin.Plugin.JellyPlay.Services.Admin;
using Jellyfin.Plugin.JellyPlay.Storage;
using Xunit;

namespace Jellyfin.Plugin.JellyPlay.Tests;

public class RateLimiterTests
{
    [Fact]
    public void AllowsUpToLimit_ThenBlocks_ThenRecoversAfterWindow()
    {
        var limiter = new RateLimiter(limit: 3, windowMs: 1_000);

        Assert.True(limiter.Allow("u1", 0));
        Assert.True(limiter.Allow("u1", 10));
        Assert.True(limiter.Allow("u1", 20));
        Assert.False(limiter.Allow("u1", 30)); // limit reached inside window
        Assert.False(limiter.Allow("u1", 999));
        Assert.True(limiter.Allow("u1", 1_001)); // first hit aged out
    }

    [Fact]
    public void KeysAreIndependent()
    {
        var limiter = new RateLimiter(limit: 1, windowMs: 60_000);

        Assert.True(limiter.Allow("a", 0));
        Assert.False(limiter.Allow("a", 1));
        Assert.True(limiter.Allow("b", 2));
    }

    [Fact]
    public void SweepDropsAgedKeys()
    {
        var limiter = new RateLimiter(limit: 1, windowMs: 100);

        Assert.True(limiter.Allow("gone", 0));
        limiter.Allow("sweeper", 200); // triggers sweep past the window
        limiter.Allow("sweeper2", 500);
        Assert.True(limiter.Allow("gone", 600)); // key was swept; fresh budget
    }
}

public class DatabaseIntegrityTests : TempDatabaseFixture
{

    public DatabaseIntegrityTests()
        : base("integrity")
    {
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void IntegrityCheck_ReportsOk_OnHealthyDatabase()
    {
        var result = _db.CheckIntegrity();

        Assert.True(result.IntegrityOk);
        Assert.Contains("ok", result.Details, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IntegrityCheck_SurvivesWrites()
    {
        _db.UpsertSettings(
            "u1",
            JellyPlayDatabase.BaseProfile,
            new[] { new Jellyfin.Plugin.JellyPlay.Storage.Models.SettingWrite("ns", "k", 1, 1, "d", new byte[] { 1 }) },
            new JellyPlayDatabase.Quotas(1024, 4096, 10));

        Assert.True(_db.CheckIntegrity().IntegrityOk);
    }
}
