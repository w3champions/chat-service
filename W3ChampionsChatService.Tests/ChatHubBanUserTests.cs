using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
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
using W3ChampionsChatService.Users;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// Hub-level coverage of the KEPT moderator method <see cref="ChatHub.BanUser"/> — cache reconciliation,
/// the endDate-only <c>PlayerBannedFromChat</c> signal, multi-connection reconciliation, the no-abort /
/// no-evict guarantees, and shadow silence. Split out of the old ChatBanRoomScope suite during the C3
/// old-protocol cutover (Task 19); these assertions are richer than (and hub-driven, unlike the
/// service-driven <c>MuteReconciliationTests</c>). The two end-to-end "ban then send" cases are
/// re-expressed against the NEW <see cref="ChatHub.SendMessage(string, string)"/> pipeline (the old ones
/// drove the deleted single-arg SendMessage).
/// </summary>
public class ChatHubBanUserTests : IntegrationTestBase
{
    private ChatHub _chatHub;
    private MuteRepository _muteRepository;
    private ConnectionMapping _connectionMapping;
    private MuteReconciliationTestHarness _reconcileHarness;
    private Mock<IHubCallerClients> _clients;
    private Mock<HubCallerContext> _hubCallerContext;

    private SessionRegistry _sessionRegistry;
    private OnlineMemberRegistry _onlineMemberRegistry;
    private ChannelRepository _channelRepository;
    private MembershipRepository _membershipRepository;
    private MessageRepository _messageRepository;

    [SetUp]
    public void SetupBeforeEach()
    {
        _muteRepository = new MuteRepository(MongoClient);
        _connectionMapping = new ConnectionMapping();
        // Wire ApplyBanAsync to the real repo so hub BanUser persists to (and is removable from) the DB.
        _reconcileHarness = new MuteReconciliationTestHarness(_connectionMapping, _muteRepository);

        var chatAuthService = new Mock<IChatAuthenticationService>();
        chatAuthService.Setup(m => m.GetUserFromIdentity(It.IsAny<W3CUserAuthentication>()))
            .ReturnsAsync(new ChatUser("victim#123", false, null, new ProfilePicture(), null, null));

        _channelRepository = new ChannelRepository(MongoClient);
        _membershipRepository = new MembershipRepository(MongoClient, _channelRepository);
        _messageRepository = new MessageRepository(MongoClient);
        _sessionRegistry = new SessionRegistry();
        _onlineMemberRegistry = new OnlineMemberRegistry();

        _chatHub = new ChatHub(
            _connectionMapping,
            new ChatHistory(),
            _reconcileHarness.Service,
            new TicketStore(),
            _sessionRegistry,
            new UserDirectoryRepository(MongoClient),
            new SessionStateAssembler(
                _membershipRepository,
                _channelRepository,
                _muteRepository,
                chatAuthService.Object,
                _onlineMemberRegistry,
                _connectionMapping),
            new FocusRegistry(),
            _onlineMemberRegistry,
            new MessageRateLimiter(),
            TimeProvider.System,
            _channelRepository,
            _membershipRepository,
            new ChannelCreationRateLimiter(),
            _messageRepository,
            FanOutEngineTestFactory.CreateIgnored(),
            ViewersAccumulatorTestFactory.CreateIgnored());

        _clients = new Mock<IHubCallerClients>();
        var callerProxy = new Mock<ISingleClientProxy>();
        callerProxy.Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var groupProxy = new Mock<IClientProxy>();
        groupProxy.Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _clients.Setup(c => c.Caller).Returns(callerProxy.Object);
        _clients.Setup(c => c.Group(It.IsAny<string>())).Returns(groupProxy.Object);
        _chatHub.Clients = _clients.Object;

        _hubCallerContext = new Mock<HubCallerContext>();
        _hubCallerContext.Setup(c => c.ConnectionId).Returns("TestId");
        _chatHub.Context = _hubCallerContext.Object;
        _chatHub.Groups = new Mock<IGroupManager>().Object;
    }

    // ── Cache reconciliation ────────────────────────────────────────────────────

