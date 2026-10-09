using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.JellyPlay.Realtime;
using Jellyfin.Plugin.JellyPlay.Services.Settings;
using Jellyfin.Plugin.JellyPlay.Storage;
using Jellyfin.Plugin.JellyPlay.Storage.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.JellyPlay.Tests;

/// <summary>Task A1: the v2 → v3 migration creates sync_history without touching stored settings.</summary>
public sealed class SyncHistoryMigrationTests : TempDirFixture
{

    public SyncHistoryMigrationTests()
        : base("synchist-mig")
    {
    }


    private string DbPath => Path.Combine(_tempDir, "plugins", "JellyPlay", "jellyplay_plugin.db");

    private static SettingWrite Write(string ns, string key, long updatedAt)
        => new(ns, key, 1, updatedAt, "d1", Encoding.UTF8.GetBytes("\"v\""));

    private static int RawUserVersion(string dbPath)
    {
        using var connection = new SqliteConnection($"Filename={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "pragma user_version";
        return Convert.ToInt32(command.ExecuteScalar()!);
    }

    private static bool TableExists(string dbPath, string tableName)
    {
        using var connection = new SqliteConnection($"Filename={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"select count(*) from sqlite_master where type = 'table' and name = '{tableName}'";
        return Convert.ToInt64(command.ExecuteScalar()!) > 0;
    }

    [Fact]
    public void V2Database_MigratesToV3_PreservesSettings_AndIsIdempotent()
    {
        // Build at the current version, store data, then roll the file back to
        // a v2 state: sync_history dropped, user_version stamped 2.
        string storedValue;
        using (var first = new JellyPlayDatabase(_tempDir))
        {
            first.UpsertSettings(
                "user1",
                JellyPlayDatabase.BaseProfile,
                new[] { Write("ui", "theme", 100), Write("player", "skip", 200) },
                new JellyPlayDatabase.Quotas(1024, 4096, 10));
            storedValue = Encoding.UTF8.GetString(first.GetSettings("user1", "").Single(row => row.Key == "theme").Value);
        }

        using (var raw = new SqliteConnection($"Filename={DbPath}"))
        {
            raw.Open();
            using var downgrade = raw.CreateCommand();
            downgrade.CommandText =
                "drop table if exists sync_history; drop index if exists idx_sync_history_user; " +
                "alter table devices drop column PushKind; alter table devices drop column PushEndpoint; " +
                "alter table devices drop column CreatedAt; pragma user_version = 2;";
            downgrade.ExecuteNonQuery();
        }

        Assert.False(TableExists(DbPath, "sync_history"));

        using (var second = new JellyPlayDatabase(_tempDir))
        {
            Assert.Equal(JellyPlayDatabase.CurrentSchemaVersion, second.UserVersion);
            Assert.Equal(JellyPlayDatabase.CurrentSchemaVersion, RawUserVersion(DbPath));
            Assert.True(TableExists(DbPath, "sync_history"));

            // The v2 data survived the migration untouched.
            var rows = second.GetSettings("user1", "");
            Assert.Equal(2, rows.Count);
            Assert.Equal(storedValue, Encoding.UTF8.GetString(rows.Single(row => row.Key == "theme").Value));

            // And the new table is immediately usable.
            var id = second.InsertSyncHistory("user1", "d1", "push", 1, 0, 5, null, 1234);
            Assert.True(id > 0);
            Assert.Single(second.GetSyncHistory("user1", 0, 50));
        }

        // Re-open: nothing left to migrate.
        using (var third = new JellyPlayDatabase(_tempDir))
        {
            Assert.Equal(JellyPlayDatabase.CurrentSchemaVersion, third.UserVersion);
            Assert.True(third.CheckIntegrity().IntegrityOk);
        }
    }

    [Fact]
    public void FreshDatabase_CreatesSyncHistory_AtCurrentVersion()
    {
        using var db = new JellyPlayDatabase(_tempDir);
        Assert.Equal(JellyPlayDatabase.CurrentSchemaVersion, db.UserVersion);
        Assert.True(TableExists(DbPath, "sync_history"));
        Assert.Empty(db.GetSyncHistory("nobody", 0, 50));
    }
}

/// <summary>
/// Task A2: pushes, pulls and resets are recorded into sync_history; the
/// rejects payload is capped; recording never fails the observed operation.
/// </summary>
public sealed class SyncRecordingTests : TempDatabaseFixture
{
    private readonly SettingsService _service;

    public SyncRecordingTests()
        : base("syncrec")
    {
        _service = new SettingsService(_db, new SseHub(NullLogger<SseHub>.Instance), () => new Configuration.SyncConfig(), NullLogger<SettingsService>.Instance, new SnapshotService(_db, () => new Configuration.SyncConfig()));
    }


    private static Api.SettingsWriteDto Dto(string ns, string key, long at, string json = "\"x\"")
        => new() { Ns = ns, Key = key, SchemaVersion = 1, UpdatedAt = at, Value = JsonDocument.Parse(json).RootElement };

    private static string? RejectsJsonOf(SyncHistoryRow row)
        => row.RejectsJson is null ? null : JsonSerializer.Deserialize<JsonElement>(row.RejectsJson).GetRawText();

    [Fact]
    public void ApplyBatch_RecordsPush_WithCountsBytesAndDevice()
    {
        var json = "\"1234567\""; // 9 bytes serialized
        _service.ApplyBatch("u1", "", "device-a", new[] { Dto("ui", "theme", 1, json), Dto("ui", "font", 2, json) });

        var entry = Assert.Single(_db.GetSyncHistory("u1", 0, 50));
        Assert.Equal("push", entry.Op);
        Assert.Equal("device-a", entry.DeviceId);
        Assert.Equal(2, entry.KeysApplied);
        Assert.Equal(0, entry.KeysRejected);
        Assert.Equal(2 * json.Length, entry.Bytes);
        Assert.Null(entry.RejectsJson);
        Assert.True(entry.Ts > 0);
    }

    [Fact]
    public void ApplyBatch_RecordsRejections_WithCappedRejectsJson()
    {
        // The capped payload builder itself: 15 rejections → 10 entries, keys/ns/reason preserved.
        var rejections = Enumerable.Range(0, 15)
            .Select(index => new RejectedSetting("ui", $"k{index}", "stale-write"))
            .ToList();
        var json = SyncRejectsCodec.Encode(rejections);
        Assert.NotNull(json);
        var parsed = JsonSerializer.Deserialize<JsonElement>(json!).EnumerateArray().ToList();
        Assert.Equal(SyncRejectsCodec.MaxRecordedRejects, parsed.Count);
        Assert.Equal("ui", parsed[0].GetProperty("ns").GetString());
        Assert.Equal("k0", parsed[0].GetProperty("key").GetString());
        Assert.Equal("stale-write", parsed[0].GetProperty("reason").GetString());

        // No rejections → no payload at all.
        Assert.Null(SyncRejectsCodec.Encode(new List<RejectedSetting>()));

        // End to end: a fully-rejected push records counts and the capped array.
        _service.ApplyBatch("u1", "", "d1", Enumerable.Range(0, 12).Select(index => Dto("ui", $"k{index}", 1)).ToList());
        var stale = _service.ApplyBatch("u1", "", "d2", Enumerable.Range(0, 12).Select(index => Dto("ui", $"k{index}", 1)).ToList());
        Assert.Empty(stale.Applied);
        Assert.Equal(12, stale.Rejected.Count);

        var entry = _db.GetSyncHistory("u1", 0, 50).First(); // newest-first: the stale retry
        Assert.Equal("push", entry.Op);
        Assert.Equal("d2", entry.DeviceId);
        Assert.Equal(0, entry.KeysApplied);
        Assert.Equal(12, entry.KeysRejected);
        var rejects = JsonSerializer.Deserialize<JsonElement>(entry.RejectsJson!).EnumerateArray().ToList();
        Assert.Equal(SyncRejectsCodec.MaxRecordedRejects, rejects.Count);
    }

    [Fact]
    public async Task GetChanged_RecordsPull_DeviceFromQueryParam_FullGetDoesNot()
    {
        var applied = _service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "theme", 1), Dto("ui", "font", 2) });
        var head = applied.Head;

