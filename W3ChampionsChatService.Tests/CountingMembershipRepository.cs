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
/// </summary>
internal sealed class CountingMembershipRepository(MongoClient client, ChannelRepository channelRepository)
    : MembershipRepository(client, channelRepository)
{
    public int LoadForChannelCallCount { get; private set; }

    public override Task<List<ChannelMembership>> LoadForChannel(string channelId)
    {
        LoadForChannelCallCount++;
        return base.LoadForChannel(channelId);
    }
}
