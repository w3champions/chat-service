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

    /// <summary>
    /// Returns the users of <paramref name="chatRoom"/> as seen by <paramref name="viewerConnectionId"/>.
    /// In banned rooms (see <see cref="DefaultChatRooms.IsBannedRoom"/>), shadow-banned users whose ban
    /// has not yet expired are hidden from all viewers except themselves (a shadow user always sees themselves).
    /// In exempt rooms (clan, lobby) the filter is a no-op — all users are visible.
    /// Uses per-connection cached expiry so the check is EXPIRY-AWARE without a DB read.
    /// All access is under the single <c>_connections</c> lock for consistency.
    /// </summary>
    public List<ChatUser> GetUsersOfRoomForViewer(string chatRoom, string viewerConnectionId)
    {
        lock (_connections)
        {
            if (!_connections.TryGetValue(chatRoom, out var roomConnections))
                return [];

            var isBannedRoom = DefaultChatRooms.IsBannedRoom(chatRoom);
            var now = DateTime.UtcNow;

            return roomConnections
                .Where(kvp =>
                {
                    // Always include the viewer themselves — shadow users see themselves.
                    if (kvp.Key == viewerConnectionId) return true;

                    // In banned rooms, exclude users whose effective mute status is Shadow.
                    // GetEffectiveMuteStatus is expiry-aware: an expired shadow ban returns None.
                    if (isBannedRoom && _mutes.TryGetValue(kvp.Key, out var cached))
                    {
                        var effectiveStatus = (cached.Status == MuteStatus.None || cached.EndDate > now)
                            ? cached.Status
                            : MuteStatus.None;
                        if (effectiveStatus == MuteStatus.Shadow) return false;
                    }

                    return true;
                })
                .Select(kvp => kvp.Value)
                .OrderBy(u => u.BattleTag)
                .ToList();
        }
    }
}
