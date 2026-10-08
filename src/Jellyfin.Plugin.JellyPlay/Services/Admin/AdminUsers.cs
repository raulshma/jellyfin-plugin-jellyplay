using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
    /// <summary>
    /// The admin-id memoization window (ms). Long enough to collapse a burst
    /// (an ItemAdded flush batch resolves the audience once anyway — this is
    /// belt-and-braces for the other callers), short enough that a permission
    /// change applies within seconds, without a restart.
    /// </summary>
    private const long MemoTtlMs = 10_000;

    private readonly IUserManager _users;
    private readonly TimeProvider _clock;
    private IReadOnlyList<string>? _adminIdsCache;
    private long _adminIdsAtMs;

    public AdminUsers(IUserManager users, TimeProvider? clock = null)
    {
        _users = users;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Display name for one host user; the id string when the host does not know the user.</summary>
    public string ResolveName(Guid userId) => DisplayName(userId.ToString(), id => _users.GetUserById(id)?.Username);

    /// <summary>The host's administrator ids — audience "admins". Memoized for a few
    /// seconds behind the injected clock: a 2000-item import must not pay a
    /// full <see cref="IUserManager"/> enumeration per event (and the event
    /// pipeline resolves once per flush batch besides). Permission changes
    /// still apply within the TTL — no restart needed.</summary>
    public IReadOnlyList<string> AdminUserIds
    {
        get
        {
            var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
            var cached = _adminIdsCache;
            if (cached is not null && now - Volatile.Read(ref _adminIdsAtMs) < MemoTtlMs)
            {
                return cached;
            }

            var fresh = _users.GetUsers()
                .Where(user => user.HasPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsAdministrator))
                .Select(user => user.Id.ToString())
                .ToList();
            _adminIdsCache = fresh;
            Volatile.Write(ref _adminIdsAtMs, now);
            return fresh;
        }
    }

    /// <summary>Every host user (id + username) — the admin pickers' data source (drill-down, preview simulator). Fresh per call.</summary>
    public IReadOnlyList<Api.AdminUserRef> AllUsers()
        => _users.GetUsers()
            .Select(user => new Api.AdminUserRef(user.Id.ToString(), user.Username))
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
