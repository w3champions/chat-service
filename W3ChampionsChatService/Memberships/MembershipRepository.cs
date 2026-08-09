using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Memberships;

/// <summary>
/// Durable (channel, user) membership store.
/// <para>
/// BATTLETAG KEY CONVENTION (C5 T4): the persisted <see cref="ChannelMembership.BattleTag"/> is ALWAYS
/// stored lowercased, and every read/update lowercases its incoming <c>battleTag</c> argument before
/// building the Mongo filter (Mongo <c>$eq</c> is case-SENSITIVE — there is no collation or CI index).
/// This conforms membership storage to the same lowercased-key convention the rest of the DM machinery
/// already assumes: <see cref="DmPairKey.For"/> (sorted, lowercased pair-key), the relationship provider
/// and <see cref="Mutes.MuteRepository"/> (both lowercase their keys), and <see cref="Sessions.SessionRegistry"/>
/// (whose "the DB lowercases battleTags" note documents the intended convention). Without it, a DM
/// counterpart membership materialized under the pair-key's lowercased tag would be invisible to the
/// recipient — whose own reads use their VERBATIM (often uppercase) JWT casing — silently dropping the
/// DM from their reconnect SessionState, tray, and GetMessages/FocusChannel, and letting a later
/// JWT-cased self-OpenDm insert a DUPLICATE row past the case-sensitive <c>ux_channelId_battleTag</c>
/// unique index. Normalizing here at the single durable choke point also makes that unique index dedupe
/// case-insensitively.
/// </para>
/// </summary>
public class MembershipRepository(MongoClient mongoClient, ChannelRepository channelRepository) : MongoDbRepositoryBase(mongoClient)
{
    private IMongoCollection<ChannelMembership> Memberships =>
        CreateCollection<ChannelMembership>(ChatCollections.ChannelMemberships);

    /// <summary>Lowercases a battleTag to the durable membership key convention (see the class doc).</summary>
    private static string NormalizeTag(string battleTag) => battleTag.ToLowerInvariant();

    // Persists a lowercased-BattleTag COPY without mutating the caller's object (immutability). NOTE:
    // keep this field list in sync with ChannelMembership — a new field must be copied here too.
    private static ChannelMembership WithNormalizedBattleTag(ChannelMembership membership) =>
        new ChannelMembership
        {
            Id = membership.Id,
            ChannelId = membership.ChannelId,
            BattleTag = NormalizeTag(membership.BattleTag),
            Role = membership.Role,
            NotificationLevel = membership.NotificationLevel,
            LastReadSeq = membership.LastReadSeq,
            JoinedAt = membership.JoinedAt,
            DeclinedUntil = membership.DeclinedUntil,
        };

    public Task Insert(ChannelMembership membership) =>
        Memberships.InsertOneAsync(WithNormalizedBattleTag(membership));

    public Task<ChannelMembership> Load(string channelId, string battleTag)
    {
        var tag = NormalizeTag(battleTag);
        return Memberships.Find(m => m.ChannelId == channelId && m.BattleTag == tag).FirstOrDefaultAsync();
    }

    // 2026-08-04 follow-up (carried launcher-review item): sorted JoinedAt ascending — without an
    // explicit sort Mongo returns natural/insertion order (arbitrary, not contractual), which the
    // launcher was silently relying on to preserve "join order" for name-joinable (SemiPublic) channels
    // across a reconnect. SessionStateAssembler.AssembleAndSeed (and every other caller) consumes this
    // list straight through for the non-DM slice of SessionStateDto.Channels, so sorting once here
    // fixes the contract at the single durable choke point rather than re-sorting at every call site.
    // The bounded, recency-ordered 1:1-DM slice (follow-up spec §6, SelectSnapshotMemberships) is
    // unaffected — it re-sorts its own DM subset by LastMessageAt regardless of this base ordering.
    public Task<List<ChannelMembership>> LoadForUser(string battleTag)
    {
        var tag = NormalizeTag(battleTag);
        return Memberships.Find(m => m.BattleTag == tag).SortBy(m => m.JoinedAt).ToListAsync();
    }

    /// <summary>
    /// Minimal-payload sibling of <see cref="LoadForUser"/> (2026-08-05 PR36 feedback, Part 3) — a
    /// projected read returning ONLY the <see cref="ChannelMembership.ChannelId"/> values for a user —
    /// minimal WIRE payload (the server still fetches the documents; ix_battleTag_joinedAt is
    /// BattleTag-prefixed only, so the projection is not index-covered — the point is that no document
    /// bodies cross the wire). Backs <see cref="CountNameJoinableMembershipsForUser"/>,
    /// which only ever needs the id set. No sort (callers of this projection don't need ordering; unlike
    /// <see cref="LoadForUser"/>, whose JoinedAt-ascending sort is a client-order contract).
    /// </summary>
    public Task<List<string>> LoadChannelIdsForUser(string battleTag)
    {
        var tag = NormalizeTag(battleTag);
        return Memberships.Find(m => m.BattleTag == tag).Project(m => m.ChannelId).ToListAsync();
    }

