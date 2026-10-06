using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyPlay.Api;
using Jellyfin.Plugin.JellyPlay.Configuration;
using Jellyfin.Plugin.JellyPlay.Services.Push;
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

/// <summary>The outcome of a registration attempt; the caller maps it to the wire contract's status codes and error bodies.</summary>
public enum RegisterDeviceOutcome
{
    Registered,
    DeviceIdRequired,
    InvalidPushRegistration,
    PushKindUnavailable
}

/// <summary>
/// Owns the device registry contract: idempotent registration (with the
/// push attach/preserve/detach rule and the FCM availability gate),
/// unregistration and the owner-scoped listing. Endpoint URLs are secrets —
/// they round-trip only to their owner via <see cref="ListForUser"/>.
/// </summary>
public sealed class DeviceRegistryService
{
    private readonly JellyPlayDatabase _db;
    private readonly Func<PushConfig> _pushConfig;
    private readonly TimeProvider _clock;

    public DeviceRegistryService(JellyPlayDatabase db, Func<PushConfig> pushConfig, TimeProvider? clock = null)
    {
        _db = db;
        _pushConfig = pushConfig;
        _clock = clock ?? TimeProvider.System;
    }

    public RegisterDeviceOutcome Register(string userId, string? deviceId, string name, string platform, string appVersion, PushDirective? push)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return RegisterDeviceOutcome.DeviceIdRequired;
        }

        // Attach validates and overwrites (idempotent re-registration when the
        // distributor rotates endpoints); Detach clears the registration but
        // keeps the device row; null directive preserves what is stored.
        string? pushKind = null;
        string? pushEndpoint = null;
        if (push is PushDirective.Attach attach)
        {
            if (!PushRegistrations.IsValidRegistration(attach.Kind, attach.Endpoint))
            {
                return RegisterDeviceOutcome.InvalidPushRegistration;
            }

            // fcm needs configured FCM credentials (project id + service-account key).
            if (PushRegistrations.IsFcmKind(attach.Kind) && !_pushConfig().FcmConfigured())
            {
                return RegisterDeviceOutcome.PushKindUnavailable;
            }

            pushKind = attach.Kind;
            pushEndpoint = attach.Endpoint.Trim();
        }
        else if (push is PushDirective.Detach)
        {
            pushKind = null;
            pushEndpoint = null;
        }

        var existing = _db.GetDeviceById(deviceId);
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        var nextPushKind = push is null ? existing?.PushKind : pushKind;
        var nextPushEndpoint = push is null ? existing?.PushEndpoint : pushEndpoint;
        _db.UpsertDevice(new DeviceRow(
            deviceId,
            userId,
            name,
            platform,
            appVersion,
            now,
            nextPushKind,
            nextPushEndpoint,
            existing?.CreatedAt ?? now));
        return RegisterDeviceOutcome.Registered;
    }

    public bool Unregister(string userId, string deviceId) => _db.DeleteDevice(userId, deviceId);

    /// <summary>The owner's devices with their push blocks (never another user's).</summary>
    public IReadOnlyList<DeviceDto> ListForUser(string userId)
        => _db.GetDevices(userId)
            .Select(ToDto)
            .ToList();

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
                : new DevicePushDto(row.PushKind, row.PushEndpoint));
}
