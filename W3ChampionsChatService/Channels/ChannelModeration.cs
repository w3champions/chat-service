using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Channels;

/// <summary>
/// The single moderation "scope wall" predicate (spec §10 + plan D5/D6/D9): a channel is IN SCOPE for
/// moderator actions — <c>ChatHub.DeleteMessage</c>, <c>ChatHub.PurgeMessagesFromUser</c>, and the REST
/// moderation-history message read (<c>Messages/ModerationHistoryController.cs</c>) — iff it is
/// <see cref="ChannelType.Public"/>, <see cref="ChannelType.SemiPublic"/>, or
/// <see cref="ChannelType.System"/> with <see cref="SystemChannelKind.Match"/>. DM/GroupDm,
/// System+Clan, and System+Lobby are NEVER in scope — a moderator never touches private/clan/lobby
/// content; the TTL cleans those.
/// <para>
/// Extracted to ONE definition (C4 Task 7, replacing the hub-private <c>IsPurgeableChannel</c>) so the
/// hub and the REST surface can never drift apart on what a moderator is allowed to touch. The REST
/// channel-LIST query (<see cref="ChannelRepository.LoadModeratableChannels"/>) mirrors this exact
/// three-shape eligibility as a Mongo filter (a C# predicate can't be pushed into a query) — keep both
/// in sync if this ever changes.
/// </para>
/// </summary>
public static class ChannelModeration
{
    public static bool IsModeratable(ChatChannel channel) =>
        channel.Type == ChannelType.Public
        || channel.Type == ChannelType.SemiPublic
        || (channel.Type == ChannelType.System && channel.SystemKind == SystemChannelKind.Match);

    /// <summary>
    /// The MUTE scope wall — the second, deliberately NARROWER wall (<c>ChatHub.SendMessage</c> step 6):
    /// a lounge mute (full or shadow) is enforced on a send iff the channel is
    /// <see cref="ChannelType.Public"/>, or it is a LADDER match room
    /// (<see cref="ChannelType.System"/> + <see cref="SystemChannelKind.Match"/> +
    /// <see cref="ChatChannel.Ladder"/>). Everything else — SemiPublic, DM/GroupDm, System+Clan,
    /// System+Lobby, and a CUSTOM-GAME match room — is exempt, preserving the legacy mute scope.
    /// <para>
    /// WHY THIS IS NOT <see cref="IsModeratable"/>: the two walls answer different questions and are
    /// intentionally not the same set. IsModeratable asks "may a moderator reach INTO this room after
    /// the fact" (delete/purge/read history) and includes SemiPublic and EVERY match room. This asks
    /// "is a muted user silenced while typing HERE", which is a product decision about where a mute
    /// bites: ladder chat is competitive-integrity surface and is gated; a custom lobby is the host's
    /// own room and is not (an explicit product call — a muted player can still talk to the friends
    /// who invited them). Keeping them separate is the point; do not collapse them.
    /// </para>
    /// </summary>
    public static bool IsMuteEnforced(ChatChannel channel) =>
        channel.Type == ChannelType.Public
        || (channel.Type == ChannelType.System
            && channel.SystemKind == SystemChannelKind.Match
            && channel.Ladder);
}
