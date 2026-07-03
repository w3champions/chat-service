using System;
using System.Linq;
using System.Threading.Tasks;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.FanOut;
using W3ChampionsChatService.Memberships;
using W3ChampionsChatService.Protocol;

namespace W3ChampionsChatService.Chats;

/// <summary>
/// C3 (Task 9): the focused-set subscription. <see cref="FocusChannel"/>/<see cref="UnfocusChannel"/>
/// mutate <c>FocusRegistry</c> only — they decide who is in the "focused" set that later gates full
/// <c>MessageReceived</c> targeting versus coalesced <c>ChannelActivity</c> (acceptance 1), and
/// <see cref="FocusChannel"/>'s response carries the channel's live viewer roster (acceptance 4).
/// Neither method pushes a SignalR event: <c>ViewersChanged</c> batching via a <c>ViewersAccumulator</c>
/// is Task 14 — deliberately NOT built here (see FanOut/FocusRegistry.cs doc comment).
///
/// C3 (Task 10): <see cref="JoinChannel"/>/<see cref="LeaveChannel"/>/<see cref="SetNotificationLevel"/>
/// — membership self-service, including implicit semiPublic creation. See <see cref="JoinChannel"/>'s
/// doc comment for the full resolution order (acceptance 9/10).
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

    /// <summary>
    /// Membership self-service join, including implicit semiPublic creation (acceptance 9/10). The
    /// resolution order is load-bearing — each step is honored EXACTLY, in sequence:
    /// <list type="number">
    /// <item>Fail-closed: an unregistered connection (never authenticated, or displaced/torn down) is
    /// denied outright — there is no identity to join a channel under.</item>
    /// <item><see cref="Channels.ChannelRepository.LoadAnyByNormalizedName"/> resolves ANY channel
    /// with that normalized name, across every <see cref="ChannelType"/>:
    /// <list type="bullet">
    /// <item><b>Public</b> — the full-ban gate: a full-banned caller (<see cref="ConnectionMapping.GetEffectiveMuteStatus"/>
    /// == <see cref="MuteStatus.Full"/>) is denied (carries the legacy <c>LoginAsAuthenticated</c>
    /// room-scope semantics — a full ban hides/blocks public rooms). SemiPublic is deliberately EXEMPT
    /// from this gate.</item>
    /// <item><b>SemiPublic</b> — proceeds straight to the membership steps below, no gate.</item>
    /// <item><b>System / Dm / GroupDm</b> (any ACL-governed, non-name-joinable type) — denied. A name
    /// collision with an ACL channel (e.g. a live match's System channel) must NEVER fall through to
    /// implicit creation.</item>
    /// <item><b>No match</b> — falls through to the cap check and (if not capped) implicit creation
    /// below. The channel is NOT created yet at this point.</item>
    /// </list>
    /// </item>
    /// <item>Idempotent already-member short-circuit (found-channel path only, since the not-found path
    /// can never have an existing membership): an existing membership returns
    /// <see cref="ChatResultCode.Ok"/> with the existing channel + membership, and does NOT count
    /// against the cap below — a member already at the cap can still re-join.</item>
    /// <item>Membership cap (<see cref="ChatLimits.MaxPublicMembershipsPerUser"/>, counting only
    /// name-joinable Public+SemiPublic memberships via
    /// <see cref="Memberships.MembershipRepository.CountNameJoinableMembershipsForUser"/>): checked
    /// BEFORE the creation throttle and BEFORE any channel is created, for a genuinely NEW membership
    /// on EITHER path (an existing channel the caller isn't in yet, or a not-found name). This ordering
    /// is deliberate: a capped user joining a brand-new name must get a deterministic
    /// <see cref="ChatResultCode.PermissionDenied"/> with no orphan SemiPublic channel persisted and no
    /// creation-throttle token consumed.</item>
    /// <item>Not-found path only: implicit semiPublic creation, now that the cap has cleared. The
    /// creation throttle (<see cref="FanOut.ChannelCreationRateLimiter"/>, keyed by battleTag) gates the
    /// ACTUAL create; over the limit returns <see cref="ChatResultCode.Throttled"/> with the window's
    /// remaining seconds. Only genuine creations are metered — joining an existing channel never
    /// touches the throttle.</item>
    /// <item>Create the membership with <see cref="NotificationLevel.Mentions"/> — an EXPLICIT override
    /// of <see cref="ChannelMembership.NotificationLevel"/>'s model default (<c>All</c>) — insert it,
    /// seed <see cref="FanOut.OnlineMemberRegistry"/> for this connection, and return Ok.</item>
    /// </list>
    /// </summary>
    public async Task<JoinChannelResult> JoinChannel(string name)
    {
        if (!_sessionRegistry.TryGetByConnectionId(Context.ConnectionId, out var session))
        {
            return new JoinChannelResult(ChatResultCode.PermissionDenied);
        }

        var battleTag = session.Identity.BattleTag;
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var normalizedName = ChannelNames.Normalize(name);

        var channel = await _channelRepository.LoadAnyByNormalizedName(normalizedName);

        if (channel != null)
        {
            if (channel.Type == ChannelType.Public)
            {
                // Full-ban gate: carries LoginAsAuthenticated's room-scope semantics — a full-banned
                // user cannot join public channels. SemiPublic is exempt (falls through below).
                if (_connections.GetEffectiveMuteStatus(Context.ConnectionId, now) == MuteStatus.Full)
                {
                    return new JoinChannelResult(ChatResultCode.PermissionDenied);
                }
            }
            else if (channel.Type != ChannelType.SemiPublic)
            {
                // System / Dm / GroupDm — ACL-governed, not joinable by name. NOT an implicit-create
                // case: a name collision with an existing ACL channel must be rejected outright.
                return new JoinChannelResult(ChatResultCode.PermissionDenied);
            }

            var existingMembership = await _membershipRepository.Load(channel.Id, battleTag);
            if (existingMembership != null)
            {
                // Idempotent: already a member. Does not count against the cap below — a member at cap
                // can still re-join.
                return new JoinChannelResult(ChatResultCode.Ok, Channel: channel, Membership: existingMembership);
            }
        }

        // Membership cap: checked BEFORE the creation throttle / actual channel creation, for a
        // genuinely new membership on EITHER path (existing channel not yet joined, or not-found name).
        // A capped user joining a brand-new name must get a deterministic PermissionDenied with no
        // orphan channel created and no throttle token spent.
        var membershipCount = await _membershipRepository.CountNameJoinableMembershipsForUser(battleTag);
        if (membershipCount >= ChatLimits.MaxPublicMembershipsPerUser)
        {
            return new JoinChannelResult(ChatResultCode.PermissionDenied);
        }

        if (channel == null)
        {
            // Implicit semiPublic creation — throttle ACTUAL creations only (per battleTag).
            var decision = _channelCreationRateLimiter.TryAcquire(battleTag, now);
            if (!decision.Allowed)
            {
                return new JoinChannelResult(ChatResultCode.Throttled, decision.RetryAfterSeconds);
            }

            channel = await _channelRepository.FindOrCreateSemiPublic(name, now);
        }

        var membership = new ChannelMembership
        {
            ChannelId = channel.Id,
            BattleTag = battleTag,
            // Override the model default (All) — a freshly joined channel starts at Mentions.
            NotificationLevel = NotificationLevel.Mentions,
            JoinedAt = now,
        };
        await _membershipRepository.Insert(membership);

        _onlineMemberRegistry.Join(channel.Id, Context.ConnectionId,
            new MemberState(battleTag, NotificationLevel.Mentions, membership.LastReadSeq));

        return new JoinChannelResult(ChatResultCode.Ok, Channel: channel, Membership: membership);
    }

    /// <summary>
    /// Leaves a channel: deletes the membership row, drops the caller's <see cref="FanOut.OnlineMemberRegistry"/>
    /// entry, and unfocuses it in <see cref="FanOut.FocusRegistry"/>. Idempotent and unconditional on
    /// membership state — leaving a channel the caller was never a member of (or re-leaving one already
    /// left) is still <see cref="ChatResultCode.Ok"/>, mirroring <see cref="UnfocusChannel"/>'s
    /// no-op-if-absent contract. Fail-closed on identity, same as <see cref="JoinChannel"/> and
    /// <see cref="FocusChannel"/>: an unregistered connection has no battleTag to delete a membership
    /// under.
    /// </summary>
    public async Task<ChannelOperationResult> LeaveChannel(string channelId)
    {
        if (!_sessionRegistry.TryGetByConnectionId(Context.ConnectionId, out var session))
        {
            return new ChannelOperationResult(ChatResultCode.PermissionDenied);
        }

        var battleTag = session.Identity.BattleTag;

        await _membershipRepository.Delete(channelId, battleTag);
        _onlineMemberRegistry.Leave(channelId, Context.ConnectionId);
        _focusRegistry.Unfocus(Context.ConnectionId, channelId);

        return new ChannelOperationResult(ChatResultCode.Ok);
    }

    /// <summary>
    /// Updates the caller's per-channel notification level. Non-member → <see cref="ChatResultCode.NotMember"/>.
    /// Public channels support only <see cref="NotificationLevel.Mentions"/>/<see cref="NotificationLevel.None"/>
    /// — a request for <see cref="NotificationLevel.All"/> on a Public channel is REJECTED with
    /// <see cref="ChatResultCode.PermissionDenied"/> (acceptance 3: no silent coercion/clamping).
    /// SemiPublic (and every other type) supports all three levels. On success, persists via
    /// <see cref="Memberships.MembershipRepository.SetNotificationLevel"/> and mirrors the change into
    /// <see cref="FanOut.OnlineMemberRegistry"/> so the hot fan-out path sees it immediately.
    /// </summary>
    public async Task<ChannelOperationResult> SetNotificationLevel(string channelId, NotificationLevel level)
    {
        if (!_sessionRegistry.TryGetByConnectionId(Context.ConnectionId, out var session))
        {
            return new ChannelOperationResult(ChatResultCode.PermissionDenied);
        }

        var battleTag = session.Identity.BattleTag;

        var membership = await _membershipRepository.Load(channelId, battleTag);
        if (membership == null)
        {
            return new ChannelOperationResult(ChatResultCode.NotMember);
        }

        if (level == NotificationLevel.All)
        {
            var channel = await _channelRepository.Load(channelId);
            if (channel != null && channel.Type == ChannelType.Public)
            {
                return new ChannelOperationResult(ChatResultCode.PermissionDenied);
            }
        }

        await _membershipRepository.SetNotificationLevel(channelId, battleTag, level);
        _onlineMemberRegistry.SetNotificationLevel(channelId, Context.ConnectionId, level);

        return new ChannelOperationResult(ChatResultCode.Ok);
    }
}
