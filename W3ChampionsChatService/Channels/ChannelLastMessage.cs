using System;

namespace W3ChampionsChatService.Channels;

/// <summary>
/// Denormalized projection of a channel's newest USER-VISIBLE message, maintained on the channel doc
/// so a conversation list can render "who said what, and when" WITHOUT reading the message collection.
/// <para>
/// WHY this exists: before it, "the last message in this conversation" was only ever knowable from a
/// live event — <c>ChannelActivity</c> (unfocused channels only, and coalesced/suppressed) or
/// <c>MessageReceived</c> (focused channels only). Neither <c>SessionState</c> nor
/// <c>GetConversations</c> carried the text at all, so a client had no way to render a conversation
/// list at rest and no way to recover one after a reconnect: whatever it had cached from live events
/// was simply lost. Clients were reconstructing this projection from event fragments, which cannot be
/// made correct — see the launcher-e investigation in w3champions/launcher-e#848.
/// </para>
/// <para>
/// SCOPE (deliberately narrow, mirroring <c>DmActivityPreviewDto</c>'s own scope rules):
/// <list type="bullet">
/// <item>Only <c>Dm</c> (once <see cref="Domain.DmRequestState.Accepted"/>) and <c>GroupDm</c> channels
/// carry it. A PENDING Dm never does — the recipient must not see a stranger's message text before
/// accepting, which is the same consent wall that already suppresses their <c>ChannelActivity</c>.
/// Public/SemiPublic/System channels never do either: <c>PublicCatalog</c> ships the raw
/// <see cref="ChatChannel"/> for rooms the caller has NOT joined, and a preview there would publish
/// room content to non-members.</item>
/// <item>SHADOW messages never populate it. A shadow-banned author's text must never surface to anyone
/// else, and this projection is channel-global (no per-viewer filtering is possible on it) — so the
/// shadow illusion is preserved by simply never projecting one. <see cref="ChatChannel.LastSeq"/> and
/// <see cref="ChatChannel.LastMessageAt"/> still advance for a shadow message, exactly as before, so
/// the two can legitimately disagree: <see cref="Seq"/> is the newest NON-shadow message. Always trust
/// <see cref="Seq"/> over <see cref="ChatChannel.LastSeq"/> when rendering this.</item>
/// </list>
/// </para>
/// <para>
/// NO DELETION INVALIDATION EXISTS, AND NONE IS NEEDED — but only because of an invariant that lives in
/// another file: <see cref="ChannelModeration.IsModeratable"/> (Public / SemiPublic / System+Match) is
/// DISJOINT from the projected set above, and it gates BOTH <c>ChatHub.DeleteMessage</c> and
/// <c>ChatHub.PurgeMessagesFromUser</c>. A projected message therefore cannot be deleted. If moderation
/// is ever widened to reach Dm/GroupDm, this projection silently starts serving deleted text to every
/// member of the conversation, forever — a moderation hole, not a cosmetic bug. <c>ChannelLastMessageTests</c>
/// pins the disjointness so that change fails loudly here instead of shipping.
/// </para>
/// <para>
/// Message TTL is a knowingly accepted staleness: <c>ExpiryCalculator.ForChannelMessage</c> can reap the
/// projected message while the (message-anchored, longer) shell TTL keeps the channel alive, leaving an
/// excerpt for a row that no longer exists. It is a bounded string on the channel doc, read by clients
/// that never dereference it back to a message, and the alternative — a TTL-aware sweep over every shell —
/// buys nothing a user can perceive.
/// </para>
/// </summary>
public class ChannelLastMessage
{
    /// <summary>
    /// The projected message's per-channel sequence number. Also the concurrency token: the advance is a
    /// compare-and-set on this field (<see cref="ChannelRepository.TryAdvanceLastMessage"/>), so
    /// concurrent sends that reach the write out of order can never regress the projection.
    /// </summary>
    public long Seq { get; set; }

    public string SenderBattleTag { get; set; }

    public string SenderName { get; set; }

    /// <summary>
    /// The message content bounded by <see cref="Protocol.Excerpts.Bounded"/> — the SAME helper (and so
    /// the same <see cref="Domain.ChatLimits.DmPreviewExcerptLength"/> cap and the same surrogate-pair
    /// safety) that builds <c>DmActivityPreviewDto.Excerpt</c>, so a client that renders one from the
    /// live event and one from the snapshot can never see the two disagree on the same message.
    /// </summary>
    public string Excerpt { get; set; }

    /// <summary>
    /// The projected message's <see cref="Messages.ChannelMessage.SentAt"/> — a SERVER instant, which is
    /// what makes this usable as a conversation-list sort key. (<see cref="ChatChannel.LastMessageAt"/>
    /// is the same clock but advances for shadow messages too.)
    /// </summary>
    public DateTime SentAt { get; set; }
}
