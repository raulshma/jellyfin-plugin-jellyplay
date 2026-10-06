using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyPlay.Services.Seerr;

/// <summary>
/// Registers the plugin's webhook receiver in Seerr automatically (admin
/// settings PUT), so Seerr request activity flows into JellyPlay events without
/// manual dashboard steps. Secret lives in plugin config.
/// </summary>
public sealed class SeerrWebhookProvisioner
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly Func<SeerrConfig> _config;
    private readonly ILogger<SeerrWebhookProvisioner> _logger;

    public SeerrWebhookProvisioner(IHttpClientFactory httpFactory, Func<SeerrConfig> config, ILogger<SeerrWebhookProvisioner> logger)
    {
        _httpFactory = httpFactory;
        _config = config;
        _logger = logger;
    }

    public async Task<bool> ProvisionAsync(string pluginWebhookBaseUrl)
    {
        var config = _config();
        if (string.IsNullOrEmpty(config.ServerUrl) || string.IsNullOrEmpty(config.ApiKey) || !config.AutoProvisionWebhook)
        {
            return false;
        }

        try
        {
            var client = _httpFactory.CreateClient("JellyPlayHttpClient");
            var url = $"{config.ServerUrl.TrimEnd('/')}/api/v1/settings/main";
            var settingsUrl = $"{pluginWebhookBaseUrl.TrimEnd('/')}/jellyplay/seerr/webhook";

            // Read current main settings, then patch the webhook fields — never blind-overwrite.
            using var get = new HttpRequestMessage(HttpMethod.Get, url);
            get.Headers.Add("X-Api-Key", config.ApiKey);
            using var getResponse = await client.SendAsync(get);
            getResponse.EnsureSuccessStatusCode();
            var settings = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());

            var payload = new System.Dynamic.ExpandoObject() as IDictionary<string, object?>;
            foreach (var property in settings.RootElement.EnumerateObject())
            {
                payload[property.Name] = property.Value.Clone();
            }

            payload["webhookEnabled"] = true;
            payload["webhookIp"] = settingsUrl;
            payload["webhookPayload"] = BuildDefaultPayloadJson();
            payload["webhookTypes"] = 4; // REQUEST_PENDING + REQUEST_APPROVED bitfield (Seerr convention: 1|2|4|8|16|32|64)

            using var put = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(payload) };
            put.Headers.Add("X-Api-Key", config.ApiKey);
            using var putResponse = await client.SendAsync(put);
            putResponse.EnsureSuccessStatusCode();
            _logger.LogInformation("Seerr webhook provisioned at {Url}", settingsUrl);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Seerr webhook provisioning failed");
            return false;
        }
    }

    private static string BuildDefaultPayloadJson() =>
        """
        {
          "notification_type": "{{notification_type}}",
          "event": "{{event}}",
          "subject": "{{subject}}",
          "message": "{{message}}",
          "image": "{{image}}",
          "request_id": "{{requestId}}",
          "requestedBy_username": "{{requestedBy_username}}",
          "media_type": "{{mediaType}}",
          "media_tmdbid": "{{mediaTmdbid}}"
        }
        """;
}

/// <summary>Runs webhook provisioning once at server start — only when a base URL is configured.</summary>
public sealed class SeerrProvisioningHostedService : IHostedService
{
    private readonly SeerrWebhookProvisioner _provisioner;
    private readonly Func<SeerrConfig> _config;
    private readonly ILogger<SeerrProvisioningHostedService> _logger;

    public SeerrProvisioningHostedService(SeerrWebhookProvisioner provisioner, Func<SeerrConfig> config, ILogger<SeerrProvisioningHostedService> logger)
    {
        _provisioner = provisioner;
        _config = config;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var configured = _config().JellyfinBaseUrl;
        if (string.IsNullOrWhiteSpace(configured))
        {
            // No blind localhost attempts: without an externally reachable base
            // URL the provisioned webhook would be useless anyway. POST
            // jellyplay/seerr/reprovision derives and persists the base URL
            // from the incoming admin request.
            _logger.LogWarning(
                "Seerr webhook auto-provision skipped: Seerr:JellyfinBaseUrl is not configured. Set it in the dashboard, or call POST jellyplay/seerr/reprovision once from a reachable address.");
            return Task.CompletedTask;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await _provisioner.ProvisionAsync(configured);
                }
                catch
                {
                    // logged inside provisioner; retried via admin endpoint
                }
            },
            cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