    [Test]
    public async Task BanUser_LiveUser_FullBan_UpdatesCacheToFull()
    {
        // Arrange: a live user in W3C Lounge
        var liveUser = new ChatUser("victim#123", false, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("VictimConn", "W3C Lounge", liveUser);
        _connectionMapping.SetMute("VictimConn", MuteStatus.None, DateTime.MinValue);

        // Admin performs ban (the reconcile signal goes through the MuteReconciliationService harness).
        var adminUser = new ChatUser("admin#1", true, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("TestId", "W3C Lounge", adminUser);
        _hubCallerContext.Setup(c => c.ConnectionId).Returns("TestId");

        var endDate = DateTime.UtcNow.AddDays(1).ToString("O");
        await _chatHub.BanUser("victim#123", "bad behavior", false, endDate);

        // Mute cache must be updated on the live connection
        Assert.IsTrue(_connectionMapping.TryGetMute("VictimConn", out var cached),
            "Full ban must produce a cache HIT on the live connection");
        Assert.AreEqual(MuteStatus.Full, cached.Status,
            "Full ban must update the cached MuteStatus to Full for the live connection");
    }

    [Test]
    public async Task BanUser_LiveUser_ShadowBan_UpdatesCacheToShadow()
    {
        var liveUser = new ChatUser("victim#123", false, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("VictimConn", "W3C Lounge", liveUser);
        _connectionMapping.SetMute("VictimConn", MuteStatus.None, DateTime.MinValue);

        var adminUser = new ChatUser("admin#1", true, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("TestId", "W3C Lounge", adminUser);

        var endDate = DateTime.UtcNow.AddDays(1).ToString("O");
        await _chatHub.BanUser("victim#123", "spam", true, endDate);

        Assert.IsTrue(_connectionMapping.TryGetMute("VictimConn", out var cached),
            "Shadow ban must produce a cache HIT on the live connection");
        Assert.AreEqual(MuteStatus.Shadow, cached.Status,
            "Shadow ban must update the cached MuteStatus to Shadow for the live connection");
    }

    [Test]
    public async Task BanUser_LiveUser_FullBan_CacheEndDateMatchesBanEndDate()
    {
        // Verify the cached endDate is populated so expiry enforcement works from the cache
        var liveUser = new ChatUser("victim#123", false, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("VictimConn", "W3C Lounge", liveUser);
        _connectionMapping.SetMute("VictimConn", MuteStatus.None, DateTime.MinValue);

        var adminUser = new ChatUser("admin#1", true, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("TestId", "W3C Lounge", adminUser);

        var endDateStr = DateTime.UtcNow.AddDays(7).ToString("O");
        await _chatHub.BanUser("victim#123", "reason", false, endDateStr);

        _connectionMapping.TryGetMute("VictimConn", out var cached);
        Assert.AreEqual(MuteStatus.Full, cached.Status);
        // EndDate must be in the future (ban is active) and close to the requested endDate
        Assert.Greater(cached.EndDate, DateTime.UtcNow,
            "Cached EndDate must be in the future for an active full ban");
    }

    [Test]
    public async Task BanUser_LiveUser_FullBan_InBannedRoom_SendsPlayerBannedFromChat()
    {
        // Live user sitting in W3C Lounge (a public room)
        var liveUser = new ChatUser("victim#123", false, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("VictimConn", "W3C Lounge", liveUser);
        _connectionMapping.SetMute("VictimConn", MuteStatus.None, DateTime.MinValue);

        var adminUser = new ChatUser("admin#1", true, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("TestId", "W3C Lounge", adminUser);

        var endDate = DateTime.UtcNow.AddDays(1).ToString("O");
        await _chatHub.BanUser("victim#123", "bad behavior", false, endDate);

        // The reconcile signal flows through MuteReconciliationService (IHubContext), captured by the harness.
        var payloadInSignal = _reconcileHarness.PayloadFor("VictimConn", "PlayerBannedFromChat");
        Assert.AreEqual(1, _reconcileHarness.SignalCount("VictimConn", "PlayerBannedFromChat"),
            "Live fully-banned user in a public room must receive PlayerBannedFromChat");
        // SECURITY: the slimmed payload carries ONLY the expiry — never the LoungeMute.
        Assert.IsNotInstanceOf<LoungeMute>(payloadInSignal,
            "PlayerBannedFromChat must NOT send the full LoungeMute (leaks reason/isShadowBan)");
        AssertPlayerBannedPayloadIsEndDateOnly(payloadInSignal);
    }

    [Test]
    public async Task BanUser_LiveUser_FullBan_InBannedRoom_NoContextAbort()
    {
        // G1: PlayerBannedFromChat is sent with NO Context.Abort() — the connection must stay alive
        var liveUser = new ChatUser("victim#123", false, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("VictimConn", "W3C Lounge", liveUser);
        _connectionMapping.SetMute("VictimConn", MuteStatus.None, DateTime.MinValue);

        bool abortCalled = false;
        _hubCallerContext.Setup(c => c.Abort()).Callback(() => abortCalled = true);

        var adminUser = new ChatUser("admin#1", true, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("TestId", "W3C Lounge", adminUser);

        var endDate = DateTime.UtcNow.AddDays(1).ToString("O");
        await _chatHub.BanUser("victim#123", "bad behavior", false, endDate);

        Assert.IsFalse(abortCalled,
            "BanUser must NOT call Context.Abort() on the admin or victim (G1 — no abort anywhere)");
    }

    [Test]
    public async Task BanUser_LiveUser_FullBan_DoesNotEvictFromRoom()
    {
        // Spec §12: do NOT forcibly evict the user from their current room.
        // Enforcement happens on their next SendMessage/join which reads the updated cache.
        var liveUser = new ChatUser("victim#123", false, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("VictimConn", "W3C Lounge", liveUser);
        _connectionMapping.SetMute("VictimConn", MuteStatus.None, DateTime.MinValue);

        var adminUser = new ChatUser("admin#1", true, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("TestId", "W3C Lounge", adminUser);

        var endDate = DateTime.UtcNow.AddDays(1).ToString("O");
        await _chatHub.BanUser("victim#123", "bad behavior", false, endDate);

        // The victim must still be in W3C Lounge after the ban — not evicted
        var room = _connectionMapping.GetRoom("VictimConn");
        Assert.AreEqual("W3C Lounge", room,
            "BanUser must NOT evict the user from their current room (spec §12)");
    }

    [Test]
    public async Task BanUser_LiveUser_FullBan_InExemptRoom_AlsoSendsPlayerBannedFromChat()
    {
        // R7/G5: a user full-banned while sitting in an EXEMPT room (clan/lobby) must STILL
        // receive PlayerBannedFromChat — they must clearly and persistently know they're banned,
        // independent of channel. The signal is NOT gated on the room being a banned room.
        var liveUser = new ChatUser("victim#123", false, "AB", new ProfilePicture(), null, null);
        _connectionMapping.Add("VictimConn", "clan AB", liveUser);
        _connectionMapping.SetMute("VictimConn", MuteStatus.None, DateTime.MinValue);

        bool abortCalled = false;
        _hubCallerContext.Setup(c => c.Abort()).Callback(() => abortCalled = true);

        var adminUser = new ChatUser("admin#1", true, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("TestId", "W3C Lounge", adminUser);

        var endDate = DateTime.UtcNow.AddDays(1).ToString("O");
        await _chatHub.BanUser("victim#123", "bad behavior", false, endDate);

        var payloadInSignal = _reconcileHarness.PayloadFor("VictimConn", "PlayerBannedFromChat");
        Assert.AreEqual(1, _reconcileHarness.SignalCount("VictimConn", "PlayerBannedFromChat"),
            "Full-banned live user in an EXEMPT room must still receive PlayerBannedFromChat (R7/G5)");
        // SECURITY: the slimmed payload carries ONLY the expiry — never the LoungeMute.
        Assert.IsNotInstanceOf<LoungeMute>(payloadInSignal,
            "PlayerBannedFromChat must NOT send the full LoungeMute (leaks reason/isShadowBan)");
        AssertPlayerBannedPayloadIsEndDateOnly(payloadInSignal);
        Assert.IsFalse(abortCalled,
            "Context.Abort() must NOT be called when signalling a full ban in an exempt room (G1)");
    }

    [Test]
    public async Task BanUser_LiveUser_MultipleConnections_AllReconciled()
    {
        // A user can be connected from multiple clients (multiple connection ids). A full ban
        // must reconcile EVERY live connection: each cache flips to Full AND each receives
        // PlayerBannedFromChat. No Context.Abort() on any of them.
        var conn1User = new ChatUser("victim#123", false, null, new ProfilePicture(), null, null);
        var conn2User = new ChatUser("victim#123", false, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("VictimConn1", "W3C Lounge", conn1User);
        _connectionMapping.SetMute("VictimConn1", MuteStatus.None, DateTime.MinValue);
        _connectionMapping.Add("VictimConn2", "1 vs 1", conn2User);
        _connectionMapping.SetMute("VictimConn2", MuteStatus.None, DateTime.MinValue);

        bool abortCalled = false;
        _hubCallerContext.Setup(c => c.Abort()).Callback(() => abortCalled = true);

        var adminUser = new ChatUser("admin#1", true, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("TestId", "W3C Lounge", adminUser);

        var endDate = DateTime.UtcNow.AddDays(1).ToString("O");
        await _chatHub.BanUser("victim#123", "bad behavior", false, endDate);

        // Both caches updated to Full
        Assert.IsTrue(_connectionMapping.TryGetMute("VictimConn1", out var cached1));
        Assert.AreEqual(MuteStatus.Full, cached1.Status,
            "First connection's cache must be reconciled to Full");
        Assert.IsTrue(_connectionMapping.TryGetMute("VictimConn2", out var cached2));
        Assert.AreEqual(MuteStatus.Full, cached2.Status,
            "Second connection's cache must be reconciled to Full");

        // Both connections received exactly one PlayerBannedFromChat (via the reconcile harness)
        Assert.AreEqual(1, _reconcileHarness.SignalCount("VictimConn1", "PlayerBannedFromChat"),
            "First connection must receive PlayerBannedFromChat");
        Assert.AreEqual(1, _reconcileHarness.SignalCount("VictimConn2", "PlayerBannedFromChat"),
            "Second connection must receive PlayerBannedFromChat");

        Assert.IsFalse(abortCalled, "Context.Abort() must NOT be called on any connection (G1)");
    }

    [Test]
    public async Task BanUser_LiveUser_ShadowBan_SendsNoSignalToTarget()
    {
        // Shadow ban: illusion preserved — no PlayerBannedFromChat sent to target
        var liveUser = new ChatUser("victim#123", false, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("VictimConn", "W3C Lounge", liveUser);
        _connectionMapping.SetMute("VictimConn", MuteStatus.None, DateTime.MinValue);

        var adminUser = new ChatUser("admin#1", true, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("TestId", "W3C Lounge", adminUser);

        var endDate = DateTime.UtcNow.AddDays(1).ToString("O");
        await _chatHub.BanUser("victim#123", "spam", true, endDate);

        Assert.AreEqual(0, _reconcileHarness.SignalsFor("VictimConn").Count,
            "Shadow ban must send NO signal to the target (illusion preserved)");
    }

    [Test]
    public void BanUser_UserNotConnected_NoException()
    {
        // Banning a user who is not currently connected — should complete without error
        var adminUser = new ChatUser("admin#1", true, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("TestId", "W3C Lounge", adminUser);

        var endDate = DateTime.UtcNow.AddDays(1).ToString("O");

        Assert.DoesNotThrowAsync(() =>
            _chatHub.BanUser("offline#999", "reason", false, endDate));
    }

    // ── End-to-end: hub BanUser reconciles the cache, then the NEW send pipeline enforces it ─────
    // Re-expressed from the deleted old-protocol SubsequentSendMessage_*FromCache tests, now driving
    // SendMessage(channelId, content). Proves the ban-cache set by hub BanUser gates the new send path
    // WITHOUT a DB read (the ban row is wiped before the send).

    [Test]
    public async Task BanUser_LiveUser_FullBan_ThenNewSend_ReturnsMuted_CacheOnly()
    {
        var channel = await CreateChannel("W3C Lounge", ChannelType.Public);
        SeedLiveMember("VictimConn", "victim#123", channel.Id);
        SeatAdmin();

        var endDate = DateTime.UtcNow.AddDays(1).ToString("O");
        await _chatHub.BanUser("victim#123", "bad behavior", false, endDate);

        Assert.IsTrue(_connectionMapping.TryGetMute("VictimConn", out var cached));
        Assert.AreEqual(MuteStatus.Full, cached.Status, "Hub full ban must reconcile the victim's live cache to Full");

        // Wipe the DB so a DB read would find NO ban — enforcement must be cache-only.
        await _muteRepository.DeleteLoungeMute("victim#123");

        UseConnection("VictimConn");
        var result = await _chatHub.SendMessage(channel.Id, "should be rejected");

        Assert.AreEqual(ChatResultCode.Muted, result.Code,
            "After a live hub full ban, the next SendMessage in a public channel is rejected from the cache (no DB read)");
        var reloaded = await _channelRepository.Load(channel.Id);
        Assert.AreEqual(0L, reloaded.LastSeq, "A cache-muted send must not persist");
    }

    [Test]
    public async Task BanUser_LiveUser_ShadowBan_ThenNewSend_ReturnsOk_PersistsFlagged_CacheOnly()
    {
        var channel = await CreateChannel("W3C Lounge", ChannelType.Public);
        SeedLiveMember("VictimConn", "victim#123", channel.Id);
        SeatAdmin();

        var endDate = DateTime.UtcNow.AddDays(1).ToString("O");
        await _chatHub.BanUser("victim#123", "spam", true, endDate);

        Assert.IsTrue(_connectionMapping.TryGetMute("VictimConn", out var cached));
        Assert.AreEqual(MuteStatus.Shadow, cached.Status, "Hub shadow ban must reconcile the victim's live cache to Shadow");

        await _muteRepository.DeleteLoungeMute("victim#123");

        UseConnection("VictimConn");
        var result = await _chatHub.SendMessage(channel.Id, "invisible message");

        Assert.AreEqual(ChatResultCode.Ok, result.Code, "A shadow-banned user still gets Ok (the illusion)");
        var persisted = await _messageRepository.Load(result.MessageId);
        Assert.IsNotNull(persisted);
        Assert.IsTrue(persisted.Shadow,
            "The message persists flagged Shadow=true from the reconciled cache (no DB read)");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private void SeatAdmin() =>
        _connectionMapping.Add("TestId", "W3C Lounge", new ChatUser("admin#1", true, null, new ProfilePicture(), null, null));

    private void UseConnection(string connectionId) =>
        _hubCallerContext.Setup(c => c.ConnectionId).Returns(connectionId);

    private async Task<ChatChannel> CreateChannel(string name, ChannelType type)
    {
        var channel = new ChatChannel { Type = type, Name = name, NormalizedName = ChannelNames.Normalize(name) };
        await _channelRepository.Insert(channel);
        return channel;
    }

    // Seeds a victim connection the SAME way the connect path does: a live session, the connection→user
    // mapping (so a ban reconciles it), an unbanned mute cache, and an OnlineMemberRegistry membership.
    private void SeedLiveMember(string connectionId, string battleTag, string channelId)
    {
        _sessionRegistry.Register(
            connectionId,
            new W3CUserAuthentication { BattleTag = battleTag, Name = battleTag.Split('#')[0] },
            null);
        _connectionMapping.Add(connectionId, "W3C Lounge", new ChatUser(battleTag, false, null, new ProfilePicture(), null, null));
        _connectionMapping.SetMute(connectionId, MuteStatus.None, DateTime.MinValue);
        _onlineMemberRegistry.Join(channelId, connectionId, new MemberState(battleTag, NotificationLevel.Mentions, 0));
    }

    /// <summary>
    /// Asserts the slimmed PlayerBannedFromChat payload exposes ONLY an <c>endDate</c> property
    /// (a future DateTime) and leaks neither <c>reason</c> nor <c>isShadowBan</c> to the client.
    /// The payload is an anonymous type, so it is inspected via reflection.
    /// </summary>
    private static void AssertPlayerBannedPayloadIsEndDateOnly(object payload)
    {
        Assert.IsNotNull(payload, "PlayerBannedFromChat payload must not be null");
        var type = payload.GetType();
        var props = type.GetProperties().Select(p => p.Name).ToList();

        Assert.Contains("endDate", props,
            "PlayerBannedFromChat payload must carry an endDate (backward-compat with old clients)");
        Assert.IsFalse(props.Contains("reason"),
            "SECURITY: PlayerBannedFromChat payload must NOT leak the moderation reason");
        Assert.IsFalse(props.Contains("isShadowBan"),
            "SECURITY: PlayerBannedFromChat payload must NOT leak the isShadowBan flag");

        var endDate = (DateTime)type.GetProperty("endDate").GetValue(payload);
        Assert.Greater(endDate, DateTime.UtcNow,
            "The endDate in the payload must be the (future) ban expiry");
    }
}
