using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Channels;

public class ChannelRepository(MongoClient mongoClient) : MongoDbRepositoryBase(mongoClient)
{
    private IMongoCollection<ChatChannel> Channels => CreateCollection<ChatChannel>(ChatCollections.Channels);

    public Task Insert(ChatChannel channel) => Channels.InsertOneAsync(channel);

    public Task<ChatChannel> Load(string id) => Channels.Find(c => c.Id == id).FirstOrDefaultAsync();

    public Task<List<ChatChannel>> LoadByIds(IEnumerable<string> ids) =>
        Channels.Find(Builders<ChatChannel>.Filter.In(c => c.Id, ids.ToList())).ToListAsync();

    /// <summary>
    /// Server-side count of the subset of <paramref name="ids"/> that are name-joinable
    /// (<see cref="ChannelType.Public"/> or <see cref="ChannelType.SemiPublic"/>) — 2026-08-05 PR36
    /// feedback, Part 3: backs <see cref="Memberships.MembershipRepository.CountNameJoinableMembershipsForUser"/>'s
    /// minimal-payload rewrite, so the join-cap gate counts via <c>CountDocumentsAsync</c> instead of
    /// pulling every channel document behind a user's memberships just to count a type-filtered subset.
    /// A channel id with no matching document (deleted channel behind an orphan membership) simply
    /// doesn't count — same as <see cref="LoadByIds"/> silently omitting it.
    /// </summary>
    public Task<long> CountNameJoinableAmongIds(IReadOnlyCollection<string> ids)
    {
        if (ids.Count == 0)
        {
            return Task.FromResult(0L);
        }

        var filterBuilder = Builders<ChatChannel>.Filter;
        var filter = filterBuilder.And(
            filterBuilder.In(c => c.Id, ids),
            filterBuilder.Or(
                filterBuilder.Eq(c => c.Type, ChannelType.Public),
                filterBuilder.Eq(c => c.Type, ChannelType.SemiPublic)));

        return Channels.CountDocumentsAsync(filter);
    }

    public Task<ChatChannel> LoadByNormalizedName(ChannelType type, string normalizedName) =>
        Channels.Find(c => c.Type == type && c.NormalizedName == normalizedName).FirstOrDefaultAsync();

    /// <summary>
    /// Finds a name-joinable channel by normalized name across ALL types (not scoped to one
    /// ChannelType) — backs the join resolution order: a name match on an existing
    /// non-name-joinable type (e.g. System) must be distinguishable from "no match" so the
    /// caller can reject with PermissionDenied instead of falling through to implicit create.
    /// Virtual: a test seam (fix round 1, finding F2b — mirrors <c>MembershipRepository</c>'s
    /// virtual-method spy idiom) so a test can prove <c>ChatHub.Channels.JoinChannel</c>'s new
    /// null/whitespace-name guard issues ZERO channel-collection reads — this is the FIRST such
    /// read the method performs, so a zero call count here proves the whole DB-read path was
    /// pre-empted.
    /// </summary>
    public virtual Task<ChatChannel> LoadAnyByNormalizedName(string normalizedName) =>
        Channels.Find(c => c.NormalizedName == normalizedName).FirstOrDefaultAsync();

    public Task<List<ChatChannel>> LoadAllOfType(ChannelType type) =>
        Channels.Find(c => c.Type == type).ToListAsync();

