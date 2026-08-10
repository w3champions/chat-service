using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.FanOut;
using W3ChampionsChatService.Protocol;
using W3ChampionsChatService.Sessions;
using W3ChampionsChatService.Users;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// The FreshFromWb rule is the single most important behaviour here: a website-backend blip must never
/// broadcast a degraded profile to every viewer in a channel.
/// </summary>
public class FlairRefresherTests : IntegrationTestBase
{
    private const string ChangedTag = "peter#123";
    private const string ViewerTag = "alice#456";

    private HubPushCaptureHarness _harness;
    private SessionRegistry _sessions;
    private ConnectionMapping _connections;
    private FocusRegistry _focus;
    private UserDirectoryRepository _userDirectory;
    private Mock<IChatAuthenticationService> _auth;
    private FlairRefresher _refresher;

    [SetUp]
    public void SetupBeforeEach()
    {
        _harness = new HubPushCaptureHarness();
        _sessions = new SessionRegistry();
        _connections = new ConnectionMapping();
        _focus = new FocusRegistry();
        _userDirectory = new UserDirectoryRepository(MongoClient);
        _auth = new Mock<IChatAuthenticationService>();

        _refresher = new FlairRefresher(
            _sessions, _auth.Object, _connections, _userDirectory, _focus,
            _harness.HubContext, new FakeTimeProvider());
    }

    private static ChatUser UserWith(string battleTag, AvatarCategory race, long pictureId) =>
        new(battleTag, false, "W3C",
            new ProfilePicture { Race = race, PictureId = pictureId, IsClassic = false },
            new ChatColor("chat_color_purple"),
            [new ChatIcon("chat_icon_crown")]);

    private void GoOnline(string connectionId, string battleTag)
    {
        _sessions.Register(connectionId, new W3CUserAuthentication { BattleTag = battleTag, Name = battleTag.Split('#')[0] }, null);
        _connections.RegisterUser(connectionId, UserWith(battleTag, AvatarCategory.HU, 1));
    }

    private void ResolvesTo(ChatUser user, bool freshFromWb) =>
        _auth.Setup(a => a.GetUserFromIdentity(It.IsAny<W3CUserAuthentication>()))
            .ReturnsAsync(new ChatUserResolution(user, freshFromWb));

    [Test]
    public async Task Refresh_WithNoLiveSession_IsANoOp()
    {
        ResolvesTo(UserWith(ChangedTag, AvatarCategory.NE, 7), true);

        await _refresher.Refresh(ChangedTag);

        _auth.Verify(a => a.GetUserFromIdentity(It.IsAny<W3CUserAuthentication>()), Times.Never);
        Assert.That(_harness.AllSignals, Is.Empty);
    }

    [Test]
    public async Task Refresh_UpdatesConnectionMappingSoTheirOwnNextMessageCarriesTheNewFlair()
    {
        GoOnline("conn-peter", ChangedTag);
        ResolvesTo(UserWith(ChangedTag, AvatarCategory.NE, 7), true);

        await _refresher.Refresh(ChangedTag);

        var cached = _connections.GetUser("conn-peter");
        Assert.That(cached.ProfilePicture.Race, Is.EqualTo(AvatarCategory.NE));
        Assert.That(cached.ProfilePicture.PictureId, Is.EqualTo(7));
    }

    [Test]
    public async Task Refresh_EmitsFlairChangedToTheChangedUsersOwnConnection_EvenWithNoFocus()
    {
        // A user focused on nothing must still see their OWN avatar update.
        GoOnline("conn-peter", ChangedTag);
        ResolvesTo(UserWith(ChangedTag, AvatarCategory.NE, 7), true);

        await _refresher.Refresh(ChangedTag);

        var signals = _harness.SignalsFor("conn-peter").Where(s => s.Method == ChatEvents.FlairChanged).ToList();
        Assert.That(signals, Has.Count.EqualTo(1));
        var payload = (FlairChangedDto)signals.Single().Payload;
        Assert.That(payload.BattleTag, Is.EqualTo(ChangedTag));
        Assert.That(payload.Profile.ProfilePicture.PictureId, Is.EqualTo(7));
        Assert.That(payload.Profile.ClanId, Is.EqualTo("W3C"));
    }