        _service.GetChanged("u1", "", 0, "device-pull");
        _service.GetChanged("u1", "", 0, null); // no deviceId param → empty string
        await _service.FlushPullHistoryAsync(); // pull rows land on the flusher, not inline

        var pulls = _db.GetSyncHistory("u1", 0, 50).Where(row => row.Op == "pull").ToList();
        Assert.Equal(2, pulls.Count);
        Assert.True(pulls[0].Id > pulls[1].Id); // newest-first: null-deviceId pull ran last
        Assert.Equal(string.Empty, pulls[0].DeviceId);
        Assert.Equal(2, pulls[0].KeysApplied);
        Assert.Equal("device-pull", pulls[1].DeviceId);

        // Full snapshot reads are explicitly NOT recorded.
        _service.GetAll("u1", "");
        Assert.Equal(3, _db.GetSyncHistory("u1", 0, 50).Count); // 1 push + 2 pulls
        Assert.Equal(head, _db.GetChangeLogHead("u1"));
    }

    [Fact]
    public void ResetNamespace_RecordsReset_WithDeletedKeyCount()
    {
        _service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "a", 1), Dto("ui", "b", 2), Dto("player", "c", 3) });

        _service.ResetNamespace("u1", "", "ui", "device-r");

        var entry = Assert.Single(_db.GetSyncHistory("u1", 0, 50).Where(row => row.Op == "reset"));
        Assert.Equal("device-r", entry.DeviceId);
        Assert.Equal(2, entry.KeysApplied);
        Assert.Equal(0, entry.KeysRejected);
    }

    [Fact]
    public void HistoryRecording_NeverFailsTheSyncOperation()
    {
        // Simulate a broken history store (e.g. the table vanished): the sync
        // operation itself must still succeed end to end.
        using (var raw = new SqliteConnection($"Filename={DbPath};Pooling=False"))
        {
            raw.Open();
            using var drop = raw.CreateCommand();
            drop.CommandText = "drop table sync_history";
            drop.ExecuteNonQuery();
        }

        var result = _service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "theme", 1) });
        Assert.Single(result.Applied);
        Assert.True(result.Head > 0);
        Assert.Single(_db.GetSettings("u1", ""));

        var snapshot = _service.GetChanged("u1", "", 0, "d1");
        Assert.Single(snapshot.Settings);

        _service.ResetNamespace("u1", "", "ui", "d1");
        Assert.Empty(_db.GetSettings("u1", ""));
    }

    private string DbPath => Path.Combine(_tempDir, "plugins", "JellyPlay", "jellyplay_plugin.db");
}

