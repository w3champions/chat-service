using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Users;

/// <summary>
/// Cache of every user chat has seen (spec §5) — wb owns the real profile; chat never writes back.
/// <para>
/// BATTLETAG KEY CONVENTION (C6 T2 / D8): the persisted <see cref="UserDirectoryEntry.BattleTag"/>
/// (the Mongo <c>_id</c>) is ALWAYS stored lowercased, and every read/write lowercases its incoming
/// <c>battleTag</c> argument before building the Mongo filter (Mongo <c>$eq</c> is case-SENSITIVE —
/// there is no collation or CI index on <c>_id</c>). This conforms the directory to the same
/// lowercased-key convention <see cref="Memberships.MembershipRepository"/> and
/// <see cref="UserSettingsRepository"/> already use — closing a C5-identified gap where a directory
/// row keyed under a caller's JWT casing (e.g. connect via "Wolf#456", a later read via "wolf#456")
/// would silently miss on <see cref="Load"/> and duplicate on <see cref="Upsert"/>. The caller's
/// original casing survives on <see cref="UserDirectoryEntry.DisplayBattleTag"/>.
/// </para>
/// </summary>
public class UserDirectoryRepository(MongoClient mongoClient) : MongoDbRepositoryBase(mongoClient)
{
    private IMongoCollection<UserDirectoryEntry> Directory =>
        CreateCollection<UserDirectoryEntry>(ChatCollections.UserDirectory);

    /// <summary>Lowercases a battleTag to the durable directory key convention (see the class doc).</summary>
    private static string NormalizeTag(string battleTag) => battleTag.ToLowerInvariant();

    // Persists a lowercased-BattleTag COPY without mutating the caller's object (immutability) — mirrors
    // MembershipRepository.WithNormalizedBattleTag. NOTE: keep this field list in sync with
    // UserDirectoryEntry — a new field must be copied here too.
    private static UserDirectoryEntry WithNormalizedBattleTag(UserDirectoryEntry entry) =>
        new UserDirectoryEntry
        {
            BattleTag = NormalizeTag(entry.BattleTag),
            DisplayBattleTag = entry.DisplayBattleTag,
            NormalizedName = entry.NormalizedName,
            LastSeenAt = entry.LastSeenAt,
            Profile = entry.Profile,
        };

    /// <summary>
    /// Full replace-upsert — the CONNECT-time write (T3): keyed on the lowercased BattleTag, overwrites
    /// the entire document including <see cref="UserDirectoryEntry.Profile"/>. Reserved for the
    /// enrichment write, where a fresh Profile is actually available; the disconnect-time write must
    /// use <see cref="SetLastSeen"/> instead so a wb outage never clobbers a previously-cached Profile
    /// with null (the disconnect-upsert clobber guard).
    /// </summary>
    public Task Upsert(UserDirectoryEntry entry)
    {
        var normalized = WithNormalizedBattleTag(entry);
        return Directory.ReplaceOneAsync(
            e => e.BattleTag == normalized.BattleTag, normalized, new ReplaceOptions { IsUpsert = true });
    }

    /// <summary>Case-insensitive point read on the lowercased key (see class doc).</summary>
    public Task<UserDirectoryEntry> Load(string battleTag)
    {
        var tag = NormalizeTag(battleTag);
        return Directory.Find(e => e.BattleTag == tag).FirstOrDefaultAsync();
    }

    /// <summary>
    /// Partial update — the DISCONNECT-time write (D8/D9): advances only LastSeenAt, DisplayBattleTag,
    /// and NormalizedName, and NEVER touches <see cref="UserDirectoryEntry.Profile"/> (the clobber
    /// guard — a disconnect must never overwrite a previously-cached enrichment Profile with null; only
    /// the CONNECT-time <see cref="Upsert"/> may replace Profile, and only when a fresh wb read
    /// succeeded). Upserts if the row doesn't exist yet — the directory is a cache, so a first-ever
    /// disconnect for a user whose connect-time write never landed must still materialize a row.
    /// </summary>
    public Task SetLastSeen(string battleTag, string displayBattleTag, string normalizedName, DateTime now)
    {
        var tag = NormalizeTag(battleTag);
        var update = Builders<UserDirectoryEntry>.Update
            .Set(e => e.LastSeenAt, now)
            .Set(e => e.DisplayBattleTag, displayBattleTag)
            .Set(e => e.NormalizedName, normalizedName);
        return Directory.UpdateOneAsync(e => e.BattleTag == tag, update, new UpdateOptions { IsUpsert = true });
    }

    /// <summary>
    /// Tiered mention search (T8), tier 3: rows whose <see cref="UserDirectoryEntry.NormalizedName"/>
    /// starts with <paramref name="prefixLower"/> AND whose LastSeenAt is within the activity window
    /// (<paramref name="minLastSeenAt"/>, the 90d gate — <see cref="ChatLimits.MentionCandidateActivityWindow"/>)
    /// — backed by <c>ix_normalizedName_lastSeenAt</c> so both the prefix bound and the LastSeenAt
    /// filter stay index-served. <paramref name="prefixLower"/> must already be lowercased by the
    /// caller (the search boundary, T8) — this method does not re-normalize it, so an empty prefix
    /// ("" — matches every activity-eligible row) is preserved verbatim. The prefix is anchored
    /// (<c>^prefix</c>, no trailing anchor — a genuine prefix match, unlike the exact-match anchoring
    /// used elsewhere for the shadow-self fix) and regex-escaped so a battleTag containing regex
    /// metacharacters can never be misinterpreted as a pattern.
    /// </summary>
    public Task<List<UserDirectoryEntry>> SearchByNormalizedPrefix(string prefixLower, DateTime minLastSeenAt, int limit)
    {
        var filterBuilder = Builders<UserDirectoryEntry>.Filter;
        var filter = filterBuilder.And(
            filterBuilder.Regex(e => e.NormalizedName, new BsonRegularExpression("^" + Regex.Escape(prefixLower))),
            filterBuilder.Gte(e => e.LastSeenAt, minLastSeenAt));

        return Directory.Find(filter).Limit(limit).ToListAsync();
    }

    /// <summary>Batch point-read (T8/T10 enrichment) — lowercased <c>$in</c> on the primary key.</summary>
    public Task<List<UserDirectoryEntry>> LoadMany(IEnumerable<string> battleTags)
    {
        var tags = battleTags.Select(NormalizeTag).ToList();
        return Directory.Find(e => tags.Contains(e.BattleTag)).ToListAsync();
    }
}
