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
/// C3 (Task 17): <c>MarkRead</c> — advances the caller's per-channel read cursor in BOTH the durable
/// Mongo membership row and the in-memory <see cref="OnlineMemberRegistry"/>. Direct-hub-instantiation
/// idiom (mirrors <see cref="ChatHubGetMessagesTests"/> / the HUB-LEVEL section of
/// <see cref="ViewersAccumulatorTests"/>); a <see cref="FakeTimeProvider"/> drives the clock.
/// <para>
/// NO SERVER-SIDE THROTTLE TEST: the server deliberately does NOT hard-enforce
/// <see cref="ChatLimits.MarkReadThrottle"/> — the 5s value is the pinned CLIENT coalescing contract
/// (spec §7, "Client coalesces…"). A hard server reject would break the client's final-flush-on-unfocus
/// (a legitimate MarkRead that must go through even <5s after the previous one), and blanket
/// method-abuse is already the SignalR-level rate limiter's job. There is therefore no
/// throttle-rejection test in this file by design — see the doc comment on <c>ChatHub.MarkRead</c>.
/// </para>
/// </summary>
public class ChatHubMarkReadTests : IntegrationTestBase
{
    private const string BattleTag = "peter#123";

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

    private async Task<ChatChannel> CreateChannel(string name = "general", ChannelType type = ChannelType.Public, long lastSeq = 0)
    {
        var channel = new ChatChannel { Type = type, Name = name, NormalizedName = ChannelNames.Normalize(name), LastSeq = lastSeq };
        await _channelRepository.Insert(channel);
        return channel;
    }

    // Seeds a connection AND its durable membership row the same way the connect path does: a live
    // session, the flair-bearing ChatUser, the mute cache, an OnlineMemberRegistry membership entry
    // for the channel, and — unlike ChatHubGetMessagesTests.SeedMember (which never needs the durable
    // row) — the actual Mongo ChannelMembership doc, since MarkRead's assertions read BOTH stores.
    private async Task SeedMember(string connectionId, string battleTag, string channelId, long lastReadSeq = 0)
    {
        RegisterSession(connectionId, battleTag);
        _connectionMapping.RegisterUser(connectionId, new ChatUser(battleTag, false, battleTag.Split('#')[0], new ProfilePicture(), null, null));
        _connectionMapping.SetMute(connectionId, MuteStatus.None, DateTime.MinValue);
        _onlineMemberRegistry.Join(channelId, connectionId, new MemberState(battleTag, NotificationLevel.All, lastReadSeq));
        await _membershipRepository.Insert(new ChannelMembership
        {
            ChannelId = channelId,
            BattleTag = battleTag,
            LastReadSeq = lastReadSeq,
            JoinedAt = Now,
        });
    }

    // Seeds a durable message via the SAME seq-allocation path the real send pipeline uses
    // (ChannelRepository.AllocateSeq), so the channel's LastSeq advances exactly as it would on a
    // real send.
    private async Task<ChannelMessage> SeedMessage(string channelId, string battleTag, string content)
    {
        var seq = await _channelRepository.AllocateSeq(channelId, Now);
        var message = new ChannelMessage
        {
            ChannelId = channelId,
            Seq = seq,
            Sender = new MessageSender { BattleTag = battleTag, Name = battleTag.Split('#')[0] },
            Content = content,
            SentAt = Now,
        };
        await _messageRepository.Insert(message);
        return message;
    }

    // ---------------------------------------------------------------------------------------------
    // Acceptance 7: persists + updates the registry
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task MarkRead_PersistsLastReadSeq_AndUpdatesRegistry()
    {
        var channel = await CreateChannel();
        await SeedMember("conn-1", BattleTag, channel.Id);
        var hub = BuildHub("conn-1");

        for (var i = 1; i <= 10; i++)
        {
            await SeedMessage(channel.Id, BattleTag, $"seed-{i}");
        }

        var result = await hub.MarkRead(channel.Id, 5);

        Assert.AreEqual(ChatResultCode.Ok, result.Code);

        var persisted = await _membershipRepository.Load(channel.Id, BattleTag);
        Assert.AreEqual(5L, persisted.LastReadSeq, "the durable Mongo membership row must carry the new seq");

        Assert.IsTrue(_onlineMemberRegistry.TryGetMember(channel.Id, "conn-1", out var member));
        Assert.AreEqual(5L, member.LastReadSeq, "the in-memory registry must carry the new seq too");
    }

