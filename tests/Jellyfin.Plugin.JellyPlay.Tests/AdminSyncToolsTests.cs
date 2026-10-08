using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.JellyPlay.Realtime;
using Jellyfin.Plugin.JellyPlay.Services.Admin;
using Jellyfin.Plugin.JellyPlay.Services.Devices;
using Jellyfin.Plugin.JellyPlay.Services.Settings;
using Jellyfin.Plugin.JellyPlay.Storage;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.JellyPlay.Tests;

/// <summary>
/// Phase 4 admin surfaces: the resolved-settings preview (pure), the push
/// dry-run (pure), the audit export (json + csv), the admin live-monitor
/// stream (emission) and the per-user drill-down fold — plus the elevation
/// pin over every new admin action's controller.
/// </summary>
public sealed class AdminPreviewTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "jellyplay-admin-preview-" + Guid.NewGuid().ToString("N"));
    private readonly JellyPlayDatabase _db;
    private readonly SnapshotService _snapshots;
    private readonly SettingsService _service;

    public AdminPreviewTests()
    {
        Directory.CreateDirectory(_tempDir);
        _db = new JellyPlayDatabase(_tempDir);
        _snapshots = new SnapshotService(_db, () => new Configuration.SyncConfig());
        _service = new SettingsService(_db, new SseHub(NullLogger<SseHub>.Instance), () => new Configuration.SyncConfig(), NullLogger<SettingsService>.Instance, _snapshots);
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

    private static Api.SettingsWriteDto Dto(string ns, string key, long at, string json = "\"v\"")
        => new() { Ns = ns, Key = key, SchemaVersion = 1, UpdatedAt = at, Value = JsonDocument.Parse(json).RootElement };

    [Fact]
    public void Preview_PureResolve_ModesAndNoWrites()
    {
        // The user's own row plus a forced and a suggested default: the
        // preview is exactly what the user's resolved endpoint serves.
        _service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "theme", 1, "\"user\"") });
        _service.SetAdminDefaults(
            SettingsService.GlobalDefaultsScope,
            JsonDocument.Parse(
                "{\"ui/theme\":{\"mode\":\"forced\",\"value\":\"dark\"},\"player/skip\":{\"mode\":\"suggested\",\"value\":15}}")
                .RootElement);
        var headBefore = _db.GetChangeLogHead("u1");
        var historyBefore = _db.GetSyncHistory("u1", 0, 50).Count;

        var preview = _service.ResolveProfile("u1", "");

        Assert.Equal("\"dark\"", preview.Settings.Single(entry => entry.Key == "theme").Value.GetRawText());
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["ui/theme"] = "forced",
                ["player/skip"] = "suggested"
            },
            preview.Modes);

        // Pure read: no rows beyond the user's own, no history, no snapshots,
        // the head untouched.
        var (keys, bytes) = _db.GetUserFootprint("u1");
        Assert.Equal(1, keys);
        Assert.True(bytes > 0);
        Assert.Equal(headBefore, _db.GetChangeLogHead("u1"));
        Assert.Equal(historyBefore, _db.GetSyncHistory("u1", 0, 50).Count);
        Assert.Empty(_snapshots.List("u1"));

        // And a user with NO rows previews cleanly (defaults only) without
        // creating anything.
        var empty = _service.ResolveProfile("ghost", "");
        Assert.Single(empty.Settings, entry => entry.Key == "skip");
        Assert.Equal((0, 0L), _db.GetUserFootprint("ghost"));
        Assert.Empty(_db.GetSyncHistory("ghost", 0, 50));
    }
}

