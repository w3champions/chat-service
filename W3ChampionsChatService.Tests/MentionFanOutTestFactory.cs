using MongoDB.Driver;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Memberships;
using W3ChampionsChatService.Mentions;
using W3ChampionsChatService.Sessions;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// Builds a throwaway <see cref="MentionFanOut"/> for hub tests that require it as a constructor
/// dependency (the C6 Task 5 ctor growth, D15) but do NOT assert on mention delivery. Its pushes go to
/// an ignored capture sink and its <see cref="SessionRegistry"/> is empty, so even if a test's send
/// DID carry mention markup the fan-out would resolve no live targets. In practice the SendMessage call
/// site skips the fan-out entirely for a message with no mention tags (the common case for these
/// tests), so the repos below are never touched.
/// <para>
/// Centralised so the fan-out's dependency list is threaded through ONE place instead of every hub-test
/// setup — mirroring <see cref="FanOutEngineTestFactory"/>. Tests that DO assert on mention delivery
/// (see <c>MentionFanOutTests</c> / the mention cases in <c>ChatHubSendMessageTests</c>) construct it
/// explicitly with their own capture harness + shared repos.
/// </para>
/// </summary>
internal static class MentionFanOutTestFactory
{
    internal static MentionFanOut CreateIgnored(MongoClient mongoClient)
    {
        var harness = new HubPushCaptureHarness();
        var channelRepository = new ChannelRepository(mongoClient);
        return new MentionFanOut(
            harness.HubContext,
            new SessionRegistry(),
            new MembershipRepository(mongoClient, channelRepository),
            new MentionInboxRepository(mongoClient));
    }
}
