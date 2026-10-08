using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Configuration;
using Jellyfin.Plugin.JellyPlay.Helpers;
using Jellyfin.Plugin.JellyPlay.Storage;
using Jellyfin.Plugin.JellyPlay.Storage.Models;
using MediaBrowser.Controller.QuickConnect;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyPlay.Services.Seerr;

public sealed record SeerrLoginRequest(string AuthType, string? Username, string? Password, string? QuickConnectSecret);

public sealed record SeerrLoginResult(bool Success, string? Error);

/// <summary>
/// The one module that owns the Seerr TTL durations (the session TTL derived
/// from config and the two 60s cache windows): a single seam for the windows
/// so a duration change lands in one place. Durations are unchanged — this is
/// locality, not a policy edit.
/// </summary>
internal static class SeerrSessionTtls
{
    /// <summary>Validation-cache window in ms; a write past one window triggers the expired-entries sweep.</summary>
    internal const long ValidationTtlMs = 60_000;

    /// <summary>Session-cookie cache window in ms (same sweep-on-write shape as the validation cache).</summary>
    internal const long CookieCacheTtlMs = 60_000;

    internal static long SessionTtlMs(SeerrConfig config) => config.SessionTtlHours * 3_600_000L;

    internal static bool IsExpired(SeerrSessionRow session, long nowMs, SeerrConfig config)
        => nowMs - session.CreatedAt > SessionTtlMs(config);
}

/// <summary>
/// Server-side Seerr SSO: password login or a Quick Connect bridge (the plugin
/// authorizes the QC secret against Jellyfin, then exchanges it for a Seerr
/// session). Sessions persist per Jellyfin user — encrypted at rest with a
/// plugin-held key (SecretBox) — and the client never touches the Seerr API key.
/// All Seerr traffic crosses the <see cref="SeerrSender"/> transport seam.
/// </summary>
public sealed class SeerrSessionService
{
    private readonly JellyPlayDatabase _db;
    private readonly IQuickConnect _quickConnect;
    private readonly SecretBox _secretBox;
    private readonly Func<SeerrConfig> _config;
    private readonly SeerrSender _sender;
    private readonly ILogger<SeerrSessionService> _logger;
    private readonly TimeProvider _clock;

    // Per-user cookie-header cache with sweep-on-write (the ValidationCache
    // shape): a proxied request must not pay the row read + AES-GCM decrypt +
    // JSON parse on every hit. Entries carry the built header only — never
    // its plaintext sources — and expire at the earlier of the cache window
    // and the session's own TTL.
    private readonly ConcurrentDictionary<string, (long ExpiresAtMs, string Header)> _cookieCache = new();
    private long _lastCookieSweepMs;

