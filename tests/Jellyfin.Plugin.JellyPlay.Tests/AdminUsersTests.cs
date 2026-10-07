using System;
using System.Collections.Generic;
using Jellyfin.Plugin.JellyPlay.Services.Admin;
using Xunit;

namespace Jellyfin.Plugin.JellyPlay.Tests;

/// <summary>
/// The one admin-identity fallback decision (AdminUsers.DisplayName), shared by
/// the analytics/push/sync admin overviews: a Guid id resolves through the
/// host's user manager, anything unknown or blank falls back to the raw id
/// string. The host's IUserManager itself stays at the DI boundary.
/// </summary>
public sealed class AdminUsersDisplayNameTests
{
    private static readonly Guid KnownUser = new("11111111-1111-1111-1111-111111111111");

    private static string Resolve(string userId, Func<Guid, string?>? lookup = null)
        => AdminUsers.DisplayName(userId, lookup ?? (id => id == KnownUser ? "Alice" : null));

    [Fact]
    public void KnownUser_ResolvesDisplayName()
    {
        Assert.Equal("Alice", Resolve(KnownUser.ToString()));
    }

    [Theory]
    [InlineData("not-a-guid")] // legacy/non-Guid row ids stay verbatim
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-000000000000")] // Guid.Empty is not a user
    public void NonGuidIds_FallBackToTheRawId_WithoutCallingTheHost(string userId)
    {
        Assert.Equal(userId, Resolve(userId, _ => throw new InvalidOperationException("the host must not be asked")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UnknownOrBlankName_FallsBackToTheRawId(string? name)
    {
        Assert.Equal(KnownUser.ToString(), Resolve(KnownUser.ToString(), _ => name));
    }
}
