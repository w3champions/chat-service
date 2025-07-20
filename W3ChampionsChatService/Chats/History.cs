using System.Collections.Generic;
using System.Linq;

namespace W3ChampionsChatService.Chats;

public class ChatHistory : Dictionary<string, List<ChatMessage>>
{
    public void AddMessage(string chatRoom, ChatMessage message)
    {
        if (!ContainsKey(chatRoom))
        {
            Add(chatRoom, new List<ChatMessage> { message });
        }
        else
        {
            this[chatRoom].Add(message);
            if (this[chatRoom].Count > 50)
            {
                this[chatRoom].RemoveAt(0);
            }
        }
    }

    public List<ChatMessage> GetMessages(string chatRoom)
    {
        if (!ContainsKey(chatRoom))
        {
            return new List<ChatMessage>();
        }

        return this[chatRoom];
    }

    public bool DeleteMessage(string messageId)
    {
        foreach (var chatRoom in Keys)
        {
            var messages = this[chatRoom];
            var messageToDelete = messages.FirstOrDefault(m => m.Id == messageId);
            if (messageToDelete != null)
            {
                messages.Remove(messageToDelete);
                return true;
            }
        }
        return false;
    }

    public List<ChatMessage> DeleteMessagesFromUser(string battleTag)
    {
        var deletedMessages = new List<ChatMessage>();
        foreach (var chatRoom in Keys)
        {
            var messages = this[chatRoom];
            var userMessages = messages.Where(m => m.User.BattleTag == battleTag).ToList();
            foreach (var message in userMessages)
            {
                messages.Remove(message);
                deletedMessages.Add(message);
            }
        }
        return deletedMessages;
    }
}