    /// <summary>
    /// Atomically allocates the next per-channel sequence number via findOneAndUpdate $inc
    /// on the channel doc, also stamping LastMessageAt (C1 amendment: lastSeq + lastMessageAt
    /// maintained on every message-insert path). Strictly monotonic under concurrency —
    /// guaranteed by MongoDB single-document $inc atomicity, so it holds regardless of
    /// service-instance count (the service also runs single-instance by design).
    /// <paramref name="shellExpiresAt"/> (C5 D10): when non-null, the SAME atomic write also
    /// $sets ExpiresAt — the caller passes <c>ExpiryCalculator.ForChannelShell(channel, now)</c> for
    /// Dm/GroupDm sends so the shell TTL is maintained on every message without a second round-trip.
    /// When null (the default — every pre-C5 caller), ExpiresAt is left completely untouched: public/
    /// semiPublic/System channels must never have this field written (they are creation-anchored or
    /// permanent, never message-anchored).
    /// </summary>
    public async Task<long> AllocateSeq(string channelId, DateTime now, DateTime? shellExpiresAt = null)
    {
        var update = Builders<ChatChannel>.Update
            .Inc(c => c.LastSeq, 1)
            .Set(c => c.LastMessageAt, now);
        if (shellExpiresAt.HasValue)
        {
            update = update.Set(c => c.ExpiresAt, shellExpiresAt.Value);
        }

        var updated = await Channels.FindOneAndUpdateAsync<ChatChannel>(
            c => c.Id == channelId,
            update,
            new FindOneAndUpdateOptions<ChatChannel> { ReturnDocument = ReturnDocument.After });

        if (updated == null)
        {
            throw new InvalidOperationException($"Cannot allocate seq: channel {channelId} does not exist");
        }

        return updated.LastSeq;
    }

    /// <summary>
    /// Implicit find-or-create for semiPublic channels (join resolution — acceptance 9a):
    /// $setOnInsert upsert keyed (Type=SemiPublic, NormalizedName), mirroring
    /// PublicChannelSeeder's idempotent pattern. Backed by the unique partial index
    /// ux_type_normalizedName (ChatDomainIndexes.EnsureChannelIndexes). A genuine concurrent
    /// race — two joiners implicitly creating the same brand-new name at once — can make the
    /// losing upsert's insert half violate that index (surfaces as MongoCommandException,
    /// Code 11000/"DuplicateKey" — findAndModify is a single command, not a bulk-write op, so
    /// this is NOT MongoWriteException, which only wraps the insert/update/delete write-command
    /// family); retried once, after which the winner's row is visible and the retry resolves
    /// as a plain match.
    /// </summary>
    public async Task<ChatChannel> FindOrCreateSemiPublic(string name, DateTime now)
    {
        var normalized = ChannelNames.Normalize(name);
        var filter = Builders<ChatChannel>.Filter.Where(c => c.Type == ChannelType.SemiPublic && c.NormalizedName == normalized);
        var update = Builders<ChatChannel>.Update
            .SetOnInsert(c => c.Id, ObjectId.GenerateNewId().ToString())
            .SetOnInsert(c => c.Name, name)
            .SetOnInsert(c => c.LastSeq, 0L)
            .SetOnInsert(c => c.LastMessageAt, now);
        var options = new FindOneAndUpdateOptions<ChatChannel>
        {
            IsUpsert = true,
            ReturnDocument = ReturnDocument.After,
        };

        return await RetryOnceOnDuplicateKey(() => Channels.FindOneAndUpdateAsync(filter, update, options));
    }

    /// <summary>
    /// Find-or-create for 1:1 Dm shells, keyed by <see cref="DmPairKey"/> (C5 T2) — mirrors
    /// <see cref="FindOrCreateSemiPublic"/>'s $setOnInsert-upsert + duplicate-key-retry-once idiom
    /// VERBATIM, backed by the unique partial index <c>ux_pairKey_dm</c> (Type == Dm). Guards its
    /// battleTag args against null/empty (C1 amendment 2): <see cref="DmPairKey.For"/> NREs on a null
    /// argument via its internal <c>.Trim()</c>, so callers get a clear, typed failure here instead.
    /// On insert, <paramref name="initiator"/> is stamped as <c>RequestInitiatedBy</c> and
    /// <c>ExpiresAt</c> is computed via <see cref="ExpiryCalculator.ForChannelShell"/> against
    /// <paramref name="state"/> (+30d Pending / +1y Accepted-at-birth for friends, D10) — the
    /// C1-amendment gap this task closes. Two concurrent calls for the SAME pair (either argument
    /// order, either direction) resolve to exactly one document.
    /// </summary>
    public async Task<ChatChannel> FindOrCreateDm(
        string battleTagA, string battleTagB, string initiator, DmRequestState state, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(battleTagA);
        ArgumentException.ThrowIfNullOrWhiteSpace(battleTagB);
        ArgumentException.ThrowIfNullOrWhiteSpace(initiator);

        var pairKey = DmPairKey.For(battleTagA, battleTagB);
        var expiresAt = ExpiryCalculator.ForChannelShell(new ChatChannel { Type = ChannelType.Dm, RequestState = state }, now);

        var filter = Builders<ChatChannel>.Filter.Where(c => c.Type == ChannelType.Dm && c.PairKey == pairKey);
        var update = Builders<ChatChannel>.Update
            .SetOnInsert(c => c.Id, ObjectId.GenerateNewId().ToString())
            .SetOnInsert(c => c.PairKey, pairKey)
            .SetOnInsert(c => c.RequestState, state)
            .SetOnInsert(c => c.RequestInitiatedBy, initiator)
            .SetOnInsert(c => c.LastSeq, 0L)
            .SetOnInsert(c => c.LastMessageAt, now)
            .SetOnInsert(c => c.ExpiresAt, expiresAt);
        var options = new FindOneAndUpdateOptions<ChatChannel>
        {
            IsUpsert = true,
            ReturnDocument = ReturnDocument.After,
        };

        return await RetryOnceOnDuplicateKey(() => Channels.FindOneAndUpdateAsync(filter, update, options));
    }

