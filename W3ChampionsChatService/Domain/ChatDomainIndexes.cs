using System;
using System.Threading.Tasks;
using MongoDB.Driver;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Memberships;
using W3ChampionsChatService.Mentions;
using W3ChampionsChatService.Messages;
using W3ChampionsChatService.Users;

namespace W3ChampionsChatService.Domain;

/// <summary>
/// Code-managed index definitions for all chat domain collections. Called at startup
/// (ChatDomainBootstrap) and from tests. Idempotent: deterministic names + CreateManyAsync
/// no-ops when an identical index already exists.
/// TTL convention: expireAfterSeconds 0 — ExpiresAt IS the absolute expiry instant, and the
/// field is omitted entirely (BsonIgnoreIfNull) on permanent documents.
/// </summary>
public static class ChatDomainIndexes
{
    /// <summary>
    /// C4 (D6): the collation the case-insensitive sender index is BUILT with. Purge-related repo
    /// queries (<see cref="Messages.MessageRepository.LoadPurgeableBySender"/>) MUST use this exact
    /// collation, or Mongo silently falls back to a collection scan instead of using the index.
    /// Strength 2 (secondary) ignores case but is still diacritic-sensitive.
    /// </summary>
    public static readonly Collation SenderCaseInsensitiveCollation = new(locale: "en", strength: CollationStrength.Secondary);

    public static async Task EnsureAllAsync(MongoClient mongoClient)
    {
        var db = mongoClient.GetDatabase(MongoDbRepositoryBase.DatabaseName);
        await EnsureChannelIndexes(db);
        await EnsureMembershipIndexes(db);
        await EnsureMessageIndexes(db);
        await EnsureMentionInboxIndexes(db);
        await EnsureUserDirectoryIndexes(db);
    }

