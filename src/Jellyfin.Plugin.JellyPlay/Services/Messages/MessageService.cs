using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyPlay.Api;
using Jellyfin.Plugin.JellyPlay.Services.Push;
using Jellyfin.Plugin.JellyPlay.Services.Shared;
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
    private readonly Func<IReadOnlyList<string>>? _adminUserIds;
    private readonly ILogger<MessageService> _logger;
    private readonly TimeProvider _clock;
    private readonly NotificationFanout _fanout;

    public MessageService(
        JellyPlayDatabase db,
        ILogger<MessageService> logger,
        NotificationFanout fanout,
        Func<IReadOnlyList<string>>? adminUserIds = null,
        TimeProvider? clock = null)
    {
        _db = db;
        _adminUserIds = adminUserIds;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _fanout = fanout;
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
            Audience.Serialize(request.Audience),
            request.StartsAt,
            request.EndsAt,
            request.OrderIndex,
            _clock.GetUtcNow().ToUnixTimeMilliseconds());
        _db.UpsertMessage(row);

        if (created)
        {
            // Message-created-only fan-out through the one SSE+push pairing:
            // inbox messages have no SSE stream, so the SSE leg is opted out
            // (push-only) — one BroadcastTargets drives the push audience.
            var targets = Audience.ResolveAudience(request.Audience, _adminUserIds?.Invoke() ?? Array.Empty<string>());
            var push = new PushMessage(PushKinds.Message, request.Title, request.Body);
            _fanout.Publish("events", "message", string.Empty, push, targets, skipSse: true);
        }

        return row;
    }

    public bool Delete(string messageId) => _db.DeleteMessage(messageId);

    /// <summary>Every stored message for the admin registry, projected row→DTO (one projection home for the admin wire shape).</summary>
    public IReadOnlyList<AdminMessageDto> GetAll() => _db.GetMessages().Select(ToAdminDto).ToList();

    /// <summary>The admin wire projection: the storage record's fields verbatim (audienceJson unparsed, byte-identical to the raw row shape).</summary>
    private static AdminMessageDto ToAdminDto(MessageRow row)
        => new(
            row.Id,
            row.Title,
            row.Body,
            row.Color,
            row.LinkUrl,
            row.LinkLabel,
            row.AudienceJson,
            row.StartsAt,
            row.EndsAt,
            row.OrderIndex,
            row.CreatedAt);

    /// <summary>Messages visible to one user right now, with read flags.</summary>
    public IReadOnlyList<MessageDto> GetInbox(string userId, bool isAdmin)
    {
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
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

            var audience = Audience.TryParse(row.AudienceJson);
            if (audience is null)
            {
                _logger.LogWarning("Message {Id} has corrupt audience payload; defaulting to all", row.Id);
                audience = new AudiencePayload();
            }

            var visible = Audience.IsVisible(audience.Type, userId, audience.UserIds, isAdmin);
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
        => _db.MarkMessageRead(userId, messageId, _clock.GetUtcNow().ToUnixTimeMilliseconds());
}
