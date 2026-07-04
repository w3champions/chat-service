using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
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
    /// <item>STRANGER path: the target must have a <c>user_directory</c> row (D14) else
    /// <see cref="ChatResultCode.NotFound"/>. The target's <see cref="DmPrivacy"/> gates creation:
    /// <see cref="DmPrivacy.Friends"/>/<see cref="DmPrivacy.Nobody"/> → <see cref="ChatResultCode.PermissionDenied"/>
    /// (deliberately reveals the target's privacy setting). An EXISTING shell (by pair-key) skips the cap
    /// and the tracker entirely and returns Ok. A genuinely NEW shell is capped: at/over
    /// <see cref="ChatLimits.StrangerDmInitiationCap"/> active initiations in the 8h window →
    /// <see cref="ChatResultCode.Throttled"/> (retry-after from the tracker); otherwise create the pending
    /// shell, record the initiation (only if THIS call actually inserted the shell — a concurrent upsert
    /// that returned an already-existing doc is not double-counted), ensure the caller's membership, seed
    /// the registry, return Ok.</item>
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

        // Self-DM is user-reachable → a typed PermissionDenied (case-insensitive: battleTags carry live
        // casing over the wire but resolve to the same identity).
        if (string.Equals(caller, battleTag, StringComparison.OrdinalIgnoreCase))
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
        if (snapshot.IsFriendWith(battleTag))
        {
            var channel = await _channelRepository.FindOrCreateDm(
                caller, battleTag, initiator: caller, DmRequestState.Accepted, now);
            var membership = await EnsureCallerMembership(channel.Id, caller, now);
            return new OpenDmResult(ChatResultCode.Ok, Channel: channel, Membership: membership);
        }

        // Not a proven friend. Taking the stranger path on a STALE snapshot would risk treating a
        // just-added friend as a stranger — the initiation requires freshness, so fail closed retriable.
        if (!snapshot.IsFresh(now))
        {
            return new OpenDmResult(ChatResultCode.Throttled, ChatLimits.RelationshipRetryAfterSeconds);
        }

        // 5. STRANGER path. D14: a stranger target must exist in the directory (self-healing on first
        // connect) — prevents junk shells and initiation-slot waste for never-seen tags.
        var directoryEntry = await _userDirectory.Load(battleTag);
        if (directoryEntry == null)
        {
            return new OpenDmResult(ChatResultCode.NotFound);
        }

        // dmPrivacy gate (the block is NEVER consulted here — D5). Friends/Nobody reject creation.
        var targetSettings = await _userSettings.LoadOrDefault(battleTag);
        if (targetSettings.DmPrivacy is DmPrivacy.Friends or DmPrivacy.Nobody)
        {
            return new OpenDmResult(ChatResultCode.PermissionDenied);
        }

        // An EXISTING conversation short-circuits the cap AND the tracker: re-opening is not a new
        // initiation, so it never throttles and never double-records.
        var existingShell = await _channelRepository.LoadByPairKey(caller, battleTag);
        if (existingShell != null)
        {
            var membership = await EnsureCallerMembership(existingShell.Id, caller, now);
            return new OpenDmResult(ChatResultCode.Ok, Channel: existingShell, Membership: membership);
        }

        // A genuinely NEW shell is subject to the 8h stranger-initiation cap (fail-closed retriable).
        if (_dmInitiationTracker.CountActive(caller, now) >= ChatLimits.StrangerDmInitiationCap)
        {
            return new OpenDmResult(ChatResultCode.Throttled, _dmInitiationTracker.RetryAfterSeconds(caller, now));
        }

        var created = await _channelRepository.FindOrCreateDm(
            caller, battleTag, initiator: caller, DmRequestState.Pending, now);

        // Record the initiation ONLY if THIS call actually inserted the shell. FindOrCreateDm stamps
        // RequestInitiatedBy via $setOnInsert, so a returned doc whose initiator is the caller was inserted
        // by us; a concurrent open from the other side that won the upsert returns a doc initiated by them
        // — that is not our initiation, so it must not be double-recorded against our cap.
        if (string.Equals(created.RequestInitiatedBy, caller, StringComparison.OrdinalIgnoreCase))
        {
            _dmInitiationTracker.Record(caller, battleTag.Trim().ToLowerInvariant(), now);
        }

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

    /// <summary>
    /// Ensures the CALLER's own membership exists for <paramref name="channelId"/> (idempotent
    /// <see cref="MembershipRepository.InsertIfAbsent"/> — a re-open returns the existing row untouched)
    /// and seeds this connection into the <see cref="OnlineMemberRegistry"/>. DM memberships keep the model
    /// default <see cref="NotificationLevel.All"/> (never flipped) and <see cref="MembershipRole.Member"/>.
    /// The registry seed mirrors <c>JoinChannel</c>'s (ChatHub.Channels.cs); the <c>ChannelType</c> seed
    /// field arrives in T5, so this uses the existing three-field <see cref="MemberState"/> signature.
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
            new MemberState(caller, membership.NotificationLevel, membership.LastReadSeq));

        return membership;
    }
}
