using System;
using W3ChampionsChatService.Messages;

namespace W3ChampionsChatService.Protocol;

/// <summary>
/// Wire-facing message projection for <see cref="GetMessagesResult"/> (Task 16) and focused
/// <c>MessageReceived</c> pushes (<see cref="FanOut.FanOutEngine"/>, Task 12). <see cref="Sender"/>
/// reuses the existing domain <see cref="MessageSender"/> snapshot rather than a parallel DTO — it
/// already carries no boundary-private fields. <see cref="Deleted"/>/<see cref="Shadow"/> are
/// user-facing flag slots; both the pull and push paths build this record EXCLUSIVELY through
/// <see cref="ForUserDelivery"/> below, which forces them false — see that factory's doc comment for
/// why.
/// </summary>
public record MessageDto(
    string Id,
    string ChannelId,
    long Seq,
    MessageSender Sender,
    string Content,
    DateTime SentAt,
    bool Deleted,
    bool Shadow)
{
    /// <summary>
    /// The ONE user-facing delivery projection, shared by BOTH the pull path
    /// (<c>ChatHub.GetMessages</c>, Task 16) and the push path (<see cref="FanOut.FanOutEngine.OnMessagePersisted"/>,
    /// Task 12) — a single call site keeps the two projections byte-identical instead of letting them
    /// drift. <see cref="Deleted"/> and <see cref="Shadow"/> are ALWAYS forced false here, even for a
    /// shadow author's OWN message: surfacing the true shadow flag would tell a shadow-banned author
    /// they are muted, which is the load-bearing illusion (C3-plan.md decision 7). This is purely a
    /// display-projection concern — the repository layer (<see cref="Messages.MessageRepository.UserVisible"/>)
    /// already excludes soft-deleted rows and other authors' shadow rows before a <see cref="ChannelMessage"/>
    /// ever reaches this factory; a viewer's OWN shadow rows DO come back from the repo and must be
    /// forced non-shadow here to preserve the illusion.
    /// </summary>
    public static MessageDto ForUserDelivery(string channelId, ChannelMessage message) =>
        new(
            Id: message.Id,
            ChannelId: channelId,
            Seq: message.Seq,
            Sender: message.Sender,
            Content: message.Content,
            SentAt: message.SentAt,
            Deleted: false,
            Shadow: false);
}
