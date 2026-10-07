using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Api;
using Jellyfin.Plugin.JellyPlay.Configuration;
using Jellyfin.Plugin.JellyPlay.Realtime;
using Jellyfin.Plugin.JellyPlay.Services.Admin;
using Jellyfin.Plugin.JellyPlay.Services.Devices;
using Jellyfin.Plugin.JellyPlay.Services.Events;
using Jellyfin.Plugin.JellyPlay.Services.Push;
using Jellyfin.Plugin.JellyPlay.Storage;
using Jellyfin.Plugin.JellyPlay.Storage.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Jellyfin.Plugin.JellyPlay.Tests;

// ---------------------------------------------------------------------------
// Shared FCM test fakes (throwaway RSA keys only — never committed material)
// ---------------------------------------------------------------------------

/// <summary>Generates a throwaway 2048-bit key + a service-account JSON shaped like a real pasted key file.</summary>
internal sealed class FakeServiceAccount : IDisposable
{
    public RSA Key { get; } = RSA.Create(2048);

    public string ClientEmail { get; } = "push@proj-x.iam.gserviceaccount.com";

    /// <summary>Raw JSON text exactly as an admin would paste it (newlines inside private_key are \n-escaped).</summary>
    public string Json(string? tokenUri = null)
    {
        var obj = new JObject
        {
            ["type"] = "service_account",
            ["project_id"] = "proj-x",
            ["client_email"] = ClientEmail,
            ["private_key"] = Key.ExportPkcs8PrivateKeyPem()
        };
        if (tokenUri is not null)
        {
            obj["token_uri"] = tokenUri;
        }

        return obj.ToString(Formatting.None);
    }

    public void Dispose() => Key.Dispose();
}

/// <summary>Fake OAuth2 token endpoint: records calls and mints distinct access tokens per call.</summary>
internal sealed class FakeFcmTokenEndpoint
{
    private int _calls;

    public TimeSpan? Latency { get; set; }

    public Func<int, HttpResponseMessage>? Responder { get; set; }

    public ConcurrentQueue<string> Urls { get; } = new();

    public ConcurrentQueue<string> Bodies { get; } = new();

    public int Calls => Volatile.Read(ref _calls);

    public PushSender Sender() => async (request, cancellationToken) =>
    {
        var index = Interlocked.Increment(ref _calls) - 1;
        if (Latency is { } latency)
        {
            await Task.Delay(latency, cancellationToken);
        }

        Urls.Enqueue(request.RequestUri?.ToString() ?? string.Empty);
        Bodies.Enqueue(request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken));
        return Responder is null ? TokenJson($"tok-{index + 1}", 3600) : Responder(index);
    };

    public static HttpResponseMessage TokenJson(string token, int expiresIn)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"access_token\":\"" + token + "\",\"expires_in\":" + expiresIn + "}",
                Encoding.UTF8,
                "application/json")
        };
}

/// <summary>Logger that counts entries per level (asserts the one-time Error latch).</summary>
internal sealed class RecordingFcmLogger : ILogger<FcmTokenProvider>
{
    private readonly object _gate = new();
    private readonly List<LogLevel> _levels = new();

    public int ErrorCount
    {
        get { lock (_gate) { return _levels.Count(level => level == LogLevel.Error); } }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
        {
            _levels.Add(logLevel);
        }
    }
}

/// <summary>Push transport recording endpoint + Authorization header + body (FCM bearer assertions).</summary>
internal sealed class HeaderRecording
{
    public ConcurrentQueue<string> Endpoints { get; } = new();

    public ConcurrentQueue<string?> Authorization { get; } = new();

    public ConcurrentQueue<string> Bodies { get; } = new();

    public int Count;

    public PushSender Sender()
        => async (request, cancellationToken) =>
        {
            Interlocked.Increment(ref Count);
            Endpoints.Enqueue(request.RequestUri?.ToString() ?? string.Empty);
            Authorization.Enqueue(request.Headers.Authorization?.ToString());
            Bodies.Enqueue(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK);
        };
}

// ---------------------------------------------------------------------------
// Service-account JSON parsing + JWT assertion construction (pure)
// ---------------------------------------------------------------------------

