using System;
using W3ChampionsChatService.Messages;

namespace W3ChampionsChatService.Protocol;

/// <summary>
/// Wire-facing message projection for <see cref="GetMessagesResult"/> and focused
/// <c>MessageReceived</c> pushes (Task 12 owns the fan-out wiring; this file/shape is pinned now so
/// <see cref="GetMessagesResult"/> has a concrete type in C3). <see cref="Sender"/> reuses the
/// existing domain <see cref="MessageSender"/> snapshot rather than a parallel DTO — it already
/// carries no boundary-private fields. <see cref="Deleted"/>/<see cref="Shadow"/> are user-facing
/// flag slots defined now, populated by C4 — always false until then, including on a shadow
/// author's own echo (the load-bearing illusion, C3-plan.md decision 7).
/// </summary>
public record MessageDto(
    string Id,
    string ChannelId,
    long Seq,
    MessageSender Sender,
    string Content,
    DateTime SentAt,
    bool Deleted,
    bool Shadow);
