using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Api;
using Jellyfin.Plugin.JellyPlay.Configuration;
using Jellyfin.Plugin.JellyPlay.Realtime;
using Jellyfin.Plugin.JellyPlay.Services.Admin;
using Jellyfin.Plugin.JellyPlay.Services.Devices;
using Jellyfin.Plugin.JellyPlay.Services.Events;
using Jellyfin.Plugin.JellyPlay.Services.Messages;
using Jellyfin.Plugin.JellyPlay.Services.Push;
using Jellyfin.Plugin.JellyPlay.Storage;
using Jellyfin.Plugin.JellyPlay.Storage.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Jellyfin.Plugin.JellyPlay.Tests;

/// <summary>
/// Shared fake push transport: records every call (endpoint + body) and can
/// simulate transport failures per call. <see cref="WaitAsync"/> makes the
/// fire-and-forget fan-out deterministic in tests.
/// </summary>
internal sealed class PushRecording
{
    public ConcurrentQueue<string> Endpoints { get; } = new();

    public ConcurrentQueue<string> Bodies { get; } = new();

    public int Count;

    /// <summary>perCall receives the zero-based call index and may throw to simulate failures.</summary>
    public PushSender Sender(Func<int, Task<HttpResponseMessage>>? perCall = null)
        => async (request, cancellationToken) =>
        {
            var index = Interlocked.Increment(ref Count) - 1;
            Endpoints.Enqueue(request.RequestUri?.ToString() ?? string.Empty);
            Bodies.Enqueue(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            return perCall is null
                ? new HttpResponseMessage(HttpStatusCode.OK)
                : await perCall(index);
        };

    public async Task WaitAsync(int expectedCalls, int timeoutMs = 5000)
    {
        var start = DateTime.UtcNow;
        while (Volatile.Read(ref Count) < expectedCalls)
        {
            if ((DateTime.UtcNow - start).TotalMilliseconds > timeoutMs)
            {
                throw new TimeoutException($"push fan-out delivered {Volatile.Read(ref Count)}/{expectedCalls} in time");
            }

            await Task.Delay(20);
        }
    }
}

// ---------------------------------------------------------------------------
// Schema v4: devices gain PushKind / PushEndpoint / CreatedAt
// ---------------------------------------------------------------------------

/// <summary>Push wave: the v3 → v4 migration adds push columns without touching stored data, idempotently.</summary>
public sealed class PushMigrationTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "jellyplay-push-mig-" + Guid.NewGuid().ToString("N"));

    public PushMigrationTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string DbPath => Path.Combine(_tempDir, "plugins", "JellyPlay", "jellyplay_plugin.db");

    private static List<string> DeviceColumns(string dbPath)
    {
        using var connection = new SqliteConnection($"Filename={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "pragma table_info(devices)";
        var names = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(1));
        }

        return names;
    }

    [Fact]
    public void FreshDatabase_IsAtV4_WithPushColumns()
    {
        using var db = new JellyPlayDatabase(_tempDir);

        Assert.Equal(JellyPlayDatabase.CurrentSchemaVersion, db.UserVersion);
        var columns = DeviceColumns(DbPath);
        Assert.Contains("PushKind", columns);
        Assert.Contains("PushEndpoint", columns);
        Assert.Contains("CreatedAt", columns);
        Assert.True(db.CheckIntegrity().IntegrityOk);
    }

    [Fact]
    public void V3Database_MigratesToV4_PreservesData_AndIsIdempotent()
    {
        string storedSetting;
        using (var first = new JellyPlayDatabase(_tempDir))
        {
            first.UpsertSettings(
                "user1",
                JellyPlayDatabase.BaseProfile,
                new[] { new SettingWrite("ui", "theme", 1, 100, "d1", Encoding.UTF8.GetBytes("\"v\"")) },
                new JellyPlayDatabase.Quotas(1024, 4096, 10));
            storedSetting = Encoding.UTF8.GetString(Assert.Single(first.GetSettings("user1", "")).Value);
            first.UpsertDevice(new DeviceWrite("d1", "user1", "Phone", "android", "1.2.3", 5000));
        }

        // Roll the file back to a v3 state: push columns dropped, user_version 3.
        using (var raw = new SqliteConnection($"Filename={DbPath}"))
        {
            raw.Open();
            using var downgrade = raw.CreateCommand();
            downgrade.CommandText =
                "alter table devices drop column PushKind; alter table devices drop column PushEndpoint; " +
                "alter table devices drop column CreatedAt; " +
                "alter table sync_history drop column FromSeq; alter table sync_history drop column ToSeq; " +
                "pragma user_version = 3;";
            downgrade.ExecuteNonQuery();
        }

        using (var second = new JellyPlayDatabase(_tempDir))
        {
            Assert.Equal(JellyPlayDatabase.CurrentSchemaVersion, second.UserVersion);
            var columns = DeviceColumns(DbPath);
            Assert.Contains("PushKind", columns);
            Assert.Contains("PushEndpoint", columns);
            Assert.Contains("CreatedAt", columns);

            // The v3 data survived untouched; the legacy row has no push registration.
            Assert.Equal(storedSetting, Encoding.UTF8.GetString(Assert.Single(second.GetSettings("user1", "")).Value));
            var device = Assert.Single(second.GetDevices("user1"));
            Assert.Equal(("d1", "Phone", 5000L), (device.DeviceId, device.Name, device.LastSeen));
            Assert.Null(device.PushKind);
            Assert.Null(device.PushEndpoint);

            // And the new columns are immediately usable.
            second.UpsertDevice(new DeviceWrite("d1", "user1", "Phone", "android", "1.2.3", 5001, "ntfy", "https://ntfy.sh/t", 5000));
            Assert.Equal("ntfy", second.GetDeviceById("d1")?.PushKind);
        }

        // Re-open: nothing left to migrate.
        using (var third = new JellyPlayDatabase(_tempDir))
        {
            Assert.Equal(JellyPlayDatabase.CurrentSchemaVersion, third.UserVersion);
            Assert.True(third.CheckIntegrity().IntegrityOk);
        }
    }
}