    /// <summary>
    /// Loads an existing Dm shell by pair-key, if any — a cheap existence check that skips the
    /// upsert write path entirely (used by call sites that only need to know "does a conversation
    /// already exist" without also find-or-creating one, e.g. the stranger-cap skip in T3).
    /// Equality on <c>PairKey</c> is an indexed point lookup — backed by the unique partial index
    /// <c>ux_pairKey_dm</c> (<see cref="Domain.ChatDomainIndexes"/>) — so this is efficient, not a scan.
    /// </summary>
    public Task<ChatChannel> LoadByPairKey(string battleTagA, string battleTagB)
    {
        var pairKey = DmPairKey.For(battleTagA, battleTagB);
        return Channels.Find(c => c.Type == ChannelType.Dm && c.PairKey == pairKey).FirstOrDefaultAsync();
    }

    /// <summary>
    /// Find-or-create for System channel shells (match/lobby/clan, C7 Task 4), keyed by
    /// (SystemKind, SystemRef) — mirrors <see cref="FindOrCreateDm"/>'s $setOnInsert-upsert +
    /// duplicate-key-retry-once idiom VERBATIM, backed by the unique partial index
    /// <c>ux_systemKind_systemRef</c> (Type == System). <c>ExpiresAt</c> is computed via
    /// <see cref="ExpiryCalculator.ForChannelShell"/> against <paramref name="kind"/> — THIS is the
    /// C1-amendment wiring this task closes: a Match shell gets <c>now + RetentionPeriods.MatchChannel</c>
    /// (24h), while a Clan shell (permanent) computes null. A null result is deliberately left OUT of
    /// the $setOnInsert chain entirely rather than set explicitly — <see cref="ChatChannel.ExpiresAt"/>
    /// is <c>[BsonIgnoreIfNull]</c> and must stay ABSENT on a permanent document (an explicit
    /// <c>$setOnInsert: {ExpiresAt: null}</c> would write the field with a null value instead of
    /// omitting it, which the TTL convention documented on <see cref="Domain.ChatDomainIndexes"/>
    /// requires). Two concurrent calls for the SAME (kind, ref) resolve to exactly one document.
    /// </summary>
    public async Task<ChatChannel> FindOrCreateSystem(SystemChannelKind kind, string systemRef, string name, DateTime now)
    {
        var expiresAt = ExpiryCalculator.ForChannelShell(new ChatChannel { Type = ChannelType.System, SystemKind = kind }, now);

        var filter = Builders<ChatChannel>.Filter.Where(c =>
            c.Type == ChannelType.System && c.SystemKind == kind && c.SystemRef == systemRef);
        var update = Builders<ChatChannel>.Update
            .SetOnInsert(c => c.Id, ObjectId.GenerateNewId().ToString())
            .SetOnInsert(c => c.SystemKind, kind)
            .SetOnInsert(c => c.SystemRef, systemRef)
            .SetOnInsert(c => c.Name, name)
            .SetOnInsert(c => c.LastSeq, 0L)
            .SetOnInsert(c => c.LastMessageAt, now);
        if (expiresAt.HasValue)
        {
            update = update.SetOnInsert(c => c.ExpiresAt, expiresAt.Value);
        }

        var options = new FindOneAndUpdateOptions<ChatChannel>
        {
            IsUpsert = true,
            ReturnDocument = ReturnDocument.After,
        };

        return await RetryOnceOnDuplicateKey(() => Channels.FindOneAndUpdateAsync(filter, update, options));
    }

