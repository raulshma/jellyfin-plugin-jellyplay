using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Jellyfin.Plugin.JellyPlay.Api;
using Jellyfin.Plugin.JellyPlay.Helpers;
using Jellyfin.Plugin.JellyPlay.Services.Messages;
using Jellyfin.Plugin.JellyPlay.Storage.Models;
using MediaBrowser.Common.Api;

namespace Jellyfin.Plugin.JellyPlay.Api;

[ApiController]
[Authorize]
[Route(JellyPlayContract.RoutePrefix)]
public class MessagesController : ControllerBase
{
    private readonly MessageService _messages;

    public MessagesController(MessageService messages)
    {
        _messages = messages;
    }

    [HttpGet("messages")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<MessageDto>> GetInbox()
        => JellyPlayResponses.Camel(new { messages = _messages.GetInbox(User.GetUserId().ToString(), User.IsAdmin()) });

    [HttpPost("messages/{messageId}/read")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult MarkRead([FromRoute, Required] string messageId)
    {
        _messages.MarkRead(User.GetUserId().ToString(), messageId);
        return NoContent();
    }

    [HttpGet("admin/messages")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<MessageRow>> GetAll() => JellyPlayResponses.Camel(new { messages = _messages.GetAll() });

    [HttpPost("admin/messages")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Upsert([FromBody, Required] MessageAdminRequest request) => JellyPlayResponses.Camel(_messages.Upsert(request));

    [HttpDelete("admin/messages/{messageId}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult Delete([FromRoute, Required] string messageId)
        => _messages.Delete(messageId) ? NoContent() : NotFound();
}