public sealed class FcmServiceAccountTests
{
    [Fact]
    public void Parse_ValidJson_ExtractsFields_WithDefaultTokenUri()
    {
        using var sa = new FakeServiceAccount();

        var parsed = FcmTokenProvider.TryParseServiceAccount(sa.Json());

        Assert.NotNull(parsed);
        Assert.Equal(sa.ClientEmail, parsed!.ClientEmail);
        Assert.Equal("https://oauth2.googleapis.com/token", parsed.TokenUri); // absent → Google default

        var withUri = FcmTokenProvider.TryParseServiceAccount(sa.Json("https://oauth2.example/token"));
        Assert.Equal("https://oauth2.example/token", withUri!.TokenUri);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"client_email\":\"\"}")]
    [InlineData("{\"client_email\":\"a@b.c\",\"private_key\":\" \"}")]
    public void Parse_MalformedOrIncomplete_YieldsNull(string json)
    {
        Assert.Null(FcmTokenProvider.TryParseServiceAccount(json));
    }

    [Fact]
    public void FixPemNewlines_UnescapesDoubleEscapedPem()
    {
        var pem = "-----BEGIN PRIVATE KEY-----\nAAA\n-----END PRIVATE KEY-----\n";
        Assert.Equal(pem, FcmTokenProvider.FixPemNewlines(pem.Replace("\n", "\\n"))); // the "\n"-escaped paste form
    }
}

public sealed class FcmJwtTests
{
    private static (string Header, string Payload, byte[] Signature, string SigningInput) Decode(string jwt)
    {
        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);
        foreach (var part in parts)
        {
            Assert.DoesNotContain('+', part); // base64url form: no padding, no +/ alphabet
            Assert.DoesNotContain('/', part);
            Assert.DoesNotContain('=', part);
        }

        byte[] DecodeSegment(string segment)
        {
            var padded = segment.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String((padded.Length % 4) switch
            {
                2 => padded + "==",
                3 => padded + "=",
                _ => padded
            });
        }

        return (
            Encoding.UTF8.GetString(DecodeSegment(parts[0])),
            Encoding.UTF8.GetString(DecodeSegment(parts[1])),
            DecodeSegment(parts[2]),
            parts[0] + "." + parts[1]);
    }

    [Fact]
    public void BuildJwt_HeaderPayloadClaimsAndBase64UrlForm()
    {
        using var sa = new FakeServiceAccount();

        var jwt = FcmTokenProvider.BuildJwt(sa.ClientEmail, sa.Key, "https://oauth2.googleapis.com/token", 1000, 4600);

        var (header, payload, signature, _) = Decode(jwt);
        Assert.Equal("{\"alg\":\"RS256\",\"typ\":\"JWT\"}", header);

        var claims = JObject.Parse(payload);
        Assert.Equal(sa.ClientEmail, claims["iss"]!.ToString());
        Assert.Equal("https://www.googleapis.com/auth/firebase.messaging", claims["scope"]!.ToString());
        Assert.Equal("https://oauth2.googleapis.com/token", claims["aud"]!.ToString());
        Assert.Equal(1000L, (long)claims["iat"]!);
        Assert.Equal(4600L, (long)claims["exp"]!); // iat + 3600
        Assert.NotEmpty(signature);
    }

    [Fact]
    public void BuildJwt_SignatureVerifiesWithExportedPublicKey()
    {
        using var sa = new FakeServiceAccount();
        var jwt = FcmTokenProvider.BuildJwt(sa.ClientEmail, sa.Key, "https://oauth2.googleapis.com/token", 7, 3607);

        var (_, _, signature, signingInput) = Decode(jwt);

        // Verify through the EXPORTED public key, not the private instance.
        using var publicKey = RSA.Create();
        publicKey.ImportSubjectPublicKeyInfo(sa.Key.ExportSubjectPublicKeyInfo(), out _);
        Assert.True(publicKey.VerifyData(
            Encoding.ASCII.GetBytes(signingInput),
            signature,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1));

        Assert.False(publicKey.VerifyData(
            Encoding.ASCII.GetBytes(signingInput + "x"),
            signature,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1)); // tampering invalidates
    }
}

// ---------------------------------------------------------------------------
// Token lifecycle: exchange, caching, single-flight, failure latch
// ---------------------------------------------------------------------------

public sealed class FcmTokenFlowTests : IDisposable
{
    private readonly FakeServiceAccount _sa = new();
    private readonly FakeFcmTokenEndpoint _endpoint = new();
    private readonly RecordingFcmLogger _logger = new();
    private readonly PushConfig _config = new() { Enabled = true, FcmProjectId = "proj-x" };
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public void Dispose() => _sa.Dispose();

