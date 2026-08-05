using System;
using System.Collections.Generic;
using System.Linq;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.FanOut;

/// <summary>
/// Immutable per-(channel, connection) subscription record. Replaced wholesale (never mutated) by
/// <see cref="OnlineMemberRegistry.SetNotificationLevel"/> / <see cref="OnlineMemberRegistry.SetLastReadSeq"/>
/// via <c>with</c>-expressions, per the immutable-update convention used across the codebase.
/// <para>
/// C5 (Task 5, D11): <see cref="ChannelType"/> is carried so <c>ChatHub.FocusChannel</c>/<c>UnfocusChannel</c>/
/// the disconnect teardown loop can zero-DB-lookup a (channel, connection) entry's type via
/// <see cref="OnlineMemberRegistry.TryGetMember"/> and exclude <see cref="Domain.ChannelType.Dm"/>/
/// <see cref="Domain.ChannelType.GroupDm"/> from the viewer-roster/<c>ViewersAccumulator</c> system —
/// DM/group presence is member-presence via the C6 interest index, never a streamed roster (spec §9).
/// </para>
/// </summary>
public sealed record MemberState(string BattleTag, NotificationLevel NotificationLevel, long LastReadSeq, ChannelType ChannelType);

/// <summary>
/// The online-member subscription index: channelId -> connectionId -> <see cref="MemberState"/>.
/// This is user→channels data held per ONLINE connection — it must NEVER enumerate channel→users
/// from Mongo (see Memberships/ChannelMembership.cs doc comment on the same guardrail). It powers
/// <c>ChannelActivity</c> targeting and the free unread&gt;100 suppression check with ZERO DB reads
/// on the send path.
///
/// Pure in-memory state: NO MongoDB, NO SignalR/IHubContext, NO ISessionRegistry dependency — every
/// <see cref="MemberState"/> is handed in by the caller (typically the SessionStateAssembler at
/// connect, or the hub on Join/Leave/SetNotificationLevel/MarkRead). Concurrency idiom mirrors
/// <see cref="W3ChampionsChatService.Sessions.SessionRegistry"/> (SessionRegistry.cs:29-114): a
/// single lock, with a connectionId->channelIds reverse index maintained in lockstep so
/// <see cref="RemoveConnection"/> never has to scan every channel.
/// </summary>
public class OnlineMemberRegistry
{
    // channelId -> (connectionId -> MemberState).
    private readonly Dictionary<string, Dictionary<string, MemberState>> _membersByChannel =
        new Dictionary<string, Dictionary<string, MemberState>>();

    // Reverse index: connectionId -> the set of channelIds it has a membership entry in.
    private readonly Dictionary<string, HashSet<string>> _channelsByConnection =
        new Dictionary<string, HashSet<string>>();

    private readonly object _lock = new object();

    /// <summary>
    /// Bulk-adds many channel entries for one connection in a single locked pass — the connect-time
    /// seed from <c>MembershipRepository.LoadForUser</c> (mapped to <see cref="MemberState"/> by the
    /// caller; this registry stays decoupled from the Mongo-backed membership model).
    /// </summary>
    public void Seed(string connectionId, IEnumerable<(string ChannelId, MemberState State)> memberships)
    {
        lock (_lock)
        {
            foreach (var (channelId, state) in memberships)
            {
                JoinNoLock(channelId, connectionId, state);
            }
        }
    }

    /// <summary>Adds/replaces a single (channelId, connectionId) membership entry.</summary>
    public void Join(string channelId, string connectionId, MemberState state)
    {
        lock (_lock)
        {
            JoinNoLock(channelId, connectionId, state);
        }
    }

