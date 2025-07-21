using System;

namespace W3ChampionsChatService.Chats;

public class ChatMessage(ChatUser user, string message)
{
    private readonly DateTimeOffset _time = DateTimeOffset.UtcNow;
    public readonly string Id = Guid.NewGuid().ToString();
    public ChatUser User { get; } = user;
    public string Message { get; } = message;

    public string Time => _time.ToString();
}
