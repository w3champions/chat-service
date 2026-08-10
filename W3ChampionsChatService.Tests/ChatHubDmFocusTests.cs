using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.FanOut;
using W3ChampionsChatService.Mentions;
using W3ChampionsChatService.Memberships;
using W3ChampionsChatService.Messages;
using W3ChampionsChatService.Mutes;
using W3ChampionsChatService.Protocol;
using W3ChampionsChatService.Sessions;
using W3ChampionsChatService.Users;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// C5 Task 5 (D11): Dm/GroupDm channels are excluded from the viewer-roster/<c>ViewersChanged</c>
/// system — spec §9 scopes viewer rosters to CHANNELS; DM/group presence is member-presence via the C6
/// interest index, never a streamed roster. The decline-invisibility guardrail explicitly includes
/// PRESENCE: a <c>ViewersChanged{joined:[recipient]}</c> reaching a focused sender when a declined
/// recipient peeks at the conversation would be a direct leak. <c>FocusChannel</c> must therefore never
/// call <see cref="ViewersAccumulator.RecordChange"/> for Dm/GroupDm and must always return an EMPTY
/// roster for them; <c>UnfocusChannel</c> and the disconnect teardown loop mirror the same exclusion.
/// <see cref="FocusRegistry"/> participation (focused delivery + the C6 interest derivation) is
/// UNCHANGED — only roster/accumulator participation is excluded. Public/SemiPublic/System channels are
/// unaffected (regression pin).
/// <para>
/// Hub-level fixture mirrors <c>ViewersAccumulatorTests</c>' HUB-LEVEL section: a REAL
/// <see cref="ViewersAccumulator"/> + <see cref="FocusRegistry"/> shared across hubs built from the
/// same registries, backed by a <see cref="HubPushCaptureHarness"/> so a <c>ViewersChanged</c> leak to a
/// focused sender is directly observable after a flush. NUnit constraint style.
/// </para>
/// </summary>
public class ChatHubDmFocusTests : IntegrationTestBase
{
    private const string BattleTag = "peter#123";
    private const string OtherBattleTag = "wolf#456";

