using System;

namespace Jellyfin.Plugin.JellyPlay.Services.Push;

/// <summary>
/// The push-registration rules shared by the device registry (which accepts a
/// registration) and the dispatcher (which consumes one): valid wire kinds and
/// the endpoint requirement. Pure — no config, no IO.
/// </summary>
public static class PushRegistrations
{
    public const string GenericKind = "generic";
    public const string NtfyKind = "ntfy";
    public const string FcmKind = "fcm";

    /// <summary>Registration gate: kind must be "generic"|"ntfy"|"fcm" and the endpoint a non-blank value (an FCM registration token for fcm).</summary>
    public static bool IsValidRegistration(string? kind, string? endpoint)
        => (string.Equals(kind, GenericKind, StringComparison.Ordinal)
            || string.Equals(kind, NtfyKind, StringComparison.Ordinal)
            || string.Equals(kind, FcmKind, StringComparison.Ordinal))
           && !string.IsNullOrWhiteSpace(endpoint);

    /// <summary>The fcm kind additionally requires configured FCM credentials to be usable (400 push-kind-unavailable otherwise).</summary>
    public static bool IsFcmKind(string? kind) => string.Equals(kind, FcmKind, StringComparison.Ordinal);

    /// <summary>The ntfy kind's publish topic is the endpoint URL's last path segment.</summary>
    public static bool IsNtfyKind(string? kind) => string.Equals(kind, NtfyKind, StringComparison.Ordinal);
}