// ---------------------------------------------------------------------------
// Device-row push persistence + audience queries
// ---------------------------------------------------------------------------

public sealed class PushDeviceStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "jellyplay-push-store-" + Guid.NewGuid().ToString("N"));
    private readonly JellyPlayDatabase _db;

    public PushDeviceStoreTests()
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

    private static DeviceWrite Device(string id, string user, string? kind = "generic", string? endpoint = "https://push.example/hook")
        => new(id, user, "Name " + id, "android", "1.0", 100, kind, endpoint, 90);

    [Fact]
    public void PushRegistration_RoundTrips()
    {
        _db.UpsertDevice(Device("d1", "u1", "ntfy", "https://ntfy.sh/topic"));

        var row = Assert.Single(_db.GetDevices("u1"));
        Assert.Equal(("ntfy", "https://ntfy.sh/topic"), (row.PushKind, row.PushEndpoint));
        Assert.Equal(90, row.CreatedAt);
        Assert.Equal("ntfy", _db.GetDeviceById("d1")?.PushKind);
    }

    [Fact]
    public void PushRegistration_ReplaceAndClear()
    {
        _db.UpsertDevice(Device("d1", "u1", "generic", "https://a.example/x"));
        _db.UpsertDevice(Device("d1", "u1", "ntfy", "https://ntfy.sh/t")); // replace
        Assert.Equal(("ntfy", "https://ntfy.sh/t"), (_db.GetDeviceById("d1")!.PushKind, _db.GetDeviceById("d1")!.PushEndpoint));

        _db.UpsertDevice(Device("d1", "u1", null, null)); // cleared
        Assert.Null(_db.GetDeviceById("d1")!.PushKind);
        Assert.Null(_db.GetDeviceById("d1")!.PushEndpoint);
    }

    [Fact]
    public void GetPushDevices_FiltersByKindSet_AndByUsers()
    {
        _db.UpsertDevice(Device("p1", "u1"));
        _db.UpsertDevice(Device("p2", "u2", "ntfy", "https://ntfy.sh/t2"));
        _db.UpsertDevice(Device("plain", "u3", null, null)); // not push-registered
        _db.UpsertDevice(Device("half", "u3", "generic", "")); // blank endpoint → excluded

        Assert.Equal(2, _db.GetPushDevices(null).Count); // all users
        Assert.Equal(new[] { "p1" }, _db.GetPushDevices(new[] { "u1" }).Select(row => row.DeviceId));
        Assert.Equal(2, _db.GetPushDevices(new[] { "u1", "u2" }).Count);
        Assert.Empty(_db.GetPushDevices(new[] { "nobody" }));
        Assert.Empty(_db.GetPushDevices(Array.Empty<string>())); // empty = nobody (never "all")
        Assert.Equal(2, _db.GetAllPushDevices().Count);
    }
}

// ---------------------------------------------------------------------------
// Registration API surface (POST/GET jellyplay/devices)
// ---------------------------------------------------------------------------