/// <summary>Schema v6: sync_history gains the FromSeq/ToSeq diff range; data preserved, idempotent.</summary>
public sealed class SyncDiffMigrationTests : TempDirFixture
{

    public SyncDiffMigrationTests()
        : base("syncdiff-mig")
    {
    }


    private string DbPath => Path.Combine(_tempDir, "plugins", "JellyPlay", "jellyplay_plugin.db");

    private static int RawUserVersion(string dbPath)
    {
        using var connection = new SqliteConnection($"Filename={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "pragma user_version";
        return Convert.ToInt32(command.ExecuteScalar()!);
    }

    private static List<string> SyncHistoryColumns(string dbPath)
    {
        using var connection = new SqliteConnection($"Filename={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "pragma table_info(sync_history)";
        var names = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(1));
        }

        return names;
    }

    [Fact]
    public void V5Database_MigratesToV6_PreservesData_AndIsIdempotent()
    {
        long legacyRowId;
        using (var first = new JellyPlayDatabase(_tempDir))
        {
            Assert.Equal(JellyPlayDatabase.CurrentSchemaVersion, first.UserVersion);
            first.UpsertSettings(
                "user1",
                JellyPlayDatabase.BaseProfile,
                new[] { new SettingWrite("ui", "theme", 1, 100, "d1", Encoding.UTF8.GetBytes("\"dark\"")) },
                new JellyPlayDatabase.Quotas(1024, 4096, 10));
            legacyRowId = first.InsertSyncHistory("user1", "d1", "push", 1, 0, 5, null, 1234);
        }

        // Roll the file back to a v5 state: the diff-range columns dropped, user_version 5.
        using (var raw = new SqliteConnection($"Filename={DbPath}"))
        {
            raw.Open();
            using var downgrade = raw.CreateCommand();
            downgrade.CommandText =
                "alter table sync_history drop column FromSeq; alter table sync_history drop column ToSeq; " +
                "pragma user_version = 5;";
            downgrade.ExecuteNonQuery();
        }

        Assert.DoesNotContain("FromSeq", SyncHistoryColumns(DbPath));

        using (var second = new JellyPlayDatabase(_tempDir))
        {
            Assert.Equal(JellyPlayDatabase.CurrentSchemaVersion, second.UserVersion);
            Assert.Equal(JellyPlayDatabase.CurrentSchemaVersion, RawUserVersion(DbPath));
            var columns = SyncHistoryColumns(DbPath);
            Assert.Contains("FromSeq", columns);
            Assert.Contains("ToSeq", columns);

            // The v5 data survived the migration untouched; the legacy row has no range.
            var rows = second.GetSettings("user1", "");
            Assert.Single(rows);
            Assert.Equal("\"dark\"", Encoding.UTF8.GetString(rows.Single(row => row.Key == "theme").Value));
            var legacy = second.GetSyncHistory("user1", 0, 50).Single();
            Assert.Equal(legacyRowId, legacy.Id);
            Assert.Null(legacy.FromSeq);
            Assert.Null(legacy.ToSeq);

            // And the new columns are immediately usable.
            var rangedId = second.InsertSyncHistory("user1", "d2", "pull", 0, 0, 0, null, 5678, fromSeq: 3, toSeq: 9);
            var ranged = second.GetSyncHistory("user1", 0, 50).First(row => row.Id == rangedId);
            Assert.Equal((3L, 9L), (ranged.FromSeq, ranged.ToSeq));
        }

        // Re-open: nothing left to migrate.
        using (var third = new JellyPlayDatabase(_tempDir))
        {
            Assert.Equal(JellyPlayDatabase.CurrentSchemaVersion, third.UserVersion);
            Assert.True(third.CheckIntegrity().IntegrityOk);
        }
    }

