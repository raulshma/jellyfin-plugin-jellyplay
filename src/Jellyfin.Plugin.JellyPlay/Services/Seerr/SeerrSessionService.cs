using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Configuration;
using Jellyfin.Plugin.JellyPlay.Storage;
using Jellyfin.Plugin.JellyPlay.Storage.Models;
using MediaBrowser.Controller.QuickConnect;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyPlay.Services.Seerr;

public sealed record SeerrLoginRequest(string AuthType, string? Username, string? Password, string? QuickConnectSecret);

public sealed record SeerrLoginResult(bool Success, string? Error);

/// <summary>
/// Server-side Seerr SSO: password login or a Quick Connect bridge (the plugin
/// authorizes the QC secret against Jellyfin, then exchanges it for a Seerr
/// session). Sessions persist per Jellyfin user — encrypted at rest with a
/// plugin-held key (SecretBox) — and the client never touches the Seerr API key.
/// </summary>
public sealed class SeerrSessionService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly JellyPlayDatabase _db;
    private readonly IQuickConnect _quickConnect;
    private readonly SecretBox _secretBox;
    private readonly ILogger<SeerrSessionService> _logger;

    public SeerrSessionService(IHttpClientFactory httpFactory, JellyPlayDatabase db, IQuickConnect quickConnect, SecretBox secretBox, ILogger<SeerrSessionService> logger)
    {
        _httpFactory = httpFactory;
        _db = db;
        _quickConnect = quickConnect;
        _secretBox = secretBox;
        _logger = logger;
    }

    public bool IsConfigured
    {
        get
        {
            var config = JellyPlayPlugin.Instance!.Configuration.Seerr;
            return !string.IsNullOrEmpty(config.ServerUrl) && !string.IsNullOrEmpty(config.ApiKey);
        }
    }

    public string ServerUrl => JellyPlayPlugin.Instance!.Configuration.Seerr.ServerUrl.TrimEnd('/');

    public async Task<SeerrLoginResult> LoginWithPassword(string jellyfinUserId, string username, string password)
    {
        try
        {
            var cookieContainer = new CookieContainer();
            using var handler = new HttpClientHandler { CookieContainer = cookieContainer, AllowAutoRedirect = true };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };

            using var content = JsonContent.Create(new { username, password });
            using var response = await client.PostAsync($"{ServerUrl}/api/v1/auth/local", content);
            if (!response.IsSuccessStatusCode)
            {
                return new SeerrLoginResult(false, $"seerr-rejected ({(int)response.StatusCode})");
            }

            await PersistSession(jellyfinUserId, cookieContainer, client);
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
            using var handler = new HttpClientHandler { CookieContainer = cookieContainer, AllowAutoRedirect = true };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };

            using var content = JsonContent.Create(new { type = "jellyfin", credentials = quickConnectSecret });
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{ServerUrl}/api/v1/auth/jellyfin") { Content = content };
            request.Headers.Add("X-Api-Key", JellyPlayPlugin.Instance!.Configuration.Seerr.ApiKey);
            using var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return new SeerrLoginResult(false, $"seerr-rejected ({(int)response.StatusCode})");
            }

            await PersistSession(jellyfinUserId, cookieContainer, client);
            return new SeerrLoginResult(true, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Seerr Quick Connect login failed for {User}", jellyfinUserId);
            return new SeerrLoginResult(false, "login-failed");
        }
    }

    private async Task PersistSession(string jellyfinUserId, CookieContainer cookieContainer, HttpClient client)
    {
        var cookies = cookieContainer.GetCookies(new Uri(ServerUrl))
            .Cast<Cookie>()
            .Select(cookie => (cookie.Name, cookie.Value))
            .ToList();

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var payload = _secretBox.Protect(JsonSerializer.Serialize(cookies));
        if (payload is null)
        {
            // Fail closed: no key means no plaintext at rest, and no session.
            _logger.LogError("Seerr session encryption key unavailable; session for {User} was NOT persisted", jellyfinUserId);
            return;
        }

        _db.UpsertSeerrSession(new SeerrSessionRow(jellyfinUserId, payload, now, now));

        // Warm-validation: confirm the session actually resolves a user.
        using var check = new HttpRequestMessage(HttpMethod.Get, $"{ServerUrl}/api/v1/auth/me");
        ApplyCookies(check, cookies);
        using var checkResponse = await client.SendAsync(check);
        if (checkResponse.IsSuccessStatusCode)
        {
            _logger.LogInformation("Seerr session established for {User}", jellyfinUserId);
        }
    }

    public SeerrSessionRow? GetSession(string userId) => _db.GetSeerrSession(userId);

    public bool DeleteSession(string userId) => _db.DeleteSeerrSession(userId);

    /// <summary>Loads the user's session cookies into a request; returns false when absent/expired/undecryptable.</summary>
    public bool TryApplySession(string userId, HttpRequestMessage request)
    {
        var session = _db.GetSeerrSession(userId);
        if (session is null)
        {
            return false;
        }

        var ttlMs = JellyPlayPlugin.Instance!.Configuration.Seerr.SessionTtlHours * 3_600_000L;
        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - session.CreatedAt > ttlMs)
        {
            _db.DeleteSeerrSession(userId);
            return false;
        }

        var cookiesJson = DecodeCookies(session.CookiesPayload);
        if (cookiesJson is null)
        {
            // Encrypted payload without a usable key (lost/corrupted key file):
            // the session is unrecoverable — treat as absent, drop the row.
            _logger.LogWarning("Seerr session payload for {User} could not be decrypted; treating as unlinked", userId);
            _db.DeleteSeerrSession(userId);
            return false;
        }

        ApplyCookies(request, DeserializeCookies(cookiesJson));
        return true;
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

    private static void ApplyCookies(HttpRequestMessage request, IEnumerable<(string Name, string Value)> cookies)
    {
        var header = string.Join("; ", cookies.Select(cookie => $"{cookie.Name}={cookie.Value}"));
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
