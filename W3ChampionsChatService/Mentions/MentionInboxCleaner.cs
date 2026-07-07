using System.Collections.Generic;
using System.Threading.Tasks;
using MongoDB.Driver;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Mentions;

/// <summary>
/// The real <see cref="IMentionInboxCleaner"/> (C6 Task 7, D7): deletes every <c>mention_inbox</c>
/// entry whose <see cref="MentionInboxEntry.MessageId"/> is one of <paramref name="messageIds"/> (see
/// <see cref="RemoveForMessages"/>) — a single indexed <c>DeleteMany</c> served by
/// <c>ix_messageId</c> (<see cref="ChatDomainIndexes"/>).
/// <para>
/// C4's <c>ChatHub.DeleteMessage</c>/<c>PurgeMessagesFromUser</c> call this hook strictly AFTER their
/// moderation audit log write and still BEFORE their fan-out event — an ordering C4 owns and this
/// class never touches (see <see cref="IMentionInboxCleaner"/>'s doc comment for the full C4/C6
/// coordination contract). This class only supplies what runs INSIDE that already-ordered hook.
/// </para>
/// <para>
/// Hard-deletes rows in <c>mention_inbox</c> ONLY — a different collection with different rules than
/// <c>messages</c> (moderation NEVER hard-deletes a message — soft-delete via <c>Deleted{By,At}</c>
/// only, TTL-only physical removal, pinned by a dedicated guardrail test). A mention-inbox entry is
/// pure denormalized notification state with no independent retention requirement once its message is
/// gone, so a real, physical delete here is correct and intentional.
/// </para>
/// </summary>
public class MentionInboxCleaner(MongoClient mongoClient) : MongoDbRepositoryBase(mongoClient), IMentionInboxCleaner
{
    private IMongoCollection<MentionInboxEntry> Inbox =>
        CreateCollection<MentionInboxEntry>(ChatCollections.MentionInbox);

    /// <summary>
    /// Deletes every entry referencing any id in <paramref name="messageIds"/>. Safe (no-op, never
    /// throws) against an empty collection — the common purge case, where most of a purged user's
    /// eligible message ids were never mentioned at all — and against a batch of ids that reference no
    /// existing entries; <c>DeleteMany</c> against an <c>$in</c> filter simply matches zero documents in
    /// that case, so no special-casing is needed for correctness, only for the empty-list short-circuit
    /// (skips a pointless Mongo round-trip).
    /// </summary>
    public Task RemoveForMessages(IReadOnlyCollection<string> messageIds)
    {
        if (messageIds == null || messageIds.Count == 0)
        {
            return Task.CompletedTask;
        }

        var filter = Builders<MentionInboxEntry>.Filter.In(e => e.MessageId, messageIds);
        return Inbox.DeleteManyAsync(filter);
    }
}