/// <summary>Registration contract: persist/replace/preserve, invalid → 400, endpoints never leak across users.</summary>
public sealed class DeviceRegistrationApiTests : IDisposable
{
    private const string UserA = "11111111-1111-1111-1111-111111111111";
    private const string UserB = "22222222-2222-2222-2222-222222222222";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "jellyplay-push-api-" + Guid.NewGuid().ToString("N"));
    private readonly JellyPlayDatabase _db;

    public DeviceRegistrationApiTests()
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

    private EventsController Controller(string userId, string deviceId)
    {
        var hub = new SseHub(NullLogger<SseHub>.Instance);
        var events = new EventService(
            hub,
            () => new EventsConfig(),
            () => new List<string>(),
            NullLogger<EventService>.Instance);
        var devices = new DeviceRegistryService(_db, () => new PushConfig(), SettingsServiceFactory.Create(_db, hub));
        var controller = new EventsController(hub, events, devices);
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[]
                {
                    new Claim("Jellyfin-UserId", userId),
                    new Claim("Jellyfin-DeviceId", deviceId)
                },
                "Bearer"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        return controller;
    }

    private static DeviceRegistrationRequest Request(string deviceId, string? kind = null, string? endpoint = null, bool detach = false)
    {
        var request = new DeviceRegistrationRequest
        {
            DeviceId = deviceId,
            Name = "Dev " + deviceId,
            Platform = "android",
            AppVersion = "1.0"
        };
        if (detach)
        {
            request.Push = JsonSerializer.SerializeToElement((object?)null);
        }
        else if (kind is not null)
        {
            request.Push = JsonSerializer.SerializeToElement(
                new DevicePushRegistration { Kind = kind, Endpoint = endpoint ?? string.Empty });
        }

        return request;
    }

    private static JArray GetDevicesJson(EventsController controller)
    {
        var result = Assert.IsAssignableFrom<ContentResult>(controller.GetDevices());
        return JArray.Parse(result.Content!);
    }

    [Fact]
    public void Register_WithPush_Persists_AndEchoesToOwner()
    {
        var controller = Controller(UserA, "d1");
        Assert.IsType<NoContentResult>(controller.RegisterDevice(Request("d1", "generic", "https://push.example/hook")));

        var row = Assert.Single(GetDevicesJson(controller));
        Assert.Equal("d1", row["deviceId"]!.ToString());
        Assert.Equal(UserA, row["userId"]!.ToString());
        Assert.Equal("generic", row["push"]!["kind"]!.ToString());
        Assert.Equal("https://push.example/hook", row["push"]!["endpoint"]!.ToString());

        var stored = _db.GetDeviceById("d1");
        Assert.Equal(("generic", "https://push.example/hook"), (stored!.PushKind, stored.PushEndpoint));
        Assert.NotNull(stored.CreatedAt);
    }

    [Fact]
    public void RePost_WithNewPush_Replaces()
    {
        var controller = Controller(UserA, "d1");
        controller.RegisterDevice(Request("d1", "generic", "https://old.example/a"));
        controller.RegisterDevice(Request("d1", "ntfy", "https://ntfy.sh/newtopic"));

        var row = Assert.Single(GetDevicesJson(controller));
        Assert.Equal("ntfy", row["push"]!["kind"]!.ToString());
        Assert.Equal("https://ntfy.sh/newtopic", row["push"]!["endpoint"]!.ToString());
    }

    [Fact]
    public void RePost_WithoutPushBlock_PreservesRegistration()
    {
        var controller = Controller(UserA, "d1");
        controller.RegisterDevice(Request("d1", "ntfy", "https://ntfy.sh/keep"));
        controller.RegisterDevice(Request("d1")); // plain re-registration (e.g. name update)

        var row = Assert.Single(GetDevicesJson(controller));
        Assert.Equal("https://ntfy.sh/keep", row["push"]!["endpoint"]!.ToString());
    }

    [Fact]
    public void RePost_WithExplicitPushNull_Detaches()
    {
        var controller = Controller(UserA, "d1");
        controller.RegisterDevice(Request("d1", "ntfy", "https://ntfy.sh/gone"));
        controller.RegisterDevice(Request("d1", detach: true)); // the client's push-off wire shape

        var row = Assert.Single(GetDevicesJson(controller));
        Assert.Null(row["push"]); // registration cleared...
        var stored = _db.GetDeviceById("d1");
        Assert.NotNull(stored); // ...but the device row survives (unlike DELETE)
        Assert.Null(stored!.PushKind);
        Assert.Null(stored.PushEndpoint);
    }

    [Theory]
    [InlineData("webpush", "https://x.example")] // unsupported kind
    [InlineData("generic", "")] // blank endpoint
    [InlineData("generic", "   ")]
    public void Register_InvalidPush_IsRejected400(string kind, string endpoint)
    {
        var controller = Controller(UserA, "d1");

        var result = controller.RegisterDevice(Request("d1", kind, endpoint));

        // The body is the gate's error shape ({error: "code"}), not a raw object result.
        var badRequest = Assert.IsAssignableFrom<ContentResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, badRequest.StatusCode);
        var body = JObject.Parse(badRequest.Content!);
        Assert.Equal("invalid-push-registration", body["error"]!.ToString());
        Assert.Single(body.Properties());
        Assert.Null(_db.GetDeviceById("d1")); // nothing persisted
    }

    [Fact]
    public void Register_WithoutPush_OmitsField()
    {
        var controller = Controller(UserA, "d1");
        controller.RegisterDevice(Request("d1"));

        var row = Assert.Single(GetDevicesJson(controller));
        Assert.Null(row["push"]); // omitted entirely, not serialized as null
    }

    [Fact]
    public void Devices_NeverEchoAnotherUsersEndpoint()
    {
        Controller(UserA, "dA").RegisterDevice(Request("dA", "generic", "https://alice.example/secret"));
        Controller(UserB, "dB").RegisterDevice(Request("dB", "ntfy", "https://ntfy.sh/bobstopic"));

        var mine = GetDevicesJson(Controller(UserA, "dA"));
        var payload = mine.ToString();

        Assert.Single(mine); // only own rows are listed at all
        Assert.Contains("alice.example/secret", payload); // own endpoint visible
        Assert.DoesNotContain("bobstopic", payload); // other user's endpoint never present
    }
}

