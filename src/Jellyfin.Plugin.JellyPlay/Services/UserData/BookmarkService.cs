using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.JellyPlay.Services.UserData;

/// <summary>Book-reader bookmark/notes sync backed by the plugin database.</summary>
public sealed class BookmarkService
{
    private readonly Jellyfin.Plugin.JellyPlay.Storage.JellyPlayDatabase _db;
    private readonly TimeProvider _clock;

    public BookmarkService(Jellyfin.Plugin.JellyPlay.Storage.JellyPlayDatabase db, TimeProvider? clock = null)
    {
        _db = db;
        _clock = clock ?? TimeProvider.System;
    }

    public IReadOnlyList<Storage.Models.BookmarkRow> GetBookmarks(string userId, string? itemId)
        => _db.GetBookmarks(userId, itemId);

    public Storage.Models.BookmarkRow Upsert(string userId, string itemId, Api.BookmarkRequest request)
    {
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
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
