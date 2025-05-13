using System.Collections.Generic;
using System.Linq;

namespace W3ChampionsChatService.Chats;

public class ConnectionMapping
{
    private readonly Dictionary<string, Dictionary<string, ChatUser>> _connections =
        new Dictionary<string, Dictionary<string, ChatUser>>();

    public List<ChatUser> GetUsersOfRoom(string chatRoom)
    {
        lock (_connections)
        {
            return _connections[chatRoom].Values.Select(v => v).OrderBy(r => r.BattleTag).ToList();
        }
    }

    public void Add(string connectionId, string chatRoom, ChatUser user)
    {
        lock (_connections)
        {
            if (!_connections.ContainsKey(chatRoom))
            {
                var chatUsers = new Dictionary<string, ChatUser> { { connectionId, user } };
                _connections.Add(chatRoom, chatUsers);
            }
            else
            {
                var chatUsers = _connections[chatRoom];
                if (!chatUsers.ContainsKey(connectionId))
                {
                    chatUsers.Add(connectionId, user);
                }
            }
        }
    }

    public ChatUser GetUser(string connectionId)
    {
        lock (_connections)
        {
            var connection = _connections.Values.SingleOrDefault(r => r.ContainsKey(connectionId));
            return connection?[connectionId];
        }
    }

    public void Remove(string connectionId)
    {
        lock (_connections)
        {
            var connection = _connections.Values.SingleOrDefault(r => r.ContainsKey(connectionId));
            connection?.Remove(connectionId);
        }
    }

    public string GetRoom(string connectionId)
    {
        lock (_connections)
        {
            foreach (var keyValuePair in _connections)
            {
                var contains = keyValuePair.Value.Keys.Contains(connectionId);
                if (contains) return keyValuePair.Key;
            }

            return null;
        }
    }
}
