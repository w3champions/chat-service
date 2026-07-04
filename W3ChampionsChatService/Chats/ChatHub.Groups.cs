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
    /// FIRST (<see cref="MembershipRole.Owner"/>) — stamped before every other member's row for
    /// deterministic auto-promotion ordering (T8) — then each member's (<see cref="MembershipRole.Member"/>),
    /// all at <see cref="NotificationLevel.All"/> and the SAME <c>JoinedAt</c> instant.</item>
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

        // Insert the CREATOR's membership FIRST (Owner) — stamped before every other member's row for
        // deterministic auto-promotion ordering (T8), even though every JoinedAt here is the SAME instant.
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
}