// ---------------------------------------------------------------------------
// Payload builders (exact JSON, Newtonsoft-pinned shapes)
// ---------------------------------------------------------------------------

public sealed class PushPayloadTests
{
    [Theory]
    [InlineData("generic", true)]
    [InlineData("ntfy", true)]
    [InlineData("fcm", true)] // structurally valid; FCM-configured gating happens at the API (push-kind-unavailable)
    [InlineData(null, false)]
    [InlineData("GENERIC", false)] // kinds are matched exactly
    [InlineData("webpush", false)] // unknown kinds stay invalid
    public void IsValidRegistration_GatesKindAndEndpoint(string? kind, bool valid)
    {
        Assert.Equal(valid, PushDispatcher.IsValidRegistration(kind, "https://push.example/x"));
        Assert.False(PushDispatcher.IsValidRegistration(kind, " "));
        Assert.False(PushDispatcher.IsValidRegistration(kind, null));
    }

    [Fact]
    public void GenericPayload_ExactShape_WithAndWithoutItemId()
    {
        Assert.Equal(
            "{\"title\":\"Hello\",\"body\":\"World\",\"kind\":\"new-media\",\"itemId\":\"abc123\"}",
            PushDispatcher.BuildGenericPayload(new PushMessage(PushKinds.NewMedia, "Hello", "World", "abc123")));

        Assert.Equal(
            "{\"title\":\"Hello\",\"body\":\"World\",\"kind\":\"message\"}",
            PushDispatcher.BuildGenericPayload(new PushMessage(PushKinds.Message, "Hello", "World")));
    }

    [Fact]
    public void NtfyPayload_ExactShape_TopicTagsPriorityHeaders()
    {
        var json = PushDispatcher.BuildNtfyPayload(
            new PushMessage(PushKinds.Broadcast, "Hello", "World", "itm"), "mytopic");

        Assert.Equal(
            "{\"topic\":\"mytopic\",\"title\":\"Hello\",\"message\":\"World\",\"tags\":[\"jellyplay\"],\"priority\":\"default\",\"headers\":{\"X-JellyPlay-Kind\":\"broadcast\",\"X-JellyPlay-ItemId\":\"itm\"}}",
            json);

        var withoutItem = JObject.Parse(PushDispatcher.BuildNtfyPayload(new PushMessage(PushKinds.Message, "T", "B"), "t"));
        Assert.Null(withoutItem["headers"]!["X-JellyPlay-ItemId"]);
        Assert.Equal("message", withoutItem["headers"]!["X-JellyPlay-Kind"]!.ToString());
    }

    [Theory]
    [InlineData("https://ntfy.sh/mytopic", "mytopic")]
    [InlineData("https://ntfy.sh/team/apps/mytopic", "mytopic")]
    [InlineData("https://ntfy.sh/mytopic/", "mytopic")]
    [InlineData("https://ntfy.sh/my%20topic", "my topic")]
    public void ExtractNtfyTopic_TakesLastPathSegment(string endpoint, string topic)
    {
        Assert.Equal(topic, PushDispatcher.ExtractNtfyTopic(endpoint));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("https://ntfy.sh/")]
    public void ExtractNtfyTopic_Unresolvable_YieldsNull(string? endpoint)
    {
        Assert.Null(PushDispatcher.ExtractNtfyTopic(endpoint));
    }

    [Fact]
    public void EndpointHost_IsHostOnly_NeverPathOrScheme()
    {
        Assert.Equal("ntfy.sh", PushDispatcher.EndpointHost("https://ntfy.sh/secret-topic/sub?x=1"));
        Assert.Equal(string.Empty, PushDispatcher.EndpointHost("garbage"));
        Assert.Equal(string.Empty, PushDispatcher.EndpointHost(null));
    }
}

// ---------------------------------------------------------------------------
// Dispatcher fan-out with a fake sender
// ---------------------------------------------------------------------------

