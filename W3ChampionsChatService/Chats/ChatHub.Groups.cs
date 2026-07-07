using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Memberships;
using W3ChampionsChatService.Protocol;
using W3ChampionsChatService.Relationships;

namespace W3ChampionsChatService.Chats;

/// <summary>
/// C5 (Task 7): group creation — <see cref="CreateGroup"/> is create-from-scratch ONLY (groups never
/// spawn from a 1:1); T8 adds the mutation surface (add/remove/promote/rename/leave).
/// </summary>
public partial class ChatHub
{
    /// <summary>
    /// Creates a brand-new <see cref="ChannelType.GroupDm"/> with the caller as sole
    /// <see cref="MembershipRole.Owner"/> and <paramref name="members"/> as ordinary
    /// <see cref="MembershipRole.Member"/>s. Every reject is a typed <see cref="CreateGroupResult"/>
    /// (never a silent drop). The resolution order below is LOAD-BEARING and honored EXACTLY:
    /// <list type="number">
    /// <item>Fail-closed identity: no live session → <see cref="ChatResultCode.PermissionDenied"/> (there
    /// is no identity to attribute ownership to).</item>
    /// <item><paramref name="name"/> is trimmed; empty-after-trim OR longer than
    /// <see cref="ChatLimits.GroupNameMaxLength"/> → <see cref="ChatResultCode.TooLong"/> (the pinned
    /// enum has no dedicated "invalid name" value — mirrors C3's empty-send→TooLong precedent).</item>
    /// <item><paramref name="members"/> == null → <see cref="HubException"/> (client-bug mapping, D18).
    /// A null/blank/whitespace ENTRY inside an otherwise non-null array is simply dropped as noise (not a
    /// client-bug signal on its own).</item>
    /// <item>Normalize: trim every entry, de-dupe OrdinalIgnoreCase, and drop the caller's OWN tag if
    /// present (a creator cannot add themselves — they are already the owner).</item>
    /// <item>Size bounds: the TOTAL distinct participant count (caller + distinct members) must fall
    /// within <c>[<see cref="ChatLimits.GroupMinSize"/>, <see cref="ChatLimits.MaxGroupSize"/>]</c>, else
    /// <see cref="ChatResultCode.PermissionDenied"/> — there is no dedicated "too small"/"too large" code
    /// in the pinned enum, so this too mirrors the empty→TooLong mapping precedent (a size violation is
    /// simply "you may not do this").</item>
    /// <item>Friends gate: fetch the CALLER's relationship snapshot, requiring FRESHNESS (unlike the 1:1
    /// delivery block-check, which accepts a stale/last-known snapshot) — an unavailable or stale
    /// snapshot fails closed to <see cref="ChatResultCode.Throttled"/> with
    /// <see cref="ChatLimits.RelationshipRetryAfterSeconds"/> (never a silent "assume friend"/"assume
    /// stranger" decision). Every distinct member must be in <c>snapshot.Friends</c>, else
    /// <see cref="ChatResultCode.PermissionDenied"/>. Friendship alone proves each member's existence — no
    /// separate <c>user_directory</c> check is needed (unlike stranger <c>OpenDm</c>, D14).</item>
    /// <item>Throttle (D13): <see cref="FanOut.ChannelCreationRateLimiter.TryAcquire"/> — the SAME
    /// singleton budget <c>JoinChannel</c>'s implicit semiPublic creation draws from (spec §13 treats
    /// "Group/semi-public creation" as ONE lever). Consumed ONLY after every validation step above has
    /// passed and BEFORE any write (mirrors <c>JoinChannel</c>'s cap-before-throttle ordering) — over the
    /// limit → <see cref="ChatResultCode.Throttled"/> with the window's remaining seconds, no channel
    /// created.</item>
    /// <item>Insert the <see cref="ChatChannel"/> (<see cref="ChannelType.GroupDm"/>,
    /// <see cref="ChatChannel.Name"/> set, <see cref="ChatChannel.NormalizedName"/> deliberately left
    /// NULL — D16: a group name must never collide into <c>LoadAnyByNormalizedName</c>'s join-resolution
    /// path, which would block implicit semiPublic creation of the same display name) with a fresh +1y
    /// shell expiry (<see cref="ExpiryCalculator.ForChannelShell"/>). Insert the CREATOR's membership
    /// FIRST (<see cref="MembershipRole.Owner"/>), stamped <c>JoinedAt = now</c> — equal to (never later
    /// than) every other member's row, since all of them share this same instant — then each member's
    /// (<see cref="MembershipRole.Member"/>), all at <see cref="NotificationLevel.All"/>. Insertion order
    /// carries no ordering semantics of its own: T8's auto-promotion selects its target by earliest
    /// <c>JoinedAt</c>, tie-broken by an ordinal battleTag comparison — never by insert order.</item>
    /// <item><see cref="FanOut.FanOutEngine.PushChannelAdded"/>(focus: false) for the caller AND every
    /// member — "no-auto-open" is pinned, so every push carries <c>Focus == false</c>; an OFFLINE
    /// target is a no-op inside the engine (their next <c>SessionState</c> picks the group up on
    /// connect). This also seeds each online recipient's <see cref="FanOut.OnlineMemberRegistry"/> entry
    /// stamped <see cref="ChannelType.GroupDm"/> (never <see cref="EnsureCallerMembership"/>, which
    /// hardcodes <see cref="ChannelType.Dm"/> — T5 caution).</item>
    /// <item>Return <see cref="CreateGroupResult"/>(<see cref="ChatResultCode.Ok"/>, the new channel, and
    /// ONLY the CALLER's own membership — never another member's row, the D3 leak-wall carried forward
    /// from T2).</item>
    /// </list>
    /// Groups NEVER spawn from a 1:1 — this is the sole group-creation entry point (create-from-scratch
    /// only).
    /// </summary>
    public async Task<CreateGroupResult> CreateGroup(string name, string[] members)
    {
        // 1. Fail-closed identity.
        if (!_sessionRegistry.TryGetByConnectionId(Context.ConnectionId, out var session))
        {
            return new CreateGroupResult(ChatResultCode.PermissionDenied);
        }

        var caller = session.Identity.BattleTag;

        // 2. Name validation — empty-after-trim or over-length maps to TooLong (no dedicated code).
        var trimmedName = name?.Trim();
        if (string.IsNullOrEmpty(trimmedName) || trimmedName.Length > ChatLimits.GroupNameMaxLength)
        {
            return new CreateGroupResult(ChatResultCode.TooLong);
        }

        // 3. Malformed members arg → HubException (client-bug mapping, D18) — thrown BEFORE any
        // relationship read, mirroring OpenDm's null-arg guard.
        if (members == null)
        {
            throw new HubException("CreateGroup requires a non-null members array");
        }

        // 4. Normalize + de-dupe (OrdinalIgnoreCase), dropping blank entries and the caller's own tag.
        var distinctMembers = members
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Trim())
            .Where(m => !string.Equals(m, caller, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 5. Size bounds: total distinct participants (caller + members) within [GroupMinSize, MaxGroupSize].
        var totalSize = distinctMembers.Count + 1;
        if (totalSize < ChatLimits.GroupMinSize || totalSize > ChatLimits.MaxGroupSize)
        {
            return new CreateGroupResult(ChatResultCode.PermissionDenied);
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // 6. Friends gate — FRESH snapshot required (stricter than the 1:1 delivery block-check, which
        // accepts a stale/last-known snapshot). Unavailable/stale ⇒ fail closed retriable.
        RelationshipSnapshot snapshot;
        try
        {
            snapshot = await _relationshipProvider.GetSnapshotAsync(caller);
        }
        catch (RelationshipUnavailableException)
        {
            return new CreateGroupResult(ChatResultCode.Throttled, ChatLimits.RelationshipRetryAfterSeconds);
        }

        if (!snapshot.IsFresh(now))
        {
            return new CreateGroupResult(ChatResultCode.Throttled, ChatLimits.RelationshipRetryAfterSeconds);
        }

        if (distinctMembers.Any(member => !snapshot.IsFriendWith(member)))
        {
            return new CreateGroupResult(ChatResultCode.PermissionDenied);
        }

        // 7. Throttle (D13): the SAME shared budget as implicit semiPublic creation. Consumed only after
        // every validation above has passed, before any write.
        var decision = _channelCreationRateLimiter.TryAcquire(caller, now);
        if (!decision.Allowed)
        {
            return new CreateGroupResult(ChatResultCode.Throttled, decision.RetryAfterSeconds);
        }

        // 8. Insert the channel — NormalizedName is deliberately left null (D16).
        var channel = new ChatChannel
        {
            Type = ChannelType.GroupDm,
            Name = trimmedName,
            LastSeq = 0,
            LastMessageAt = now,
        };
        channel.ExpiresAt = ExpiryCalculator.ForChannelShell(channel, now);
        await _channelRepository.Insert(channel);

        // Insert the CREATOR's membership FIRST (Owner), stamped JoinedAt = now — equal to (never later
        // than) every other member's row, since all of them share this SAME instant. Insertion order here
        // carries no ordering semantics of its own: T8's auto-promotion picks its target by earliest
        // JoinedAt, tie-broken by an ordinal battleTag comparison — never by insert order.
        var creatorMembership = new ChannelMembership
        {
            ChannelId = channel.Id,
            BattleTag = caller,
            Role = MembershipRole.Owner,
            NotificationLevel = NotificationLevel.All,
            JoinedAt = now,
        };
        await _membershipRepository.Insert(creatorMembership);

        var memberMemberships = new List<ChannelMembership>(distinctMembers.Count);
        foreach (var member in distinctMembers)
        {
            var membership = new ChannelMembership
            {
                ChannelId = channel.Id,
                BattleTag = member,
                Role = MembershipRole.Member,
                NotificationLevel = NotificationLevel.All,
                JoinedAt = now,
            };
            await _membershipRepository.Insert(membership);
            memberMemberships.Add(membership);
        }

        // 9. Push ChannelAdded(focus:false) to the caller AND every member (no-auto-open, pinned). Each
        // push also seeds the recipient's OnlineMemberRegistry entry (stamped GroupDm) if they are online
        // — an offline recipient is a no-op; their next SessionState picks the group up on connect.
        await _fanOutEngine.PushChannelAdded(channel, creatorMembership, focus: false);
        foreach (var membership in memberMemberships)
        {
            await _fanOutEngine.PushChannelAdded(channel, membership, focus: false);
        }

        // 10. Return ONLY the caller's own membership (D3 leak-wall, T2 carry-forward).
        return new CreateGroupResult(ChatResultCode.Ok, Channel: channel, Membership: creatorMembership);
    }

    /// <summary>
    /// Adds <paramref name="targetBattleTag"/> to an existing <see cref="ChannelType.GroupDm"/> as an
    /// ordinary <see cref="MembershipRole.Member"/>. This is the ONE egalitarian group mutation available to
    /// ANY member (D12): the adder needs only to be a current member AND the target must be one of the
    /// ADDER's OWN friends. Every reject is a typed <see cref="ChannelOperationResult"/>; the order below is
    /// LOAD-BEARING and honored EXACTLY:
    /// <list type="number">
    /// <item>Fail-closed session → <see cref="ChatResultCode.PermissionDenied"/> (no identity to attribute
    /// the add to).</item>
    /// <item>Load the channel; missing → <see cref="ChatResultCode.NotFound"/>; a
    /// non-<see cref="ChannelType.GroupDm"/> channel → <see cref="ChatResultCode.PermissionDenied"/> (guards
    /// Public/SemiPublic/Dm/System from the group-mutation surface).</item>
    /// <item>Caller-is-member: a zero-DB <see cref="FanOut.OnlineMemberRegistry.IsMember"/> hot check (the
    /// online caller's group membership is seeded at connect) — a non-member →
    /// <see cref="ChatResultCode.PermissionDenied"/>.</item>
    /// <item>Null/empty/whitespace <paramref name="targetBattleTag"/> → <see cref="HubException"/> (client-bug
    /// mapping, D18) — thrown only AFTER the membership check so a non-member never trips it.</item>
    /// <item>Target ALREADY a member (<see cref="Memberships.MembershipRepository.Load"/> non-null) →
    /// idempotent <see cref="ChatResultCode.Ok"/> with NO re-insert and NO push. This short-circuits BEFORE
    /// the size and friends gates (pinned order): a duplicate add is a harmless no-op regardless of current
    /// friendship or group size.</item>
    /// <item>Size cap: <see cref="Memberships.MembershipRepository.CountForChannel"/> ≥
    /// <see cref="ChatLimits.MaxGroupSize"/> → <see cref="ChatResultCode.PermissionDenied"/> (no dedicated
    /// "full" code in the pinned enum — mirrors CreateGroup's size-mapping precedent).</item>
    /// <item>Adder's-OWN-friends gate: the CALLER's relationship snapshot, requiring FRESHNESS (as
    /// CreateGroup does) — unavailable (<see cref="RelationshipUnavailableException"/>) or stale →
    /// <see cref="ChatResultCode.Throttled"/> (fail closed retriable), a target not in
    /// <c>snapshot.Friends</c> → <see cref="ChatResultCode.PermissionDenied"/>. There is deliberately NO
    /// block check here (D14: group anti-abuse is leave+unfriend, never a re-add guard).</item>
    /// <item>Insert the membership IDEMPOTENTLY (<see cref="Memberships.MembershipRepository.InsertIfAbsent"/>
    /// — <see cref="MembershipRole.Member"/>, <see cref="NotificationLevel.All"/>, <c>JoinedAt = now</c>) so
    /// two concurrent adds of the SAME new friend (both of which read <c>null</c> at the already-member
    /// short-circuit above and then race the <c>ux_channelId_battleTag</c> unique index) BOTH resolve to
    /// <see cref="ChatResultCode.Ok"/> instead of one surfacing a raw E11000 duplicate-key write, then
    /// <see cref="FanOut.FanOutEngine.PushChannelAdded"/>(focus:false) — no-auto-open, seeding the added
    /// member's registry when online (offline → their next SessionState surfaces the group). Never
    /// <see cref="EnsureCallerMembership"/> (which hardcodes <see cref="ChannelType.Dm"/>).</item>
    /// </list>
    /// </summary>
    public async Task<ChannelOperationResult> AddGroupMember(string channelId, string targetBattleTag)
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
        if (channel.Type != ChannelType.GroupDm)
        {
            return new ChannelOperationResult(ChatResultCode.PermissionDenied);
        }

        // Any member may add (D12) — a zero-DB registry hot check. A non-member is denied WITHOUT any
        // target-existence disclosure (the target arg is not even read yet).
        if (!_onlineMemberRegistry.IsMember(Context.ConnectionId, channelId))
        {
            return new ChannelOperationResult(ChatResultCode.PermissionDenied);
        }

        // Malformed target arg → client-bug (D18). After the membership check so a non-member never trips it.
        if (string.IsNullOrWhiteSpace(targetBattleTag))
        {
            throw new HubException("AddGroupMember requires a non-empty battleTag");
        }

        var target = targetBattleTag.Trim();

        // Idempotent already-a-member short-circuit — BEFORE the size/friends gates (pinned order): a
        // duplicate add is a no-op Ok regardless of current friendship or group size, and pushes nothing.
        var existing = await _membershipRepository.Load(channelId, target);
        if (existing != null)
        {
            return new ChannelOperationResult(ChatResultCode.Ok);
        }

        // Size cap (no dedicated "full" code — mirrors CreateGroup's size→PermissionDenied mapping).
        if (await _membershipRepository.CountForChannel(channelId) >= ChatLimits.MaxGroupSize)
        {
            return new ChannelOperationResult(ChatResultCode.PermissionDenied);
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // Adder's-OWN-friends gate — FRESH snapshot required (stricter than the 1:1 delivery block-check).
        // NO block check (D14). Unavailable/stale ⇒ fail closed retriable; non-friend ⇒ PermissionDenied.
        RelationshipSnapshot snapshot;
        try
        {
            snapshot = await _relationshipProvider.GetSnapshotAsync(caller);
        }
        catch (RelationshipUnavailableException)
        {
            return new ChannelOperationResult(ChatResultCode.Throttled, ChatLimits.RelationshipRetryAfterSeconds);
        }
        if (!snapshot.IsFresh(now))
        {
            return new ChannelOperationResult(ChatResultCode.Throttled, ChatLimits.RelationshipRetryAfterSeconds);
        }
        if (!snapshot.IsFriendWith(target))
        {
            return new ChannelOperationResult(ChatResultCode.PermissionDenied);
        }

        var membership = new ChannelMembership
        {
            ChannelId = channelId,
            BattleTag = target,
            Role = MembershipRole.Member,
            NotificationLevel = NotificationLevel.All,
            JoinedAt = now,
        };
        // Idempotent insert (SEC-Low-2): two concurrent AddGroupMember calls for the same new friend both
        // pass the already-member short-circuit above (each read null), then race the ux_channelId_battleTag
        // unique index. Plain Insert would let one caller hit an unhandled E11000 MongoWriteException;
        // InsertIfAbsent resolves both to the existing-or-inserted row (never null on success), so both
        // return Ok. Push the returned row (equivalent to the locally-built one) when present.
        var inserted = await _membershipRepository.InsertIfAbsent(membership);
        await _fanOutEngine.PushChannelAdded(channel, inserted ?? membership, focus: false);

        return new ChannelOperationResult(ChatResultCode.Ok);
    }

    /// <summary>
    /// Removes <paramref name="targetBattleTag"/> from a <see cref="ChannelType.GroupDm"/> — an OWNER-ONLY
    /// forced removal. The order below is LOAD-BEARING and SECURE (the owner check runs FIRST so a non-owner
    /// never learns whether the target exists — the NotFound-vs-PermissionDenied ordering is deliberate):
    /// <list type="number">
    /// <item>Fail-closed session → <see cref="ChatResultCode.PermissionDenied"/>.</item>
    /// <item>Load channel; missing → <see cref="ChatResultCode.NotFound"/>; non-GroupDm →
    /// <see cref="ChatResultCode.PermissionDenied"/>.</item>
    /// <item>Load the CALLER's OWN membership row (owner checks read the caller's Mongo row — the in-memory
    /// registry carries NO role, D12); a null row or a non-<see cref="MembershipRole.Owner"/> role →
    /// <see cref="ChatResultCode.PermissionDenied"/>. This is FIRST on purpose: a non-owner gets the identical
    /// <see cref="ChatResultCode.PermissionDenied"/> whether the target exists or not (no existence oracle).</item>
    /// <item>Null/empty target → <see cref="HubException"/> (D18) — after the owner gate.</item>
    /// <item>Load the TARGET membership; missing → <see cref="ChatResultCode.NotFound"/> (only an owner ever
    /// reaches this existence signal — that is WHY the owner gate precedes it).</item>
    /// <item>Target <see cref="MembershipRole.Owner"/> → <see cref="ChatResultCode.PermissionDenied"/> — the
    /// PINNED anti-coup wall: owners can NEVER remove owners, INCLUDING self (an owner cannot self-remove via
    /// RemoveGroupMember; owners exit only via <see cref="LeaveChannel"/>, which auto-promotes if they were
    /// the last owner).</item>
    /// <item>Delete the target membership, then <see cref="FanOut.FanOutEngine.PushChannelRemoved"/> — the
    /// target did NOT initiate the departure, so they need the <c>ChannelRemoved</c> event plus registry +
    /// focus cleanup. Per D11 this is DELIBERATELY NOT routed through
    /// <see cref="FanOut.ViewersAccumulator.RecordChange"/> (Dm/GroupDm are excluded from the viewer-roster
    /// system — a forced private-lane removal emits no <c>ViewersChanged</c>), which structurally satisfies
    /// the C3 amendment for private lanes (OQ-1). No empty-group check is needed — the caller is an owner who
    /// remains.</item>
    /// </list>
    /// </summary>
    public async Task<ChannelOperationResult> RemoveGroupMember(string channelId, string targetBattleTag)
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
        if (channel.Type != ChannelType.GroupDm)
        {
            return new ChannelOperationResult(ChatResultCode.PermissionDenied);
        }

        // Owner gate FIRST — a non-owner cannot distinguish an existing vs absent target (owner reads the
        // caller's OWN Mongo row; the registry carries no role, D12).
        var callerMembership = await _membershipRepository.Load(channelId, caller);
        if (callerMembership == null || callerMembership.Role != MembershipRole.Owner)
        {
            return new ChannelOperationResult(ChatResultCode.PermissionDenied);
        }

        if (string.IsNullOrWhiteSpace(targetBattleTag))
        {
            throw new HubException("RemoveGroupMember requires a non-empty battleTag");
        }

        var target = targetBattleTag.Trim();

        var targetMembership = await _membershipRepository.Load(channelId, target);
        if (targetMembership == null)
        {
            return new ChannelOperationResult(ChatResultCode.NotFound);
        }

        // The PINNED anti-coup wall: owners can never remove owners — INCLUDING self.
        if (targetMembership.Role == MembershipRole.Owner)
        {
            return new ChannelOperationResult(ChatResultCode.PermissionDenied);
        }

        await _membershipRepository.Delete(channelId, target);
        // Forced removal: the target needs the event + registry/focus cleanup. NO RecordChange (D11).
        await _fanOutEngine.PushChannelRemoved(channelId, target);

        return new ChannelOperationResult(ChatResultCode.Ok);
    }

    /// <summary>
    /// Promotes <paramref name="targetBattleTag"/> to a co-<see cref="MembershipRole.Owner"/> of a
    /// <see cref="ChannelType.GroupDm"/> — OWNER-ONLY, and ADDITIVE (the owner set is egalitarian; the
    /// promoter stays an owner). Order (owner-first, mirroring
    /// <see cref="RemoveGroupMember"/>'s NotFound-vs-PermissionDenied ordering rationale):
    /// <list type="number">
    /// <item>Fail-closed session → <see cref="ChatResultCode.PermissionDenied"/>.</item>
    /// <item>Load channel; missing → <see cref="ChatResultCode.NotFound"/>; non-GroupDm →
    /// <see cref="ChatResultCode.PermissionDenied"/>.</item>
    /// <item>Caller's own membership must be <see cref="MembershipRole.Owner"/> (loaded from Mongo — the
    /// registry carries no role, D12) else <see cref="ChatResultCode.PermissionDenied"/>. Owner-first, so a
    /// non-owner never learns whether the target exists.</item>
    /// <item>Null/empty target → <see cref="HubException"/> (D18).</item>
    /// <item>Target membership missing → <see cref="ChatResultCode.NotFound"/>.</item>
    /// <item>Target already <see cref="MembershipRole.Owner"/> → idempotent <see cref="ChatResultCode.Ok"/>
    /// (safe to replay).</item>
    /// <item>Otherwise <see cref="Memberships.MembershipRepository.SetRole"/>(<see cref="MembershipRole.Owner"/>)
    /// → <see cref="ChatResultCode.Ok"/>. No live event is pinned — the promoted member learns their new role
    /// on their next SessionState (L3 handoff).</item>
    /// </list>
    /// </summary>
    public async Task<ChannelOperationResult> PromoteOwner(string channelId, string targetBattleTag)
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
        if (channel.Type != ChannelType.GroupDm)
        {
            return new ChannelOperationResult(ChatResultCode.PermissionDenied);
        }

        var callerMembership = await _membershipRepository.Load(channelId, caller);
        if (callerMembership == null || callerMembership.Role != MembershipRole.Owner)
        {
            return new ChannelOperationResult(ChatResultCode.PermissionDenied);
        }

        if (string.IsNullOrWhiteSpace(targetBattleTag))
        {
            throw new HubException("PromoteOwner requires a non-empty battleTag");
        }

        var target = targetBattleTag.Trim();

        var targetMembership = await _membershipRepository.Load(channelId, target);
        if (targetMembership == null)
        {
            return new ChannelOperationResult(ChatResultCode.NotFound);
        }

        // Idempotent: promoting an already-owner is a safe replay.
        if (targetMembership.Role == MembershipRole.Owner)
        {
            return new ChannelOperationResult(ChatResultCode.Ok);
        }

        await _membershipRepository.SetRole(channelId, target, MembershipRole.Owner);
        return new ChannelOperationResult(ChatResultCode.Ok);
    }

