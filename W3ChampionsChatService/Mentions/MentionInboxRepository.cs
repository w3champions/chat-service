using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MongoDB.Driver;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Mentions;

/// <summary>
/// BATTLETAG KEY CONVENTION (C6 Task 6 boyscout fix, mirrors <see cref="Memberships.MembershipRepository"/>'s
/// class doc): every entry's <see cref="MentionInboxEntry.BattleTag"/> is stored LOWERCASED
/// (<see cref="MentionFanOut"/> lowercases it before <see cref="Insert"/>), and every read/update below
/// normalizes its incoming <c>battleTag</c> argument the SAME way before building the Mongo filter —
/// a caller (<c>ChatHub</c>) may pass the JWT-cased identity battleTag straight through, exactly like
/// every <see cref="Memberships.MembershipRepository"/> call site does.
/// </summary>
public class MentionInboxRepository(MongoClient mongoClient) : MongoDbRepositoryBase(mongoClient)
{
    private IMongoCollection<MentionInboxEntry> Inbox =>
        CreateCollection<MentionInboxEntry>(ChatCollections.MentionInbox);

    /// <summary>Lowercases a battleTag to the durable mention-inbox key convention (see the class doc).</summary>
    private static string NormalizeTag(string battleTag) => battleTag.ToLowerInvariant();

    // virtual: lets fault-isolation tests substitute a throwing insert to prove MentionFanOut's
    // per-target try/catch (a single failed insert must not break the other targets or the sender ack).
    public virtual Task Insert(MentionInboxEntry entry) => Inbox.InsertOneAsync(entry);

    /// <summary>
    /// The caller's own mention inbox, NEWEST FIRST, capped at
    /// <see cref="ChatLimits.MentionInboxMaxEntries"/> (C6 Task 6, D6 — the 30d TTL already bounds the
    /// underlying collection; this cap bounds a single <c>GetMentionInbox</c> response). Read entries
    /// are included too (read/unread is purely the <see cref="MentionInboxEntry.ReadAt"/> field) —
    /// nothing is ever filtered out or deleted here.
    /// </summary>
    public Task<List<MentionInboxEntry>> LoadForUser(string battleTag)
    {
        var tag = NormalizeTag(battleTag);
        return Inbox.Find(e => e.BattleTag == tag)
            .SortByDescending(e => e.CreatedAt)
            .Limit(ChatLimits.MentionInboxMaxEntries)
            .ToListAsync();
    }

    /// <summary>
    /// Per-entry idempotent ack (C6 Task 6, D6) — <c>MarkMentionsRead</c>'s sole write. ONE conditional
    /// <c>UpdateMany</c> whose filter ANDs three legs: (a) <c>_id ∈ ids</c>; (b) <c>BattleTag ==</c> the
    /// caller's own lowercased tag — the AUTHORIZATION BOUNDARY: an id belonging to another user simply
    /// does not match this leg, so it is silently skipped — never acked, never an error, and never an
    /// oracle that would let a caller distinguish "foreign id" from "already read" from "doesn't exist"
    /// (all three collapse to the identical no-op outcome); (c) <c>ReadAt == null</c> — the IDEMPOTENCY
    /// guard: an already-read entry is excluded from the update, so a second ack of the same id can
    /// never overwrite its FIRST-seen <c>ReadAt</c> with a later timestamp. Returns the actual matched
    /// count (tests/diagnostics only — the hub's <c>Ok</c> ack never branches on it, per D6: idempotent,
    /// no oracle either way).
    /// </summary>
    public async Task<long> MarkRead(IReadOnlyList<string> ids, string battleTag, DateTime now)
    {
        var tag = NormalizeTag(battleTag);
        var filter = Builders<MentionInboxEntry>.Filter.And(
            Builders<MentionInboxEntry>.Filter.In(e => e.Id, ids),
            Builders<MentionInboxEntry>.Filter.Eq(e => e.BattleTag, tag),
            Builders<MentionInboxEntry>.Filter.Eq(e => e.ReadAt, null));
        var update = Builders<MentionInboxEntry>.Update.Set(e => e.ReadAt, now);

        var result = await Inbox.UpdateManyAsync(filter, update);
        return result.ModifiedCount;
    }

    /// <summary>
    /// Mark-EVERY-unread-entry-read (C6 Task 6, D6) — the SAME conditional update as
    /// <see cref="MarkRead"/> minus the id filter: every one of the caller's own still-unread entries
    /// (<c>ReadAt == null</c>) is acked in ONE write. Already-read entries are untouched (their original
    /// <c>ReadAt</c> survives), and NOTHING is ever deleted — read entries persist (dimmed client-side)
    /// until the 30d TTL.
    /// </summary>
    public async Task<long> MarkAllRead(string battleTag, DateTime now)
    {
        var tag = NormalizeTag(battleTag);
        var filter = Builders<MentionInboxEntry>.Filter.And(
            Builders<MentionInboxEntry>.Filter.Eq(e => e.BattleTag, tag),
            Builders<MentionInboxEntry>.Filter.Eq(e => e.ReadAt, null));
        var update = Builders<MentionInboxEntry>.Update.Set(e => e.ReadAt, now);

        var result = await Inbox.UpdateManyAsync(filter, update);
        return result.ModifiedCount;
    }

    /// <summary>
    /// Live unread count (C6 Task 6, D6) — backs <see cref="Protocol.SessionStateDto.MentionUnreadCount"/>.
    /// <c>ReadAt == null</c> ONLY; a read entry (still present, dimmed client-side) never counts again.
    /// </summary>
    public Task<long> CountUnread(string battleTag)
    {
        var tag = NormalizeTag(battleTag);
        return Inbox.CountDocumentsAsync(e => e.BattleTag == tag && e.ReadAt == null);
    }
}
