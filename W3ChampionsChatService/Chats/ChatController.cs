using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using System.Collections.Generic;
using W3ChampionsChatService.Authentication;

namespace W3ChampionsChatService.Chats;

[ApiController]
[Route("api/chats")]
public class ChatController(ChatHistory chatHistory) : ControllerBase
{
    private readonly ChatHistory _chatHistory = chatHistory;

    [HttpGet("{chatroom}")]
    [UserHasPermission(EPermission.Moderation)]
    public async Task<IActionResult> GetChatRoomMessages([FromRoute] string chatroom)
    {
        List<ChatMessage> messages = _chatHistory.GetAllMessages(chatroom);
        return Ok(messages);
    }
}