    /// <summary>
    /// Adds/replaces a single (channelId, connectionId) membership entry like <see cref="Join"/>, but
    /// never REGRESSES an already-tracked <see cref="MemberState.LastReadSeq"/>: if an entry already
    /// exists, the stored value becomes <c>Math.Max(existing.LastReadSeq, state.LastReadSeq)</c> instead
    /// of a plain overwrite; every other field of <paramref name="state"/> (NotificationLevel, etc.) is
    /// applied as given. <see cref="Chats.ChatHub.GetConversations"/> (2026-08-04 follow-up spec §6)
    /// calls this instead of <see cref="Join"/>: its per-shell loop awaits a Mongo unread count between
    /// reading a membership's DB LastReadSeq and seeding it here, so a concurrent <c>MarkRead</c> that
    /// lands in that window (which advances the registry via <see cref="AdvanceLastReadSeq"/>) must never
    /// be regressed back down by the now-stale DB value. Absent entry ⇒ identical to <see cref="Join"/>
    /// (first seed, nothing to preserve). Single locked pass — read-then-write is atomic, no separate
    /// caller-side TryGetMember/Join TOCTOU window.
    /// </summary>
    public void JoinPreservingReadCursor(string channelId, string connectionId, MemberState state)
    {
        lock (_lock)
        {
            var merged = TryGetNoLock(channelId, connectionId, out var existing)
                ? state with { LastReadSeq = Math.Max(existing.LastReadSeq, state.LastReadSeq) }
                : state;
            JoinNoLock(channelId, connectionId, merged);
        }
    }

    /// <summary>Removes a single (channelId, connectionId) membership entry. No-op if absent.</summary>
    public void Leave(string channelId, string connectionId)
    {
        lock (_lock)
        {
            RemoveMembershipNoLock(channelId, connectionId);
        }
    }

    /// <summary>
    /// Updates the notification level for an existing (channelId, connectionId) entry only.
    /// No-op if the connection has no membership entry for that channel.
    /// </summary>
    public void SetNotificationLevel(string channelId, string connectionId, NotificationLevel level)
    {
        lock (_lock)
        {
            if (TryGetNoLock(channelId, connectionId, out var current))
            {
                _membersByChannel[channelId][connectionId] = current with { NotificationLevel = level };
            }
        }
    }

    /// <summary>
    /// Updates the last-read sequence for an existing (channelId, connectionId) entry only.
    /// No-op if the connection has no membership entry for that channel. PLAIN OVERWRITE — NOT
    /// monotonic. Kept exactly as-is (existing contract, existing <c>FanOutRegistryTests</c>
    /// coverage); callers that must never regress the cursor (e.g. <c>ChatHub.MarkRead</c>, Task 17)
    /// use <see cref="AdvanceLastReadSeq"/> instead.
    /// </summary>
    public void SetLastReadSeq(string channelId, string connectionId, long seq)
    {
        lock (_lock)
        {
            if (TryGetNoLock(channelId, connectionId, out var current))
            {
                _membersByChannel[channelId][connectionId] = current with { LastReadSeq = seq };
            }
        }
    }

    /// <summary>
    /// Monotonic (max) advance of the last-read sequence for an existing (channelId, connectionId)
    /// entry only — no-op if the connection has no membership entry for that channel. Same
    /// no-op-if-absent/lock discipline as <see cref="SetLastReadSeq"/>, but takes
    /// <c>Math.Max(current.LastReadSeq, seq)</c> instead of a plain overwrite, so a lower/stale/
    /// out-of-order seq never regresses the tracked cursor.
    /// <para>
    /// <c>ChatHub.MarkRead</c> (Task 17) calls THIS, not <see cref="SetLastReadSeq"/>: the durable
    /// Mongo counterpart (<see cref="Memberships.MembershipRepository.UpdateLastReadSeq"/>) is
    /// already a <c>$max</c>, so a stale MarkRead is a DB no-op. If the hub instead called the plain
    /// overwrite here, that same stale call would still regress the IN-MEMORY registry below the
    /// durable cursor — the two stores would diverge, and <see cref="ActivityCoalescer"/>'s emit-time
    /// unread recompute (which reads ONLY this registry) would over-count unread and wrongly
    /// re-suppress an already-caught-up member. This method keeps both stores monotonic together.
    /// </para>
    /// </summary>
    public void AdvanceLastReadSeq(string channelId, string connectionId, long seq)
    {
        lock (_lock)
        {
            if (TryGetNoLock(channelId, connectionId, out var current))
            {
                _membersByChannel[channelId][connectionId] = current with { LastReadSeq = Math.Max(current.LastReadSeq, seq) };
            }
        }
    }

    /// <summary>Snapshot of every online member's state for <paramref name="channelId"/>.</summary>
    public IReadOnlyCollection<MemberState> GetMembers(string channelId)
    {
        lock (_lock)
        {
            return _membersByChannel.TryGetValue(channelId, out var members)
                ? members.Values.ToList()
                : Array.Empty<MemberState>();
        }
    }