    // Virtual: a test seam, mirroring DeleteOrphanedForUser's existing precedent in this class. PR40
    // review (P1) made clan-membership REVOCATION fail-closed, and the only way to prove a failed delete
    // fails the connect (rather than silently leaving the user in a former clan) is a throwing double.
    public virtual Task Delete(string channelId, string battleTag)
    {
        var tag = NormalizeTag(battleTag);
        return Memberships.DeleteOneAsync(m => m.ChannelId == channelId && m.BattleTag == tag);
    }

    /// <summary>
    /// Match-channel-hygiene brief (2026-08-05), Part 1 — connect-time orphan self-heal. Batched delete of
    /// membership rows whose channel no longer exists (e.g. a lost mm→chat member-removal left the row
    /// behind after the System channel's <c>ttl_expiresAt</c> TTL'd the doc). Called ONLY from
    /// <see cref="Protocol.SessionStateAssembler.AssembleAndSeed"/> with the CONNECTING user's own
    /// excluded (channel-less) <see cref="ChannelMembership.ChannelId"/> set — every row therefore shares
    /// the SAME BattleTag, so a single equality ANDed with a ChannelId <c>$in</c> serves the whole batch
    /// through the unique <c>ux_channelId_battleTag</c> index (one index seek per channel id, bounded by
    /// the — typically tiny — orphan count, never a collection scan). Virtual: a test seam so a throwing
    /// double can prove the caller's best-effort posture (a delete failure must never fail connect).
    /// </summary>
    public virtual async Task<long> DeleteOrphanedForUser(string battleTag, IReadOnlyCollection<string> channelIds)
    {
        var tag = NormalizeTag(battleTag);
        var filter = Builders<ChannelMembership>.Filter.And(
            Builders<ChannelMembership>.Filter.In(m => m.ChannelId, channelIds),
            Builders<ChannelMembership>.Filter.Eq(m => m.BattleTag, tag));
        var result = await Memberships.DeleteManyAsync(filter);
        return result.DeletedCount;
    }

    /// <summary>Monotonic read-state advance ($max) — a lower/stale seq from an out-of-order
    /// or duplicate MarkRead call never regresses LastReadSeq.</summary>
    public Task UpdateLastReadSeq(string channelId, string battleTag, long seq)
    {
        var tag = NormalizeTag(battleTag);
        return Memberships.UpdateOneAsync(
            m => m.ChannelId == channelId && m.BattleTag == tag,
            Builders<ChannelMembership>.Update.Max(m => m.LastReadSeq, seq));
    }

    public Task SetNotificationLevel(string channelId, string battleTag, NotificationLevel level)
    {
        var tag = NormalizeTag(battleTag);
        return Memberships.UpdateOneAsync(
            m => m.ChannelId == channelId && m.BattleTag == tag,
            Builders<ChannelMembership>.Update.Set(m => m.NotificationLevel, level));
    }

    /// <summary>
    /// Membership cap gate (acceptance 10) — counts only name-joinable (Public + SemiPublic)
    /// memberships; System/Dm/GroupDm never count against the cap.
    /// <para>
    /// 2026-08-05 PR36 feedback (Part 3): previously loaded the caller's FULL membership documents
    /// (<c>LoadForUser</c>, DMs included) and the FULL channel documents behind them
    /// (<c>ChannelRepository.LoadByIds</c>) just to count a type-filtered subset client-side — wasted
    /// payload for a call site (<c>ChatHub.Channels.JoinChannel</c>'s join-by-name path) that only ever
    /// needs a number. Rewritten to two minimal round-trips: (1) <see cref="LoadChannelIdsForUser"/>, a
    /// projected membership read returning ONLY ChannelId values; (2)
    /// <see cref="Channels.ChannelRepository.CountNameJoinableAmongIds"/>, a server-side
    /// <c>CountDocumentsAsync(Id ∈ ids AND (Type == Public OR Type == SemiPublic))</c>. Same semantics as
    /// before — the unique <c>ux_channelId_battleTag</c> index guarantees at most one membership per
    /// (channel, user), so distinct channel ids among a user's memberships correspond 1:1 with the
    /// memberships themselves.
    /// </para>
    /// </summary>
    public async Task<int> CountNameJoinableMembershipsForUser(string battleTag)
    {
        var channelIds = await LoadChannelIdsForUser(battleTag);
        if (channelIds.Count == 0) return 0;

        return (int)await channelRepository.CountNameJoinableAmongIds(channelIds);
    }

