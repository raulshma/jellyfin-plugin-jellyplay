using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.JellyPlay.Services.Push;

/// <summary>One parsed Firebase service-account key file (subset of fields the token flow needs).</summary>
internal sealed record FcmServiceAccount(string ClientEmail, string PrivateKeyPem, string TokenUri);

/// <summary>
/// OAuth2 access tokens for the FCM HTTP v1 API via the JWT-bearer grant,
/// dependency-free (BCL crypto only). The service-account JSON is parsed into
/// a JWT signed RS256 with the account's PKCS8 key, exchanged at the token
/// endpoint, and the resulting access token is cached in memory until five
/// minutes before expiry with a single-flight refresh. A malformed key or
/// JSON latches a permanent failure (logged once at Error) instead of
/// retry-storming Google; pasting a different key resets the latch.
/// The service-account content itself is never logged.
/// </summary>
public sealed class FcmTokenProvider
{
    /// <summary>OAuth2 scope every FCM v1 send requires.</summary>
    public const string MessagingScope = "https://www.googleapis.com/auth/firebase.messaging";

    /// <summary>Google's OAuth2 token endpoint (service-account default when token_uri is absent).</summary>
    public const string DefaultTokenUri = "https://oauth2.googleapis.com/token";

    /// <summary>RFC 7523 grant type for service-account JWT assertion exchange.</summary>
    public const string JwtBearerGrantType = "urn:ietf:params:oauth:grant-type:jwt-bearer";

    /// <summary>Access-token budget; Google latency must not hold the push fan-out.</summary>
    public const int TimeoutSeconds = 10;

    /// <summary>JWT assertion lifetime (Google allows up to one hour).</summary>
    public const int AssertionLifetimeSeconds = 3600;

    private static readonly TimeSpan RefreshLead = TimeSpan.FromMinutes(5);

    private readonly Func<PushConfig> _config;
    private readonly ILogger<FcmTokenProvider> _logger;
    private readonly Func<DateTimeOffset> _clock;
    private readonly PushSender _sender;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    // Key state (only mutated while holding _refreshLock).
    private string? _loadedKeyJson;
    private bool _keyInvalid;
    private RSA? _signingKey;
    private string _clientEmail = string.Empty;
    private string _tokenUri = DefaultTokenUri;

    // Token cache (written while holding _refreshLock; read unlocked — the
    // worst race is one redundant refresh, which the lock then collapses).
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiry;

    /// <summary>DI constructor: real transport with a per-request 10s budget.</summary>
    public FcmTokenProvider(Func<PushConfig> config, ILogger<FcmTokenProvider> logger, IHttpClientFactory httpFactory)
        : this(config, logger, static () => DateTimeOffset.UtcNow, NamedClientSender(httpFactory))
    {
    }

    /// <summary>Test constructor with injectable clock and raw HTTP sender seams.</summary>
    internal FcmTokenProvider(Func<PushConfig> config, ILogger<FcmTokenProvider> logger, Func<DateTimeOffset> clock, PushSender sender)
    {
        _config = config;
        _logger = logger;
        _clock = clock;
        _sender = sender;
    }

    /// <summary>
    /// A cached or freshly-minted OAuth2 access token, or null when FCM is
    /// unconfigured, the key is (permanently) invalid, or the exchange
    /// transiently failed. Never throws.
    /// </summary>
    public async Task<string?> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        if (!PushEligibility.IsFcmUsable(_config()))
        {
            return null;
        }

        var cached = ValidCachedToken(_clock());
        if (cached is not null)
        {
            return cached;
        }

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var config = _config();
            if (!PushEligibility.IsFcmUsable(config) || !EnsureKeyLoaded(config.FcmServiceAccountJson))
            {
                return null;
            }

            cached = ValidCachedToken(_clock());
            if (cached is not null)
            {
                return cached;
            }

