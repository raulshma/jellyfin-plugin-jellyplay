using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.JellyPlay.Api;
using Jellyfin.Plugin.JellyPlay.Services.Push;
using Jellyfin.Plugin.JellyPlay.Storage;
using Jellyfin.Plugin.JellyPlay.Storage.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyPlay.Services.Messages;

/// <summary>
/// Inbox messages: admin-authored, audience-targeted (all / admins / explicit
/// users), start/end dated, read-state tracked per user. Message creation
/// additionally fans a push notification out to the audience's
/// push-registered devices (fire-and-forget; edits do not re-push).
/// </summary>
public sealed class MessageService
{
    private readonly JellyPlayDatabase _db;
    private readonly PushDispatcher? _push;
    private readonly Func<IReadOnlyList<string>>? _adminUserIds;
    private readonly ILogger<MessageService> _logger;

    public MessageService(
        JellyPlayDatabase db,
        ILogger<MessageService> logger,
        PushDispatcher? push = null,
        Func<IReadOnlyList<string>>? adminUserIds = null)
    {
        _db = db;
        _push = push;
        _adminUserIds = adminUserIds;
        _logger = logger;
    }

    public MessageRow Upsert(MessageAdminRequest request)
    {
        var created = string.IsNullOrEmpty(request.Id);
        var id = created ? Guid.NewGuid().ToString("N") : request.Id!;
        var row = new MessageRow(
            id,
            request.Title,
            request.Body,
            request.Color,
            request.LinkUrl ?? string.Empty,
            request.LinkLabel ?? string.Empty,
            JsonSerializer.Serialize(new AudiencePayload
            {
                Type = request.Audience.Type,
                UserIds = request.Audience.UserIds
            }),
            request.StartsAt,
            request.EndsAt,
            request.OrderIndex,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _db.UpsertMessage(row);

        if (created)
        {
            _push?.DispatchToUsers(
                new PushMessage(PushKinds.Message, request.Title, request.Body),
                ResolveAudienceTargets(request.Audience.Type, request.Audience.UserIds, _adminUserIds?.Invoke() ?? Array.Empty<string>()));
        }

        return row;
    }

    /// <summary>
    /// Push audience for a message: "admins" → the admin id set, "users" → the
    /// explicit ids (empty list delivers to nobody), anything else → null =
    /// every user. Pure so it is unit-testable without the host.
    /// </summary>
    internal static IReadOnlyCollection<string>? ResolveAudienceTargets(
        string? audienceType,
        IReadOnlyList<string> explicitUserIds,
        IReadOnlyList<string> adminUserIds)
        => audienceType switch
        {
            "admins" => new HashSet<string>(adminUserIds, StringComparer.Ordinal),
            "users" => new HashSet<string>(explicitUserIds, StringComparer.Ordinal),
            _ => null
        };

    public bool Delete(string messageId) => _db.DeleteMessage(messageId);

    public IReadOnlyList<MessageRow> GetAll() => _db.GetMessages();

    /// <summary>Messages visible to one user right now, with read flags.</summary>
    public IReadOnlyList<MessageDto> GetInbox(string userId, bool isAdmin)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var read = _db.GetReadMessageIds(userId);
        var inbox = new List<MessageDto>();

        foreach (var row in _db.GetMessages())
        {
            if (row.StartsAt is { } starts && now < starts)
            {
                continue;
            }

            if (row.EndsAt is { } ends && now > ends)
            {
                continue;
            }

            AudiencePayload? audience;
            try
            {
                audience = JsonSerializer.Deserialize<AudiencePayload>(row.AudienceJson);
            }
            catch (JsonException)
            {
                _logger.LogWarning("Message {Id} has corrupt audience payload; defaulting to all", row.Id);
                audience = new AudiencePayload();
            }

            var visible = audience?.Type switch
            {
                "admins" => isAdmin,
                "users" => audience.UserIds.Contains(userId, StringComparer.Ordinal),
                _ => true
            };

            if (!visible)
            {
                continue;
            }

            inbox.Add(new MessageDto(
                row.Id,
                row.Title,
                row.Body,
                row.Color,
                row.LinkUrl,
                row.LinkLabel,
                row.StartsAt,
                row.EndsAt,
                row.OrderIndex,
                row.CreatedAt,
                read.Contains(row.Id)));
        }

        return inbox;
    }

    public void MarkRead(string userId, string messageId)
        => _db.MarkMessageRead(userId, messageId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
}
