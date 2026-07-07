using System;
using System.Threading.Tasks;
using MongoDB.Driver;
using NUnit.Framework;
using W3ChampionsChatService.Mutes;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// Pins the lounge-mute CASE-SENSITIVITY model (Marco decision 2): the mute is STORED with the
/// moderator-entered DISPLAY casing in the <c>battleTag</c> field, while matching is CASE-INSENSITIVE
/// via the lowercased Mongo <c>_id</c> (<see cref="LoungeMute.Id"/>). The shared-with-prod document shape
/// is unchanged and there is NO destructive migration — existing all-lowercase prod rows must keep
/// matching. These are the guardrails that let admins see the real tag casing without breaking either the
/// connect-time mute resolve (<c>GetMutedPlayer</c>) or an unban (<c>DeleteLoungeMute</c>) under any casing.
/// </summary>
public class LoungeMuteCaseSensitivityTests : IntegrationTestBase
{
    private MuteRepository _muteRepository;

    [SetUp]
    public void SetupBeforeEach()
    {
        _muteRepository = new MuteRepository(MongoClient);
    }

    // The raw, typed LoungeMute collection — used to seed a LEGACY prod row directly (bypassing
    // AddLoungeMute) so the back-compat proof is independent of the new write path.
    private IMongoCollection<LoungeMute> MuteCollection =>
        MongoClient.GetDatabase(MongoDbRepositoryBase.DatabaseName).GetCollection<LoungeMute>(nameof(LoungeMute));

    private static LoungeMuteRequest MuteRequest(string battleTag, bool isShadowBan = false) => new()
    {
        battleTag = battleTag,
        endDate = DateTime.UtcNow.AddDays(1).ToString("O"),
        author = "admin#1",
        reason = "test",
        isShadowBan = isShadowBan,
    };

    [Test]
    public async Task GetMutedPlayer_MatchesCaseInsensitively_ViaLowercasedId()
    {
        // (a) Store a mute with mixed casing, then resolve it on connect with a DIFFERENT casing.
        await _muteRepository.AddLoungeMute(MuteRequest("Peter#123"));

        var resolved = await _muteRepository.GetMutedPlayer("PETER#123");

        Assert.IsNotNull(resolved, "GetMutedPlayer must match case-insensitively (via the lowercased _id)");
        Assert.AreEqual("Peter#123", resolved.battleTag, "The stored display casing is preserved on read-back");
        Assert.AreEqual("peter#123", resolved.Id, "The match key (_id) is the lowercased tag");
    }

    [Test]
    public async Task LegacyLowercasedRow_StillMatches_OnGetAndDelete_UnderAnyCasing()
    {
        // (b) BACK-COMPAT: a pre-existing prod row was written fully lowercased (battleTag == _id ==
        // lowercase). Seed that exact on-disk shape directly (a lowercase battleTag serializes _id to the
        // same lowercased value), bypassing AddLoungeMute entirely.
        await MuteCollection.InsertOneAsync(new LoungeMute
        {
            battleTag = "legacy#1",
            endDate = DateTime.UtcNow.AddDays(1),
            insertDate = DateTime.UtcNow,
            author = "admin#1",
            reason = "old ban",
            isShadowBan = false,
        });

        // Reads under any casing still find the legacy row.
        Assert.IsNotNull(await _muteRepository.GetMutedPlayer("legacy#1"), "Exact-casing read of a legacy row must match");
        Assert.IsNotNull(await _muteRepository.GetMutedPlayer("LEGACY#1"), "Mixed-casing read of a legacy row must still match");

        // A delete under a DIFFERENT casing removes the legacy row (keyed on the lowercased _id).
        var deleteResult = await _muteRepository.DeleteLoungeMute("Legacy#1");

        Assert.AreEqual(1, deleteResult.DeletedCount, "A mixed-case delete must remove the legacy lowercased row");
        Assert.IsNull(await _muteRepository.GetMutedPlayer("legacy#1"), "The legacy row must be gone after the delete");
    }

    [Test]
    public async Task ReMute_DifferentCasing_ReplacesInPlace_NoDuplicate_NoImmutableIdError()
    {
        // (c) Mute the same player twice under DIFFERENT casings. Because identity is the lowercased _id,
        // the second AddLoungeMute REPLACES the first document (it must NOT throw MongoDB's immutable-_id
        // error, since _id is unchanged across the replace) and must NOT create a duplicate row.
        await _muteRepository.AddLoungeMute(MuteRequest("peter#123"));
        await _muteRepository.AddLoungeMute(MuteRequest("Peter#123"));

        var all = await _muteRepository.GetLoungeMutes();

        Assert.AreEqual(1, all.Count, "A re-mute under different casing must REPLACE, not duplicate (same lowercased _id)");
        Assert.AreEqual("Peter#123", all[0].battleTag, "The surviving row carries the LATEST display casing");
        Assert.AreEqual("peter#123", all[0].Id, "The _id stays the lowercased match key across the replace");
    }

    [Test]
    public async Task GetLoungeMutes_ReturnsOriginalCaseBattleTag()
    {
        // (d) The admin list surfaces the moderator-entered display casing (the intended fix for
        // "admins see lowercased tags").
        await _muteRepository.AddLoungeMute(MuteRequest("MixedCase#999"));

        var all = await _muteRepository.GetLoungeMutes();

        Assert.AreEqual(1, all.Count);
        Assert.AreEqual("MixedCase#999", all[0].battleTag, "GetLoungeMutes must return the original-case battleTag");
    }
}
