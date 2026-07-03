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
    IMuteRepository muteRepository,
    IChatAuthenticationService chatAuthenticationService,
    OnlineMemberRegistry onlineMemberRegistry,
    ConnectionMapping connectionMapping)
{
    // The only EPermission values the client is ever told about (explicit allow-list — see
    // OwnProfileDto's boundary-privacy doc). Extend deliberately, one at a time, as new
    // chat-relevant permissions are introduced; never widen this to "everything".
    private static readonly IReadOnlySet<EPermission> ChatRelevantPermissions =
        new HashSet<EPermission> { EPermission.Moderation };

    public async Task<(SessionStateDto Dto, MuteStatus MuteStatus)> AssembleAndSeed(
        W3CUserAuthentication identity, string connectionId, DateTime now)
    {
        var memberships = await membershipRepository.LoadForUser(identity.BattleTag);
        var channelsById = (await channelRepository.LoadByIds(memberships.Select(m => m.ChannelId)))
            .ToDictionary(c => c.Id);
        var publicCatalog = await channelRepository.LoadAllOfType(ChannelType.Public);
        var mutedPlayer = await muteRepository.GetMutedPlayer(identity.BattleTag);
        var chatUser = await chatAuthenticationService.GetUserFromIdentity(identity);

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

        var dto = new SessionStateDto(
            Channels: channelBackedMemberships
                .Select(m => ToChannelDto(channelsById[m.ChannelId], m))
                .ToList(),
            PublicCatalog: effectivePublicCatalog,
            PendingDmRequests: Array.Empty<object>(),
            MentionUnreadCount: 0,
            OwnProfile: ToOwnProfileDto(identity, chatUser),
            MuteState: ToMuteStateDto(muteStatus, mutedPlayer));

        SeedOnlineMemberRegistry(connectionId, identity.BattleTag, channelBackedMemberships);
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

    private static ChannelDto ToChannelDto(ChatChannel channel, ChannelMembership membership)
    {
        var unreadCount = Math.Max(0L, channel.LastSeq - membership.LastReadSeq);
        return new ChannelDto(
            channel,
            MembershipDto.From(membership),
            unreadCount,
            unreadCount > 0);
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

    // ChatUser doesn't yet carry league/rank/gamesPlayed — ChatProfile's own doc marks those as
    // additive ranking enrichment (C6 directory / W1 wb endpoint); left null until that lands.
    private static ChatProfile ToChatProfile(ChatUser chatUser) => new()
    {
        ClanId = chatUser.ClanTag,
        ProfilePicture = chatUser.ProfilePicture,
        ChatColor = chatUser.ChatColor,
        ChatIcons = chatUser.ChatIcons,
    };

    // SECURITY: shadow bans must never surface to the client — only a FULL ban exposes {endDate}.
    // No mute / expired mute (muteStatus == None) also yields null.
    private static MuteStateDto ToMuteStateDto(MuteStatus status, LoungeMute mute) =>
        status == MuteStatus.Full ? new MuteStateDto(mute.endDate) : null;

    private void SeedOnlineMemberRegistry(string connectionId, string battleTag, List<ChannelMembership> channelBackedMemberships)
    {
        // channelBackedMemberships is already filtered to rows whose channel exists (same filter the
        // DTO's Channels list uses) — the registry's channel set must match the DTO's exactly, so
        // nothing ever fans out to a channel with no row. Materialized (ToList) before crossing into
        // the registry's locked Seed — Seed enumerates its argument while holding the lock
        // (FanOut/OnlineMemberRegistry.cs carry-forward note).
        var seed = channelBackedMemberships
            .Select(m => (m.ChannelId, new MemberState(battleTag, m.NotificationLevel, m.LastReadSeq)))
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
