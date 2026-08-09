using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Protocol;

/// <summary>
/// Active-viewer roster entry for <see cref="FocusChannelResult"/> and <see cref="ViewersChangedDto"/>.
/// <para>
/// <see cref="Profile"/> is the viewer's flair, resolved in-memory by
/// <see cref="Chats.ViewerResolver"/> from the same <c>ConnectionMapping</c> entry that
/// <c>ChatHub.BuildSenderSnapshot</c> reads for message flair — so a user's roster avatar and their
/// message avatar are the same value by construction, never merely by convention. NULL only for a
/// viewer whose live session or connection entry vanished mid-call (a teardown race); clients render
/// their default avatar for that case. No default value — every construction site must decide
/// explicitly whether the entry carries flair or is deliberately flairless (pass <c>null</c>).
/// </para>
/// </summary>
public record ChannelViewerDto(string BattleTag, string Name, ChatProfile Profile);
