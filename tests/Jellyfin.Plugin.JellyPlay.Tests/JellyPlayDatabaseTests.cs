using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.JellyPlay.Storage;
using Jellyfin.Plugin.JellyPlay.Storage.Models;
using Xunit;

namespace Jellyfin.Plugin.JellyPlay.Tests;

public sealed class JellyPlayDatabaseTests : TempDatabaseFixture
{

    private static readonly JellyPlayDatabase.Quotas Quotas = new(1024, 4096, 5);

    public JellyPlayDatabaseTests()
        : base("tests")
    {
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

        var changed = _db.GetChangedSettings("user1", since, JellyPlayDatabase.BaseProfile, offset: 0, limit: 100);

        Assert.Equal(2, changed.Count);
        var entryA = changed.Single(row => row.Key == "a");
        Assert.Equal(2, entryA.UpdatedAt);
    }

    [Fact]
    public void DeleteNamespace_Tombstones_SettingsAndKeepsChangeLog()
    {
        _db.UpsertSettings("user1", "", new[] { Write("ui", "a", 1), Write("player", "b", 1) }, Quotas);
        var headBefore = _db.GetChangeLogHead("user1");

        var deleted = _db.DeleteNamespace("user1", "", "ui", tombstoneAt: 500);

        // The rows are gone, but the change log now carries a tombstone per
        // deleted key (op 'del') so deltas can carry the deletion onward.
        Assert.Equal(1, deleted);
        var rows = _db.GetSettings("user1", "");
        Assert.Single(rows);
        Assert.Equal("player", rows[0].Ns);

        Assert.True(_db.GetChangeLogHead("user1") > headBefore);
        Assert.Empty(_db.GetChangedSettings("user1", headBefore, JellyPlayDatabase.BaseProfile, offset: 0, limit: 100));
        var deletedKeys = Assert.Single(_db.GetDeletedSettings("user1", headBefore, JellyPlayDatabase.BaseProfile));
        Assert.Equal(("ui", "a"), (deletedKeys.Ns, deletedKeys.Key));
    }

    [Fact]
    public void Upsert_Tombstone_RemovesRow_AndAppendsDelLog()
    {
        _db.UpsertSettings("user1", "", new[] { Write("ui", "a", 1, "\"v\"") }, Quotas);

        var result = _db.UpsertSettings("user1", "", new[] { new SettingWrite("ui", "a", 1, 5, "d2", Array.Empty<byte>(), IsDelete: true) }, Quotas);

        var applied = Assert.Single(result.Applied);
        Assert.True(applied.Deleted);
        Assert.Empty(_db.GetSettings("user1", ""));
    }

    [Fact]
    public void Upsert_OlderPut_AfterTombstone_IsRejected()
    {
        // Anti-resurrection: once a tombstone is recorded, a put with an older
        // (or equal) timestamp must not recreate the key.
        _db.UpsertSettings("user1", "", new[] { Write("ui", "a", 10, "\"v\"") }, Quotas);
        _db.UpsertSettings("user1", "", new[] { new SettingWrite("ui", "a", 1, 20, "d2", Array.Empty<byte>(), IsDelete: true) }, Quotas);

        var resurrect = _db.UpsertSettings("user1", "", new[] { Write("ui", "a", 15, "\"zombie\"") }, Quotas);
        Assert.Equal("stale-write", Assert.Single(resurrect.Rejected).Reason);
        var equalTs = _db.UpsertSettings("user1", "", new[] { Write("ui", "a", 20, "\"zombie\"") }, Quotas);
        Assert.Equal("stale-write", Assert.Single(equalTs.Rejected).Reason);

        // A strictly newer put legitimately wins over the tombstone (LWW).
        var newer = _db.UpsertSettings("user1", "", new[] { Write("ui", "a", 21, "\"fresh\"") }, Quotas);
        Assert.Single(newer.Applied);
        Assert.Equal("\"fresh\"", System.Text.Encoding.UTF8.GetString(Assert.Single(_db.GetSettings("user1", "")).Value));
    }

    [Fact]
    public void Upsert_DeleteOfAbsentKey_StillRecordsTombstone_WhenNewer()
    {
        // A device that never saw the key deletes it anyway: the tombstone is
        // recorded (newer than nothing) so peers holding a stale copy learn
        // about the deletion; a second identical delete is stale.
        var first = _db.UpsertSettings("user1", "", new[] { new SettingWrite("ui", "ghost", 1, 5, "d2", Array.Empty<byte>(), IsDelete: true) }, Quotas);
        Assert.True(Assert.Single(first.Applied).Deleted);

        var second = _db.UpsertSettings("user1", "", new[] { new SettingWrite("ui", "ghost", 1, 5, "d2", Array.Empty<byte>(), IsDelete: true) }, Quotas);
        Assert.Equal("stale-write", Assert.Single(second.Rejected).Reason);
    }

