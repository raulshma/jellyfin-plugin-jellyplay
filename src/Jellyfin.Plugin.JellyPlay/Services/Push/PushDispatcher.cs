using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Api;
using Jellyfin.Plugin.JellyPlay.Configuration;
using Jellyfin.Plugin.JellyPlay.Services.Admin;
using Jellyfin.Plugin.JellyPlay.Services.Devices;
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

    /// <summary>
    /// The silent trigger kind (registry v7): a data-only nudge telling the
    /// client to flush its sync queue. Delivered ONLY to devices whose
    /// registered caps include "silent-push" — devices that never advertised
    /// the cap must not receive it, because clients without silent handling
    /// render unknown kinds as visible notifications.
    /// </summary>
    public const string SyncNudge = "sync-nudge";
}

/// <summary>Device capability strings (registry v7, self-reported at registration) the dispatcher gates on.</summary>
public static class DeviceCaps
{
    /// <summary>The cap that opts a device into sync-nudge delivery.</summary>
    public const string SilentPush = "silent-push";
}

/// <summary>One push notification fanned out to every push-registered device of the target users.</summary>
public sealed record PushMessage(string Kind, string Title, string Body, string? ItemId = null);

/// <summary>Transport seam: issue one prepared request (tests substitute a fake; production uses the named pooled client).</summary>
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

    /// <summary>Named IHttpClientFactory client behind both push transports (dispatcher + FCM token exchange).</summary>
    public const string HttpClientName = "jellyplay-push";

    /// <summary>Bounded fan-out: at most this many device sends in flight per dispatch (a large broadcast must not open 100 sockets at once).</summary>
    private const int MaxConcurrentSends = 8;

    private const string NtfyKindHeader = "X-JellyPlay-Kind";
    private const string NtfyItemIdHeader = "X-JellyPlay-ItemId";
    private const string FcmSendUrlPrefix = "https://fcm.googleapis.com/v1/projects/";

    private readonly JellyPlayDatabase _db;
    private readonly Func<PushConfig> _config;
    private readonly ILogger<PushDispatcher> _logger;
    private readonly PushSender _sender;
    private readonly FcmTokenProvider? _fcmTokens;

    /// <summary>DI constructor: real transport with a per-request 10s budget.</summary>
    public PushDispatcher(JellyPlayDatabase db, Func<PushConfig> config, ILogger<PushDispatcher> logger, IHttpClientFactory httpFactory, FcmTokenProvider fcmTokens)
        : this(db, config, logger, NamedClientSender(httpFactory), fcmTokens)
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

        await SendToManyAsync(devices, message, "Push dispatch").ConfigureAwait(false);
    }

    /// <summary>
    /// Fire-and-forget sync-nudge to the user's silent-push-capable devices —
    /// the settings-sync trigger for devices not presumed connected via the
    /// settings SSE stream. Skipped entirely when the user's devices lack the
    /// "silent-push" cap or push is disabled.
    /// </summary>
    public void DispatchSyncNudge(string userId)
    {
        if (!_config().Enabled)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await DispatchSyncNudgeAsync(userId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Sync-nudge fan-out crashed unexpectedly");
            }
        });
    }

    /// <summary>Synchronous-for-the-caller sync-nudge fan-out. Never throws.</summary>
    internal async Task DispatchSyncNudgeAsync(string userId)
    {
        if (!_config().Enabled)
        {
            return;
        }

        List<DeviceRow> devices;
        try
        {
            devices = new List<DeviceRow>(_db.GetPushDevices(new[] { userId }));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Sync-nudge dispatch: could not query push-registered devices");
            return;
        }

        // The query already excludes revoked devices (ADR-0005, enforced in the
        // registry SQL); the nudge additionally requires the silent-push cap.
        var capable = devices
            .Where(device => CapsInclude(device.CapsJson, DeviceCaps.SilentPush))
            .ToList();
        if (capable.Count == 0)
        {
            return;
        }

        var message = new PushMessage(PushKinds.SyncNudge, "JellyPlay", "settings-changed");
        await SendToManyAsync(capable, message, "Sync-nudge dispatch").ConfigureAwait(false);
    }

    /// <summary>
    /// The ONE fan-out behind every dispatch: resolves the FCM token lazily
    /// EXACTLY once (a fetch failure or unconfigured FCM skips every fcm device
    /// with no retry storm — the token is cached ~55 min inside the provider),
    /// then sends to all devices with bounded parallelism
    /// (<see cref="MaxConcurrentSends"/> in flight) so a 100-device broadcast is
    /// ten 10s waves, not a thousand seconds of serial waiting. Per-device
    /// exception isolation lives in <see cref="DispatchOneAsync"/>.
    /// </summary>
    private async Task SendToManyAsync(IReadOnlyList<DeviceRow> devices, PushMessage message, string logContext)
    {
        string? fcmToken = null;
        var fcmResolved = false;
        using var gate = new SemaphoreSlim(MaxConcurrentSends, MaxConcurrentSends);
        var sends = new List<Task>(devices.Count);
        foreach (var device in devices)
        {
            if (PushRegistrations.IsFcmKind(device.PushKind))
            {
                if (!fcmResolved)
                {
                    fcmResolved = true;
                    fcmToken = _fcmTokens is null
                        ? null
                        : await _fcmTokens.GetTokenAsync(CancellationToken.None).ConfigureAwait(false);
                    if (fcmToken is null)
                    {
                        _logger.LogDebug("{Context}: FCM transport unavailable (unconfigured or token fetch failed)", logContext);
                    }
                }

                if (fcmToken is null)
                {
                    _logger.LogDebug("Push to device {DeviceId} skipped — fcm kind but FCM is not available", device.DeviceId);
                    continue;
                }
            }

            await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            sends.Add(SendOneAsync(gate, device, message, fcmToken));
        }

        await Task.WhenAll(sends).ConfigureAwait(false);
    }

    /// <summary>One send under the fan-out's concurrency gate (the slot is always released, even when the send throws).</summary>
    private async Task SendOneAsync(SemaphoreSlim gate, DeviceRow device, PushMessage message, string? fcmToken)
    {
        try
        {
            await DispatchOneAsync(device, message, fcmToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Whether the device's registered caps JSON includes the capability.</summary>
    internal static bool CapsInclude(string? capsJson, string cap)
        => DeviceRegistryService.ParseCaps(capsJson).Contains(cap, StringComparer.Ordinal);

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
        if (PushRegistrations.IsFcmKind(device.PushKind))
        {
            var request = new HttpRequestMessage(
                HttpMethod.Post,
                FcmSendUrlPrefix + Uri.EscapeDataString(_config().FcmProjectId) + "/messages:send");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fcmBearerToken);
            request.Content = new StringContent(BuildFcmPayload(message, device.PushEndpoint ?? string.Empty), Encoding.UTF8, "application/json");
            return request;
        }

        var request2 = new HttpRequestMessage(HttpMethod.Post, device.PushEndpoint);
        var content = PushRegistrations.IsNtfyKind(device.PushKind)
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
    /// non-string data values); itemId present only when set. The silent
    /// sync-nudge kind ships DATA-ONLY (no notification block) — a visible
    /// payload would defeat its purpose.
    /// </summary>
    internal static string BuildFcmPayload(PushMessage message, string fcmRegistrationToken)
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
    /// <paramref name="userName"/> (id fallback — <see cref="AdminUsers.DisplayName"/>);
    /// registeredAt prefers the row's CreatedAt and falls back to LastSeen for
    /// pre-v4 rows.
    /// </summary>
    public AdminPushOverviewResponse GetAdminOverview(Func<Guid, string?> userName)
    {
        var devices = _db.GetAllPushDevices()
            .Select(row => new AdminPushDeviceDto(
                row.DeviceId,
                AdminUsers.DisplayName(row.UserId, userName),
                row.Name,
                row.PushKind ?? string.Empty,
                EndpointHost(row.PushEndpoint),
                row.CreatedAt ?? row.LastSeen))
            .ToList();

        return new AdminPushOverviewResponse(_config().Enabled, _config().FcmConfigured(), devices);
    }

    /// <summary>Sends through the pooled named client (created per request; the factory owns the handler lifetime).</summary>
    private static PushSender NamedClientSender(IHttpClientFactory httpFactory)
        => (request, cancellationToken) => httpFactory.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
}

