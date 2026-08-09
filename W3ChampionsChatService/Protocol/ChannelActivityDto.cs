using System;
using W3ChampionsChatService.Channels;

namespace W3ChampionsChatService.Protocol;

/// <summary>
/// The coalesced <c>ChannelActivity</c> push an UNFOCUSED member with notification level
/// <see cref="Domain.NotificationLevel.All"/> receives instead of the full <c>MessageReceived</c>
/// payload (the "no full payloads to unfocused" guardrail — C3 acceptance 1). It carries only the
/// channel and the latest sequence, so coalescing a burst into one push is lossless (a later push
/// with the newest <see cref="LastSeq"/> supersedes any it replaced).
/// <para>
/// <see cref="Preview"/> was a forward-declared slot for C5's DM message preview in C3 (field present in
/// the wire contract, always null). C5 (Task 9, D15) fills it: for an accepted <c>Dm</c> channel's
/// activity it carries a <see cref="DmActivityPreviewDto"/> (sender + a bounded excerpt); for every other
/// channel type (<c>GroupDm</c>/<c>Public</c>/<c>System</c>) it remains null (OQ-7, strict Dm-only
/// scope — groups get plain activity). Typed <c>object</c> deliberately: the wire contract does not
/// commit to a single preview shape, and a null serializes identically regardless of the type.
/// </para>
/// <para>
/// <see cref="SentAt"/> is the <c>SentAt</c> of the message this activity is reporting — the missing
/// half of the conversation-list contract. <see cref="ChatChannel.LastMessageAt"/> only ever refreshes
/// on a snapshot or a <c>GetConversations</c> page, so a client sorting its conversation list by it had
/// no live signal at all and had to invent one (w3champions/launcher-e#848 sorts on a client-side
/// ordinal precisely because this field did not exist). It is the same server clock as
/// <c>LastMessageAt</c> and <see cref="ChannelLastMessage.SentAt"/>, so the three are directly
/// comparable. Null only on a coalesced push assembled before this field existed — treat absence as
/// "no ordering information", never as a zero timestamp.
/// </para>
/// <para>
/// COALESCING: like <see cref="Preview"/>, this is latest-offered-wins. Under a burst the emitted pair
/// is the most recent OFFER's, which under concurrent same-channel sends is not necessarily the highest
/// <see cref="LastSeq"/> (that one takes a MAX). The two can therefore describe different messages a few
/// hundred milliseconds apart. That imprecision is pre-existing and deliberate — see
/// <see cref="FanOut.ActivityCoalescer.Offer"/> — and is invisible at the only resolution this feeds: a
/// conversation-list row's text and sort position.
/// </para>
/// </summary>
public record ChannelActivityDto(string ChannelId, long LastSeq, object Preview = null, DateTime? SentAt = null);
