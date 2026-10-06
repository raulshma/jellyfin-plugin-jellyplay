using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Configuration;
using Jellyfin.Plugin.JellyPlay.Storage;
using Jellyfin.Plugin.JellyPlay.Storage.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.JellyPlay.Services.Push;

/// <summary>Wire kinds a push notification can carry (mirrors the SSE event families that push).</summary>
public static class PushKinds
{
    public const string NewMedia = "new-media";
    public const string Broadcast = "broadcast";
    public const string Message = "message";
}

/// <summary>One push notification fanned out to every push-registered device of the target users.</summary>
public sealed record PushMessage(string Kind, string Title, string Body, string? ItemId = null);

/// <summary>Transport seam: issue one prepared request (tests substitute a fake; production uses a shared client).</summary>
public delegate Task<HttpResponseMessage> PushSender(HttpRequestMessage request, CancellationToken cancellationToken);

/// <summary>
/// The plugin-side push server: fans out notifications to ntfy, generic
/// UnifiedPush and FCM (Google, for Play-Store client builds) endpoints
/// persisted on device rows. Fire-and-forget — callers never block and
/// failures are logged at Debug per device with no retry; SSE remains the
/// reliable in-session channel. Payload shapes are pinned in
/// docs/CONTRACT.md ("Push").
/// </summary>
public sealed class PushDispatcher
{
    /// <summary>Per-endpoint budget; a slow distributor must not hold the background worker.</summary>
    public const int TimeoutSeconds = 10;

    private const string NtfyKindHeader = "X-JellyPlay-Kind";
    private const string NtfyItemIdHeader = "X-JellyPlay-ItemId";
    private const string FcmSendUrlPrefix = "https://fcm.googleapis.com/v1/projects/";

    private static readonly HttpClient SharedClient = CreateSharedClient();

    private readonly JellyPlayDatabase _db;
    private readonly Func<PushConfig> _config;
    private readonly ILogger<PushDispatcher> _logger;
    private readonly PushSender _sender;
    private readonly FcmTokenProvider? _fcmTokens;

    /// <summary>DI constructor: real transport with a per-request 10s budget.</summary>
    public PushDispatcher(JellyPlayDatabase db, Func<PushConfig> config, ILogger<PushDispatcher> logger, FcmTokenProvider fcmTokens)
        : this(db, config, logger, SharedClientSendAsync, fcmTokens)
    {
    }

    /// <summary>Test constructor with an injectable sender seam (and optional FCM token provider).</summary>
    internal PushDispatcher(JellyPlayDatabase db, Func<PushConfig> config, ILogger<PushDispatcher> logger, PushSender sender, FcmTokenProvider? fcmTokens = null)
    {
        _db = db;
        _config = config;
        _logger = logger;
        _sender = sender;
        _fcmTokens = fcmTokens;
    }

    /// <summary>Registration gate: kind must be "generic"|"ntfy"|"fcm" and the endpoint a non-blank value (an FCM registration token for fcm).</summary>
    public static bool IsValidRegistration(string? kind, string? endpoint) => PushRegistrations.IsValidRegistration(kind, endpoint);

    /// <summary>The fcm kind additionally requires configured FCM credentials to be usable (400 push-kind-unavailable otherwise).</summary>
    public static bool IsFcmKind(string? kind) => PushRegistrations.IsFcmKind(kind);