public sealed class AdminPushDryRunTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "jellyplay-admin-dryrun-" + Guid.NewGuid().ToString("N"));
    private readonly JellyPlayDatabase _db;
    private readonly SnapshotService _snapshots;
    private readonly SettingsService _service;
    private readonly AdminDefaultsService _admin;

    public AdminPushDryRunTests()
    {
        Directory.CreateDirectory(_tempDir);
        _db = new JellyPlayDatabase(_tempDir);
        _snapshots = new SnapshotService(_db, () => new Configuration.SyncConfig());
        _service = new SettingsService(_db, new SseHub(NullLogger<SseHub>.Instance), () => new Configuration.SyncConfig(), NullLogger<SettingsService>.Instance, _snapshots);
        _admin = new AdminDefaultsService(
            _service,
            _db,
            _snapshots,
            NullLogger<AdminDefaultsService>.Instance);
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

    private static Api.SettingsWriteDto Dto(string ns, string key, long at, string json = "\"v\"")
        => new() { Ns = ns, Key = key, SchemaVersion = 1, UpdatedAt = at, Value = JsonDocument.Parse(json).RootElement };

    [Fact]
    public void DryRun_ReportsWouldApplyWouldReject_AndWritesNothing()
    {
        // u1's stored value is older than any push stamp → the forced default
        // would apply; u2's is stamped far in the future → the push would
        // lose LWW and reject stale-write.
        _service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "theme", 1, "\"user\"") });
        _db.UpsertSettings(
            "u2",
            JellyPlayDatabase.BaseProfile,
            new[] { new Storage.Models.SettingWrite("ui", "theme", 1, DateTimeOffset.MaxValue.ToUnixTimeMilliseconds(), "d1", System.Text.Encoding.UTF8.GetBytes("\"future\"")) },
            new JellyPlayDatabase.Quotas(1024, 4096, 100));
        _service.SetAdminDefaults(
            SettingsService.GlobalDefaultsScope,
            JsonDocument.Parse("{\"ui/theme\":{\"mode\":\"forced\",\"value\":\"dark\"}}").RootElement);
        var headBefore = _db.GetChangeLogHead("u1");
        var historyBefore = Math.Max(_db.GetSyncHistory("u1", 0, 50).Count, _db.GetSyncHistory("u2", 0, 50).Count);

        var outcome = _admin.PushDefaults(null, dryRun: true);

        Assert.Equal(2, outcome.Users);
        Assert.Equal(0, outcome.KeysPushed); // a dry run never pushes
        Assert.NotNull(outcome.DryRun);
        Assert.Equal(1, outcome.DryRun!.WouldApply);
        Assert.Equal(1, outcome.DryRun.WouldReject);
        var reject = Assert.Single(outcome.DryRun.WouldRejects);
        Assert.Equal(("u2", "ui", "theme", "stale-write"), (reject.UserId, reject.Ns, reject.Key, reject.Reason));
        Assert.Empty(outcome.DryRun.Problems);

        // NOTHING was written: both users keep their stored values, no
        // restore points, no NEW history rows, the head untouched.
        Assert.Equal("\"user\"", _service.GetAll("u1", "").Settings.Single().Value.GetRawText());
        Assert.Equal("\"future\"", _service.GetAll("u2", "").Settings.Single().Value.GetRawText());
        Assert.Empty(_snapshots.List("u1"));
        Assert.Empty(_snapshots.List("u2"));
        Assert.Equal(historyBefore, _db.GetSyncHistory("u1", 0, 50).Count);
        Assert.Empty(_db.GetSyncHistory("u2", 0, 50));
        Assert.Equal(headBefore, _db.GetChangeLogHead("u1"));

        // The real push still works and reports no dry-run payload.
        var pushed = _admin.PushDefaults(null);
        Assert.Equal(1, pushed.KeysPushed);
        Assert.Null(pushed.DryRun);
        Assert.Equal("\"dark\"", _service.GetAll("u1", "").Settings.Single().Value.GetRawText());
        Assert.Single(_snapshots.List("u1"), row => row.Origin == "admin-push");
    }

    [Fact]
    public void DryRun_FlagsCatalogProblemsInTheStoredDefaults()
    {
        // SetAdminDefaults itself refuses bad payloads, so a stored broken
        // map can only exist via the raw write (an older plugin, a restore).
        // The dry run must surface the problem instead of failing the push.
        _db.SetAdminDefaults(
            SettingsService.GlobalDefaultsScope,
            System.Text.Encoding.UTF8.GetBytes("{\"prefs/blue_light_filter_strength\":{\"mode\":\"forced\",\"value\":\"oops\"}}"),
            updatedAt: 1);

        var outcome = _admin.PushDefaults("u1", dryRun: true);

        var problem = Assert.Single(outcome.DryRun!.Problems);
        Assert.Contains("prefs/blue_light_filter_strength", problem, StringComparison.Ordinal);
        Assert.Equal(1, outcome.DryRun.WouldApply); // the (bad) forced entry is still what a push would write
    }
}

