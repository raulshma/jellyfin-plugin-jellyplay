using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Configuration;
using Jellyfin.Plugin.JellyPlay.Services.Seerr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Jellyfin.Plugin.JellyPlay.Tests;

/// <summary>Fake Seerr transport: records every request (method, URL, api-key header, body) and answers from a queue.</summary>
internal sealed class FakeSeerrTransport
{
    public sealed record RecordedRequest(string Method, string Url, string? ApiKey, string? Body);

    private readonly ConcurrentQueue<HttpResponseMessage> _responses = new();

    public List<RecordedRequest> Requests { get; } = new();

    public FakeSeerrTransport Responds(HttpStatusCode status, string json)
    {
        _responses.Enqueue(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        return this;
    }

    public SeerrSender Sender() => (request, _, _) =>
    {
        Requests.Add(new RecordedRequest(
            request.Method.Method,
            request.RequestUri?.ToString() ?? string.Empty,
            request.Headers.TryGetValues("X-Api-Key", out var values) ? values.FirstOrDefault() : null,
            request.Content is null ? null : request.Content.ReadAsStringAsync().GetAwaiter().GetResult()));
        return Task.FromResult(_responses.TryDequeue(out var response)
            ? response
            : throw new InvalidOperationException($"unexpected request #{Requests.Count} (no queued response)"));
    };
}

/// <summary>Logger that records level + exception per entry (asserts the failure-logging paths).</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly object _gate = new();
    private readonly List<(LogLevel Level, Exception? Exception)> _entries = new();

    public IReadOnlyList<(LogLevel Level, Exception? Exception)> Entries
    {
        get { lock (_gate) { return _entries.ToList(); } }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
        {
            _entries.Add((logLevel, exception));
        }
    }
}

// ---------------------------------------------------------------------------
// SeerrWebhookProvisioner through the SeerrSender seam
// ---------------------------------------------------------------------------

/// <summary>
/// The provisioner reads Seerr's main settings and patches the webhook fields
/// onto them (never blind-overwrite); every request crosses the same
/// <see cref="SeerrSender"/> seam the rest of the Seerr bridge uses. Request
/// shapes here hit a Jellyseerr/Overseerr API — not our contract.
/// </summary>
public sealed class SeerrProvisionerTests
{
    private static SeerrConfig Config(string serverUrl = "http://seerr:5055", bool autoProvision = true)
        => new() { ServerUrl = serverUrl, ApiKey = "seerr-key-1", AutoProvisionWebhook = autoProvision };

    private const string ServerSettings = """
        {"apiKeysEnabled":true,"mainLanguage":"en","discoverRegion":"all"}
        """;

    [Fact]
    public async Task Provision_ReadsSettings_ThenPatchesWebhookFields_KeepingServerValues()
    {
        var transport = new FakeSeerrTransport()
            .Responds(HttpStatusCode.OK, ServerSettings)
            .Responds(HttpStatusCode.OK, "{}"); // the PUT's 200
        var provisioner = new SeerrWebhookProvisioner(transport.Sender(), () => Config(), NullLogger<SeerrWebhookProvisioner>.Instance);

        var provisioned = await provisioner.ProvisionAsync("https://jelly.example/jellyfin");

        Assert.True(provisioned);
        Assert.Equal(2, transport.Requests.Count);

        var get = transport.Requests[0];
        Assert.Equal(("GET", "http://seerr:5055/api/v1/settings/main", "seerr-key-1"), (get.Method, get.Url, get.ApiKey));

        var put = transport.Requests[1];
        Assert.Equal(("PUT", "http://seerr:5055/api/v1/settings/main", "seerr-key-1"), (put.Method, put.Url, put.ApiKey));

        var payload = JObject.Parse(put.Body!);
        // Server-owned fields ride through untouched...
        Assert.Equal(true, payload["apiKeysEnabled"]!.Value<bool>());
        Assert.Equal("en", payload["mainLanguage"]!.ToString());
        // ...and the four webhook fields are patched on top.
        Assert.Equal(true, payload["webhookEnabled"]!.Value<bool>());
        Assert.Equal("https://jelly.example/jellyfin/jellyplay/seerr/webhook", payload["webhookIp"]!.ToString());
        Assert.Equal(4, payload["webhookTypes"]!.Value<int>()); // REQUEST_PENDING + REQUEST_APPROVED
        // Seerr keeps the payload template as a JSON-encoded string field.
        var webhookPayload = JObject.Parse(payload["webhookPayload"]!.ToString());
        Assert.Equal("{{notification_type}}", webhookPayload["notification_type"]!.ToString());
    }

