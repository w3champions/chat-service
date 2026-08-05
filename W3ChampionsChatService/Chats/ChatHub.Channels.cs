using System;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
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

        // Hot path: membership via OnlineMemberRegistry.IsMember (seeded at connect from the caller's
        // channel-backed memberships, zero DB, O(1) reverse-index lookup — no roster copy under the
        // lock, mirrors SendMessage/GetMessages/MarkRead). MaxConnectionsPerBattleTag == 1, so "this
        // connection is a member" is equivalent to "this battleTag is a member".
        var isMember = _onlineMemberRegistry.IsMember(Context.ConnectionId, channelId);

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

        // C5 (Task 5, D11): Dm/GroupDm never enter the viewer-roster/ViewersAccumulator system — spec §9
        // scopes viewer rosters to CHANNELS, and DM/group presence is member-presence via the C6 interest
        // index instead. Zero-DB type lookup: IsMember above already proved this (channelId, connectionId)
        // entry exists, so IsPrivateLaneChannel resolves it without a second Mongo round-trip.
        var isPrivateLane = IsPrivateLaneChannel(channelId, Context.ConnectionId);

        // C3 (Task 14): route the viewer-roster change into the batched ViewersChanged accumulator
        // BEFORE the FocusRegistry mutation, so its captured pre-window baseline reflects this battleTag
        // as it was BEFORE this focus (i.e. NOT-yet-viewing on a genuine first focus). Emits nothing
        // itself — the flush hosted service (Task 15) drains the accumulated batch ≤ every 5s. Skipped
        // entirely for a private-lane channel (D11) — it must never carry a roster delta.
        if (!isPrivateLane)
        {
            _viewersAccumulator.RecordChange(channelId, battleTag, _timeProvider.GetUtcNow().UtcDateTime);
        }

        _focusRegistry.Focus(Context.ConnectionId, channelId, battleTag);

        if (isPrivateLane)
        {
            // C6 (Task 9, D11): DERIVE presence interest from this focus — the SOLE way a connection ever
            // gains it (there is no subscribe API). Register interest in every current member EXCEPT the
            // caller's own tag. LoadForChannel is legitimate for a private lane (Dm = 2 members, GroupDm is
            // ACL-bound/capped; the never-enumerate guardrail is Public-channel-only). This branch is reached
            // for Dm/GroupDm ONLY — Public/SemiPublic/System focuses register NOTHING, structurally.
            //
            // TOCTOU CLOSED (C6 Task 9 review fix): the Mongo roster read + registry commit is not a single
            // atomic step, so a concurrent membership mutation (a co-member leaving / being kicked) could
            // land in the await-continuation gap. Before this fix, if that departure's OnMemberRemoved fired
            // while this connection was not yet recorded as a watcher, it was a no-op for this connection and
            // the stale snapshot's RegisterFocus then re-added the just-departed member — a grant no later leg
            // revoked until the next re-focus/unfocus/disconnect (i.e. potentially for the whole connection
            // lifetime). RegisterPresenceInterestWithVersionGuard now closes that window with an optimistic-
            // concurrency check: it reads the registry's per-channel membership version BEFORE the roster read
            // and commits ONLY if the version is unchanged at commit time (re-reading on a detected race), so a
            // version-matched commit is provably consistent with the registry's own view rather than a stale
            // snapshot. The sole exception is a bounded-retry fallback that registers best-effort ONLY under
            // pathological sustained mutation of this exact channel (see RegisterPresenceInterestWithVersionGuard)
            // — a far smaller, explicitly-accepted residual risk than blocking the focus indefinitely.
            await RegisterPresenceInterestWithVersionGuard(channelId, battleTag);

            // D11: FocusRegistry participation above still happens (focused delivery + the C6 interest
            // derivation depend on it) but the client gets an EMPTY roster, never the channel's active-
            // viewer set — a Dm/GroupDm never streams "who is viewing" (§1 non-goal, §9 channel-scoped
            // rosters, and the decline-invisibility "presence" guardrail).
            return new FocusChannelResult(ChatResultCode.Ok, Array.Empty<ChannelViewerDto>());
        }

        // Roster = the channel's ACTIVE viewers (online AND focused) from FocusRegistry — NEVER from
        // membership. Each distinct battleTag is resolved to a display name via the live session; a
        // roster entry with no live session (e.g. a teardown race) falls back to the battleTag itself
        // rather than dropping the entry or throwing.
        var viewers = _focusRegistry.GetRoster(channelId)
            .Select(rosterBattleTag => new ChannelViewerDto(rosterBattleTag, ResolveViewerName(rosterBattleTag)))
            .ToList();

        return new FocusChannelResult(ChatResultCode.Ok, viewers);
    }

    // Bound for the version-guarded presence-interest registration below. Membership mutations are rare
    // relative to focus calls, so a race is resolved on the very next read in practice; this cap is purely
    // defensive so FocusChannel can never spin indefinitely under pathological sustained mutation of the
    // exact same channel. On exhaustion it registers the freshest snapshot best-effort — a slightly-stale
    // presence-bool grant is a far smaller residual risk than blocking the focus.
    private const int MaxPresenceRegisterAttempts = 8;

    /// <summary>
    /// C6 (Task 9 review fix): registers this connection's DERIVED presence interest for a just-focused
    /// private-lane channel WITHOUT the original read-then-commit TOCTOU. Each attempt reads the registry's
    /// per-channel membership version, snapshots the roster
    /// (<see cref="Memberships.MembershipRepository.LoadForChannel"/>), then commits interest ONLY if the
    /// version is still unchanged (<see cref="FanOut.PresenceInterestRegistry.TryRegisterFocusIfVersionMatches"/>)
    /// — the version check and the commit share the registry's lock, so no mutation can slip a stale tag past
    /// the guard. A concurrent membership change (leave / kick / channel deletion) bumps the version, rejecting
    /// the stale snapshot and forcing a bounded re-read. The fast path (no concurrent mutation, the
    /// overwhelmingly common case) costs exactly one extra version read before the Mongo round-trip and one
    /// (lock-shared with the commit) after — negligible over the read itself.
    /// </summary>
    private async Task RegisterPresenceInterestWithVersionGuard(string channelId, string ownBattleTag)
    {
        for (var attempt = 1; ; attempt++)
        {
            var version = _presenceInterestRegistry.GetChannelVersion(channelId);
            var members = await _membershipRepository.LoadForChannel(channelId);
            var memberTags = members.Select(m => m.BattleTag).ToList();

            if (_presenceInterestRegistry.TryRegisterFocusIfVersionMatches(
                    Context.ConnectionId, channelId, ownBattleTag, memberTags, version))
            {
                return;
            }

            if (attempt >= MaxPresenceRegisterAttempts)
            {
                // Bounded-retry fallback (extremely unlikely — sustained mutation of this exact channel):
                // commit the freshest snapshot best-effort. The authoritative REPLACE semantics of any later
                // re-focus self-correct it; blocking the focus indefinitely would be the worse outcome.
                Log.Warning(
                    "Presence-interest registration for connection {ConnectionId} on channel {ChannelId} did not converge within {Attempts} attempts under concurrent membership mutation — registering freshest snapshot best-effort",
                    Context.ConnectionId, channelId, MaxPresenceRegisterAttempts);
                _presenceInterestRegistry.RegisterFocus(Context.ConnectionId, channelId, ownBattleTag, memberTags);
                return;
            }
        }
    }

    public Task<ChannelOperationResult> UnfocusChannel(string channelId)
    {
        // C3 (Task 14): route the viewer-roster change into the batched ViewersChanged accumulator
        // BEFORE the FocusRegistry mutation, so its captured pre-window baseline reflects this battleTag
        // as it was BEFORE this unfocus (i.e. VIEWING while still focused). The battleTag comes from
        // FocusRegistry's own per-connection record (not the session) so this stays as unconditional
        // and identity-independent as the Unfocus below — a connection with no focus state records
        // nothing. Emits nothing itself; the flush service drains the batch ≤ every 5s.
        // C5 (Task 5, D11): skipped for Dm/GroupDm — the same zero-DB IsPrivateLaneChannel lookup used by
        // FocusChannel and the disconnect teardown loop. A connection with no OnlineMemberRegistry entry
        // (e.g. a stale/torn-down membership) is NOT treated as private-lane — this mirrors the pre-T5
        // unconditional-record behavior for that edge case.
        if (_focusRegistry.TryGetBattleTag(Context.ConnectionId, out var battleTag)
            && !IsPrivateLaneChannel(channelId, Context.ConnectionId))
        {
            _viewersAccumulator.RecordChange(channelId, battleTag, _timeProvider.GetUtcNow().UtcDateTime);
        }

        // Idempotent, unconditional: FocusRegistry.Unfocus is already a no-op for an unknown
        // (connection, channel) pair, so unfocusing a channel the caller never focused (or an
        // unregistered connection) still returns Ok — there is nothing to reject.
        _focusRegistry.Unfocus(Context.ConnectionId, channelId);

        // C6 (Task 9, D11): revoke this connection's presence interest derived from the channel.
        // UNCONDITIONAL and no-op-safe — the registry self-noops for a (connection, channel) it never
        // registered (e.g. a Public-channel unfocus), so no "is this even private?" branch is needed.
        // Refcount-by-channel: a watched tag ALSO reachable via another focused channel survives.
        _presenceInterestRegistry.RevokeFocus(Context.ConnectionId, channelId);

        return Task.FromResult(new ChannelOperationResult(ChatResultCode.Ok));
    }

    private string ResolveViewerName(string battleTag)
    {
        var session = _sessionRegistry.GetByBattleTag(battleTag);
        return session?.Identity?.Name ?? battleTag;
    }

    /// <summary>
    /// C5 (Task 5, D11): zero-DB channel-type lookup via <see cref="OnlineMemberRegistry.TryGetMember"/> —
    /// true iff a (channel, connection) entry exists AND its <see cref="MemberState.ChannelType"/> is
    /// <see cref="ChannelType.Dm"/>/<see cref="ChannelType.GroupDm"/>, the two types excluded from the
    /// viewer-roster/<see cref="FanOut.ViewersAccumulator"/> system (spec §9 + the decline-invisibility
    /// "presence" guardrail). A MISSING entry is NOT private-lane — mirrors the legacy behavior where an
    /// absent registry entry never suppressed the accumulator record. Shared by <see cref="UnfocusChannel"/>
    /// and the disconnect teardown loop (<c>ChatHub.cs</c>); <see cref="FocusChannel"/> inlines the same
    /// lookup since it already holds the <c>TryGetMember</c> result from its membership check above.
    /// </summary>
    private bool IsPrivateLaneChannel(string channelId, string connectionId) =>
        _onlineMemberRegistry.TryGetMember(channelId, connectionId, out var member)
        && member.ChannelType is ChannelType.Dm or ChannelType.GroupDm;

    /// <summary>
    /// Membership self-service join, including implicit semiPublic creation (acceptance 9/10). The
    /// resolution order is load-bearing — each step is honored EXACTLY, in sequence:
    /// <list type="number">
    /// <item>Fail-closed: an unregistered connection (never authenticated, or displaced/torn down) is
    /// denied outright — there is no identity to join a channel under.</item>
    /// <item>Fix round 1 (finding F2b): a null/whitespace-only <paramref name="name"/> is rejected here,
    /// BEFORE any DB read — <see cref="ChatResultCode.PermissionDenied"/>, zero channel-collection reads.
    /// See the guard's own inline comment for the mechanism this pre-empts: a proven, reachable-today
    /// path into the ACL-type denial branch below.</item>
    /// <item><see cref="Channels.ChannelRepository.LoadAnyByNormalizedName"/> resolves ANY channel
    /// with that normalized name, across every <see cref="ChannelType"/>:
    /// <list type="bullet">
    /// <item><b>Public</b> — the full-ban gate: a full-banned caller (<see cref="ConnectionMapping.GetEffectiveMuteStatus"/>
    /// == <see cref="MuteStatus.Full"/>) is denied — the full-ban room-scope rule blocks joining public
    /// rooms (mirrored on connect by <see cref="Protocol.SessionStateAssembler"/>, which hides the public
    /// catalog for a full-banned user). SemiPublic is deliberately EXEMPT from this gate.</item>
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
    /// seed <see cref="FanOut.OnlineMemberRegistry"/> for this connection, and return Ok. PR36 follow-up
    /// (D2): the Mentions default is used ONLY when the caller has no persisted preference for this
    /// channel (<see cref="Memberships.NotificationPreferenceRepository.Load"/>); otherwise the (re)join
    /// seeds from that persisted level instead — see the seeding code just above the insert.</item>
    /// </list>
    /// </summary>
    public async Task<JoinChannelResult> JoinChannel(string name)
    {
        if (!_sessionRegistry.TryGetByConnectionId(Context.ConnectionId, out var session))
        {
            return new JoinChannelResult(ChatResultCode.PermissionDenied);
        }

        // Fix round 1 (finding F2b): reject a null/whitespace-only name BEFORE any DB read. Proven:
        // without this guard, ChannelNames.Normalize(null) -> null, and LoadAnyByNormalizedName(null)
        // renders the Mongo filter {NormalizedName: null} — because ChatChannel.NormalizedName is
        // [BsonIgnoreIfNull], that filter matches every document where the field is ABSENT, i.e. EVERY
        // System/Dm/GroupDm document (none of their creation paths — FindOrCreateSystem, FindOrCreateDm,
        // the GroupDm creation path — ever populate NormalizedName). So JoinChannel(null) matched the
        // first such document and landed in the ACL-type denial branch below on EVERY call — a LIVE
        // guard reached via a guaranteed COLLSCAN (the partial index ux_type_normalizedName can't serve
        // a null match), not dead code. This guard pre-empts that path entirely, returning the SAME
        // PermissionDenied the branch below would have yielded, with zero channel-collection reads.
        if (string.IsNullOrWhiteSpace(name))
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
                // Full-ban gate: the full-ban room-scope rule blocks a full-banned user from joining
                // public channels. SemiPublic is exempt (falls through below).
                if (_connections.GetEffectiveMuteStatus(Context.ConnectionId, now) == MuteStatus.Full)
                {
                    return new JoinChannelResult(ChatResultCode.PermissionDenied);
                }
            }
            else if (channel.Type != ChannelType.SemiPublic)
            {
                // System / Dm / GroupDm — ACL-governed, not joinable by name. NOT an implicit-create
                // case: a name collision with an existing ACL channel must be rejected outright.
                //
                // Match-channel-hygiene brief (2026-08-05), Part 3 — CORRECTED, fix round 1 (finding F2):
                // the original comment here claimed this branch was structurally unreachable. That claim
                // was WRONG. Proven: ChannelNames.Normalize(null) -> null, and
                // LoadAnyByNormalizedName(null) renders the Mongo filter {NormalizedName: null} — because
                // ChatChannel.NormalizedName is [BsonIgnoreIfNull], that filter matches every document
                // where the field is ABSENT, which is EVERY System/Dm/GroupDm document (none of their
                // creation paths — FindOrCreateSystem, FindOrCreateDm, the GroupDm creation path — ever
                // populate NormalizedName). So JoinChannel(null) matched the first such document and
                // landed HERE on every call — a LIVE ACL guard, reached via a guaranteed COLLSCAN (the
                // partial index ux_type_normalizedName can't serve a null match), not dead code.
                //
                // Fix round 1 (finding F2b) added an explicit null/whitespace-name guard at the top of
                // JoinChannel that now pre-empts this exact path before any DB read — so for a
                // null/whitespace name this branch is unreachable again TODAY. It stays as
                // defense-in-depth for the ORIGINAL concern this comment used to (wrongly) dismiss: a
                // real, non-blank name that collides with a System/Dm/GroupDm channel the moment any
                // future write path ever populates NormalizedName on one of those types — and as a
                // backstop if the F2b guard above is ever weakened or removed without re-reading this
                // history.
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

        // PR36 follow-up (D2): a (re)join seeds from the caller's own PERSISTED preference for this
        // channel if one exists (the last level they EXPLICITLY set via SetNotificationLevel, surviving
        // a prior leave's hard membership delete) — otherwise falls back to the fresh-join Mentions
        // default. This is what lets "join → set None → leave → rejoin" keep the room silenced.
        var pref = await _notificationPreferenceRepository.Load(battleTag, channel.Id);
        var seedLevel = pref?.NotificationLevel ?? NotificationLevel.Mentions;

        var membership = new ChannelMembership
        {
            ChannelId = channel.Id,
            BattleTag = battleTag,
            // Override the model default (All) — a freshly joined channel starts at Mentions, unless a
            // persisted preference says otherwise (see above).
            NotificationLevel = seedLevel,
            JoinedAt = now,
        };
        await _membershipRepository.Insert(membership);

        _onlineMemberRegistry.Join(channel.Id, Context.ConnectionId,
            new MemberState(battleTag, seedLevel, membership.LastReadSeq, channel.Type));

        return new JoinChannelResult(ChatResultCode.Ok, Channel: channel, Membership: membership);
    }

    /// <summary>
    /// Leaves a channel: deletes the caller's membership row, drops their <see cref="FanOut.OnlineMemberRegistry"/>
    /// entry, and unfocuses it in <see cref="FanOut.FocusRegistry"/>. Idempotent and unconditional on
    /// membership state — leaving a channel the caller was never a member of (or re-leaving one already left)
    /// is still <see cref="ChatResultCode.Ok"/>, mirroring <see cref="UnfocusChannel"/>'s no-op-if-absent
    /// contract; a missing/vanished channel or a non-membership NEVER becomes a NotFound/NotMember (the
    /// pre-C5 contract). Fail-closed on identity: an unregistered connection has no battleTag to delete a
    /// membership under.
    /// <para>
    /// C5 (Task 8): the channel is loaded BEFORE mutating so the departure branches by <see cref="ChannelType"/>:
    /// </para>
    /// <list type="bullet">
    /// <item><b>Public / SemiPublic / System (and a null/vanished channel)</b>: BYTE-IDENTICAL to the pre-C5
    /// contract — route the roster change through the batched <c>ViewersChanged</c> accumulator
    /// (<see cref="FanOut.ViewersAccumulator.RecordChange"/>) BEFORE the
    /// <see cref="FanOut.FocusRegistry.Unfocus"/> so a focused viewer who explicitly leaves while staying
    /// connected still emits a <c>left</c> delta (no phantom viewer); recording AFTER the unfocus would invert
    /// the delta. A null channel is treated as non-private-lane, exactly as the legacy unconditional
    /// RecordChange did.</item>
    /// <item><b>Dm / GroupDm (private lanes)</b>: SKIP <see cref="FanOut.ViewersAccumulator.RecordChange"/>
    /// (D11 — private lanes are excluded from the viewer-roster system entirely; a voluntary departure must
    /// never surface a roster delta, structurally satisfying the C3 amendment for Dm/GroupDm — OQ-1). The Dm
    /// conversation SHELL (channel doc) is left UNTOUCHED so pair-key resurrection keeps restoring it.</item>
    /// <item><b>GroupDm departure bookkeeping</b> (via <see cref="HandleGroupDeparture"/>, after the
    /// private-lane teardown): the last member leaving deletes the channel doc + residual memberships; a
    /// departing LAST owner auto-promotes the longest-standing remaining member.</item>
    /// </list>
    /// <para>
    /// H4 (2026-08-05 reconciliation review, <c>final-review-fable.md</c>) — product decision (Marco,
    /// 2026-08-05): the membership delete below stays deliberately TYPE-AGNOSTIC and UNGATED, no
    /// <see cref="ChannelType"/> check and no ref lock. That is what keeps the launcher's stuck-row escape
    /// hatch working — a user can always force-leave a channel the client considers stuck, whatever kind
    /// it is. Accepted cost: on a LIVE (non-detached, still assertion-reachable) match channel this races
    /// <c>MatchChannelService.ApplyRosterAssertion</c>'s full-set diff, so a leave landing between two
    /// assertions is silently reverted by the next one. That is correct, not a bug — mm is authoritative
    /// for lobby membership while a channel is live; membership of a live lobby belongs to mm, not the
    /// user. It is also UI-unreachable in practice: the launcher hides Leave for the caller's
    /// currently-live match channel (<c>launcher-e/src/components/chat/ChannelListRow.tsx</c>,
    /// <c>!isCurrentLiveMatchChannel</c>) and shows it only for stale/stuck System rows. Stale rows (dead
    /// lobby ⇒ no assertion ever reaches them again) and detached/frozen post-game rooms (assertions
    /// discarded once frozen) get no further assertions either way, so a leave on either of THOSE sticks —
    /// exactly the case the escape hatch exists for. No behavior change; this codifies the decision to
    /// keep the exception as-is rather than adding a server-side no-op-for-live-match-channel guard.
    /// </para>
    /// </summary>
    public async Task<ChannelOperationResult> LeaveChannel(string channelId)
    {
        if (!_sessionRegistry.TryGetByConnectionId(Context.ConnectionId, out var session))
        {
            return new ChannelOperationResult(ChatResultCode.PermissionDenied);
        }

        var battleTag = session.Identity.BattleTag;

        // Load the channel BEFORE mutating so the departure can branch by type. A missing/vanished channel
        // stays a no-op Ok (treated as non-private-lane below) — LeaveChannel never returns NotFound.
        var channel = await _channelRepository.Load(channelId);

        await _membershipRepository.Delete(channelId, battleTag);
        _onlineMemberRegistry.Leave(channelId, Context.ConnectionId);

        // C3 (Task 14) preserved for Public/SemiPublic/System: route the viewer-roster change into the
        // batched ViewersChanged accumulator BEFORE the FocusRegistry mutation (pre-window baseline =
        // VIEWING while still focused), so an explicit leave while staying connected still emits a `left`
        // delta. C5 (Task 8, D11): SKIP this for Dm/GroupDm — private lanes never enter the roster system.
        // A null/vanished channel is treated as non-private-lane, exactly like the legacy unconditional call.
        var isPrivateLane = channel != null && channel.Type is ChannelType.Dm or ChannelType.GroupDm;
        if (!isPrivateLane)
        {
            _viewersAccumulator.RecordChange(channelId, battleTag, _timeProvider.GetUtcNow().UtcDateTime);
        }

        _focusRegistry.Unfocus(Context.ConnectionId, channelId);

        // C6 (Task 9, D11): revoke THIS connection's presence interest derived from the channel it just
        // left (UNCONDITIONAL + no-op-safe, mirroring UnfocusChannel — a Public-channel leave self-noops).
        _presenceInterestRegistry.RevokeFocus(Context.ConnectionId, channelId);

        // C6 (Task 9, D11): the leaver is no longer a member of a Dm/GroupDm, so every OTHER connection
        // watching that private channel must drop the leaver's presence. Gated to private lanes (for a
        // Public channel OnMemberRemoved is a registry no-op anyway — nothing registers interest through
        // it — but gating keeps the intent explicit and off the hot public-leave path). A null/vanished
        // channel is treated as non-private-lane, exactly like the accumulator branch above.
        if (isPrivateLane)
        {
            _presenceInterestRegistry.OnMemberRemoved(channelId, battleTag);
        }

        // C5 (Task 8, D12): GroupDm departure bookkeeping — empty-deletion or last-owner auto-promotion.
        if (channel != null && channel.Type == ChannelType.GroupDm)
        {
            await HandleGroupDeparture(channelId);
        }

        return new ChannelOperationResult(ChatResultCode.Ok);
    }

    /// <summary>
    /// C5 (Task 8, D12): post-leave bookkeeping for a <see cref="ChannelType.GroupDm"/>. Reloads the surviving
    /// members (enumerating a group via <see cref="Memberships.MembershipRepository.LoadForChannel"/> is
    /// legitimate — the never-enumerate guardrail is for PUBLIC channels; groups are ACL-bound and capped) and:
    /// <list type="bullet">
    /// <item>NONE remain ⇒ the LAST member left: hard-delete the channel doc
    /// (<see cref="Channels.ChannelRepository.Delete"/>) + any residual membership rows
    /// (<see cref="Memberships.MembershipRepository.DeleteAllForChannel"/>). Messages are left to the 90d TTL
    /// (no reader exists for a deleted channel and moderators are scope-walled out of GroupDm).</item>
    /// <item>An <see cref="MembershipRole.Owner"/> still remains ⇒ NO-OP (the owner-set is intact).</item>
    /// <item>NO owner remains ⇒ auto-promote the LONGEST-STANDING remaining member (earliest
    /// <c>JoinedAt</c>), ties broken deterministically by <see cref="string.CompareOrdinal(string, string)"/>
    /// over the (already-lowercased) battleTags, to <see cref="MembershipRole.Owner"/>. Auto-promotion emits
    /// NO live event — the promoted member learns their role on their next SessionState (L3 handoff).</item>
    /// </list>
    /// </summary>
    private async Task HandleGroupDeparture(string channelId)
    {
        var remaining = await _membershipRepository.LoadForChannel(channelId);

        if (remaining.Count == 0)
        {
            // Last member left ⇒ delete the channel doc + any residual membership rows (residual safety).
            await _channelRepository.Delete(channelId);
            await _membershipRepository.DeleteAllForChannel(channelId);
            // C6 (Task 9, D11): the channel is gone — drop any residual presence interest derived through
            // it (defensive: the departing last member's own interest was already revoked in LeaveChannel,
            // and no other member remains to be a watcher — but a deleted channel must leave no index trace).
            _presenceInterestRegistry.RemoveChannel(channelId);
            return;
        }

        if (remaining.Any(m => m.Role == MembershipRole.Owner))
        {
            // An owner still remains — the owner-set is intact, nothing to do.
            return;
        }

        // No owner remains ⇒ auto-promote the longest-standing member (earliest JoinedAt, CompareOrdinal
        // tie-break over the already-lowercased tags — deterministic and test-pinned).
        var successor = remaining
            .OrderBy(m => m.JoinedAt)
            .ThenBy(m => m.BattleTag, StringComparer.Ordinal)
            .First();
        await _membershipRepository.SetRole(channelId, successor.BattleTag, MembershipRole.Owner);
    }

    /// <summary>
    /// Updates the caller's per-channel notification level. Non-member → <see cref="ChatResultCode.NotMember"/>.
    /// Public channels support only <see cref="NotificationLevel.Mentions"/>/<see cref="NotificationLevel.None"/>
    /// — a request for <see cref="NotificationLevel.All"/> on a Public channel is REJECTED with
    /// <see cref="ChatResultCode.PermissionDenied"/> (acceptance 3: no silent coercion/clamping).
    /// SemiPublic (and every other type) supports all three levels. On success, persists via
    /// <see cref="Memberships.MembershipRepository.SetNotificationLevel"/> and mirrors the change into
    /// <see cref="FanOut.OnlineMemberRegistry"/> so the hot fan-out path sees it immediately.
    /// <para>
    /// PR36 follow-up (D2): AFTER that membership update succeeds, ALSO upserts a
    /// <see cref="Memberships.NotificationPreference"/> row for (battleTag, channelId) — but ONLY when
    /// the channel is name-joinable (<see cref="ChannelType.Public"/>/<see cref="ChannelType.SemiPublic"/>).
    /// Dm/GroupDm/System memberships are ACL-governed, not user-leavable in the room-catalog sense, so the
    /// pref collection stays bounded to the room catalog — this write is what lets the level SURVIVE a
    /// later hard-delete leave (<see cref="LeaveChannel"/>) and get re-seeded on rejoin (<see cref="JoinChannel"/>)
    /// or consulted for a non-member Public mention (<see cref="Mentions.MentionFanOut"/>).
    /// </para>
    /// <para>
    /// Fix round 1 (F5): the pref upsert is a SECONDARY, best-effort write — by the time it runs, the
    /// primary membership update has already succeeded and the caller's request is already satisfied. A
    /// failure there (e.g. a Mongo hiccup) must NEVER surface as an error for an already-applied level
    /// change — this matches the "secondary write never breaks the primary ack" posture elsewhere in the
    /// codebase (<see cref="Mentions.MentionFanOut"/>'s per-target fault isolation, <see cref="FanOut.FanOutEngine"/>'s
    /// per-recipient fault isolation). Failures are caught and logged; the method still returns Ok.
    /// </para>
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

        // PR36 follow-up (D2): the channel is now loaded UNCONDITIONALLY (previously only for the All-
        // on-Public rejection check below) — the pref write further down needs its Type regardless of
        // which level was requested.
        var channel = await _channelRepository.Load(channelId);

        if (level == NotificationLevel.All && channel != null && channel.Type == ChannelType.Public)
        {
            return new ChannelOperationResult(ChatResultCode.PermissionDenied);
        }

        await _membershipRepository.SetNotificationLevel(channelId, battleTag, level);
        _onlineMemberRegistry.SetNotificationLevel(channelId, Context.ConnectionId, level);

        // PR36 follow-up (D2): persist the just-applied level for name-joinable rooms only, so it
        // survives a later leave/rejoin cycle. A vanished channel (null) is treated as non-name-joinable
        // — no pref write, nothing to seed back into on a rejoin that can never happen.
        // Fix round 1 (F5): best-effort — the membership update above already succeeded and the caller's
        // request is already satisfied, so a failure persisting this SECONDARY carrier must not turn an
        // already-applied level change into an error response.
        if (channel != null && (channel.Type == ChannelType.Public || channel.Type == ChannelType.SemiPublic))
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            try
            {
                await _notificationPreferenceRepository.Upsert(battleTag, channelId, level, now);
            }
            catch (Exception ex)
            {
                Log.Warning(
                    ex,
                    "NotificationPreference upsert failed for {BattleTag} on channel {ChannelId} — the membership level was already applied; the persisted preference may lag until the next successful set",
                    battleTag,
                    channelId);
            }
        }

        return new ChannelOperationResult(ChatResultCode.Ok);
    }
}