    /// <summary>
    /// Loads an existing System channel shell by (SystemKind, SystemRef), if any — the lookup
    /// counterpart of <see cref="FindOrCreateSystem"/> for call sites that only need an existence
    /// check (mirrors <see cref="LoadByPairKey"/>'s shape).
    /// </summary>
    public Task<ChatChannel> LoadBySystemRef(SystemChannelKind kind, string systemRef) =>
        Channels.Find(c => c.Type == ChannelType.System && c.SystemKind == kind && c.SystemRef == systemRef).FirstOrDefaultAsync();

    /// <summary>
    /// Consent-machine accept transition (C5 D3/D10): a conditional write that flips
    /// <c>RequestState</c> Pending → Accepted ONLY when it is currently Pending, and in the same
    /// atomic update sets <c>ExpiresAt</c> to the +1y accepted-shell expiry. Returns false (no-op)
    /// when the channel is already Accepted or missing — the accept-race guard: a reply-accept
    /// racing an explicit AcceptRequest (or two AcceptRequest calls) can only ever win once.
    /// </summary>
    public async Task<bool> SetRequestAccepted(string channelId, DateTime now)
    {
        var expiresAt = ExpiryCalculator.ForChannelShell(new ChatChannel { Type = ChannelType.Dm, RequestState = DmRequestState.Accepted }, now);

        var filter = Builders<ChatChannel>.Filter.Where(c =>
            c.Id == channelId && c.Type == ChannelType.Dm && c.RequestState == DmRequestState.Pending);
        var update = Builders<ChatChannel>.Update
            .Set(c => c.RequestState, DmRequestState.Accepted)
            .Set(c => c.ExpiresAt, expiresAt);

        var result = await Channels.UpdateOneAsync(filter, update);
        return result.ModifiedCount == 1;
    }

    /// <summary>
    /// The (epoch, seq) ADMISSION GATE for a roster assertion, expressed as ONE conditional update —
    /// admit and stamp are atomic (the SetRequestAccepted idiom). Returns true when the assertion may
    /// be applied. Staleness semantics (plan D3): (a) no epoch stored yet => accept; (b) SAME epoch =>
    /// accept only when seq is STRICTLY greater than the stored one; (c) DIFFERENT epoch => accept and
    /// RE-ANCHOR (epochs are opaque and unordered, and a discard rule would permanently wedge a channel
    /// that survived an mm restart — the caller logs this anomaly). A detached channel is never
    /// admitted: the domain layer checks Detached first, and this filter re-checks it as the durable
    /// backstop against a concurrent detach.
    /// Virtual: a test seam (the MembershipRepository.LoadForChannel idiom) so a subclass can block
    /// inside this call and prove the per-ref gate actually serializes two concurrent assertions.
    /// </summary>
    public virtual async Task<bool> TryAdvanceAssertion(string channelId, string epoch, long seq)
    {
        var fb = Builders<ChatChannel>.Filter;
        var admissible = fb.Or(
            fb.Exists(c => c.AssertEpoch, false),
            fb.Ne(c => c.AssertEpoch, epoch),
            fb.And(
                fb.Eq(c => c.AssertEpoch, epoch),
                // Strict Lt (not Lte) is REDUNDANT BY DESIGN with the ModifiedCount check below: either
                // one alone already rejects an equal-seq replay (Lt excludes the doc from the filter;
                // ModifiedCount==0 catches a no-op $set when the filter is relaxed to Lte). Only the
                // COMBINATION is mutation-tested/pinned — do not relax one on the assumption the other
                // is independently test-pinned (Task 1 review r1, mutations M1/M3).
                fb.Or(fb.Exists(c => c.AssertSeq, false), fb.Lt(c => c.AssertSeq, seq))));
        var filter = fb.And(fb.Eq(c => c.Id, channelId), fb.Ne(c => c.Detached, true), admissible);
        var update = Builders<ChatChannel>.Update
            .Set(c => c.AssertEpoch, epoch)
            .Set(c => c.AssertSeq, seq);

        var result = await Channels.UpdateOneAsync(filter, update);
        // ModifiedCount (not MatchedCount) is REDUNDANT BY DESIGN with the strict Lt above — see the
        // comment there. Do not switch to MatchedCount on the assumption "TryAdvanceAssertion always
        // writes when it matches"; that assumption breaks the moment Lt is ever relaxed to Lte.
        return result.ModifiedCount == 1;
    }