    [Fact]
    public async Task Provision_TrimsTrailingSlash_OnBothUrls()
    {
        var transport = new FakeSeerrTransport()
            .Responds(HttpStatusCode.OK, "{}")
            .Responds(HttpStatusCode.OK, "{}");
        var provisioner = new SeerrWebhookProvisioner(
            transport.Sender(),
            () => Config(serverUrl: "http://seerr:5055///"),
            NullLogger<SeerrWebhookProvisioner>.Instance);

        await provisioner.ProvisionAsync("https://jelly.example/");

        Assert.All(transport.Requests, request => Assert.Equal("http://seerr:5055/api/v1/settings/main", request.Url));
        Assert.Equal("https://jelly.example/jellyplay/seerr/webhook", JObject.Parse(transport.Requests[1].Body!)["webhookIp"]!.ToString());
    }

    [Fact]
    public async Task Provision_SeerrRejects_LogsWarning_ReturnsFalse()
    {
        var transport = new FakeSeerrTransport().Responds(HttpStatusCode.InternalServerError, "{}");
        var logger = new RecordingLogger<SeerrWebhookProvisioner>();
        var provisioner = new SeerrWebhookProvisioner(transport.Sender(), () => Config(), logger);

        Assert.False(await provisioner.ProvisionAsync("https://jelly.example"));
        Assert.Single(transport.Requests); // died on the GET, never PUT
        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.NotNull(warning.Exception); // the status failure is carried, not swallowed
    }

    [Theory]
    [InlineData("", "key", true)] // no server URL
    [InlineData("http://seerr:5055", "", true)] // no API key
    [InlineData("http://seerr:5055", "key", false)] // auto-provision off
    public void Provision_Unconfigured_YieldsFalseWithoutHttp(string serverUrl, string apiKey, bool autoProvision)
    {
        var transport = new FakeSeerrTransport();
        var provisioner = new SeerrWebhookProvisioner(
            transport.Sender(),
            () => new SeerrConfig { ServerUrl = serverUrl, ApiKey = apiKey, AutoProvisionWebhook = autoProvision },
            NullLogger<SeerrWebhookProvisioner>.Instance);

        Assert.False(provisioner.ProvisionAsync("https://jelly.example").GetAwaiter().GetResult());
        Assert.Empty(transport.Requests);
    }
}

// ---------------------------------------------------------------------------
// SeerrProvisioningHostedService: the startup fire-and-forget
// ---------------------------------------------------------------------------

public sealed class SeerrProvisioningHostedServiceTests
{
    [Fact]
    public async Task StartAsync_WithoutConfiguredBaseUrl_SkipsProvisioningWithWarning()
    {
        var transport = new FakeSeerrTransport();
        var logger = new RecordingLogger<SeerrProvisioningHostedService>();
        var service = new SeerrProvisioningHostedService(
            new SeerrWebhookProvisioner(transport.Sender(), () => new SeerrConfig(), NullLogger<SeerrWebhookProvisioner>.Instance),
            () => new SeerrConfig(), // JellyfinBaseUrl empty
            logger);

        await service.StartAsync(CancellationToken.None);

        Assert.Empty(transport.Requests); // no blind localhost attempts
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task StartAsync_WithBaseUrl_ProvisionsInBackground()
    {
        var transport = new FakeSeerrTransport().Responds(HttpStatusCode.OK, "{}");
        var service = new SeerrProvisioningHostedService(
            new SeerrWebhookProvisioner(
                transport.Sender(),
                () => new SeerrConfig { ServerUrl = "http://seerr:5055", ApiKey = "k" },
                NullLogger<SeerrWebhookProvisioner>.Instance),
            () => new SeerrConfig { JellyfinBaseUrl = "https://jelly.example" },
            NullLogger<SeerrProvisioningHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => transport.Requests.Count == 2); // fire-and-forget still lands both calls

        Assert.Equal("GET", transport.Requests[0].Method);
        Assert.Equal("PUT", transport.Requests[1].Method);
    }

    [Fact]
    public void StopAsync_Completes()
    {
        var service = new SeerrProvisioningHostedService(
            new SeerrWebhookProvisioner(
                new FakeSeerrTransport().Sender(),
                () => new SeerrConfig(),
                NullLogger<SeerrWebhookProvisioner>.Instance),
            () => new SeerrConfig(),
            NullLogger<SeerrProvisioningHostedService>.Instance);

        Assert.Equal(Task.CompletedTask, service.StopAsync(CancellationToken.None));
    }

    private static async Task WaitUntilAsync(Func<bool> done, int timeoutMs = 5000)
    {
        var start = DateTime.UtcNow;
        while (!done())
        {
            if ((DateTime.UtcNow - start).TotalMilliseconds > timeoutMs)
            {
                throw new TimeoutException("the background provisioning did not land in time");
            }

            await Task.Delay(20);
        }
    }
}