public sealed class AuditExportTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "jellyplay-admin-audit-" + Guid.NewGuid().ToString("N"));
    private readonly JellyPlayDatabase _db;
    private readonly SettingsService _service;

    public AuditExportTests()
    {
        Directory.CreateDirectory(_tempDir);
        _db = new JellyPlayDatabase(_tempDir);
        _service = new SettingsService(_db, new SseHub(NullLogger<SseHub>.Instance), () => new Configuration.SyncConfig(), NullLogger<SettingsService>.Instance, new SnapshotService(_db, () => new Configuration.SyncConfig()));
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

    private static Api.SettingsWriteDto Dto(string ns, string key, long at)
        => new() { Ns = ns, Key = key, SchemaVersion = 1, UpdatedAt = at, Value = JsonDocument.Parse("\"x\"").RootElement };

    private SyncInsightsService Insights => new(_db, () => new Configuration.SyncConfig());

    [Fact]
    public void ExportAudit_FoldsHistoryWithKeyDiffs_NewestFirstAndUserIsolated()
    {
        var first = _service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "a", 1), Dto("ui", "b", 2) });
        _service.ResetNamespace("u1", "", "ui", "d1");
        _service.ApplyBatch("u2", "", "d9", new[] { Dto("ui", "x", 1) });

        var export = Insights.ExportAudit("u1", 200);

        Assert.Equal("u1", export.UserId);
        Assert.True(export.ExportedAt > 0);
        Assert.Equal(2, export.History.Count); // the reset + the first push; u2's push never leaks

        var reset = export.History[0]; // newest-first
        Assert.Equal("reset", reset.Op);
        Assert.Equal(new[] { ("ui", "b"), ("ui", "a") }, reset.Keys.Select(key => (key.Ns, key.Key)).ToList());

        var push = export.History[1];
        Assert.Equal("push", push.Op);
        Assert.Equal(2, push.KeysApplied);
        Assert.Equal(new[] { ("ui", "b"), ("ui", "a") }, push.Keys.Select(key => (key.Ns, key.Key)).ToList());
        Assert.Equal(first.Head, push.ToSeq);
        Assert.Null(push.Rejects);

        // An operation recorded with rejections carries them in the export.
        _service.ApplyBatch("u1", "", "d2", new[] { new Api.SettingsWriteDto { Ns = "ui", Key = "zz", UpdatedAt = 1, Deleted = true } });
        _service.ApplyBatch("u1", "", "d3", new[] { Dto("ui", "zz", 1) }); // equal ts → stale
        var withRejects = Insights.ExportAudit("u1", 1).History.Single();
        Assert.Equal(1, withRejects.KeysRejected);
        var reject = Assert.Single(withRejects.Rejects!);
        Assert.Equal("stale-write", reject.Reason);
    }

    [Fact]
    public void ExportAudit_LimitClampsAndZeroMeansDefault()
    {
        for (var index = 0; index < 4; index++)
        {
            _db.InsertSyncHistory("u1", "d1", "push", 0, 0, 0, null, 1000 + index);
        }

        Assert.Equal(2, Insights.ExportAudit("u1", 2).History.Count);
        Assert.Equal(4, Insights.ExportAudit("u1", 100_000).History.Count); // max is 1000, range smaller
        Assert.Equal(4, Insights.ExportAudit("u1", 0).History.Count); // 0 → default (200), not "nothing"
    }

    [Fact]
    public void SyncAuditCsv_HeaderRowsQuotingAndZeroWidthEntries()
    {
        var export = new Api.AuditExportResponse(
            "u1",
            500,
            new[]
            {
                // A normal entry with two diff keys (one needing quoting).
                new Api.AuditEntryDto(7, 1000, "d1", "push", 2, 0, null, 0, 7, new[]
                {
                    new Api.SyncHistoryKeyDto("ui", "plain", 5),
                    new Api.SyncHistoryKeyDto("ui", "a,\"x\"", 6)
                }),
                // A zero-width range (no-op push / pre-v7 row): still one row.
                new Api.AuditEntryDto(8, 2000, "d2", "pull", 0, 0, null, null, null, Array.Empty<Api.SyncHistoryKeyDto>())
            });

        var csv = SyncAuditCsv.Build(export);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).ToList();

        Assert.Equal(SyncAuditCsv.Header, lines[0]);
        Assert.Equal(4, lines.Count); // header + 2 diff keys + 1 zero-width row
        Assert.Contains("7,1000,d1,push,2,0,0,7,ui,plain,5", lines[1], StringComparison.Ordinal);
        Assert.Contains("7,1000,d1,push,2,0,0,7,ui,\"a,\"\"x\"\"\",6", lines[2], StringComparison.Ordinal);

        var zeroWidth = lines[3].Split(',');
        Assert.Equal(11, zeroWidth.Length);
        Assert.Equal("8", zeroWidth[0]);
        Assert.Equal("pull", zeroWidth[3]);
        Assert.Equal(string.Empty, zeroWidth[8]); // ns empty
        Assert.Equal(string.Empty, zeroWidth[10]); // keyUpdatedAt empty
    }
}