public sealed class PushDispatcherTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "jellyplay-push-disp-" + Guid.NewGuid().ToString("N"));
    private readonly JellyPlayDatabase _db;

    public PushDispatcherTests()
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

    private PushDispatcher Dispatcher(bool enabled, PushSender sender)
        => new(_db, () => new PushConfig { Enabled = enabled }, NullLogger<PushDispatcher>.Instance, sender);

    [Fact]
    public async Task FanOut_HitsEveryEndpoint_WithKindSpecificBodies()
    {
        _db.UpsertDevice(new DeviceWrite("g1", "u1", "G", "android", "1", 1, "generic", "https://push.example/g1", 1));
        _db.UpsertDevice(new DeviceWrite("n1", "u1", "N", "android", "1", 1, "ntfy", "https://ntfy.sh/mytopic", 1));
        _db.UpsertDevice(new DeviceWrite("other", "u2", "O", "android", "1", 1, "generic", "https://push.example/other", 1));
        var recording = new PushRecording();

        await Dispatcher(enabled: true, recording.Sender())
            .DispatchAsync(new PushMessage(PushKinds.NewMedia, "Title", "Body", "itm1"), new[] { "u1" });

        Assert.Equal(2, recording.Count); // target user's devices only, all of them

        var bodies = recording.Bodies.ToList();
        Assert.Equal(
            "{\"title\":\"Title\",\"body\":\"Body\",\"kind\":\"new-media\",\"itemId\":\"itm1\"}",
            bodies[recording.Endpoints.ToList().IndexOf("https://push.example/g1")]);

        var ntfy = JObject.Parse(bodies[recording.Endpoints.ToList().IndexOf("https://ntfy.sh/mytopic")]);
        Assert.Equal("mytopic", ntfy["topic"]);
        Assert.Equal("Title", ntfy["title"]);
        Assert.Equal("Body", ntfy["message"]);
        Assert.Equal("jellyplay", ntfy["tags"]![0]);
        Assert.Equal("default", ntfy["priority"]);
        Assert.Equal("new-media", ntfy["headers"]!["X-JellyPlay-Kind"]);
    }

    [Fact]
    public async Task Failure_Isolated_OtherEndpointsStillAttempted()
    {
        _db.UpsertDevice(new DeviceWrite("a", "u1", "A", "android", "1", 1, "generic", "https://a.example/hook", 1));
        _db.UpsertDevice(new DeviceWrite("b", "u1", "B", "android", "1", 1, "generic", "https://b.example/hook", 1));

        // First call explodes, second times out — neither may crash the fan-out.
        var recording = new PushRecording();
        var sender = recording.Sender(index => index switch
        {
            0 => throw new InvalidOperationException("boom"),
            _ => throw new OperationCanceledException("simulated 10s timeout")
        });

        await Dispatcher(enabled: true, sender)
            .DispatchAsync(new PushMessage(PushKinds.Broadcast, "T", "B"), new[] { "u1" });

        Assert.Equal(2, recording.Count); // both endpoints attempted despite failures
        Assert.Equal(2, recording.Endpoints.Distinct().Count());
    }

    [Fact]
    public async Task Disabled_DispatchesNothing()
    {
        _db.UpsertDevice(new DeviceWrite("a", "u1", "A", "android", "1", 1, "generic", "https://a.example/hook", 1));
        var recording = new PushRecording();

        await Dispatcher(enabled: false, recording.Sender())
            .DispatchAsync(new PushMessage(PushKinds.Broadcast, "T", "B"), null);

        Assert.Equal(0, recording.Count);
    }

    [Fact]
    public async Task EmptyUserList_DeliversToNobody_WhileNullDeliversToAll()
    {
        _db.UpsertDevice(new DeviceWrite("a", "u1", "A", "android", "1", 1, "generic", "https://a.example/hook", 1));
        _db.UpsertDevice(new DeviceWrite("b", "u2", "B", "android", "1", 1, "generic", "https://b.example/hook", 1));

        var empty = new PushRecording();
        await Dispatcher(enabled: true, empty.Sender())
            .DispatchAsync(new PushMessage(PushKinds.Message, "T", "B"), Array.Empty<string>());
        Assert.Equal(0, empty.Count);

        var all = new PushRecording();
        await Dispatcher(enabled: true, all.Sender())
            .DispatchAsync(new PushMessage(PushKinds.Message, "T", "B"), null);
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task DispatchToUsers_IsFireAndForget_AndStillDelivers()
    {
        _db.UpsertDevice(new DeviceWrite("a", "u1", "A", "android", "1", 1, "generic", "https://a.example/hook", 1));
        var recording = new PushRecording();

        var dispatcher = Dispatcher(enabled: true, recording.Sender());
        dispatcher.DispatchToUsers(new PushMessage(PushKinds.Message, "T", "B"), new[] { "u1" });
        // returns without awaiting; the background task must still land the request

        await recording.WaitAsync(1);
        Assert.Equal("https://a.example/hook", recording.Endpoints.Single());
    }

    [Fact]
    public void AdminOverview_SurfacesHostOnly_AndFallsBackForNamesAndDates()
    {
        var aliceGuid = Guid.NewGuid();
        _db.UpsertDevice(new DeviceWrite("a", aliceGuid.ToString(), "Alice phone", "android", "1", 777, "generic", "https://push.example/secret/path", 111));
        _db.UpsertDevice(new DeviceWrite("b", "ghost", "Ghost dev", "ios", "1", 888, "ntfy", "https://ntfy.sh/topic", null));

        var overview = Dispatcher(enabled: true, static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))
            .GetAdminOverview(guid => guid == aliceGuid ? "Alice" : null);

        Assert.True(overview.Enabled);
        var rows = overview.Devices.ToDictionary(row => row.DeviceId);

        var alice = rows["a"];
        Assert.Equal("Alice", alice.UserName);
        Assert.Equal("Alice phone", alice.DeviceName);
        Assert.Equal("generic", alice.Kind);
        Assert.Equal("push.example", alice.EndpointHost); // host only — never path
        Assert.Equal(111, alice.RegisteredAt);

        var ghost = rows["b"];
        Assert.Equal("ghost", ghost.UserName); // id fallback for users unknown to the host
        Assert.Equal("ntfy.sh", ghost.EndpointHost);
        Assert.Equal(888, ghost.RegisteredAt); // CreatedAt null (legacy row) → LastSeen
    }
}

