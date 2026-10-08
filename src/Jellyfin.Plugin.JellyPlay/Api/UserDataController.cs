using System.ComponentModel.DataAnnotations;
using System.Linq;
using Jellyfin.Plugin.JellyPlay.Helpers;
using Jellyfin.Plugin.JellyPlay.Services.UserData;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyPlay.Api;

[ApiController]
[Authorize]
[Route(JellyPlayContract.RoutePrefix)]
public class UserDataController : JellyPlayControllerBase
{
    private readonly UserRatingsService _ratings;
    private readonly BookmarkService _bookmarks;

    public UserDataController(UserRatingsService ratings, BookmarkService bookmarks)
    {
        _ratings = ratings;
        _bookmarks = bookmarks;
    }

    [HttpGet("userratings/mine")]
    public IActionResult GetMyRatings([FromQuery] string? filter)
        => JellyPlayResponses.Camel(new { ratings = _ratings.GetMyRatings(User.GetUserId(), filter) });

    [HttpGet("bookmarks/{itemId}")]
    public IActionResult GetBookmarks([FromRoute, Required] string itemId)
        => JellyPlayResponses.Camel(new { bookmarks = _bookmarks.GetBookmarks(User.GetUserIdString(), itemId).Select(BookmarkService.ToDto) });

    [HttpPost("bookmarks/{itemId}")]
    public IActionResult UpsertBookmark([FromRoute, Required] string itemId, [FromBody, Required] BookmarkRequest request)
        => JellyPlayResponses.Camel(BookmarkService.ToDto(_bookmarks.Upsert(User.GetUserIdString(), itemId, request)));

    [HttpDelete("bookmarks/{itemId}/{bookmarkId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult DeleteBookmark([FromRoute, Required] string itemId, [FromRoute, Required] string bookmarkId)
        => _bookmarks.Delete(User.GetUserIdString(), bookmarkId) ? NoContent() : NotFound();
}
