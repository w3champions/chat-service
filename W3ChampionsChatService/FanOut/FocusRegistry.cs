using System;
using System.Collections.Generic;
using System.Linq;

namespace W3ChampionsChatService.FanOut;

/// <summary>
/// The focused-channel index: which online connections currently have which channels open in the
/// foreground (as opposed to merely being a member). Drives the "focused → full MessageReceived,
/// unfocused → coalesced ChannelActivity" fan-out split (C3 acceptance 1) and the viewer roster
/// returned from FocusChannel (acceptance 4).
///
/// Pure in-memory state: NO MongoDB, NO SignalR/IHubContext, NO ISessionRegistry dependency — the
/// battleTag for a connection is resolved by the CALLER at focus time and stored alongside, so the
/// roster query needs no external lookup. Concurrency idiom mirrors
/// <see cref="W3ChampionsChatService.Sessions.SessionRegistry"/> (SessionRegistry.cs:29-114): a
/// single lock, with reverse indexes maintained in lockstep and ALL work done inside the lock.
///
/// The focused-set cap (10 channels/connection) is NOT enforced here — the hub enforces it before
/// calling <see cref="Focus"/> (Task 9); this registry only stores whatever it is told.
/// </summary>
public class FocusRegistry
{
    // connectionId -> the set of channelIds it currently has focused.
    private readonly Dictionary<string, HashSet<string>> _focusedChannelsByConnection =
        new Dictionary<string, HashSet<string>>();

    // Reverse index: channelId -> the set of connectionIds currently focused on it.
    private readonly Dictionary<string, HashSet<string>> _connectionsByChannel =
        new Dictionary<string, HashSet<string>>();

    // connectionId -> battleTag, as supplied by the caller at focus time. A connection's battleTag
    // is fixed for its lifetime (one session per connection — see SessionRegistry), so this is a
    // single flat map rather than being duplicated per (connection, channel) pair.
    private readonly Dictionary<string, string> _battleTagByConnection =
        new Dictionary<string, string>();

    private readonly object _lock = new object();

    /// <summary>
    /// Marks <paramref name="connectionId"/> as focused on <paramref name="channelId"/>, recording
    /// its <paramref name="battleTag"/> for roster queries. Idempotent: re-focusing a channel the
    /// connection already has focused is a no-op beyond refreshing the recorded battleTag.
    /// </summary>
    public void Focus(string connectionId, string channelId, string battleTag)
    {
        lock (_lock)
        {
            _battleTagByConnection[connectionId] = battleTag;

            if (!_focusedChannelsByConnection.TryGetValue(connectionId, out var channels))
            {
                channels = new HashSet<string>();
                _focusedChannelsByConnection[connectionId] = channels;
            }
            channels.Add(channelId);

            if (!_connectionsByChannel.TryGetValue(channelId, out var connections))
            {
                connections = new HashSet<string>();
                _connectionsByChannel[channelId] = connections;
            }
            connections.Add(connectionId);
        }
    }

    /// <summary>Removes the (connectionId, channelId) focus entry. No-op if it did not exist.</summary>
    public void Unfocus(string connectionId, string channelId)
    {
        lock (_lock)
        {
            RemoveFocusEntryNoLock(connectionId, channelId);
        }
    }

    /// <summary>
    /// Snapshot of the channelIds <paramref name="connectionId"/> currently has focused. The hub
    /// (Task 9) reads this BEFORE calling <see cref="Focus"/> to enforce the focused-set cap
    /// (<see cref="Domain.ChatLimits.MaxFocusedChannels"/>) and to detect an idempotent re-focus (a
    /// channel already in this set never counts as a NEW one against the cap).
    /// </summary>
    public IReadOnlyCollection<string> GetFocusedChannels(string connectionId)
    {
        lock (_lock)
        {
            return _focusedChannelsByConnection.TryGetValue(connectionId, out var channels)
                ? channels.ToList()
                : Array.Empty<string>();
        }
    }

    /// <summary>Snapshot of the connectionIds currently focused on <paramref name="channelId"/>.</summary>
    public IReadOnlyCollection<string> GetFocusedConnections(string channelId)
    {
        lock (_lock)
        {
            return _connectionsByChannel.TryGetValue(channelId, out var connections)
                ? connections.ToList()
                : Array.Empty<string>();
        }
    }

    /// <summary>
    /// The DISTINCT battleTags of the connections currently focused on <paramref name="channelId"/>.
    /// Multiple connections sharing a battleTag (e.g. two tabs) collapse to a single roster entry.
    /// </summary>
    public IReadOnlyCollection<string> GetRoster(string channelId)
    {
        lock (_lock)
        {
            if (!_connectionsByChannel.TryGetValue(channelId, out var connections))
            {
                return Array.Empty<string>();
            }

            // Case-insensitive, matching SessionRegistry: live battleTags keep their original casing
            // while stored/DB ones are lowercased, so an ordinal compare could split one player into
            // two roster entries.
            var roster = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var connectionId in connections)
            {
                if (_battleTagByConnection.TryGetValue(connectionId, out var battleTag))
                {
                    roster.Add(battleTag);
                }
            }
            return roster.ToList();
        }
    }

    /// <summary>
    /// Clears every focus entry (and the reverse-index footprint) for <paramref name="connectionId"/>.
    /// Called on disconnect. No-op for a connection with no focus state.
    /// </summary>
    public void RemoveConnection(string connectionId)
    {
        lock (_lock)
        {
            if (!_focusedChannelsByConnection.TryGetValue(connectionId, out var channels))
            {
                return;
            }

            // Copy first: RemoveFocusEntryNoLock mutates the very set we would be enumerating.
            foreach (var channelId in channels.ToList())
            {
                RemoveFocusEntryNoLock(connectionId, channelId);
            }
        }
    }

    private void RemoveFocusEntryNoLock(string connectionId, string channelId)
    {
        if (_focusedChannelsByConnection.TryGetValue(connectionId, out var channels))
        {
            channels.Remove(channelId);
            if (channels.Count == 0)
            {
                _focusedChannelsByConnection.Remove(connectionId);
                _battleTagByConnection.Remove(connectionId);
            }
        }

        if (_connectionsByChannel.TryGetValue(channelId, out var connections))
        {
            connections.Remove(connectionId);
            if (connections.Count == 0)
            {
                _connectionsByChannel.Remove(channelId);
            }
        }
    }
}
