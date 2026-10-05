using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.JellyPlay.Api;
using Jellyfin.Plugin.JellyPlay.Storage;
using Jellyfin.Plugin.JellyPlay.Storage.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyPlay.Services.Messages;

/// <summary>
/// Inbox messages: admin-authored, audience-targeted (all / admins / explicit
/// users), start/end dated, read-state tracked per user.
/// </summary>
public sealed class MessageService
{
    private readonly JellyPlayDatabase _db;
    private readonly ILogger<MessageService> _logger;

    public MessageService(JellyPlayDatabase db, ILogger<MessageService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public MessageRow Upsert(MessageAdminRequest request)
    {
        var id = string.IsNullOrEmpty(request.Id) ? Guid.NewGuid().ToString("N") : request.Id!;
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
        return row;
    }

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