public sealed class AdminLiveMonitorTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "jellyplay-admin-live-" + Guid.NewGuid().ToString("N"));
    private readonly JellyPlayDatabase _db;
    private readonly SseHub _hub;
    private readonly SettingsService _service;

    public AdminLiveMonitorTests()
    {
        Directory.CreateDirectory(_tempDir);
        _db = new JellyPlayDatabase(_tempDir);
        _hub = new SseHub(NullLogger<SseHub>.Instance);
        _service = new SettingsService(_db, _hub, () => new Configuration.SyncConfig(), NullLogger<SettingsService>.Instance, new SnapshotService(_db, () => new Configuration.SyncConfig()));
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

    private static Api.SettingsWriteDto Dto(string ns, string key, long at)
        => new() { Ns = ns, Key = key, SchemaVersion = 1, UpdatedAt = at, Value = JsonDocument.Parse("\"x\"").RootElement };

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Nothing = TimeSpan.FromMilliseconds(200);

    [Fact]
    public async Task SettingsOperations_EmitSyncOpsToTheAdminStream()
    {
        var monitor = _hub.Subscribe("an-admin", SseHub.AdminStream);
        var client = _hub.Subscribe("u1", "settings");

        _service.ApplyBatch("u1", "", "d1", new[] { Dto("ui", "a", 1), Dto("ui", "b", 2) });
        var push = await _hub.WaitForEventAsync(monitor, Wait, CancellationToken.None);
        Assert.NotNull(push);
        Assert.Equal("sync.op", push!.EventName);
        using (var doc = JsonDocument.Parse(push.Data))
        {
            var root = doc.RootElement;
            Assert.Equal("sync.op", root.GetProperty("type").GetString());
            Assert.Equal("push", root.GetProperty("op").GetString());
            Assert.Equal("u1", root.GetProperty("userId").GetString());
            Assert.Equal("d1", root.GetProperty("deviceId").GetString());
            Assert.Equal(2, root.GetProperty("keysApplied").GetInt32());
            Assert.Equal(0, root.GetProperty("keysRejected").GetInt32());
            Assert.True(root.GetProperty("ts").GetInt64() > 0);
        }

        // The settings stream still carries its own anchored event — the
        // admin fan-out never replaces it.
        var settingsEvent = await _hub.WaitForEventAsync(client, Wait, CancellationToken.None);
        Assert.Equal("settings.changed", settingsEvent!.EventName);

        _service.GetChanged("u1", "", 0, "d2");
        var pull = await _hub.WaitForEventAsync(monitor, Wait, CancellationToken.None);
        Assert.Equal("pull", JsonDocument.Parse(pull!.Data).RootElement.GetProperty("op").GetString());

        _service.ResetNamespace("u1", "", "ui", "d1");
        var reset = await _hub.WaitForEventAsync(monitor, Wait, CancellationToken.None);
        Assert.Equal("reset", JsonDocument.Parse(reset!.Data).RootElement.GetProperty("op").GetString());

        // The wipe only records (and only emits) when it actually removed
        // rows — the reset above already tombstoned d1's keys, so give d1 a
        // fresh row in another namespace first. That fresh push emits its own
        // sync.op, which the monitor consumes before the wipe's.
        _service.ApplyBatch("u1", "", "d1", new[] { Dto("player", "c", 3) });
        var interimPush = await _hub.WaitForEventAsync(monitor, Wait, CancellationToken.None);
        Assert.Equal("push", JsonDocument.Parse(interimPush!.Data).RootElement.GetProperty("op").GetString());
        _service.WipeDevice("u1", "d1");
        var wipe = await _hub.WaitForEventAsync(monitor, Wait, CancellationToken.None);
        Assert.Equal("wipe", JsonDocument.Parse(wipe!.Data).RootElement.GetProperty("op").GetString());

        _hub.Unsubscribe(monitor);
        _hub.Unsubscribe(client);
    }

    [Fact]
    public async Task AdminStream_IsDistinctFromClientStreams_AndBroadcastsAcrossUsers()
    {
        var adminA = _hub.Subscribe("admin-a", SseHub.AdminStream);
        var adminB = _hub.Subscribe("admin-b", SseHub.AdminStream);
        var client = _hub.Subscribe("u1", "settings");

        // A user-targeted settings event must NOT reach the admin stream…
        _hub.PublishToUser("settings", "u1", "settings.changed", "{}", 1);
        var none = await _hub.WaitForEventAsync(adminA, Nothing, CancellationToken.None);
        Assert.Null(none);

        // …and the admin fan-out is a broadcast: every elevated subscriber
        // sees it, whatever their own user id.
        var delivered = _hub.PublishAll(SseHub.AdminStream, "sync.op", "{\"type\":\"sync.op\"}");
        Assert.Equal(2, delivered);
        Assert.NotNull(await _hub.WaitForEventAsync(adminA, Wait, CancellationToken.None));
        Assert.NotNull(await _hub.WaitForEventAsync(adminB, Wait, CancellationToken.None));

        // The events replay ring stays an events-stream feature; the admin
        // stream is live-only.
        Assert.Empty(_hub.ReplayEvents("admin-a", 0));

        _hub.Unsubscribe(adminA);
        _hub.Unsubscribe(adminB);
        _hub.Unsubscribe(client);
    }
}