    [Fact]
    public void FreshDatabase_CreatesDiffRangeColumns_AtCurrentVersion()
    {
        using var db = new JellyPlayDatabase(_tempDir);
        Assert.Equal(JellyPlayDatabase.CurrentSchemaVersion, db.UserVersion);
        var columns = SyncHistoryColumns(DbPath);
        Assert.Contains("FromSeq", columns);
        Assert.Contains("ToSeq", columns);
    }
}

/// <summary>
/// Per-key diff support: each recorded operation captures its change-log
/// range (push = head before/after the batch, pull = the since cursor through
/// the served head, reset = zero-width at the head after).
/// </summary>
public sealed class SyncDiffCaptureTests : TempDatabaseFixture
{
    private readonly SettingsService _service;

    public SyncDiffCaptureTests()
        : base("syncdiff-cap")
    {
        _service = new SettingsService(_db, new SseHub(NullLogger<SseHub>.Instance), () => new Configuration.SyncConfig(), NullLogger<SettingsService>.Instance, new SnapshotService(_db, () => new Configuration.SyncConfig()));
    }


    private static Api.SettingsWriteDto Dto(string ns, string key, long at)
        => new() { Ns = ns, Key = key, SchemaVersion = 1, UpdatedAt = at, Value = JsonDocument.Parse("\"x\"").RootElement };

    private SyncHistoryRow SingleOp(string userId, string op)
        => Assert.Single(_db.GetSyncHistory(userId, 0, 50), row => row.Op == op);

    [Fact]
    public void Push_BracketsTheHeadBeforeAndAfterTheBatch()
    {
        var first = _service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "a", 1), Dto("ui", "b", 2) });
        var headAfterFirst = _db.GetChangeLogHead("u1");
        Assert.Equal(first.Head, headAfterFirst);

        _service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "c", 3) });

        // Batch 1: the change log was empty before, so (fromSeq, toSeq] = everything it applied.
        var firstEntry = _db.GetSyncHistory("u1", 0, 50).OrderBy(row => row.Id).First();
        Assert.Equal(0L, firstEntry.FromSeq);
        Assert.Equal(headAfterFirst, firstEntry.ToSeq);

        // Batch 2 continues exactly at batch 1's head and reaches the current head.
        var secondEntry = _db.GetSyncHistory("u1", 0, 50).OrderByDescending(row => row.Id).First();
        Assert.Equal(headAfterFirst, secondEntry.FromSeq);
        Assert.Equal(_db.GetChangeLogHead("u1"), secondEntry.ToSeq);
    }

    [Fact]
    public async Task Pull_RecordsSinceThroughServedHead_EvenWhenEmpty()
    {
        var applied = _service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "a", 1), Dto("ui", "b", 2) });
        var head = applied.Head;

        // A delta pull from the first key: range = (since, head], 1 key served.
        // Flush between the two pulls: same (user, device) pulls inside one
        // flush window coalesce to the LATEST, so the first must land first.
        _service.GetChanged("u1", "", applied.Applied[0].Seq, "d2");
        await _service.FlushPullHistoryAsync();
        var pull = SingleOp("u1", "pull");
        Assert.Equal(applied.Applied[0].Seq, pull.FromSeq);
        Assert.Equal(head, pull.ToSeq);

        // A zero-key pull (already up to date) still records its range.
        _service.GetChanged("u1", "", head, "d2");
        await _service.FlushPullHistoryAsync();
        var emptyPull = _db.GetSyncHistory("u1", 0, 50).First(row => row.Op == "pull" && row.Id != pull.Id);
        Assert.Equal(head, emptyPull.FromSeq);
        Assert.Equal(head, emptyPull.ToSeq);
    }

    [Fact]
    public void Reset_RecordsTheTombstoneBatchRange()
    {
        _service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "a", 1), Dto("ui", "b", 2), Dto("player", "c", 3) });
        var headBefore = _db.GetChangeLogHead("u1");

        _service.ResetNamespace("u1", "", "ui", "d1");

        // Tombstone semantics (schema v7): the reset's range brackets exactly
        // the 'del' change-log rows it appended (one per removed key).
        var reset = SingleOp("u1", "reset");
        Assert.Equal(2, reset.KeysApplied); // the deleted key count
        Assert.Equal(headBefore, reset.FromSeq);
        Assert.Equal(_db.GetChangeLogHead("u1"), reset.ToSeq);
    }
}