    // ---------------------------------------------------------------------------------------------
    // $max monotonic — neither store may regress
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task MarkRead_LowerSeq_DoesNotRegress()
    {
        var channel = await CreateChannel();
        await SeedMember("conn-1", BattleTag, channel.Id, lastReadSeq: 10);
        var hub = BuildHub("conn-1");

        for (var i = 1; i <= 15; i++)
        {
            await SeedMessage(channel.Id, BattleTag, $"seed-{i}");
        }

        var result = await hub.MarkRead(channel.Id, 3);

        Assert.AreEqual(ChatResultCode.Ok, result.Code);

        // (a) the DB LastReadSeq must not regress — UpdateLastReadSeq's own $max already guarantees
        // this, but assert it here as part of the whole-method contract.
        var persisted = await _membershipRepository.Load(channel.Id, BattleTag);
        Assert.AreEqual(10L, persisted.LastReadSeq, "a lower/out-of-order MarkRead must not regress the durable DB cursor");

        // (b) the IN-MEMORY registry must not regress either — the load-bearing assertion this test
        // exists for. A naive SetLastReadSeq (plain overwrite) would fail this half while (a) still
        // passes, since the DB's own $max silently absorbs the regression independently of the hub.
        Assert.IsTrue(_onlineMemberRegistry.TryGetMember(channel.Id, "conn-1", out var member));
        Assert.AreEqual(10L, member.LastReadSeq, "a lower/out-of-order MarkRead must not regress the in-memory registry cursor either");
    }

    // ---------------------------------------------------------------------------------------------
    // Clamp to channel.LastSeq
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task MarkRead_SeqAboveChannelLastSeq_ClampsToLastSeq()
    {
        var channel = await CreateChannel();
        await SeedMember("conn-1", BattleTag, channel.Id);
        var hub = BuildHub("conn-1");

        for (var i = 1; i <= 4; i++)
        {
            await SeedMessage(channel.Id, BattleTag, $"seed-{i}");
        }
        // channel.LastSeq is now 4 in Mongo (the local `channel` var is stale — MarkRead reloads it).

        var result = await hub.MarkRead(channel.Id, seq: 1000);

        Assert.AreEqual(ChatResultCode.Ok, result.Code);

        var persisted = await _membershipRepository.Load(channel.Id, BattleTag);
        Assert.AreEqual(4L, persisted.LastReadSeq, "an inflated seq must clamp to the channel's actual LastSeq in the DB");

        Assert.IsTrue(_onlineMemberRegistry.TryGetMember(channel.Id, "conn-1", out var member));
        Assert.AreEqual(4L, member.LastReadSeq, "an inflated seq must clamp to the channel's actual LastSeq in the registry too");
    }

    // ---------------------------------------------------------------------------------------------
    // Non-member
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task MarkRead_NonMember_ReturnsNotMember()
    {
        var channel = await CreateChannel();
        RegisterSession("conn-1", BattleTag);
        // Deliberately no membership seed — the caller has a live session but is not a member.
        var hub = BuildHub("conn-1");

        var result = await hub.MarkRead(channel.Id, 5);

        Assert.AreEqual(ChatResultCode.NotMember, result.Code);
    }

    // ---------------------------------------------------------------------------------------------
    // Member-of-a-deleted-channel edge (defensive — see the doc comment on ChatHub.MarkRead)
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task MarkRead_UnknownChannel_ReturnsNotFound()
    {
        // Member-of-a-deleted-channel edge: the registry says member, but no channel doc exists.
        const string ghostChannelId = "ghost-channel-id";
        await SeedMember("conn-1", BattleTag, ghostChannelId);
        var hub = BuildHub("conn-1");

        var result = await hub.MarkRead(ghostChannelId, 5);

        Assert.AreEqual(ChatResultCode.NotFound, result.Code);
    }

