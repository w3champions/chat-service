using System.Threading.Tasks;
using MongoDB.Driver;
using W3ChampionsChatService.Channels;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// A <see cref="ChannelRepository"/> spy (subclass over the real Mongo-backed repository — mirrors
/// <see cref="CountingMembershipRepository"/>'s spy-over-real-repo idiom) that counts
/// <see cref="LoadAnyByNormalizedName"/> calls.
/// <para>
/// Fix round 1 (finding F2b): backs the <c>JoinChannel(null)</c>/<c>JoinChannel(whitespace)</c> tests
/// proving the new early null/whitespace-name guard issues ZERO channel-collection reads —
/// <see cref="LoadAnyByNormalizedName"/> is the FIRST channel-collection read <c>JoinChannel</c>
/// performs, so a zero call count here proves the guard pre-empted the entire DB-read path, not
/// merely that one specific downstream call didn't happen to fire.
/// </para>
/// </summary>
internal sealed class CountingChannelRepository(MongoClient client) : ChannelRepository(client)
{
    public int LoadAnyByNormalizedNameCallCount { get; private set; }

    public override Task<ChatChannel> LoadAnyByNormalizedName(string normalizedName)
    {
        LoadAnyByNormalizedNameCallCount++;
        return base.LoadAnyByNormalizedName(normalizedName);
    }
}
