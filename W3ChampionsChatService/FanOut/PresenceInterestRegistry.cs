using System;
using System.Collections.Generic;
using System.Linq;

namespace W3ChampionsChatService.FanOut;

/// <summary>
/// C6 (spec §9, C6-plan.md D11, Task 9) — the presence-interest index. It answers exactly ONE question:
/// "when a given battleTag's presence changes, which live connections are allowed to be told?" Interest
/// is DERIVED, automatically and exclusively, from what each connection is currently focused on — there
/// is deliberately NO client-facing subscribe API, so a client can never accumulate stale or excessive
/// surveillance of another user's online status. A connection C becomes interested in tag T iff C has a
/// Dm/GroupDm (private-lane) channel FOCUSED and T is a current member of that channel (and T is not C's
/// own tag — you never watch your own presence). Interest is revoked the moment ANY of those conditions
/// ends: C unfocuses the channel (<see cref="RevokeFocus"/>), C disconnects
/// (<see cref="RemoveConnection"/>), T stops being a member (<see cref="OnMemberRemoved"/>), or the
/// channel is deleted (<see cref="RemoveChannel"/>).
/// <para>
/// STATE (single-lock idiom, mirroring <see cref="W3ChampionsChatService.Sessions.SessionRegistry"/> and
/// <see cref="FocusRegistry"/> — one lock, reverse indexes maintained in lockstep, ALL work inside the
/// lock):
/// <list type="bullet">
/// <item><c>_watchedTagsByConnectionChannel</c>: connectionId → (channelId → the set of tags watched via
/// THAT channel). Per-channel granularity is what gives <see cref="RevokeFocus"/> its refcount-by-channel
/// semantics — a tag reachable via a SECOND focused channel of the same connection survives revoking the
/// first.</item>
/// <item><c>_interestedConnectionsByTag</c>: tag → the connections interested in it. This is the REVERSE
/// index and the sole read path (<see cref="GetInterestedConnections"/>). Keyed
/// <see cref="StringComparer.OrdinalIgnoreCase"/> — LOAD-BEARING: watched tags are stored from the
/// (lowercased) membership rows, while a presence subject connects under its LIVE (mixed) casing, so an
/// ordinal reverse lookup would silently miss every watcher.</item>
/// <item><c>_watchingConnectionsByChannel</c>: channelId → the connections that registered interest
/// through it. Lets <see cref="OnMemberAdded"/>/<see cref="OnMemberRemoved"/> update every watcher of a
/// channel in O(watchers) when its membership changes. Because <see cref="RegisterFocus"/> is the ONLY
/// writer and the hub calls it for private-lane channels ONLY, this map never contains a Public/
/// SemiPublic/System channel — so the member-change hooks are automatically a no-op for those.</item>
/// <item><c>_ownTagByConnection</c>: connectionId → its own battleTag, so a connection is never made to
/// watch its own presence on ANY path (registration or a later membership add).</item>
/// </list>
/// </para>
/// </summary>
public class PresenceInterestRegistry
{
    // connectionId -> (channelId -> tags watched via that channel). Tag sets are case-insensitive.
    private readonly Dictionary<string, Dictionary<string, HashSet<string>>> _watchedTagsByConnectionChannel =
        new Dictionary<string, Dictionary<string, HashSet<string>>>();

    // tag -> interested connections (the read path). OrdinalIgnoreCase: stored (lowercased) membership
    // tags must still be found under a subject's live (mixed) connect casing.
    private readonly Dictionary<string, HashSet<string>> _interestedConnectionsByTag =
        new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

    // channelId -> connections that registered interest through it (its watchers). Only ever holds
    // Dm/GroupDm ids, since RegisterFocus (the sole writer) is called for private lanes only.
    private readonly Dictionary<string, HashSet<string>> _watchingConnectionsByChannel =
        new Dictionary<string, HashSet<string>>();

    // connectionId -> its own battleTag — so a connection never watches its own presence, on any path.
    private readonly Dictionary<string, string> _ownTagByConnection =
        new Dictionary<string, string>();

    private readonly object _lock = new object();