            return await RequestTokenAsync(_clock(), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null; // caller cancelled; nothing cached, no latch
        }
        catch (Exception ex)
        {
            // Transient exchange failure: log, don't cache, don't latch — the
            // next dispatch attempt may try again once (never in a loop).
            _logger.LogDebug(ex, "FCM token exchange failed unexpectedly");
            return null;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// Parses the service-account JSON and imports its private key. Called on
    /// first use and whenever the pasted JSON changes; invalid content logs
    /// once at Error and latches (returning null) until the content changes.
    /// </summary>
    private bool EnsureKeyLoaded(string serviceAccountJson)
    {
        if (_loadedKeyJson is not null && string.Equals(_loadedKeyJson, serviceAccountJson, StringComparison.Ordinal))
        {
            return !_keyInvalid;
        }

        ResetKeyState();
        _loadedKeyJson = serviceAccountJson;
        try
        {
            var account = TryParseServiceAccount(serviceAccountJson)
                ?? throw new FormatException("service-account JSON must contain non-blank client_email and private_key");

            var rsa = RSA.Create();
            try
            {
                rsa.ImportFromPem(FixPemNewlines(account.PrivateKeyPem));
                if (rsa.KeySize < 2048)
                {
                    throw new InvalidOperationException($"service-account RSA key must be 2048+ bits (got {rsa.KeySize})");
                }

                _signingKey = rsa;
            }
            catch
            {
                rsa.Dispose();
                throw;
            }

            _clientEmail = account.ClientEmail;
            _tokenUri = account.TokenUri;
            return true;
        }
        catch (Exception ex)
        {
            _keyInvalid = true;
            _logger.LogError(
                ex,
                "FCM push: the configured service-account key is invalid; FCM dispatch stays off until it is replaced");
            return false;
        }
    }

    private async Task<string?> RequestTokenAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var issuedAt = now.ToUnixTimeSeconds();
        var assertion = BuildJwt(_clientEmail, _signingKey!, _tokenUri, issuedAt, issuedAt + AssertionLifetimeSeconds);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
            using var request = new HttpRequestMessage(HttpMethod.Post, _tokenUri);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = JwtBearerGrantType,
                ["assertion"] = assertion
            });

            using var response = await _sender(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("FCM token endpoint returned {StatusCode}", (int)response.StatusCode);
                return null;
            }

            var body = JObject.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            var accessToken = body.Value<string>("access_token");
            var expiresIn = long.TryParse(
                body["expires_in"]?.ToString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var seconds) ? seconds : 0;
            if (string.IsNullOrEmpty(accessToken) || expiresIn <= 0)
            {
                _logger.LogDebug("FCM token endpoint response is missing access_token or a positive expires_in");
                return null;
            }

            _accessToken = accessToken;
            _accessTokenExpiry = now + TimeSpan.FromSeconds(expiresIn);
            return accessToken;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("FCM token request timed out after {Seconds}s", TimeoutSeconds);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FCM token request failed");
            return null;
        }
    }

    private string? ValidCachedToken(DateTimeOffset now)
        => _accessToken is not null && now < _accessTokenExpiry - RefreshLead ? _accessToken : null;

    private void ResetKeyState()
    {
        _keyInvalid = false;
        _signingKey?.Dispose();
        _signingKey = null;
        _clientEmail = string.Empty;
        _tokenUri = DefaultTokenUri;
        _accessToken = null;
        _accessTokenExpiry = DateTimeOffset.MinValue;
    }

    /// <summary>Service-account PEMs escape newlines as literal "\n" inside the JSON; the pasted content may carry either form.</summary>
    internal static string FixPemNewlines(string pem) => pem.Replace("\\n", "\n", StringComparison.Ordinal);

    /// <summary>Parses the service-account key JSON; null when client_email or private_key is missing/blank.</summary>
    internal static FcmServiceAccount? TryParseServiceAccount(string json)
    {
        try
        {
            var obj = JObject.Parse(json);
            var clientEmail = obj.Value<string>("client_email");
            var privateKey = obj.Value<string>("private_key");
            if (string.IsNullOrWhiteSpace(clientEmail) || string.IsNullOrWhiteSpace(privateKey))
            {
                return null;
            }

            var tokenUri = obj.Value<string>("token_uri");
            return new FcmServiceAccount(
                clientEmail,
                privateKey,
                string.IsNullOrWhiteSpace(tokenUri) ? DefaultTokenUri : tokenUri);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Base64url (RFC 4648 §5, unpadded) — JWT segment encoding.</summary>
    internal static string Base64UrlEncode(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// RS256 JWT assertion: header {alg:RS256,typ:JWT}, payload
    /// {iss,scope,aud,iat,exp} — all base64url, signed PKCS#1 v1.5/SHA-256.
    /// </summary>
    internal static string BuildJwt(string clientEmail, RSA signingKey, string audience, long issuedAtSeconds, long expiresAtSeconds)
    {
        var header = new JObject
        {
            ["alg"] = "RS256",
            ["typ"] = "JWT"
        };
        var payload = new JObject
        {
            ["iss"] = clientEmail,
            ["scope"] = MessagingScope,
            ["aud"] = audience,
            ["iat"] = issuedAtSeconds,
            ["exp"] = expiresAtSeconds
        };

        var signingInput = string.Create(
            CultureInfo.InvariantCulture,
            $"{Base64UrlEncode(Encoding.UTF8.GetBytes(header.ToString(Formatting.None)))}.{Base64UrlEncode(Encoding.UTF8.GetBytes(payload.ToString(Formatting.None)))}");
        var signature = signingKey.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return string.Create(CultureInfo.InvariantCulture, $"{signingInput}.{Base64UrlEncode(signature)}");
    }

    /// <summary>Sends through the pooled named client (created per request; the factory owns the handler lifetime).</summary>
    private static PushSender NamedClientSender(IHttpClientFactory httpFactory)
        => (request, cancellationToken) => httpFactory.CreateClient(PushPayloads.HttpClientName).SendAsync(request, cancellationToken);
}