    /// <summary>Freezes the room (plan D4) — see ChatChannel.Detached. Idempotent.
    /// Virtual: a test seam (the TryAdvanceAssertion idiom) so a subclass can observe WHEN the latch
    /// lands and pin the plan's DETACH-LAST / adds-before-detach ordering, which is otherwise
    /// invisible in-process (nothing between the latch and the member writes reads Detached).</summary>
    public virtual Task SetDetached(string channelId) =>
        Channels.UpdateOneAsync(c => c.Id == channelId, Builders<ChatChannel>.Update.Set(c => c.Detached, true));

    /// <summary>
    /// Every System+Match channel eligible for an epoch sync — NOT detached (a detached room is excluded
    /// from every sweep by design, so it is filtered out server-side and never even loaded) AND already
    /// stamped by the assertion protocol (<c>AssertEpoch</c> exists).
    /// <para>
    /// The <c>AssertEpoch</c>-exists clause is a 2026-08-05 fix wave amendment (final review H1, plan D8
    /// amendment). Every channel the reconciliation-era mm creates is stamped by construction (create
    /// carries epoch/seq; the roster-assertion endpoint stamps on demand), so it is correctly a sweep
    /// candidate. A channel created via <c>POST /internal/channels</c> without epoch/seq and never since
    /// asserted has no <c>AssertEpoch</c> field at all — <see cref="ChatChannel.AssertEpoch"/> is
    /// <c>[BsonIgnoreIfNull]</c>, so it is genuinely absent from the document, not merely null — and is
    /// therefore invisible to this query. It falls to its own 24h creation-anchored TTL instead of being
    /// torn down by the very first post-deploy epoch sync. Without this clause, that first sync would tear
    /// down every non-detached System+Match channel already in the database at mm's deploy instant,
    /// including every in-progress ladder game's chat (~4,900 channels/day measured against production).
    /// </para>
    /// Bounded in practice by the 24h creation-anchored TTL on match channels, which is why this needs
    /// no pagination; served by the ux_systemKind_systemRef index's SystemKind prefix.
    /// </summary>
    public Task<List<ChatChannel>> LoadNonDetachedMatchChannels()
    {
        var fb = Builders<ChatChannel>.Filter;
        return Channels.Find(fb.And(
            fb.Eq(c => c.Type, ChannelType.System),
            fb.Eq(c => c.SystemKind, SystemChannelKind.Match),
            fb.Ne(c => c.Detached, true),
            fb.Exists(c => c.AssertEpoch, true))).ToListAsync();
    }