    /// <summary>All memberships of one channel (C5 D12) — legitimate here: the never-enumerate-
    /// channel→users guardrail on <see cref="ChannelMembership"/> is about PUBLIC channels; groups
    /// are ACL-bound and capped at <see cref="ChatLimits.MaxGroupSize"/>, so enumerating a group's
    /// members (roster, owner lookups, auto-promotion) is the intended access pattern.</summary>
    // virtual: a test seam (mirroring UserDirectoryRepository.Load / MentionInboxRepository.Insert) so a
    // subclass can interpose a deterministic concurrent membership mutation between this read and a caller's
    // subsequent commit — used to reproduce the FocusChannel read→commit TOCTOU without timing/sleeps.
    public virtual Task<List<ChannelMembership>> LoadForChannel(string channelId) =>
        Memberships.Find(m => m.ChannelId == channelId).ToListAsync();

    /// <summary>Member count for a single channel (C5 D12 — group size bounds, last-member-leaves
    /// detection). Uses the same ux_channelId_battleTag-backed collection scan as
    /// <see cref="LoadForChannel"/> but returns a bare count.</summary>
    public async Task<int> CountForChannel(string channelId) =>
        (int)await Memberships.CountDocumentsAsync(m => m.ChannelId == channelId);

    /// <summary>
    /// D1 follow-up (2026-08-05, mention-canonicalization brief): batched member-scope check for
    /// <c>ChatHub.SearchMentionCandidates</c>' SemiPublic/System lane. Given an already-assembled,
    /// SMALL candidate battleTag set (bounded to <see cref="ChatLimits.MentionSearchMaxResults"/>, ~20),
    /// returns the SUBSET that actually has a <c>channel_memberships</c> row for
    /// <paramref name="channelId"/> — ONE indexed <c>$in</c> query (backed by the same
    /// <c>ux_channelId_battleTag</c> index <see cref="LoadForChannel"/> uses), never the full-room
    /// membership scan <see cref="LoadForChannel"/> performs. This is what keeps the search bounded on a
    /// big SemiPublic/System room: the read is sized to the CANDIDATE list, never the room's total
    /// membership. A projected read (only <see cref="ChannelMembership.BattleTag"/> crosses the wire).
    /// Tags are lowercase-normalized both in the query and the returned set (see the class doc's BATTLETAG
    /// KEY CONVENTION), so callers should compare case-insensitively. Virtual solely so tests can
    /// spy/count calls (mirrors <see cref="LoadForChannel"/>) — fix round 1, finding F6a: proves a lane
    /// that must never perform this scoping read (e.g. Public) actually doesn't.
    /// </summary>
    public virtual async Task<HashSet<string>> LoadMemberBattleTags(string channelId, IEnumerable<string> battleTags)
    {
        var tags = battleTags.Select(NormalizeTag).ToList();
        if (tags.Count == 0)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var rows = await Memberships
            .Find(m => m.ChannelId == channelId && tags.Contains(m.BattleTag))
            .Project(m => m.BattleTag)
            .ToListAsync();
        return new HashSet<string>(rows, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// D1 fix round 1 (finding F1): member-scoped RECALL BACKFILL for
    /// <c>ChatHub.SearchMentionCandidates</c>' SemiPublic/System lane. The global tiers (viewer/online/
    /// directory) run UNSCOPED and are ranked+capped against the WORLD before the candidate-side
    /// <see cref="LoadMemberBattleTags"/> check ever runs — on a big room, a short (1-2 char) prefix can
    /// be dominated by non-member noise and filtered down to near-nothing, degrading autocomplete recall
    /// to "currently-focused viewers only". This method restores full recall (online-but-unfocused AND
    /// offline members) by querying <c>channel_memberships</c> DIRECTLY for members whose (lowercased)
    /// BattleTag matches the prefix — an anchored range scan on the SAME compound
    /// <c>ux_channelId_battleTag</c> index <see cref="LoadForChannel"/> uses (ChannelId equality + a
    /// BattleTag prefix bound), bounded to <paramref name="limit"/> rows, NEVER the room's total
    /// membership (never <see cref="LoadForChannel"/>'s full scan). Mirrors
    /// <see cref="Users.UserDirectoryRepository.SearchByNormalizedPrefix"/>'s contract exactly:
    /// <paramref name="prefixLower"/> must already be lowercased by the caller (not re-normalized here —
    /// it is already the durable storage casing), and <paramref name="excludeBattleTagsLower"/> ANDs in a
    /// <c>$nin</c> against battleTags the caller's tiers 1-3 already claimed, so this query's own
    /// <paramref name="limit"/> is never wasted re-fetching a dupe ahead of a genuinely new match — the
    /// same starve-out bug class that method's own doc explains. A projected read: only
    /// <see cref="ChannelMembership.BattleTag"/> crosses the wire. Virtual solely so tests can spy/count
    /// calls (mirrors <see cref="LoadForChannel"/>/<see cref="LoadMemberBattleTags"/>).
    /// </summary>
    public virtual Task<List<string>> SearchMemberBattleTagsByPrefix(
        string channelId, string prefixLower, int limit, IReadOnlyCollection<string> excludeBattleTagsLower = null)
    {
        var filterBuilder = Builders<ChannelMembership>.Filter;
        var filter = filterBuilder.And(
            filterBuilder.Eq(m => m.ChannelId, channelId),
            filterBuilder.Regex(m => m.BattleTag, new BsonRegularExpression("^" + Regex.Escape(prefixLower))));

        if (excludeBattleTagsLower is { Count: > 0 })
        {
            filter = filterBuilder.And(filter, filterBuilder.Nin(m => m.BattleTag, excludeBattleTagsLower));
        }

        return Memberships.Find(filter).Limit(limit).Project(m => m.BattleTag).ToListAsync();
    }

    public Task SetRole(string channelId, string battleTag, MembershipRole role)
    {
        var tag = NormalizeTag(battleTag);
        return Memberships.UpdateOneAsync(
            m => m.ChannelId == channelId && m.BattleTag == tag,
            Builders<ChannelMembership>.Update.Set(m => m.Role, role));
    }

    /// <summary>C5 D3: stamps the RECIPIENT's own decline-suppression window. Never touches the
    /// channel doc or any other member's row.</summary>
    public Task SetDeclinedUntil(string channelId, string battleTag, DateTime declinedUntil)
    {
        var tag = NormalizeTag(battleTag);
        return Memberships.UpdateOneAsync(
            m => m.ChannelId == channelId && m.BattleTag == tag,
            Builders<ChannelMembership>.Update.Set(m => m.DeclinedUntil, declinedUntil));
    }

    /// <summary>C5 D3/T4: clears a resolved decline window — called when the suppression period has
    /// elapsed and a fresh request is about to resurface, or when the conversation is accepted.</summary>
    public Task ClearDeclinedUntil(string channelId, string battleTag)
    {
        var tag = NormalizeTag(battleTag);
        return Memberships.UpdateOneAsync(
            m => m.ChannelId == channelId && m.BattleTag == tag,
            Builders<ChannelMembership>.Update.Unset(m => m.DeclinedUntil));
    }

    /// <summary>Residual-row cleanup when a channel is deleted (C5 D12 — last group member leaves).</summary>
    public Task DeleteAllForChannel(string channelId) =>
        Memberships.DeleteManyAsync(m => m.ChannelId == channelId);

    /// <summary>
    /// Idempotent membership upsert (C5 T2) — mirrors <see cref="Channels.ChannelRepository.FindOrCreateSemiPublic"/>'s
    /// $setOnInsert-upsert + duplicate-key-retry-once idiom, backed by the unique
    /// <c>ux_channelId_battleTag</c> index. Used for lazy recipient materialization (a DM's recipient
    /// membership is created on first successfully-delivered message, D4) where a genuine race —
    /// e.g. two concurrent sends both trying to materialize the same recipient — must resolve to
    /// exactly one row rather than surfacing a raw duplicate-key write exception. The BattleTag is
    /// lowercased in BOTH the match filter and the $setOnInsert (see the class doc) so a JWT-cased
    /// caller resolves to the same row a pair-key-cased materialization created — never a duplicate.
    /// </summary>
    public async Task<ChannelMembership> InsertIfAbsent(ChannelMembership membership)
    {
        var tag = NormalizeTag(membership.BattleTag);
        var filter = Builders<ChannelMembership>.Filter.Where(m =>
            m.ChannelId == membership.ChannelId && m.BattleTag == tag);
        var update = Builders<ChannelMembership>.Update
            .SetOnInsert(m => m.Id, membership.Id ?? ObjectId.GenerateNewId().ToString())
            .SetOnInsert(m => m.ChannelId, membership.ChannelId)
            .SetOnInsert(m => m.BattleTag, tag)
            .SetOnInsert(m => m.Role, membership.Role)
            .SetOnInsert(m => m.NotificationLevel, membership.NotificationLevel)
            .SetOnInsert(m => m.LastReadSeq, membership.LastReadSeq)
            .SetOnInsert(m => m.JoinedAt, membership.JoinedAt);
        var options = new FindOneAndUpdateOptions<ChannelMembership>
        {
            IsUpsert = true,
            ReturnDocument = ReturnDocument.After,
        };

        return await RetryOnceOnDuplicateKey(() => Memberships.FindOneAndUpdateAsync(filter, update, options));
    }
}
