using System;
using System.Collections.Generic;
using System.Linq;

namespace W3ChampionsChatService.Chats;

public enum MuteStatus
{
    None,
    Shadow,
    Full
}

/// <summary>
/// Per-connection mute state cached at login. Carries both the resolved status and
/// the expiry so the hub can enforce expiry from the cache alone — no per-message DB read.
/// </summary>
public readonly record struct CachedMute(MuteStatus Status, DateTime EndDate);

public class ConnectionMapping
{
    private readonly Dictionary<string, Dictionary<string, ChatUser>> _connections =
        new Dictionary<string, Dictionary<string, ChatUser>>();

    // Per-connection mute cache. Keyed by connectionId.
    // Guarded by the same lock (_connections) as the connection map so reads/writes
    // of both dictionaries stay atomic and consistent (no TOCTOU on Remove).
    private readonly Dictionary<string, CachedMute> _mutes =
        new Dictionary<string, CachedMute>();

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
            _mutes.Remove(connectionId);
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

    public List<string> GetConnectionIdsForUser(string battleTag)
    {
        lock (_connections)
        {
            var connectionIds = new List<string>();
            foreach (var chatRoomConnections in _connections.Values)
            {
                foreach (var connection in chatRoomConnections)
                {
                    if (connection.Value.BattleTag == battleTag)
                    {
                        connectionIds.Add(connection.Key);
                    }
                }
            }
            return connectionIds;
        }
    }

    /// <summary>
    /// Cache the mute status and expiry for a connection.
    /// Pass <c>status = MuteStatus.None</c> and <c>endDate = DateTime.MinValue</c> for an
    /// explicitly-resolved unbanned connection (distinguishes a cache HIT-None from a MISS).
    /// </summary>
    public void SetMute(string connectionId, MuteStatus status, DateTime endDate)
    {
        lock (_connections)
        {
            _mutes[connectionId] = new CachedMute(status, endDate);
        }
    }

    /// <summary>
    /// Returns true if a cache entry exists for the connection (regardless of status),
    /// and writes it to <paramref name="cached"/>. Returns false on a cache MISS.
    /// Use this to distinguish "cached None" from "never resolved" — only a MISS triggers
    /// a lazy DB re-resolve.
    /// </summary>
    public bool TryGetMute(string connectionId, out CachedMute cached)
    {
        lock (_connections)
        {
            return _mutes.TryGetValue(connectionId, out cached);
        }
    }

    /// <summary>
    /// Returns the effective mute status for <paramref name="now"/>. If there is no cache
    /// entry, or the entry is non-None but its <c>EndDate</c> has passed, returns
    /// <c>MuteStatus.None</c>. Does NOT trigger a DB read — call <see cref="TryGetMute"/>
    /// first to decide whether a lazy resolve is needed.
    /// </summary>
    public MuteStatus GetEffectiveMuteStatus(string connectionId, DateTime now)
    {
        lock (_connections)
        {
            if (!_mutes.TryGetValue(connectionId, out var cached))
                return MuteStatus.None;

            // None is always effective (unbanned); other statuses expire when EndDate passes.
            if (cached.Status == MuteStatus.None || cached.EndDate > now)
                return cached.Status;

            return MuteStatus.None;
        }
    }
}
