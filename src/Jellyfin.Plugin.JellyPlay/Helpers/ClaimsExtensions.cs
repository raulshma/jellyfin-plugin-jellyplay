using System;
using System.Security.Claims;

namespace Jellyfin.Plugin.JellyPlay.Helpers;

/// <summary>
/// Claim accessors for Jellyfin-authenticated principals. Jellyfin populates
/// <c>Jellyfin-UserId</c> / <c>Jellyfin-DeviceId</c> claims; NameIdentifier is the
/// fallback used by older token formats.
/// </summary>
public static class ClaimsExtensions
{
    private const string UserIdClaim = "Jellyfin-UserId";
    private const string DeviceIdClaim = "Jellyfin-DeviceId";

    public static Guid GetUserId(this ClaimsPrincipal user)
    {
        var value = user.FindFirstValue(UserIdClaim)
                    ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(value, out var id) ? id : Guid.Empty;
    }

    public static string GetDeviceId(this ClaimsPrincipal user)
        => user.FindFirstValue(DeviceIdClaim) ?? string.Empty;

    /// <summary>
    /// Caller-identity module's string interface: the caller's user id as a
    /// string, keeping string-conversion locality here so API controllers
    /// leverage one seam instead of repeating GetUserId().ToString().
    /// </summary>
    public static string GetUserIdString(this ClaimsPrincipal user) => GetUserId(user).ToString();

    /// <summary>
    /// Caller-identity module's device-resolution interface: prefers the
    /// explicit request device id, falling back to the claim. An empty request
    /// id is treated as missing (empty-means-missing): no valid device identity
    /// is ever the empty string, so it resolves to the claim like a null does.
    /// Keeps the empty-means-missing locality behind one seam for all controllers.
    /// </summary>
    public static string ResolveDeviceId(this ClaimsPrincipal user, string? requestDeviceId)
        => string.IsNullOrEmpty(requestDeviceId) ? GetDeviceId(user) : requestDeviceId;

    /// <summary>
    /// Storage-facing device fallback: no valid device identity is ever the
    /// empty string, so a missing identity stamps "unknown" (the wipe matcher
    /// and the history record share this seam with the write path, never a
    /// second spelling).
    /// </summary>
    public static string NormalizeDeviceId(string? deviceId)
        => string.IsNullOrEmpty(deviceId) ? "unknown" : deviceId;

    public static bool IsAdmin(this ClaimsPrincipal user)
        => user.IsInRole("Administrator");
}
