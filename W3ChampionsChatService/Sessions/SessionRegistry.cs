using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.SignalR;
using W3ChampionsChatService.Authentication;

namespace W3ChampionsChatService.Sessions;

public interface ISessionRegistry
{
    /// <summary>Registers connectionId as THE session for identity.BattleTag (case-insensitive).
    /// Returns the displaced previous session (caller notifies + closes it), or null.</summary>
    ChatSession Register(string connectionId, W3CUserAuthentication identity, HubCallerContext context);

    /// <summary>Identity-checked teardown: forgets the connection, and removes the battleTag
    /// mapping ONLY if it still points at connectionId. Safe against the displaced-old-socket race.</summary>
    void Unregister(string connectionId);

    bool TryGetByConnectionId(string connectionId, out ChatSession session);

    ChatSession GetByBattleTag(string battleTag);
}

/// <summary>
/// The authoritative battleTag→connection map enforcing exactly ONE active connection per battleTag
/// (C2). Single-instance in-memory, by design: session state is node-local and never needs to
/// survive a restart. Concurrency idiom mirrors Chats/ConnectionMapping.cs: two private dictionaries
/// guarded by a single lock, with every public method doing all of its work inside that lock.
/// </summary>
public class SessionRegistry : ISessionRegistry
{
    // The current session per battleTag. Case-insensitive: the DB lowercases battleTags while live
    // ones keep their casing (see Chats/ConnectionMapping.GetConnectionIdsForUser), so an exact
    // compare would silently miss a casing mismatch.
    private readonly Dictionary<string, ChatSession> _byBattleTag =
        new Dictionary<string, ChatSession>(StringComparer.OrdinalIgnoreCase);

    // Reverse map: connectionId -> battleTag. Default comparer — SignalR connection ids are exact.
    private readonly Dictionary<string, string> _battleTagByConnection =
        new Dictionary<string, string>();

    private readonly object _lock = new object();

    public ChatSession Register(string connectionId, W3CUserAuthentication identity, HubCallerContext context)
    {
        lock (_lock)
        {
            _battleTagByConnection[connectionId] = identity.BattleTag;

            _byBattleTag.TryGetValue(identity.BattleTag, out var previous);
            _byBattleTag[identity.BattleTag] = new ChatSession
            {
                ConnectionId = connectionId,
                Identity = identity,
                Context = context
            };

            // Deliberately DO NOT remove the displaced OLD connection's reverse-map entry here: it
            // lives until that connection's own Unregister. This keeps the identity check in
            // Unregister the SINGLE load-bearing guard against the flo "signed in elsewhere" race
            // (and keeps the displacement-race test mutation-sensitive to exactly that check).
            return previous; // null when there was no previous session for this battleTag
        }
    }

    public void Unregister(string connectionId)
    {
        lock (_lock)
        {
            if (!_battleTagByConnection.Remove(connectionId, out var battleTag))
            {
                // Unknown/already-torn-down connection — nothing to do.
                return;
            }

            // IDENTITY-CHECKED teardown (flo "signed in elsewhere" race guard): remove the battleTag
            // mapping ONLY if it still points at THIS connection. After a displacement the mapping
            // points at the NEW connection — the dying OLD socket must not evict it.
            if (_byBattleTag.TryGetValue(battleTag, out var current) && current.ConnectionId == connectionId)
            {
                _byBattleTag.Remove(battleTag);
            }
        }
    }

    public bool TryGetByConnectionId(string connectionId, out ChatSession session)
    {
        lock (_lock)
        {
            session = null;
            if (!_battleTagByConnection.TryGetValue(connectionId, out var battleTag))
            {
                return false;
            }

            // Return the main entry ONLY if it is still THIS connection. A displaced-but-not-yet-closed
            // OLD connection resolves to nothing current — fail-closed for the Task-7 permission filter.
            if (_byBattleTag.TryGetValue(battleTag, out var entry) && entry.ConnectionId == connectionId)
            {
                session = entry;
                return true;
            }

            return false;
        }
    }

    public ChatSession GetByBattleTag(string battleTag)
    {
        lock (_lock)
        {
            return _byBattleTag.TryGetValue(battleTag, out var session) ? session : null;
        }
    }
}