    /// <summary>
    /// Registers <paramref name="connectionId"/>'s interest, derived from focusing a Dm/GroupDm channel,
    /// in every tag of <paramref name="memberTags"/> EXCEPT <paramref name="ownBattleTag"/> (you never
    /// watch your own presence; the compare is case-insensitive). AUTHORITATIVE resync: the provided
    /// membership fully REPLACES whatever was previously watched via <paramref name="channelId"/> for this
    /// connection, so a re-focus that carries a changed roster self-corrects (a member that vanished from
    /// the roster loses its interest here, subject to the refcount check across the connection's OTHER
    /// focused channels). Idempotent for an unchanged re-focus. This is the SOLE entry point through which
    /// a connection ever gains presence interest — there is no subscribe API.
    /// </summary>
    public void RegisterFocus(string connectionId, string channelId, string ownBattleTag, IEnumerable<string> memberTags)
    {
        lock (_lock)
        {
            _ownTagByConnection[connectionId] = ownBattleTag;

            // The desired watched-set for THIS channel: every distinct member tag except our own.
            var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (memberTags != null)
            {
                foreach (var tag in memberTags)
                {
                    if (string.IsNullOrEmpty(tag))
                    {
                        continue;
                    }
                    if (string.Equals(tag, ownBattleTag, StringComparison.OrdinalIgnoreCase))
                    {
                        continue; // never watch your own presence
                    }
                    desired.Add(tag);
                }
            }

            var byChannel = GetOrCreateChannelMapNoLock(connectionId);
            byChannel.TryGetValue(channelId, out var previous); // null on a first focus of this channel
            byChannel[channelId] = desired;
            AddWatcherNoLock(channelId, connectionId);

            // Newly-desired tags gain a reverse-index entry (HashSet.Add makes this idempotent).
            foreach (var tag in desired)
            {
                AddInterestNoLock(connectionId, tag);
            }

            // Tags watched via this channel BEFORE but no longer desired (roster shrank on a re-focus):
            // drop them unless still reachable via another of this connection's focused channels.
            if (previous != null)
            {
                foreach (var tag in previous)
                {
                    if (!desired.Contains(tag))
                    {
                        RemoveInterestIfOrphanedNoLock(connectionId, tag);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Revokes the interest <paramref name="connectionId"/> derived from focusing
    /// <paramref name="channelId"/> (called on unfocus / voluntary leave / forced removal of that
    /// connection). Refcount-by-channel: a tag that is ALSO watched via another currently-focused channel
    /// of the SAME connection stays watched — only tags reachable ONLY through this channel lose their
    /// interest. No-op (safe) for a (connection, channel) that was never registered — callers may invoke
    /// this unconditionally without a "was this even private?" pre-check.
    /// </summary>
    public void RevokeFocus(string connectionId, string channelId)
    {
        lock (_lock)
        {
            if (!_watchedTagsByConnectionChannel.TryGetValue(connectionId, out var byChannel))
            {
                return;
            }
            if (!byChannel.Remove(channelId, out var watched))
            {
                return;
            }

            RemoveWatcherNoLock(channelId, connectionId);

            // Removed this channel's entry FIRST, so the orphan check now scans only the connection's
            // REMAINING focused channels — that is exactly the refcount-by-channel survival test.
            foreach (var tag in watched)
            {
                RemoveInterestIfOrphanedNoLock(connectionId, tag);
            }

            if (byChannel.Count == 0)
            {
                _watchedTagsByConnectionChannel.Remove(connectionId);
                _ownTagByConnection.Remove(connectionId);
            }
        }
    }

    /// <summary>
    /// A tag became a member of <paramref name="channelId"/> (someone joined a Dm/GroupDm): every
    /// connection currently watching that channel gains interest in <paramref name="tag"/> — EXCEPT a
    /// watcher whose own battleTag equals it (a connection never watches itself). No-op if nobody is
    /// watching the channel (always true for Public/SemiPublic/System, since nothing registers interest
    /// through them).
    /// </summary>
    public void OnMemberAdded(string channelId, string tag)
    {
        if (string.IsNullOrEmpty(tag))
        {
            return;
        }
        lock (_lock)
        {
            if (!_watchingConnectionsByChannel.TryGetValue(channelId, out var watchers))
            {
                return;
            }
            foreach (var connectionId in watchers)
            {
                if (_ownTagByConnection.TryGetValue(connectionId, out var own)
                    && string.Equals(own, tag, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // never watch your own presence
                }
                if (_watchedTagsByConnectionChannel.TryGetValue(connectionId, out var byChannel)
                    && byChannel.TryGetValue(channelId, out var watched))
                {
                    watched.Add(tag);
                }
                AddInterestNoLock(connectionId, tag);
            }
        }
    }

    /// <summary>
    /// A tag stopped being a member of <paramref name="channelId"/> (left / was forcibly removed / the
    /// Dm counterpart departed): every connection watching that channel loses interest in
    /// <paramref name="tag"/>, unless it still reaches that tag via another of its focused channels
    /// (refcount-by-channel). No-op if nobody is watching the channel.
    /// </summary>
    public void OnMemberRemoved(string channelId, string tag)
    {
        if (string.IsNullOrEmpty(tag))
        {
            return;
        }
        lock (_lock)
        {
            if (!_watchingConnectionsByChannel.TryGetValue(channelId, out var watchers))
            {
                return;
            }
            foreach (var connectionId in watchers)
            {
                if (_watchedTagsByConnectionChannel.TryGetValue(connectionId, out var byChannel)
                    && byChannel.TryGetValue(channelId, out var watched))
                {
                    watched.Remove(tag);
                }
                RemoveInterestIfOrphanedNoLock(connectionId, tag);
            }
        }
    }

    /// <summary>
    /// Full teardown of a deleted channel (e.g. an emptied group auto-deleted): drops every watcher's
    /// interest that was derived through it, honoring the refcount for tags those watchers also reach via
    /// other focused channels. No-op if the channel had no watchers.
    /// </summary>
    public void RemoveChannel(string channelId)
    {
        lock (_lock)
        {
            if (!_watchingConnectionsByChannel.Remove(channelId, out var watchers))
            {
                return;
            }
            foreach (var connectionId in watchers)
            {
                if (_watchedTagsByConnectionChannel.TryGetValue(connectionId, out var byChannel)
                    && byChannel.Remove(channelId, out var watched))
                {
                    foreach (var tag in watched)
                    {
                        RemoveInterestIfOrphanedNoLock(connectionId, tag);
                    }
                    if (byChannel.Count == 0)
                    {
                        _watchedTagsByConnectionChannel.Remove(connectionId);
                        _ownTagByConnection.Remove(connectionId);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Full teardown on disconnect: drops ALL interest <paramref name="connectionId"/> held (it can no
    /// longer be told about anyone). The connection's role as a WATCHER is what is removed here; other
    /// connections' interest in THIS connection's battleTag (keyed on the subject, not the watcher) is
    /// untouched — that is what a separate <c>PresenceChanged(offline)</c> emit conveys. No-op if the
    /// connection held no interest.
    /// </summary>
    public void RemoveConnection(string connectionId)
    {
        lock (_lock)
        {
            _ownTagByConnection.Remove(connectionId);
            if (!_watchedTagsByConnectionChannel.Remove(connectionId, out var byChannel))
            {
                return;
            }
            foreach (var (channelId, watched) in byChannel)
            {
                RemoveWatcherNoLock(channelId, connectionId);
                // The whole connection is gone, so no other channel can keep any of these tags alive for
                // it — remove directly rather than via the orphan check (its per-connection map is gone).
                foreach (var tag in watched)
                {
                    RemoveInterestDirectNoLock(connectionId, tag);
                }
            }
        }
    }

    /// <summary>
    /// The read path: a snapshot of every connection that should be told when
    /// <paramref name="battleTag"/>'s presence changes. Case-insensitive (a live-cased subject resolves
    /// interest recorded under the lowercased membership casing). Returns a COPY so the caller can iterate
    /// (and fault-isolate per-recipient sends) outside the lock. Empty for a tag nobody is watching.
    /// </summary>
    public IReadOnlyCollection<string> GetInterestedConnections(string battleTag)
    {
        lock (_lock)
        {
            return _interestedConnectionsByTag.TryGetValue(battleTag, out var connections)
                ? connections.ToList()
                : Array.Empty<string>();
        }
    }

    // ---- internals (all callers already hold _lock) ------------------------------------------------

    private Dictionary<string, HashSet<string>> GetOrCreateChannelMapNoLock(string connectionId)
    {
        if (!_watchedTagsByConnectionChannel.TryGetValue(connectionId, out var byChannel))
        {
            byChannel = new Dictionary<string, HashSet<string>>();
            _watchedTagsByConnectionChannel[connectionId] = byChannel;
        }
        return byChannel;
    }

    private void AddWatcherNoLock(string channelId, string connectionId)
    {
        if (!_watchingConnectionsByChannel.TryGetValue(channelId, out var watchers))
        {
            watchers = new HashSet<string>();
            _watchingConnectionsByChannel[channelId] = watchers;
        }
        watchers.Add(connectionId);
    }

    private void RemoveWatcherNoLock(string channelId, string connectionId)
    {
        if (_watchingConnectionsByChannel.TryGetValue(channelId, out var watchers))
        {
            watchers.Remove(connectionId);
            if (watchers.Count == 0)
            {
                _watchingConnectionsByChannel.Remove(channelId);
            }
        }
    }

    private void AddInterestNoLock(string connectionId, string tag)
    {
        if (!_interestedConnectionsByTag.TryGetValue(tag, out var connections))
        {
            connections = new HashSet<string>();
            _interestedConnectionsByTag[tag] = connections;
        }
        connections.Add(connectionId);
    }

    // Drops the (connection, tag) reverse entry ONLY if the connection no longer reaches tag through any
    // of its currently-focused channels — the refcount-by-channel survival test.
    private void RemoveInterestIfOrphanedNoLock(string connectionId, string tag)
    {
        if (ConnectionStillWatchesTagNoLock(connectionId, tag))
        {
            return;
        }
        RemoveInterestDirectNoLock(connectionId, tag);
    }

    private void RemoveInterestDirectNoLock(string connectionId, string tag)
    {
        if (_interestedConnectionsByTag.TryGetValue(tag, out var connections))
        {
            connections.Remove(connectionId);
            if (connections.Count == 0)
            {
                _interestedConnectionsByTag.Remove(tag);
            }
        }
    }

    private bool ConnectionStillWatchesTagNoLock(string connectionId, string tag)
    {
        if (!_watchedTagsByConnectionChannel.TryGetValue(connectionId, out var byChannel))
        {
            return false;
        }
        foreach (var tags in byChannel.Values)
        {
            if (tags.Contains(tag))
            {
                return true;
            }
        }
        return false;
    }
}
