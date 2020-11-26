using System;

namespace W3ChampionsChatService.Chats
{
    public class ChatMessage
    {
        private readonly DateTimeOffset _time;
        public ChatUser User { get; }
        public string Message { get; }

        public string Time => _time.ToString();

        public ChatMessage(ChatUser user, string message)
        {
            User = user;
            Message = message;
            _time = DateTimeOffset.UtcNow;
        }
    }
}