    private FcmTokenProvider Provider()
    {
        if (string.IsNullOrEmpty(_config.FcmServiceAccountJson))
        {
            _config.FcmServiceAccountJson = _sa.Json();
        }

        return new FcmTokenProvider(() => _config, _logger, () => _now, _endpoint.Sender());
    }

    private static (string GrantType, string Assertion) ParseForm(string body)
    {
        var pairs = body.Split('&')
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(parts => parts[0], parts => Uri.UnescapeDataString(parts[1]));
        return (pairs["grant_type"], pairs["assertion"]);
    }

    [Fact]
    public async Task GetToken_ExchangesJwt_FormBodyAndEndpoint()
    {
        var provider = Provider();

        var token = await provider.GetTokenAsync();

        Assert.Equal("tok-1", token);
        Assert.Equal(1, _endpoint.Calls);
        Assert.Equal("https://oauth2.googleapis.com/token", _endpoint.Urls.Single());
        var (grantType, assertion) = ParseForm(_endpoint.Bodies.Single());
        Assert.Equal("urn:ietf:params:oauth:grant-type:jwt-bearer", grantType);

        var payload = JObject.Parse(Encoding.UTF8.GetString(DecodeAssertion(assertion)));
        Assert.Equal(_sa.ClientEmail, payload["iss"]!.ToString());
        Assert.Equal("https://oauth2.googleapis.com/token", payload["aud"]!.ToString());
    }

    [Fact]
    public async Task GetToken_CachesUntilFiveMinutesBeforeExpiry()
    {
        var provider = Provider();

        Assert.Equal("tok-1", await provider.GetTokenAsync());
        _now = _now.AddMinutes(54); // 3600s expiry − 5min lead → valid for 55min
        Assert.Equal("tok-1", await provider.GetTokenAsync());
        Assert.Equal(1, _endpoint.Calls); // served from cache

        _now = _now.AddMinutes(1); // inside the 5-minute window → refresh
        Assert.Equal("tok-2", await provider.GetTokenAsync());
        Assert.Equal(2, _endpoint.Calls);
    }

    [Fact]
    public async Task GetToken_SingleFlight_OneExchangeForConcurrentCallers()
    {
        _endpoint.Latency = TimeSpan.FromMilliseconds(120);
        var provider = Provider();

        var tokens = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => provider.GetTokenAsync()));

        Assert.All(tokens, token => Assert.Equal("tok-1", token));
        Assert.Equal(1, _endpoint.Calls); // collapsed into one HTTP exchange
    }

    [Fact]
    public async Task GetToken_InvalidJson_LatchesAtError_RecoversOnKeyChange()
    {
        _config.FcmServiceAccountJson = "{ definitely not json";
        var provider = Provider();

        Assert.Null(await provider.GetTokenAsync());
        Assert.Equal(1, _logger.ErrorCount);
        Assert.Equal(0, _endpoint.Calls); // never even tried Google

        _now = _now.AddHours(2);
        Assert.Null(await provider.GetTokenAsync()); // permanent for this key
        Assert.Null(await provider.GetTokenAsync());
        Assert.Equal(1, _logger.ErrorCount); // logged once, no retry storm
        Assert.Equal(0, _endpoint.Calls);

        _config.FcmServiceAccountJson = _sa.Json(); // admin pastes a valid key → fresh attempt
        Assert.Equal("tok-1", await provider.GetTokenAsync());
        Assert.Equal(1, _logger.ErrorCount);
        Assert.Equal(1, _endpoint.Calls);
    }

    [Fact]
    public async Task GetToken_InvalidPem_Latches()
    {
        _config.FcmServiceAccountJson = new JObject
        {
            ["client_email"] = _sa.ClientEmail,
            ["private_key"] = "-----BEGIN PRIVATE KEY-----\nnot-a-key\n-----END PRIVATE KEY-----"
        }.ToString(Formatting.None);

        var provider = Provider();

        Assert.Null(await provider.GetTokenAsync());
        Assert.Null(await provider.GetTokenAsync());
        Assert.Equal(1, _logger.ErrorCount);
        Assert.Equal(0, _endpoint.Calls);
    }

    [Fact]
    public async Task GetToken_DoubleEscapedPem_StillImports()
    {
        // Some admin pastes end up with "\n" escaped twice inside the JSON string.
        var pem = _sa.Key.ExportPkcs8PrivateKeyPem().Replace("\n", "\\n");
        _config.FcmServiceAccountJson = new JObject
        {
            ["client_email"] = _sa.ClientEmail,
            ["private_key"] = pem
        }.ToString(Formatting.None);

        var provider = Provider();

        Assert.Equal("tok-1", await provider.GetTokenAsync());
    }

    [Fact]
    public async Task GetToken_TransientEndpointFailure_IsNotLatched()
    {
        _endpoint.Responder = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var provider = Provider();

        Assert.Null(await provider.GetTokenAsync());
        Assert.Equal(0, _logger.ErrorCount); // transient → Debug only, no latch

        _endpoint.Responder = null;
        Assert.Equal("tok-2", await provider.GetTokenAsync()); // retried on the next call (failed call was tok-1's slot)
        Assert.Equal(2, _endpoint.Calls);
    }

    [Fact]
    public async Task GetToken_MissingAccessToken_YieldsNull()
    {
        _endpoint.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"expires_in\":3600}", Encoding.UTF8, "application/json")
        };

        Assert.Null(await Provider().GetTokenAsync());
        Assert.Equal(0, _logger.ErrorCount);
    }

    [Fact]
    public async Task GetToken_Unconfigured_ReturnsNullWithoutHttp()
    {
        var provider = new FcmTokenProvider(
            () => new PushConfig { Enabled = true },
            _logger,
            () => _now,
            _endpoint.Sender());

        Assert.Null(await provider.GetTokenAsync());
        Assert.Equal(0, _endpoint.Calls);
    }

    private static byte[] DecodeAssertion(string assertion)
    {
        var parts = assertion.Split('.');
        var padded = parts[1].Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String((padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            _ => padded
        });
    }
}

