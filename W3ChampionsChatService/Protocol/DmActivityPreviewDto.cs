namespace W3ChampionsChatService.Protocol;

/// <summary>
/// The <see cref="ChannelActivityDto.Preview"/> payload for an accepted <c>Dm</c> channel's coalesced
/// activity push (spec §7 — "DMs additionally carry a preview"; C5 plan decision D15/T9). Populated
/// ONLY for <c>Dm</c> channels (OQ-7, strict scope): <c>GroupDm</c>/<c>Public</c>/<c>System</c> activity
/// always carries a null <see cref="ChannelActivityDto.Preview"/> — groups get plain activity.
/// <see cref="SenderBattleTag"/>/<see cref="SenderName"/> are the persisted message's sender snapshot
/// (no extra lookup — reused from the same source the focused <c>MessageReceived</c> delivery already
/// built), and <see cref="Excerpt"/> is the message content's first
/// <see cref="Domain.ChatLimits.DmPreviewExcerptLength"/> characters (mention-inbox "~120 chars"
/// precedent, spec §5) — a plain bounded substring, no word-boundary trimming.
/// </summary>
public record DmActivityPreviewDto(string SenderBattleTag, string SenderName, string Excerpt);
