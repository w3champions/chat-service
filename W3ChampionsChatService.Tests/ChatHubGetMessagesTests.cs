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
using W3ChampionsChatService.Memberships;
using W3ChampionsChatService.Messages;
using W3ChampionsChatService.Mutes;
using W3ChampionsChatService.Protocol;
using W3ChampionsChatService.Sessions;
using W3ChampionsChatService.Settings;
using W3ChampionsChatService.Users;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// C3 Task 16: <c>GetMessages</c> — the pull-only history read that mirrors <c>FocusChannel</c>'s
/// NotFound-vs-NotMember resolution order over the Task 3 repo's paging methods. Direct-hub-
/// instantiation idiom (mirrors <see cref="ChatHubSendMessageTests"/>); a <see cref="FakeTimeProvider"/>
/// drives the clock so seeding via the real <c>SendMessage</c> pipeline (acceptance 6) never trips the
/// message rate limiter.
/// <para>
/// GetMessages is PULL-ONLY: every assertion here reads the method's own typed
/// <see cref="GetMessagesResult"/> — none of these tests assert on any SignalR push, because
/// GetMessages must never make one (the hard guardrail).
/// </para>
/// </summary>
public class ChatHubGetMessagesTests : IntegrationTestBase
{
    private const string BattleTag = "peter#123";
    private const string OtherBattleTag = "wolf#456";

    private static readonly DateTimeOffset FixedNow = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private ConnectionMapping _connectionMapping;
    private ChatHistory _chatHistory;
    private UserDirectoryRepository _userDirectory;
    private SettingsRepository _settingsRepository;
    private MuteRepository _muteRepository;
    private MuteReconciliationTestHarness _reconcileHarness;
    private TicketStore _ticketStore;
    private Mock<IChatAuthenticationService> _authService;

    private ChannelRepository _channelRepository;
    private MembershipRepository _membershipRepository;
    private MessageRepository _messageRepository;
    private SessionRegistry _sessionRegistry;
    private FocusRegistry _focusRegistry;
    private OnlineMemberRegistry _onlineMemberRegistry;
    private MessageRateLimiter _messageRateLimiter;
    private ChannelCreationRateLimiter _channelCreationRateLimiter;
    private SessionStateAssembler _assembler;
    private FanOutEngine _fanOutEngine;
    private FakeTimeProvider _time;

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    [SetUp]
    public void SetupBeforeEach()
    {
        _time = new FakeTimeProvider(FixedNow);

        _connectionMapping = new ConnectionMapping();
        _chatHistory = new ChatHistory();
        _userDirectory = new UserDirectoryRepository(MongoClient);
        _settingsRepository = new SettingsRepository(MongoClient);
        _muteRepository = new MuteRepository(MongoClient);
        _reconcileHarness = new MuteReconciliationTestHarness(_connectionMapping, _muteRepository);
        _ticketStore = new TicketStore();

        _authService = new Mock<IChatAuthenticationService>();
        _authService.Setup(m => m.GetUserFromIdentity(It.IsAny<W3CUserAuthentication>()))
            .ReturnsAsync((W3CUserAuthentication id) =>
                new ChatUser(id.BattleTag, id.IsAdmin, id.Name, new ProfilePicture(), null, null));

        _channelRepository = new ChannelRepository(MongoClient);
        _membershipRepository = new MembershipRepository(MongoClient, _channelRepository);
        _messageRepository = new MessageRepository(MongoClient);
        _sessionRegistry = new SessionRegistry();
        _focusRegistry = new FocusRegistry();
        _onlineMemberRegistry = new OnlineMemberRegistry();
        _messageRateLimiter = new MessageRateLimiter();
        _channelCreationRateLimiter = new ChannelCreationRateLimiter();
        _fanOutEngine = FanOutEngineTestFactory.CreateIgnored();
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
            _authService.Object,
            _muteRepository,
            _settingsRepository,
            _connectionMapping,
            _chatHistory,
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
            ViewersAccumulatorTestFactory.CreateIgnored());

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

    private async Task<ChatChannel> CreateChannel(string name = "general", ChannelType type = ChannelType.Public)
    {
        var channel = new ChatChannel { Type = type, Name = name, NormalizedName = ChannelNames.Normalize(name) };
        await _channelRepository.Insert(channel);
        return channel;
    }