    /// <summary>
    /// Snapshot of every online member's <c>(connectionId, state)</c> pair for <paramref name="channelId"/>.
    /// Unlike <see cref="GetMembers"/> (which projects away the connectionId), this keeps the key so the
    /// fan-out engine (Task 13) can, per member, both consult <see cref="FocusRegistry"/> for the
    /// focused/unfocused split AND target the coalesced <c>ChannelActivity</c> at the right connection.
    /// </summary>
    public IReadOnlyCollection<(string ConnectionId, MemberState State)> GetMembersWithConnections(string channelId)
    {
        lock (_lock)
        {
            if (!_membersByChannel.TryGetValue(channelId, out var members))
            {
                return Array.Empty<(string, MemberState)>();
            }

            var result = new List<(string ConnectionId, MemberState State)>(members.Count);
            foreach (var kvp in members)
            {
                result.Add((kvp.Key, kvp.Value));
            }
            return result;
        }
    }

    /// <summary>
    /// The live <see cref="MemberState"/> for a single (channelId, connectionId), or false if that
    /// connection has no membership entry in the channel. The ActivityCoalescer (Task 13) reads this at
    /// EMIT time to recompute the member's current unread (offeredLastSeq − <see cref="MemberState.LastReadSeq"/>)
    /// for the &gt;100 suppression check — deliberately a fresh read, because a MarkRead between the
    /// coalescer's offer and its flush can change the suppression outcome.
    /// </summary>
    public bool TryGetMember(string channelId, string connectionId, out MemberState state)
    {
        lock (_lock)
        {
            return TryGetNoLock(channelId, connectionId, out state);
        }
    }

    /// <summary>
    /// O(1) membership test for <paramref name="connectionId"/> in <paramref name="channelId"/> — reads
    /// the <c>_channelsByConnection</c> reverse index only, so it never allocates or copies a channel's
    /// roster. Exists for hot paths (e.g. <c>ChatHub.SendMessage</c>) that must reject a non-member
    /// BEFORE doing any heavier work (rate limiting, a DB load): an O(members) <see cref="GetMembers"/>
    /// copy under the shared lock on every call is exactly the amplification a throttled caller looping
    /// the hot path would exploit.
    /// </summary>
    public bool IsMember(string connectionId, string channelId)
    {
        lock (_lock)
        {
            return _channelsByConnection.TryGetValue(connectionId, out var channels) && channels.Contains(channelId);
        }
    }

    /// <summary>
    /// Drops every membership entry for <paramref name="connectionId"/> across all channels (and the
    /// reverse-index footprint). Called on disconnect. No-op for a connection with no entries.
    /// </summary>
    public void RemoveConnection(string connectionId)
    {
        lock (_lock)
        {
            if (!_channelsByConnection.TryGetValue(connectionId, out var channels))
            {
                return;
            }

            // Copy first: RemoveMembershipNoLock mutates the very set we would be enumerating.
            foreach (var channelId in channels.ToList())
            {
                RemoveMembershipNoLock(channelId, connectionId);
            }
        }
    }

    private void JoinNoLock(string channelId, string connectionId, MemberState state)
    {
        if (!_membersByChannel.TryGetValue(channelId, out var members))
        {
            members = new Dictionary<string, MemberState>();
            _membersByChannel[channelId] = members;
        }
        members[connectionId] = state;

        if (!_channelsByConnection.TryGetValue(connectionId, out var channels))
        {
            channels = new HashSet<string>();
            _channelsByConnection[connectionId] = channels;
        }
        channels.Add(channelId);
    }

    private void RemoveMembershipNoLock(string channelId, string connectionId)
    {
        if (_membersByChannel.TryGetValue(channelId, out var members))
        {
            members.Remove(connectionId);
            if (members.Count == 0)
            {
                _membersByChannel.Remove(channelId);
            }
        }

        if (_channelsByConnection.TryGetValue(connectionId, out var channels))
        {
            channels.Remove(channelId);
            if (channels.Count == 0)
            {
                _channelsByConnection.Remove(connectionId);
            }
        }
    }

    private bool TryGetNoLock(string channelId, string connectionId, out MemberState state)
    {
        state = null;
        return _membersByChannel.TryGetValue(channelId, out var members)
            && members.TryGetValue(connectionId, out state);
    }
}