    private static async Task EnsureChannelIndexes(IMongoDatabase db)
    {
        var channels = db.GetCollection<ChatChannel>(ChatCollections.Channels);
        await channels.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<ChatChannel>(
                Builders<ChatChannel>.IndexKeys.Ascending(c => c.PairKey),
                new CreateIndexOptions<ChatChannel>
                {
                    Name = "ux_pairKey_dm",
                    Unique = true,
                    PartialFilterExpression = Builders<ChatChannel>.Filter.Eq(c => c.Type, ChannelType.Dm),
                }),
            new CreateIndexModel<ChatChannel>(
                Builders<ChatChannel>.IndexKeys.Ascending(c => c.Type).Ascending(c => c.NormalizedName),
                new CreateIndexOptions<ChatChannel>
                {
                    Name = "ux_type_normalizedName",
                    Unique = true,
                    // name-joinable types only (Public + SemiPublic) — the two types that ever
                    // populate NormalizedName; System/Dm/GroupDm use SystemRef/PairKey instead
                    // and must stay unaffected by this uniqueness constraint.
                    PartialFilterExpression = Builders<ChatChannel>.Filter.In(
                        c => c.Type, [ChannelType.Public, ChannelType.SemiPublic]),
                }),
            // C7 Task 4: backs ChannelRepository.FindOrCreateSystem/LoadBySystemRef — mirrors
            // ux_pairKey_dm's partial-unique shape exactly (Type == System instead of Type == Dm).
            // System channels (match/lobby/clan shells) are keyed by (SystemKind, SystemRef); this
            // guarantees exactly one channel document per (kind, ref) pair even under a genuine
            // concurrent find-or-create race.
            new CreateIndexModel<ChatChannel>(
                Builders<ChatChannel>.IndexKeys.Ascending(c => c.SystemKind).Ascending(c => c.SystemRef),
                new CreateIndexOptions<ChatChannel>
                {
                    Name = "ux_systemKind_systemRef",
                    Unique = true,
                    PartialFilterExpression = Builders<ChatChannel>.Filter.Eq(c => c.Type, ChannelType.System),
                }),
            new CreateIndexModel<ChatChannel>(
                Builders<ChatChannel>.IndexKeys.Ascending(c => c.ExpiresAt),
                new CreateIndexOptions { Name = "ttl_expiresAt", ExpireAfter = TimeSpan.Zero }),
        ]);
    }

    private static async Task EnsureMembershipIndexes(IMongoDatabase db)
    {
        var memberships = db.GetCollection<ChannelMembership>(ChatCollections.ChannelMemberships);
        await memberships.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<ChannelMembership>(
                Builders<ChannelMembership>.IndexKeys.Ascending(m => m.ChannelId).Ascending(m => m.BattleTag),
                new CreateIndexOptions { Name = "ux_channelId_battleTag", Unique = true }),
            new CreateIndexModel<ChannelMembership>(
                Builders<ChannelMembership>.IndexKeys.Ascending(m => m.BattleTag),
                new CreateIndexOptions { Name = "ix_battleTag" }),
        ]);
    }

    private static async Task EnsureMessageIndexes(IMongoDatabase db)
    {
        var messages = db.GetCollection<ChannelMessage>(ChatCollections.Messages);

        // D6: best-effort drop of the superseded case-SENSITIVE index. Safe on a fresh database
        // (nothing to drop — swallow IndexNotFound) and on an already-migrated one (same reason);
        // only a pre-migration database that still carries the old name actually loses it here.
        try
        {
            await messages.Indexes.DropOneAsync("ix_sender_sentAt");
        }
        catch (MongoCommandException ex) when (IsIndexNotFound(ex))
        {
            // no-op: nothing to drop
        }

        await messages.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<ChannelMessage>(
                Builders<ChannelMessage>.IndexKeys.Ascending(m => m.ChannelId).Ascending(m => m.Seq),
                new CreateIndexOptions { Name = "ux_channelId_seq", Unique = true }),
            // D6: replaces ix_sender_sentAt. Collated case-insensitively so a mixed-case purge
            // argument (LoadPurgeableBySender) matches the stored sender casing — fixing the legacy
            // case-SENSITIVE bug in Chats/History.cs's DeleteMessagesFromUser (`==` comparison).
            new CreateIndexModel<ChannelMessage>(
                Builders<ChannelMessage>.IndexKeys.Ascending(m => m.Sender.BattleTag).Ascending(m => m.SentAt),
                new CreateIndexOptions
                {
                    Name = "ix_sender_ci_sentAt",
                    Collation = SenderCaseInsensitiveCollation,
                }),
            new CreateIndexModel<ChannelMessage>(
                Builders<ChannelMessage>.IndexKeys.Ascending(m => m.ExpiresAt),
                new CreateIndexOptions { Name = "ttl_expiresAt", ExpireAfter = TimeSpan.Zero }),
        ]);
    }

    private static bool IsIndexNotFound(MongoCommandException ex) => ex.CodeName == "IndexNotFound";

    private static async Task EnsureMentionInboxIndexes(IMongoDatabase db)
    {
        var inbox = db.GetCollection<MentionInboxEntry>(ChatCollections.MentionInbox);
        await inbox.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<MentionInboxEntry>(
                Builders<MentionInboxEntry>.IndexKeys.Ascending(e => e.BattleTag),
                new CreateIndexOptions { Name = "ix_battleTag" }),
            new CreateIndexModel<MentionInboxEntry>(
                Builders<MentionInboxEntry>.IndexKeys.Ascending(e => e.ExpiresAt),
                new CreateIndexOptions { Name = "ttl_expiresAt", ExpireAfter = TimeSpan.Zero }),
            // C6 Task 7 (D7): backs MentionInboxCleaner.RemoveForMessages' DeleteMany(MessageId ∈ ids) —
            // moderation's DeleteMessage/PurgeMessagesFromUser cleanup hook. Without this, every
            // moderation delete/purge would COLLSCAN mention_inbox looking for entries to remove.
            new CreateIndexModel<MentionInboxEntry>(
                Builders<MentionInboxEntry>.IndexKeys.Ascending(e => e.MessageId),
                new CreateIndexOptions { Name = "ix_messageId" }),
        ]);
    }

    /// <summary>
    /// C6 T2 (C1 amendment 2 — the one domain index C1 missed): backs the tiered mention search's
    /// tier-3 directory scan (<see cref="Users.UserDirectoryRepository.SearchByNormalizedPrefix"/>) —
    /// the compound key lets a NormalizedName prefix bound AND the 90d LastSeenAt activity gate both
    /// stay index-served in one scan. Non-unique (defensive: legacy/test stub rows could collide on
    /// name-only values; battle.net tags are case-insensitively unique in reality, and the collection's
    /// real uniqueness is already enforced by the lowercased <c>_id</c>).
    /// </summary>
    private static async Task EnsureUserDirectoryIndexes(IMongoDatabase db)
    {
        var directory = db.GetCollection<UserDirectoryEntry>(ChatCollections.UserDirectory);
        await directory.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<UserDirectoryEntry>(
                Builders<UserDirectoryEntry>.IndexKeys.Ascending(e => e.NormalizedName).Descending(e => e.LastSeenAt),
                new CreateIndexOptions { Name = "ix_normalizedName_lastSeenAt" }),
        ]);
    }
}
