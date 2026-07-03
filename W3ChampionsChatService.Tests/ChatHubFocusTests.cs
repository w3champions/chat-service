using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Moq;
using NUnit.Framework;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.FanOut;
using W3ChampionsChatService.Mentions;
using W3ChampionsChatService.Memberships;
using W3ChampionsChatService.Mutes;
using W3ChampionsChatService.Protocol;
using W3ChampionsChatService.Sessions;
using W3ChampionsChatService.Users;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// C3 Task 9: <c>FocusChannel</c>/<c>UnfocusChannel</c> — the focused-set subscription that decides
/// who gets full message payloads and who appears in a channel's live viewer roster (acceptance 1,
/// 4). Covers the member (hot path, zero DB)/non-member (cold path, ChannelRepository) resolution
/// order and its NotFound-vs-NotMember split, the MaxFocusedChannels cap plus its idempotent-refocus
/// carve-out, and roster construction (FocusRegistry's active-viewer roster, never membership).
/// <para>
/// All hubs in a test SHARE the same registries (built once in SetUp), following the
/// ChatHubConnectionTests multi-connection idiom, so a second hub's FocusChannel call is visible in
/// the roster a first hub's FocusChannel call returns.
/// </para>
/// </summary>
public class ChatHubFocusTests : IntegrationTestBase
{
    private const string BattleTag = "peter#123";
    private const string OtherBattleTag = "alice#456";
    private const string MemberOnlyBattleTag = "ghost#789";

    private ConnectionMapping _connectionMapping;
    private ChatHistory _chatHistory;
    private UserDirectoryRepository _userDirectory;
    private MuteRepository _muteRepository;
    private MuteReconciliationService _reconcileService;
    private TicketStore _ticketStore;
    private Mock<IChatAuthenticationService> _authService;

    private ChannelRepository _channelRepository;
    private MembershipRepository _membershipRepository;
    private SessionRegistry _sessionRegistry;
    private FocusRegistry _focusRegistry;
    private OnlineMemberRegistry _onlineMemberRegistry;
    private MessageRateLimiter _messageRateLimiter;
    private ChannelCreationRateLimiter _channelCreationRateLimiter;
    private SessionStateAssembler _assembler;

    [SetUp]
    public void SetupBeforeEach()
    {
        _connectionMapping = new ConnectionMapping();
        _chatHistory = new ChatHistory();
        _userDirectory = new UserDirectoryRepository(MongoClient);
        _muteRepository = new MuteRepository(MongoClient);
        _reconcileService = new MuteReconciliationTestHarness(_connectionMapping, _muteRepository).Service;
        _ticketStore = new TicketStore();

        _authService = new Mock<IChatAuthenticationService>();
        _authService.Setup(m => m.GetUserFromIdentity(It.IsAny<W3CUserAuthentication>()))
            .ReturnsAsync((W3CUserAuthentication id) =>
                new ChatUser(id.BattleTag, id.IsAdmin, id.Name, new ProfilePicture(), null, null));

        _channelRepository = new ChannelRepository(MongoClient);
        _membershipRepository = new MembershipRepository(MongoClient, _channelRepository);
        _sessionRegistry = new SessionRegistry();
        _focusRegistry = new FocusRegistry();
        _onlineMemberRegistry = new OnlineMemberRegistry();
        _messageRateLimiter = new MessageRateLimiter();
        _channelCreationRateLimiter = new ChannelCreationRateLimiter();
        _assembler = new SessionStateAssembler(
            _membershipRepository,
            _channelRepository,
            _muteRepository,
            _authService.Object,
            _onlineMemberRegistry,
            _connectionMapping);
    }

    private ChatHub BuildHub(string connectionId)
    {
        var hub = new ChatHub(
            _connectionMapping,
            _chatHistory,
            _reconcileService,
            _ticketStore,
            _sessionRegistry,
            _userDirectory,
            _assembler,
            _focusRegistry,
            _onlineMemberRegistry,
            _messageRateLimiter,
            TimeProvider.System,
            _channelRepository,
            _membershipRepository,
            _channelCreationRateLimiter,
            new W3ChampionsChatService.Messages.MessageRepository(MongoClient),
            FanOutEngineTestFactory.CreateIgnored(),
            ViewersAccumulatorTestFactory.CreateIgnored(),
            new NoOpMentionInboxCleaner());

        hub.Clients = new Mock<IHubCallerClients>().Object;

        var context = new Mock<HubCallerContext>();
        context.Setup(c => c.ConnectionId).Returns(connectionId);
        hub.Context = context.Object;
        hub.Groups = new Mock<IGroupManager>().Object;

        return hub;
    }

    // Registers a live session for battleTag under connectionId (SessionRegistry.Register), needed
    // for ISessionRegistry.TryGetByConnectionId / GetByBattleTag to resolve during FocusChannel. The
    // HubCallerContext is null-tolerated (mirrors SessionRegistryTests/ChatSession's doc comment) —
    // these tests never trigger a displacement abort.
    private void RegisterSession(string connectionId, string battleTag, string name = null) =>
        _sessionRegistry.Register(
            connectionId,
            new W3CUserAuthentication { BattleTag = battleTag, Name = name ?? battleTag.Split('#')[0] },
            null);

