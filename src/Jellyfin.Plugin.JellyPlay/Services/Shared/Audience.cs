using System;
using System.Collections.Generic;
using System.Linq;
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
///
/// Depth: the <see cref="BroadcastTargets"/> value is the one fan-out
/// interface every SSE + push pairing leverages — one Null|Set seam (null =
/// broadcast-all, empty = nobody) instead of each caller re-implementing the
/// null-vs-empty locality. <see cref="ResolveAudience"/> is the unified
/// resolution every audience-bearing surface calls.
/// </summary>
public static class Audience
{
    /// <summary>
    /// The one broadcast-target value behind every fan-out: null broadcasts to
    /// all users, an empty set delivers to nobody, otherwise the member ids.
    /// The same Null|Set semantics the hub and the push store already enforce —
    /// now named, so the SSE + push pairing shares one interface instead of
    /// passing raw nullable collections.
    /// </summary>
    public sealed record BroadcastTargets(IReadOnlyCollection<string>? UserIds)
    {
        /// <summary>Broadcast to every user (the null leg).</summary>
        public static BroadcastTargets All { get; } = new((IReadOnlyCollection<string>?)null);

        /// <summary>Deliver to nobody (the empty-set leg).</summary>
        public static BroadcastTargets Nobody { get; } = new(Array.Empty<string>());

        /// <summary>Whether this is the broadcast-all leg (null).</summary>
        public bool IsBroadcast => UserIds is null;

        /// <summary>Whether this delivers to nobody (non-null but empty).</summary>
        public bool IsNobody => UserIds is { Count: 0 };

        /// <summary>Wraps a raw nullable set (null stays broadcast-all).</summary>
        public static BroadcastTargets From(IReadOnlyCollection<string>? userIds)
            => userIds is null ? All : new BroadcastTargets(userIds);
    }
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
    /// The unified audience resolution both historical helpers leverage: one
    /// case-insensitive switch ("admins" → admin set, "users" → explicit ids,
    /// anything else → broadcast-all). Pure so it is unit-testable without the
    /// host; the <see cref="BroadcastTargets"/> wrapper makes the Null|Set
    /// contract explicit at the type level.
    /// </summary>
    public static BroadcastTargets ResolveAudience(
        string? audienceType,
        IReadOnlyList<string> explicitUserIds,
        IReadOnlyList<string> adminUserIds)
        => BroadcastTargets.From(audienceType switch
        {
            { } type when string.Equals(type, "admins", StringComparison.OrdinalIgnoreCase)
                => (IReadOnlyCollection<string>?)new HashSet<string>(adminUserIds, StringComparer.Ordinal),
            { } type when string.Equals(type, "users", StringComparison.OrdinalIgnoreCase)
                => new HashSet<string>(explicitUserIds, StringComparer.Ordinal),
            _ => null
        });

    /// <summary>
    /// Event-pipeline overload of the unified seam: the event tag has no
    /// explicit user list ("admins" → admin set, anything else → broadcast).
    /// Event semantics: only "admins" is gated; "users"/"all"/null/corrupt all
    /// broadcast (the message path's explicit-ids leg has no meaning here).
    /// </summary>
    public static BroadcastTargets ResolveAudience(
        string? audience,
        IReadOnlyList<string> adminUserIds)
        => string.Equals(audience, "admins", StringComparison.OrdinalIgnoreCase)
            ? BroadcastTargets.From(new HashSet<string>(adminUserIds, StringComparer.Ordinal))
            : BroadcastTargets.All;

    /// <summary>
    /// Payload overload: resolves a stored <see cref="AudiencePayload"/> (the
    /// message registry's shape) through the same unified seam.
    /// </summary>
    public static BroadcastTargets ResolveAudience(
        AudiencePayload audience,
        IReadOnlyList<string> adminUserIds)
        => ResolveAudience(audience.Type, audience.UserIds, adminUserIds);

    /// <summary>
    /// Whether one user sees content addressed to an audience: admins-gated
    /// content needs an admin, users-gated content needs membership in the
    /// explicit set, anything else ("all", corrupt, absent) is visible. The
    /// one visibility seam behind the inbox projection and any future
    /// audience-gated read, so the push fan-out and the read path share the
    /// same decision instead of re-implementing the switch.
    /// </summary>
    public static bool IsVisible(
        string? audienceType,
        string userId,
        IReadOnlyList<string> explicitUserIds,
        bool isAdmin)
        => audienceType switch
        {
            { } type when string.Equals(type, "admins", StringComparison.OrdinalIgnoreCase) => isAdmin,
            { } type when string.Equals(type, "users", StringComparison.OrdinalIgnoreCase)
                => explicitUserIds.Contains(userId, StringComparer.Ordinal),
            _ => true
        };

}