    [Test]
    public async Task Refresh_EmitsToEveryConnectionFocusedOnAChannelTheChangedUserIsFocusedOn()
    {
        GoOnline("conn-peter", ChangedTag);
        GoOnline("conn-alice", ViewerTag);
        _focus.Focus("conn-peter", "lounge", ChangedTag);
        _focus.Focus("conn-alice", "lounge", ViewerTag);
        ResolvesTo(UserWith(ChangedTag, AvatarCategory.NE, 7), true);

        await _refresher.Refresh(ChangedTag);

        Assert.That(_harness.SignalsFor("conn-alice").Count(s => s.Method == ChatEvents.FlairChanged), Is.EqualTo(1));
        Assert.That(_harness.SignalsFor("conn-peter").Count(s => s.Method == ChatEvents.FlairChanged), Is.EqualTo(1));
    }

    [Test]
    public async Task Refresh_DoesNotEmitToAConnectionFocusedOnlyOnAnUnrelatedChannel()
    {
        // A connection focused only on a channel the changed player is not focused on must receive
        // nothing — a regression to Clients.All would still pass every other test in this file.
        const string BystanderTag = "bystander#789";
        GoOnline("conn-peter", ChangedTag);
        GoOnline("conn-bystander", BystanderTag);
        _focus.Focus("conn-peter", "lounge", ChangedTag);
        _focus.Focus("conn-bystander", "clan", BystanderTag);
        ResolvesTo(UserWith(ChangedTag, AvatarCategory.NE, 7), true);

        await _refresher.Refresh(ChangedTag);

        Assert.That(_harness.SignalsFor("conn-bystander"), Is.Empty,
            "a connection focused only on an unrelated channel must receive no FlairChanged signal");
    }

    [Test]
    public async Task Refresh_SendsOncePerConnection_EvenWhenSharingSeveralChannels()
    {
        GoOnline("conn-peter", ChangedTag);
        GoOnline("conn-alice", ViewerTag);
        _focus.Focus("conn-peter", "lounge", ChangedTag);
        _focus.Focus("conn-peter", "clan", ChangedTag);
        _focus.Focus("conn-alice", "lounge", ViewerTag);
        _focus.Focus("conn-alice", "clan", ViewerTag);
        ResolvesTo(UserWith(ChangedTag, AvatarCategory.NE, 7), true);

        await _refresher.Refresh(ChangedTag);

        Assert.That(_harness.SignalsFor("conn-alice").Count(s => s.Method == ChatEvents.FlairChanged), Is.EqualTo(1));
    }

    [Test]
    public async Task Refresh_WhenNotFreshFromWb_DoesNothingAtAll()
    {
        // THE RULE. A wb blip resolves to a degraded tier-3 profile. Acting on it would replace good
        // cached flair and broadcast the default avatar to everyone viewing this user — turning a
        // transient upstream hiccup into a visible regression for the whole channel.
        GoOnline("conn-peter", ChangedTag);
        GoOnline("conn-alice", ViewerTag);
        _focus.Focus("conn-peter", "lounge", ChangedTag);
        _focus.Focus("conn-alice", "lounge", ViewerTag);

        var degraded = new ChatUser(ChangedTag, false, null, new ProfilePicture(), null, null);
        ResolvesTo(degraded, false);

        await _refresher.Refresh(ChangedTag);

        Assert.That(_harness.AllSignals, Is.Empty, "no FlairChanged may be emitted on a stale resolution");

        var cached = _connections.GetUser("conn-peter");
        Assert.That(cached.ProfilePicture.Race, Is.EqualTo(AvatarCategory.HU),
            "the good cached ChatUser must survive — never clobbered by a degraded resolution");
        Assert.That(cached.ClanTag, Is.EqualTo("W3C"));

        var entry = await _userDirectory.Load(ChangedTag);
        Assert.That(entry, Is.Null, "no directory write may happen on a stale resolution");
    }

    [Test]
    public async Task Refresh_WhenFreshFromWb_WritesTheDirectoryProfile()
    {
        GoOnline("conn-peter", ChangedTag);
        ResolvesTo(UserWith(ChangedTag, AvatarCategory.NE, 7), true);

        await _refresher.Refresh(ChangedTag);

        var entry = await _userDirectory.Load(ChangedTag);
        Assert.That(entry, Is.Not.Null);
        Assert.That(entry.Profile.ProfilePicture.PictureId, Is.EqualTo(7));
    }

