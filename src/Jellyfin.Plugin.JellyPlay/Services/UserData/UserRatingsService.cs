using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Jellyfin.Plugin.JellyPlay.Helpers;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyPlay.Services.UserData;

public sealed record UserRatingEntry(
    Guid ItemId,
    string Name,
    string ItemType,
    bool? Likes,
    int? Rating,
    DateTime? LastPlayedDate,
    int PlayCount);

/// <summary>
/// One-response personal ratings: the user's liked/disliked/rated items joined
/// with basic item info, folded from Jellyfin's own user-data store. The fold
/// is memoized per (user, filter) for a few seconds — the build walks up to
/// 5000 items with one user-data read per item, and back-to-back requests
/// (dashboard, rows, client refresh) share one build. The service has no write
/// paths of its own (mutations go through the host's own user-data API), so
/// the short TTL is the staleness bound. This service stays the only place
/// that talks to IUserDataManager/IUserManager.
/// </summary>
public sealed class UserRatingsService
{
    private static readonly TimeSpan MemoTtl = TimeSpan.FromSeconds(10);

    /// <summary>Hard memo ceiling: at the cap new keys are simply not memoized — a dropped entry only costs its next reader one rebuild.</summary>
    private const int MemoCap = 256;

    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILogger<UserRatingsService> _logger;
    private readonly TimeProvider _clock;

    /// <summary>
    /// Per-(user, filter) short-TTL memo of the built list. The key carries
    /// the NORMALIZED filter only — the raw query string is unvalidated client
    /// input, and without normalization every distinct value would take its
    /// own 5000-item walk and hold the full result forever. Bounded like the
    /// sibling caches: sweep-on-write drops expired entries, the cap drops
    /// new keys past the ceiling.
    /// </summary>
    private readonly ConcurrentDictionary<(Guid UserId, string? Filter), MemoizedRatings> _memo = new();
    private long _lastSweepMs;

    public UserRatingsService(
        IUserManager userManager,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        ILogger<UserRatingsService> logger,
        TimeProvider? clock = null)
    {
        _userManager = userManager;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The user's liked/disliked/rated items, folded from Jellyfin's own user-data store.</summary>
    public IReadOnlyList<UserRatingEntry> GetMyRatings(Guid userId, string? filter)
    {
        if (_userManager.GetUserById(userId) is null)
        {
            return Array.Empty<UserRatingEntry>();
        }

        var key = (userId, NormalizeFilter(filter));
        var nowMs = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        if (_memo.TryGetValue(key, out var memoized) && nowMs - memoized.LoadedAtMs < MemoTtl.TotalMilliseconds)
        {
            return memoized.Entries;
        }

        var entries = BuildRatings(userId, filter);
        if (_memo.Count < MemoCap)
        {
            _memo[key] = new MemoizedRatings(nowMs, entries);
        }

        MaybeSweep(nowMs);
        return entries;
    }

    /// <summary>
    /// Maps the raw query value onto the known filter vocabulary; anything
    /// else — empty, misspelled, hostile — collapses onto the default fold,
    /// exactly the case <see cref="BuildRatings"/>'s switch already treats.
    /// </summary>
    private static string? NormalizeFilter(string? filter)
        => filter switch
        {
            "likes" => "likes",
            "dislikes" => "dislikes",
            "rated" => "rated",
            _ => null
        };

    /// <summary>Occasional O(n) sweep so abandoned (user, filter) keys do not accumulate forever (the shared <see cref="SweepGate"/>).</summary>
    private void MaybeSweep(long nowMs)
    {
        if (!SweepGate.Enter(ref _lastSweepMs, nowMs, (long)MemoTtl.TotalMilliseconds))
        {
            return;
        }

        foreach (var (key, memoized) in _memo)
        {
            if (nowMs - memoized.LoadedAtMs >= MemoTtl.TotalMilliseconds)
            {
                _memo.TryRemove(key, out _);
            }
        }
    }

    private IReadOnlyList<UserRatingEntry> BuildRatings(Guid userId, string? filter)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return Array.Empty<UserRatingEntry>();
        }

        var items = _libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Movie, Jellyfin.Data.Enums.BaseItemKind.Series, Jellyfin.Data.Enums.BaseItemKind.Episode, Jellyfin.Data.Enums.BaseItemKind.Audio, Jellyfin.Data.Enums.BaseItemKind.MusicAlbum, Jellyfin.Data.Enums.BaseItemKind.Book },
            Recursive = true,
            Limit = 5000
        });

        var entries = new List<UserRatingEntry>();
        foreach (var item in items)
        {
            var userData = _userDataManager.GetUserData(user, item);
            if (userData is null)
            {
                continue;
            }

            var liked = userData.Likes;
            var rating = userData.Rating;
            var include = filter switch
            {
                "likes" => liked == true,
                "dislikes" => liked == false,
                "rated" => rating is > 0,
                _ => liked is not null || rating is > 0
            };

            if (!include)
            {
                continue;
            }

            entries.Add(new UserRatingEntry(
                item.Id,
                item.Name ?? string.Empty,
                item.GetType().Name,
                liked,
                rating is > 0 ? (int?)Convert.ToInt32(rating.Value) : null,
                userData.LastPlayedDate,
                userData.PlayCount));
        }

        return entries;
    }

    private sealed record MemoizedRatings(long LoadedAtMs, IReadOnlyList<UserRatingEntry> Entries);
}