    // Seeds a connection the same way the connect path does: a live session, the flair-bearing
    // ChatUser, the mute cache, and an OnlineMemberRegistry membership for the channel — mirrors
    // ChatHubSendMessageTests.SeedMember exactly, since these tests also drive the real SendMessage
    // pipeline (acceptance 6).
    private void SeedMember(string connectionId, string battleTag, string channelId)
    {
        RegisterSession(connectionId, battleTag);
        _connectionMapping.RegisterUser(connectionId, new ChatUser(battleTag, false, battleTag.Split('#')[0], new ProfilePicture(), null, null));
        _connectionMapping.SetMute(connectionId, MuteStatus.None, DateTime.MinValue);
        _onlineMemberRegistry.Join(channelId, connectionId, new MemberState(battleTag, NotificationLevel.Mentions, 0));
    }

    // Seeds a durable message via the SAME seq-allocation path the real send pipeline uses
    // (ChannelRepository.AllocateSeq), so the channel's LastSeq stays in sync with directly-seeded
    // history — a later real hub.SendMessage call (acceptance 6's mid-paging send) allocates the
    // correctly next seq rather than colliding with a manually-assigned one.
    private async Task<ChannelMessage> SeedMessage(string channelId, string battleTag, string content, bool shadow = false)
    {
        var seq = await _channelRepository.AllocateSeq(channelId, Now);
        var message = new ChannelMessage
        {
            ChannelId = channelId,
            Seq = seq,
            Sender = new MessageSender { BattleTag = battleTag, Name = battleTag.Split('#')[0] },
            Content = content,
            SentAt = Now,
            Shadow = shadow,
        };
        await _messageRepository.Insert(message);
        return message;
    }

