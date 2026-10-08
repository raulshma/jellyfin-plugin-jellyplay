using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.JellyPlay.Services.Push;

/// <summary>
/// The one push-payload module: pure builders for every transport (generic
/// UnifiedPush, ntfy JSON publish, FCM HTTP v1) plus the two URL helpers the
/// transport needs. Small interface, deep behaviour — the JSON shapes pinned
/// in docs/CONTRACT.md ("Push") live here, so the Dispatch module leverages
/// one seam instead of owning shaping itself. The interface is the test
/// surface: payload bytes are pinnable without a database or HTTP.
/// </summary>
public static class PushPayloads
{
    /// <summary>Named IHttpClientFactory client behind both push transports (dispatcher + FCM token exchange).</summary>
    public const string HttpClientName = "jellyplay-push";

    internal const string NtfyKindHeader = "X-JellyPlay-Kind";
    internal const string NtfyItemIdHeader = "X-JellyPlay-ItemId";

    /// <summary>Generic UnifiedPush body: {title, body, kind, itemId?}.</summary>
    public static string BuildGenericPayload(PushMessage message)
    {
        var payload = new JObject
        {
            ["title"] = message.Title,
            ["body"] = message.Body,
            ["kind"] = message.Kind
        };

        if (!string.IsNullOrEmpty(message.ItemId))
        {
            payload["itemId"] = message.ItemId;
        }

        return payload.ToString(Formatting.None);
    }

    /// <summary>
    /// ntfy JSON publish format (ntfy &gt;= 2.x): machine fields (kind, itemId)
    /// ride the supported per-message "headers" map rather than the topic body.
    /// </summary>
    public static string BuildNtfyPayload(PushMessage message, string topic)
    {
        var headers = new JObject { [NtfyKindHeader] = message.Kind };
        if (!string.IsNullOrEmpty(message.ItemId))
        {
            headers[NtfyItemIdHeader] = message.ItemId;
        }

        var payload = new JObject
        {
            ["topic"] = topic,
            ["title"] = message.Title,
            ["message"] = message.Body,
            ["tags"] = new JArray("jellyplay"),
            ["priority"] = "default",
            ["headers"] = headers
        };

        return payload.ToString(Formatting.None);
    }

    /// <summary>
    /// FCM HTTP v1 send body: the device's endpoint IS the FCM registration
    /// token; machine fields ride the string-typed "data" map (FCM forbids
    /// non-string data values); itemId present only when set. The silent
    /// sync-nudge kind ships DATA-ONLY (no notification block) — a visible
    /// payload would defeat its purpose.
    /// </summary>
    public static string BuildFcmPayload(PushMessage message, string fcmRegistrationToken)
    {
        var data = new JObject { ["kind"] = message.Kind };
        if (!string.IsNullOrEmpty(message.ItemId))
        {
            data["itemId"] = message.ItemId;
        }

        var inner = new JObject { ["token"] = fcmRegistrationToken };
        if (!string.Equals(message.Kind, PushKinds.SyncNudge, StringComparison.Ordinal))
        {
            inner["notification"] = new JObject
            {
                ["title"] = message.Title,
                ["body"] = message.Body
            };
        }

        inner["data"] = data;
        inner["android"] = new JObject { ["priority"] = "NORMAL" };

        var payload = new JObject { ["message"] = inner };
        return payload.ToString(Formatting.None);
    }

    /// <summary>The ntfy topic is the last path segment of the publish URL; null when the endpoint does not parse.</summary>
    public static string? ExtractNtfyTopic(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var segment = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        return segment is null ? null : Uri.UnescapeDataString(segment);
    }

    /// <summary>Admin overview host projection: host only, never path or scheme.</summary>
    public static string EndpointHost(string? endpoint)
        => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;
}