    private async Task<ChatChannel> CreateChannel(string name = "general", ChannelType type = ChannelType.Public)
    {
        var channel = new ChatChannel { Type = type, Name = name, NormalizedName = ChannelNames.Normalize(name) };
        await _channelRepository.Insert(channel);
        return channel;
    }

    // Seeds OnlineMemberRegistry the way SessionStateAssembler.AssembleAndSeed does at connect —
    // this is the hot-path "IS a member" signal FocusChannel reads (zero DB).
    private void SeedMembership(string connectionId, string channelId, string battleTag) =>
        _onlineMemberRegistry.Join(channelId, connectionId, new MemberState(battleTag, NotificationLevel.All, 0));

    [Test]
    public async Task FocusChannel_Member_ReturnsOk_WithFullViewerRoster()
    {
        var channel = await CreateChannel();

        RegisterSession("conn-peter", BattleTag, "Peter");
        RegisterSession("conn-alice", OtherBattleTag, "Alice");
        RegisterSession("conn-ghost", MemberOnlyBattleTag, "Ghost");
        SeedMembership("conn-peter", channel.Id, BattleTag);
        SeedMembership("conn-alice", channel.Id, OtherBattleTag);
        // Ghost is a MEMBER of the channel but never focuses it — discriminates the roster source:
        // if FocusChannel ever read OnlineMemberRegistry instead of FocusRegistry for the roster,
        // Ghost would wrongly appear below.
        SeedMembership("conn-ghost", channel.Id, MemberOnlyBattleTag);

        // Alice focuses first on her own hub connection (shared registries — same idiom as
        // ChatHubConnectionTests' multi-connection displacement tests).
        var aliceHub = BuildHub("conn-alice");
        var aliceResult = await aliceHub.FocusChannel(channel.Id);
        Assert.AreEqual(ChatResultCode.Ok, aliceResult.Code, "Alice's own focus must succeed");

        var peterHub = BuildHub("conn-peter");
        var peterResult = await peterHub.FocusChannel(channel.Id);

        Assert.AreEqual(ChatResultCode.Ok, peterResult.Code);
        Assert.That(peterResult.Viewers.Select(v => v.BattleTag), Is.EquivalentTo(new[] { BattleTag, OtherBattleTag }),
            "The roster must include BOTH active (online AND focused) viewers, not just the caller — " +
            "and must EXCLUDE Ghost, who is a member but never focused (roster is FocusRegistry's active " +
            "set, never OnlineMemberRegistry's membership)");

        var peterViewer = peterResult.Viewers.Single(v => v.BattleTag == BattleTag);
        var aliceViewer = peterResult.Viewers.Single(v => v.BattleTag == OtherBattleTag);
        Assert.AreEqual("Peter", peterViewer.Name, "The viewer name must resolve from the live session identity");
        Assert.AreEqual("Alice", aliceViewer.Name);
    }

    [Test]
    public async Task FocusChannel_ViewerWithNoLiveSession_NameFallsBackToBattleTag()
    {
        // A real (narrow) race: a connection's FocusRegistry entry can briefly outlive its
        // SessionRegistry entry (old socket's OnDisconnectedAsync hasn't fired yet after a
        // reconnect/teardown). ResolveViewerName must fall back to the battleTag rather than
        // dropping the roster entry or throwing.
        const string GhostBattleTag = "ghost#999";
        var channel = await CreateChannel();

        RegisterSession("conn-ghost", GhostBattleTag, "GhostName");
        SeedMembership("conn-ghost", channel.Id, GhostBattleTag);
        var ghostHub = BuildHub("conn-ghost");
        await ghostHub.FocusChannel(channel.Id);

        // Simulate the session having been torn down WITHOUT FocusRegistry being cleared yet.
        _sessionRegistry.Unregister("conn-ghost");

        RegisterSession("conn-peter", BattleTag, "Peter");
        SeedMembership("conn-peter", channel.Id, BattleTag);
        var peterHub = BuildHub("conn-peter");

        var result = await peterHub.FocusChannel(channel.Id);

        var ghostViewer = result.Viewers.Single(v => v.BattleTag == GhostBattleTag);
        Assert.AreEqual(GhostBattleTag, ghostViewer.Name,
            "A roster entry with no live session must fall back to the battleTag, not throw or vanish");
    }

    [Test]
    public async Task FocusChannel_NonMember_ReturnsNotMember()
    {
        var channel = await CreateChannel();
        RegisterSession("conn-1", BattleTag);
        // Deliberately no SeedMembership — the caller has a live session but is not a member.

        var hub = BuildHub("conn-1");
        var result = await hub.FocusChannel(channel.Id);

        Assert.AreEqual(ChatResultCode.NotMember, result.Code);
        Assert.IsNull(result.Viewers);
    }

    [Test]
    public async Task FocusChannel_UnknownChannel_ReturnsNotFound()
    {
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");

        var result = await hub.FocusChannel("no-such-channel-id");

        Assert.AreEqual(ChatResultCode.NotFound, result.Code);
        Assert.IsNull(result.Viewers);
    }

