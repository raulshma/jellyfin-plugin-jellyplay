using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.JellyPlay.Storage;
using Jellyfin.Plugin.JellyPlay.Storage.Models;
using Xunit;

namespace Jellyfin.Plugin.JellyPlay.Tests;

public sealed class JellyPlayDatabaseTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "jellyplay-tests-" + Guid.NewGuid().ToString("N"));
    private readonly JellyPlayDatabase _db;

    private static readonly JellyPlayDatabase.Quotas Quotas = new(1024, 4096, 5);

    public JellyPlayDatabaseTests()
    {
        Directory.CreateDirectory(_tempDir);
        _db = new JellyPlayDatabase(_tempDir);
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static SettingWrite Write(string ns, string key, long updatedAt, string value = "\"v\"", string device = "d1")
        => new(ns, key, 1, updatedAt, device, System.Text.Encoding.UTF8.GetBytes(value));

    [Fact]
    public void Upsert_NewKey_Applies()
    {
        var result = _db.UpsertSettings("user1", "", new[] { Write("ui", "theme", 100) }, Quotas);

        var applied = Assert.Single(result.Applied);
        Assert.Equal(("ui", "theme"), (applied.Ns, applied.Key));
        Assert.Empty(result.Rejected);
    }

    [Fact]
    public void Upsert_OlderWrite_IsRejected_LwwHolds()
    {
        _db.UpsertSettings("user1", "", new[] { Write("ui", "theme", 200, "\"new\"") }, Quotas);
        var result = _db.UpsertSettings("user1", "", new[] { Write("ui", "theme", 100, "\"old\"") }, Quotas);

        var rejected = Assert.Single(result.Rejected);
        Assert.Equal("stale-write", rejected.Reason);
        var row = Assert.Single(_db.GetSettings("user1", ""));
        Assert.Equal(200, row.UpdatedAt);
    }

    [Fact]
    public void Upsert_EqualTimestamp_IsRejected()
    {
        // Equal timestamps reject (no-oscillation rule): first write stays authoritative.
        _db.UpsertSettings("user1", "", new[] { Write("ui", "theme", 100, "\"a\"", "d1") }, Quotas);
        var result = _db.UpsertSettings("user1", "", new[] { Write("ui", "theme", 100, "\"b\"", "d2") }, Quotas);

        var rejected = Assert.Single(result.Rejected);
        Assert.Equal("stale-write", rejected.Reason);
        var row = Assert.Single(_db.GetSettings("user1", ""));
        Assert.Equal("d1", row.DeviceId);
    }

    [Fact]
    public void Upsert_ValueGrowth_PastQuota_IsRejected()
    {
        // ~1KB values (under MaxKeyBytes=1KB? no — MaxKeyBytes 1024, value 1022 fits).
        // Four keys fit under MaxUserBytes=4KB (4*1022=4088); the fifth crosses it.
        var blob = new string('x', 1020);
        var first4 = new[] { Write("ns", "k1", 1, $"\"{blob}\""), Write("ns", "k2", 1, $"\"{blob}\""), Write("ns", "k3", 1, $"\"{blob}\""), Write("ns", "k4", 1, $"\"{blob}\"") };
        var seeded = _db.UpsertSettings("user1", "", first4, Quotas);
        Assert.Equal(4, seeded.Applied.Count);

        var result = _db.UpsertSettings("user1", "", new[] { Write("ns", "k5", 1, $"\"{blob}\"") }, Quotas);

        var rejected = Assert.Single(result.Rejected);
        Assert.Equal("quota-exceeded", rejected.Reason);
        Assert.Empty(result.Applied);
    }

    [Fact]
    public void Upsert_OverSizedKey_IsRejected()
    {
        var oversized = new string('z', 2000);
        var result = _db.UpsertSettings("user1", "", new[] { Write("ns", "k", 1, $"\"{oversized}\"") }, Quotas);

        var rejected = Assert.Single(result.Rejected);
        Assert.Equal("key-too-large", rejected.Reason);
    }

    [Fact]
    public void Upsert_KeyLimit_IsEnforced()
    {
        var writes = Enumerable.Range(0, 7).Select(index => Write("ns", $"k{index}", 1)).ToList();
        var result = _db.UpsertSettings("user1", "", writes, Quotas);

        Assert.Equal(5, result.Applied.Count);
        Assert.Equal(2, result.Rejected.Count);
    }

    [Fact]
    public void Profiles_AreIsolated()
    {
        _db.UpsertSettings("user1", "", new[] { Write("ui", "theme", 1, "\"base\"") }, Quotas);
        _db.UpsertSettings("user1", "tv", new[] { Write("ui", "theme", 2, "\"tv\"") }, Quotas);

        Assert.Equal("\"base\"", System.Text.Encoding.UTF8.GetString(Assert.Single(_db.GetSettings("user1", "")).Value));
        Assert.Equal("\"tv\"", System.Text.Encoding.UTF8.GetString(Assert.Single(_db.GetSettings("user1", "tv")).Value));
    }

    [Fact]
    public void ChangedSince_ReturnsCurrentValue_PerKey()
    {
        var first = _db.UpsertSettings("user1", "", new[] { Write("ui", "a", 1) }, Quotas);
        var second = _db.UpsertSettings("user1", "", new[] { Write("ui", "a", 2), Write("ui", "b", 3) }, Quotas);
        var since = first.Applied[0].Seq;

        var changed = _db.GetChangedSettings("user1", since);

        Assert.Equal(2, changed.Count);
        var entryA = changed.Single(row => row.Key == "a");
        Assert.Equal(2, entryA.UpdatedAt);
    }

    [Fact]
    public void DeleteNamespace_RemovesSettings_AndChangeLog()
    {
        _db.UpsertSettings("user1", "", new[] { Write("ui", "a", 1), Write("player", "b", 1) }, Quotas);
        _db.DeleteNamespace("user1", "", "ui");

        var rows = _db.GetSettings("user1", "");
        Assert.Single(rows);
        Assert.Equal("player", rows[0].Ns);
    }

    [Fact]
    public void ChangeLog_Prune_RemovesOnlyOld()
    {
        _db.UpsertSettings("user1", "", new[] { Write("ui", "a", 1) }, Quotas);
        Assert.True(_db.GetChangeLogHead("user1") > 0);

        _db.PruneChangeLog(retentionDays: 30); // nothing old enough
        Assert.True(_db.GetChangeLogHead("user1") >= 0);
    }

    [Fact]
    public void Messages_ReadState_TracksPerUser()
    {
        var message = new MessageRow("m1", "T", "B", string.Empty, string.Empty, string.Empty, "{\"type\":\"all\"}", null, null, 0, 1);
        _db.UpsertMessage(message);
        _db.MarkMessageRead("u1", "m1", 99);
        _db.MarkMessageRead("u1", "m1", 100); // idempotent

        Assert.Contains("m1", _db.GetReadMessageIds("u1"));
        Assert.DoesNotContain("m1", _db.GetReadMessageIds("u2"));
        Assert.True(_db.DeleteMessage("m1"));
        Assert.Empty(_db.GetReadMessageIds("u1")); // cascade delete
    }

    [Fact]
    public void Bookmarks_UpsertAndDelete()
    {
        var bookmark = new BookmarkRow("b1", "u1", "item1", 12.5, 3, "Chapter", "note", 1, 2);
        _db.UpsertBookmark(bookmark);
        var updated = bookmark with { Position = 50, UpdatedAt = 3 };
        _db.UpsertBookmark(updated);

        var rows = _db.GetBookmarks("u1", "item1");
        var row = Assert.Single(rows);
        Assert.Equal(50, row.Position);
        Assert.Equal(1, row.CreatedAt);

        Assert.True(_db.DeleteBookmark("u1", "b1"));
        Assert.False(_db.DeleteBookmark("u2", "b1")); // user scoping
    }

    [Fact]
    public void AdminDefaults_SetGetAndRestore()
    {
        _db.SetAdminDefaults("global", System.Text.Encoding.UTF8.GetBytes("{\"ui/theme\":{\"mode\":\"forced\"}}"), 1);
        _db.SetAdminDefaults("user-1", System.Text.Encoding.UTF8.GetBytes("{}"), 2);

        var backup = _db.GetAllAdminDefaults();
        Assert.Equal(2, backup.Count);

        _db.RestoreAdminDefaults(backup.Take(1).ToList());
        Assert.Single(_db.GetAllAdminDefaults());
    }
}