// ---------------------------------------------------------------------------
// Silent push: the sync-nudge kind (registry v7, caps-gated)
// ---------------------------------------------------------------------------

/// <summary>
/// The sync-nudge fan-out delivers ONLY to devices whose registered caps
/// include "silent-push" (revoked devices and uncapped devices are skipped) —
/// old clients render unknown kinds as visible notifications, so the cap is
/// the safety gate.
/// </summary>
public sealed class SyncNudgeDispatcherTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "jellyplay-nudge-" + Guid.NewGuid().ToString("N"));
    private readonly JellyPlayDatabase _db;

    public SyncNudgeDispatcherTests()
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
    public async Task Nudge_GoesOnlyToSilentPushCapableDevices()
    {
        // capable (generic + ntfy), uncapped, and revoked-but-capped devices for u1.
        _db.UpsertDevice(new DeviceWrite("cap-g", "u1", "G", "android", "1", 1, "generic", "https://push.example/g", 1, CapsJson: "[\"silent-push\"]"));
        _db.UpsertDevice(new DeviceWrite("cap-n", "u1", "N", "android", "1", 1, "ntfy", "https://ntfy.sh/topic", 1, CapsJson: "[\"silent-push\",\"other\"]"));
        _db.UpsertDevice(new DeviceWrite("plain", "u1", "P", "android", "1", 1, "generic", "https://push.example/plain", 1, CapsJson: "[\"other\"]"));
        _db.UpsertDevice(new DeviceWrite("gone", "u1", "X", "android", "1", 1, "generic", "https://push.example/gone", 1, CapsJson: "[\"silent-push\"]"));
        _db.SetDeviceRevoked("u1", "gone", revoked: true);
        var recording = new PushRecording();

        var dispatcher = new PushDispatcher(
            _db,
            () => new PushConfig { Enabled = true },
            NullLogger<PushDispatcher>.Instance,
            recording.Sender());
        await dispatcher.DispatchSyncNudgeAsync("u1");

        Assert.Equal(2, recording.Count);
        var endpoints = recording.Endpoints.ToList();
        Assert.Contains("https://push.example/g", endpoints);
        Assert.Contains("https://ntfy.sh/topic", endpoints);
        Assert.DoesNotContain("https://push.example/plain", endpoints);
        Assert.DoesNotContain("https://push.example/gone", endpoints);

        // Every body carries the silent kind.
        Assert.All(recording.Bodies, body => Assert.Contains("sync-nudge", body));
        var nudge = JObject.Parse(recording.Bodies.First(body => body.Contains("mytopic") || body.Contains("\"topic\"")));
        Assert.Equal("sync-nudge", nudge["headers"]!["X-JellyPlay-Kind"]);
    }

    [Fact]
    public async Task Nudge_DisabledPush_IssuesNoRequests()
    {
        _db.UpsertDevice(new DeviceWrite("cap", "u1", "G", "android", "1", 1, "generic", "https://push.example/g", 1, CapsJson: "[\"silent-push\"]"));
        var recording = new PushRecording();

        var dispatcher = new PushDispatcher(
            _db,
            () => new PushConfig { Enabled = false },
            NullLogger<PushDispatcher>.Instance,
            recording.Sender());
        await dispatcher.DispatchSyncNudgeAsync("u1");

        Assert.Equal(0, recording.Count);
    }

    [Fact]
    public void FcmNudgePayload_IsDataOnly()
    {
        var payload = JObject.Parse(PushDispatcher.BuildFcmPayload(
            new PushMessage(PushKinds.SyncNudge, "JellyPlay", "settings-changed"), "regtok"));

        Assert.Equal("sync-nudge", payload["message"]!["data"]!["kind"]);
        Assert.Null(payload["message"]!["notification"]); // silent: no visible block
    }
}

