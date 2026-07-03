using System;
using System.Collections.Generic;
using System.Linq;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.FanOut;

/// <summary>
/// Immutable per-(channel, connection) subscription record. Replaced wholesale (never mutated) by
/// <see cref="OnlineMemberRegistry.SetNotificationLevel"/> / <see cref="OnlineMemberRegistry.SetLastReadSeq"/>
/// via <c>with</c>-expressions, per the immutable-update convention used across the codebase.
/// </summary>
public sealed record MemberState(string BattleTag, NotificationLevel NotificationLevel, long LastReadSeq);

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
    /// No-op if the connection has no membership entry for that channel.
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
