using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.SignalR;
using W3ChampionsChatService.Authentication;

namespace W3ChampionsChatService.Sessions;

public interface ISessionRegistry
{
    /// <summary>Registers connectionId as THE session for identity.BattleTag (case-insensitive).
    /// Returns the displaced previous session (caller notifies + closes it), or null.</summary>
    ChatSession Register(string connectionId, W3CUserAuthentication identity, HubCallerContext context);

    /// <summary>Identity-checked teardown: forgets the connection, and removes the battleTag
    /// mapping ONLY if it still points at connectionId. Safe against the displaced-old-socket race.
    /// <para>
    /// Returns TRUE iff this call actually removed the battleTag's live session mapping — a GENUINE
    /// online→offline transition. Returns FALSE for an unknown/already-torn-down connectionId, OR for a
    /// displaced OLD socket whose mapping already points at a NEWER connection (the user is still online).
    /// C6 (Task 9, D11) uses this as the disconnect-side transition signal that gates the
    /// <c>PresenceChanged(offline)</c> emit. Additive: existing callers may ignore the result.
    /// </para></summary>
    bool Unregister(string connectionId);

    bool TryGetByConnectionId(string connectionId, out ChatSession session);

    ChatSession GetByBattleTag(string battleTag);

    /// <summary>
    /// C6 (Task 8, D10): a snapshot of every currently-registered battleTag — Tier 2 of
    /// <c>SearchMentionCandidates</c> ("online users anywhere", not necessarily viewing the channel
    /// being searched). Display casing: each entry is SOME live casing this battleTag has connected
    /// under (never the lowercased Mongo/directory convention) — specifically the casing from the
    /// FIRST <see cref="Register"/> call since the entry was last fully removed, since
    /// <c>Dictionary&lt;TKey,TValue&gt;</c>'s indexer only replaces the VALUE for an existing key, never
    /// the key's own casing. <c>SearchMentionCandidates</c>' enrichment step prefers the directory's
    /// freshly-upserted <c>DisplayBattleTag</c> over this raw fallback whenever a directory row exists,
    /// so this staleness only surfaces for an online user with no directory row at all — a narrow edge
    /// case. Taken under the same lock as every other read here.
    /// </summary>
    IReadOnlyCollection<string> GetOnlineBattleTags();
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

    public bool Unregister(string connectionId)
    {
        lock (_lock)
        {
            if (!_battleTagByConnection.Remove(connectionId, out var battleTag))
            {
                // Unknown/already-torn-down connection — nothing to do; not a transition.
                return false;
            }

            // IDENTITY-CHECKED teardown (flo "signed in elsewhere" race guard): remove the battleTag
            // mapping ONLY if it still points at THIS connection. After a displacement the mapping
            // points at the NEW connection — the dying OLD socket must not evict it (and its disconnect
            // is NOT an online→offline transition: the user is still online via the newer connection).
            if (_byBattleTag.TryGetValue(battleTag, out var current) && current.ConnectionId == connectionId)
            {
                _byBattleTag.Remove(battleTag);
                return true; // this call removed the live mapping — a genuine offline transition.
            }

            return false; // displaced old socket — the mapping already points at a newer connection.
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

    public IReadOnlyCollection<string> GetOnlineBattleTags()
    {
        lock (_lock)
        {
            return _byBattleTag.Keys.ToList();
        }
    }
}