/// <summary>Task: the per-key diff endpoint semantics (range correctness, user isolation, reset-empty, limit clamp).</summary>
public sealed class SyncHistoryKeysTests : TempDatabaseFixture
{
    private readonly SettingsService _service;

    public SyncHistoryKeysTests()
        : base("synckeys")
    {
        _service = new SettingsService(_db, new SseHub(NullLogger<SseHub>.Instance), () => new Configuration.SyncConfig(), NullLogger<SettingsService>.Instance, new SnapshotService(_db, () => new Configuration.SyncConfig()));
    }


    private SyncInsightsService Service => new(_db, () => new Configuration.SyncConfig());

    private static Api.SettingsWriteDto Dto(string ns, string key, long at)
        => new() { Ns = ns, Key = key, SchemaVersion = 1, UpdatedAt = at, Value = JsonDocument.Parse("\"x\"").RootElement };

    /// <summary>Two pushes; returns (firstEntry, secondEntry) with the change-log rows each batch appended.</summary>
    private (SyncHistoryRow First, SyncHistoryRow Second) PushTwice()
    {
        _service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "a", 1), Dto("ui", "b", 2) });
        _service.ApplyBatch("u1", "", "d1", new[] { Dto("player", "c", 3) });
        var entries = _db.GetSyncHistory("u1", 0, 50);
        return (entries.Single(row => row.KeysApplied == 2), entries.Single(row => row.KeysApplied == 1));
    }

    [Fact]
    public void Keys_ReturnTheRangeNewestFirst_WithTimestamps()
    {
        var (first, second) = PushTwice();

        var firstDiff = Service.GetHistoryKeys("u1", first.Id, 200);
        Assert.NotNull(firstDiff);
        Assert.Equal(first.Id, firstDiff!.Seq);
        Assert.Equal("push", firstDiff.Op);
        Assert.Equal(new[] { ("ui", "b"), ("ui", "a") }, firstDiff.Keys.Select(key => (key.Ns, key.Key)).ToList());

        var secondDiff = Service.GetHistoryKeys("u1", second.Id, 200);
        Assert.NotNull(secondDiff);
        Assert.Equal(new[] { ("player", "c") }, secondDiff!.Keys.Select(key => (key.Ns, key.Key)).ToList());

        // Timestamps come from the change-log rows (the writes' UpdatedAt).
        Assert.Equal(2, firstDiff.Keys.Single(key => key.Key == "b").UpdatedAt);
        Assert.Equal(3, secondDiff.Keys.Single(key => key.Key == "c").UpdatedAt);
    }

    [Fact]
    public void Keys_UserIsolation_UnknownOrForeignSeqIs404()
    {
        var (first, _) = PushTwice();
        _service.ApplyBatch("u2", "", "d9", new[] { Dto("ui", "x", 1) });
        var u2Entry = Assert.Single(_db.GetSyncHistory("u2", 0, 50));

        // Own row resolves; another user's seq and unknown seqs do not.
        Assert.NotNull(Service.GetHistoryKeys("u1", first.Id, 200));
        Assert.Null(Service.GetHistoryKeys("u1", u2Entry.Id));
        Assert.Null(Service.GetHistoryKeys("u2", first.Id));
        Assert.Null(Service.GetHistoryKeys("u1", 999_999));
    }

    [Fact]
    public void Keys_ResetRows_CarryTheirTombstonedKeys_MissingRangesYieldEmpty()
    {
        _service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "a", 1), Dto("ui", "b", 2) });
        _service.ResetNamespace("u1", "", "ui", "d1");
        var reset = Assert.Single(_db.GetSyncHistory("u1", 0, 50), row => row.Op == "reset");

        // Tombstone semantics (schema v7): the reset's range holds the 'del'
        // rows, so the per-key diff lists the keys the reset removed.
        var response = Service.GetHistoryKeys("u1", reset.Id, 200);
        Assert.NotNull(response);
        Assert.Equal("reset", response!.Op);
        Assert.Equal(new[] { ("ui", "b"), ("ui", "a") }, response.Keys.Select(key => (key.Ns, key.Key)).ToList());

        // A legacy row recorded before the range existed (nulls) degrades to an empty list.
        var legacyId = _db.InsertSyncHistory("u1", "d2", "push", 1, 0, 0, null, 42);
        Assert.Empty(Service.GetHistoryKeys("u1", legacyId, 200)!.Keys);
    }

    [Fact]
    public void Keys_LimitClampsToOneToTwoHundred_AndReturnsNewestFirst()
    {
        var (first, _) = PushTwice();

        // The range holds exactly 2 rows: clamp bounds are visible from both sides.
        Assert.Equal(1, Service.GetHistoryKeys("u1", first.Id, 1)!.Keys.Count);
        Assert.Equal(1, Service.GetHistoryKeys("u1", first.Id, -5)!.Keys.Count); // below 1 → 1
        Assert.Equal(2, Service.GetHistoryKeys("u1", first.Id, 2)!.Keys.Count);

        // A wider range shows newest-first truncation and the default/cap behavior.
        var wideId = _db.InsertSyncHistory("u1", "d2", "push", 0, 0, 0, null, 99, fromSeq: 0, toSeq: _db.GetChangeLogHead("u1"));
        var wide = Service.GetHistoryKeys("u1", wideId, 2)!;
        Assert.Equal(new[] { ("player", "c"), ("ui", "b") }, wide.Keys.Select(key => (key.Ns, key.Key)).ToList()); // newest two of three

        Assert.Equal(3, Service.GetHistoryKeys("u1", wideId, 0)!.Keys.Count); // 0 → default 200 → everything
        Assert.Equal(3, Service.GetHistoryKeys("u1", wideId, 100_000)!.Keys.Count); // capped at 200, range smaller
    }

    [Fact]
    public void History_EntriesExposeTheDiffRange()
    {
        var (first, second) = PushTwice();

        var entries = Service.GetHistory("u1", null, 50).Entries;
        var firstDto = entries.Single(entry => entry.Seq == first.Id);
        Assert.Equal((0L, first.ToSeq), (firstDto.FromSeq, firstDto.ToSeq));
        var secondDto = entries.Single(entry => entry.Seq == second.Id);
        Assert.Equal(first.ToSeq, secondDto.FromSeq); // batch 2 continues at batch 1's head
    }
}