// ---------------------------------------------------------------------------
// FCM payload builder (exact JSON)
// ---------------------------------------------------------------------------

public sealed class FcmPayloadTests
{
    [Fact]
    public void FcmPayload_ExactShape_WithItemId()
    {
        Assert.Equal(
            "{\"message\":{\"token\":\"regtok-1\",\"notification\":{\"title\":\"Hello\",\"body\":\"World\"},\"data\":{\"kind\":\"new-media\",\"itemId\":\"abc123\"},\"android\":{\"priority\":\"NORMAL\"}}}",
            PushDispatcher.BuildFcmPayload(new PushMessage(PushKinds.NewMedia, "Hello", "World", "abc123"), "regtok-1"));
    }

    [Fact]
    public void FcmPayload_OmitsItemId_AndCoercesDataToStrings()
    {
        var json = PushDispatcher.BuildFcmPayload(new PushMessage(PushKinds.Broadcast, "T", "B"), "regtok-2");

        Assert.Equal(
            "{\"message\":{\"token\":\"regtok-2\",\"notification\":{\"title\":\"T\",\"body\":\"B\"},\"data\":{\"kind\":\"broadcast\"},\"android\":{\"priority\":\"NORMAL\"}}}",
            json);

        var data = JObject.Parse(json)["message"]!["data"]!;
        Assert.Equal(JTokenType.String, data["kind"]!.Type); // FCM data values MUST be strings
        Assert.Null(data["itemId"]); // omitted entirely when unset
    }
}

// ---------------------------------------------------------------------------
// Dispatcher routing: fcm only when configured, token shared per fan-out
// ---------------------------------------------------------------------------

