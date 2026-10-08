using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyPlay.Configuration;
using Jellyfin.Plugin.JellyPlay.Storage;
using Jellyfin.Plugin.JellyPlay.Storage.Models;

namespace Jellyfin.Plugin.JellyPlay.Services.Push;

/// <summary>
/// The ONE push-eligibility module: every push-gating rule behind one
/// interface, so the registry, the dispatch and the settings-sync trigger all
/// leverage the same seams instead of re-implementing caps parsing or FCM
/// readiness in three places.
///
/// Locality: caps JSON parsing + membership, push-eligibility (registered +
/// not revoked), the sync-nudge target query (push-eligible + silent-push
/// cap, caps parsed once per row) and FCM usability (project id +
/// service-account key) live here. The historical homes
/// (<see cref="Devices.DeviceRegistryService.ParseCaps"/>,
/// <see cref="DeviceCaps"/>, <c>FcmConfigured</c>) stay as thin adapters
/// delegating here, so no caller breaks.
/// </summary>
public static class PushEligibility
{
    /// <summary>The cap that opts a device into sync-nudge delivery (registry v7).</summary>
    public const string SilentPushCap = "silent-push";

    /// <summary>Parses a device row's caps JSON array; malformed payloads degrade to empty.</summary>
    public static IReadOnlyList<string> ParseCaps(string? capsJson)
    {
        if (string.IsNullOrEmpty(capsJson))
        {
            return Array.Empty<string>();
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(capsJson) ?? new List<string>();
        }
        catch (System.Text.Json.JsonException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Whether the device's registered caps JSON includes the capability (the one caps-membership seam).</summary>
    public static bool CapsInclude(string? capsJson, string cap)
        => ParseCaps(capsJson).Contains(cap, StringComparer.Ordinal);

    /// <summary>
    /// Whether the row is push-eligible: a push registration is present
    /// (kind + non-empty endpoint) and the row is not revoked. Mirrors the
    /// <c>GetPushDevices</c> source filter (ADR-0005: revoked exclusion at the
    /// source) so in-memory filtering and the SQL query can never disagree.
    /// </summary>
    public static bool IsPushEligible(DeviceRow row)
        => row.PushKind is not null
            && row.PushEndpoint is not null
            && row.PushEndpoint != string.Empty
            && !row.Revoked;

    /// <summary>
    /// The ONE FCM-readiness seam: the fcm push kind is usable only when both
    /// the project id and a service-account key are set. Replaces the triple
    /// <c>FcmConfigured()</c> check (registry gate, token provider, admin
    /// overview) so the readiness rule has a single home.
    /// </summary>
    public static bool IsFcmUsable(PushConfig config)
        => !string.IsNullOrWhiteSpace(config.FcmProjectId)
            && !string.IsNullOrWhiteSpace(config.FcmServiceAccountJson);

    /// <summary>
    /// The one sync-nudge target query: the user's push-registered devices
    /// (revoked excluded at the source), re-checked for push-eligibility in
    /// memory (mirrors the <c>GetPushDevices</c> source filter, ADR-0005, so
    /// the SQL query and this check can never disagree) and filtered to the
    /// silent-push cap with each row's caps JSON parsed ONCE. Devices that
    /// never advertised the cap must not receive the nudge — clients without
    /// silent handling render unknown kinds as visible notifications. Both
    /// <see cref="PushDispatcher.DispatchSyncNudgeAsync"/> and the settings-sync
    /// <c>PublishChanged</c> fallback share this seam, so the nudge audience
    /// cannot drift between callers.
    /// </summary>
    public static IReadOnlyList<DeviceRow> GetSyncNudgeDevices(JellyPlayDatabase db, string userId)
    {
        var capable = new List<DeviceRow>();
        foreach (var row in db.GetPushDevices(new[] { userId }))
        {
            if (!IsPushEligible(row))
            {
                continue;
            }

            if (CapsInclude(row.CapsJson, SilentPushCap))
            {
                capable.Add(row);
            }
        }

        return capable;
    }
}