// ---------------------------------------------------------------------------
// Audience reuse: push targets == SSE targets
// ---------------------------------------------------------------------------

/// <summary>New-media push rides the exact SSE audience resolution; messages target the message audience.</summary>
public sealed class PushAudienceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "jellyplay-push-aud-" + Guid.NewGuid().ToString("N"));
    private readonly JellyPlayDatabase _db;
    private readonly PushRecording _recording = new();

    public PushAudienceTests()
    {
        Directory.CreateDirectory(_tempDir);
        _db = new JellyPlayDatabase(_tempDir);
        _db.UpsertDevice(new DeviceWrite("admin-dev", "admin-1", "A", "android", "1", 1, "generic", "https://push.example/admin", 1));
        _db.UpsertDevice(new DeviceWrite("regular-dev", "regular", "R", "android", "1", 1, "generic", "https://push.example/regular", 1));
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

    private static EpisodeGroup Group(Guid itemId)
        => new(Guid.NewGuid(), Guid.NewGuid(), "Show", 1, new[] { new EpisodeRef(itemId, "E1") }, null);

    private PushDispatcher Dispatcher(bool enabled = true)
        => new(_db, () => new PushConfig { Enabled = enabled }, NullLogger<PushDispatcher>.Instance, _recording.Sender());

    [Fact]
    public void ResolveMessageAudience_AdminUsersAll()
    {
        var admins = MessageService.ResolveAudienceTargets("admins", new List<string>(), new[] { "a1", "a2" });
        Assert.NotNull(admins);
        Assert.Equal(new[] { "a1", "a2" }, admins!.OrderBy(x => x));

        var users = MessageService.ResolveAudienceTargets("users", new List<string> { "u9" }, new[] { "a1" });
        Assert.Equal(new[] { "u9" }, users);

        Assert.Empty(MessageService.ResolveAudienceTargets("users", new List<string>(), new[] { "a1" })!); // empty = nobody
        Assert.Null(MessageService.ResolveAudienceTargets("all", new List<string>(), Array.Empty<string>()));
    }

    [Fact]
    public async Task NewMediaAdminsAudience_PushesExactlyTheSseSet()
    {
        var admins = new List<string> { "admin-1" };
        var events = new EventService(
            new SseHub(NullLogger<SseHub>.Instance),
            () => new EventsConfig { NewMediaEnabled = true, NewMediaAudience = "admins" },
            () => admins,
            NullLogger<EventService>.Instance,
            Dispatcher());

        var sseTargets = EventService.ResolveAudienceTargets("admins", admins);
        events.PublishNewMedia(Group(Guid.NewGuid()));
        await _recording.WaitAsync(1);

        // SSE delivers to exactly {admin-1}; so must push.
        Assert.NotNull(sseTargets);
        Assert.Equal(new[] { "admin-1" }, sseTargets!.ToList());
        Assert.Equal(new[] { "https://push.example/admin" }, _recording.Endpoints.ToList());

        var body = JObject.Parse(_recording.Bodies.Single());
        Assert.Equal("new-media", body["kind"]);
        Assert.NotNull(body["itemId"]);
    }

    [Fact]
    public async Task NewMediaAllAudience_PushesEveryRegisteredUser()
    {
        var events = new EventService(
            new SseHub(NullLogger<SseHub>.Instance),
            () => new EventsConfig { NewMediaEnabled = true, NewMediaAudience = "all" },
            () => new List<string> { "admin-1" },
            NullLogger<EventService>.Instance,
            Dispatcher());

        events.PublishNewMovie(Guid.NewGuid(), "A Movie", null);
        await _recording.WaitAsync(2);

        Assert.Equal(
            new[] { "https://push.example/admin", "https://push.example/regular" },
            _recording.Endpoints.OrderBy(url => url, StringComparer.Ordinal).ToList());
    }

    [Fact]
    public async Task Broadcast_PushesAllUsers_FireAndForget()
    {
        var events = new EventService(
            new SseHub(NullLogger<SseHub>.Instance),
            () => new EventsConfig(),
            () => new List<string> { "admin-1" },
            NullLogger<EventService>.Instance,
            Dispatcher());

        events.PublishBroadcast("Maintenance", "Restart tonight", null); // fire-and-forget
        await _recording.WaitAsync(2);

        Assert.Equal(2, _recording.Endpoints.Count);
        var body = JObject.Parse(_recording.Bodies.First());
        Assert.Equal("Maintenance", body["title"]);
        Assert.Equal("broadcast", body["kind"]);
    }

    [Fact]
    public async Task MessageCreation_PushesAudienceUsers_UpdatesDoNotRePush()
    {
        var service = new MessageService(
            _db,
            NullLogger<MessageService>.Instance,
            Dispatcher(),
            () => new List<string> { "admin-1" });

        var created = service.Upsert(new MessageAdminRequest
        {
            Title = "Welcome",
            Body = "Hello there",
            Audience = new AudiencePayload { Type = "users", UserIds = new List<string> { "regular" } }
        });
        await _recording.WaitAsync(1);

        Assert.Equal(new[] { "https://push.example/regular" }, _recording.Endpoints.ToList());
        var body = JObject.Parse(_recording.Bodies.Single());
        Assert.Equal("message", body["kind"]);
        Assert.Equal("Welcome", body["title"]);

        // An edit (explicit id) must not re-push.
        service.Upsert(new MessageAdminRequest
        {
            Id = created.Id,
            Title = "Welcome edited",
            Body = "Hello there",
            Audience = new AudiencePayload { Type = "users", UserIds = new List<string> { "regular" } }
        });
        Assert.Equal(1, _recording.Count);
    }

    [Fact]
    public async Task DisabledPush_IssuesNoRequests()
    {
        var events = new EventService(
            new SseHub(NullLogger<SseHub>.Instance),
            () => new EventsConfig { NewMediaEnabled = true, NewMediaAudience = "all" },
            () => new List<string> { "admin-1" },
            NullLogger<EventService>.Instance,
            Dispatcher(enabled: false));

        Assert.Equal(0, events.PublishNewMedia(Group(Guid.NewGuid()))); // empty SSE hub delivers 0; push must stay silent too
        events.PublishBroadcast("t", "b", null);

        var service = new MessageService(
            _db,
            NullLogger<MessageService>.Instance,
            Dispatcher(enabled: false),
            () => new List<string>());
        service.Upsert(new MessageAdminRequest { Title = "t", Body = "b", Audience = new AudiencePayload { Type = "all" } });

        // A beat for any wrongly-issued request to surface; none may arrive.
        await Task.Delay(150);
        Assert.Equal(0, _recording.Count);
    }
}

