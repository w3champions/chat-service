using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
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
        // Wire the reconciliation service's ApplyBanAsync to the REAL repo so controller/hub bans
        // actually persist to (and are removable from) the live DB in these tests.
        _harness = new MuteReconciliationTestHarness(_connectionMapping, _muteRepository);
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
    public async Task ControllerDelete_MixedCaseBattleTag_ActuallyDeletesRow_AndReturnsOk()
    {
        // Casing fix: the row is stored lowercased (AddLoungeMute), so a mixed-case DELETE must still
        // match and remove it — and report 200 OK, not a false 404.
        await _muteRepository.AddLoungeMute(new LoungeMuteRequest
        {
            battleTag = "Victim#123",
            endDate = DateTime.UtcNow.AddDays(1).ToString("O"),
            author = "admin#1",
            reason = "bad behavior",
            isShadowBan = false
        });

        var result = await _controller.DeleteLoungeMute("VICTIM#123");

        Assert.IsInstanceOf<OkObjectResult>(result,
            "A mixed-case DELETE that matches a stored (lowercased) mute must return 200 OK");
        Assert.IsNull(await _muteRepository.GetMutedPlayer("victim#123"),
            "A mixed-case DELETE must actually remove the stored row (casing fix)");
    }

    [Test]
    public async Task ControllerDelete_AbsentTag_ReturnsNotFound_ButStillClearsLiveCache()
    {
        // An explicit moderator unban of a target whose DB row is already gone/expired must STILL free
        // any live connection (reconcile-then-respond), yet report an accurate 404 since nothing was
        // deleted from the DB.
        var liveUser = new ChatUser("victim#123", false, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("VictimConn", "W3C Lounge", liveUser);
        _connectionMapping.SetMute("VictimConn", MuteStatus.Full, DateTime.UtcNow.AddDays(1));
        // No DB row exists for this tag (SetUp drops the DB; we never AddLoungeMute here).

        var result = await _controller.DeleteLoungeMute("victim#123");

        Assert.IsInstanceOf<NotFoundObjectResult>(result,
            "DELETE of an absent tag must return 404 (nothing was deleted)");
        Assert.IsTrue(_connectionMapping.TryGetMute("VictimConn", out var cached));
        Assert.AreEqual(MuteStatus.None, cached.Status,
            "An explicit unban must clear the live cache even when the DB row was already gone");
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

    // ── T5 ApplyBanAsync (consolidated ban orchestration) ─────────────────────

    [Test]
    public async Task ApplyBanAsync_FullBan_PersistsAndReconciles()
    {
        var liveUser = new ChatUser("victim#123", false, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("VictimConn", "W3C Lounge", liveUser);
        _connectionMapping.SetMute("VictimConn", MuteStatus.None, DateTime.MinValue);

        var (success, parsed) = await _harness.Service.ApplyBanAsync(new LoungeMuteRequest
        {
            battleTag = "victim#123",
            endDate = DateTime.UtcNow.AddDays(1).ToString("O"),
            author = "admin#1",
            reason = "bad behavior",
            isShadowBan = false
        });

        Assert.IsTrue(success, "Full ban with a valid endDate must succeed");
        Assert.Greater(parsed, DateTime.UtcNow, "Parsed endDate must be the future ban expiry");
        // Persisted to the DB.
        Assert.IsNotNull(await _muteRepository.GetMutedPlayer("victim#123"), "ApplyBanAsync must persist the mute");
        // Live connection reconciled to Full + received the signal.
        Assert.IsTrue(_connectionMapping.TryGetMute("VictimConn", out var cached));
        Assert.AreEqual(MuteStatus.Full, cached.Status, "Live cache must be reconciled to Full");
        Assert.AreEqual(1, _harness.SignalCount("VictimConn", "PlayerBannedFromChat"),
            "Full ban must push PlayerBannedFromChat");
    }

    [Test]
    public async Task ApplyBanAsync_ShadowBan_PersistsAndReconciles_Silent()
    {
        var liveUser = new ChatUser("victim#123", false, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("VictimConn", "W3C Lounge", liveUser);
        _connectionMapping.SetMute("VictimConn", MuteStatus.None, DateTime.MinValue);

        var (success, _) = await _harness.Service.ApplyBanAsync(new LoungeMuteRequest
        {
            battleTag = "victim#123",
            endDate = DateTime.UtcNow.AddDays(1).ToString("O"),
            author = "admin#1",
            reason = "spam",
            isShadowBan = true
        });

        Assert.IsTrue(success);
        var persisted = await _muteRepository.GetMutedPlayer("victim#123");
        Assert.IsNotNull(persisted);
        Assert.IsTrue(persisted.isShadowBan, "Shadow ban must persist with isShadowBan=true");
        Assert.IsTrue(_connectionMapping.TryGetMute("VictimConn", out var cached));
        Assert.AreEqual(MuteStatus.Shadow, cached.Status, "Live cache must be reconciled to Shadow");
        Assert.AreEqual(0, _harness.SignalsFor("VictimConn").Count,
            "Shadow ban must send NO signal to the target (illusion preserved)");
    }

    [Test]
    public async Task ApplyBanAsync_MalformedEndDate_ReturnsFailure_PersistsThenSkipsReconcile()
    {
        // Use a mock repo whose AddLoungeMute succeeds (no-op) so the ban is "persisted" but the
        // subsequent TryParse fails — isolating the parse-failure branch. (The real MuteRepository's
        // AddLoungeMute would itself throw on a malformed date before we reach the TryParse guard.)
        var mutes = new Mock<IMuteRepository>();
        var persisted = false;
        mutes.Setup(m => m.AddLoungeMute(It.IsAny<LoungeMuteRequest>()))
            .Callback(() => persisted = true)
            .Returns(Task.CompletedTask);
        var harness = new MuteReconciliationTestHarness(_connectionMapping, mutes.Object);

        var liveUser = new ChatUser("victim#123", false, null, new ProfilePicture(), null, null);
        _connectionMapping.Add("VictimConn", "W3C Lounge", liveUser);
        _connectionMapping.SetMute("VictimConn", MuteStatus.None, DateTime.MinValue);

        var (success, parsed) = await harness.Service.ApplyBanAsync(new LoungeMuteRequest
        {
            battleTag = "victim#123",
            endDate = "not-a-real-date",
            author = "admin#1",
            reason = "x",
            isShadowBan = false
        });

        Assert.IsFalse(success, "A malformed endDate must return success=false");
        Assert.AreEqual(DateTime.MinValue, parsed);
        Assert.IsTrue(persisted, "The ban is still persisted before the parse failure");
        // The live cache must NOT have been reconciled (still None) and no signal sent.
        Assert.IsTrue(_connectionMapping.TryGetMute("VictimConn", out var cached));
        Assert.AreEqual(MuteStatus.None, cached.Status, "Live cache must NOT be reconciled on a parse failure");
        Assert.AreEqual(0, harness.SignalsFor("VictimConn").Count, "No signal on a parse failure");
    }

    [Test]
    public async Task ApplyBanAsync_HubBanUser_And_ControllerAddLoungeMute_ProduceIdenticalCacheState()
    {
        // Two live connections of the same battleTag; ban one via the hub, the other via the controller.
        // Both paths go through ApplyBanAsync → identical cache state.
        _connectionMapping.Add("HubConn", "W3C Lounge", new ChatUser("victim#123", false, null, new ProfilePicture(), null, null));
        _connectionMapping.SetMute("HubConn", MuteStatus.None, DateTime.MinValue);

        // Admin seat for the hub BanUser (GetUser must resolve for the caller).
        _connectionMapping.Add("TestId", "W3C Lounge", new ChatUser("admin#1", true, null, new ProfilePicture(), null, null));
        var hubContext = new Mock<HubCallerContext>();
        hubContext.Setup(c => c.ConnectionId).Returns("TestId");
        _chatHub.Context = hubContext.Object;

        var endDate = DateTime.UtcNow.AddDays(1).ToString("O");
        await _chatHub.BanUser("victim#123", "bad behavior", false, endDate);

        Assert.IsTrue(_connectionMapping.TryGetMute("HubConn", out var afterHub));
        var hubStatus = afterHub.Status;

        // Reset that connection's cache and ban via the controller instead.
        _connectionMapping.SetMute("HubConn", MuteStatus.None, DateTime.MinValue);
        await _controller.AddLoungeMute(new LoungeMuteRequest
        {
            battleTag = "victim#123",
            endDate = endDate,
            author = "admin#1",
            reason = "bad behavior",
            isShadowBan = false
        });

        Assert.IsTrue(_connectionMapping.TryGetMute("HubConn", out var afterController));
        Assert.AreEqual(hubStatus, afterController.Status,
            "Hub BanUser and controller AddLoungeMute must produce identical cache status");
        Assert.AreEqual(MuteStatus.Full, afterController.Status);
    }
}