    // ---------------------------------------------------------------------------------------------
    // Acceptance 7's regression clause: a fresh reconnect-assembled SessionState reflects unread 0
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task MarkRead_Regression_SubsequentSessionState_ReflectsUnreadZero()
    {
        var channel = await CreateChannel();
        await SeedMember("conn-1", BattleTag, channel.Id);
        var hub = BuildHub("conn-1");

        for (var i = 1; i <= 5; i++)
        {
            await SeedMessage(channel.Id, BattleTag, $"seed-{i}");
        }
        var reloadedChannel = await _channelRepository.Load(channel.Id);

        var markReadResult = await hub.MarkRead(channel.Id, reloadedChannel.LastSeq);
        Assert.AreEqual(ChatResultCode.Ok, markReadResult.Code);

        // A fresh reconnect (new connectionId, same battleTag) rebuilds SessionState straight from the
        // durable membership row — proving MarkRead's Mongo write, not just the in-memory registry,
        // carries the caught-up cursor forward.
        var identity = new W3CUserAuthentication { BattleTag = BattleTag, Name = BattleTag.Split('#')[0] };
        var (dto, _) = await _assembler.AssembleAndSeed(identity, "conn-reconnect", Now);

        var channelDto = dto.Channels.Single(c => c.Channel.Id == channel.Id);
        Assert.AreEqual(0L, channelDto.UnreadCount);
        Assert.IsFalse(channelDto.HasUnread);
    }

    // ---------------------------------------------------------------------------------------------
    // Bridges acceptance 2: MarkRead re-enables a suppressed ActivityCoalescer emission
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task MarkRead_ReenablesSuppressedActivity()
    {
        // A channel whose LastSeq is already far ahead (created directly at 200, mirroring
        // ActivityCoalescerTests' pattern of driving the coalescer without paying for hundreds of
        // real sends) so the clamp never interferes with the seqs exercised below.
        var channel = await CreateChannel(lastSeq: 200);
        await SeedMember("conn-1", BattleTag, channel.Id, lastReadSeq: 0);
        var hub = BuildHub("conn-1");

        // A real ActivityCoalescer sharing the HUB'S OWN OnlineMemberRegistry — so a MarkRead the hub
        // performs is immediately visible to the coalescer's emit-time unread recompute.
        var harness = new HubPushCaptureHarness();
        var coalescer = new ActivityCoalescer(harness.HubContext, _onlineMemberRegistry);

        // Unread = 150 - 0 = 150 > 100 → suppressed at emit time, despite the window being due
        // (first-ever offer for this pair).
        await coalescer.Offer("conn-1", channel.Id, lastSeq: 150, Now);
        Assert.AreEqual(0, harness.SignalCount("conn-1", ChatEvents.ChannelActivity), "unread > 100 must suppress the emission");

        // MarkRead through the REAL hub method advances the registry's LastReadSeq to 100 (clamped
        // against the channel's LastSeq of 200, so it lands exactly at 100 — unread now = 150-100=50).
        var markReadResult = await hub.MarkRead(channel.Id, 100);
        Assert.AreEqual(ChatResultCode.Ok, markReadResult.Code);

        // Suppression is re-checked AT EMIT time (never at offer) — the next due offer (≥10s later,
        // mirroring the coalescer's own window floor) now emits because unread (50) ≤ 100.
        await coalescer.Offer("conn-1", channel.Id, lastSeq: 150, Now.AddSeconds(11));
        Assert.AreEqual(1, harness.SignalCount("conn-1", ChatEvents.ChannelActivity), "a MarkRead that drops unread to ≤100 must re-enable emission on the next due offer/flush");
        var dto = harness.PayloadFor("conn-1", ChatEvents.ChannelActivity) as ChannelActivityDto;
        Assert.AreEqual(150, dto.LastSeq);
    }
}