    /// <summary>
    /// Renames a <see cref="ChannelType.GroupDm"/> — OWNER-ONLY. Order:
    /// <list type="number">
    /// <item>Fail-closed session → <see cref="ChatResultCode.PermissionDenied"/>.</item>
    /// <item>Load channel; missing → <see cref="ChatResultCode.NotFound"/>; non-GroupDm →
    /// <see cref="ChatResultCode.PermissionDenied"/>.</item>
    /// <item>Caller's own membership must be <see cref="MembershipRole.Owner"/> else
    /// <see cref="ChatResultCode.PermissionDenied"/>.</item>
    /// <item>Validate the name with the SAME rules as <c>CreateGroup</c> (T7): trim, then empty-after-trim OR
    /// longer than <see cref="ChatLimits.GroupNameMaxLength"/> → <see cref="ChatResultCode.TooLong"/> (no
    /// dedicated "invalid name" value in the pinned enum).</item>
    /// <item><see cref="Channels.ChannelRepository.SetName"/> — <c>$set</c> the display
    /// <see cref="ChatChannel.Name"/> ONLY, NEVER <see cref="ChatChannel.NormalizedName"/> (D16: a group name
    /// must never collide into <c>LoadAnyByNormalizedName</c>'s join-resolution path). No live event is
    /// pinned — clients learn the new name on their next SessionState (L3 handoff).</item>
    /// </list>
    /// </summary>
    public async Task<ChannelOperationResult> RenameGroup(string channelId, string name)
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
        if (channel.Type != ChannelType.GroupDm)
        {
            return new ChannelOperationResult(ChatResultCode.PermissionDenied);
        }

        var callerMembership = await _membershipRepository.Load(channelId, caller);
        if (callerMembership == null || callerMembership.Role != MembershipRole.Owner)
        {
            return new ChannelOperationResult(ChatResultCode.PermissionDenied);
        }

        // Same name rules as CreateGroup (T7): trim, empty-after-trim or over-length → TooLong.
        var trimmedName = name?.Trim();
        if (string.IsNullOrEmpty(trimmedName) || trimmedName.Length > ChatLimits.GroupNameMaxLength)
        {
            return new ChannelOperationResult(ChatResultCode.TooLong);
        }

        // $set Name ONLY — NEVER NormalizedName (D16).
        await _channelRepository.SetName(channelId, trimmedName);
        return new ChannelOperationResult(ChatResultCode.Ok);
    }
}
