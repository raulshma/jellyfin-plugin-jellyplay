using System;
using System.Text.Json;
using Jellyfin.Plugin.JellyPlay.Api;
using Jellyfin.Plugin.JellyPlay.Services.Devices;

namespace Jellyfin.Plugin.JellyPlay.Services.Push;

/// <summary>
/// Push policy module: the one seam for push-registration rules behind the
/// Device registry module. Owns the wire tri-state normalization (object /
/// absent / explicit null → attach / preserve / detach), the kind + endpoint
/// gate, and the FCM usability gate, so the registry, the Dispatch module and
/// the HTTP adapters all leverage one interface instead of bouncing between
/// small rule modules.
/// </summary>
public static class PushPolicy
{
    public const string GenericKind = "generic";
    public const string NtfyKind = "ntfy";
    public const string FcmKind = "fcm";

    /// <summary>Camel-case-insensitive binding for the raw push element (matches ASP.NET's body binding).</summary>
    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Normalizes the wire's three shapes to the registry module's
    /// <see cref="PushDirective"/>: absent (null) is the Preserve-signal,
    /// an object is Attach(kind,endpoint), any other JSON value is Detach.
    /// </summary>
    public static PushDirective? ParseDirective(JsonElement? push)
        => push switch
        {
            null => null,
            { ValueKind: JsonValueKind.Object } element
                => element.Deserialize<DevicePushRegistration>(JsonOptions) is { } parsed
                    ? new PushDirective.Attach(parsed.Kind, parsed.Endpoint)
                    : new PushDirective.Attach(string.Empty, string.Empty),
            _ => new PushDirective.Detach()
        };

    /// <summary>Registration gate: kind must be "generic"|"ntfy"|"fcm" and the endpoint a non-blank value (an FCM registration token for fcm).</summary>
    public static bool IsValidRegistration(string? kind, string? endpoint)
        => (string.Equals(kind, GenericKind, StringComparison.Ordinal)
            || string.Equals(kind, NtfyKind, StringComparison.Ordinal)
            || string.Equals(kind, FcmKind, StringComparison.Ordinal))
            && !string.IsNullOrWhiteSpace(endpoint);

    /// <summary>The fcm kind additionally requires configured FCM credentials to be usable.</summary>
    public static bool IsFcmKind(string? kind) => string.Equals(kind, FcmKind, StringComparison.Ordinal);

    /// <summary>Whether the push registration's kind is ntfy (the sender derives its publish topic from the endpoint).</summary>
    public static bool IsNtfyKind(string? kind) => string.Equals(kind, NtfyKind, StringComparison.Ordinal);
}
