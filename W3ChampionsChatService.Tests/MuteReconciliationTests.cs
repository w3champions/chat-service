using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Moq;
using NUnit.Framework;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Mutes;
using W3ChampionsChatService.Settings;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// Closes the out-of-band-ban regression at the WRITE path: a ban/unban issued via the REST
/// <see cref="MuteController"/> must reconcile the target's live connections (via
/// <see cref="MuteReconciliationService"/>) so it takes effect immediately — exactly like the hub's
/// <c>BanUser</c> — without a per-send DB read and without waiting for a reconnect.
/// </summary>
public class MuteReconciliationTests : IntegrationTestBase
{
    private MuteRepository _muteRepository;
    private ConnectionMapping _connectionMapping;
    private MuteReconciliationTestHarness _harness;
    private MuteController _controller;

    // For driving SendMessage on the victim's connection through a hub.
    private ChatHub _chatHub;
    private Mock<IHubCallerClients> _clients;
    private Mock<HubCallerContext> _hubCallerContext;
    private Mock<ISingleClientProxy> _callerProxy;
    private Mock<IClientProxy> _groupProxy;
    private int _groupSendCount;

    [SetUp]
    public void SetupBeforeEach()
    {
        _muteRepository = new MuteRepository(MongoClient);
        _connectionMapping = new ConnectionMapping();
        _harness = new MuteReconciliationTestHarness(_connectionMapping);
        _controller = new MuteController(_muteRepository, _harness.Service);

        var chatAuthService = new Mock<IChatAuthenticationService>();
        chatAuthService.Setup(m => m.GetUser(It.IsAny<string>()))
            .ReturnsAsync(new ChatUser("victim#123", false, null, new ProfilePicture(), null, null));

        _chatHub = new ChatHub(
            chatAuthService.Object,
            _muteRepository,
            new SettingsRepository(MongoClient),
            _connectionMapping,
            new ChatHistory(),
            _harness.Service,
            null);

        _clients = new Mock<IHubCallerClients>();
        _callerProxy = new Mock<ISingleClientProxy>();
        _callerProxy.Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _groupSendCount = 0;
        _groupProxy = new Mock<IClientProxy>();
        _groupProxy.Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((_, _, _) => _groupSendCount++)
            .Returns(Task.CompletedTask);
        _clients.Setup(c => c.Caller).Returns(_callerProxy.Object);
        _clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupProxy.Object);

