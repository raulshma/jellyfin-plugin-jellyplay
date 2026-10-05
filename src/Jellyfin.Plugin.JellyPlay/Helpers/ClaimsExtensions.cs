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

    public static bool IsAdmin(this ClaimsPrincipal user)
        => user.IsInRole("Administrator");
}
