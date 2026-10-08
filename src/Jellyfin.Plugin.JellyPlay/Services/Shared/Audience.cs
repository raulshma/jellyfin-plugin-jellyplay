using System;
using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.JellyPlay.Api;

namespace Jellyfin.Plugin.JellyPlay.Services.Shared;

/// <summary>
/// The ONE audience module: normalization, target resolution and the stored
/// payload's JSON round-trip, shared by every audience-bearing surface (the
/// event pipeline's new-media audience, the message registry's per-message
/// audience). Audiences normalize to three shapes: "admins" (the host's
/// administrator ids, resolved by the caller), "users" (the explicit id list)
/// and anything else = "all" (broadcast — represented as a null target set).
/// The <c>AudienceJson</c> round-trip is purely internal storage plumbing:
/// the stored bytes are exactly what <c>MessageService</c> historically
/// wrote inline (preserved byte-for-byte by this extraction), and nothing on
/// the wire depends on this class's serializer options.
/// </summary>
public static class Audience
{
    /// <summary>Serializes the stored audience payload (identical bytes to serializing the payload class directly).</summary>
    public static string Serialize(AudiencePayload audience)
        => JsonSerializer.Serialize(audience);

    /// <summary>Parses a stored audience payload; null when the JSON is malformed (callers degrade to "all").</summary>
    public static AudiencePayload? TryParse(string audienceJson)
    {
        try
        {
            return JsonSerializer.Deserialize<AudiencePayload>(audienceJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Push/SSE targets for a message audience: "admins" → the admin id set,
    /// "users" → the explicit ids (empty list delivers to nobody), anything
    /// else → null = every user. Pure so it is unit-testable without the host.
    /// </summary>
    public static IReadOnlyCollection<string>? ResolveTargets(
        string? audienceType,
        IReadOnlyList<string> explicitUserIds,
        IReadOnlyList<string> adminUserIds)
        => audienceType switch
        {
            "admins" => new HashSet<string>(adminUserIds, StringComparer.Ordinal),
            "users" => new HashSet<string>(explicitUserIds, StringComparer.Ordinal),
            _ => null
        };

    /// <summary>
    /// Targets for the event pipeline's simpler audience tag: "admins" → the
    /// admin id set, anything else ("all") → null = broadcast to every
    /// subscriber. Pure so it is unit-testable without the host.
    /// </summary>
    public static IReadOnlySet<string>? ResolveEventTargets(string? audience, IReadOnlyList<string> adminUserIds)
        => string.Equals(audience, "admins", StringComparison.OrdinalIgnoreCase)
            ? new HashSet<string>(adminUserIds, StringComparer.Ordinal)
            : null;
}