    // Finding 2 (P2): a player can disconnect WHILE GetUserFromIdentity is in flight. Every side effect
    // after that await must be skipped, or ConnectionMapping.RegisterUser resurrects an entry that
    // ChatHub.OnDisconnectedAsync's `finally` already removed — and since disconnect only fires once,
    // nothing would ever remove it again (an unbounded leak under repeated races).
    [Test]
    public async Task Refresh_WhenTheSessionDisconnectsDuringTheAwait_SkipsEverySideEffect()
    {
        GoOnline("conn-peter", ChangedTag);
        _auth.Setup(a => a.GetUserFromIdentity(It.IsAny<W3CUserAuthentication>()))
            .Returns(async () =>
            {
                // Simulate ChatHub.OnDisconnectedAsync's `finally` block running while this call is
                // in flight: Unregister the session, then remove the connection→user mapping — exactly
                // what the hub does, in the same order.
                _sessions.Unregister("conn-peter");
                _connections.Remove("conn-peter");
                await Task.Yield();
                return new ChatUserResolution(UserWith(ChangedTag, AvatarCategory.NE, 7), true);
            });

        await _refresher.Refresh(ChangedTag);

        Assert.That(_connections.GetUser("conn-peter"), Is.Null,
            "RegisterUser must not resurrect an entry ChatHub.OnDisconnectedAsync already removed");
        Assert.That(_harness.AllSignals, Is.Empty,
            "no FlairChanged may be sent to a connection that disconnected mid-refresh");
        var entry = await _userDirectory.Load(ChangedTag);
        Assert.That(entry, Is.Null, "no directory write may happen for a session that is no longer live");
    }

    // Finding 2 (P2), reconnect variant: the player disconnects AND reconnects under a NEW connection id
    // while GetUserFromIdentity is in flight. The stale `session` captured before the await must not be
    // acted on even though a session for the battleTag exists again — it is not the SAME connection.
    [Test]
    public async Task Refresh_WhenTheSessionReconnectsUnderANewConnectionDuringTheAwait_SkipsEverySideEffect()
    {
        GoOnline("conn-peter-old", ChangedTag);
        _auth.Setup(a => a.GetUserFromIdentity(It.IsAny<W3CUserAuthentication>()))
            .Returns(async () =>
            {
                _sessions.Unregister("conn-peter-old");
                _connections.Remove("conn-peter-old");
                GoOnline("conn-peter-new", ChangedTag);
                await Task.Yield();
                return new ChatUserResolution(UserWith(ChangedTag, AvatarCategory.NE, 7), true);
            });

        await _refresher.Refresh(ChangedTag);

        Assert.That(_connections.GetUser("conn-peter-old"), Is.Null,
            "the OLD connection's mapping must not be resurrected");
        Assert.That(_harness.AllSignals, Is.Empty,
            "the stale pre-await session must not be used to push a FlairChanged");
    }

    // Codex P2 follow-up: the pre-write revalidation (the test above) closes the "disconnect during the
    // await" race, but ChatHub.OnDisconnectedAsync's Unregister+Remove sequence can still land in the
    // narrow, unsynchronized gap between that revalidation and the ConnectionMapping.RegisterUser write —
    // two separate lock sections with no await in between. Real thread preemption would only hit this
    // non-deterministically, so a mocked ISessionRegistry is used to force the exact interleaving: the
    // session is present for the first two reads (the initial lookup and the pre-write revalidation) and
    // gone by the THIRD read — the post-write validation this fix adds. A real ConnectionMapping is used
    // so the assertion exercises the actual Remove/RegisterUser interplay, not a mock.
    [Test]
    public async Task Refresh_WhenDisconnectRacesTheConnectionMappingWrite_LeavesNoOrphanMappingEntry()
    {
        var connections = new ConnectionMapping();
        connections.RegisterUser("conn-peter", UserWith(ChangedTag, AvatarCategory.HU, 1));

        var identity = new W3CUserAuthentication { BattleTag = ChangedTag, Name = "peter" };
        var liveSession = new ChatSession { ConnectionId = "conn-peter", Identity = identity, Context = null };

        var sessions = new Mock<ISessionRegistry>();
        sessions.SetupSequence(s => s.GetByBattleTag(ChangedTag))
            .Returns(liveSession)      // initial lookup (Refresh's first read)
            .Returns(liveSession)      // pre-write revalidation (after the GetUserFromIdentity await)
            .Returns((ChatSession)null); // post-write validation: disconnect landed in the gap

        var auth = new Mock<IChatAuthenticationService>();
        auth.Setup(a => a.GetUserFromIdentity(It.IsAny<W3CUserAuthentication>()))
            .ReturnsAsync(new ChatUserResolution(UserWith(ChangedTag, AvatarCategory.NE, 7), true));

        var refresher = new FlairRefresher(
            sessions.Object, auth.Object, connections, _userDirectory, _focus,
            _harness.HubContext, new FakeTimeProvider());

        await refresher.Refresh(ChangedTag);

        Assert.That(connections.GetUser("conn-peter"), Is.Null,
            "the post-write validation must remove the entry RegisterUser resurrected once the authoritative session is gone");
    }