/// <summary>Task A3/A4: status aggregation, history query semantics, admin folding, retention prune.</summary>
public sealed class SyncInsightsTests : TempDatabaseFixture
{
    private Configuration.SyncConfig _config = new();

    private SyncInsightsService Service => new(_db, () => _config);

    public SyncInsightsTests()
        : base("syncins")
    {
    }


    private static SettingWrite Write(string ns, string key, long updatedAt, string value = "\"v\"")
        => new(ns, key, 1, updatedAt, "d1", Encoding.UTF8.GetBytes(value));

    [Fact]
    public void Status_FoldsNamespacesDevicesAndQuotas()
    {
        // Serialized byte lengths: "\"12345\"" = 7, "\"123\"" = 5, "\"1234567\"" = 9.
        // Namespaces aggregate across profiles ("ui" appears in base AND tv and is folded).
        _db.UpsertSettings("u1", "", new[] { Write("ui", "a", 1, "\"12345\""), Write("player", "b", 2, "\"123\"") }, new JellyPlayDatabase.Quotas(1024, 4096, 10));
        _db.UpsertSettings("u1", "tv", new[] { Write("ui", "a", 3, "\"1234567\"") }, new JellyPlayDatabase.Quotas(1024, 4096, 10));

        // Two devices, several ops each: the fold keeps only the latest per device.
        _db.InsertSyncHistory("u1", "phone", "push", 1, 0, 10, null, 1000);
        _db.InsertSyncHistory("u1", "tv", "pull", 2, 0, 0, null, 2000);
        _db.InsertSyncHistory("u1", "phone", "reset", 5, 0, 0, null, 3000);
        _db.InsertSyncHistory("other-user", "phone", "push", 9, 9, 9, null, 4000); // someone else's device must not leak

        _config = new Configuration.SyncConfig { MaxUserBytes = 1234, MaxKeysPerUser = 42, HistoryRetentionDays = 7 };

        var status = Service.GetStatus("u1");

        Assert.Equal(_db.GetChangeLogHead("u1"), status.Head);
        Assert.Equal(3, status.Keys);
        Assert.Equal(21, status.Bytes); // 7 + 5 + 9
        Assert.Equal(1234, status.QuotaBytes);
        Assert.Equal(42, status.QuotaKeys);
        Assert.Equal(7, status.HistoryRetentionDays);

        var ns = status.Namespaces.ToList();
        Assert.Equal(2, ns.Count);
        Assert.Equal("player", ns[0].Ns);
        Assert.Equal(1, ns[0].Keys);
        Assert.Equal(5, ns[0].Bytes);
        Assert.Equal("ui", ns[1].Ns);
        Assert.Equal(2, ns[1].Keys); // base + tv folded
        Assert.Equal(16, ns[1].Bytes); // 7 (base) + 9 (tv overlay)

        var perDevice = status.PerDevice.ToDictionary(row => row.DeviceId);
        Assert.Equal(2, perDevice.Count);
        Assert.Equal("reset", perDevice["phone"].LastOp);
        Assert.Equal(3000, perDevice["phone"].LastSyncAt);
        Assert.Equal("pull", perDevice["tv"].LastOp);
        Assert.Equal(2000, perDevice["tv"].LastSyncAt);
    }

