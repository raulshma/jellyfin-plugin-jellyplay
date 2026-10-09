using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyPlay.Api;
using Jellyfin.Plugin.JellyPlay.Configuration;
using Jellyfin.Plugin.JellyPlay.Services.Push;
using Jellyfin.Plugin.JellyPlay.Services.Settings;
using Jellyfin.Plugin.JellyPlay.Storage;
using Jellyfin.Plugin.JellyPlay.Storage.Models;

namespace Jellyfin.Plugin.JellyPlay.Services.Devices;

/// <summary>
/// The normalized push intent of a device registration, already collapsed from
/// the wire's three shapes: an object is <see cref="Attach"/>, an explicit JSON
/// null is <see cref="Detach"/>, and an absent block is <see cref="Preserve"/>.
/// </summary>
public abstract record PushDirective
{
    public sealed record Attach(string Kind, string Endpoint) : PushDirective;

    public sealed record Detach : PushDirective;

    public sealed record Preserve : PushDirective;
}

/// <summary>
/// The registration input bundle: the caller's identity plus the device's
/// self-description and its normalized <see cref="PushDirective"/> — the
/// fields <see cref="DeviceRegistryService.Register"/> needs, as one
/// immutable value instead of a parameter clump.
/// </summary>
public sealed record DeviceRegistration(
    string UserId,
    string? DeviceId,
    string Name,
    string Platform,
    string AppVersion,
    PushDirective? Push,
    string? Model = null,
    IReadOnlyList<string>? Caps = null);

/// <summary>The outcome of a registration attempt; the caller maps it to the wire contract's status codes and error bodies.</summary>
public enum RegisterDeviceOutcome
{
    Registered,
    DeviceIdRequired,
    InvalidPushRegistration,
    PushKindUnavailable,
    DeviceRevoked
}

/// <summary>The outcome of a DELETE /devices/{id} call; the caller wipes settings rows only on <see cref="Revoked"/>.</summary>
public enum DeleteDeviceOutcome
{
    NotFound,

    /// <summary>Legacy capless device: the row (and its push registration) is removed — no revoked flag, no wipe.</summary>
    Unregistered,

    /// <summary>Capped (v7) device: the row is flagged revoked; the caller tombstone-wipes its settings rows.</summary>
    Revoked
}

/// <summary>
/// Owns the device registry contract: idempotent registration (with the
/// push attach/preserve/detach rule and the FCM availability gate), the
/// caps-gated delete (revoke + wipe for v7 clients, plain unregister for
/// capless legacy ones), rename and the owner-scoped listing. Endpoint URLs
/// are secrets — they round-trip only to their owner via
/// <see cref="ListForUser"/>.
/// </summary>
public sealed class DeviceRegistryService
{
    private readonly JellyPlayDatabase _db;
    private readonly Func<PushConfig> _pushConfig;
    private readonly SettingsService? _settings;
    private readonly TimeProvider _clock;

    public DeviceRegistryService(JellyPlayDatabase db, Func<PushConfig> pushConfig, SettingsService? settings = null, TimeProvider? clock = null)
    {
        _db = db;
        _pushConfig = pushConfig;
        _settings = settings;
        _clock = clock ?? TimeProvider.System;
    }

    public RegisterDeviceOutcome Register(DeviceRegistration request)
    {
        if (string.IsNullOrEmpty(request.DeviceId))
        {
            return RegisterDeviceOutcome.DeviceIdRequired;
        }

        var deviceId = request.DeviceId;
        var existing = _db.GetDeviceById(deviceId);

        // A revoked device cannot re-register itself back into good standing;
        // only an explicit owner action (none exists yet) could un-revoke.
        if (existing is { Revoked: true })
        {
            return RegisterDeviceOutcome.DeviceRevoked;
        }

        // Attach validates and overwrites (idempotent re-registration when the
        // distributor rotates endpoints); Detach clears the registration but
        // keeps the device row; null directive preserves what is stored.
        string? pushKind = null;
        string? pushEndpoint = null;
        if (request.Push is PushDirective.Attach attach)
        {
            if (!PushPolicy.IsValidRegistration(attach.Kind, attach.Endpoint))
            {
                return RegisterDeviceOutcome.InvalidPushRegistration;
            }

            // fcm needs configured FCM credentials (the one usability seam in PushEligibility).
            if (PushPolicy.IsFcmKind(attach.Kind) && !PushEligibility.IsFcmUsable(_pushConfig()))
            {
                return RegisterDeviceOutcome.PushKindUnavailable;
            }

            pushKind = attach.Kind;
            pushEndpoint = attach.Endpoint.Trim();
        }
        else if (request.Push is PushDirective.Detach)
        {
            pushKind = null;
            pushEndpoint = null;
        }

        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        var nextPushKind = request.Push is null ? existing?.PushKind : pushKind;
        var nextPushEndpoint = request.Push is null ? existing?.PushEndpoint : pushEndpoint;
        _db.UpsertDevice(new DeviceWrite(
            deviceId,
            request.UserId,
            request.Name,
            request.Platform,
            request.AppVersion,
            now,
            nextPushKind,
            nextPushEndpoint,
            existing?.CreatedAt ?? now,
            request.Model,
            SerializeCaps(request.Caps)));
        return RegisterDeviceOutcome.Registered;
    }

