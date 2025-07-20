using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Chats;
using Serilog;

namespace W3ChampionsChatService.Mutes;

[ApiController]
[Route("api/deletion")]
public class DeletionController(ChatHistory chatHistory, IHubContext<ChatHub> hubContext) : ControllerBase
{
    private static readonly string AdminSecret = Environment.GetEnvironmentVariable("ADMIN_SECRET") ?? "300C018C-6321-4BAB-B289-9CB3DB760CBB";
    private readonly ChatHistory _chatHistory = chatHistory;
    private readonly IHubContext<ChatHub> _hubContext = hubContext;

    [HttpDelete("messages/{messageId}")]
    [CheckIfBattleTagIsAdmin]
    public async Task<IActionResult> DeleteMessage([FromRoute] string messageId, string secret)
    {
        if (secret != AdminSecret)
        {
            return StatusCode(403);
        }

        Log.Information("Deleting message with ID {MessageId}", messageId);
        var wasDeleted = _chatHistory.DeleteMessage(messageId);

        if (wasDeleted)
        {
            await _hubContext.Clients.All.SendAsync("MessageDeleted", messageId);
            return Ok();
        }

        return NotFound("Message not found");
    }

    [HttpDelete("messages/from-user/{battleTag}")]
    [CheckIfBattleTagIsAdmin]
    public async Task<IActionResult> PurgeMessagesFromUser([FromRoute] string battleTag, string secret)
    {
        if (secret != AdminSecret)
        {
            return StatusCode(403);
        }

        Log.Information("Purging all messages from user {BattleTag}", battleTag);
        var deletedMessages = _chatHistory.DeleteMessagesFromUser(battleTag);

        foreach (var message in deletedMessages)
        {
            await _hubContext.Clients.All.SendAsync("MessageDeleted", message.Id);
        }

        Log.Information("Deleted {Count} messages from user {BattleTag}", deletedMessages.Count, battleTag);

        return Ok(new { DeletedCount = deletedMessages.Count });
    }
}
