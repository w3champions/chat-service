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
public readonly record struct CachedMute(MuteStatus Status, DateTime EndDate)
{
    /// <summary>
    /// The single source of truth for the cache-expiry rule (this is a SECURITY rule —
    /// it gates ban enforcement, so it must never be re-derived in divergent copies).
    /// <c>None</c> never expires (an unbanned connection stays unbanned); any other status
    /// is effective only while <see cref="EndDate"/> is still in the future. Once the ban
    /// has expired it returns <c>None</c>.
    /// </summary>
    public MuteStatus EffectiveStatus(DateTime now) =>
        (Status == MuteStatus.None || EndDate > now) ? Status : MuteStatus.None;
}

public class ConnectionMapping
{
    private readonly Dictionary<string, Dictionary<string, ChatUser>> _connections =
        new Dictionary<string, Dictionary<string, ChatUser>>();

    // Per-connection mute cache. Keyed by connectionId.
    // Guarded by the same lock (_connections) as the connection map so reads/writes
    // of both dictionaries stay atomic and consistent (no TOCTOU on Remove).
    private readonly Dictionary<string, CachedMute> _mutes =
        new Dictionary<string, CachedMute>();

    // Per-connection user, keyed by connectionId. This is the AUTHORITATIVE record of which
    // ChatUser owns a connection — independent of room membership — so GetUser is non-null even
    // for a connection seated in no room (e.g. a no-clan full-banned user). Guarded by the same
    // lock (_connections) as the other maps.
    private readonly Dictionary<string, ChatUser> _users =
        new Dictionary<string, ChatUser>();

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
            // Add is the room-seated path; it also registers the connection→user mapping so a single
            // call covers both. (No-room seats use RegisterUser instead.)
            _users[connectionId] = user;
        }
    }

    /// <summary>
    /// Registers a connection→user mapping WITHOUT seating the connection in any room. For no-room
    /// seats only (e.g. a no-clan full-banned user); room-seated callers use <see cref="Add"/>, which
    /// also registers. This keeps <see cref="GetUser"/> authoritative for every authenticated connection.
    /// </summary>
    public void RegisterUser(string connectionId, ChatUser user)
    {
        lock (_connections)
        {
            _users[connectionId] = user;
        }
    }

    public ChatUser GetUser(string connectionId)
    {
        lock (_connections)
        {
            // Authoritative O(1) lookup; non-null for both room-seated and no-room connections.
            return _users.TryGetValue(connectionId, out var user) ? user : null;
        }
    }

    public void Remove(string connectionId)
    {
        lock (_connections)
        {
            var connection = _connections.Values.SingleOrDefault(r => r.ContainsKey(connectionId));
            connection?.Remove(connectionId);
            _mutes.Remove(connectionId);
            _users.Remove(connectionId);
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
            // Scan _users (the single source of truth for connection→user), so a no-room seat
            // (e.g. a no-clan full-banned user) is reconcilable — room buckets would miss it.
            // Case-insensitive match: the DB lowercases battleTags (see MuteRepository), while
            // connection-stored tags keep their original casing — so an exact (==) compare would
            // silently miss a casing mismatch (e.g. live-ban reconcile).
            return _users
                .Where(kvp => string.Equals(kvp.Value.BattleTag, battleTag, StringComparison.OrdinalIgnoreCase))
                .Select(kvp => kvp.Key)
                .ToList();
        }
    }

    /// <summary>
    /// Cache the mute status and expiry for a connection. Seeded authoritatively at login for
    /// every login path, so after login the cache always reflects the user's true status.
    /// Pass <c>status = MuteStatus.None</c> and <c>endDate = DateTime.MinValue</c> for an
    /// unbanned connection.
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
    /// Since the cache is seeded at login for every path, a MISS only happens for a
    /// connection that is not (yet) logged in; the send/switch hot paths consult
    /// <see cref="GetEffectiveMuteStatus"/> instead and never re-query the DB.
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
    /// <remarks>
    /// The expiry rule itself lives on <see cref="CachedMute.EffectiveStatus"/> (single source).
    /// This method just acquires the lock and delegates to <see cref="GetEffectiveMuteStatusNoLock"/>.
    /// </remarks>
    public MuteStatus GetEffectiveMuteStatus(string connectionId, DateTime now)
    {
        lock (_connections)
        {
            return GetEffectiveMuteStatusNoLock(connectionId, now);
        }
    }

    /// <summary>
    /// Resolves the effective mute status from the cache. The caller MUST already hold
    /// <c>lock(_connections)</c>. A cache MISS returns <c>None</c>; a HIT delegates to
    /// <see cref="CachedMute.EffectiveStatus"/> (the single expiry rule).
    /// </summary>
    private MuteStatus GetEffectiveMuteStatusNoLock(string connectionId, DateTime now)
    {
        // Cache MISS → None; otherwise the single expiry rule lives on CachedMute.EffectiveStatus.
        return _mutes.TryGetValue(connectionId, out var cached)
            ? cached.EffectiveStatus(now)
            : MuteStatus.None;
    }
}
