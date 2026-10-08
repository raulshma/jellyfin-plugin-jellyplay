using System;
using System.Text.Json;
using Jellyfin.Plugin.JellyPlay.Configuration;
using Jellyfin.Plugin.JellyPlay.Services.Admin;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyPlay.Services.Seerr;

/// <summary>Anonymous webhook intake outcome — the controller maps each member to its pinned wire response.</summary>
public enum SeerrWebhookIntakeOutcome
{
    Accepted,
    Unauthorized,
    BadPayload
}

/// <summary>
/// The deep module behind the anonymous Seerr webhook route: secret-match +
/// parse → broadcast behind one small interface. The controller stays thin
/// (AllowAnonymous + rate-limit + body read, then a single delegate call) and
/// keeps owning the serialization gate — this module never touches HTTP, so
/// ADR-0001 is undisturbed. Config crosses the Func seam (ADR-0002); the
/// provisioner's config-write path is untouched. Reads:
/// <c>subject</c> defaults to "Seerr request", a missing <c>message</c> to
/// "Request activity in Seerr" — the exact mapping the controller inlined before.
/// </summary>
public sealed class SeerrWebhookIntake
{
    private readonly Func<SeerrConfig> _config;
    private readonly Events.EventService _events;
    private readonly ILogger<SeerrWebhookIntake> _logger;

    public SeerrWebhookIntake(Func<SeerrConfig> config, Events.EventService events, ILogger<SeerrWebhookIntake> logger)
    {
        _config = config;
        _events = events;
        _logger = logger;
    }

    /// <summary>
    /// The intake interface: authorize, parse, publish. One call per webhook
    /// delivery — the caller (thin controller) learns one method, the
    /// secret/parse/broadcast depth lives here.
    /// </summary>
    public SeerrWebhookIntakeOutcome Handle(string? presentedSecret, string body)
    {
        if (!IsAuthorized(presentedSecret))
        {
            return SeerrWebhookIntakeOutcome.Unauthorized;
        }

        SeerrWebhookBroadcast broadcast;
        try
        {
            broadcast = ParseBroadcast(body);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Seerr webhook intake rejected malformed payload");
            return SeerrWebhookIntakeOutcome.BadPayload;
        }

        _events.PublishBroadcast(broadcast.Title, broadcast.Body, null);
        return SeerrWebhookIntakeOutcome.Accepted;
    }

    /// <summary>
    /// The secret-match seam: an empty configured secret never authorizes
    /// (fail closed); comparison itself is <see cref="WebhookSecurity"/>'s
    /// constant-time match, leveraged — not reimplemented — here.
    /// </summary>
    public bool IsAuthorized(string? presentedSecret)
    {
        var configured = _config().WebhookSecret;
        return !string.IsNullOrEmpty(configured)
            && WebhookSecurity.SecretMatches(configured, presentedSecret);
    }

    /// <summary>
    /// Pure parse → broadcast mapping (no secret, no publish): the unit-testable
    /// internal seam. Throws <see cref="JsonException"/> on malformed bodies,
    /// which <see cref="Handle"/> maps to <c>BadPayload</c>.
    /// </summary>
    internal static SeerrWebhookBroadcast ParseBroadcast(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var subject = root.TryGetProperty("subject", out var subjectElement) ? subjectElement.GetString() : "Seerr request";
        var message = root.TryGetProperty("message", out var messageElement) ? messageElement.GetString() : null;
        return new SeerrWebhookBroadcast($"Seerr: {subject}", message ?? "Request activity in Seerr");
    }
}

/// <summary>The parsed broadcast content of one accepted webhook delivery.</summary>
public sealed record SeerrWebhookBroadcast(string Title, string Body);
