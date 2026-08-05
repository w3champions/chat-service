using System.Collections.Generic;
using System.Threading.Tasks;
using MongoDB.Driver;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Memberships;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// A <see cref="MembershipRepository"/> spy (subclass over the real Mongo-backed repository — mirrors
/// <see cref="CountingUserDirectoryRepository"/>'s spy-over-real-repo idiom) that counts
/// <see cref="LoadForChannel"/> calls, so a test can assert a bounded-room search path (D1, 2026-08-05
/// follow-up: <c>ChatHub.SearchMentionCandidates</c>' SemiPublic/System member-scoping lane) never falls
/// back to the full-room membership scan <see cref="LoadForChannel"/> performs — the whole point of the
/// candidate-side <see cref="MembershipRepository.LoadMemberBattleTags"/> check being bounded to the
/// (small, already-capped) candidate list rather than the room's total membership. <see cref="LoadForChannel"/>
/// is already <c>virtual</c> for exactly this kind of test seam (see its own doc comment).
/// <para>
/// Fix round 1 (finding F6a): also counts <see cref="LoadMemberBattleTags"/> calls, so a test can assert
/// the Public lane — the ONLY channel type that must never perform ANY membership-scoping read at all —
/// genuinely performs zero, not merely zero of the full-room variant.
/// </para>
/// <para>
/// Match-channel-hygiene brief (2026-08-05), Part 1: also counts <see cref="DeleteOrphanedForUser"/>
/// calls, so a test can assert SessionStateAssembler.AssembleAndSeed's zero-orphans common case issues
/// no extra delete query, and that a second (already-healed) connect doesn't call it again.
/// </para>
/// </summary>
internal sealed class CountingMembershipRepository(MongoClient client, ChannelRepository channelRepository)
    : MembershipRepository(client, channelRepository)
{
    public int LoadForChannelCallCount { get; private set; }
    public int LoadMemberBattleTagsCallCount { get; private set; }
    public int DeleteOrphanedForUserCallCount { get; private set; }

    public override Task<List<ChannelMembership>> LoadForChannel(string channelId)
    {
        LoadForChannelCallCount++;
        return base.LoadForChannel(channelId);
    }

    public override Task<HashSet<string>> LoadMemberBattleTags(string channelId, IEnumerable<string> battleTags)
    {
        LoadMemberBattleTagsCallCount++;
        return base.LoadMemberBattleTags(channelId, battleTags);
    }

    public override Task<long> DeleteOrphanedForUser(string battleTag, IReadOnlyCollection<string> channelIds)
    {
        DeleteOrphanedForUserCallCount++;
        return base.DeleteOrphanedForUser(battleTag, channelIds);
    }
}
