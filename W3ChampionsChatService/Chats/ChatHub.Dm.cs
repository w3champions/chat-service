using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using MongoDB.Bson;
using Serilog;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.FanOut;
using W3ChampionsChatService.Memberships;
using W3ChampionsChatService.Protocol;
using W3ChampionsChatService.Relationships;
using W3ChampionsChatService.Users;

namespace W3ChampionsChatService.Chats;

/// <summary>
/// C5 (Task 3): the DM front door — <see cref="OpenDm"/> (consent-creation matrix + block-uniform
/// observability + the fail-closed stranger-initiation cap) and <see cref="SetDmPrivacy"/>. Shared DM
/// helpers live here too; T4 extends this partial with the send-path private-lane gates.
/// </summary>
public partial class ChatHub
{
    /// <summary>
    /// Opens (find-or-creates) the 1:1 DM between the caller and <paramref name="battleTag"/> and returns
    /// the channel plus the caller's OWN membership. The resolution order below is LOAD-BEARING and honored
    /// EXACTLY — every reject is a typed <see cref="OpenDmResult"/> (never a silent drop), each mapping:
    /// <list type="number">
    /// <item>Fail-closed identity: no live session → <see cref="ChatResultCode.PermissionDenied"/> (there is
    /// no identity to open a DM under).</item>
    /// <item>Null/whitespace <paramref name="battleTag"/> → <see cref="HubException"/> (client-bug mapping,
    /// D18) — thrown BEFORE any relationship read (the provider does not guard null). Self-DM (the caller's
    /// own tag, case-insensitive) → <see cref="ChatResultCode.PermissionDenied"/>.</item>
    /// <item>Fetch the CALLER's relationship snapshot. A snapshot proving friendship takes the FRIEND path
    /// even if stale (friend-cache hits win over an outage — D1 tier c). Otherwise, if the snapshot is
    /// unavailable (<see cref="RelationshipUnavailableException"/>) or stale — i.e. the outcome would need
    /// the stranger path but we cannot trust "not friend" — the initiation fails closed:
    /// <see cref="ChatResultCode.Throttled"/> with <see cref="ChatLimits.RelationshipRetryAfterSeconds"/>
    /// (D1 tier a; NEVER a silent no-friend decision).</item>
    /// <item>FRIEND path: find-or-create a born-<see cref="DmRequestState.Accepted"/> shell (friends bypass
    /// consent AND the target's dmPrivacy AND the D14 directory check — the fresh friend edge proves the
    /// target exists), ensure the caller's membership, seed the registry, return
    /// <see cref="ChatResultCode.Ok"/>. NO initiation is recorded.</item>
    /// <item>STRANGER path: an EXISTING shell (by pair-key, pending OR accepted) short-circuits FIRST —
    /// skipping the directory check, the dmPrivacy gate, AND the cap — and returns Ok (D8/OQ-6: re-opening
    /// an established lane is not a creation, so a later dmPrivacy tightening never retro-gates it). Only a
    /// genuinely NEW shell is gated: the target must have a <c>user_directory</c> row (D14) else
    /// <see cref="ChatResultCode.NotFound"/>; then the target's <see cref="DmPrivacy"/> gates creation via
    /// an ALLOW-LIST — only <see cref="DmPrivacy.Everyone"/> permits, any other value (incl. out-of-range)
    /// fails closed to <see cref="ChatResultCode.PermissionDenied"/> (deliberately reveals the setting);
    /// then the 8h cap is enforced ATOMICALLY (check-and-record under one lock, before the DB create) — at/
    /// over <see cref="ChatLimits.StrangerDmInitiationCap"/> active initiations →
    /// <see cref="ChatResultCode.Throttled"/> (retry-after from the tracker, no DB write); otherwise the
    /// admitted initiation is recorded, the pending shell is created, the caller's membership ensured, the
    /// registry seeded, and Ok returned.</item>
    /// </list>
    /// The block check is NEVER consulted here (D5): the observable result is computed from friendship +
    /// the target's dmPrivacy ALONE, so a caller blocked by the target walks the identical path (their
    /// sends are silently dropped later, in T4). OpenDm only ever creates/returns the CALLER's own
    /// membership — never a counterparty's (T2 carry-forward: the recipient's decline lives on THEIR
    /// membership and must never ride back to the sender).
    /// </summary>
    public async Task<OpenDmResult> OpenDm(string battleTag)
    {
        // 1. Fail-closed identity.
        if (!_sessionRegistry.TryGetByConnectionId(Context.ConnectionId, out var session))
        {
            return new OpenDmResult(ChatResultCode.PermissionDenied);
        }

        // 2. Malformed arg → HubException, guarded BEFORE any provider read (the provider does not guard
        // null; an unguarded null would escape as an unmapped HubException from deeper in the stack).
        if (string.IsNullOrWhiteSpace(battleTag))
        {
            throw new HubException("OpenDm requires a non-empty battleTag");
        }

        var caller = session.Identity.BattleTag;

        // Normalize the incoming tag ONCE (FIX 3): trim whitespace so a padded arg agrees across every guard
        // below — the self-check, friend-check, directory Load, dmPrivacy read, and DmPairKey (which trims
        // internally). Case is left untouched: the pair-key and relationship checks are case-insensitive.
        var target = battleTag.Trim();

        // Self-DM is user-reachable → a typed PermissionDenied (case-insensitive: battleTags carry live
        // casing over the wire but resolve to the same identity).
        if (string.Equals(caller, target, StringComparison.OrdinalIgnoreCase))
        {
            return new OpenDmResult(ChatResultCode.PermissionDenied);
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // 3. Fetch the CALLER's snapshot (never the target's — D5). No usable snapshot at all is an outage:
        // the outcome would need the stranger path (we cannot prove friendship), so fail closed retriable.
        RelationshipSnapshot snapshot;
        try
        {
            snapshot = await _relationshipProvider.GetSnapshotAsync(caller);
        }
        catch (RelationshipUnavailableException)
        {
            return new OpenDmResult(ChatResultCode.Throttled, ChatLimits.RelationshipRetryAfterSeconds);
        }

        // 4. FRIEND path — friends bypass consent, dmPrivacy, and the directory check; a friendship proof
        // proceeds even on a STALE snapshot (friend-cache hits win over an outage).
        if (snapshot.IsFriendWith(target))
        {
            var channel = await _channelRepository.FindOrCreateDm(
                caller, target, initiator: caller, DmRequestState.Accepted, now);
            var membership = await EnsureCallerMembership(channel.Id, caller, now);
            return new OpenDmResult(ChatResultCode.Ok, Channel: channel, Membership: membership);
        }

        // Not a proven friend. Taking the stranger path on a STALE snapshot would risk treating a
        // just-added friend as a stranger — the initiation requires freshness, so fail closed retriable.
        if (!snapshot.IsFresh(now))
        {
            return new OpenDmResult(ChatResultCode.Throttled, ChatLimits.RelationshipRetryAfterSeconds);
        }

        // 5. STRANGER path. An EXISTING shell short-circuits EVERYTHING below — the directory check, the
        // dmPrivacy gate, AND the cap (FIX 1 / D8 / OQ-6). Re-opening a lane is NOT a creation: the shell
        // already proves the target exists and the lane exists, so a later dmPrivacy tightening must never
        // retro-gate an established conversation (accepted OR pending). Pending-phase DELIVERY still
        // re-checks dmPrivacy in the T4 send path — that gate is separate and unchanged. Re-opening never
        // throttles and never records a new initiation.
        var existingShell = await _channelRepository.LoadByPairKey(caller, target);
        if (existingShell != null)
        {
            var membership = await EnsureCallerMembership(existingShell.Id, caller, now);
            return new OpenDmResult(ChatResultCode.Ok, Channel: existingShell, Membership: membership);
        }

        // A genuinely NEW shell (no existing lane). D14: a stranger target must exist in the directory
        // (self-healing on first connect) — prevents junk shells and initiation-slot waste for never-seen
        // tags.
        var directoryEntry = await _userDirectory.Load(target);
        if (directoryEntry == null)
        {
            return new OpenDmResult(ChatResultCode.NotFound);
        }

        // dmPrivacy gate (the block is NEVER consulted here — D5). ALLOW-LIST (FIX 4): ONLY Everyone lets a
        // stranger create; Friends/Nobody — and any out-of-range cast value — fail CLOSED. Friends already
        // bypassed this above (a friend still reaches a Nobody target).
        var targetSettings = await _userSettings.LoadOrDefault(target);
        if (targetSettings.DmPrivacy is not DmPrivacy.Everyone)
        {
            return new OpenDmResult(ChatResultCode.PermissionDenied);
        }

        // The 8h stranger-initiation cap is enforced ATOMICALLY (FIX 2): check-and-record under one lock,
        // BEFORE the DB create, so a rejected initiation writes nothing AND concurrent same-caller opens
        // cannot slip past the cap (TOCTOU-free). A false return means at/over the cap → fail-closed
        // retriable. On admit we record the attempt: should a concurrent open from the OTHER side win the
        // upsert between the existence check above and FindOrCreateDm below (returning a doc they
        // initiated), the record still stands — the caller legitimately attempted a NEW stranger initiation
        // (D7 counts the attempt), and that race is benign and vanishingly rare under
        // single-connection-per-battleTag.
        var normalizedTarget = target.ToLowerInvariant();
        if (!_dmInitiationTracker.TryRecord(caller, normalizedTarget, now, ChatLimits.StrangerDmInitiationCap))
        {
            return new OpenDmResult(ChatResultCode.Throttled, _dmInitiationTracker.RetryAfterSeconds(caller, now));
        }

        var created = await _channelRepository.FindOrCreateDm(
            caller, target, initiator: caller, DmRequestState.Pending, now);

        var callerMembership = await EnsureCallerMembership(created.Id, caller, now);
        return new OpenDmResult(ChatResultCode.Ok, Channel: created, Membership: callerMembership);
    }

    /// <summary>
    /// Sets the caller's dmPrivacy (§11 Settings). Fail-closed identity → <see cref="ChatResultCode.PermissionDenied"/>;
    /// otherwise a read-modify-write of the caller's <see cref="UserSettings"/> that touches ONLY
    /// <see cref="UserSettings.DmPrivacy"/> (LoadOrDefault preserves every sibling field) and returns
    /// <see cref="ChatResultCode.Ok"/>.
    /// </summary>
    public async Task<ChannelOperationResult> SetDmPrivacy(DmPrivacy privacy)
    {
        if (!_sessionRegistry.TryGetByConnectionId(Context.ConnectionId, out var session))
        {
            return new ChannelOperationResult(ChatResultCode.PermissionDenied);
        }

        var caller = session.Identity.BattleTag;
        // Read-modify-write so a future cached setting (notification level, sounds) is preserved — mirrors
        // UpsertDirectoryStub's Load → set → Upsert pattern.
        var settings = await _userSettings.LoadOrDefault(caller);
        settings.DmPrivacy = privacy;
        await _userSettings.Upsert(settings);
        return new ChannelOperationResult(ChatResultCode.Ok);
    }

    // ================================================================================================
    // C5 (Task 6): the consent state machine's user-facing half — AcceptRequest / DeclineRequest. Both
    // are RECIPIENT-only actions on a 1:1 Dm request; they share the same fail-closed guard cluster and
    // differ ONLY in their terminal effect (accept flips the channel + frees the slot; decline stamps a
    // per-recipient suppression window and is otherwise a NO-OP invisible to the sender).
    // ================================================================================================

    /// <summary>
    /// The RECIPIENT accepts a pending 1:1 Dm request, flipping it to <see cref="DmRequestState.Accepted"/>
    /// PERMANENTLY ("normal forever", §4). Every reject is a typed <see cref="ChannelOperationResult"/>:
    /// <list type="number">
    /// <item>Fail-closed identity: no live session → <see cref="ChatResultCode.PermissionDenied"/>.</item>
    /// <item>Load the channel; missing → <see cref="ChatResultCode.NotFound"/>.</item>
    /// <item>Guard cluster (all collapse to <see cref="ChatResultCode.PermissionDenied"/>): the channel
    /// must be a <see cref="ChannelType.Dm"/>, the caller must be a member (hot-path zero-DB
    /// <see cref="FanOut.OnlineMemberRegistry.IsMember"/> gate, mirroring <c>SendMessage</c> step 3), and
    /// the caller must NOT be <see cref="ChatChannel.RequestInitiatedBy"/> (only the recipient can accept
    /// — an initiator accepting their own outgoing request is rejected).</item>
    /// <item>Idempotent: an already-<see cref="DmRequestState.Accepted"/> conversation returns
    /// <see cref="ChatResultCode.Ok"/> unchanged (accept is safe to replay).</item>
    /// <item>Otherwise <see cref="AcceptPendingCore"/> (the shared T4 transition: conditional
    /// <see cref="Channels.ChannelRepository.SetRequestAccepted"/> flip + +1y shell expiry, clear the
    /// recipient's own <see cref="Memberships.ChannelMembership.DeclinedUntil"/>, and free the initiator's
    /// stranger-initiation slot via <see cref="FanOut.DmInitiationTracker.MarkAccepted"/>) → Ok.</item>
    /// </list>
    /// NO event is pushed to the initiator: they observe the acceptance via <see cref="ChatChannel.RequestState"/>
    /// on their next SessionState (or simply by receiving the recipient's replies) — nothing beyond that is
    /// pinned, so nothing is emitted.
    /// </summary>
    public async Task<ChannelOperationResult> AcceptRequest(string channelId)
    {
        if (!_sessionRegistry.TryGetByConnectionId(Context.ConnectionId, out var session))
        {
            return new ChannelOperationResult(ChatResultCode.PermissionDenied);
        }

        var caller = session.Identity.BattleTag;

        var channel = await _channelRepository.Load(channelId);
        if (channel == null)
        {
            return new ChannelOperationResult(ChatResultCode.NotFound);
        }

        if (!IsConsentActionAllowed(channel, channelId, caller))
        {
            return new ChannelOperationResult(ChatResultCode.PermissionDenied);
        }

        // Idempotent: re-accepting an already-accepted conversation is a no-op Ok (accept-race safe too —
        // AcceptPendingCore's conditional flip only takes effect once, but short-circuiting here also
        // skips a needless write attempt when the state is already known-accepted).
        if (channel.RequestState == DmRequestState.Accepted)
        {
            return new ChannelOperationResult(ChatResultCode.Ok);
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        await AcceptPendingCore(channelId, channel.RequestInitiatedBy, caller, now);
        return new ChannelOperationResult(ChatResultCode.Ok);
    }

    /// <summary>
    /// The RECIPIENT declines a pending 1:1 Dm request — a SOFT + TEMPORAL suppression, NEVER a state
    /// change. Shares <see cref="AcceptRequest"/>'s exact guard cluster (fail-closed session; NotFound on a
    /// missing channel; Dm + member + not-the-initiator else <see cref="ChatResultCode.PermissionDenied"/>),
    /// then additionally requires the request to still be <see cref="DmRequestState.Pending"/> — an
    /// already-<see cref="DmRequestState.Accepted"/> conversation cannot be declined
    /// (<see cref="ChatResultCode.PermissionDenied"/>; accepted = "normal forever").
    /// <para>
    /// The ONLY write decline ever performs is stamping the caller's OWN membership
    /// <see cref="Memberships.ChannelMembership.DeclinedUntil"/> to <c>now + <see cref="ChatLimits.DmDeclineSuppression"/></c>
    /// (24h). ABSOLUTELY NOTHING ELSE HAPPENS: no channel-doc write (the channel stays byte-identical, still
    /// Pending — so the sender's wire view is unchanged), no event to ANYONE (least of all the initiator —
    /// the sender must never learn they were declined), and no tracker change (a declined initiation STILL
    /// counts toward the 8h stranger-initiation cap — pinned). This method IS the marquee decline-invisibility
    /// property: the sender observes an identical result/event/SessionState surface whether the recipient
    /// declines or does nothing.
    /// </para>
    /// </summary>
    public async Task<ChannelOperationResult> DeclineRequest(string channelId)
    {
        if (!_sessionRegistry.TryGetByConnectionId(Context.ConnectionId, out var session))
        {
            return new ChannelOperationResult(ChatResultCode.PermissionDenied);
        }

        var caller = session.Identity.BattleTag;

        var channel = await _channelRepository.Load(channelId);
        if (channel == null)
        {
            return new ChannelOperationResult(ChatResultCode.NotFound);
        }

        if (!IsConsentActionAllowed(channel, channelId, caller))
        {
            return new ChannelOperationResult(ChatResultCode.PermissionDenied);
        }

        // Decline is only meaningful on a still-Pending request — the ONE guard that differs from accept.
        // An accepted conversation is "normal forever" and cannot be declined.
        if (channel.RequestState != DmRequestState.Pending)
        {
            return new ChannelOperationResult(ChatResultCode.PermissionDenied);
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        // The SOLE effect: the recipient's own membership suppression window. No channel write, no event,
        // no tracker change — byte-invisible to the sender (D3).
        await _membershipRepository.SetDeclinedUntil(channelId, caller, now + ChatLimits.DmDeclineSuppression);
        return new ChannelOperationResult(ChatResultCode.Ok);
    }

    /// <summary>
    /// The shared RECIPIENT-only guard cluster for <see cref="AcceptRequest"/> / <see cref="DeclineRequest"/>
    /// (all failures map to <see cref="ChatResultCode.PermissionDenied"/> at the call site): the channel must
    /// be a <see cref="ChannelType.Dm"/>, the caller must be a member of it (hot-path zero-DB registry gate),
    /// and the caller must NOT be the request initiator (only the recipient can accept/decline). Never leaks
    /// pending-vs-declined state — it depends only on channel type, membership, and who wrote first.
    /// </summary>
    private bool IsConsentActionAllowed(ChatChannel channel, string channelId, string caller) =>
        channel.Type == ChannelType.Dm
        && _onlineMemberRegistry.IsMember(Context.ConnectionId, channelId)
        && !string.Equals(caller, channel.RequestInitiatedBy, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Ensures the CALLER's own membership exists for <paramref name="channelId"/> (idempotent
    /// <see cref="MembershipRepository.InsertIfAbsent"/> — a re-open returns the existing row untouched)
    /// and seeds this connection into the <see cref="OnlineMemberRegistry"/>. DM memberships keep the model
    /// default <see cref="NotificationLevel.All"/> (never flipped) and <see cref="MembershipRole.Member"/>.
    /// The registry seed mirrors <c>JoinChannel</c>'s (ChatHub.Channels.cs); <see cref="OpenDm"/> only ever
    /// calls this for a <see cref="ChannelType.Dm"/> channel, so the <see cref="MemberState.ChannelType"/>
    /// seed (C5 Task 5, D11) is stamped <see cref="ChannelType.Dm"/> literally.
    /// </summary>
    private async Task<ChannelMembership> EnsureCallerMembership(string channelId, string caller, DateTime now)
    {
        var membership = await _membershipRepository.InsertIfAbsent(new ChannelMembership
        {
            ChannelId = channelId,
            BattleTag = caller,
            Role = MembershipRole.Member,
            NotificationLevel = NotificationLevel.All,
            JoinedAt = now,
        });

        _onlineMemberRegistry.Join(channelId, Context.ConnectionId,
            new MemberState(caller, membership.NotificationLevel, membership.LastReadSeq, ChannelType.Dm));

        return membership;
    }

    // ================================================================================================
    // C5 (Task 4): send-path private-lane gates + shared helpers. SendMessage (ChatHub.Messaging.cs)
    // calls into these between the channel load and the persist step (step 5.5 — since C6 Task 4, this
    // runs AFTER the content-intrinsic mention-markup validation gate at step 5.25, never before it: a
    // blocked sender's invalid mention content must be rejected identically to an unblocked sender's, not
    // silently short-circuited here first), and again post-persist for Dm recipient materialization.
    // ================================================================================================

    /// <summary>
    /// D6 silent-drop ack fabrication. Every DELIBERATELY silent Dm drop (blocked 1:1 send; pending-cap
    /// exceeded; pending-phase dmPrivacy recheck failure) returns this shape — an <see cref="ChatResultCode.Ok"/>
    /// carrying a FABRICATED, non-null <c>MessageId</c>/<c>Seq</c> so it is byte-shaped exactly like a real
    /// ack. A <c>null</c> id/seq on Ok, or any non-Ok code, would instantly leak the block/decline/cap state
    /// (silent-drop uniformity is the marquee property). The fabricated seq is <c>channel.LastSeq + 1</c>
    /// (the next real seq a client would expect); clients dedupe by messageId, so the never-persisted id is
    /// harmless (D6 documented residuals).
    /// </summary>
    private static SendMessageResult FakeSendAck(ChatChannel channel) =>
        new SendMessageResult(ChatResultCode.Ok, MessageId: ObjectId.GenerateNewId().ToString(), Seq: channel.LastSeq + 1);

    /// <summary>
    /// Resolves the COUNTERPART battleTag of a 1:1 <see cref="ChannelType.Dm"/> from its
    /// <see cref="ChatChannel.PairKey"/> by stripping the sender's normalized tag. The pair-key is
    /// <c>{a}|{b}</c> of the two sorted, lowercased tags (<see cref="DmPairKey.For"/>), so the returned
    /// counterpart is LOWERCASED (normalized) — matching how it is used downstream (case-insensitive
    /// snapshot/registry lookups; the materialized membership + settings reads). Never called for GroupDm
    /// (no pair-key).
    /// </summary>
    private static string ResolveDmCounterpart(ChatChannel channel, string senderBattleTag) =>
        DmPairKey.CounterpartOf(channel.PairKey, senderBattleTag);

    /// <summary>
    /// The shared consent-accept transition used by BOTH reply-accept (a recipient's first reply) and
    /// auto-accept (an initiator's send once the two are friends). Conditionally flips the channel
    /// <see cref="DmRequestState.Pending"/> → <see cref="DmRequestState.Accepted"/> via
    /// <see cref="Channels.ChannelRepository.SetRequestAccepted"/> (idempotent under an accept-race — only
    /// the winning flip returns true). On the WINNING flip it (a) clears the recipient's own
    /// decline-suppression window (<see cref="Memberships.MembershipRepository.ClearDeclinedUntil"/>) so a
    /// previously-declined-then-accepted conversation is fully resolved, and (b) frees the initiator's
    /// stranger-initiation slot INSTANTLY (<see cref="FanOut.DmInitiationTracker.MarkAccepted"/> — "accepted
    /// frees capacity"). <paramref name="recipient"/> is the NON-initiator party (the reply's sender, or the
    /// counterpart on an initiator auto-accept); its normalized form keys the tracker exactly as
    /// <see cref="OpenDm"/> recorded it. The caller updates its in-memory <c>channel.RequestState</c> so the
    /// persist step computes the +1y accepted-shell expiry.
    /// </summary>
    private async Task AcceptPendingCore(string channelId, string initiator, string recipient, DateTime now)
    {
        var flipped = await _channelRepository.SetRequestAccepted(channelId, now);
        if (!flipped)
        {
            // Already accepted (a concurrent AcceptRequest / reply won the race) — treat as accepted, but do
            // not re-clear/re-free (the winning flip already did, keeping the transition's side-effects once).
            return;
        }

        await _membershipRepository.ClearDeclinedUntil(channelId, recipient);
        _dmInitiationTracker.MarkAccepted(initiator, recipient.ToLowerInvariant());
    }

    /// <summary>
    /// Step 5.5 of the send pipeline (private-lane gates), invoked from
    /// <see cref="SendMessage(string, string)"/> ONLY for <see cref="ChannelType.Dm"/>/
    /// <see cref="ChannelType.GroupDm"/> channels, between the mention-markup validation gate (step 5.25 —
    /// C6 Task 4, D2; deliberately upstream of THIS gate so a blocked sender never gets a different
    /// outcome than an unblocked sender for the same invalid content) and the mute gate. Returns a
    /// SHORT-CIRCUIT <see cref="SendMessageResult"/> to return immediately (a silent <see cref="FakeSendAck"/>,
    /// or the one fail-closed <see cref="ChatResultCode.Throttled"/>), or <c>null</c> to proceed to persist.
    /// May flip <paramref name="channel"/>'s in-memory <see cref="ChatChannel.RequestState"/> to
    /// <see cref="DmRequestState.Accepted"/> (reply/auto-accept) so the persist step re-stamps the +1y
    /// accepted-shell expiry rather than the +30d pending one. Honored EXACTLY in this order:
    /// <list type="number">
    /// <item><b>GroupDm</b>: membership was already checked at step 3 — no block/consent gates; proceed.</item>
    /// <item><b>Dm block gate</b> (EVERY Dm send, pending OR accepted — a block silences an established lane
    /// too): fetch the COUNTERPART's snapshot ONCE (reused below). If it has BLOCKED the sender ⇒
    /// <see cref="FakeSendAck"/> (persist NOTHING, deliver NOTHING, materialize NO membership — "never
    /// delivered/stored, no new lane opens"). If NO snapshot exists at all
    /// (<see cref="RelationshipUnavailableException"/>) ⇒ <see cref="ChatResultCode.Throttled"/> — the ONLY
    /// non-silent fail-closed here (a system-outage condition independent of block state; a silent drop
    /// would wrongly imply a block, and a stale snapshot is accepted so this only triggers on a total miss).</item>
    /// <item><b>Pending machine</b> (only while <see cref="DmRequestState.Pending"/>):
    /// <list type="bullet">
    /// <item>sender ≠ <see cref="ChatChannel.RequestInitiatedBy"/> (the recipient replying) ⇒ reply-accept
    /// (<see cref="AcceptPendingCore"/>) then proceed as Accepted — a reply is never capped.</item>
    /// <item>sender = initiator AND the counterpart snapshot now lists the sender as a friend ⇒ auto-accept
    /// (friends bypass consent, D8) then proceed as Accepted.</item>
    /// <item>sender = initiator, still a stranger ⇒ recheck the recipient's <see cref="DmPrivacy"/>
    /// (ALLOW-LIST: only <see cref="DmPrivacy.Everyone"/> proceeds; Friends/Nobody/out-of-range ⇒
    /// <see cref="FakeSendAck"/>, silent and uniform with a decline), then the pending-depth cap
    /// (<c>LastSeq ≥ <see cref="ChatLimits.PendingConversationMaxMessages"/></c> ⇒ <see cref="FakeSendAck"/>).</item>
    /// </list></item>
    /// </list>
    /// </summary>
    private async Task<SendMessageResult> ApplyPrivateLaneGates(ChatChannel channel, string senderBattleTag, DateTime now)
    {
        // GroupDm: no consent/block gates — membership already proven at step 3.
        if (channel.Type == ChannelType.GroupDm)
        {
            return null;
        }

        var counterpart = ResolveDmCounterpart(channel, senderBattleTag);

        // (1) Block gate — the counterpart's snapshot, reused for the friend check below.
        RelationshipSnapshot snapshot;
        try
        {
            snapshot = await _relationshipProvider.GetSnapshotAsync(counterpart);
        }
        catch (RelationshipUnavailableException)
        {
            // No snapshot at all: fail closed retriable (NON-silent). Block-agnostic — leaks no block state.
            return new SendMessageResult(ChatResultCode.Throttled, ChatLimits.RelationshipRetryAfterSeconds);
        }

        if (snapshot.HasBlocked(senderBattleTag))
        {
            // Blocked: silent non-delivery. Nothing is persisted, delivered, or materialized (SendMessage
            // returns this before the persist/materialize/fan-out steps).
            return FakeSendAck(channel);
        }

        // (2) Pending machine.
        if (channel.RequestState == DmRequestState.Pending)
        {
            if (!string.Equals(senderBattleTag, channel.RequestInitiatedBy, StringComparison.OrdinalIgnoreCase))
            {
                // Recipient replying → reply-accept, then proceed as Accepted (never capped).
                await AcceptPendingCore(channel.Id, channel.RequestInitiatedBy, senderBattleTag, now);
                channel.RequestState = DmRequestState.Accepted;
            }
            else if (snapshot.IsFriendWith(senderBattleTag))
            {
                // Initiator sending, now friends → auto-accept (D8), then proceed as Accepted.
                await AcceptPendingCore(channel.Id, channel.RequestInitiatedBy, counterpart, now);
                channel.RequestState = DmRequestState.Accepted;
            }
            else
            {
                // Initiator sending, still a stranger. Re-check the recipient's dmPrivacy every send (D8);
                // an ALLOW-LIST so a tightened setting (or an out-of-range cast) fails closed to a silent
                // drop — indistinguishable from a decline.
                var recipientSettings = await _userSettings.LoadOrDefault(counterpart);
                if (recipientSettings.DmPrivacy is not DmPrivacy.Everyone)
                {
                    return FakeSendAck(channel);
                }

                // Pending-depth cap: only the initiator grows a pending conversation, so LastSeq is the depth.
                if (channel.LastSeq >= ChatLimits.PendingConversationMaxMessages)
                {
                    return FakeSendAck(channel);
                }
            }
        }

        return null; // proceed to persist
    }

    /// <summary>
    /// Post-persist, pre-fan-out Dm hook (invoked from <see cref="SendMessage(string, string)"/> after the
    /// message is durably stored, for <see cref="ChannelType.Dm"/> only): lazily materializes the
    /// counterpart's membership and fires the <see cref="ChatEvents.RequestReceived"/> transition.
    /// <list type="bullet">
    /// <item>Materializes the counterpart's membership if absent (idempotent
    /// <see cref="Memberships.MembershipRepository.InsertIfAbsent"/>, level <see cref="NotificationLevel.All"/>,
    /// role <see cref="MembershipRole.Member"/>). A NEW materialization is detected race-safely by comparing
    /// the returned row's Id to the candidate's fresh Id — on insert they match; an existing match returns
    /// the pre-existing row (different Id), and under a concurrent double-materialize exactly one caller wins
    /// the unique index and sees its own candidate Id. On first materialization it
    /// <see cref="FanOut.FanOutEngine.PushChannelAdded"/>(focus:false) — seeding the recipient's registry and
    /// pushing <c>ChannelAdded</c> WITHOUT auto-opening the DM.</item>
    /// <item>Fires <see cref="ChatEvents.RequestReceived"/> (targeted at the recipient's single live
    /// connection via <see cref="Sessions.ISessionRegistry.GetByBattleTag"/>; offline ⇒ skipped, the tray
    /// carries it via SessionState in T6) IFF the channel is <see cref="DmRequestState.Pending"/> AND the
    /// sender is the initiator AND (the membership was JUST materialized OR the recipient's decline window
    /// has elapsed — <c>now ≥ DeclinedUntil</c>, cleared first so the request resurfaces). Suppressed while
    /// still inside the decline window, and NOT re-fired on every pending message (the tray is already live).
    /// A reply/auto-accept flipped the in-memory RequestState to Accepted, so those never re-notify here.</item>
    /// </list>
    /// </summary>
    private async Task MaterializeDmRecipientAndNotify(ChatChannel channel, string senderBattleTag, DateTime now)
    {
        var counterpart = ResolveDmCounterpart(channel, senderBattleTag);

        var candidate = new ChannelMembership
        {
            ChannelId = channel.Id,
            BattleTag = counterpart,
            Role = MembershipRole.Member,
            NotificationLevel = NotificationLevel.All,
            JoinedAt = now,
        };
        var recipientMembership = await _membershipRepository.InsertIfAbsent(candidate);
        var newlyMaterialized = recipientMembership.Id == candidate.Id;

        // Follow-up spec §6 (bounded-snapshot repair): the recipient's connect snapshot may have
        // EXCLUDED this older 1:1 shell — and with it their OnlineMemberRegistry seed (registry set ==
        // DTO set). If they are online but this connection's registry lacks the channel, RE-ANNOUNCE
        // exactly like a first materialization: PushChannelAdded(focus:false) re-seeds the registry AND
        // hands the client the shell — and because this hook runs BEFORE the step-8 fan-out, the
        // activity for THIS very message reaches them. An already-seeded recipient is untouched (no
        // ChannelAdded on ordinary messages); ChannelAdded is an upsert client-side, so a client that
        // somehow still knows the shell just refreshes it.
        var recipientSession = _sessionRegistry.GetByBattleTag(counterpart);
        var needsReAnnounce = !newlyMaterialized
            && recipientSession != null
            && !_onlineMemberRegistry.IsMember(recipientSession.ConnectionId, channel.Id);

        if (newlyMaterialized || needsReAnnounce)
        {
            // Seeds the recipient's OnlineMemberRegistry + pushes ChannelAdded(focus:false) if they are
            // online; a no-op for an offline recipient (their SessionState picks the channel up on connect).
            await _fanOutEngine.PushChannelAdded(channel, recipientMembership, focus: false);
        }

        // RequestReceived transition — only a fresh/resurfaced pending request FROM the initiator.
        if (channel.RequestState != DmRequestState.Pending
            || !string.Equals(senderBattleTag, channel.RequestInitiatedBy, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var declinedUntil = recipientMembership.DeclinedUntil;
        var resurface = declinedUntil.HasValue && now >= declinedUntil.Value;

        // Suppress while still inside the decline window; and don't re-fire on ordinary later messages.
        if (!newlyMaterialized && !resurface)
        {
            return;
        }

        if (resurface)
        {
            await _membershipRepository.ClearDeclinedUntil(channel.Id, counterpart);
        }

        if (recipientSession != null)
        {
            var dto = new PendingDmRequestDto(channel.Id, channel.RequestInitiatedBy, channel.LastMessageAt ?? now);
            // Fault isolation (C5 LOW-1, security review): the RequestReceived push is a BEST-EFFORT live
            // notification — the message is already durably persisted (SendMessage's step 7, before this
            // post-persist hook), so a torn-down recipient connection must NEVER propagate out of
            // SendMessage; reconnect heals it via the tray (SessionState). Mirrors the same try/catch shape
            // as FanOutEngine.OnMessagePersisted/PushChannelAdded/PushChannelRemoved.
            try
            {
                await Clients.Client(recipientSession.ConnectionId).SendAsync(ChatEvents.RequestReceived, dto);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "RequestReceived push failed for {ConnectionId} on channel {ChannelId} — best-effort, tray resurfaces it via SessionState", recipientSession.ConnectionId, channel.Id);
            }
        }
    }

    /// <summary>
    /// 2026-08-04 follow-up spec §6: pages the caller's OLDER 1:1 Dm shells (the ones the bounded
    /// connect snapshot excluded), newest-first by (LastMessageAt, ChannelId), cursor =
    /// (cursorLastMessageAt, cursorChannelId) — both null for the first page; strictly-older-than
    /// filtering keyed on the PAIR makes the pagination stable under concurrent recency changes (a
    /// conversation that moves forward jumps to the FRONT and can never be double-served in a later
    /// page). Reuses ChannelDto (the SessionState.Channels shape) and computes the SAME D7
    /// user-visible unread per returned shell. Every returned shell is ALSO seeded into the caller's
    /// OnlineMemberRegistry — the follow-up §6 companion rule to the bounded connect seed — so a paged
    /// conversation is immediately usable (SendMessage/FocusChannel/GetMessages/MarkRead) without an
    /// extra OpenDm round-trip. Resolution order:
    /// <list type="number">
    /// <item>Fail-closed identity → <see cref="ChatResultCode.PermissionDenied"/>.</item>
    /// <item>Malformed cursor (exactly one half supplied) → <see cref="HubException"/> (the
    /// <c>GetMessages</c> client-bug mapping).</item>
    /// <item>Clamp <paramref name="limit"/> to [1, <see cref="ChatLimits.ConversationsPageSize"/>].</item>
    /// <item>Resolve the CALLER's <see cref="RelationshipSnapshot"/> ONCE (mirrors <see cref="OpenDm"/>
    /// step 3): no usable snapshot at all (<see cref="RelationshipUnavailableException"/>) fails closed to
    /// <see cref="ChatResultCode.Throttled"/> rather than risk serving an unfiltered page — a blocked
    /// counterpart's shell must NEVER page in here as an ordinary conversation (it stays in the connect
    /// snapshot unconditionally, Task 6, for the client's Blocked section ONLY).</item>
    /// <item>Load ALL memberships + their channels (the same two indexed queries the connect snapshot
    /// runs), keep <see cref="ChannelType.Dm"/> shells that are NOT <see cref="DmRequestState.Pending"/>
    /// (pending requests ride the connect snapshot + request tray ONLY — see
    /// <see cref="Protocol.SessionStateAssembler.SelectSnapshotMemberships"/>), NOT blocked, and carry a
    /// non-null <see cref="ChatChannel.LastMessageAt"/> (a null-stamped doc is data-impossible in
    /// production but would otherwise dead-end the client's wire cursor); order, cursor-filter, take the
    /// page.</item>
    /// <item>Per shell: D7 unread count → <see cref="ChannelDto"/>; seed the registry via
    /// <see cref="OnlineMemberRegistry.JoinPreservingReadCursor"/> (never regresses a newer in-flight
    /// <c>MarkRead</c>); return Ok.</item>
    /// </list>
    /// Per-page cost scales with the caller's TOTAL membership count (one <c>LoadForUser</c> + one
    /// <c>LoadByIds</c> per call, independent of the requested page size) — an explicit brief decision,
    /// acceptable because a client is expected to need only 1-3 pages before reaching the end. Paging
    /// deliberately relaxes Task 6's "registry set == connect-snapshot DTO set" invariant: the registry
    /// can now hold MORE than what any single SessionState carried. The broader property still holds —
    /// registry membership implies the client actually knows about the channel — because every shell
    /// seeded here is returned in this SAME response.
    /// </summary>
    public async Task<GetConversationsResult> GetConversations(DateTime? cursorLastMessageAt, string cursorChannelId, int limit)
    {
        // 1. Fail-closed identity.
        if (!_sessionRegistry.TryGetByConnectionId(Context.ConnectionId, out var session))
        {
            return new GetConversationsResult(ChatResultCode.PermissionDenied);
        }

        // 2. The cursor is a PAIR — both halves or neither (first page).
        var hasCursorTime = cursorLastMessageAt.HasValue;
        var hasCursorId = !string.IsNullOrEmpty(cursorChannelId);
        if (hasCursorTime != hasCursorId)
        {
            throw new HubException("GetConversations: cursorLastMessageAt and cursorChannelId form one cursor — supply both or neither.");
        }

        // 3. Clamp — never Limit(0)/unbounded, never rejected.
        var effectiveLimit = Math.Clamp(limit, 1, ChatLimits.ConversationsPageSize);

        var battleTag = session.Identity.BattleTag;

        // 4. Resolve the CALLER's relationship snapshot ONCE (mirrors OpenDm step 3): blocked shells stay
        // in the connect snapshot unconditionally (Task 6's Blocked tray) but must NEVER page in here as
        // an ordinary, live conversation — e.g. a 2-year-old blocked shell must not re-surface and get
        // registry-Joined as a live fan-out target. No usable snapshot at all is an outage: fail closed
        // retriable rather than risk serving an unfiltered page.
        RelationshipSnapshot snapshot;
        try
        {
            snapshot = await _relationshipProvider.GetSnapshotAsync(battleTag);
        }
        catch (RelationshipUnavailableException)
        {
            return new GetConversationsResult(ChatResultCode.Throttled, ChatLimits.RelationshipRetryAfterSeconds);
        }

        // 5. Membership-first (user→channels — the only supported direction), then the channel docs in
        // ONE LoadByIds. In-memory ordering over the caller's own bounded conversation count mirrors
        // the CountNameJoinableMembershipsForUser precedent — no new aggregation pipeline.
        var memberships = await _membershipRepository.LoadForUser(battleTag);
        var channelsById = (await _channelRepository.LoadByIds(memberships.Select(m => m.ChannelId)))
            .ToDictionary(c => c.Id);

        var ordered = memberships
            .Where(m => channelsById.TryGetValue(m.ChannelId, out var c)
                && c.Type == ChannelType.Dm
                // Pending requests ride the connect snapshot + request tray ONLY — never a paged
                // "conversation", from either direction (incoming or caller-initiated outgoing).
                && c.RequestState != DmRequestState.Pending
                // A null-stamped LastMessageAt is data-impossible in production (FindOrCreateDm always
                // stamps it at insert) but would otherwise produce an inexpressible client cursor for
                // that shell — exclude it from the page rather than dead-end pagination. The `?? JoinedAt`
                // SortTime fallback below is retained regardless, for ordering determinism.
                && c.LastMessageAt != null
                // Blocked-counterpart shells stay Blocked-tray-only (Task 6) — never page in here.
                && !(c.PairKey != null && snapshot.HasBlocked(DmPairKey.CounterpartOf(c.PairKey, battleTag))))
            .Select(m =>
            {
                var channel = channelsById[m.ChannelId];
                return (Membership: m, Channel: channel, SortTime: channel.LastMessageAt ?? m.JoinedAt);
            })
            .OrderByDescending(x => x.SortTime)
            .ThenByDescending(x => x.Channel.Id, StringComparer.Ordinal);

        var page = (hasCursorTime
                ? ordered.Where(x => x.SortTime < cursorLastMessageAt.Value
                    || (x.SortTime == cursorLastMessageAt.Value && string.CompareOrdinal(x.Channel.Id, cursorChannelId) < 0))
                : ordered)
            .Take(effectiveLimit)
            .ToList();

        // 6. Project (same D7 unread as the connect snapshot) + seed the registry per returned shell.
        var conversations = new List<ChannelDto>(page.Count);
        foreach (var item in page)
        {
            var unreadCount = await _messageRepository.CountUserVisibleAfter(
                item.Channel.Id, battleTag, item.Membership.LastReadSeq);
            conversations.Add(new ChannelDto(
                item.Channel, MembershipDto.From(item.Membership), unreadCount, unreadCount > 0));

            // JoinPreservingReadCursor (not the plain Join): this loop awaits Mongo per shell, so a
            // concurrent MarkRead landing on an already-seeded (channel, connection) entry during that
            // window must never be regressed back down by the DB-loaded LastReadSeq captured before it.
            _onlineMemberRegistry.JoinPreservingReadCursor(
                item.Channel.Id,
                Context.ConnectionId,
                new MemberState(battleTag, item.Membership.NotificationLevel, item.Membership.LastReadSeq, ChannelType.Dm));
        }

        return new GetConversationsResult(ChatResultCode.Ok, Conversations: conversations);
    }
}