    // ---------------------------------------------------------------------------------------------
    // Paging (acceptance 6)
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task GetMessages_BeforeSeq_PagesBackwards_NoGapsDupes_WithSendWhilePaging()
    {
        var channel = await CreateChannel();
        SeedMember("conn-1", BattleTag, channel.Id);
        var hub = BuildHub("conn-1");

        // Seed 6 messages (seq 1..6) via the same seq-allocation path SendMessage uses.
        for (var i = 1; i <= 6; i++)
        {
            await SeedMessage(channel.Id, BattleTag, $"seed-{i}");
        }

        // First page: latest 3 → seq 4,5,6.
        var firstPage = await hub.GetMessages(channel.Id, beforeSeq: null, aroundSeq: null, limit: 3);
        Assert.AreEqual(ChatResultCode.Ok, firstPage.Code);
        CollectionAssert.AreEqual(new long[] { 4, 5, 6 }, firstPage.Messages.Select(m => m.Seq).ToArray());

        // A new message arrives THROUGH THE REAL SendMessage PIPELINE while the client is still
        // paging backwards through older history (acceptance 6).
        var sendResult = await hub.SendMessage(channel.Id, "sent-while-paging");
        Assert.AreEqual(ChatResultCode.Ok, sendResult.Code);
        Assert.AreEqual(7L, sendResult.Seq, "The concurrent send must allocate the next seq (7)");

        // Continue paging backwards from the first page's oldest seq.
        var minSeqOfFirstPage = firstPage.Messages.Min(m => m.Seq);
        var secondPage = await hub.GetMessages(channel.Id, beforeSeq: minSeqOfFirstPage, aroundSeq: null, limit: 3);
        Assert.AreEqual(ChatResultCode.Ok, secondPage.Code);
        CollectionAssert.AreEqual(new long[] { 1, 2, 3 }, secondPage.Messages.Select(m => m.Seq).ToArray());

        // The two backward pages cover exactly the pre-existing history — no gaps, no dupes — and
        // are immune to the concurrent send (seq 7 belongs to neither backward page).
        var union = firstPage.Messages.Select(m => m.Seq)
            .Concat(secondPage.Messages.Select(m => m.Seq))
            .OrderBy(s => s)
            .ToArray();
        CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4, 5, 6 }, union);
        Assert.AreEqual(6, union.Distinct().Count(), "pages must not overlap despite the concurrent send");
    }

    [Test]
    public async Task GetMessages_AroundSeq_ReturnsTargetWindow()
    {
        var channel = await CreateChannel();
        SeedMember("conn-1", BattleTag, channel.Id);
        var hub = BuildHub("conn-1");

        for (var i = 1; i <= 21; i++)
        {
            await SeedMessage(channel.Id, BattleTag, $"seed-{i}");
        }

        var result = await hub.GetMessages(channel.Id, beforeSeq: null, aroundSeq: 11, limit: 10);

        Assert.AreEqual(ChatResultCode.Ok, result.Code);
        CollectionAssert.AreEqual(Enumerable.Range(6, 11).Select(i => (long)i).ToArray(), result.Messages.Select(m => m.Seq).ToArray());
    }

    // ---------------------------------------------------------------------------------------------
    // Membership / channel resolution (mirrors FocusChannel's split)
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task GetMessages_NonMember_ReturnsNotMember()
    {
        var channel = await CreateChannel();
        RegisterSession("conn-1", BattleTag);
        // Deliberately no membership seed — the caller has a live session but is not a member.
        var hub = BuildHub("conn-1");

        var result = await hub.GetMessages(channel.Id, beforeSeq: null, aroundSeq: null, limit: 10);

        Assert.AreEqual(ChatResultCode.NotMember, result.Code);
        Assert.IsNull(result.Messages);
    }

    [Test]
    public async Task GetMessages_UnknownChannel_ReturnsNotFound()
    {
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");

        var result = await hub.GetMessages("no-such-channel-id", beforeSeq: null, aroundSeq: null, limit: 10);

        Assert.AreEqual(ChatResultCode.NotFound, result.Code);
        Assert.IsNull(result.Messages);
    }

    [Test]
    public async Task GetMessages_UnregisteredConnection_ReturnsPermissionDenied_FailClosed()
    {
        var channel = await CreateChannel();
        // No RegisterSession call — never authenticated, or displaced/torn down.
        var hub = BuildHub("conn-ghost");

        var result = await hub.GetMessages(channel.Id, beforeSeq: null, aroundSeq: null, limit: 10);

        Assert.AreEqual(ChatResultCode.PermissionDenied, result.Code);
        Assert.IsNull(result.Messages);
    }

    // ---------------------------------------------------------------------------------------------
    // Limit clamp (delegated to the repo — assert the hub passes it straight through)
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task GetMessages_LimitClampedToMessagePageSize()
    {
        var channel = await CreateChannel();
        SeedMember("conn-1", BattleTag, channel.Id);
        var hub = BuildHub("conn-1");

        var inserts = Enumerable.Range(1, ChatLimits.MessagePageSize + 5)
            .Select(_ => SeedMessage(channel.Id, BattleTag, "bulk"));
        await Task.WhenAll(inserts);

        var result = await hub.GetMessages(channel.Id, beforeSeq: null, aroundSeq: null, limit: ChatLimits.MessagePageSize * 10);

        Assert.AreEqual(ChatResultCode.Ok, result.Code);
        Assert.AreEqual(ChatLimits.MessagePageSize, result.Messages.Count,
            "A limit far above the page-size cap must be clamped down by the repo, never rejected or returned in full");
    }

    // ---------------------------------------------------------------------------------------------
    // Malformed-arg guard (decision 5 mapping: client bug → HubException, not a typed result)
    // ---------------------------------------------------------------------------------------------

    [Test]
    public void GetMessages_BothSeqParams_ThrowsHubException()
    {
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");

        Assert.ThrowsAsync<HubException>(async () => await hub.GetMessages("some-channel-id", beforeSeq: 5, aroundSeq: 5, limit: 10),
            "Supplying BOTH beforeSeq and aroundSeq is a client programming error — HubException, not a typed result");
    }

    // ---------------------------------------------------------------------------------------------
    // Read filters (repo-enforced; hub must not re-filter, and must force the shadow illusion)
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task GetMessages_RespectsUserReadFilters()
    {
        var channel = await CreateChannel();
        SeedMember("conn-1", BattleTag, channel.Id);
        var hub = BuildHub("conn-1");

        var normal = await SeedMessage(channel.Id, BattleTag, "visible to everyone");
        var deleted = await SeedMessage(channel.Id, BattleTag, "will be deleted");
        var foreignShadow = await SeedMessage(channel.Id, OtherBattleTag, "shadow from someone else", shadow: true);
        var ownShadow = await SeedMessage(channel.Id, BattleTag, "my own shadow post", shadow: true);
        await _messageRepository.MarkDeleted(deleted.Id, "Mod#1", Now);

        var result = await hub.GetMessages(channel.Id, beforeSeq: null, aroundSeq: null, limit: 10);

        Assert.AreEqual(ChatResultCode.Ok, result.Code);
        CollectionAssert.AreEqual(new[] { normal.Seq, ownShadow.Seq }, result.Messages.Select(m => m.Seq).ToArray(),
            "The viewer must see their own shadow message and the normal message, but NOT the foreign " +
            "shadow message and NOT the soft-deleted message");

        var ownShadowDto = result.Messages.Single(m => m.Seq == ownShadow.Seq);
        Assert.IsFalse(ownShadowDto.Shadow, "The viewer's OWN shadow message must render as non-shadow (the illusion)");
        Assert.IsFalse(ownShadowDto.Deleted, "Deleted must always be forced false on the user-facing projection");
    }
}