public sealed class FcmDispatchTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "jellyplay-fcm-disp-" + Guid.NewGuid().ToString("N"));
    private readonly JellyPlayDatabase _db;
    private readonly FakeServiceAccount _sa = new();
    private readonly FakeFcmTokenEndpoint _endpoint = new();
    private readonly PushConfig _config = new() { Enabled = true };

    public FcmDispatchTests()
    {
        Directory.CreateDirectory(_tempDir);
        _db = new JellyPlayDatabase(_tempDir);
    }

    public void Dispose()
    {
        _db.Dispose();
        _sa.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private PushDispatcher Dispatcher(HeaderRecording recording)
    {
        var provider = new FcmTokenProvider(() => _config, NullLogger<FcmTokenProvider>.Instance, () => DateTimeOffset.UtcNow, _endpoint.Sender());
        return new PushDispatcher(_db, () => _config, NullLogger<PushDispatcher>.Instance, recording.Sender(), provider);
    }

    [Fact]
    public async Task FcmDevice_GetsBearerSendToGoogle_WithExactPayload()
    {
        _config.FcmProjectId = "proj-x";
        _config.FcmServiceAccountJson = _sa.Json();
        _db.UpsertDevice(new DeviceWrite("f1", "u1", "F", "android", "1", 1, "fcm", "fcm-reg-token-1", 1));
        _db.UpsertDevice(new DeviceWrite("g1", "u1", "G", "android", "1", 1, "generic", "https://push.example/g1", 1));
        var recording = new HeaderRecording();

        await Dispatcher(recording).DispatchAsync(new PushMessage(PushKinds.NewMedia, "Title", "Body", "itm1"), new[] { "u1" });

        Assert.Equal(2, recording.Count);
        Assert.Equal(1, _endpoint.Calls); // one token exchange for the whole fan-out

        var endpoints = recording.Endpoints.ToList();
        var fcmIndex = endpoints.IndexOf("https://fcm.googleapis.com/v1/projects/proj-x/messages:send");
        Assert.True(fcmIndex >= 0, "fcm request must hit the FCM v1 send endpoint");
        Assert.Equal("Bearer tok-1", recording.Authorization.ToList()[fcmIndex]);
        Assert.Equal(
            "{\"message\":{\"token\":\"fcm-reg-token-1\",\"notification\":{\"title\":\"Title\",\"body\":\"Body\"},\"data\":{\"kind\":\"new-media\",\"itemId\":\"itm1\"},\"android\":{\"priority\":\"NORMAL\"}}}",
            recording.Bodies.ToList()[fcmIndex]);
        Assert.Null(recording.Authorization.ToList()[endpoints.IndexOf("https://push.example/g1")]); // generic stays anonymous
    }

    [Fact]
    public async Task FcmUnconfigured_SkipsFcmDevices_DebugPath_NoHttp()
    {
        _db.UpsertDevice(new DeviceWrite("f1", "u1", "F", "android", "1", 1, "fcm", "fcm-reg-token-1", 1));
        _db.UpsertDevice(new DeviceWrite("g1", "u1", "G", "android", "1", 1, "generic", "https://push.example/g1", 1));
        var recording = new HeaderRecording();

        await Dispatcher(recording).DispatchAsync(new PushMessage(PushKinds.Broadcast, "T", "B"), new[] { "u1" });

        Assert.Equal(1, recording.Count); // fcm skipped, generic delivered
        Assert.Equal("https://push.example/g1", recording.Endpoints.Single());
        Assert.Equal(0, _endpoint.Calls); // not configured → no token request at all
    }

    [Fact]
    public async Task TokenFetchFails_FcmDevicesSkipped_OneAttemptPerFanOut()
    {
        _config.FcmProjectId = "proj-x";
        _config.FcmServiceAccountJson = _sa.Json();
        _endpoint.Responder = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        _db.UpsertDevice(new DeviceWrite("f1", "u1", "F", "android", "1", 1, "fcm", "fcm-reg-token-1", 1));
        _db.UpsertDevice(new DeviceWrite("f2", "u1", "F2", "android", "1", 1, "fcm", "fcm-reg-token-2", 1));
        _db.UpsertDevice(new DeviceWrite("g1", "u1", "G", "android", "1", 1, "generic", "https://push.example/g1", 1));
        var recording = new HeaderRecording();

        await Dispatcher(recording).DispatchAsync(new PushMessage(PushKinds.Broadcast, "T", "B"), new[] { "u1" });

        Assert.Equal(1, recording.Count); // only the generic device
        Assert.Equal("https://push.example/g1", recording.Endpoints.Single());
        Assert.Equal(1, _endpoint.Calls); // token resolved once per fan-out, not per device
    }

    [Fact]
    public void AdminOverview_ReportsFcmConfiguredBoolean_AndHidesTokenHost()
    {
        _db.UpsertDevice(new DeviceWrite("f1", "u1", "F", "android", "1", 1, "fcm", "super-secret-registration-token", 1));

        var dispatcher = Dispatcher(new HeaderRecording());
        var unconfigured = dispatcher.GetAdminOverview(_ => null);
        Assert.False(unconfigured.FcmConfigured);

        _config.FcmProjectId = "proj-x";
        _config.FcmServiceAccountJson = _sa.Json();
        var configured = dispatcher.GetAdminOverview(_ => null);
        Assert.True(configured.FcmConfigured);

        var row = Assert.Single(configured.Devices);
        Assert.Equal("fcm", row.Kind);
        Assert.Equal(string.Empty, row.EndpointHost); // a registration token is not a URL — never surfaced
        Assert.DoesNotContain("super-secret", Newtonsoft.Json.JsonConvert.SerializeObject(configured));
    }
}

// ---------------------------------------------------------------------------
// Registration API: fcm accepted only when configured (push-kind-unavailable)
// ---------------------------------------------------------------------------

