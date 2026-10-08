using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Helpers;
using Jellyfin.Plugin.JellyPlay.Services.Seerr;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MediaBrowser.Common.Api;

namespace Jellyfin.Plugin.JellyPlay.Api;

/// <summary>
/// Seerr bridge: SSO login (password / Quick Connect), session lifecycle,
/// calendar proxy for non-admins, webhook receiver, and the catch-all API proxy.
/// </summary>
[ApiController]
[Authorize]
[Route(JellyPlayContract.RoutePrefix + "/seerr")]
public class SeerrController : JellyPlayControllerBase
{
    private readonly SeerrSessionService _sessions;
    private readonly SeerrProxyService _proxy;
    private readonly SeerrWebhookProvisioner _provisioner;
    private readonly SeerrWebhookIntake _intake;
    private readonly Func<Configuration.SeerrConfig> _config;

    public SeerrController(
        SeerrSessionService sessions,
        SeerrProxyService proxy,
        SeerrWebhookProvisioner provisioner,
        SeerrWebhookIntake intake,
        Func<Configuration.SeerrConfig> config)
    {
        _sessions = sessions;
        _proxy = proxy;
        _provisioner = provisioner;
        _intake = intake;
        _config = config;
    }

    [HttpPost("login")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody, Required] SeerrLoginRequest request)
    {
        var userId = User.GetUserIdString();
        var result = request.AuthType switch
        {
            "password" => await _sessions.LoginWithPassword(userId, request.Username ?? string.Empty, request.Password ?? string.Empty),
            "quickconnect" => await _sessions.LoginWithQuickConnect(userId, request.QuickConnectSecret ?? string.Empty),
            _ => new SeerrLoginResult(false, "unknown-auth-type")
        };

        return result.Success
            ? JellyPlayResponses.Camel(new { linked = true })
            // Byte-stability: the upstream error string goes over the wire
            // verbatim — null stays null, no synthetic fallback.
            : JellyPlayResponses.Error(StatusCodes.Status401Unauthorized, result.Error);
    }

    [HttpGet("status")]
    public IActionResult Status()
    {
        var session = _sessions.GetSession(User.GetUserIdString());
        return JellyPlayResponses.Camel(new
        {
            configured = _sessions.IsConfigured,
            serverUrl = _sessions.IsConfigured ? _sessions.ServerUrl : null,
            linked = session is not null,
            createdAt = session?.CreatedAt
        });
    }

    /// <summary>Re-validates the stored session against Seerr (GET auth/me with the stored cookies); 60s cache.</summary>
    [HttpGet("validate")]
    public async Task<IActionResult> Validate(CancellationToken cancellationToken)
        => JellyPlayResponses.Camel(new { valid = await _proxy.ValidateUserSessionAsync(User.GetUserIdString(), cancellationToken) });

    [HttpDelete("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult Logout()
        => _sessions.DeleteSession(User.GetUserIdString()) ? NoContent() : NotFound();

    [HttpGet("radarr/calendar")]
    public Task ProxyRadarrCalendar(CancellationToken cancellationToken)
        => _proxy.ProxyAsync(HttpContext, "radarr/calendar?advancedFilters=false", User.GetUserIdString(), cancellationToken);

    [HttpGet("sonarr/calendar")]
    public Task ProxySonarrCalendar(CancellationToken cancellationToken)
        => _proxy.ProxyAsync(HttpContext, "sonarr/calendar?advancedFilters=false", User.GetUserIdString(), cancellationToken);

    /// <summary>Catch-all Seerr API proxy (per-user session enforced).</summary>
    [HttpGet("{**path}")]
    [HttpPost("{**path}")]
    [HttpPut("{**path}")]
    [HttpDelete("{**path}")]
    [HttpPatch("{**path}")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public Task Proxy(CancellationToken cancellationToken)
    {
        var path = (string?)HttpContext.Request.RouteValues["path"] ?? string.Empty;
        return _proxy.ProxyAsync(HttpContext, path, User.GetUserIdString(), cancellationToken);
    }

    /// <summary>
    /// Inbound Seerr webhook (AllowAnonymous). Abuse containment runs first —
    /// the rate-limit filter fires before the action (30/min per remote
    /// client, 429 when exceeded) — then the body is handed to the intake
    /// module (secret-match + parse → broadcast) and the outcome mapped
    /// through the serialization gate. The controller owns HTTP only.
    /// </summary>
    [HttpPost("webhook")]
    [AllowAnonymous]
    [RequestSizeLimit(256 * 1024)] // anonymous inbound — cap the unauthenticated read before parsing
    [RateLimit(Services.Admin.RateLimiterKind.Webhook, "webhook", RateLimitKeyStrategy.ClientIdentity)]
    [ApiExplorerSettings(IgnoreApi = true)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Webhook()
    {
        var secret = Request.Headers["X-JellyPlay-Webhook-Secret"].ToString();
        using var reader = new System.IO.StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync();
        return _intake.Handle(secret, body) switch
        {
            SeerrWebhookIntakeOutcome.Accepted => JellyPlayResponses.Camel(),
            SeerrWebhookIntakeOutcome.BadPayload => BadRequest(),
            _ => Unauthorized(),
        };
    }

    /// <summary>
    /// Effective webhook base URL (externally reachable Jellyfin address) and
    /// where it came from: "config" when Seerr:JellyfinBaseUrl is set, otherwise
    /// derived from this admin request ("request") — derivation is informational
    /// until POST reprovision persists it.
    /// </summary>
    [HttpGet("webhookInfo")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult WebhookInfo()
    {
        var config = _config();
        var fromConfig = !string.IsNullOrWhiteSpace(config.JellyfinBaseUrl);
        var baseUrl = fromConfig ? config.JellyfinBaseUrl.TrimEnd('/') : RequestBaseUrl();
        return JellyPlayResponses.Camel(new
        {
            autoProvision = config.AutoProvisionWebhook,
            secretConfigured = !string.IsNullOrEmpty(config.WebhookSecret),
            webhookPath = $"{JellyPlayContract.RoutePrefix}/seerr/webhook",
            baseUrl,
            baseUrlSource = fromConfig ? "config" : "request"
        });
    }

    /// <summary>
    /// Re-registers the webhook into Seerr. The base URL comes from the incoming
    /// admin request (reverse-proxy path base included) and is persisted into
    /// config by the provisioner (the documented ADR-0002 Seerr-lifecycle
    /// exception lives in the service) so startup auto-provision works from
    /// then on. The controller only derives the URL and maps the outcome.
    /// </summary>
    [HttpPost("reprovision")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> Reprovision()
    {
        var baseUrl = RequestBaseUrl();
        var ok = await _provisioner.ProvisionAsync(baseUrl, persistBaseUrl: true);
        return ok
            ? JellyPlayResponses.Camel(new { provisioned = true, baseUrl, baseUrlSource = "request" })
            // Pinned response shape in docs/CONTRACT.md — not the generic error body.
            : JellyPlayResponses.Camel(new { provisioned = false, baseUrl, baseUrlSource = "request" }, StatusCodes.Status502BadGateway);
    }

    private string RequestBaseUrl()
        => $"{Request.Scheme}://{Request.Host}{Request.PathBase}".TrimEnd('/');
}