// ---------------------------------------------------------------------------
// Production transport: both push services ride the named pooled client
// ---------------------------------------------------------------------------

/// <summary>Stub IHttpClientFactory: hands out clients over one shared handler (the test owns its lifetime).</summary>
internal sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public StubHttpClientFactory(HttpMessageHandler handler)
    {
        _handler = handler;
    }

    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}

/// <summary>Handler recording the requests that went through the factory client.</summary>
internal sealed class RecordingHttpHandler : HttpMessageHandler
{
    public Func<HttpResponseMessage>? Responder { get; set; }

    public int Calls;

    public ConcurrentQueue<string> Endpoints { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        Endpoints.Enqueue(request.RequestUri?.ToString() ?? string.Empty);
        return Task.FromResult(Responder?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.OK));
    }
}

/// <summary>The DI constructors wire the transport to IHttpClientFactory ("jellyplay-push") — no hidden static clients remain.</summary>
public sealed class PushHttpFactoryTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "jellyplay-push-http-" + Guid.NewGuid().ToString("N"));
    private readonly JellyPlayDatabase _db;

    public PushHttpFactoryTests()
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
    public async Task Dispatcher_DiConstructor_SendsThroughTheFactoryClient()
    {
        var handler = new RecordingHttpHandler();
        _db.UpsertDevice(new DeviceWrite("a", "u1", "A", "android", "1", 1, "generic", "https://push.example/a", 1));
        var dispatcher = new PushDispatcher(
            _db,
            () => new PushConfig { Enabled = true },
            NullLogger<PushDispatcher>.Instance,
            new StubHttpClientFactory(handler),
            new FcmTokenProvider(
                () => new PushConfig(),
                NullLogger<FcmTokenProvider>.Instance,
                () => DateTimeOffset.UtcNow,
                static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));

        await dispatcher.DispatchAsync(new PushMessage(PushKinds.Message, "T", "B"), new[] { "u1" });

        Assert.Equal(1, handler.Calls);
        Assert.Equal("https://push.example/a", handler.Endpoints.Single());
    }

    [Fact]
    public async Task FcmTokenProvider_DiConstructor_ExchangesThroughTheFactoryClient()
    {
        using var sa = new FakeServiceAccount();
        var handler = new RecordingHttpHandler
        {
            Responder = static () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"access_token\":\"tok-1\",\"expires_in\":3600}", Encoding.UTF8, "application/json")
            }
        };
        var provider = new FcmTokenProvider(
            () => new PushConfig { Enabled = true, FcmProjectId = "proj-x", FcmServiceAccountJson = sa.Json() },
            NullLogger<FcmTokenProvider>.Instance,
            new StubHttpClientFactory(handler));

        Assert.Equal("tok-1", await provider.GetTokenAsync());
        Assert.Equal(1, handler.Calls);
        Assert.Equal("https://oauth2.googleapis.com/token", handler.Endpoints.Single());
    }
}
