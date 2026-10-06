using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyPlay.Services.Seerr;

/// <summary>
/// Transport seam for the Seerr bridge (same pattern as the push
/// PushSender): issue one prepared request; tests substitute a fake,
/// production uses the named pooled client. Login flows pass a
/// CookieContainer to capture the Seerr session cookie; proxy and validation
/// flows pass null (the user's session rides the request's Cookie header).
/// Callers own the returned response's lifetime.
/// </summary>
public delegate Task<HttpResponseMessage> SeerrSender(HttpRequestMessage request, CookieContainer? cookieContainer, CancellationToken cancellationToken);

/// <summary>Production adapter for <see cref="SeerrSender"/>.</summary>
public sealed class HttpClientSeerrSender
{
    public const int LoginTimeoutSeconds = 15;

    private readonly IHttpClientFactory _httpFactory;

    public HttpClientSeerrSender(IHttpClientFactory httpFactory)
    {
        _httpFactory = httpFactory;
    }

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CookieContainer? cookieContainer, CancellationToken cancellationToken)
    {
        // Proxy/validation: the pooled named client, returning at response
        // headers so body streaming (calendar, images) is not bounded by the
        // client's 15s budget. Login: a dedicated handler so the session
        // cookie lands in the caller's container (and redirects are followed).
        if (cookieContainer is null)
        {
            var client = _httpFactory.CreateClient("JellyPlayHttpClient");
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }

        using var handler = new HttpClientHandler { CookieContainer = cookieContainer, AllowAutoRedirect = true };
        using var loginClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(LoginTimeoutSeconds) };
        return await loginClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