    [Fact]
    public void Upsert_NamespaceQuota_IsEnforced()
    {
        // prefs caps at 12 bytes: two 7-byte values fit only once.
        var quotas = new JellyPlayDatabase.Quotas(1024, 4096, 50, new Dictionary<string, int> { ["prefs"] = 12 });
        var first = _db.UpsertSettings("user1", "", new[] { Write("prefs", "a", 1, "\"12345\"") }, quotas); // 7 bytes
        Assert.Single(first.Applied);

        var second = _db.UpsertSettings("user1", "", new[] { Write("prefs", "b", 1, "\"12345\"") }, quotas);
        Assert.Equal("ns-quota-exceeded", Assert.Single(second.Rejected).Reason);

        // Other namespaces are untouched by the prefs cap.
        var other = _db.UpsertSettings("user1", "", new[] { Write("player", "a", 1, "\"12345\"") }, quotas);
        Assert.Single(other.Applied);
    }

    [Fact]
    public void Upsert_MidBatchDelete_FreesQuotaForLaterWritesInTheSameBatch()
    {
        // The per-batch quota counters must credit a delete that happens
        // EARLIER in the same batch: one 1022-byte key fills the 1026-byte
        // store; the batch deletes it and writes a fresh key of the same size.
        var blob = new string('x', 1020);
        var tight = new JellyPlayDatabase.Quotas(1024, 1026, 5);
        _db.UpsertSettings("user1", "", new[] { Write("ns", "k0", 1, $"\"{blob}\"") }, tight);
        Assert.Equal(1022L, _db.GetUserFootprint("user1").TotalBytes);

        var result = _db.UpsertSettings(
            "user1",
            "",
            new[]
            {
                new SettingWrite("ns", "k0", 1, 2, "d1", Array.Empty<byte>(), IsDelete: true),
                Write("ns", "k1", 3, $"\"{blob}\"")
            },
            tight);

        var applied = result.Applied.Single(write => !write.Deleted);
        Assert.Equal(("ns", "k1"), (applied.Ns, applied.Key));
        Assert.Single(result.Applied, write => write.Deleted); // the batch's own delete applied too
        Assert.Empty(result.Rejected);
        Assert.Equal(1022L, _db.GetUserFootprint("user1").TotalBytes); // one key's worth, not zero, not two

        // And the reverse order still rejects: the write lands first, the
        // store is full, the delete cannot retroactively un-fill it.
        var fullFirst = _db.UpsertSettings(
            "user1",
            "",
            new[]
            {
                Write("ns", "k2", 4, $"\"{blob}\""),
                new SettingWrite("ns", "k1", 1, 5, "d1", Array.Empty<byte>(), IsDelete: true)
            },
            tight);
        Assert.Equal("quota-exceeded", Assert.Single(fullFirst.Rejected).Reason);
        Assert.Single(fullFirst.Applied); // only the delete applied
    }

    [Fact]
    public void Upsert_InBatchShrink_ThenGrow_DoesNotFalseRejectQuota()
    {
        // An overwrite with a SMALLER value must credit the freed bytes back
        // to the batch's running counters (the fresh-per-key totals this loop
        // replaced never double-counted a shrink): one 1022-byte key fills
        // the 1026-byte store; the batch shrinks the key and grows it back —
        // neither write may see a phantom quota rejection.
        var blob = new string('x', 1020);
        var tight = new JellyPlayDatabase.Quotas(1024, 1026, 5);
        _db.UpsertSettings("user1", "", new[] { Write("ns", "k0", 1, $"\"{blob}\"") }, tight);
        Assert.Equal(1022L, _db.GetUserFootprint("user1").TotalBytes);

        var result = _db.UpsertSettings(
            "user1",
            "",
            new[]
            {
                Write("ns", "k0", 2, "\"tiny\""), // shrink: frees 1016 bytes in-batch
                Write("ns", "k0", 3, $"\"{blob}\"") // grows back to full size
            },
            tight);

        Assert.Empty(result.Rejected);
        Assert.Equal(2, result.Applied.Count);
        Assert.Equal(1022L, _db.GetUserFootprint("user1").TotalBytes);
    }

