using System;
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
public class SeerrController : ControllerBase
{
    private readonly SeerrSessionService _sessions;
    private readonly SeerrProxyService _proxy;
    private readonly SeerrWebhookProvisioner _provisioner;
    private readonly Services.Events.EventService _events;

    public SeerrController(SeerrSessionService sessions, SeerrProxyService proxy, SeerrWebhookProvisioner provisioner, Services.Events.EventService events)
    {
        _sessions = sessions;
        _proxy = proxy;
        _provisioner = provisioner;
        _events = events;
    }

    [HttpPost("login")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody, Required] SeerrLoginRequest request)
    {
        var userId = User.GetUserId().ToString();
        var result = request.AuthType switch
        {
            "password" => await _sessions.LoginWithPassword(userId, request.Username ?? string.Empty, request.Password ?? string.Empty),
            "quickconnect" => await _sessions.LoginWithQuickConnect(userId, request.QuickConnectSecret ?? string.Empty),
            _ => new SeerrLoginResult(false, "unknown-auth-type")
        };

        return result.Success ? Ok(new { linked = true }) : Unauthorized(new { error = result.Error });
    }

    [HttpGet("status")]
    public IActionResult Status()
    {
        var session = _sessions.GetSession(User.GetUserId().ToString());
        return Ok(new
        {
            configured = _sessions.IsConfigured,
            serverUrl = _sessions.IsConfigured ? _sessions.ServerUrl : null,
            linked = session is not null,
            createdAt = session?.CreatedAt
        });
    }

    [HttpGet("validate")]
    public IActionResult Validate()
        => Ok(new { valid = _sessions.GetSession(User.GetUserId().ToString()) is not null });

    [HttpDelete("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult Logout()
        => _sessions.DeleteSession(User.GetUserId().ToString()) ? NoContent() : NotFound();

    [HttpGet("radarr/calendar")]
    public Task ProxyRadarrCalendar(CancellationToken cancellationToken)
        => _proxy.ProxyAsync(HttpContext, "radarr/calendar?advancedFilters=false", User.GetUserId().ToString(), cancellationToken);

    [HttpGet("sonarr/calendar")]
    public Task ProxySonarrCalendar(CancellationToken cancellationToken)
        => _proxy.ProxyAsync(HttpContext, "sonarr/calendar?advancedFilters=false", User.GetUserId().ToString(), cancellationToken);

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
        return _proxy.ProxyAsync(HttpContext, path, User.GetUserId().ToString(), cancellationToken);
    }

    /// <summary>Inbound Seerr webhook (AllowAnonymous; secret header required).</summary>
    [HttpPost("webhook")]
    [AllowAnonymous]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<IActionResult> Webhook()
    {
        var config = JellyPlayPlugin.Instance!.Configuration.Seerr;
        var secret = Request.Headers["X-JellyPlay-Webhook-Secret"].ToString();
        if (string.IsNullOrEmpty(config.WebhookSecret) || secret != config.WebhookSecret)
        {
            return Unauthorized();
        }

        using var reader = new System.IO.StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var root = doc.RootElement;
            var subject = root.TryGetProperty("subject", out var subjectElement) ? subjectElement.GetString() : "Seerr request";
            var message = root.TryGetProperty("message", out var messageElement) ? messageElement.GetString() : null;
            _events.PublishBroadcast($"Seerr: {subject}", message ?? "Request activity in Seerr", null);
            return Ok();
        }
        catch (System.Text.Json.JsonException)
        {
            return BadRequest();
        }
    }

    [HttpGet("webhookInfo")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult WebhookInfo()
    {
        var config = JellyPlayPlugin.Instance!.Configuration.Seerr;
        return Ok(new
        {
            autoProvision = config.AutoProvisionWebhook,
            secretConfigured = !string.IsNullOrEmpty(config.WebhookSecret),
            webhookPath = $"{JellyPlayContract.RoutePrefix}/seerr/webhook"
        });
    }

    [HttpPost("reprovision")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> Reprovision()
    {
        var baseUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
        var ok = await _provisioner.ProvisionAsync(baseUrl);
        return ok ? Ok(new { provisioned = true }) : StatusCode(StatusCodes.Status502BadGateway, new { provisioned = false });
    }
}
