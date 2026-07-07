using System;
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
using W3ChampionsChatService.Memberships;
using W3ChampionsChatService.Mentions;
using W3ChampionsChatService.Messages;
using W3ChampionsChatService.Mutes;
using W3ChampionsChatService.Protocol;
using W3ChampionsChatService.Sessions;
using W3ChampionsChatService.Users;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// C5 Task 3: <c>SetDmPrivacy(DmPrivacy)</c> — the thin per-user settings write behind the dmPrivacy gate.
/// Direct-hub-instantiation idiom (mirrors <see cref="ChatHubSendMessageTests"/>); a
/// <see cref="FakeTimeProvider"/> drives the hub clock. NUnit constraint style.
/// </summary>
public class ChatHubSettingsTests : IntegrationTestBase
{
    private const string BattleTag = "peter#123";
    private static readonly DateTimeOffset FixedNow = new(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);

    private ConnectionMapping _connectionMapping;
    private MuteReconciliationTestHarness _reconcileHarness;
    private TicketStore _ticketStore;
    private SessionRegistry _sessionRegistry;
    private UserDirectoryRepository _userDirectory;
    private ChannelRepository _channelRepository;
    private MembershipRepository _membershipRepository;
    private MessageRepository _messageRepository;
    private FocusRegistry _focusRegistry;
    private OnlineMemberRegistry _onlineMemberRegistry;
    private MessageRateLimiter _messageRateLimiter;
    private ChannelCreationRateLimiter _channelCreationRateLimiter;
    private SessionStateAssembler _assembler;
    private FanOutEngine _fanOutEngine;
    private UserSettingsRepository _userSettings;
    private DmInitiationTracker _dmInitiationTracker;
    private FakeTimeProvider _time;
    private Mock<IChatAuthenticationService> _authService;

    [SetUp]
    public void SetupBeforeEach()
    {
        _time = new FakeTimeProvider(FixedNow);
        _connectionMapping = new ConnectionMapping();
        _userDirectory = new UserDirectoryRepository(MongoClient);
        _reconcileHarness = new MuteReconciliationTestHarness(_connectionMapping, new MuteRepository(MongoClient));
        _ticketStore = new TicketStore();
        _channelRepository = new ChannelRepository(MongoClient);
        _membershipRepository = new MembershipRepository(MongoClient, _channelRepository);
        _messageRepository = new MessageRepository(MongoClient);
        _sessionRegistry = new SessionRegistry();
        _focusRegistry = new FocusRegistry();
        _onlineMemberRegistry = new OnlineMemberRegistry();
        _messageRateLimiter = new MessageRateLimiter();
        _channelCreationRateLimiter = new ChannelCreationRateLimiter();
        _fanOutEngine = FanOutEngineTestFactory.CreateIgnored();
        _userSettings = new UserSettingsRepository(MongoClient);
        _dmInitiationTracker = new DmInitiationTracker();

        _authService = new Mock<IChatAuthenticationService>();
        _authService.Setup(m => m.GetUserFromIdentity(It.IsAny<W3CUserAuthentication>()))
            .ReturnsAsync((W3CUserAuthentication id) =>
                new ChatUserResolution(new ChatUser(id.BattleTag, id.IsAdmin, id.Name, new ProfilePicture(), null, null), true));
        _assembler = new SessionStateAssembler(
            _membershipRepository,
            _channelRepository,
            _messageRepository,
            new MuteRepository(MongoClient),
            _onlineMemberRegistry,
            _connectionMapping,
            new MentionInboxRepository(MongoClient));
    }

    private ChatHub BuildHub(string connectionId)
    {
        var hub = new ChatHub(
            _connectionMapping,
            _reconcileHarness.Service,
            _ticketStore,
            _sessionRegistry,
            _userDirectory,
            _assembler,
            _focusRegistry,
            _onlineMemberRegistry,
            _messageRateLimiter,
            _time,
            _channelRepository,
            _membershipRepository,
            _channelCreationRateLimiter,
            _messageRepository,
            _fanOutEngine,
            ViewersAccumulatorTestFactory.CreateIgnored(),
            new NoOpMentionInboxCleaner(),
            RelationshipProviderTestFactory.CreateIgnored(),
            _userSettings,
            _dmInitiationTracker,
            _authService.Object,
            MentionFanOutTestFactory.CreateIgnored(MongoClient),
            new PresenceInterestRegistry(),
            new MentionInboxRepository(MongoClient));

        var clients = new Mock<IHubCallerClients>();
        clients.Setup(c => c.Caller).Returns(new Mock<ISingleClientProxy>().Object);
        hub.Clients = clients.Object;

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

    [Test]
    public async Task SetDmPrivacy_PersistsAndDefaultsEveryone()
    {
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");

        // Default before any write: a user with no settings doc reads Everyone.
        var initial = await _userSettings.LoadOrDefault(BattleTag);
        Assert.That(initial.DmPrivacy, Is.EqualTo(DmPrivacy.Everyone), "the default dmPrivacy is Everyone");

        var result = await hub.SetDmPrivacy(DmPrivacy.Nobody);

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        var reloaded = await _userSettings.LoadOrDefault(BattleTag);
        Assert.That(reloaded.DmPrivacy, Is.EqualTo(DmPrivacy.Nobody), "SetDmPrivacy persists the new value");
    }

    [Test]
    public async Task SetDmPrivacy_LeavesOtherSettingsFieldsUntouched()
    {
        // Pre-seed a settings doc with non-default sibling fields; SetDmPrivacy must not clobber them.
        await _userSettings.Upsert(new UserSettings
        {
            BattleTag = BattleTag,
            DmPrivacy = DmPrivacy.Everyone,
            DefaultNotificationLevel = NotificationLevel.Mentions,
            SoundsEnabled = false,
        });
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");

        var result = await hub.SetDmPrivacy(DmPrivacy.Friends);

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        var reloaded = await _userSettings.LoadOrDefault(BattleTag);
        Assert.That(reloaded.DmPrivacy, Is.EqualTo(DmPrivacy.Friends));
        Assert.That(reloaded.DefaultNotificationLevel, Is.EqualTo(NotificationLevel.Mentions),
            "the notification-level setting must be preserved");
        Assert.That(reloaded.SoundsEnabled, Is.False, "the sounds setting must be preserved");
    }

    [Test]
    public async Task SetDmPrivacy_NoSession_FailClosed_PermissionDenied()
    {
        var hub = BuildHub("conn-ghost"); // no session registered

        var result = await hub.SetDmPrivacy(DmPrivacy.Nobody);

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.PermissionDenied));
    }
}