public sealed class AdminDrilldownTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "jellyplay-admin-drill-" + Guid.NewGuid().ToString("N"));
    private readonly JellyPlayDatabase _db;

    public AdminDrilldownTests()
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

    [Fact]
    public void Drilldown_FoldsStatusDevicesAndNames()
    {
        var userGuid = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var userId = userGuid.ToString();
        _db.UpsertSettings(
            userId,
            JellyPlayDatabase.BaseProfile,
            new[] { new Storage.Models.SettingWrite("ui", "a", 1, 100, "d1", System.Text.Encoding.UTF8.GetBytes("\"12345\"")) },
            new JellyPlayDatabase.Quotas(1024, 4096, 100));
        _db.InsertSyncHistory(userId, "d1", "push", 1, 0, 5, null, 1000);

        var devices = new DeviceRegistryService(_db, () => new Configuration.PushConfig());
        Assert.Equal(RegisterDeviceOutcome.Registered, devices.Register(new DeviceRegistration(userId, "d1", "Phone", "android", "1.0", null, Model: "Pixel", Caps: new[] { "silent-push" })));
        Assert.Equal(RegisterDeviceOutcome.Registered, devices.Register(new DeviceRegistration(userId, "d2", "TV", "tv", "2.0", null)));
        Assert.True(devices.Revoke(userId, "d2"));

        var insights = new SyncInsightsService(_db, () => new Configuration.SyncConfig());
        var drilldown = insights.GetAdminUserDrilldown(userId, guid => guid == userGuid ? "Alice" : null);

        Assert.Equal(userId, drilldown.UserId);
        Assert.Equal("Alice", drilldown.UserName);
        Assert.Equal(_db.GetChangeLogHead(userId), drilldown.Status.Head);
        Assert.Equal(1, drilldown.Status.Keys);
        Assert.Equal(7, drilldown.Status.Bytes); // "\"12345\"" serialized

        Assert.Equal(2, drilldown.Devices.Count);
        var phone = drilldown.Devices.Single(device => device.DeviceId == "d1");
        Assert.Equal(("Phone", "android", "Pixel", false), (phone.Name, phone.Platform, phone.Model, phone.Revoked));
        Assert.Single(phone.Caps!, cap => cap == "silent-push");
        Assert.True(drilldown.Devices.Single(device => device.DeviceId == "d2").Revoked);

        // The ADMIN drill-down type carries no push endpoint field — the
        // registry's owner-only secret can never leak through it.
        Assert.DoesNotContain(drilldown.Devices, device => device.GetType().GetProperties().Any(property => property.Name.Contains("Endpoint", StringComparison.Ordinal)));
    }

    [Fact]
    public void AdminRevokeThenWipe_MatchesTheOwnerPath()
    {
        var userId = "22222222-2222-2222-2222-222222222222";
        var service = new SettingsService(_db, new SseHub(NullLogger<SseHub>.Instance), () => new Configuration.SyncConfig(), NullLogger<SettingsService>.Instance, new SnapshotService(_db, () => new Configuration.SyncConfig()));
        var devices = new DeviceRegistryService(_db, () => new Configuration.PushConfig(), service);
        // A v7 device (caps registered): its DELETE is revoke + wipe.
        Assert.Equal(RegisterDeviceOutcome.Registered, devices.Register(new DeviceRegistration(userId, "d1", "Phone", "android", "1.0", null, Caps: new[] { "silent-push" })));
        service.ApplyBatch(userId, "", "d1", new[]
        {
            new Api.SettingsWriteDto { Ns = "ui", Key = "a", SchemaVersion = 1, UpdatedAt = 1, Value = JsonDocument.Parse("\"x\"").RootElement },
            new Api.SettingsWriteDto { Ns = "ui", Key = "b", SchemaVersion = 1, UpdatedAt = 2, Value = JsonDocument.Parse("\"x\"").RootElement }
        });

        // The admin route and the owner's DELETE devices/{id} share one
        // orchestration: caps-gated registry revoke + data wipe.
        Assert.Equal(DeleteDeviceOutcome.Revoked, devices.RevokeAndWipe(userId, "d1"));

        Assert.Empty(service.GetAll(userId, "").Settings);
        var wipe = Assert.Single(_db.GetSyncHistory(userId, 0, 50), row => row.Op == "wipe");
        Assert.Equal(2, wipe.KeysApplied);
        Assert.Equal("d1", wipe.DeviceId);
        Assert.Equal("device-revoked", Assert.Single(service.ApplyBatch(userId, "", "d1", new[]
        {
            new Api.SettingsWriteDto { Ns = "ui", Key = "a", SchemaVersion = 1, UpdatedAt = 9, Value = JsonDocument.Parse("\"x\"").RootElement }
        }).Rejected).Reason);
    }
}

