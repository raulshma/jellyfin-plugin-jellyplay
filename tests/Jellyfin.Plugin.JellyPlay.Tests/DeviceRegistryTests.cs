using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.JellyPlay.Realtime;
using Jellyfin.Plugin.JellyPlay.Services.Devices;
using Jellyfin.Plugin.JellyPlay.Services.Settings;
using Jellyfin.Plugin.JellyPlay.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.JellyPlay.Tests;

/// <summary>
/// Device registry v7: self-reported model/caps, the rename route's service
/// contract, the revoke semantics — a revoked device cannot re-register,
/// keeps its (filtered-out) push eligibility, and its settings rows are wiped
/// through the tombstone pipeline — and the DELETE route's caps gate (legacy
/// capless devices plain-unregister; capped ones revoke + wipe).
/// </summary>
public sealed class DeviceRegistryTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "jellyplay-registry-" + Guid.NewGuid().ToString("N"));
    private readonly JellyPlayDatabase _db;
    private readonly DeviceRegistryService _devices;
    private readonly SettingsService _settings;

    public DeviceRegistryTests()
    {
        Directory.CreateDirectory(_tempDir);
        _db = new JellyPlayDatabase(_tempDir);
        _settings = SettingsServiceFactory.Create(_db, new SseHub(NullLogger<SseHub>.Instance));
        _devices = new DeviceRegistryService(_db, () => new Configuration.PushConfig(), _settings);
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
    public void Register_SelfReportedModelAndCaps_Persist_AndRoundTripThroughTheDto()
    {
        Assert.Equal(RegisterDeviceOutcome.Registered,
            _devices.Register(new DeviceRegistration("u1", "d1", "Pixel", "android", "1.2.3", null, "Pixel 9", new[] { "silent-push", "widgets" })));

        var row = _db.GetDeviceById("d1");
        Assert.Equal("Pixel 9", row!.Model);
        Assert.Equal("[\"silent-push\",\"widgets\"]", row.CapsJson);
        Assert.False(row.Revoked);

        var dto = Assert.Single(_devices.ListForUser("u1"));
        Assert.Equal("Pixel 9", dto.Model);
        Assert.Equal(new[] { "silent-push", "widgets" }, dto.Caps);
        Assert.False(dto.Revoked);
    }

    [Fact]
    public void ReRegister_OverwritesCaps_PreservesRevokedFlag()
    {
        _devices.Register(new DeviceRegistration("u1", "d1", "Phone", "android", "1.0", null, "M1", new[] { "silent-push" }));
        _devices.Revoke("u1", "d1");

        // Re-registration is REFUSED for revoked devices (no self-unrevoke).
        Assert.Equal(RegisterDeviceOutcome.DeviceRevoked,
            _devices.Register(new DeviceRegistration("u1", "d1", "Phone", "android", "1.1", null, "M2", new[] { "other" })));
        Assert.True(_db.GetDeviceById("d1")!.Revoked);

        // An unknown id registers normally with caps; a second registration overwrites them.
        _devices.Register(new DeviceRegistration("u1", "d2", "Phone", "android", "1.0", null, null, new[] { "silent-push" }));
        _devices.Register(new DeviceRegistration("u1", "d2", "Phone", "android", "1.0", null, "M9", new[] { "widgets" }));
        var d2 = _db.GetDeviceById("d2");
        Assert.Equal("M9", d2!.Model);
        Assert.Equal("[\"widgets\"]", d2.CapsJson);
    }

    [Fact]
    public void Rename_UpdatesOnlyProvidedFields_OwnerScoped()
    {
        _devices.Register(new DeviceRegistration("u1", "d1", "Old name", "android", "1.0", null, "M1"));

        Assert.True(_devices.Rename("u1", "d1", "New name", null));
        var row = _db.GetDeviceById("d1")!;
        Assert.Equal("New name", row.Name);
        Assert.Equal("M1", row.Model); // untouched

        // Another user cannot rename (nor revoke) the device.
        Assert.False(_devices.Rename("u2", "d1", "Hijacked", null));
        Assert.False(_devices.Rename("u1", "missing", "Ghost", null));
    }

    [Fact]
    public void Revoke_FlagsRow_ExcludesFromPush_KeepsRegistration()
    {
        _devices.Register(new DeviceRegistration("u1", "d1", "Phone", "android", "1.0",
            new PushDirective.Attach("generic", "https://push.example/hook"), "M1", new[] { "silent-push" }));

        Assert.True(_devices.Revoke("u1", "d1"));
        Assert.True(_devices.Revoke("u1", "d1")); // re-revoking an owned row is a harmless no-op update

        var row = _db.GetDeviceById("d1")!;
        Assert.True(row.Revoked);
        Assert.Equal("generic", row.PushKind); // registration data survives; the dispatcher filters revoked rows

        // The admin overview projection still works; the user list flags it.
        Assert.True(_devices.ListForUser("u1").Single().Revoked);
    }

    [Fact]
    public void RevokeThenWipe_SettingsRowsTombstoned_AndWritesRejected()
    {
        // A v7 device (caps registered): its DELETE is revoke + wipe.
        _devices.Register(new DeviceRegistration("u1", "d1", "Phone", "android", "1.0", null, Caps: new[] { "silent-push" }));
        _settings.ApplyBatch("u1", "", "d1", new[]
        {
            new Api.SettingsWriteDto { Ns = "ui", Key = "theme", UpdatedAt = 1, Value = System.Text.Json.JsonDocument.Parse("\"dark\"").RootElement }
        });
        _settings.ApplyBatch("u1", "tv", "d1", new[]
        {
            new Api.SettingsWriteDto { Ns = "ui", Key = "layout", UpdatedAt = 1, Value = System.Text.Json.JsonDocument.Parse("\"tv\"").RootElement }
        });

        // The shared revoke+wipe orchestration (the one entry point behind the
        // owner's DELETE route and the admin revoke).
        Assert.Equal(DeleteDeviceOutcome.Revoked, _devices.RevokeAndWipe("u1", "d1"));

        Assert.Empty(_settings.GetAll("u1", "").Settings);
        Assert.Empty(_settings.GetAll("u1", "tv").Settings);
        Assert.Single(_db.GetSyncHistory("u1", 0, 50), row => row.Op == "wipe");

        // An unknown device is outcome-only: NotFound, nothing wiped.
        Assert.Equal(DeleteDeviceOutcome.NotFound, _devices.RevokeAndWipe("u1", "missing"));
    }

    // ------------------------------------------------------------------
    // Caps-gated DELETE (legacy compatibility)
    // ------------------------------------------------------------------

    [Fact]
    public void Delete_CaplessLegacyDevice_PlainUnregister_ReRegisterSucceeds()
    {
        // A pre-v7 client never registers caps; its DELETE is the routine
        // push-detach (unregister now, re-register later with the same id).
        _devices.Register(new DeviceRegistration("u1", "legacy-1", "Phone", "android", "1.0", null));

        Assert.Equal(DeleteDeviceOutcome.Unregistered, _devices.Delete("u1", "legacy-1"));

        // The row is GONE (no revoked flag to consult) — re-registering the
        // same stable deviceId works, and the fresh row is in good standing.
        Assert.Null(_db.GetDeviceById("legacy-1"));
        Assert.Equal(RegisterDeviceOutcome.Registered, _devices.Register(new DeviceRegistration("u1", "legacy-1", "Phone", "android", "1.1", null)));
        Assert.False(_db.GetDeviceById("legacy-1")!.Revoked);

        // Owner scoping holds for the gated delete too.
        Assert.Equal(DeleteDeviceOutcome.NotFound, _devices.Delete("u2", "legacy-1"));
    }

    [Fact]
    public void Delete_CappedDevice_RevokesAndWipes_ReRegisterRefused()
    {
        // A v7 client always registers at least the "silent-push" cap.
        _devices.Register(new DeviceRegistration("u1", "d1", "Phone", "android", "1.0", null, "M1", new[] { "silent-push" }));
        _settings.ApplyBatch("u1", "", "d1", new[]
        {
            new Api.SettingsWriteDto { Ns = "ui", Key = "theme", UpdatedAt = 1, Value = System.Text.Json.JsonDocument.Parse("\"dark\"").RootElement }
        });

        Assert.Equal(DeleteDeviceOutcome.Revoked, _devices.Delete("u1", "d1"));

        // The wipe runs only on Revoked — mirrored here: rows gone,
        // re-register bricked, writes rejected (the revoke+wipe behavior).
        _settings.WipeDevice("u1", "d1");
        Assert.Empty(_settings.GetAll("u1", "").Settings);
        Assert.True(_db.GetDeviceById("d1")!.Revoked);
        Assert.Equal(RegisterDeviceOutcome.DeviceRevoked,
            _devices.Register(new DeviceRegistration("u1", "d1", "Phone", "android", "1.1", null, "M2", new[] { "silent-push" })));

        // An already-revoked row stays revoked even when it carries no caps
        // (revoked by an earlier owner action; the gate must not un-revoke).
        _devices.Register(new DeviceRegistration("u1", "d2", "Tablet", "android", "1.0", null));
        Assert.True(_devices.Revoke("u1", "d2"));
        Assert.Equal(DeleteDeviceOutcome.Revoked, _devices.Delete("u1", "d2"));
        Assert.True(_db.GetDeviceById("d2")!.Revoked);
    }

    [Fact]
    public void ParseCaps_MalformedPayloads_DegradeToEmpty()
    {
        Assert.Empty(DeviceRegistryService.ParseCaps(null));
        Assert.Empty(DeviceRegistryService.ParseCaps(string.Empty));
        Assert.Empty(DeviceRegistryService.ParseCaps("not-json"));
        Assert.Equal(new[] { "silent-push" }, DeviceRegistryService.ParseCaps("[\"silent-push\"]"));
    }
}