    /// <summary>
    /// Fire-and-forget fan-out to the push-registered devices of
    /// <paramref name="userIds"/> (null = every user, e.g. "all" audiences;
    /// an empty collection delivers to nobody). Returns immediately — the
    /// dispatch continues on a background task with full exception isolation.
    /// </summary>
    public void DispatchToUsers(PushMessage message, IReadOnlyCollection<string>? userIds)
    {
        if (!_config().Enabled || userIds is { Count: 0 })
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await DispatchAsync(message, userIds).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // DispatchAsync isolates per device; this guards the fan-out loop itself.
                _logger.LogDebug(ex, "Push fan-out crashed unexpectedly");
            }
        });
    }

    /// <summary>Synchronous-for-the-caller variant (queries targets, awaits all sends). Never throws.</summary>
    internal async Task DispatchAsync(PushMessage message, IReadOnlyCollection<string>? userIds)
    {
        if (!_config().Enabled)
        {
            return;
        }

        List<DeviceRow> devices;
        try
        {
            devices = new List<DeviceRow>(_db.GetPushDevices(userIds));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Push dispatch: could not query push-registered devices");
            return;
        }

        // One token per fan-out (cached ~55 min inside the provider): a fetch
        // failure or unconfigured FCM skips every fcm device with no retry storm.
        string? fcmToken = null;
        var fcmResolved = false;
        foreach (var device in devices)
        {
            if (IsFcmKind(device.PushKind))
            {
                if (!fcmResolved)
                {
                    fcmResolved = true;
                    fcmToken = _fcmTokens is null
                        ? null
                        : await _fcmTokens.GetTokenAsync(CancellationToken.None).ConfigureAwait(false);
                    if (fcmToken is null)
                    {
                        _logger.LogDebug("Push dispatch: FCM transport unavailable (unconfigured or token fetch failed)");
                    }
                }

                if (fcmToken is null)
                {
                    _logger.LogDebug("Push to device {DeviceId} skipped — fcm kind but FCM is not available", device.DeviceId);
                    continue;
                }
            }

            await DispatchOneAsync(device, message, fcmToken).ConfigureAwait(false);
        }
    }

    private async Task DispatchOneAsync(DeviceRow device, PushMessage message, string? fcmToken)
    {
        try
        {
            using var request = BuildRequest(device, message, fcmToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
            timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
            using var response = await _sender(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "Push to device {DeviceId} ({EndpointHost}) returned {StatusCode}",
                    device.DeviceId, EndpointHost(device.PushEndpoint), (int)response.StatusCode);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug(
                "Push to device {DeviceId} ({EndpointHost}) timed out after {Seconds}s",
                device.DeviceId, EndpointHost(device.PushEndpoint), TimeoutSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Push to device {DeviceId} ({EndpointHost}) failed",
                device.DeviceId, EndpointHost(device.PushEndpoint));
        }
    }

    /// <summary>Builds the kind-specific POST (never throws for a registered device; ntfy topics unresolvable from the URL yield a null-content-free request the transport will fail).</summary>
    internal HttpRequestMessage BuildRequest(DeviceRow device, PushMessage message, string? fcmBearerToken)
    {
        if (IsFcmKind(device.PushKind))
        {
            var request = new HttpRequestMessage(
                HttpMethod.Post,
                FcmSendUrlPrefix + Uri.EscapeDataString(_config().FcmProjectId) + "/messages:send");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fcmBearerToken);
            request.Content = new StringContent(BuildFcmPayload(message, device.PushEndpoint ?? string.Empty), Encoding.UTF8, "application/json");
            return request;
        }

        var request2 = new HttpRequestMessage(HttpMethod.Post, device.PushEndpoint);
        var content = IsNtfy(device.PushKind)
            ? BuildNtfyPayload(message, ExtractNtfyTopic(device.PushEndpoint) ?? string.Empty)
            : BuildGenericPayload(message);
        request2.Content = new StringContent(content, Encoding.UTF8, "application/json");
        return request2;
    }

    /// <summary>Generic UnifiedPush body: {title, body, kind, itemId?}.</summary>
    internal static string BuildGenericPayload(PushMessage message)
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
    /// ntfy JSON publish format (ntfy >= 2.x): machine fields (kind, itemId)
    /// ride the supported per-message "headers" map rather than the topic body.
    /// </summary>
    internal static string BuildNtfyPayload(PushMessage message, string topic)
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
    /// non-string data values); itemId present only when set.
    /// </summary>
    internal static string BuildFcmPayload(PushMessage message, string fcmRegistrationToken)
    {
        var data = new JObject { ["kind"] = message.Kind };
        if (!string.IsNullOrEmpty(message.ItemId))
        {
            data["itemId"] = message.ItemId;
        }

        var payload = new JObject
        {
            ["message"] = new JObject
            {
                ["token"] = fcmRegistrationToken,
                ["notification"] = new JObject
                {
                    ["title"] = message.Title,
                    ["body"] = message.Body
                },
                ["data"] = data,
                ["android"] = new JObject { ["priority"] = "NORMAL" }
            }
        };

        return payload.ToString(Formatting.None);
    }

    /// <summary>The ntfy topic is the last path segment of the publish URL; null when the endpoint does not parse.</summary>
    internal static string? ExtractNtfyTopic(string? endpoint)
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
    internal static string EndpointHost(string? endpoint)
        => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;

    /// <summary>
    /// Cross-user push fleet view for admins. Names resolve through
    /// <paramref name="userName"/> (id fallback); registeredAt prefers the
    /// row's CreatedAt and falls back to LastSeen for pre-v4 rows.
    /// </summary>
    public AdminPushOverviewResponse GetAdminOverview(Func<Guid, string?> userName)
    {
        var devices = _db.GetAllPushDevices()
            .Select(row =>
            {
                var name = Guid.TryParse(row.UserId, out var guid) && guid != Guid.Empty
                    ? userName(guid)
                    : null;
                return new AdminPushDeviceDto(
                    row.DeviceId,
                    string.IsNullOrWhiteSpace(name) ? row.UserId : name!,
                    row.Name,
                    row.PushKind ?? string.Empty,
                    EndpointHost(row.PushEndpoint),
                    row.CreatedAt ?? row.LastSeen);
            })
            .ToList();

        return new AdminPushOverviewResponse(_config().Enabled, _config().FcmConfigured(), devices);
    }

    private static bool IsNtfy(string? kind) => PushRegistrations.IsNtfyKind(kind);

    private static HttpClient CreateSharedClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(TimeoutSeconds)
        })
        {
            Timeout = Timeout.InfiniteTimeSpan // each request is bounded by its own 10s CTS
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("jellyfin-plugin-jellyplay/1.0");
        return client;
    }

    private static async Task<HttpResponseMessage> SharedClientSendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => await SharedClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
}

/// <summary>Admin overview row: endpoint URLs are secrets — only the host is ever surfaced.</summary>
public sealed record AdminPushDeviceDto(
    string DeviceId,
    string UserName,
    string DeviceName,
    string Kind,
    string EndpointHost,
    long RegisteredAt);

/// <summary>Response shape for GET jellyplay/admin/push/overview. FCM readiness is reported as a boolean only — the service-account key is never surfaced.</summary>
public sealed record AdminPushOverviewResponse(bool Enabled, bool FcmConfigured, IReadOnlyList<AdminPushDeviceDto> Devices);

