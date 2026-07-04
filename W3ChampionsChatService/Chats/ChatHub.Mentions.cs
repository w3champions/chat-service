using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Protocol;

namespace W3ChampionsChatService.Chats;

/// <summary>
/// C6 (Task 6, D6): the mention-inbox READ/ACK surface — <see cref="GetMentionInbox"/>,
/// <see cref="MarkMentionsRead"/>, <see cref="MarkAllMentionsRead"/>. The WRITE side (inbox-entry
/// creation + the targeted <c>MentionNotified</c> push) is <see cref="Mentions.MentionFanOut"/>
/// (Task 5); this partial owns everything the CLIENT drives afterward — paging the inbox and
/// acknowledging entries.
/// <para>
/// ACK RULES (pinned — C6-plan.md §2 + D6): acks are CLIENT-DRIVEN and PER-ENTRY. The server NEVER
/// derives a mention read from a channel's <c>lastReadSeq</c> or any other seq — no seq-derived
/// auto-ack, ever. Seeing a NEWER mention must never retroactively ack an OLDER, still-unseen one.
/// This is a DELIBERATE DEPARTURE from how regular channel read-state works elsewhere in this
/// codebase (<see cref="ChatHub.MarkRead"/> IS seq-derived, advancing a single cursor) — a prior
/// design decision explicitly rejected seq-derived mention acks, because seeing a newer mention
/// should never silently clear an older one the user hasn't seen yet. <see cref="MarkMentionsRead"/>
/// is idempotent: acking an already-read id a second time is a no-op — its <c>ReadAt</c> keeps the
/// FIRST-seen value, never overwritten by a later ack. Read entries are KEPT (dimmed client-side)
/// until the 30d TTL — NOTHING in this file ever deletes a <c>mention_inbox</c> row (that is Task 7's
/// job, and only for the moderation-delete scenario).
/// </para>
/// <para>
/// AUTHORIZATION BOUNDARY: <see cref="Mentions.MentionInboxRepository.MarkRead"/>'s Mongo filter ANDs
/// the caller's own lowercased BattleTag alongside the id-membership check — an id belonging to
/// someone else simply does not match that filter, so it is silently skipped: never acked, never an
/// error, and never an oracle a caller could use to probe whether a given id belongs to another user.
/// </para>
/// </summary>
public partial class ChatHub
{
    /// <summary>
    /// C6 (Task 6): pages the CALLER'S OWN mention inbox, newest-first, capped at
    /// <see cref="ChatLimits.MentionInboxMaxEntries"/> (<see cref="Mentions.MentionInboxRepository.LoadForUser"/>
    /// — battleTag is passed straight through, JWT-cased, and normalized internally by the repository).
    /// Fail-closed session → <see cref="ChatResultCode.PermissionDenied"/>; else every entry — read AND
    /// unread; read entries are dimmed client-side, never dropped here — is projected through
    /// <see cref="MentionInboxEntryDto"/>, the explicit boundary-privacy projection that deliberately
    /// never exposes <c>ExpiresAt</c> (server-only TTL bookkeeping — Task 1's convention).
    /// </summary>
    public async Task<GetMentionInboxResult> GetMentionInbox()
    {
        // Fail-closed: no live session → no identity to page an inbox for.
        if (!_sessionRegistry.TryGetByConnectionId(Context.ConnectionId, out var session))
        {
            return new GetMentionInboxResult(ChatResultCode.PermissionDenied);
        }

        var entries = await _mentionInboxRepository.LoadForUser(session.Identity.BattleTag);
        var dtos = entries
            .Select(e => new MentionInboxEntryDto(
                e.Id,
                e.ChannelId,
                e.MessageId,
                e.Seq,
                e.AuthorBattleTag,
                e.AuthorName,
                e.Excerpt,
                e.CreatedAt,
                e.ReadAt))
            .ToList();
        return new GetMentionInboxResult(ChatResultCode.Ok, dtos);
    }

    /// <summary>
    /// C6 (Task 6, D6): per-entry idempotent ack. Resolution order mirrors the rest of the hub's
    /// client-bug-vs-typed-result split (e.g. <see cref="GetMessages"/>): fail-closed session FIRST
    /// (there is no identity to ack under), THEN the malformed-arg guards — a null array, or one over
    /// <see cref="ChatLimits.MentionAckBatchMax"/>, is a client programming error and throws
    /// <see cref="HubException"/> rather than returning a typed reject. The actual ack is ONE
    /// conditional <c>UpdateMany</c> (<see cref="Mentions.MentionInboxRepository.MarkRead"/>) whose
    /// filter ANDs the caller's own lowercased tag alongside the id list — see the class doc's
    /// AUTHORIZATION BOUNDARY and no-seq-auto-ack notes. Returns <see cref="ChatResultCode.Ok"/>
    /// REGARDLESS of the matched count (D6 — idempotent and foreign-id-safe; the result carries no
    /// information about which/how-many ids actually matched, so it can never be used as an oracle).
    /// </summary>
    public async Task<ChannelOperationResult> MarkMentionsRead(string[] mentionIds)
    {
        // 1. Fail-closed: no live session → no identity to ack an inbox for.
        if (!_sessionRegistry.TryGetByConnectionId(Context.ConnectionId, out var session))
        {
            return new ChannelOperationResult(ChatResultCode.PermissionDenied);
        }

        // 2. Malformed-arg guards, before any DB work — client-bug mapping (mirrors GetMessages/OpenDm/
        // CreateGroup's HubException precedent), NOT a typed result.
        if (mentionIds == null)
        {
            throw new HubException("MarkMentionsRead requires a non-null mentionIds array");
        }
        if (mentionIds.Length > ChatLimits.MentionAckBatchMax)
        {
            throw new HubException($"MarkMentionsRead: mentionIds exceeds the {ChatLimits.MentionAckBatchMax}-id batch cap");
        }

        // 3. The owner-filtered, unread-conditional ack — see the class doc for the authorization
        // boundary and idempotency guarantees this single call provides.
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        await _mentionInboxRepository.MarkRead(mentionIds, session.Identity.BattleTag, now);

        // 4. Typed ack — ALWAYS Ok, independent of how many ids actually matched (D6).
        return new ChannelOperationResult(ChatResultCode.Ok);
    }

    /// <summary>
    /// C6 (Task 6, D6): marks EVERY unread entry of the caller's OWN inbox read — the same
    /// owner-filtered, unread-conditional update <see cref="MarkMentionsRead"/> uses, without the id
    /// list. Fail-closed session → <see cref="ChatResultCode.PermissionDenied"/>. Entries persist
    /// (never deleted) exactly as the class doc describes.
    /// </summary>
    public async Task<ChannelOperationResult> MarkAllMentionsRead()
    {
        if (!_sessionRegistry.TryGetByConnectionId(Context.ConnectionId, out var session))
        {
            return new ChannelOperationResult(ChatResultCode.PermissionDenied);
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        await _mentionInboxRepository.MarkAllRead(session.Identity.BattleTag, now);

        return new ChannelOperationResult(ChatResultCode.Ok);
    }
}
