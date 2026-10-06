using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyPlay.Services.Seerr;

/// <summary>
/// Catch-all Seerr proxy: streams client requests to the configured Seerr
/// instance with the user's stored session (or the admin API key only for
/// explicit server-scoped calls). The plugin is the auth boundary — the Seerr
/// API key never reaches the client. Also owns the session validation probe
/// used by the seerr/validate endpoint. Outbound traffic crosses the
/// <see cref="SeerrSender"/> transport seam; request rewriting is a pure
/// internal step so the auth boundary is testable without a host.
/// </summary>
public sealed class SeerrProxyService
{
    private static readonly TimeSpan ValidationCacheTtl = TimeSpan.FromSeconds(60);

    private readonly SeerrSender _sender;
    private readonly SeerrSessionService _sessions;
    private readonly ILogger<SeerrProxyService> _logger;
    private readonly ConcurrentDictionary<string, (long At, bool Valid)> _validationCache = new();

    private static readonly string[] ForwardedHeaders =
    [
        "Accept", "Content-Type", "Accept-Language"
    ];

    public SeerrProxyService(SeerrSender sender, SeerrSessionService sessions, ILogger<SeerrProxyService> logger)
    {
        _sender = sender;
        _sessions = sessions;
        _logger = logger;
    }

    /// <summary>
    /// Real validation of the user's stored Seerr session: GET /api/v1/auth/me
    /// with the stored cookies — any 2xx is valid; 401/403/transport errors and
    /// a missing/expired session are not. Cached 60s per user to keep the
    /// dashboard's periodic checks cheap.
    /// </summary>
    public async Task<bool> ValidateUserSessionAsync(string userId, CancellationToken cancellationToken)
    {
        if (!_sessions.IsConfigured)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (_validationCache.TryGetValue(userId, out var cached)
            && now - cached.At < (long)ValidationCacheTtl.TotalMilliseconds)
        {
            return cached.Valid;
        }

        var valid = await ValidateOnceAsync(userId, cancellationToken);
        _validationCache[userId] = (now, valid);
        return valid;
    }

    private async Task<bool> ValidateOnceAsync(string userId, CancellationToken cancellationToken)
    {
        try
        {
            using var request = BuildOutboundRequest(HttpMethod.Get, "/api/v1/auth/me", string.Empty);
            if (!_sessions.TryApplySession(userId, request))
            {
                return false;
            }

            using var response = await _sender(request, null, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Seerr session validation failed for {User}", userId);
            return false;
        }
    }

    public async Task ProxyAsync(HttpContext context, string path, string userId, CancellationToken cancellationToken)
    {
        if (!_sessions.IsConfigured)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var request = context.Request;
        var method = new HttpMethod(request.Method);
        var outbound = BuildOutboundRequest(method, $"/api/v1/{path}", request.QueryString.ToString());
        foreach (var header in ForwardedHeaders)
        {
            if (request.Headers.TryGetValue(header, out var values))
            {
                outbound.Headers.TryAddWithoutValidation(header, values.ToString());
            }
        }

        if (request.ContentLength is > 0 || (request.ContentType is not null && method != HttpMethod.Get && method != HttpMethod.Delete))
        {
            outbound.Content = new StreamContent(request.Body);
            if (request.ContentType is not null)
            {
                outbound.Content.Headers.TryAddWithoutValidation("Content-Type", request.ContentType);
            }
        }

        if (!_sessions.TryApplySession(userId, outbound))
        {
            outbound.Dispose();
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "seerr-not-linked" }, cancellationToken);
            return;
        }

        try
        {
            using var response = await _sender(outbound, null, cancellationToken);

            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var header in response.Headers)
            {
                if (header.Key.StartsWith("X-", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.Headers[header.Key] = header.Value.ToArray();
                }
            }

            if (response.Content.Headers.ContentType is not null)
            {
                context.Response.ContentType = response.Content.Headers.ContentType.ToString();
            }

            await response.Content.CopyToAsync(context.Response.Body, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Seerr proxy failure for {Path}", path);
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
            }
        }
        finally
        {
            outbound.Dispose();
        }
    }

    /// <summary>Pure request rewrite: absolute URL against the configured Seerr instance. No session, no transport.</summary>
    internal HttpRequestMessage BuildOutboundRequest(HttpMethod method, string pathAndQuery, string queryString)
        => new(method, $"{_sessions.ServerUrl}{pathAndQuery}{queryString}");
}