    // A newer connection can replace this session between the ConnectionMapping write and the post-write
    // check (e.g. two closely spaced profile changes). Once that happens, this refresh is stale: it must
    // stop after cleaning up the mapping it wrote, not fall through and overwrite a newer directory
    // profile or broadcast an older flair to live viewers.
    [Test]
    public async Task Refresh_WhenDisconnectRacesTheConnectionMappingWrite_PerformsNoDirectoryUpsertOrFanOut()
    {
        var connections = new ConnectionMapping();
        connections.RegisterUser("conn-peter", UserWith(ChangedTag, AvatarCategory.HU, 1));

        var identity = new W3CUserAuthentication { BattleTag = ChangedTag, Name = "peter" };
        var liveSession = new ChatSession { ConnectionId = "conn-peter", Identity = identity, Context = null };

        var sessions = new Mock<ISessionRegistry>();
        sessions.SetupSequence(s => s.GetByBattleTag(ChangedTag))
            .Returns(liveSession)      // initial lookup (Refresh's first read)
            .Returns(liveSession)      // pre-write revalidation (after the GetUserFromIdentity await)
            .Returns((ChatSession)null); // post-write validation: a newer connection took over in the gap

        var auth = new Mock<IChatAuthenticationService>();
        auth.Setup(a => a.GetUserFromIdentity(It.IsAny<W3CUserAuthentication>()))
            .ReturnsAsync(new ChatUserResolution(UserWith(ChangedTag, AvatarCategory.NE, 7), true));

        var refresher = new FlairRefresher(
            sessions.Object, auth.Object, connections, _userDirectory, _focus,
            _harness.HubContext, new FakeTimeProvider());

        await refresher.Refresh(ChangedTag);

        Assert.That(connections.GetUser("conn-peter"), Is.Null,
            "the stale mapping write must still be cleaned up");
        var entry = await _userDirectory.Load(ChangedTag);
        Assert.That(entry, Is.Null,
            "a stale refresh must not overwrite the directory profile a newer refresh may have already written");
        Assert.That(_harness.AllSignals, Is.Empty,
            "a stale refresh must not broadcast an outdated flair to live viewers");
    }

    // Finding 3 (P2): the webhook-supplied battleTag can carry website-backend's storage casing, while
    // the live session identity carries the authoritative display casing. DisplayBattleTag must always
    // win with the identity's casing — matching the connect path (ChatHub.UpsertDirectory).
    [Test]
    public async Task Refresh_UsesTheSessionIdentitysCasing_NotTheWebhookSuppliedCasing()
    {
        const string IdentityCasedTag = "Peter#123";
        const string WebhookCasedTag = "peter#123";
        GoOnline("conn-peter", IdentityCasedTag);
        ResolvesTo(UserWith(IdentityCasedTag, AvatarCategory.NE, 7), true);

        await _refresher.Refresh(WebhookCasedTag);

        var entry = await _userDirectory.Load(IdentityCasedTag);
        Assert.That(entry, Is.Not.Null);
        Assert.That(entry.DisplayBattleTag, Is.EqualTo(IdentityCasedTag),
            "the session identity's display casing must win over the webhook-supplied casing");

        var signals = _harness.SignalsFor("conn-peter").Where(s => s.Method == ChatEvents.FlairChanged).ToList();
        Assert.That(signals, Has.Count.EqualTo(1));
        var payload = (FlairChangedDto)signals.Single().Payload;
        Assert.That(payload.BattleTag, Is.EqualTo(IdentityCasedTag),
            "the pushed FlairChangedDto must also carry the identity's casing, matching the directory write");
    }
}
