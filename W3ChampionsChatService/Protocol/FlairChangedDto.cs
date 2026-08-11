using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Protocol;

/// <summary>
/// A live flair update for one player, pushed when website-backend reports that their portrait, chat
/// colour, chat icons or clan changed.
/// <para>
/// <see cref="Profile"/> is built by the same <see cref="ChatProfileMapper.FromChatUser"/> that
/// supplies roster flair and message <c>sender.flair</c>, so a live update cannot render differently
/// from what a fresh roster would have shown. It is never null: this event is only emitted after a
/// resolution that was confirmed fresh from website-backend.
/// </para>
/// </summary>
public record FlairChangedDto(string BattleTag, ChatProfile Profile);
