using System;
using System.Linq;
using System.Threading.Tasks;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Protocol;

namespace W3ChampionsChatService.Chats;

/// <summary>
/// C3 (Task 9): the focused-set subscription. <see cref="FocusChannel"/>/<see cref="UnfocusChannel"/>
/// mutate <c>FocusRegistry</c> only — they decide who is in the "focused" set that later gates full
/// <c>MessageReceived</c> targeting versus coalesced <c>ChannelActivity</c> (acceptance 1), and
/// <see cref="FocusChannel"/>'s response carries the channel's live viewer roster (acceptance 4).
/// Neither method pushes a SignalR event: <c>ViewersChanged</c> batching via a <c>ViewersAccumulator</c>
/// is Task 14 — deliberately NOT built here (see FanOut/FocusRegistry.cs doc comment).
/// </summary>
public partial class ChatHub
{
    public async Task<FocusChannelResult> FocusChannel(string channelId)
    {
        // Fail-closed: an unregistered connection (never authenticated, or its session was displaced/
        // torn down) is denied outright — there is no identity to focus a channel under.
        if (!_sessionRegistry.TryGetByConnectionId(Context.ConnectionId, out var session))
        {
            return new FocusChannelResult(ChatResultCode.PermissionDenied);
        }

        var battleTag = session.Identity.BattleTag;

        // Hot path: membership is read from OnlineMemberRegistry (seeded at connect from the caller's
        // channel-backed memberships, zero DB) — case-insensitive, matching the casing convention used
        // throughout Sessions/FanOut (a live battleTag keeps its connect-time casing; DB-derived ones
        // are lowercased).
        var isMember = _onlineMemberRegistry.GetMembers(channelId)
            .Any(m => string.Equals(m.BattleTag, battleTag, StringComparison.OrdinalIgnoreCase));

        if (!isMember)
        {
            // Cold path, only reached for a non-member: a single Load distinguishes "no such channel"
            // (NotFound) from "channel exists, caller just isn't in it" (NotMember).
            var channel = await _channelRepository.Load(channelId);
            return channel == null
                ? new FocusChannelResult(ChatResultCode.NotFound)
                : new FocusChannelResult(ChatResultCode.NotMember);
        }

        // Focused-set cap (ChatLimits.MaxFocusedChannels): re-focusing a channel already in the
        // caller's focused set is idempotent and must NOT count as a new one against the cap. Only a
        // genuinely NEW distinct channel is subject to the cap.
        var focusedChannels = _focusRegistry.GetFocusedChannels(Context.ConnectionId);
        var alreadyFocused = focusedChannels.Contains(channelId);
        if (!alreadyFocused && focusedChannels.Count >= ChatLimits.MaxFocusedChannels)
        {
            return new FocusChannelResult(ChatResultCode.PermissionDenied);
        }

        _focusRegistry.Focus(Context.ConnectionId, channelId, battleTag);

        // Roster = the channel's ACTIVE viewers (online AND focused) from FocusRegistry — NEVER from
        // membership. Each distinct battleTag is resolved to a display name via the live session; a
        // roster entry with no live session (e.g. a teardown race) falls back to the battleTag itself
        // rather than dropping the entry or throwing.
        var viewers = _focusRegistry.GetRoster(channelId)
            .Select(rosterBattleTag => new ChannelViewerDto(rosterBattleTag, ResolveViewerName(rosterBattleTag)))
            .ToList();

        return new FocusChannelResult(ChatResultCode.Ok, viewers);
    }

    public Task<ChannelOperationResult> UnfocusChannel(string channelId)
    {
        // Idempotent, unconditional: FocusRegistry.Unfocus is already a no-op for an unknown
        // (connection, channel) pair, so unfocusing a channel the caller never focused (or an
        // unregistered connection) still returns Ok — there is nothing to reject.
        _focusRegistry.Unfocus(Context.ConnectionId, channelId);
        return Task.FromResult(new ChannelOperationResult(ChatResultCode.Ok));
    }

    private string ResolveViewerName(string battleTag)
    {
        var session = _sessionRegistry.GetByBattleTag(battleTag);
        return session?.Identity?.Name ?? battleTag;
    }
}