    private static readonly DateTime T0 = new DateTime(2026, 7, 4, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Flush = ChatLimits.ViewersChangedFlush;

    private FakeTimeProvider _time;
    private HubPushCaptureHarness _harness;
    private ViewersAccumulator _accumulator;
    private ViewerResolver _viewerResolver;
    private FocusRegistry _focusRegistry;
    private OnlineMemberRegistry _onlineMemberRegistry;
    private SessionRegistry _sessionRegistry;
    private ConnectionMapping _connectionMapping;
    private UserDirectoryRepository _userDirectory;
    private MuteRepository _muteRepository;
    private MuteReconciliationService _reconcileService;
    private TicketStore _ticketStore;
    private Mock<IChatAuthenticationService> _authService;
    private ChannelRepository _channelRepository;
    private MembershipRepository _membershipRepository;
    private MessageRateLimiter _messageRateLimiter;
    private ReadRateLimiter _readRateLimiter;
    private ChannelCreationRateLimiter _channelCreationRateLimiter;
    private SessionStateAssembler _assembler;

    [SetUp]
    public void SetupBeforeEach()
    {
        _time = new FakeTimeProvider(new DateTimeOffset(T0, TimeSpan.Zero));
        _harness = new HubPushCaptureHarness();
        _focusRegistry = new FocusRegistry();
        _onlineMemberRegistry = new OnlineMemberRegistry();
        _sessionRegistry = new SessionRegistry();
        _connectionMapping = new ConnectionMapping();
        // The accumulator shares the SAME FocusRegistry the hubs mutate — its baseline capture
        // (RecordChange) and current-state read (FlushDue) see the live roster the hubs produce. It also
        // shares the SAME session/connection registries the hubs register into, so a joined entry's
        // display name/flair reflect the live ChatUser each test seeds.
        _viewerResolver = new ViewerResolver(_sessionRegistry, _connectionMapping);
        _accumulator = new ViewersAccumulator(_harness.HubContext, _focusRegistry, _viewerResolver);

        _userDirectory = new UserDirectoryRepository(MongoClient);
        _muteRepository = new MuteRepository(MongoClient);
        _reconcileService = new MuteReconciliationTestHarness(_connectionMapping, _muteRepository).Service;
        _ticketStore = new TicketStore();

        _authService = new Mock<IChatAuthenticationService>();
        _authService.Setup(m => m.GetUserFromIdentity(It.IsAny<W3CUserAuthentication>()))
            .ReturnsAsync((W3CUserAuthentication id) =>
                new ChatUserResolution(new ChatUser(id.BattleTag, id.IsAdmin, id.Name, new ProfilePicture(), null, null), true));

        _channelRepository = new ChannelRepository(MongoClient);
        _membershipRepository = new MembershipRepository(MongoClient, _channelRepository);
        _messageRateLimiter = new MessageRateLimiter();
        _readRateLimiter = new ReadRateLimiter();
        _channelCreationRateLimiter = new ChannelCreationRateLimiter();
        _assembler = new SessionStateAssembler(
            _membershipRepository,
            _channelRepository,
            new MessageRepository(MongoClient),
            _muteRepository,
            _onlineMemberRegistry,
            _connectionMapping,
            new MentionInboxRepository(MongoClient));
    }

    private ChatHub BuildHub(string connectionId)
    {
        var hub = new ChatHub(
            _connectionMapping,
            _reconcileService,
            _ticketStore,
            _sessionRegistry,
            _userDirectory,
            _assembler,
            _focusRegistry,
            _onlineMemberRegistry,
            _messageRateLimiter,
            _readRateLimiter,
            _time,
            _channelRepository,
            _membershipRepository,
            _channelCreationRateLimiter,
            new MessageRepository(MongoClient),
            FanOutEngineTestFactory.CreateIgnored(),
            _accumulator,
            new NoOpMentionInboxCleaner(),
            RelationshipProviderTestFactory.CreateIgnored(),
            new UserSettingsRepository(MongoClient),
            new DmInitiationTracker(),
            _authService.Object,
            MentionFanOutTestFactory.CreateIgnored(MongoClient),
            new PresenceInterestRegistry(),
            new MentionInboxRepository(MongoClient),
            new NotificationPreferenceRepository(MongoClient),
            _viewerResolver);

        hub.Clients = new Mock<IHubCallerClients>().Object;
        var context = new Mock<HubCallerContext>();
        context.Setup(c => c.ConnectionId).Returns(connectionId);
        hub.Context = context.Object;
        hub.Groups = new Mock<IGroupManager>().Object;
        return hub;
    }

    private void RegisterSession(string connectionId, string battleTag) =>
        _sessionRegistry.Register(
            connectionId,
            new W3CUserAuthentication { BattleTag = battleTag, Name = battleTag.Split('#')[0] },
            null);

    private async Task<ChatChannel> CreateChannel(ChannelType type, string name = null)
    {
        var channel = new ChatChannel
        {
            Type = type,
            Name = name,
            NormalizedName = name != null ? ChannelNames.Normalize(name) : null,
        };
        await _channelRepository.Insert(channel);
        return channel;
    }

    // Seeds OnlineMemberRegistry the way SessionStateAssembler/JoinChannel/PushChannelAdded do — the
    // zero-DB "IS a member" signal FocusChannel reads, now carrying the ChannelType (D11) that lets
    // FocusChannel/UnfocusChannel/disconnect decide roster/accumulator participation.
    private void SeedMembership(string connectionId, string channelId, string battleTag, ChannelType type) =>
        _onlineMemberRegistry.Join(channelId, connectionId, new MemberState(battleTag, NotificationLevel.All, 0, type));

    [Test]
    public async Task FocusDm_ReturnsOkWithEmptyViewers_AndNoViewersAccumulatorRecord()
    {
        var channel = await CreateChannel(ChannelType.Dm);
        RegisterSession("conn-1", BattleTag);
        SeedMembership("conn-1", channel.Id, BattleTag, ChannelType.Dm);
        var hub = BuildHub("conn-1");

        var result = await hub.FocusChannel(channel.Id);

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(result.Viewers, Is.Empty, "a Dm FocusChannel must never return a viewer roster (D11)");
        Assert.That(_accumulator.PendingChangeCount(channel.Id), Is.EqualTo(0),
            "focusing a Dm must never enter the ViewersAccumulator");
    }

    [Test]
    public async Task FocusGroupDm_SameExclusion()
    {
        var channel = await CreateChannel(ChannelType.GroupDm);
        RegisterSession("conn-1", BattleTag);
        SeedMembership("conn-1", channel.Id, BattleTag, ChannelType.GroupDm);
        var hub = BuildHub("conn-1");

        var result = await hub.FocusChannel(channel.Id);

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(result.Viewers, Is.Empty, "a GroupDm FocusChannel must never return a viewer roster (D11)");
        Assert.That(_accumulator.PendingChangeCount(channel.Id), Is.EqualTo(0),
            "focusing a GroupDm must never enter the ViewersAccumulator");
    }

    [Test]
    public async Task FocusPublic_RosterBehaviorUnchanged()
    {
        // Regression pin — the D11 exclusion must never bleed into Public's existing roster contract.
        var channel = await CreateChannel(ChannelType.Public, "general");
        RegisterSession("conn-1", BattleTag);
        RegisterSession("conn-2", OtherBattleTag);
        SeedMembership("conn-1", channel.Id, BattleTag, ChannelType.Public);
        SeedMembership("conn-2", channel.Id, OtherBattleTag, ChannelType.Public);

        await BuildHub("conn-2").FocusChannel(channel.Id);
        var result = await BuildHub("conn-1").FocusChannel(channel.Id);

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(result.Viewers.Select(v => v.BattleTag), Is.EquivalentTo(new[] { BattleTag, OtherBattleTag }),
            "a Public channel's full active-viewer roster is unaffected by the D11 exclusion");
        Assert.That(_accumulator.PendingChangeCount(channel.Id), Is.EqualTo(2),
            "regression pin — Public focuses still record into the ViewersAccumulator");
    }

    [Test]
    public async Task RecipientFocusesPendingDm_SenderFocused_ReceivesNoViewersChanged_EverAfterFlush()
    {
        // The presence-leak pin: a declined/ignored recipient peeking at a still-pending Dm while the
        // sender is focused must NEVER surface a ViewersChanged to the sender — flush after flush.
        var channel = await _channelRepository.FindOrCreateDm(BattleTag, OtherBattleTag, BattleTag, DmRequestState.Pending, T0);
        RegisterSession("conn-sender", BattleTag);
        RegisterSession("conn-recipient", OtherBattleTag);
        SeedMembership("conn-sender", channel.Id, BattleTag, ChannelType.Dm);
        SeedMembership("conn-recipient", channel.Id, OtherBattleTag, ChannelType.Dm);

        await BuildHub("conn-sender").FocusChannel(channel.Id);
        await BuildHub("conn-recipient").FocusChannel(channel.Id);

        await _accumulator.FlushDue(T0 + Flush);
        await _accumulator.FlushDue(T0 + Flush + Flush);

        Assert.That(_harness.SignalCount("conn-sender", ChatEvents.ViewersChanged), Is.EqualTo(0),
            "a focused sender must NEVER receive ViewersChanged for a Dm — the direct decline/presence leak D11 forecloses");
    }

    [Test]
    public async Task DisconnectWhileFocusedOnDm_NoAccumulatorRecord_PublicChannelsStillRecorded()
    {
        var dm = await CreateChannel(ChannelType.Dm);
        var pub = await CreateChannel(ChannelType.Public, "general-2");
        RegisterSession("conn-1", BattleTag);
        SeedMembership("conn-1", dm.Id, BattleTag, ChannelType.Dm);
        SeedMembership("conn-1", pub.Id, BattleTag, ChannelType.Public);
        var hub = BuildHub("conn-1");
        await hub.FocusChannel(dm.Id);
        await hub.FocusChannel(pub.Id);

        // Drain both windows to a clean slate so only the disconnect's OWN RecordChange call is observed.
        await _accumulator.FlushDue(T0 + Flush);

        await hub.OnDisconnectedAsync(null);

        Assert.That(_accumulator.PendingChangeCount(dm.Id), Is.EqualTo(0),
            "a disconnect while focused on a Dm must never record into the ViewersAccumulator");
        Assert.That(_accumulator.PendingChangeCount(pub.Id), Is.EqualTo(1),
            "regression pin — a disconnect while focused on a Public channel still records");
    }

    [Test]
    public async Task UnfocusDm_NoRecord_StillOk()
    {
        var channel = await CreateChannel(ChannelType.Dm);
        RegisterSession("conn-1", BattleTag);
        SeedMembership("conn-1", channel.Id, BattleTag, ChannelType.Dm);
        var hub = BuildHub("conn-1");
        await hub.FocusChannel(channel.Id);

        var result = await hub.UnfocusChannel(channel.Id);

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(_accumulator.PendingChangeCount(channel.Id), Is.EqualTo(0),
            "unfocusing a Dm must never record into the ViewersAccumulator");
    }
}
