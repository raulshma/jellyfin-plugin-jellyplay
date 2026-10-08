using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.JellyPlay.Realtime;
using Jellyfin.Plugin.JellyPlay.Services.Settings;
using Jellyfin.Plugin.JellyPlay.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.JellyPlay.Tests;

/// <summary>Shared construction helper for tests that need a SettingsService over a real SQLite database.</summary>
internal static class SettingsServiceFactory
{
    public static Services.Settings.SettingsService Create(
        JellyPlayDatabase db,
        SseHub hub,
        Configuration.SyncConfig? config = null,
        Services.Settings.SnapshotService? snapshots = null,
        Services.Push.PushDispatcher? push = null,
        TimeProvider? clock = null)
        => new(
            db,
            hub,
            () => config ?? new Configuration.SyncConfig(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Services.Settings.SettingsService>.Instance,
            snapshots ?? new Services.Settings.SnapshotService(db, () => config ?? new Configuration.SyncConfig()),
            push,
            clock);
}

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "jellyplay-svc-tests-" + Guid.NewGuid().ToString("N"));
    private readonly JellyPlayDatabase _db;
    private readonly SnapshotService _snapshots;
    private readonly SettingsService _service;
    private Configuration.SyncConfig _syncConfig = new();

    public SettingsServiceTests()
    {
        Directory.CreateDirectory(_tempDir);
        _db = new JellyPlayDatabase(_tempDir);
        _snapshots = new SnapshotService(_db, () => _syncConfig);
        _service = new SettingsService(_db, new SseHub(NullLogger<SseHub>.Instance), () => _syncConfig, NullLogger<SettingsService>.Instance, _snapshots);
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

    private static Api.SettingsWriteDto Dto(string ns, string key, long at, string json = "true")
        => new() { Ns = ns, Key = key, SchemaVersion = 1, UpdatedAt = at, Value = JsonDocument.Parse(json).RootElement };

    [Fact]
    public void ResolveProfile_BasePlusOverlay_OverlayWins()
    {
        _service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "theme", 1, "\"base\""), Dto("ui", "skip", 1, "30") });
        _service.ApplyBatch("u1", "tv", "d1", new[] { Dto("ui", "theme", 2, "\"tv\"") });

        var resolved = _service.ResolveProfile("u1", "tv");

        var theme = resolved.Settings.Single(entry => entry.Key == "theme");
        Assert.Equal("\"tv\"", theme.Value.Json);
        Assert.Equal(2, resolved.Settings.Count); // skip inherited from base
    }

    [Fact]
    public void ResolveProfile_ForcedDefault_OverridesUser()
    {
        _service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "theme", 1, "\"user\"") });
        _service.SetAdminDefaults(
            SettingsService.GlobalDefaultsScope,
            JsonDocument.Parse("{\"ui/theme\":{\"mode\":\"forced\",\"value\":\"dark\"}}").RootElement);

        var resolved = _service.ResolveProfile("u1", "");

        Assert.Equal("\"dark\"", resolved.Settings.Single(entry => entry.Key == "theme").Value.Json);
    }

    [Fact]
    public void ResolveProfile_SuggestedDefault_FillsMissingOnly()
    {
        _service.SetAdminDefaults(
            SettingsService.GlobalDefaultsScope,
            JsonDocument.Parse("{\"player/skip\":{\"mode\":\"suggested\",\"value\":15}}").RootElement);

        var resolved = _service.ResolveProfile("u2", "");

        Assert.Equal("15", resolved.Settings.Single(entry => entry.Key == "skip").Value.Json);
    }

    [Fact]
    public void ResolveProfile_SuggestedDefault_DoesNotOverrideUserValue()
    {
        _service.ApplyBatch("u1", "", "d1", new[] { Dto("player", "skip", 1, "30") });
        _service.SetAdminDefaults(
            SettingsService.GlobalDefaultsScope,
            JsonDocument.Parse("{\"player/skip\":{\"mode\":\"suggested\",\"value\":15}}").RootElement);

        var resolved = _service.ResolveProfile("u1", "");

        Assert.Equal("30", resolved.Settings.Single(entry => entry.Key == "skip").Value.Json);
    }

    [Fact]
    public void ApplyBatch_RejectsStale_AndReturnsHead()
    {
        var first = _service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "theme", 10) });
        var second = _service.ApplyBatch("u1", "", "d2", new[] { Dto("ui", "theme", 5) });

        Assert.Single(first.Applied);
        Assert.Single(second.Rejected);
        Assert.True(second.Head >= first.Head);
    }

    [Fact]
    public void ResetNamespace_RemovesAllKeysInNamespace()
    {
        _service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "a", 1), Dto("ui", "b", 1), Dto("player", "c", 1) });

        _service.ResetNamespace("u1", "", "ui", "d1");

        var all = _service.GetAll("u1", "");
        var remaining = Assert.Single(all.Settings);
        Assert.Equal("player", remaining.Ns);
    }

    // ------------------------------------------------------------------
    // Resolved-settings modes map (additive field, enables forced-lock UI)
    // ------------------------------------------------------------------

    [Fact]
    public void ResolveProfile_ModesMap_MixesForcedSuggestedUnset()
    {
        _service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "theme", 1, "\"user\""), Dto("ui", "locked", 1, "\"user\"") });
        _service.SetAdminDefaults(
            SettingsService.GlobalDefaultsScope,
            JsonDocument.Parse(
                "{\"ui/locked\":{\"mode\":\"forced\",\"value\":true},\"player/skip\":{\"mode\":\"suggested\",\"value\":15}}")
                .RootElement);

        var resolved = _service.ResolveProfile("u1", "");

        // forced replaces the user value; suggested fills the unset key; the
        // plain user key (and the user's locked value before the override)
        // reads as unset.
        Assert.Equal("true", resolved.Settings.Single(entry => entry.Key == "locked").Value.Json);
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["ui/locked"] = "forced",
                ["player/skip"] = "suggested",
                ["ui/theme"] = "unset"
            },
            resolved.Modes);
    }

    [Fact]
    public void ResolveProfile_SuggestedLosingToUserValue_ModeStaysUnset()
    {
        // User scope wins: the suggested default does not apply, so the
        // resolved value's provenance is the user's — "unset", not "suggested".
        _service.ApplyBatch("u1", "", "d1", new[] { Dto("player", "skip", 1, "30") });
        _service.SetAdminDefaults(
            SettingsService.GlobalDefaultsScope,
            JsonDocument.Parse("{\"player/skip\":{\"mode\":\"suggested\",\"value\":15}}").RootElement);

        var resolved = _service.ResolveProfile("u1", "");

        Assert.Equal("30", resolved.Settings.Single(entry => entry.Key == "skip").Value.Json);
        Assert.Equal("unset", resolved.Modes!["player/skip"]);
    }

    [Fact]
    public void ResolveProfile_UserScopeDefaultWins_ModeComesFromUserScope()
    {
        // Global says suggested, the user-scope default says forced: the user
        // scope wins for BOTH the mode and the semantics.
        _service.SetAdminDefaults(
            SettingsService.GlobalDefaultsScope,
            JsonDocument.Parse("{\"ui/volume\":{\"mode\":\"suggested\",\"value\":50}}").RootElement);
        _service.SetAdminDefaults(
            "u1",
            JsonDocument.Parse("{\"ui/volume\":{\"mode\":\"forced\",\"value\":80}}").RootElement);

        var resolved = _service.ResolveProfile("u1", "");

        Assert.Equal("80", resolved.Settings.Single(entry => entry.Key == "volume").Value.Json);
        Assert.Equal("forced", resolved.Modes!["ui/volume"]);

        // And the reverse: a user-scope suggested downgrade loses to nothing —
        // it replaces the global forced entry wholesale.
        _service.SetAdminDefaults(
            "u2",
            JsonDocument.Parse("{\"ui/volume\":{\"mode\":\"suggested\",\"value\":40}}").RootElement);
        _service.SetAdminDefaults(
            SettingsService.GlobalDefaultsScope,
            JsonDocument.Parse("{\"ui/volume\":{\"mode\":\"forced\",\"value\":90}}").RootElement);

        var u2 = _service.ResolveProfile("u2", "");
        Assert.Equal("40", u2.Settings.Single(entry => entry.Key == "volume").Value.Json);
        Assert.Equal("suggested", u2.Modes!["ui/volume"]);
    }

    [Fact]
    public void ResolveProfile_ProfileOverlayKey_ModeIsUnset()
    {
        _service.ApplyBatch("u1", "tv", "d1", new[] { Dto("ui", "layout", 1, "\"tv\"") });

        var resolved = _service.ResolveProfile("u1", "tv");

        Assert.Equal("unset", resolved.Modes!["ui/layout"]);
    }

    [Fact]
    public void PlainSnapshots_CarryNoModes()
    {
        _service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "theme", 1) });
        _service.SetAdminDefaults(
            SettingsService.GlobalDefaultsScope,
            JsonDocument.Parse("{\"ui/locked\":{\"mode\":\"forced\",\"value\":true}}").RootElement);

        Assert.Null(_service.GetAll("u1", "").Modes);
        Assert.Null(_service.GetChanged("u1", "", 0, "d1").Modes);
    }

    // ------------------------------------------------------------------
    // Tombstones (schema v7)
    // ------------------------------------------------------------------

    [Fact]
    public void ApplyBatch_Delete_RemovesKey_AndDeltaCarriesDeleted()
    {
        var first = _service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "keep", 1), Dto("ui", "gone", 1) });

        var delete = new Api.SettingsWriteDto { Ns = "ui", Key = "gone", UpdatedAt = 2, Deleted = true };
        var applied = _service.ApplyBatch("u1", "", "d2", new[] { delete });

        Assert.True(applied.Applied.Single().Deleted);
        Assert.Single(_service.GetAll("u1", "").Settings, entry => entry.Key == "keep");

        // The delta since the first batch: no live rows, one tombstoned key.
        var delta = _service.GetChanged("u1", "", first.Head, "d2");
        Assert.Empty(delta.Settings);
        var deleted = Assert.Single(delta.Deleted!);
        Assert.Equal(("ui", "gone"), (deleted.Ns, deleted.Key));
    }

    [Fact]
    public void ApplyBatch_NullValueWithoutFlag_IsStoredNotDeleted()
    {
        // The flag is the ONLY tombstone form: a JSON-null value without it is
        // a stored value ("null" bytes, the pre-v7 behavior), never a delete.
        var applied = _service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "theme", 1, "null") });

        Assert.Single(applied.Applied);
        Assert.False(Assert.Single(applied.Applied).Deleted);
        var stored = Assert.Single(_service.GetAll("u1", "").Settings);
        Assert.Equal("null", stored.Value.Json);
        Assert.Null(_service.GetChanged("u1", "", 0, "d2").Deleted);
    }

    [Fact]
    public void ApplyBatch_StalePut_AfterDelete_IsRejectedAntiResurrection()
    {
        _service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "theme", 10) });
        _service.ApplyBatch("u1", "", "d2", new[] { new Api.SettingsWriteDto { Ns = "ui", Key = "theme", UpdatedAt = 20, Deleted = true } });

        // A peer still holding the old copy must not resurrect it (older or
        // equal timestamp loses to the tombstone; strictly newer wins LWW).
        var zombie = _service.ApplyBatch("u1", "", "d3", new[] { Dto("ui", "theme", 15, "\"zombie\"") });
        Assert.Equal("stale-write", Assert.Single(zombie.Rejected).Reason);
        Assert.Empty(_service.GetAll("u1", "").Settings);

        var newer = _service.ApplyBatch("u1", "", "d3", new[] { Dto("ui", "theme", 21, "\"fresh\"") });
        Assert.Single(newer.Applied);
        Assert.Single(_service.GetAll("u1", "").Settings);
    }

    [Fact]
    public void ResetNamespace_TombstonedKeysReachTheDelta()
    {
        _service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "a", 1), Dto("ui", "b", 2) });
        var head = _service.GetAll("u1", "").Head;

        _service.ResetNamespace("u1", "", "ui", "d1");

        var delta = _service.GetChanged("u1", "", head, "d2");
        Assert.Empty(delta.Settings);
        Assert.Equal(new[] { ("ui", "a"), ("ui", "b") }, delta.Deleted!.Select(key => (key.Ns, key.Key)).ToList());
    }

    // ------------------------------------------------------------------
    // Clock-skew clamp
    // ------------------------------------------------------------------

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public void ApplyBatch_WriteTooFarAhead_IsRejectedClockSkew()
    {
        var at = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var service = SettingsServiceFactory.Create(_db, new SseHub(NullLogger<SseHub>.Instance), clock: new FixedTimeProvider(at));
        var serverNow = at.ToUnixTimeMilliseconds();

        var farAhead = service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "a", serverNow + SettingsService.MaxClockSkewMilliseconds + 1) });
        Assert.Equal("clock-skew", Assert.Single(farAhead.Rejected).Reason);

        // Exactly at the 5-minute bound is still fine (pure client-clock LWW).
        var atBound = service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "a", serverNow + SettingsService.MaxClockSkewMilliseconds) });
        Assert.Single(atBound.Applied);

        // And so is anything in the past (LWW decides).
        var past = service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "b", 1) });
        Assert.Single(past.Applied);
    }

    // ------------------------------------------------------------------
    // Pagination
    // ------------------------------------------------------------------

    [Fact]
    public void GetAll_Paginates_WithNextCursor()
    {
        _service.ApplyBatch("u1", "", "d1", Enumerable.Range(0, 5).Select(index => Dto("ui", $"k{index}", index + 1)).ToList());

        var page1 = _service.GetAll("u1", "", cursor: null, limit: 2);
        Assert.Equal(2, page1.Settings.Count);
        Assert.Equal(2, page1.NextCursor);

        var page2 = _service.GetAll("u1", "", page1.NextCursor, 2);
        Assert.Equal(2, page2.Settings.Count);
        Assert.Equal(4, page2.NextCursor);

        var last = _service.GetAll("u1", "", page2.NextCursor, 2);
        Assert.Single(last.Settings);
        Assert.Null(last.NextCursor); // absent = last page
    }

    [Fact]
    public void GetChanged_PaginatesRows_WhileDeletedStaysWhole()
    {
        _service.ApplyBatch("u1", "", "d1", Enumerable.Range(0, 4).Select(index => Dto("ui", $"k{index}", index + 1)).ToList());
        _service.ApplyBatch("u1", "", "d2", new[] { new Api.SettingsWriteDto { Ns = "ui", Key = "gone", UpdatedAt = 99, Deleted = true } });

        // Delta from the beginning of time: 4 live rows + 1 tombstoned key.
        var page = _service.GetChanged("u1", "", 0, "d2", cursor: null, limit: 2);
        Assert.Equal(2, page.Settings.Count);
        Assert.Equal(2, page.NextCursor);
        Assert.Single(page.Deleted!); // deleted[] is never paginated

        var last = _service.GetChanged("u1", "", 0, "d2", page.NextCursor, 2);
        Assert.Equal(2, last.Settings.Count);
        Assert.Null(last.NextCursor);
        Assert.Single(last.Deleted!);
    }

    // ------------------------------------------------------------------
    // Device registry integration: revoked devices
    // ------------------------------------------------------------------

    [Fact]
    public void ApplyBatch_RevokedDevice_WritesRejectedDeviceRevoked()
    {
        var devices = new Services.Devices.DeviceRegistryService(_db, () => new Configuration.PushConfig());
        Assert.Equal(Services.Devices.RegisterDeviceOutcome.Registered, devices.Register(new Services.Devices.DeviceRegistration("u1", "d1", "Phone", "android", "1.0", null)));
        _service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "theme", 1) });

        Assert.True(devices.Revoke("u1", "d1"));

        var rejected = _service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "theme", 9, "\"new\"") });
        Assert.Equal("device-revoked", Assert.Single(rejected.Rejected).Reason);
        Assert.Empty(rejected.Applied);

        // Unknown devices are never considered revoked.
        var unknown = _service.ApplyBatch("u1", "", "never-seen", new[] { Dto("ui", "other", 9) });
        Assert.Single(unknown.Applied);
    }

    [Fact]
    public void WipeDevice_TombstonesOnlyThatDevicesRows_AndRecordsWipe()
    {
        _service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "mine", 1), Dto("player", "theirs", 1) });
        _service.ApplyBatch("u1", "", "d2", new[] { Dto("ui", "d2key", 1) });
        var head = _service.GetAll("u1", "").Head;

        _service.WipeDevice("u1", "d1");

        var remaining = _service.GetAll("u1", "");
        Assert.Single(remaining.Settings, entry => entry.DeviceId == "d2");

        var delta = _service.GetChanged("u1", "", head, "d2");
        Assert.Empty(delta.Settings);
        Assert.Equal(2, delta.Deleted!.Count); // d1's rows (both profiles/namespaces) tombstoned

        var wipe = Assert.Single(_db.GetSyncHistory("u1", 0, 50), row => row.Op == "wipe");
        Assert.Equal(2, wipe.KeysApplied);
    }

    // ------------------------------------------------------------------
    // Restore points
    // ------------------------------------------------------------------

    [Fact]
    public void RestoreSnapshot_ReturnsTheStoreToItsCapturedState()
    {
        _service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "a", 1, "\"one\""), Dto("ui", "b", 1, "\"two\"") });
        var snapshotId = _snapshots.Create("u1", "manual")!.Value;

        // Destructive drift after the capture: overwrite one key, delete another.
        _service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "a", 2, "\"CHANGED\"") });
        _service.ApplyBatch("u1", "", "d1", new[] { new Api.SettingsWriteDto { Ns = "ui", Key = "b", UpdatedAt = 3, Deleted = true } });
        var preRestoreHead = _service.GetAll("u1", "").Head;

        var response = _service.RestoreSnapshot("u1", snapshotId);

        Assert.NotNull(response);
        var restored = _service.GetAll("u1", "");
        Assert.Equal(2, restored.Settings.Count);
        Assert.Equal("\"one\"", restored.Settings.Single(entry => entry.Key == "a").Value.Json);
        Assert.Equal("\"two\"", restored.Settings.Single(entry => entry.Key == "b").Value.Json);

        // Peers learn about the restore through the ordinary delta: since the
        // pre-restore head they see the re-applied (and re-created) keys.
        var delta = _service.GetChanged("u1", "", preRestoreHead, "d2");
        Assert.NotNull(delta);
        Assert.Equal(2, delta.Settings.Count);
        Assert.Null(delta.Deleted); // nothing stays deleted: the restore re-created every key
    }

    [Fact]
    public void RestoreSnapshot_TombstonesOnlyKeysAbsentFromTheSnapshot()
    {
        // Drift by overwrite alone: every snapshot key is still present, so
        // the restore diff must not tombstone ANY row (the del+put pairs the
        // full-retombstone path produced were pure change-log noise).
        _service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "a", 1, "\"one\"") });
        var snapshotId = _snapshots.Create("u1", "manual")!.Value;
        _service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "a", 2, "\"CHANGED\"") });
        var preRestoreHead = _db.GetChangeLogHead("u1");

        var response = _service.RestoreSnapshot("u1", snapshotId);

        Assert.NotNull(response);
        Assert.Single(response!.Applied, entry => entry.Key == "a");
        Assert.Equal("\"one\"", _service.GetAll("u1", "").Settings.Single().Value.Json);

        // No deletions were recorded by the restore, in any profile.
        Assert.Empty(_db.GetDeletedSettings("u1", preRestoreHead, JellyPlayDatabase.BaseProfile));
        Assert.Empty(_db.GetDeletedSettings("u1", preRestoreHead, "tv"));
    }

    [Fact]
    public void RestoreSnapshot_OverlappingRowStampedIntoTheFuture_SnapshotValueStillWins()
    {
        // A live row may carry a client stamp up to the skew ceiling into the
        // future; the re-apply must be stamped past IT, or the snapshot value
        // silently loses LWW for that key (tombstone-everything never lost
        // this race — the restore must not either).
        var at = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var serverNow = at.ToUnixTimeMilliseconds();
        var service = SettingsServiceFactory.Create(_db, new SseHub(NullLogger<SseHub>.Instance), clock: new FixedTimeProvider(at));

        service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "a", 1, "\"keep\"") });
        var snapshotId = _snapshots.Create("u1", "manual")!.Value;
        // ~4 minutes ahead — inside the clamp, so the hijack applies.
        service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "a", serverNow + 4 * 60 * 1000, "\"hijacked\"") });

        var response = service.RestoreSnapshot("u1", snapshotId);

        Assert.NotNull(response);
        Assert.Single(response!.Applied, entry => entry.Key == "a"); // neither stale-write nor clock-skew
        Assert.Empty(response!.Rejected);
        Assert.Equal("\"keep\"", service.GetAll("u1", "").Settings.Single(entry => entry.Key == "a").Value.Json);
    }

    [Fact]
    public void RestoreSnapshot_OverlappingRowAtTheSkewCeiling_SnapshotValueStillWins()
    {
        // The adversarial edge of the future-stamp case: the overlapping live
        // row sits EXACTLY at the skew ceiling (same server millisecond), so
        // the restore's stamp is ceiling + 1 — beyond the client clamp. The
        // restore is server-initiated and bounded by its own server stamp: it
        // must still apply (no clock-skew, no stale-write) and the snapshot
        // value must win, not silently restore nothing.
        var at = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var serverNow = at.ToUnixTimeMilliseconds();
        var service = SettingsServiceFactory.Create(_db, new SseHub(NullLogger<SseHub>.Instance), clock: new FixedTimeProvider(at));

        service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "a", 1, "\"keep\"") });
        var snapshotId = _snapshots.Create("u1", "manual")!.Value;
        // Exactly AT the 5-minute clamp ceiling — the furthest stamp the clamp admits.
        service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "a", serverNow + SettingsService.MaxClockSkewMilliseconds, "\"hijacked\"") });

        var response = service.RestoreSnapshot("u1", snapshotId);

        Assert.NotNull(response);
        Assert.Single(response!.Applied, entry => entry.Key == "a");
        Assert.Empty(response!.Rejected); // in particular: no "clock-skew" rejects
        Assert.Equal("\"keep\"", service.GetAll("u1", "").Settings.Single(entry => entry.Key == "a").Value.Json);
    }

    [Fact]
    public void RestoreSnapshot_ForeignOrUnknownId_IsNull()
    {
        _service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "a", 1) });
        _snapshots.Create("u1", "manual");

        Assert.Null(_service.RestoreSnapshot("u1", 999));
        Assert.Empty(_snapshots.List("u2")); // snapshots are owner-scoped
    }

    [Fact]
    public void RestoreSnapshot_TombstonesPerProfile_DelRowsCarryTheirOwnProfile()
    {
        // A snapshot spanning the base profile AND a device profile.
        _service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "a", 1, "\"one\"") });
        _service.ApplyBatch("u1", "tv", "d1", new[] { Dto("ui", "layout", 1, "\"tv\"") });
        var snapshotId = _snapshots.Create("u1", "manual")!.Value;

        // Drift after the capture: both keys overwritten, plus a tv-only key
        // the snapshot does not hold (it must stay gone after the restore).
        _service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "a", 2, "\"CHANGED\"") });
        _service.ApplyBatch("u1", "tv", "d1", new[] { Dto("ui", "layout", 2, "\"CHANGED\""), Dto("ui", "extra", 2, "\"drift\"") });
        var preRestoreHead = _service.GetAll("u1", "").Head;

        var response = _service.RestoreSnapshot("u1", snapshotId);

        Assert.NotNull(response);
        // The wire response folds every profile's re-apply batch: applied[]
        // carries BOTH profiles' keys (not just the last batch's), and the
        // head is the final change-log head.
        Assert.Contains(response!.Applied, entry => entry.Key == "a"); // base profile
        Assert.Contains(response!.Applied, entry => entry.Key == "layout"); // tv profile
        Assert.Equal(2, response!.Applied.Count); // the tv-only drift key is not re-applied
        Assert.Equal(_db.GetChangeLogHead("u1"), response!.Head);

        Assert.Equal("\"one\"", _service.GetAll("u1", "").Settings.Single(entry => entry.Key == "a").Value.Json);
        var tv = _service.GetAll("u1", "tv");
        Assert.Single(tv.Settings, entry => entry.Key == "layout"); // extra stayed gone

        // The restore's change-log range attributes each tombstone to the
        // profile its key lives in — no base-profile 'del' rows for tv keys.
        var rows = _db.GetChangeLogRange("u1", preRestoreHead, _service.GetAll("u1", "").Head, 200);
        Assert.DoesNotContain(rows, row => row.Profile == JellyPlayDatabase.BaseProfile && row.Key == "layout");
        Assert.DoesNotContain(rows, row => row.Profile == JellyPlayDatabase.BaseProfile && row.Key == "extra");
        Assert.Contains(rows, row => row.Profile == "tv" && row.Key == "layout");

        // The still-absent tv-only key is tombstoned under tv, not base.
        var deleted = _db.GetDeletedSettings("u1", preRestoreHead, "tv")
            .Concat(_db.GetDeletedSettings("u1", preRestoreHead, JellyPlayDatabase.BaseProfile))
            .ToList();
        Assert.Equal("tv", deleted.Single(key => key.Key == "extra").Profile);
    }

    [Fact]
    public void SetDeviceProfile_CapturesProfileCopyRestorePoint_AdminPushCapturesAdminPush()
    {
        _service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "theme", 1, "\"base\"") });

        _service.SetDeviceProfile("u1", "tv", "d1", new[] { Dto("ui", "theme", 2, "\"tv\"") });
        Assert.Single(_snapshots.List("u1"), row => row.Origin == "profile-copy");

        var snapshots = new SnapshotService(_db, () => new Configuration.SyncConfig());
        var admin = new Services.Admin.AdminDefaultsService(
            _service,
            _db,
            snapshots,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Services.Admin.AdminDefaultsService>.Instance);
        _service.SetAdminDefaults(
            SettingsService.GlobalDefaultsScope,
            JsonDocument.Parse("{\"player/skip\":{\"mode\":\"forced\",\"value\":15}}").RootElement);
        admin.PushDefaults("u1");

        Assert.Single(_snapshots.List("u1"), row => row.Origin == "admin-push");
        // Manual captures work too.
        Assert.NotNull(_snapshots.Create("u1", "manual"));
        Assert.Single(_snapshots.List("u1"), row => row.Origin == "manual");
    }

    // ------------------------------------------------------------------
    // Export / import
    // ------------------------------------------------------------------

    [Fact]
    public void Export_CarriesAllProfilesModesAndCatalogStamp()
    {
        _service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "theme", 1, "\"base\"") });
        _service.ApplyBatch("u1", "tv", "d1", new[] { Dto("ui", "layout", 1, "\"tv\"") });
        _service.SetAdminDefaults(
            SettingsService.GlobalDefaultsScope,
            JsonDocument.Parse("{\"ui/theme\":{\"mode\":\"forced\",\"value\":\"dark\"}}").RootElement);

        var bundle = _service.Export("u1");

        Assert.Equal(Services.Settings.ClientSettingsCatalog.CatalogSchema, bundle.CatalogSchema);
        Assert.True(bundle.CatalogSettings > 0);
        Assert.Equal(2, bundle.Profiles.Count);
        var tv = bundle.Profiles.Single(profile => profile.Profile == "tv");
        Assert.Single(tv.Settings, entry => entry.Key == "layout");
        Assert.Equal("forced", bundle.Modes[""][ "ui/theme"]); // provenance travels with the bundle

        // The export contains the USER's stored value, not the forced default.
        Assert.Equal("\"base\"", bundle.Profiles.Single(profile => profile.Profile == "").Settings.Single(entry => entry.Key == "theme").Value.Json);
    }

    [Fact]
    public void Import_ReappliesWithServerNowTimestamps()
    {
        var donor = SettingsServiceFactory.Create(_db, new SseHub(NullLogger<SseHub>.Instance));
        donor.ApplyBatch("donor", "", "d1", new[] { Dto("ui", "theme", 1, "\"exported\"") });
        donor.ApplyBatch("donor", "tv", "d1", new[] { Dto("ui", "layout", 1, "\"tv\"") });
        var bundle = donor.Export("donor");

        var response = _service.Import("u1", "d9", bundle);

        Assert.Equal(2, response.Applied.Count);
        var baseAll = _service.GetAll("u1", "");
        Assert.Equal("\"exported\"", baseAll.Settings.Single(entry => entry.Key == "theme").Value.Json);
        Assert.Equal("d9", baseAll.Settings.Single(entry => entry.Key == "theme").DeviceId);
        Assert.NotEqual(1, baseAll.Settings.Single(entry => entry.Key == "theme").UpdatedAt); // server-stamped, not the donor's ts
        Assert.Equal("\"tv\"", _service.GetAll("u1", "tv").Settings.Single().Value.Json);
    }
}
