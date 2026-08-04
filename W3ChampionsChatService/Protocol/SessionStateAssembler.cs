using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.FanOut;
using W3ChampionsChatService.Memberships;
using W3ChampionsChatService.Mentions;
using W3ChampionsChatService.Messages;
using W3ChampionsChatService.Mutes;

namespace W3ChampionsChatService.Protocol;

/// <summary>
/// Builds the <see cref="SessionStateDto"/> snapshot on every (re)connect — the single source of
/// truth the client rebuilds its whole UI from (spec acceptance 8) — and seeds the two pieces of
/// per-connection server-side state that only exist from this point on: the
/// <see cref="OnlineMemberRegistry"/> (fan-out targeting) and the legacy
/// <see cref="ConnectionMapping"/> mute cache (so <c>MuteReconciliationService</c> keeps reaching
/// this connection for live ban/unban pushes).
/// <para>
/// Deliberately does NOT touch SignalR/<c>IHubContext</c> — it only assembles and seeds; the actual
/// push of <c>SessionState</c> (and, on a full ban, the legacy <c>PlayerBannedFromChat</c>) is the
/// connect path's job (Task 8), which is why <see cref="AssembleAndSeed"/> returns the resolved
/// <see cref="MuteStatus"/> alongside the DTO — the DTO's own <see cref="SessionStateDto.MuteState"/>
/// already carries the endDate a full-ban push needs.
/// </para>
/// BOUNDARY-PRIVACY CRITICAL: never serializes the raw <c>W3CUserAuthentication</c> permission
/// snapshot or the mute shadow flag — see <see cref="ToOwnProfileDto"/> and
/// <see cref="ToMuteStateDto"/>.
/// </summary>
public class SessionStateAssembler(
    MembershipRepository membershipRepository,
    ChannelRepository channelRepository,
    MessageRepository messageRepository,
    IMuteRepository muteRepository,
    OnlineMemberRegistry onlineMemberRegistry,
    ConnectionMapping connectionMapping,
    // C6 (Task 6, D6): backs the real MentionUnreadCount below (replaces the C3 hardcoded-0 stub).
    MentionInboxRepository mentionInboxRepository)
{
    // The only EPermission values the client is ever told about (explicit allow-list — see
    // OwnProfileDto's boundary-privacy doc). Extend deliberately, one at a time, as new
    // chat-relevant permissions are introduced; never widen this to "everything".
    private static readonly IReadOnlySet<EPermission> ChatRelevantPermissions =
        new HashSet<EPermission> { EPermission.Moderation };

    // Follow-up spec §3: position of each seeded room in the hardcoded catalog (DefaultChatRooms.Rooms,
    // "W3C Lounge" first), keyed by normalized name. Computed once — ordering and seeding read the SAME
    // constant, so the contract "catalog order == seed list order" cannot drift.
    private static readonly IReadOnlyDictionary<string, int> CatalogOrder = DefaultChatRooms.Rooms
        .Select((name, index) => (Name: ChannelNames.Normalize(name), Index: index))
        .ToDictionary(x => x.Name, x => x.Index);

    /// <summary>
    /// Orders the public catalog deterministically: seed-list position first (follow-up spec §3 —
    /// "catalog order == seed order"); any public channel whose name is NOT in the seed list (legacy
    /// leftover) sorts after all seeded rooms, alphabetically by normalized name.
    /// </summary>
    internal static List<ChatChannel> OrderByCatalog(List<ChatChannel> channels) => channels
        .OrderBy(c => CatalogOrder.TryGetValue(c.NormalizedName ?? string.Empty, out var index) ? index : int.MaxValue)
        .ThenBy(c => c.NormalizedName, StringComparer.Ordinal)
        .ToList();

    // D9: chatUser is now RESOLVED BY THE CALLER (ChatHub, hoisted) and handed straight through — this
    // method no longer calls IChatAuthenticationService.GetUserFromIdentity itself. Before this change
    // the connect path resolved the flair TWICE per connect (once here, once again for the connect-time
    // directory upsert); hoisting the ONE resolution into the hub and threading it through here (and
    // into the directory upsert) means a single wb round-trip serves both.
    public async Task<(SessionStateDto Dto, MuteStatus MuteStatus)> AssembleAndSeed(
        W3CUserAuthentication identity, string connectionId, DateTime now, ChatUser chatUser)
    {
        var memberships = await membershipRepository.LoadForUser(identity.BattleTag);
        var channelsById = (await channelRepository.LoadByIds(memberships.Select(m => m.ChannelId)))
            .ToDictionary(c => c.Id);
        var publicCatalog = OrderByCatalog(await channelRepository.LoadAllOfType(ChannelType.Public));
        var mutedPlayer = await muteRepository.GetMutedPlayer(identity.BattleTag);

        var muteStatus = ResolveMuteStatus(mutedPlayer, now);

        // Full-ban room-scope rule: the public catalog (name-joinable rooms the caller isn't already a
        // member of) is hidden on a full-ban connect. Existing memberships (Channels below) are
        // untouched — a full-banned user keeps the rooms they're already in.
        IReadOnlyList<ChatChannel> effectivePublicCatalog = muteStatus == MuteStatus.Full
            ? Array.Empty<ChatChannel>()
            : publicCatalog;

        // Channel-backed memberships only (row exists AND the channel it points at still exists) —
        // the single filtered set both the DTO's Channels list and the OnlineMemberRegistry seed are
        // built from, so a membership orphaned by a deleted channel never fans out to a channel with
        // no row even though it doesn't reach the client either.
        var channelBackedMemberships = memberships
            .Where(m => channelsById.ContainsKey(m.ChannelId))
            .ToList();

        // D7 (Amendment 3): unread is computed per channel as the COUNT of user-visible rows after the
        // member's read cursor (see ToChannelDto) — an async, index-bounded Mongo count per membership.
        // Sequential await (not Task.WhenAll): memberships are bounded (the public+semiPublic cap plus
        // DMs/groups/match), and each count is a fast indexed range count, so the simpler sequential loop
        // is preferred over parallelizing across the Mongo connection pool.
        var channelDtos = new List<ChannelDto>(channelBackedMemberships.Count);
        foreach (var membership in channelBackedMemberships)
        {
            channelDtos.Add(await ToChannelDto(channelsById[membership.ChannelId], membership, identity.BattleTag));
        }

        // C6 (Task 6, D6): the live unread-mention count — CountUnread(ReadAt == null). identity.BattleTag
        // is passed straight through (JWT-cased); the repository normalizes it to the lowercased
        // mention-inbox key convention internally (mirrors MembershipRepository's call sites above).
        var mentionUnreadCount = await mentionInboxRepository.CountUnread(identity.BattleTag);

        var dto = new SessionStateDto(
            Channels: channelDtos,
            PublicCatalog: effectivePublicCatalog,
            PendingDmRequests: BuildPendingDmTray(channelBackedMemberships, channelsById, identity.BattleTag, now),
            MentionUnreadCount: (int)mentionUnreadCount,
            OwnProfile: ToOwnProfileDto(identity, chatUser),
            MuteState: ToMuteStateDto(muteStatus, mutedPlayer));

        SeedOnlineMemberRegistry(connectionId, identity.BattleTag, channelBackedMemberships, channelsById);
        SeedLegacyMuteCache(connectionId, chatUser, muteStatus, mutedPlayer);

        return (dto, muteStatus);
    }

    private static MuteStatus ResolveMuteStatus(LoungeMute mute, DateTime now)
    {
        // An absent OR expired mute is treated as no mute (LoungeMute.IsActive is the single
        // source of truth for the expiry rule).
        if (mute == null || !mute.IsActive(now)) return MuteStatus.None;
        return mute.isShadowBan ? MuteStatus.Shadow : MuteStatus.Full;
    }

    private async Task<ChannelDto> ToChannelDto(ChatChannel channel, ChannelMembership membership, string viewerBattleTag)
    {
        // D7 (Amendment 3): unread is the COUNT of USER-VISIBLE rows after the member's read cursor —
        // NOT channel.LastSeq − membership.LastReadSeq. That raw-seq delta counts INVISIBLE rows
        // (foreign-author shadow rows + soft-deleted rows), so on reconnect it produced PHANTOM unread
        // for a shadow-banned author's message or a purged message — the exact defect pinned acceptance 2
        // ("shadow messages generate NO unread for others") forbids. CountUserVisibleAfter applies the
        // UserVisible predicate (Deleted == null AND (Shadow == false OR sender == viewer)) with
        // Seq > LastReadSeq, index-bounded on ux_channelId_seq. The viewer's OWN shadow rows still count
        // toward THEIR own unread (via the sender == viewer disjunct) — the symmetric illusion.
        //
        // KNOWN LIVE-PATH RESIDUAL (documented here, deliberately NOT fixed — a launcher/L4 concern, out
        // of C4 scope, and self-healing): this fixes the CONNECT-time snapshot only. A shadow/soft-deleted
        // row still advances channel.LastSeq. The server pushes NO ChannelActivity for a shadow message
        // (C3 constraint), but when a LATER real message fires ChannelActivity{lastSeq}, the client's live
        // math (lastSeq − lastReadSeq, spec §7) transiently OVER-counts by the number of invisible rows in
        // the unread gap — until the next MarkRead (sets lastReadSeq to the max VISIBLE seq the member
        // rendered → the count returns to correct) or the next reconnect (re-baselines via this D7
        // snapshot). Bounded, rare (shadow bans are rare), and self-healing.
        var unreadCount = await messageRepository.CountUserVisibleAfter(channel.Id, viewerBattleTag, membership.LastReadSeq);
        return new ChannelDto(
            channel,
            MembershipDto.From(membership),
            unreadCount,
            unreadCount > 0);
    }

    /// <summary>
    /// C5 T6 — the pending-Dm-request tray (spec §11 SessionState slot). Built ENTIRELY from the
    /// already-loaded <paramref name="channelBackedMemberships"/> + <paramref name="channelsById"/> (zero
    /// extra Mongo reads): the connecting viewer sees one <see cref="PendingDmRequestDto"/> per channel that
    /// is a <see cref="ChannelType.Dm"/> whose <see cref="ChatChannel.RequestState"/> is
    /// <see cref="DmRequestState.Pending"/>, was initiated by SOMEONE ELSE (<see cref="ChatChannel.RequestInitiatedBy"/>
    /// != the viewer, case-insensitive — the viewer's OWN outgoing requests never appear here), and is NOT
    /// currently decline-suppressed (the viewer's own membership <see cref="ChannelMembership.DeclinedUntil"/>
    /// is null or already elapsed — D3's soft+temporal decline: still Pending on the channel doc, hidden from
    /// the tray for the 24h window). <see cref="PendingDmRequestDto.RequestedAt"/> is the channel's last
    /// message time, falling back to the membership's join time for a shell with no message yet. The same
    /// pending-recipient channels ALSO remain in the DTO's <see cref="SessionStateDto.Channels"/> (D4
    /// dual-listing) — this tray is additive, never a filter on that list.
    /// </summary>
    private static IReadOnlyList<PendingDmRequestDto> BuildPendingDmTray(
        List<ChannelMembership> channelBackedMemberships,
        IReadOnlyDictionary<string, ChatChannel> channelsById,
        string viewerBattleTag,
        DateTime now)
    {
        var tray = new List<PendingDmRequestDto>();
        foreach (var membership in channelBackedMemberships)
        {
            var channel = channelsById[membership.ChannelId];
            if (channel.Type != ChannelType.Dm || channel.RequestState != DmRequestState.Pending)
            {
                continue;
            }

            // The viewer is the RECIPIENT of the request, never its initiator (they wrote first — it is not
            // a request TO them). RequestInitiatedBy is stored JWT-cased on the channel doc, so compare
            // case-insensitively against the viewer's identity.
            if (string.Equals(channel.RequestInitiatedBy, viewerBattleTag, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Decline suppression: hide while the recipient's own 24h window is still active. The window
            // lives ONLY on this membership row (never the channel doc, never serialized) — D3.
            if (membership.DeclinedUntil.HasValue && membership.DeclinedUntil.Value > now)
            {
                continue;
            }

            tray.Add(new PendingDmRequestDto(
                channel.Id,
                channel.RequestInitiatedBy,
                channel.LastMessageAt ?? membership.JoinedAt));
        }

        return tray;
    }

    private static OwnProfileDto ToOwnProfileDto(W3CUserAuthentication identity, ChatUser chatUser)
    {
        // Explicit projection — NEVER hand the raw IReadOnlySet<EPermission> (or the identity object
        // itself) to the DTO. Only chat-relevant permissions, by name.
        var permissions = identity.Permissions
            .Where(ChatRelevantPermissions.Contains)
            .Select(p => p.ToString())
            .ToList();

        return new OwnProfileDto(identity.BattleTag, identity.Name, identity.IsAdmin, ToChatProfile(chatUser), permissions);
    }

    // D9: delegates to the single shared ChatUser→ChatProfile mapper (Domain/ChatProfileMapper.cs) —
    // also used by ChatHub.BuildSenderSnapshot — so OwnProfile.Flair and the per-message sender
    // snapshot can never drift on which ChatUser fields become client-visible flair.
    private static ChatProfile ToChatProfile(ChatUser chatUser) => ChatProfileMapper.FromChatUser(chatUser);

    // SECURITY: shadow bans must never surface to the client — only a FULL ban exposes {endDate}.
    // No mute / expired mute (muteStatus == None) also yields null.
    private static MuteStateDto ToMuteStateDto(MuteStatus status, LoungeMute mute) =>
        status == MuteStatus.Full ? new MuteStateDto(mute.endDate) : null;

    private void SeedOnlineMemberRegistry(
        string connectionId,
        string battleTag,
        List<ChannelMembership> channelBackedMemberships,
        IReadOnlyDictionary<string, ChatChannel> channelsById)
    {
        // channelBackedMemberships is already filtered to rows whose channel exists (same filter the
        // DTO's Channels list uses) — the registry's channel set must match the DTO's exactly, so
        // nothing ever fans out to a channel with no row. Materialized (ToList) before crossing into
        // the registry's locked Seed — Seed enumerates its argument while holding the lock
        // (FanOut/OnlineMemberRegistry.cs carry-forward note). C5 (Task 5, D11): each entry's
        // ChannelType comes from the already-loaded channelsById map (zero extra Mongo reads) so
        // ChatHub can later zero-DB-lookup whether a (channel, connection) is a Dm/GroupDm private lane.
        var seed = channelBackedMemberships
            .Select(m => (m.ChannelId, new MemberState(battleTag, m.NotificationLevel, m.LastReadSeq, channelsById[m.ChannelId].Type)))
            .ToList();
        onlineMemberRegistry.Seed(connectionId, seed);
    }

    private void SeedLegacyMuteCache(string connectionId, ChatUser chatUser, MuteStatus status, LoungeMute mute)
    {
        // No-room seat (RegisterUser, not Add) — this connection isn't seated in any legacy room by
        // the assembler; it just needs to be reachable by MuteReconciliationService.GetConnectionIdsForUser.
        connectionMapping.RegisterUser(connectionId, chatUser);
        connectionMapping.SetMute(connectionId, status, status == MuteStatus.None ? DateTime.MinValue : mute.endDate);
    }
}