    [Test]
    public async Task FocusChannel_UnregisteredConnection_ReturnsPermissionDenied_FailClosed()
    {
        // No RegisterSession call: the connection has no live session (never authenticated, or its
        // session was displaced/torn down) — must be denied outright, never NotFound/NotMember.
        var channel = await CreateChannel();
        var hub = BuildHub("conn-ghost");

        var result = await hub.FocusChannel(channel.Id);

        Assert.AreEqual(ChatResultCode.PermissionDenied, result.Code);
    }

    [Test]
    public async Task FocusChannel_EleventhChannel_ReturnsPermissionDenied()
    {
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");

        var channels = new ChatChannel[ChatLimits.MaxFocusedChannels + 1];
        for (var i = 0; i < channels.Length; i++)
        {
            channels[i] = await CreateChannel($"chan-{i}");
            SeedMembership("conn-1", channels[i].Id, BattleTag);
        }

        for (var i = 0; i < ChatLimits.MaxFocusedChannels; i++)
        {
            var result = await hub.FocusChannel(channels[i].Id);
            Assert.AreEqual(ChatResultCode.Ok, result.Code, $"Channel #{i} is within the cap and must succeed");
        }

        var eleventh = await hub.FocusChannel(channels[ChatLimits.MaxFocusedChannels].Id);

        Assert.AreEqual(ChatResultCode.PermissionDenied, eleventh.Code,
            "An 11th DISTINCT focused channel must be denied once the cap is already saturated");
    }

    [Test]
    public async Task FocusChannel_Idempotent_RefocusSameChannel_Ok_NoDuplicateRosterEntry()
    {
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");
        var channel = await CreateChannel();
        SeedMembership("conn-1", channel.Id, BattleTag);

        var first = await hub.FocusChannel(channel.Id);
        var second = await hub.FocusChannel(channel.Id);

        Assert.AreEqual(ChatResultCode.Ok, first.Code);
        Assert.AreEqual(ChatResultCode.Ok, second.Code, "Re-focusing an already-focused channel must still be Ok");
        Assert.AreEqual(1, second.Viewers.Count, "Re-focusing must not duplicate the caller's own roster entry");
    }

    [Test]
    public async Task FocusChannel_Idempotent_RefocusAtCap_DoesNotCountAgainstCap()
    {
        // Saturate the cap with 10 distinct channels, then re-focus one already-focused channel — the
        // re-focus must succeed (Ok) even though the connection is AT the cap, because it is not a
        // NEW distinct channel.
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");

        var channels = new ChatChannel[ChatLimits.MaxFocusedChannels];
        for (var i = 0; i < channels.Length; i++)
        {
            channels[i] = await CreateChannel($"cap-chan-{i}");
            SeedMembership("conn-1", channels[i].Id, BattleTag);
            var focusResult = await hub.FocusChannel(channels[i].Id);
            Assert.AreEqual(ChatResultCode.Ok, focusResult.Code);
        }

        var refocus = await hub.FocusChannel(channels[0].Id);

        Assert.AreEqual(ChatResultCode.Ok, refocus.Code,
            "Re-focusing a channel already in a saturated 10-channel focused set must stay Ok");
    }

    [Test]
    public async Task UnfocusChannel_RemovesFromRegistry_ReturnsOk()
    {
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");
        var channel = await CreateChannel();
        SeedMembership("conn-1", channel.Id, BattleTag);
        await hub.FocusChannel(channel.Id);

        var result = await hub.UnfocusChannel(channel.Id);

        Assert.AreEqual(ChatResultCode.Ok, result.Code);
        Assert.IsEmpty(_focusRegistry.GetFocusedConnections(channel.Id),
            "FocusRegistry must hold no entry for the connection after Unfocus");
        Assert.IsEmpty(_focusRegistry.GetFocusedChannels("conn-1"),
            "The connection's focused-channel set must be empty after Unfocus");
    }

    [Test]
    public async Task UnfocusChannel_NotFocused_StillReturnsOk_Idempotent()
    {
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");

        var result = await hub.UnfocusChannel("never-focused-channel-id");

        Assert.AreEqual(ChatResultCode.Ok, result.Code, "Unfocusing a non-focused channel is a no-op, still Ok");
    }

    [Test]
    public async Task Focus_RecordsViewerChange_ForAccumulator()
    {
        // HONEST STUB (Task 14 owns ViewersAccumulator/ViewersChanged batching — not built here, per
        // brief). The only observable "viewer changed" signal today is FocusRegistry itself: assert
        // FocusChannel updates it. Task 14 later routes this through the accumulator.
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");
        var channel = await CreateChannel();
        SeedMembership("conn-1", channel.Id, BattleTag);

        await hub.FocusChannel(channel.Id);

        Assert.That(_focusRegistry.GetRoster(channel.Id), Is.EquivalentTo(new[] { BattleTag }));
        Assert.That(_focusRegistry.GetFocusedConnections(channel.Id), Is.EquivalentTo(new[] { "conn-1" }));
    }
}
