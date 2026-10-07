using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.JellyPlay.Services.Admin;

/// <summary>
/// The one admin-identity module: display-name resolution with raw-id fallback
/// (the pattern every admin overview shares) and the administrator-id
/// enumeration behind audience "admins". Thin over the host's
/// <see cref="IUserManager"/> — the fallback decision itself is a pure static,
/// so it is testable without the host.
/// </summary>
public sealed class AdminUsers
{
    private readonly IUserManager _users;

    public AdminUsers(IUserManager users)
    {
        _users = users;
    }

    /// <summary>Display name for one host user; the id string when the host does not know the user.</summary>
    public string ResolveName(Guid userId) => DisplayName(userId.ToString(), id => _users.GetUserById(id)?.Username);

    /// <summary>
    /// The host's administrator ids — audience "admins". Fresh per call so
    /// permission changes apply to the next event without a restart.
    /// </summary>
    public IReadOnlyList<string> AdminUserIds => _users.GetUsers()
        .Where(user => user.HasPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsAdministrator))
        .Select(user => user.Id.ToString())
        .ToList();

    /// <summary>
    /// The pure fallback decision shared by every admin overview: a Guid row
    /// id resolves through <paramref name="lookup"/>; an unknown or blank name
    /// (and any non-Guid or empty id) falls back to the raw id string.
    /// </summary>
    internal static string DisplayName(string userId, Func<Guid, string?> lookup)
    {
        if (!Guid.TryParse(userId, out var guid) || guid == Guid.Empty)
        {
            return userId;
        }

        var name = lookup(guid);
        return string.IsNullOrWhiteSpace(name) ? userId : name;
    }
}
