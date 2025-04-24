using System;
using System.Collections.Generic;

namespace W3ChampionsChatService.Chats
{
    public class Message
    {
        public DateTime TimeStamp { get; set; } = DateTime.UtcNow;
        public ChatUser User { get; set; }
        public string MessageText { get; set; }

        // Fields for System Messages
        public bool IsSystemMessage { get; set; } = false;
        public string SystemMessageKey { get; set; } // e.g., "system.customGame.hostChanged"
        public Dictionary<string, object> SystemMessageParams { get; set; } // Parameters for localization

        public Message(ChatUser user, string message)
        {
            User = user;
            MessageText = message;
        }

        // Parameterless constructor for deserialization if needed
        public Message() {}
    }
}