        _hubCallerContext = new Mock<HubCallerContext>();
        _hubCallerContext.Setup(c => c.ConnectionId).Returns("VictimConn");
        _chatHub.Clients = _clients.Object;
        _chatHub.Context = _hubCallerContext.Object;
        _chatHub.Groups = new Mock<IGroupManager>().Object;
    }

    [Test]
    public async Task ControllerBan_FullBan_TakesEffectOnLiveConnection_WithoutDbRead()
    {
        // A live, unmuted user seated in a public room.
        var liveUser = new ChatUser("victim#123", false, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("VictimConn", "W3C Lounge", liveUser);
        _connectionMapping.SetMute("VictimConn", MuteStatus.None, DateTime.MinValue);

        // Moderator issues a FULL ban via the REST controller.
        var result = await _controller.AddLoungeMute(new LoungeMuteRequest
        {
            battleTag = "victim#123",
            endDate = DateTime.UtcNow.AddDays(1).ToString("O"),
            author = "admin#1",
            reason = "bad behavior",
            isShadowBan = false
        });
        Assert.IsNotNull(result, "Controller POST must return a result");

        // The live connection's cache must be reconciled to Full and receive PlayerBannedFromChat (expiry only).
        Assert.IsTrue(_connectionMapping.TryGetMute("VictimConn", out var cached));
        Assert.AreEqual(MuteStatus.Full, cached.Status,
            "Controller full ban must reconcile the live connection's cache to Full");
        Assert.AreEqual(1, _harness.SignalCount("VictimConn", "PlayerBannedFromChat"),
            "Controller full ban must push PlayerBannedFromChat to the live connection");

        // Now wipe the DB so a DB read would find NO ban — proving enforcement is cache-only.
        await _muteRepository.DeleteLoungeMute("victim#123");

        await _chatHub.SendMessage("should be rejected");

        Assert.AreEqual(0, _groupSendCount,
            "After a controller ban, the next SendMessage in a public room is rejected from the cache (no DB read)");
    }

    [Test]
    public async Task ControllerBan_ShadowBan_ReconcilesCache_SilentToTarget()
    {
        var liveUser = new ChatUser("victim#123", false, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("VictimConn", "W3C Lounge", liveUser);
        _connectionMapping.SetMute("VictimConn", MuteStatus.None, DateTime.MinValue);

        await _controller.AddLoungeMute(new LoungeMuteRequest
        {
            battleTag = "victim#123",
            endDate = DateTime.UtcNow.AddDays(1).ToString("O"),
            author = "admin#1",
            reason = "spam",
            isShadowBan = true
        });

        Assert.IsTrue(_connectionMapping.TryGetMute("VictimConn", out var cached));
        Assert.AreEqual(MuteStatus.Shadow, cached.Status,
            "Controller shadow ban must reconcile the live connection's cache to Shadow");
        Assert.AreEqual(0, _harness.SignalsFor("VictimConn").Count,
            "Controller shadow ban must send NO signal to the target (illusion preserved)");
    }

    [Test]
    public async Task ControllerUnban_ClearsCache_UserCanSendAgain_WithoutReconnect()
    {
        // A live, fully-banned user seated in a public room.
        var liveUser = new ChatUser("victim#123", false, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("VictimConn", "W3C Lounge", liveUser);
        _connectionMapping.SetMute("VictimConn", MuteStatus.Full, DateTime.UtcNow.AddDays(1));
        await _muteRepository.AddLoungeMute(new LoungeMuteRequest
        {
            battleTag = "victim#123",
            endDate = DateTime.UtcNow.AddDays(1).ToString("O"),
            author = "admin#1",
            reason = "bad behavior",
            isShadowBan = false
        });

        // Moderator deletes the mute via the REST controller.
        await _controller.DeleteLoungeMute("victim#123");

        // The live connection's cache must be cleared to None so the user can send again server-side.
        Assert.IsTrue(_connectionMapping.TryGetMute("VictimConn", out var cached));
        Assert.AreEqual(MuteStatus.None, cached.Status,
            "Controller unban must clear the live connection's cached mute to None");

        await _chatHub.SendMessage("I can talk again");

        Assert.AreEqual(1, _groupSendCount,
            "After a controller unban, the user can send again in a public room without reconnecting");
    }

    [Test]
    public async Task Service_ApplyMute_NoLiveConnection_DoesNotThrow()
    {
        // No connection for the target — applying a mute must be a graceful no-op.
        Assert.DoesNotThrowAsync(() =>
            _harness.Service.ApplyMuteToLiveConnections("ghost#1", MuteStatus.Full, DateTime.UtcNow.AddDays(1)));
        await Task.CompletedTask;
    }

    [Test]
    public async Task Service_ClearMute_NoLiveConnection_DoesNotThrow()
    {
        Assert.DoesNotThrowAsync(() => _harness.Service.ClearMuteOnLiveConnections("ghost#1"));
        await Task.CompletedTask;
    }

    [Test]
    public async Task Service_ApplyMute_FullBan_PayloadIsEndDateOnly()
    {
        var liveUser = new ChatUser("victim#123", false, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("VictimConn", "W3C Lounge", liveUser);
        _connectionMapping.SetMute("VictimConn", MuteStatus.None, DateTime.MinValue);

        var endDate = DateTime.UtcNow.AddDays(3);
        await _harness.Service.ApplyMuteToLiveConnections("victim#123", MuteStatus.Full, endDate);

        var payload = _harness.PayloadFor("VictimConn", "PlayerBannedFromChat");
        Assert.IsNotNull(payload, "Full ban must push PlayerBannedFromChat");
        Assert.IsNotInstanceOf<LoungeMute>(payload,
            "PlayerBannedFromChat must NOT send the full LoungeMute (leaks reason/isShadowBan)");
        var props = payload.GetType().GetProperties();
        Assert.AreEqual(1, props.Length, "Payload must expose exactly one property (endDate)");
        Assert.AreEqual("endDate", props[0].Name, "The only payload property must be endDate");
        Assert.AreEqual(endDate, (DateTime)props[0].GetValue(payload),
            "Payload endDate must equal the ban expiry");
    }
}