    /// <summary>
    /// DELETE /devices/{id} is a REVOKE in registry v7: the row survives
    /// (flagged, excluded from push, writes rejected) — the caller wipes its
    /// settings rows separately. Returns false when the caller owns no row.
    /// </summary>
    public bool Revoke(string userId, string deviceId) => _db.SetDeviceRevoked(userId, deviceId, revoked: true);

    /// <summary>
    /// DELETE /devices/{id} is CAPS-GATED. A device that registered caps (a v7
    /// client — the app always sends at least "silent-push") is REVOKE + wipe:
    /// the row survives flagged, excluded from push, writes rejected, and the
    /// caller wipes its settings rows. A device that never registered caps (a
    /// legacy pre-v7 client) gets the OLD semantics instead — a plain
    /// unregister: the row (and with it the push registration) is removed, no
    /// revoked flag, no wipe — so its routine push-detach / re-register cycle
    /// with the same stable deviceId keeps working (revoking there would brick
    /// it: re-register answers 400 device-revoked forever). An already-revoked
    /// row stays revoked regardless of caps — there is no un-revoke path.
    /// </summary>
    public DeleteDeviceOutcome Delete(string userId, string deviceId)
    {
        var existing = _db.GetDeviceById(deviceId);
        if (existing is null || !string.Equals(existing.UserId, userId, StringComparison.Ordinal))
        {
            return DeleteDeviceOutcome.NotFound;
        }

        if (existing.Revoked || ParseCaps(existing.CapsJson).Count > 0)
        {
            return Revoke(userId, deviceId) ? DeleteDeviceOutcome.Revoked : DeleteDeviceOutcome.NotFound;
        }

        return _db.DeleteDevice(userId, deviceId)
            ? DeleteDeviceOutcome.Unregistered
            : DeleteDeviceOutcome.NotFound;
    }

    /// <summary>
    /// The DELETE /devices/{id} orchestration shared by the owner's route and
    /// the admin revoke: the caps-gated <see cref="Delete"/>, and — only when
    /// a device came back <see cref="DeleteDeviceOutcome.Revoked"/> — the
    /// tombstone-wipe of every settings row it wrote (history operation and
    /// SSE fan-out included, via <see cref="SettingsService.WipeDevice"/>).
    /// Returns the delete outcome; the caller maps
    /// <see cref="DeleteDeviceOutcome.NotFound"/> to 404, everything else to
    /// 204.
    /// </summary>
    public DeleteDeviceOutcome RevokeAndWipe(string userId, string deviceId)
    {
        var outcome = Delete(userId, deviceId);
        if (outcome == DeleteDeviceOutcome.Revoked)
        {
            _settings?.WipeDevice(userId, deviceId);
        }

        return outcome;
    }

    /// <summary>Owner-scoped rename (null fields keep their stored value).</summary>
    public bool Rename(string userId, string deviceId, string? name, string? model)
        => _db.RenameDevice(userId, deviceId, name, model);

    /// <summary>The owner's devices with their push blocks (never another user's). Revoked rows stay listed, flagged.</summary>
    public IReadOnlyList<DeviceDto> ListForUser(string userId)
        => _db.GetDevices(userId)
            .Select(ToDto)
            .ToList();

    /// <summary>
    /// Parses a device row's caps JSON array; malformed payloads degrade to
    /// empty. Delegates to <see cref="PushEligibility.ParseCaps"/> — the caps
    /// locality lives there, spelled by every caller alike.
    /// </summary>
    private static IReadOnlyList<string> ParseCaps(string? capsJson)
        => PushEligibility.ParseCaps(capsJson);

    internal static string? SerializeCaps(IReadOnlyList<string>? caps)
        => caps is null || caps.Count == 0
            ? null
            : System.Text.Json.JsonSerializer.Serialize(caps);

    private static DeviceDto ToDto(DeviceRow row)
        => new(
            row.DeviceId,
            row.UserId,
            row.Name,
            row.Platform,
            row.AppVersion,
            row.LastSeen,
            row.PushKind is null || row.PushEndpoint is null
                ? null
                : new DevicePushDto(row.PushKind, row.PushEndpoint),
            row.Model,
            ParseCaps(row.CapsJson),
            row.Revoked);
}