/// <summary>
/// The elevation pin: every Phase 4 admin action lives on a controller whose
/// class-level authorization policy is RequiresElevation — if an action
/// migrates to a controller without it, this fails before the host ever
/// serves the route.
/// </summary>
public sealed class AdminSurfaceElevationTests
{
    private static Type Controller(string name)
        => typeof(JellyPlayContract).Assembly.GetTypes()
            .Single(type => type.Name == name && typeof(ControllerBase).IsAssignableFrom(type) && !type.IsAbstract);

    private static void AssertElevated(string controllerName, string actionName)
    {
        var controller = Controller(controllerName);
        var authorize = controller.GetCustomAttribute<AuthorizeAttribute>(inherit: true);
        Assert.NotNull(authorize);
        Assert.Equal(Policies.RequiresElevation, authorize!.Policy);

        // The action really exists and really is a served HTTP endpoint.
        var action = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .SingleOrDefault(method => method.Name == actionName
                && method.GetCustomAttributes<HttpMethodAttribute>(inherit: true).Any());
        Assert.True(action is not null, $"{controllerName}.{actionName} is not an HTTP action");
    }

    [Theory]
    [InlineData("AdminController", "PushDefaults")]
    [InlineData("AdminController", "PreviewResolved")]
    [InlineData("AdminController", "GetUsers")]
    [InlineData("AdminController", "Stream")]
    [InlineData("SyncAdminController", "GetOverview")]
    [InlineData("SyncAdminController", "GetDrilldown")]
    [InlineData("SyncAdminController", "RevokeDevice")]
    [InlineData("SyncAdminController", "ExportAudit")]
    public void AdminActions_SitBehindRequiresElevation(string controllerName, string actionName)
        => AssertElevated(controllerName, actionName);
}