    [Fact]
    public void TombstoneDeviceSettings_WipesOnlyThatDevicesRows()
    {
        _db.UpsertSettings("user1", "", new[] { Write("ui", "mine", 1, "\"a\"", "d1"), Write("player", "theirs", 1, "\"b\"", "d2") }, Quotas);
        _db.UpsertSettings("user1", "tv", new[] { Write("ui", "also-mine", 2, "\"c\"", "d1") }, Quotas);

        var wiped = _db.TombstoneDeviceSettings("user1", "d1", tombstoneAt: 900);

        Assert.Equal(2, wiped);
        var remaining = Assert.Single(_db.GetSettings("user1", ""));
        Assert.Equal("d2", remaining.DeviceId);
        Assert.Empty(_db.GetSettings("user1", "tv"));
    }

    [Fact]
    public void ChangeLog_Prune_RemovesOnlyOld()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _db.UpsertSettings("user1", "", new[] { Write("ui", "a", now) }, Quotas);
        Assert.True(_db.GetChangeLogHead("user1") > 0);

        // A cutoff 30 days back removes nothing here (the row is fresh) —
        // pinned on the injected cutoff, not the wall clock.
        var pruned = _db.PruneChangeLog(now - 30L * 86_400_000);
        Assert.Equal(0, pruned);
        Assert.True(_db.GetChangeLogHead("user1") > 0);
    }

    [Fact]
    public void ChangeLog_Prune_KeepsTombstones_SoStalePutsStayRejectedPastRetention()
    {
        // Put at t=1, delete at t=2 — both far past any retention window.
        _db.UpsertSettings("user1", "", new[] { Write("ui", "a", 1, "\"v\"") }, Quotas);
        _db.UpsertSettings("user1", "", new[] { new SettingWrite("ui", "a", 1, 2, "d2", Array.Empty<byte>(), IsDelete: true) }, Quotas);

        // Cutoff = now = everything older is eligible — yet only the
        // 'put' row goes; the tombstone IS the anti-resurrection watermark.
        var pruned = _db.PruneChangeLog(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.Equal(1, pruned);

        // A stale offline put (older than the surviving tombstone) is still
        // rejected; only a strictly newer put may legitimately re-create.
        var zombie = _db.UpsertSettings("user1", "", new[] { Write("ui", "a", 1, "\"zombie\"") }, Quotas);
        Assert.Equal("stale-write", Assert.Single(zombie.Rejected).Reason);
        var fresh = _db.UpsertSettings("user1", "", new[] { Write("ui", "a", 3, "\"fresh\"") }, Quotas);
        Assert.Single(fresh.Applied);
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

    [Fact]
    public void Snapshots_InsertListGet_PruneAndKeepLast()
    {
        for (var index = 1; index <= 7; index++)
        {
            _db.InsertSnapshot("u1", System.Text.Encoding.UTF8.GetBytes($"p{index}"), createdAt: index, "manual", keys: index, bytes: index, keepLast: 5);
        }

        _db.InsertSnapshot("u2", System.Text.Encoding.UTF8.GetBytes("other"), createdAt: 9, "admin-push", 1, 1, keepLast: 5);

        // Rolling keep-last: only the newest 5 of u1's 7 survive, newest-first.
        var snapshots = _db.GetSnapshots("u1");
        Assert.Equal(5, snapshots.Count);
        Assert.Equal(7, snapshots[0].Id);
        Assert.Equal(3, snapshots[4].Id);
        Assert.All(snapshots, row => Assert.Equal("manual", row.Origin));

        // Owner-scoped fetch returns the payload; foreign ids do not.
        var mine = _db.GetSnapshot("u1", snapshots[0].Id);
        Assert.NotNull(mine);
        Assert.Equal("p7", System.Text.Encoding.UTF8.GetString(mine!.Value.Payload));
        Assert.Null(_db.GetSnapshot("u2", snapshots[0].Id));

        // Age-based prune: retention 0 removes everything.
        Assert.Equal(6, _db.PruneSnapshots(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        Assert.Empty(_db.GetSnapshots("u1"));
        Assert.Empty(_db.GetSnapshots("u2"));
    }

    [Fact]
    public void GetAllSettingsRows_AndDistinctProfiles_CoverEveryProfile()
    {
        _db.UpsertSettings("u1", "", new[] { Write("ui", "a", 1) }, Quotas);
        _db.UpsertSettings("u1", "tv", new[] { Write("ui", "b", 2) }, Quotas);
        _db.UpsertSettings("u2", "", new[] { Write("ui", "c", 3) }, Quotas);

        Assert.Equal(2, _db.GetAllSettingsRows("u1").Count);
        Assert.Equal(new[] { "", "tv" }, _db.GetDistinctSettingProfiles("u1"));
    }
}
