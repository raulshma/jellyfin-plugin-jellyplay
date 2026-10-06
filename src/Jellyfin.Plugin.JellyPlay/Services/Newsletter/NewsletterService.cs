using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Configuration;
using MailKit.Security;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Jellyfin.Plugin.JellyPlay.Services.Newsletter;

public interface INewsletterSender
{
    Task SendAsync(string subject, string htmlBody, IReadOnlyList<string> recipients);
}

/// <summary>Sends the newsletter over SMTP (MailKit — System.Net.Mail.SmtpClient is deprecated). Credentials come from plugin config.</summary>
public sealed class SmtpNewsletterSender : INewsletterSender
{
    private readonly Func<NewsletterConfig> _config;
    private readonly ILogger<SmtpNewsletterSender> _logger;

    public SmtpNewsletterSender(Func<NewsletterConfig> config, ILogger<SmtpNewsletterSender> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task SendAsync(string subject, string htmlBody, IReadOnlyList<string> recipients)
    {
        if (recipients.Count == 0)
        {
            throw new InvalidOperationException("No newsletter recipients resolved.");
        }

        var settings = NewsletterSettings();
        using var message = new MimeMessage();
        message.From.Add(new MailboxAddress(settings.FromName, settings.FromAddress));
        foreach (var recipient in recipients.Where(address => !string.IsNullOrWhiteSpace(address)))
        {
            message.To.Add(MailboxAddress.Parse(recipient));
        }

        message.Subject = subject;
        message.Body = new BodyBuilder { HtmlBody = htmlBody }.ToMessageBody();

        using var client = new MailKit.Net.Smtp.SmtpClient();
        await client.ConnectAsync(settings.SmtpHost, settings.SmtpPort, settings.UseSsl ? SecureSocketOptions.StartTls : SecureSocketOptions.None);
        try
        {
            if (!string.IsNullOrEmpty(settings.SmtpUsername))
            {
                await client.AuthenticateAsync(settings.SmtpUsername, settings.SmtpPassword);
            }

            await client.SendAsync(message);
            _logger.LogInformation("Newsletter '{Subject}' sent to {Count} recipients", subject, message.To.Count);
        }
        finally
        {
            await client.DisconnectAsync(true);
        }
    }

    private NewsletterConfig NewsletterSettings() => _config();
}

/// <summary>
/// Backs the client routes the JellyPlay app already calls:
/// POST /newsletter/send and POST /newsletter/test (no body, root-level path).
/// The newsletter content is composed server-side from recently-added items.
/// </summary>
public sealed class NewsletterService
{
    private readonly INewsletterSender _sender;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly Func<NewsletterConfig> _config;
    private readonly ILogger<NewsletterService> _logger;

    public NewsletterService(
        INewsletterSender sender,
        IUserManager userManager,
        ILibraryManager libraryManager,
        Func<NewsletterConfig> config,
        ILogger<NewsletterService> logger)
    {
        _sender = sender;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _config = config;
        _logger = logger;
    }

    public Task SendTestAsync() => SendAsync(testOnly: true);

    public Task SendAsync() => SendAsync(testOnly: false);

    /// <summary>True when the SMTP settings carry everything a send needs.</summary>
    public bool IsConfigured => IsSmtpConfigured(_config());

    /// <summary>Pure configured-check so the 400-mapping contract is unit-testable without the host.</summary>
    public static bool IsSmtpConfigured(NewsletterConfig config)
        => !string.IsNullOrEmpty(config.SmtpHost) && !string.IsNullOrEmpty(config.FromAddress);

    private async Task SendAsync(bool testOnly)
    {
        var config = _config();
        if (!IsSmtpConfigured(config))
        {
            throw new InvalidOperationException("Newsletter SMTP is not configured.");
        }

        var configuredRecipients = config.Recipients
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        IReadOnlyList<string> recipients = testOnly
            ? (string.IsNullOrEmpty(config.TestRecipient) ? configuredRecipients : new[] { config.TestRecipient })
            : configuredRecipients;

        var (subject, html) = await ComposeFromRecentlyAdded();
        await _sender.SendAsync(subject, html, recipients);
    }

    /// <summary>Composes a simple HTML digest from items added in the last 7 days.</summary>
    private async Task<(string Subject, string Html)> ComposeFromRecentlyAdded()
    {
        var since = DateTime.UtcNow.AddDays(-7);
        var query = new MediaBrowser.Controller.Entities.InternalItemsQuery(user: null)
        {
            IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Movie, Jellyfin.Data.Enums.BaseItemKind.Series, Jellyfin.Data.Enums.BaseItemKind.Episode },
            OrderBy = new List<(Jellyfin.Data.Enums.ItemSortBy, Jellyfin.Database.Implementations.Enums.SortOrder)>
            {
                (Jellyfin.Data.Enums.ItemSortBy.DateCreated, Jellyfin.Database.Implementations.Enums.SortOrder.Descending)
            },
            Limit = 30
        };

        var result = _libraryManager.GetItemList(query);
        var items = result.Where(item => item.DateCreated >= since).Take(20).ToList();

        var rows = string.Join(string.Empty, items.Select(item =>
            $"<li><strong>{System.Net.WebUtility.HtmlEncode(item.Name)}</strong> — {item.GetType().Name}, added {item.DateCreated:yyyy-MM-dd}</li>"));

        var html = $"""
            <html><body style="font-family:sans-serif">
            <h2>JellyPlay weekly digest</h2>
            <p>New in your library this week:</p>
            <ul>{rows}</ul>
            <p style="color:#888">Sent by the JellyPlay plugin.</p>
            </body></html>
            """;
        return ($"JellyPlay digest — {items.Count} new items", html);
    }
}