public sealed class FcmRegistrationApiTests : IDisposable
{
    private const string UserA = "33333333-3333-3333-3333-333333333333";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "jellyplay-fcm-api-" + Guid.NewGuid().ToString("N"));
    private readonly JellyPlayDatabase _db;
    private readonly PushConfig _pushConfig = new();

    public FcmRegistrationApiTests()
    {
        Directory.CreateDirectory(_tempDir);
        _db = new JellyPlayDatabase(_tempDir);
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private EventsController Controller(bool withPushConfig = true)
    {
        var hub = new SseHub(NullLogger<SseHub>.Instance);
        var events = new EventService(
            hub,
            () => new EventsConfig(),
            () => new List<string>(),
            NullLogger<EventService>.Instance);
        var controller = new EventsController(
            hub,
            events,
            new DeviceRegistryService(_db, () => _pushConfig, SettingsServiceFactory.Create(_db, hub)));
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[]
                {
                    new Claim("Jellyfin-UserId", UserA),
                    new Claim("Jellyfin-DeviceId", "d1")
                },
                "Bearer"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        return controller;
    }

    private static DeviceRegistrationRequest Request(string? kind = null, string? endpoint = null, string deviceId = "d1")
    {
        var request = new DeviceRegistrationRequest
        {
            DeviceId = deviceId,
            Name = "Dev",
            Platform = "android",
            AppVersion = "1.0"
        };
        if (kind is not null)
        {
            request.Push = System.Text.Json.JsonSerializer.SerializeToElement(
                new DevicePushRegistration { Kind = kind, Endpoint = endpoint ?? string.Empty });
        }

        return request;
    }

    private static string ErrorOf(IActionResult result)
    {
        var content = Assert.IsAssignableFrom<ContentResult>(result);
        return JObject.Parse(content.Content!)["error"]!.ToString();
    }

    [Fact]
    public void Fcm_WhenConfigured_Accepted_PersistedAndEchoedToOwner()
    {
        _pushConfig.FcmProjectId = "proj-x";
        _pushConfig.FcmServiceAccountJson = "{\"client_email\":\"a@b.c\",\"private_key\":\"k\"}";
        var controller = Controller();

        Assert.IsType<NoContentResult>(controller.RegisterDevice(Request("fcm", "fcm-reg-token-9")));

        var stored = _db.GetDeviceById("d1");
        Assert.Equal(("fcm", "fcm-reg-token-9"), (stored!.PushKind, stored.PushEndpoint));

        var listed = JArray.Parse(Assert.IsAssignableFrom<ContentResult>(controller.GetDevices()).Content!)[0]!["push"]!;
        Assert.Equal("fcm", listed["kind"]!.ToString());
        Assert.Equal("fcm-reg-token-9", listed["endpoint"]!.ToString());
    }

    [Theory]
    [InlineData(false, null)] // no Func<PushConfig> injected at all
    [InlineData(true, null)] // empty config
    [InlineData(true, "proj-only")] // project id without the key
    public void Fcm_WhenNotConfigured_Rejected400pushKindUnavailable(bool withPushConfig, string? projectId)
    {
        _pushConfig.FcmProjectId = projectId ?? string.Empty;
        var controller = Controller(withPushConfig);

        var result = controller.RegisterDevice(Request("fcm", "fcm-reg-token-9"));

        Assert.Equal("push-kind-unavailable", ErrorOf(result));
        Assert.Null(_db.GetDeviceById("d1")); // nothing persisted
    }

    [Fact]
    public void Fcm_BlankEndpoint_StaysStructurallyInvalid()
    {
        _pushConfig.FcmProjectId = "proj-x";
        _pushConfig.FcmServiceAccountJson = "{}";

        Assert.Equal("invalid-push-registration", ErrorOf(Controller().RegisterDevice(Request("fcm", "   "))));
    }

    [Fact]
    public void GenericAndNtfy_UnaffectedByFcmConfigState()
    {
        var controller = Controller(withPushConfig: true); // FCM left unconfigured

        Assert.IsType<NoContentResult>(controller.RegisterDevice(Request("generic", "https://push.example/hook", "d-generic")));
        Assert.IsType<NoContentResult>(controller.RegisterDevice(Request("ntfy", "https://ntfy.sh/topic", "d-ntfy")));

        var rows = JArray.Parse(Assert.IsAssignableFrom<ContentResult>(controller.GetDevices()).Content!);
        Assert.Equal(2, rows.Count);
    }
}