    /// <summary>
    /// Epoch-sync authority reset for the channels a sync SPARES (plan D8) — CONDITIONAL: the update
    /// only lands when the stored <c>AssertEpoch</c> differs from <paramref name="epoch"/> (absent
    /// counts as different, mirroring <see cref="TryAdvanceAssertion"/>'s rule (a)/(c) split). When it
    /// does, adopt the new epoch and reset the per-lobby counter to the 0 sentinel, so mm's first
    /// assertion under the new epoch (seq >= 1) applies cleanly. Writes 0 rather than $unset — see
    /// ChatChannel.AssertSeq.
    /// <para>
    /// A channel ALREADY anchored to the sync's own epoch is left completely untouched. Such a channel
    /// was created or asserted by mm DURING this same boot — a new lobby, or a retried assertion, that
    /// landed while the epoch sync was still retrying — so it is not "stale" in the sense this reset
    /// exists to fix. Resetting it anyway would zero out an already-advancing seq counter, re-opening
    /// the duplicate-replay window for every assertion already applied under this epoch (2026-08-05
    /// Task-4 review r1, INFO-1): a retried lower-seq assertion would be wrongly re-admitted and would
    /// apply a stale full member set, reverting the roster until mm's next assertion re-converges it.
    /// The conditional keeps the reset scoped to its actual purpose — re-anchoring channels stamped
    /// under a now-dead PRE-restart epoch — and keeps <see cref="TryAdvanceAssertion"/>'s D3(c) anomaly
    /// Warning meaningful (it fires on a genuine mismatch, not on every graceful restart).
    /// </para>
    /// </summary>
    public Task StampAssertionEpoch(string channelId, string epoch)
    {
        var fb = Builders<ChatChannel>.Filter;
        var filter = fb.And(
            fb.Eq(c => c.Id, channelId),
            fb.Or(fb.Exists(c => c.AssertEpoch, false), fb.Ne(c => c.AssertEpoch, epoch)));
        var update = Builders<ChatChannel>.Update
            .Set(c => c.AssertEpoch, epoch)
            .Set(c => c.AssertSeq, 0L);

        return Channels.UpdateOneAsync(filter, update);
    }

    /// <summary>Hard-deletes a channel doc (C5 D12 — e.g. the last group member leaving; residual
    /// memberships are cleaned up separately via <see cref="Memberships.MembershipRepository.DeleteAllForChannel"/>).
    /// Messages are left to the 90d message TTL — no reader exists for a deleted channel's history
    /// and moderators are scope-walled out of Dm/GroupDm entirely.</summary>
    public Task Delete(string channelId) => Channels.DeleteOneAsync(c => c.Id == channelId);

    /// <summary>
    /// Renames a channel — sets the display <see cref="ChatChannel.Name"/> ONLY, NEVER
    /// <see cref="ChatChannel.NormalizedName"/> (C5 D16, group rename via <c>ChatHub.RenameGroup</c>). A
    /// GroupDm deliberately keeps a null <c>NormalizedName</c> so its display name can never collide into
    /// <see cref="LoadAnyByNormalizedName"/>'s join-resolution path (which would block implicit semiPublic
    /// creation of the same display name); mutating only <c>Name</c> preserves that invariant on rename.
    /// </summary>
    public Task SetName(string channelId, string name) =>
        Channels.UpdateOneAsync(c => c.Id == channelId, Builders<ChatChannel>.Update.Set(c => c.Name, name));

    /// <summary>
    /// C4 Task 7 (D9): the eligible-channel list backing GET /api/moderation/channels — the
    /// channelId-resolution surface the website-backend's moderation proxy needs (the OLD
    /// ChatHistory-backed GET /api/chat/{chatroom} took room NAMEs directly; channels are the new unit).
    /// Eligible types mirror <see cref="ChannelModeration.IsModeratable"/> EXACTLY (Public / SemiPublic /
    /// System+Match) — expressed here as an explicit Mongo filter (a C# predicate can't be pushed into a
    /// query), so keep both definitions in sync if the scope wall ever changes. Sorted by LastMessageAt
    /// DESCENDING (most recently active first); <paramref name="limit"/> is clamped to
    /// [1, <see cref="ChatLimits.ModerationChannelsPageSize"/>] — never MongoDB's Limit(0) "no limit".
    /// </summary>
    public Task<List<ChatChannel>> LoadModeratableChannels(int limit)
    {
        var effectiveLimit = Math.Clamp(limit, 1, ChatLimits.ModerationChannelsPageSize);
        var filterBuilder = Builders<ChatChannel>.Filter;
        var filter = filterBuilder.Or(
            filterBuilder.Eq(c => c.Type, ChannelType.Public),
            filterBuilder.Eq(c => c.Type, ChannelType.SemiPublic),
            filterBuilder.And(
                filterBuilder.Eq(c => c.Type, ChannelType.System),
                filterBuilder.Eq(c => c.SystemKind, SystemChannelKind.Match)));

        return Channels.Find(filter).SortByDescending(c => c.LastMessageAt).Limit(effectiveLimit).ToListAsync();
    }
}
