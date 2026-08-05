using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MongoDB.Driver;
using Serilog;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Memberships;
using W3ChampionsChatService.Messages;
using W3ChampionsChatService.Users;

namespace W3ChampionsChatService.Domain;

/// <summary>
/// Weekly maintenance (driven by WeeklyCleanupService):
/// (a) semi-public channel GC — a semiPublic channel with zero memberships AND zero stored
///     messages is dead (message TTL already removed anything older than retention);
/// (b) membership pruning for users idle &gt; 1 year per user_directory.lastSeenAt — fix round 1 (F6)
///     extends this to ALSO delete the same idle users' <see cref="Memberships.NotificationPreference"/>
///     rows, so that carrier doesn't outlive the membership it was written to survive. The pref
///     collection has no TTL of its own (PR36 D2 — it must persist indefinitely across an ordinary
///     leave/rejoin), so this job is its ONLY GC path.
/// (c) match-channel-hygiene brief (2026-08-05), Part 2 — orphaned-membership sweep: a channel doc can TTL
///     out (e.g. a System match channel's ttl_expiresAt, 24h after creation) while its membership rows
///     survive — normally self-healed at connect time (<see cref="Protocol.SessionStateAssembler.AssembleAndSeed"/>),
///     but a user who never reconnects would otherwise leave those rows stranded until the 365-day idle
///     sweep (b). This DB-hygiene pass catches them on the same weekly cadence instead.
/// Group/DM shells need no job — their ExpiresAt TTL handles them.
/// </summary>
public class CleanupJobs(MongoClient mongoClient) : MongoDbRepositoryBase(mongoClient)
{
    /// <summary>
    /// Part 2 batch size — fix round 1 (finding F5, aligned with the F1 fix): a DUAL-purpose bound for
    /// <see cref="SweepOrphanedMemberships"/>, and BOTH halves are now bounded, unlike before this round:
    /// <list type="bullet">
    /// <item>(a) the PAGE SIZE of the range-paginated distinct-ChannelId discovery scan
    /// (<see cref="LoadDistinctChannelIdsPage"/>). Before this round, discovery used a single
    /// <c>DistinctAsync</c> call — the <c>distinct</c> command packs EVERY distinct value into one reply
    /// document, capped at MongoDB's 16MB per-document limit, and was empirically PROVEN to throw
    /// "distinct too big" at ≈450k distinct ids, after which the whole weekly job died forever (finding
    /// F1). Range pagination over <c>ux_channelId_battleTag</c> keeps every individual round-trip's
    /// result set bounded by this constant regardless of how large the true distinct-id cardinality
    /// grows — the total scan is still proportional to the full membership collection, but no single
    /// Mongo reply is ever unbounded again.</item>
    /// <item>(b) the existence-check <c>$in</c> + delete batch size per page (unchanged in shape from the
    /// original design — this was already bounded before F1).</item>
    /// </list>
    /// Not spec text; hard-coded, adjust here only (mirrors <c>ChatHub.Channels.MaxPresenceRegisterAttempts</c>'s
    /// local-const-not-ChatLimits precedent — this is an internal job-tuning knob, not a client-facing
    /// limit). Internal (assembly has InternalsVisibleTo, ChatHub.cs) so a test can pin the batching
    /// boundary without hardcoding a duplicate magic number.
    /// </summary>
    internal const int OrphanSweepBatchSize = 1000;

    public async Task RunOnce(DateTime now)
    {
        await DeleteEmptySemiPublicChannels();
        await PruneIdleMemberships(now);
        await SweepOrphanedMemberships();
    }

    public async Task<long> DeleteEmptySemiPublicChannels()
    {
        var db = CreateClient();
        var channels = db.GetCollection<ChatChannel>(ChatCollections.Channels);
        var memberships = db.GetCollection<ChannelMembership>(ChatCollections.ChannelMemberships);
        var messages = db.GetCollection<ChannelMessage>(ChatCollections.Messages);

        var semiPublicChannels = await channels.Find(c => c.Type == ChannelType.SemiPublic).ToListAsync();
        long deleted = 0;
        foreach (var channel in semiPublicChannels)
        {
            var hasMembers = await memberships.Find(m => m.ChannelId == channel.Id).AnyAsync();
            if (hasMembers) continue;
            var hasMessages = await messages.Find(m => m.ChannelId == channel.Id).AnyAsync();
            if (hasMessages) continue;

            var result = await channels.DeleteOneAsync(c => c.Id == channel.Id);
            deleted += result.DeletedCount;
        }

        return deleted;
    }