    [Fact]
    public void History_NewestFirst_SinceAndLimit_UserIsolation()
    {
        var ids = new List<long>();
        for (var index = 0; index < 5; index++)
        {
            ids.Add(_db.InsertSyncHistory("u1", $"d{index}", "push", index, 0, 0, null, 1000 + index));
        }

        _db.InsertSyncHistory("u2", "dx", "push", 1, 0, 0, null, 5000);

        // Newest first, limit respected.
        var page = Service.GetHistory("u1", null, 3);
        Assert.Equal(new[] { ids[4], ids[3], ids[2] }, page.Entries.Select(entry => entry.Seq).ToList());
        Assert.All(page.Entries, entry => Assert.Equal("push", entry.Op));

        // since filters strictly older entries out.
        var sincePage = Service.GetHistory("u1", 1002, 50);
        Assert.Equal(new[] { ids[4], ids[3] }, sincePage.Entries.Select(entry => entry.Seq).ToList());

        // User isolation: u2 sees only its own entry.
        var u2 = Service.GetHistory("u2", null, 50);
        Assert.Single(u2.Entries);
        Assert.Equal("u2", _db.GetSyncHistory("u2", 0, 50).Single().UserId);

        // Rejects round-trip through the capped JSON payload.
        _db.InsertSyncHistory("u2", "dx", "push", 0, 2, 0, "[{\"ns\":\"ui\",\"key\":\"k\",\"reason\":\"stale-write\"}]", 6000);
        var withRejects = Service.GetHistory("u2", 5999, 50).Entries.Single();
        Assert.Equal(2, withRejects.KeysRejected);
        var reject = Assert.Single(withRejects.Rejects!);
        Assert.Equal(("ui", "k", "stale-write"), (reject.Ns, reject.Key, reject.Reason));

        // Malformed reject payloads degrade to "no rejects listed".
        _db.InsertSyncHistory("u2", "dx", "push", 0, 1, 0, "not-json", 6100);
        Assert.Null(Service.GetHistory("u2", 6099, 50).Entries.Single().Rejects);
    }

