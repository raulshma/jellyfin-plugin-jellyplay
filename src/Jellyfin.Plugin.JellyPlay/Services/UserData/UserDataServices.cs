using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
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
/// with basic item info, folded from Jellyfin's own user-data store.
/// </summary>
public sealed class UserRatingsService
{
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILogger<UserRatingsService> _logger;

    public UserRatingsService(IUserManager userManager, ILibraryManager libraryManager, IUserDataManager userDataManager, ILogger<UserRatingsService> logger)
    {
        _userManager = userManager;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _logger = logger;
    }

    /// <summary>The user's liked/disliked/rated items, folded from Jellyfin's own user-data store.</summary>
    public IReadOnlyList<UserRatingEntry> GetMyRatings(Guid userId, string? filter)
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
}

/// <summary>Book-reader bookmark/notes sync backed by the plugin database.</summary>
public sealed class BookmarkService
{
    private readonly Jellyfin.Plugin.JellyPlay.Storage.JellyPlayDatabase _db;

    public BookmarkService(Jellyfin.Plugin.JellyPlay.Storage.JellyPlayDatabase db)
    {
        _db = db;
    }

    public IReadOnlyList<Storage.Models.BookmarkRow> GetBookmarks(string userId, string? itemId)
        => _db.GetBookmarks(userId, itemId);

    public Storage.Models.BookmarkRow Upsert(string userId, string itemId, Api.BookmarkRequest request)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var id = string.IsNullOrEmpty(request.Id) ? Guid.NewGuid().ToString("N") : request.Id!;
        var existing = _db.GetBookmarks(userId, itemId).FirstOrDefault(bookmark => bookmark.Id == id);
        var row = new Storage.Models.BookmarkRow(
            id,
            userId,
            itemId,
            request.Position,
            request.ChapterIndex,
            request.Label,
            request.Notes,
            existing?.CreatedAt ?? now,
            now);
        _db.UpsertBookmark(row);
        return row;
    }

    public bool Delete(string userId, string bookmarkId) => _db.DeleteBookmark(userId, bookmarkId);

    public static Api.BookmarkDto ToDto(Storage.Models.BookmarkRow row) => new(
        row.Id, row.ItemId, row.Position, row.ChapterIndex, row.Label, row.Notes, row.CreatedAt, row.UpdatedAt);
}
