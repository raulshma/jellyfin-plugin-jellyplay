using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.JellyPlay.Api;
using Jellyfin.Plugin.JellyPlay.Storage.Models;

namespace Jellyfin.Plugin.JellyPlay.Services.Settings;

/// <summary>
/// The ONE rejects codec for recorded sync operations: it encodes the capped
/// <c>[{ns,key,reason}]</c> array (camelCase, degrade-to-null on malformed
/// input) and decodes it back, sharing a single serializer options instance.
/// Both the record side (SettingsService batch recording) and every read side
/// (the history endpoint, the audit export) go through here, so the wire
/// shape can never drift between writer and readers.
/// </summary>
public static class SyncRejectsCodec
{
    /// <summary>RejectsJson is capped at this many entries per recorded operation; counts stay authoritative beyond the cap.</summary>
    internal const int MaxRecordedRejects = 10;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Capped rejects JSON for a recorded operation; null when nothing was rejected.</summary>
    public static string? Encode(IReadOnlyList<RejectedSetting> rejected)
    {
        if (rejected.Count == 0)
        {
            return null;
        }

        return JsonSerializer.Serialize(
            rejected.Take(MaxRecordedRejects).Select(r => new SyncRejectDto(r.Ns, r.Key, r.Reason)),
            Options);
    }

    /// <summary>
    /// Decodes a stored rejects payload; null when absent or malformed (the
    /// counts remain authoritative — the same degradation on every read side).
    /// </summary>
    public static List<SyncRejectDto>? Decode(string? rejectsJson)
    {
        if (string.IsNullOrEmpty(rejectsJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<SyncRejectDto>>(rejectsJson, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
