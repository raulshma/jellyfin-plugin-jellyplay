using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyPlay.Services.Newsletter;

public interface INewsletterSender
{
    Task SendAsync(string subject, string htmlBody, IReadOnlyList<string> recipients);
}

/// <summary>Sends the newsletter over SMTP (System.Net.Mail). Credentials come from plugin config.</summary>
public sealed class SmtpNewsletterSender : INewsletterSender
{
    private readonly ILogger<SmtpNewsletterSender> _logger;

    public SmtpNewsletterSender(ILogger<SmtpNewsletterSender> logger)
    {
        _logger = logger;
    }

    public async Task SendAsync(string subject, string htmlBody, IReadOnlyList<string> recipients)
    {
        if (recipients.Count == 0)
        {
            throw new InvalidOperationException("No newsletter recipients resolved.");
        }

        using var message = new System.Net.Mail.MailMessage
        {
            Subject = subject,
            Body = htmlBody,
            IsBodyHtml = true,
            From = new System.Net.Mail.MailAddress(
                NewsletterSettings().FromAddress,
                NewsletterSettings().FromName)
        };
        foreach (var recipient in recipients.Where(address => !string.IsNullOrWhiteSpace(address)))
        {
            message.To.Add(recipient);
        }

        var settings = NewsletterSettings();
        using var client = new System.Net.Mail.SmtpClient(settings.SmtpHost, settings.SmtpPort)
        {
            EnableSsl = settings.UseSsl,
            Credentials = new System.Net.NetworkCredential(settings.SmtpUsername, settings.SmtpPassword)
        };

        await client.SendMailAsync(message);
        _logger.LogInformation("Newsletter '{Subject}' sent to {Count} recipients", subject, message.To.Count);
    }

    private static NewsletterConfig NewsletterSettings()
        => JellyPlayPlugin.Instance!.Configuration.Newsletter;
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
    private readonly ILogger<NewsletterService> _logger;

    public NewsletterService(
        INewsletterSender sender,
        IUserManager userManager,
        ILibraryManager libraryManager,
        ILogger<NewsletterService> logger)
    {
        _sender = sender;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public Task SendTestAsync() => SendAsync(testOnly: true);

    public Task SendAsync() => SendAsync(testOnly: false);

    private async Task SendAsync(bool testOnly)
    {
        var config = JellyPlayPlugin.Instance!.Configuration.Newsletter;
        if (string.IsNullOrEmpty(config.SmtpHost) || string.IsNullOrEmpty(config.FromAddress))
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

        var serverName = _libraryManager.ToString();
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
