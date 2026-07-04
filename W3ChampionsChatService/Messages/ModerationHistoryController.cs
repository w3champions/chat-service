using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Protocol;

namespace W3ChampionsChatService.Messages;

/// <summary>
/// C4 Task 7 (D9): the paged moderation-history REST surface — the channelId-based replacement for the
/// retired ChatHistory-backed <c>GET /api/chat/{chatroom}</c> (that endpoint took room NAMEs; this one
/// is channelId-based, matching the new durable chat domain). This is the surface the website-backend's
/// moderation proxy re-points to:
/// <list type="bullet">
/// <item><c>GET /api/moderation/channels</c> resolves the eligible channels (id-resolution — the old
/// endpoint never needed this because it took a room name directly).</item>
/// <item><c>GET /api/moderation/channels/{channelId}/messages</c> pages the REAL durable history —
/// deleted rows and shadow rows included, flags intact, via <see cref="MessageRepository.LoadPageBeforeForModerator"/>
/// (never filtered like a user read).</item>
/// </list>
/// Both actions are gated by <see cref="UserHasPermissionAttribute"/>(<see cref="EPermission.Moderation"/>)
/// and share the SAME <see cref="ChannelModeration.IsModeratable"/> scope wall as
/// <c>ChatHub.DeleteMessage</c>/<c>ChatHub.PurgeMessagesFromUser</c> — a moderator can read history for a
/// channel ONLY if they could also moderate it live. An unresolvable channel 404s (a moderator must
/// never learn whether a private channel merely doesn't exist vs. is out of scope); a resolvable but
/// ineligible channel (Dm/GroupDm/System+Clan/System+Lobby) 403s.
/// </summary>
[ApiController]
[Route("api/moderation")]
public class ModerationHistoryController(ChannelRepository channelRepository, MessageRepository messageRepository) : ControllerBase
{
    private readonly ChannelRepository _channelRepository = channelRepository;
    private readonly MessageRepository _messageRepository = messageRepository;

    [HttpGet("channels")]
    [UserHasPermission(EPermission.Moderation)]
    public async Task<IActionResult> GetModeratableChannels([FromQuery] int limit = 100)
    {
        var channels = await _channelRepository.LoadModeratableChannels(limit);
        return Ok(channels.Select(ModerationChannelDto.FromChannel).ToList());
    }

    [HttpGet("channels/{channelId}/messages")]
    [UserHasPermission(EPermission.Moderation)]
    public async Task<IActionResult> GetChannelMessages(
        [FromRoute] string channelId, [FromQuery] long? beforeSeq, [FromQuery] int limit = 100)
    {
        var channel = await _channelRepository.Load(channelId);
        if (channel == null)
        {
            return NotFound();
        }
        if (!ChannelModeration.IsModeratable(channel))
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        var page = await _messageRepository.LoadPageBeforeForModerator(channelId, beforeSeq, limit);
        var messages = page.Select(m => ModerationMessageDto.FromChannelMessage(channelId, m)).ToList();

        // The cursor uses the SAME clamp the repository applied internally (delegated via
        // MessageRepository.ClampLimit, not re-derived) so "was this page full" lines up exactly with
        // what LoadPageBeforeForModerator actually fetched: a full page means older rows may remain.
        var effectiveLimit = MessageRepository.ClampLimit(limit);
        long? nextBeforeSeq = messages.Count == effectiveLimit ? messages.Min(m => m.Seq) : null;

        return Ok(new ModerationMessagePageDto(channelId, messages, nextBeforeSeq));
    }
}