    public async Task<long> PruneIdleMemberships(DateTime now)
    {
        var db = CreateClient();
        var directory = db.GetCollection<UserDirectoryEntry>(ChatCollections.UserDirectory);
        var memberships = db.GetCollection<ChannelMembership>(ChatCollections.ChannelMemberships);
        var notificationPreferences = db.GetCollection<NotificationPreference>(ChatCollections.NotificationPreferences);

        var cutoff = now - RetentionPeriods.IdleMembership;
        var idleBattleTags = await directory
            .Find(e => e.LastSeenAt < cutoff)
            .Project(e => e.BattleTag)
            .ToListAsync();
        if (idleBattleTags.Count == 0) return 0;

        // Both stores key on battleTag, and both now store it LOWERCASED: the durable membership key
        // (C5 T4 — see MembershipRepository's class doc) and, since C6 T2's D8 re-keying, the directory
        // _id/BattleTag too (the original JWT casing now lives on UserDirectoryEntry.DisplayBattleTag, not
        // on the projected BattleTag). The projected idle tags are therefore already lowercased and this
        // .ToLowerInvariant() is a no-op on them — kept as a defensive boundary normalization so a legacy
        // pre-D8 (mixed-case) directory _id still lingering in the collection (entries are kept, never
        // TTL'd) can't silently match no membership row and no-op the prune. NotificationPreference stores
        // its BattleTag lowercased too (see that repository's class doc), so the SAME key list serves it.
        var idleMembershipKeys = idleBattleTags.Select(t => t.ToLowerInvariant()).ToList();
        var result = await memberships.DeleteManyAsync(
            Builders<ChannelMembership>.Filter.In(m => m.BattleTag, idleMembershipKeys));

        // Fix round 1 (F6): the persisted NotificationPreference carrier (PR36 D2) has no TTL of its own —
        // it is deliberately durable across an ordinary leave/rejoin — so it must be swept here alongside
        // the membership rows of the SAME idle users, or it would outlive every other trace of them.
        await notificationPreferences.DeleteManyAsync(
            Builders<NotificationPreference>.Filter.In(p => p.BattleTag, idleMembershipKeys));

        return result.DeletedCount;
    }