    public SeerrSessionService(SeerrSender sender, JellyPlayDatabase db, IQuickConnect quickConnect, SecretBox secretBox, Func<SeerrConfig> config, ILogger<SeerrSessionService> logger, TimeProvider? clock = null)
    {
        _sender = sender;
        _db = db;
        _quickConnect = quickConnect;
        _secretBox = secretBox;
        _config = config;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public bool IsConfigured
    {
        get
        {
            var config = _config();
            return !string.IsNullOrEmpty(config.ServerUrl) && !string.IsNullOrEmpty(config.ApiKey);
        }
    }

    public string ServerUrl => _config().ServerUrl.TrimEnd('/');

    public async Task<SeerrLoginResult> LoginWithPassword(string jellyfinUserId, string username, string password)
    {
        try
        {
            var cookieContainer = new CookieContainer();
            using var content = JsonContent.Create(new { username, password });
            using var response = await _sender(
                new HttpRequestMessage(HttpMethod.Post, $"{ServerUrl}/api/v1/auth/local") { Content = content },
                cookieContainer,
                CancellationToken.None);
            if (!response.IsSuccessStatusCode)
            {
                return new SeerrLoginResult(false, $"seerr-rejected ({(int)response.StatusCode})");
            }

            await PersistSession(jellyfinUserId, cookieContainer);
            return new SeerrLoginResult(true, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Seerr password login failed for {User}", jellyfinUserId);
            return new SeerrLoginResult(false, "login-failed");
        }
    }

    /// <summary>Authorizes the Jellyfin Quick Connect secret locally, then SSOs into Seerr via its jellyfin auth endpoint.</summary>
    public async Task<SeerrLoginResult> LoginWithQuickConnect(string jellyfinUserId, string quickConnectSecret)
    {
        try
        {
            if (!await _quickConnect.AuthorizeRequest(Guid.Parse(jellyfinUserId), quickConnectSecret))
            {
                return new SeerrLoginResult(false, "quickconnect-pending");
            }

            var cookieContainer = new CookieContainer();
            using var content = JsonContent.Create(new { type = "jellyfin", credentials = quickConnectSecret });
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{ServerUrl}/api/v1/auth/jellyfin") { Content = content };
            request.Headers.Add("X-Api-Key", _config().ApiKey);
            using var response = await _sender(request, cookieContainer, CancellationToken.None);
            if (!response.IsSuccessStatusCode)
            {
                return new SeerrLoginResult(false, $"seerr-rejected ({(int)response.StatusCode})");
            }

            await PersistSession(jellyfinUserId, cookieContainer);
            return new SeerrLoginResult(true, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Seerr Quick Connect login failed for {User}", jellyfinUserId);
            return new SeerrLoginResult(false, "login-failed");
        }
    }

    private async Task PersistSession(string jellyfinUserId, CookieContainer cookieContainer)
    {
        var cookies = cookieContainer.GetCookies(new Uri(ServerUrl))
            .Cast<Cookie>()
            .Select(cookie => (cookie.Name, cookie.Value))
            .ToList();

        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        var payload = _secretBox.Protect(JsonSerializer.Serialize(cookies));
        if (payload is null)
        {
            // Fail closed: no key means no plaintext at rest, and no session.
            _logger.LogError("Seerr session encryption key unavailable; session for {User} was NOT persisted", jellyfinUserId);
            return;
        }

        _db.UpsertSeerrSession(new SeerrSessionRow(jellyfinUserId, payload, now, now));
        // The row changed: any cached header for it is stale by construction.
        _cookieCache.TryRemove(jellyfinUserId, out _);

        // Warm-validation: confirm the session actually resolves a user.
        using var check = new HttpRequestMessage(HttpMethod.Get, $"{ServerUrl}/api/v1/auth/me");
        ApplyCookieHeader(check, BuildCookieHeader(cookies));
        using var checkResponse = await _sender(check, null, CancellationToken.None);
        if (checkResponse.IsSuccessStatusCode)
        {
            _logger.LogInformation("Seerr session established for {User}", jellyfinUserId);
        }
    }

    public SeerrSessionRow? GetSession(string userId) => _db.GetSeerrSession(userId);

    public bool DeleteSession(string userId)
    {
        _cookieCache.TryRemove(userId, out _);
        return _db.DeleteSeerrSession(userId);
    }

    /// <summary>
    /// Loads the user's session cookies into a request; returns false when
    /// absent/expired/undecryptable. Cache first (the header for the TTL
    /// window), the authoritative row + decrypt + parse on miss.
    /// </summary>
    public bool TryApplySession(string userId, HttpRequestMessage request)
    {
        var nowMs = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        if (_cookieCache.TryGetValue(userId, out var cached) && nowMs < cached.ExpiresAtMs)
        {
            ApplyCookieHeader(request, cached.Header);
            return true;
        }

        var session = _db.GetSeerrSession(userId);
        if (session is null)
        {
            return false;
        }

        if (SeerrSessionTtls.IsExpired(session, nowMs, _config()))
        {
            _db.DeleteSeerrSession(userId);
            _cookieCache.TryRemove(userId, out _);
            return false;
        }

        var cookiesJson = DecodeCookies(session.CookiesPayload);
        if (cookiesJson is null)
        {
            // Encrypted payload without a usable key (lost/corrupted key file):
            // the session is unrecoverable — treat as absent, drop the row.
            _logger.LogWarning("Seerr session payload for {User} could not be decrypted; treating as unlinked", userId);
            _db.DeleteSeerrSession(userId);
            _cookieCache.TryRemove(userId, out _);
            return false;
        }

        var header = BuildCookieHeader(DeserializeCookies(cookiesJson));
        CacheCookieHeader(userId, nowMs, session.CreatedAt, header);
        ApplyCookieHeader(request, header);
        return true;
    }

    private void CacheCookieHeader(string userId, long nowMs, long sessionCreatedAt, string header)
    {
        // The entry dies with its session: the earlier of the cache window's
        // end and the session's own TTL boundary.
        // Benign race, accepted: a concurrent TryApplySession that read a
        // pre-rotation row can re-cache the old cookie header after the
        // TryRemove in Login/DeleteSession "invalidated" it. The stale entry
        // lives at most the cache window (60s), the old cookies remain valid
        // server-side until they expire there, and the entry self-heals at
        // the next miss past the window.
        var sessionExpiresAt = sessionCreatedAt + SeerrSessionTtls.SessionTtlMs(_config());
        _cookieCache[userId] = (Math.Min(nowMs + SeerrSessionTtls.CookieCacheTtlMs, sessionExpiresAt), header);
        MaybeSweepCookies(nowMs);
    }

    /// <summary>Occasional O(n) sweep so abandoned users do not accumulate forever (the shared <see cref="SweepGate"/>).</summary>
    private void MaybeSweepCookies(long nowMs)
    {
        if (!SweepGate.Enter(ref _lastCookieSweepMs, nowMs, SeerrSessionTtls.CookieCacheTtlMs))
        {
            return;
        }

        foreach (var (key, entry) in _cookieCache)
        {
            if (nowMs >= entry.ExpiresAtMs)
            {
                _cookieCache.TryRemove(key, out _);
            }
        }
    }

    /// <summary>
    /// Payload → plaintext JSON. Versioned payloads decrypt via the SecretBox;
    /// legacy plaintext rows (pre-encryption) pass through as UTF-8 and are
    /// re-encrypted on the session's next write.
    /// </summary>
    private string? DecodeCookies(byte[] payload)
    {
        if (SecretBox.IsEncryptedPayload(payload))
        {
            return _secretBox.TryUnprotect(payload);
        }

        return System.Text.Encoding.UTF8.GetString(payload);
    }

    private static string BuildCookieHeader(IEnumerable<(string Name, string Value)> cookies)
        => string.Join("; ", cookies.Select(cookie => $"{cookie.Name}={cookie.Value}"));

    private static void ApplyCookieHeader(HttpRequestMessage request, string header)
    {
        if (!string.IsNullOrEmpty(header))
        {
            request.Headers.Add("Cookie", header);
        }
    }

    private static List<(string Name, string Value)> DeserializeCookies(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.EnumerateArray()
                .Select(element => (
                    element.GetProperty("Name").GetString() ?? string.Empty,
                    element.GetProperty("Value").GetString() ?? string.Empty))
                .Where(cookie => cookie.Item1.Length > 0)
                .ToList();
        }
        catch (JsonException)
        {
            return new List<(string, string)>();
        }
    }
}
