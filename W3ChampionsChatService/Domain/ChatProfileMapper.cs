using W3ChampionsChatService.Chats;

namespace W3ChampionsChatService.Domain;

/// <summary>
/// D9: the single ChatUser→ChatProfile mapping. Before this task the mapping was duplicated in two
/// places — <see cref="Protocol.SessionStateAssembler"/>'s private <c>ToChatProfile</c> (the
/// <see cref="Protocol.SessionStateDto.OwnProfile"/> flair) and <see cref="Chats.ChatHub"/>'s
/// <c>BuildSenderSnapshot</c> (the per-message sender flair snapshot) — both hand-rolling the same
/// four legacy fields and silently omitting the league/rank/games enrichment. Both call sites now
/// delegate here, so they can never drift on which <see cref="ChatUser"/> fields become client-visible
/// flair (<see cref="SenderSnapshot_And_OwnProfile_UseSameMapper"/>-style parity is structural, not
/// re-verified per call site).
/// </summary>
public static class ChatProfileMapper
{
    public static ChatProfile FromChatUser(ChatUser chatUser) => new()
    {
        ClanId = chatUser.ClanTag,
        ProfilePicture = chatUser.ProfilePicture,
        ChatColor = chatUser.ChatColor,
        ChatIcons = chatUser.ChatIcons,
        LeagueId = chatUser.LeagueId,
        LeagueName = chatUser.LeagueName,
        LeagueOrder = chatUser.LeagueOrder,
        LeagueDivision = chatUser.LeagueDivision,
        RankNumber = chatUser.RankNumber,
        GameMode = chatUser.GameMode,
        GateWay = chatUser.GateWay,
        GamesPlayed = chatUser.GamesPlayed,
        Season = chatUser.Season,
    };
}