    /// <summary>
    /// Match-channel-hygiene brief (2026-08-05), Part 2 — DB-hygiene sweep for membership rows whose
    /// channel no longer exists (see the class doc's item (c)). This is the slow-path complement to the
    /// connect-time self-heal (<see cref="Protocol.SessionStateAssembler.AssembleAndSeed"/>): it catches
    /// users who never reconnect, so an orphaned row doesn't linger until the 365-day idle sweep above.
    /// <para>
    /// Mechanics: distinct ChannelIds across ALL memberships are discovered via a RANGE-PAGINATED scan
    /// (<see cref="LoadDistinctChannelIdsPage"/> — fix round 1, finding F1; see <see cref="OrphanSweepBatchSize"/>'s
    /// doc for why the original single <c>DistinctAsync</c> call was replaced), <see cref="OrphanSweepBatchSize"/>
    /// ids at a time. Per page, an existence check against the channels collection (<c>$in</c>) yields the
    /// missing subset, which is then batch-deleted from memberships in one round-trip — this
    /// existence-check/delete body is UNCHANGED from before the F1 fix, only the discovery step that
    /// feeds it changed. A channel id with zero memberships never enters the discovery scan in the first
    /// place, so this never scans healthy channels.
    /// </para>
    /// <para>
    /// PRECONDITION (fix round 1, finding F8): the check-then-delete is safe from a TOCTOU standpoint
    /// only because a channel's <c>_id</c> is ALWAYS a fresh, server-generated
    /// <see cref="MongoDB.Bson.ObjectId"/> — every <c>FindOrCreate*</c> path (<see cref="Channels.ChannelRepository"/>)
    /// stamps <c>ObjectId.GenerateNewId().ToString()</c> via <c>$setOnInsert</c>, so an id is NEVER
    /// caller-supplied and NEVER reused across documents. That is what guarantees "no channel currently
    /// has this id" can never flip to "a DIFFERENT, unrelated channel now has this id" in the gap between
    /// this method's existence check and its delete. A future channel type keyed by a
    /// deterministic/caller-supplied id (unlike every type today) would break that guarantee and must
    /// re-derive its own safety argument before reusing this sweep unmodified.
    /// </para>
    /// </summary>
    public async Task<long> SweepOrphanedMemberships()
    {
        var db = CreateClient();
        var channels = db.GetCollection<ChatChannel>(ChatCollections.Channels);
        var memberships = db.GetCollection<ChannelMembership>(ChatCollections.ChannelMemberships);

        long deleted = 0;
        string cursorChannelId = null;
        while (true)
        {
            var page = await LoadDistinctChannelIdsPage(memberships, cursorChannelId, OrphanSweepBatchSize);
            if (page.Count == 0) break;

            // The Gt(cursor) filter on the NEXT page's query skips every remaining row of this page's
            // last-seen ChannelId too (not just the ones fetched here), so advancing the cursor to the
            // last row makes progress even when a single channel's membership count exceeds one page.
            cursorChannelId = page[^1];

            var batch = page.Distinct().ToList();
            var existingIds = await channels
                .Find(Builders<ChatChannel>.Filter.In(c => c.Id, batch))
                .Project(c => c.Id)
                .ToListAsync();
            var existingSet = new HashSet<string>(existingIds);
            var missingIds = batch.Where(id => !existingSet.Contains(id)).ToList();
            if (missingIds.Count == 0) continue;

            var result = await memberships.DeleteManyAsync(
                Builders<ChannelMembership>.Filter.In(m => m.ChannelId, missingIds));
            deleted += result.DeletedCount;
        }

        // Fix round 1 (finding F9): one operator-visible info line naming the total, mirroring the
        // connect-time self-heal's log convention (SessionStateAssembler.AssembleAndSeed) — logged only
        // when > 0, since a clean sweep with nothing to reap is not worth a log line every week.
        if (deleted > 0)
        {
            Log.Information("SweepOrphanedMemberships: deleted {Count} orphaned membership row(s)", deleted);
        }

        return deleted;
    }

    /// <summary>
    /// Fix round 1 (finding F1): the range-paginated distinct-ChannelId discovery cursor that replaces
    /// the original unbounded <c>DistinctAsync</c> call (see <see cref="OrphanSweepBatchSize"/>'s doc for
    /// the proven failure mode this fixes). Backed by <c>ux_channelId_battleTag</c> (ChannelId-leading
    /// compound index): a plain <c>Find(ChannelId &gt; cursor).SortBy(ChannelId).Limit(N)</c> page, whose
    /// documents cross the wire individually via the driver's own cursor/batch protocol — never packed
    /// into a single reply document the way <c>distinct</c> is, so the TOTAL result size across pages is
    /// genuinely unbounded while every INDIVIDUAL round-trip stays capped at <paramref name="limit"/>
    /// rows. A page can contain duplicate ChannelIds (multiple members of the same channel) — the caller
    /// dedupes. Termination: an empty page means every membership row has been visited (ChannelId is
    /// never null, so <c>Gt</c> strictly-greater pagination cannot loop).
    /// </summary>
    private static async Task<List<string>> LoadDistinctChannelIdsPage(
        IMongoCollection<ChannelMembership> memberships, string afterChannelId, int limit)
    {
        var filter = afterChannelId == null
            ? Builders<ChannelMembership>.Filter.Empty
            : Builders<ChannelMembership>.Filter.Gt(m => m.ChannelId, afterChannelId);

        return await memberships.Find(filter)
            .SortBy(m => m.ChannelId)
            .Project(m => m.ChannelId)
            .Limit(limit)
            .ToListAsync();
    }
}