    [Fact]
    public void History_LimitClampsToBounds_AndZeroMeansDefault()
    {
        // 210 entries so the 200 cap is observable.
        for (var index = 0; index < 210; index++)
        {
            _db.InsertSyncHistory("u1", "d", "push", 1, 0, 0, null, index);
        }

        Assert.Equal(SyncInsightsService.MaxHistoryLimit, Service.GetHistory("u1", null, 100_000).Entries.Count);
        Assert.Equal(1, Service.GetHistory("u1", null, 1).Entries.Count);
        Assert.Equal(1, Service.GetHistory("u1", null, -5).Entries.Count);
        Assert.Equal(SyncInsightsService.DefaultHistoryLimit, Service.GetHistory("u1", null, 0).Entries.Count);
    }

    [Fact]
    public void AdminOverview_FoldsFootprintsHistoryAndNames()
    {
        var user1 = "11111111-1111-1111-1111-111111111111";
        var user2 = "22222222-2222-2222-2222-222222222222";
        _db.UpsertSettings(user1, "", new[] { Write("ui", "a", 1, "\"12345\""), Write("ui", "b", 2, "\"123\"") }, new JellyPlayDatabase.Quotas(1024, 4096, 10));
        _db.UpsertSettings(user2, "", new[] { Write("ui", "c", 3, "\"123\"") }, new JellyPlayDatabase.Quotas(1024, 4096, 10));

        _db.InsertSyncHistory(user1, "phone", "push", 2, 0, 0, null, 1000);
        _db.InsertSyncHistory(user1, "tv", "pull", 2, 0, 0, null, 2000);
        _db.InsertSyncHistory(user1, "phone", "push", 1, 0, 0, null, 3000);

        var guid1 = Guid.Parse(user1);
        var guid2 = Guid.Parse(user2);
        var userNames = new Dictionary<Guid, string> { [guid1] = "Alice" };

        var overview = Service.GetAdminOverview(guid => userNames.TryGetValue(guid, out var name) ? name : null);

        var users = overview.Users.ToDictionary(row => row.UserId);
        Assert.Equal(2, users.Count);

        var alice = users[user1];
        Assert.Equal("Alice", alice.UserName); // resolved via the manager
        Assert.Equal(2, alice.Keys);
        Assert.Equal(12, alice.Bytes); // 7 + 5 serialized bytes
        Assert.Equal(3000, alice.LastSyncAt); // latest across devices
        Assert.Equal(2, alice.DeviceCount); // distinct devices

        var unknown = users[user2];
        Assert.Equal(user2, unknown.UserName); // unknown user falls back to the id
        Assert.Equal(1, unknown.Keys);
        Assert.Null(unknown.LastSyncAt); // no recorded history
        Assert.Equal(0, unknown.DeviceCount);

        // A non-guid settings owner (legacy/synthetic id) also degrades to the raw id.
        _db.UpsertSettings("legacy-user", "", new[] { Write("ui", "d", 4, "\"123\"") }, new JellyPlayDatabase.Quotas(1024, 4096, 10));
        var legacy = Service.GetAdminOverview(guid => userNames.TryGetValue(guid, out var n) ? n : null).Users.Single(row => row.UserId == "legacy-user");
        Assert.Equal("legacy-user", legacy.UserName);
    }

    [Fact]
    public void PruneSyncHistory_RemovesOnlyOlderThanRetention()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _db.InsertSyncHistory("u1", "old", "push", 1, 0, 0, null, now - TimeSpan.FromDays(40).Ticks / TimeSpan.TicksPerMillisecond);
        _db.InsertSyncHistory("u1", "fresh", "pull", 1, 0, 0, null, now - TimeSpan.FromDays(1).Ticks / TimeSpan.TicksPerMillisecond);
        _db.InsertSyncHistory("u2", "old", "reset", 1, 0, 0, null, now - TimeSpan.FromDays(50).Ticks / TimeSpan.TicksPerMillisecond);

        var pruned = _db.PruneSyncHistory(now - TimeSpan.FromDays(30).Ticks / TimeSpan.TicksPerMillisecond);

        Assert.Equal(2, pruned);
        var remaining = _db.GetSyncHistory("u1", 0, 50).ToList();
        Assert.Single(remaining);
        Assert.Equal("fresh", remaining[0].DeviceId);
        Assert.Empty(_db.GetSyncHistory("u2", 0, 50));

        // A now-cutoff wipes everything left.
        Assert.Equal(1, _db.PruneSyncHistory(now));
        Assert.Empty(_db.GetSyncHistory("u1", 0, 50));
    }
}
