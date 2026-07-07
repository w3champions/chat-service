using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
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
using W3ChampionsChatService.Relationships;
using W3ChampionsChatService.Sessions;
using W3ChampionsChatService.Users;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// C6 Task 10 (D12, acceptance 6): the one-shot presence READ surface —
/// <c>GetPresence</c>/<c>GetPresenceDetails</c> (<c>ChatHub.Presence.cs</c>). Complements
/// <see cref="ChatHubPresenceTests"/> (Task 9's LIVE interest-gated stream) with the explicit,
/// client-driven batch read a DM list/friends panel calls on demand. Drives the SHIPPED connect/
/// disconnect flow (mirrors <see cref="HubProtocolIntegrationTests"/>) so
/// <c>GetPresenceDetails_Friend_CarriesLastSeenAt_FromDisconnectUpsert</c> exercises the REAL Task 3
/// connect/disconnect directory upserts, not a seeded stand-in. A real <see cref="RelationshipProvider"/>
/// over a <see cref="FakeRelationshipSource"/> (NEVER HTTP — mirrors <see cref="ChatHubOpenDmTests"/>)
/// gives per-tag control of the caller's own friends list, plus an outage toggle for the fail-closed
/// leg. NUnit constraint style.
/// </summary>
public class ChatHubPresenceReadTests : IntegrationTestBase
{
    private static readonly DateTime T0 = new(2026, 7, 4, 12, 0, 0, DateTimeKind.Utc);

    private FakeTimeProvider _time;

    private TicketStore _ticketStore;
    private SessionRegistry _sessionRegistry;
    private ConnectionMapping _connectionMapping;
    private FocusRegistry _focusRegistry;
    private OnlineMemberRegistry _onlineMemberRegistry;
    private MessageRateLimiter _messageRateLimiter;
    private ChannelCreationRateLimiter _channelCreationRateLimiter;
    private FanOutEngine _fanOutEngine;
    private CountingUserDirectoryRepository _userDirectory;
    private MuteReconciliationTestHarness _reconcileHarness;
    private ChannelRepository _channelRepository;
    private MembershipRepository _membershipRepository;
    private MessageRepository _messageRepository;
    private SessionStateAssembler _assembler;
    private Mock<IChatAuthenticationService> _authService;

    private FakeRelationshipSource _relationshipSource;
    private RelationshipProvider _relationshipProvider;

    // Per-tag friends, read by the fake source's snapshot factory (OrdinalIgnoreCase) — mirrors
    // ChatHubOpenDmTests. Blocked is always empty; presence reads never consult it.
    private readonly Dictionary<string, HashSet<string>> _friends = new(StringComparer.OrdinalIgnoreCase);

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    [SetUp]
    public void SetupBeforeEach()
    {
        _friends.Clear();
        _time = new FakeTimeProvider(new DateTimeOffset(T0, TimeSpan.Zero));

        _ticketStore = new TicketStore();
        _sessionRegistry = new SessionRegistry();
        _connectionMapping = new ConnectionMapping();
        _focusRegistry = new FocusRegistry();
        _onlineMemberRegistry = new OnlineMemberRegistry();
        _messageRateLimiter = new MessageRateLimiter();
        _channelCreationRateLimiter = new ChannelCreationRateLimiter();
        _fanOutEngine = FanOutEngineTestFactory.CreateIgnored();
        _userDirectory = new CountingUserDirectoryRepository(MongoClient);
        _reconcileHarness = new MuteReconciliationTestHarness(_connectionMapping, new MuteRepository(MongoClient));
        _channelRepository = new ChannelRepository(MongoClient);
        _membershipRepository = new MembershipRepository(MongoClient, _channelRepository);
        _messageRepository = new MessageRepository(MongoClient);

        _relationshipSource = new FakeRelationshipSource((tag, now) => new RelationshipSnapshot(
            tag,
            _friends.TryGetValue(tag, out var f) ? f : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            now));
        // SINGLETON-like: one provider instance shared across every hub built in a test, mirroring the
        // real DI lifetime (Startup registers IRelationshipProvider as a singleton).
        _relationshipProvider = new RelationshipProvider(_relationshipSource, _time);

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

    // ---- fixture plumbing (mirrors HubProtocolIntegrationTests / ChatHubPresenceTests) --------------

    private static W3CUserAuthentication Identity(string battleTag) =>
        new() { BattleTag = battleTag, Name = battleTag.Split('#')[0] };

    private void SetTime(int addSeconds) =>
        _time.SetUtcNow(new DateTimeOffset(T0.AddSeconds(addSeconds), TimeSpan.Zero));

    private ChatHub BuildHub(string connectionId, string accessToken = null)
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
            _relationshipProvider,
            new UserSettingsRepository(MongoClient),
            new DmInitiationTracker(),
            _authService.Object,
            MentionFanOutTestFactory.CreateIgnored(MongoClient),
            new PresenceInterestRegistry(),
            new MentionInboxRepository(MongoClient));

        var clients = new Mock<IHubCallerClients>();
        clients.Setup(c => c.Caller).Returns(new Mock<ISingleClientProxy>().Object);
        clients.Setup(c => c.Client(It.IsAny<string>())).Returns(new Mock<ISingleClientProxy>().Object);
        hub.Clients = clients.Object;

        var context = new Mock<HubCallerContext>();
        context.Setup(c => c.ConnectionId).Returns(connectionId);
        context.Setup(c => c.Features).Returns(BuildFeatures(accessToken));
        hub.Context = context.Object;
        hub.Groups = new Mock<IGroupManager>().Object;

        return hub;
    }

    // Mints a fresh one-time ticket (real wall-clock, matching OnConnectedAsync's DateTime.UtcNow
    // consumption) and drives the SHIPPED connect path end-to-end — including the Task 3 connect-time
    // directory upsert.
    private async Task<ChatHub> Connect(string connectionId, string battleTag)
    {
        var ticket = _ticketStore.Mint(Identity(battleTag), DateTime.UtcNow);
        var hub = BuildHub(connectionId, ticket);
        await hub.OnConnectedAsync();
        return hub;
    }

    private static IFeatureCollection BuildFeatures(string accessToken)
    {
        var features = new FeatureCollection();
        var httpContext = new DefaultHttpContext();
        if (accessToken != null)
        {
            httpContext.Request.QueryString = new QueryString($"?access_token={accessToken}");
        }
        var httpContextFeature = new Mock<IHttpContextFeature>();
        httpContextFeature.Setup(f => f.HttpContext).Returns(httpContext);
        features.Set<IHttpContextFeature>(httpContextFeature.Object);
        return features;
    }

    // ================================================================================================
    // GetPresence — ungated online/offline batch read.
    // ================================================================================================

    [Test]
    public async Task GetPresence_MixedOnlineOffline_CorrectFlags_CaseInsensitive()
    {
        const string CallerTag = "Caller#1";
        const string OnlineTag = "Online#2";
        const string OfflineTag = "Offline#3";

        var caller = await Connect("conn-caller", CallerTag);
        await Connect("conn-online", OnlineTag);
        // OfflineTag deliberately never connects.

        // Queried with DIFFERENT casing than each tag actually connected under.
        var queriedOnline = OnlineTag.ToUpperInvariant();
        var queriedOffline = OfflineTag.ToLowerInvariant();
        var queriedCaller = CallerTag.ToUpperInvariant();
        var result = await caller.GetPresence(new[] { queriedOnline, queriedOffline, queriedCaller });

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        var byTag = result.Statuses.ToDictionary(s => s.BattleTag, StringComparer.OrdinalIgnoreCase);
        Assert.That(byTag[queriedOnline].Online, Is.True, "an online user must resolve True regardless of query casing");
        Assert.That(byTag[queriedOffline].Online, Is.False, "a never-connected user must resolve False");
        Assert.That(byTag[queriedCaller].Online, Is.True, "the caller itself has a live session and must show online");
    }

    [Test]
    public async Task GetPresence_NullArray_HubException()
    {
        var caller = await Connect("conn-caller", "Caller#1");

        Assert.ThrowsAsync<HubException>(async () => await caller.GetPresence(null));
    }

    [Test]
    public async Task GetPresence_OverCap_HubException()
    {
        var caller = await Connect("conn-caller", "Caller#1");
        var tooMany = Enumerable.Range(0, ChatLimits.PresenceQueryMaxBattleTags + 1)
            .Select(i => $"tag{i}#1")
            .ToArray();

        Assert.ThrowsAsync<HubException>(async () => await caller.GetPresence(tooMany));
    }

    [Test]
    public async Task GetPresence_Empty_OkEmpty()
    {
        var caller = await Connect("conn-caller", "Caller#1");

        var result = await caller.GetPresence(Array.Empty<string>());

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(result.Statuses, Is.Empty);
    }

    [Test]
    public async Task GetPresence_FailClosedSession_PermissionDenied()
    {
        var hub = BuildHub("conn-ghost"); // never connected — no live session

        var result = await hub.GetPresence(new[] { "someone#1" });

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.PermissionDenied));
    }

    [Test]
    public void GetPresence_FailClosedSession_PrecedesArgValidation_EvenForNullArray()
    {
        // Review-note follow-up: proves the session check strictly precedes the malformed-arg guards —
        // a ghost connection with a NULL array must still get PermissionDenied, never a HubException.
        var hub = BuildHub("conn-ghost");

        GetPresenceResult result = null;
        Assert.DoesNotThrowAsync(async () => result = await hub.GetPresence(null));

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.PermissionDenied));
    }

    [Test]
    public async Task GetPresence_DuplicateAndBlankEntries_HandledGracefully()
    {
        const string CallerTag = "caller#1";
        const string OnlineTag = "online#2";

        var caller = await Connect("conn-caller", CallerTag);
        await Connect("conn-online", OnlineTag);

        // A duplicate tag and a blank/whitespace entry must never crash the read.
        var result = await caller.GetPresence(new[] { OnlineTag, OnlineTag, string.Empty, "   " });

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(result.Statuses, Has.Count.EqualTo(4), "one row per requested entry, duplicates included");
        Assert.That(result.Statuses.Count(s => s.BattleTag == OnlineTag && s.Online), Is.EqualTo(2),
            "a duplicated tag resolves independently (and correctly) for each occurrence");
        Assert.That(result.Statuses.Where(s => string.IsNullOrWhiteSpace(s.BattleTag)).Select(s => s.Online),
            Is.All.False, "a blank/whitespace entry must degrade to offline rather than throwing");
    }

    // ================================================================================================
    // GetPresenceDetails — friend-gated LastSeenAt (D12, acceptance 6).
    // ================================================================================================

    [Test]
    public async Task GetPresenceDetails_Friend_CarriesLastSeenAt_FromDisconnectUpsert()
    {
        const string CallerTag = "caller#1";
        const string FriendTag = "friend#2";
        _friends[CallerTag] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { FriendTag };

        var friend = await Connect("conn-friend", FriendTag);
        SetTime(60); // distinguish the disconnect instant from the connect instant
        var disconnectInstant = Now;
        await friend.OnDisconnectedAsync(null);

        var caller = await Connect("conn-caller", CallerTag);
        var result = await caller.GetPresenceDetails(new[] { FriendTag });

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        var dto = result.Details.Single();
        Assert.That(dto.Online, Is.False, "the friend disconnected and must show offline");
        Assert.That(dto.LastSeenAt, Is.EqualTo(disconnectInstant),
            "LastSeenAt must equal the DISCONNECT instant (Task 3's SetLastSeen wiring), not the earlier connect instant");
    }

    [Test]
    public async Task GetPresenceDetails_OnlineFriend_OnlineTrue_LastSeenAtPresent()
    {
        const string CallerTag = "caller#1";
        const string FriendTag = "friend#2";
        _friends[CallerTag] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { FriendTag };

        var friend = await Connect("conn-friend", FriendTag);
        _ = friend; // still connected — never disconnects in this test
        var connectInstant = Now;

        var caller = await Connect("conn-caller", CallerTag);
        var result = await caller.GetPresenceDetails(new[] { FriendTag });

        var dto = result.Details.Single();
        Assert.That(dto.Online, Is.True);
        Assert.That(dto.LastSeenAt, Is.EqualTo(connectInstant),
            "an online friend's LastSeenAt reflects the connect-time directory upsert");
    }

    [Test]
    public async Task GetPresenceDetails_NonFriend_LastSeenAtNull_OnlineFlagStillHonest()
    {
        const string CallerTag = "caller#1";
        const string StrangerTag = "stranger#2";
        // _friends[CallerTag] intentionally left unset — the stranger is NOT a friend.

        var stranger = await Connect("conn-stranger", StrangerTag);
        SetTime(60);
        await stranger.OnDisconnectedAsync(null); // a REAL LastSeenAt now exists in the directory

        var caller = await Connect("conn-caller", CallerTag);
        var offlineResult = await caller.GetPresenceDetails(new[] { StrangerTag });
        var offlineDto = offlineResult.Details.Single();
        Assert.That(offlineDto.Online, Is.False, "online must still be honestly reported for a non-friend");
        Assert.That(offlineDto.LastSeenAt, Is.Null,
            "a non-friend's LastSeenAt must come back null even though a REAL value exists in the directory");

        // Prove the online flag stays honest even while the (still non-friend) stranger IS online.
        await Connect("conn-stranger-2", StrangerTag);
        var onlineResult = await caller.GetPresenceDetails(new[] { StrangerTag });
        var onlineDto = onlineResult.Details.Single();
        Assert.That(onlineDto.Online, Is.True, "online must be honestly reported even for a non-friend");
        Assert.That(onlineDto.LastSeenAt, Is.Null, "LastSeenAt stays suppressed for a non-friend regardless of online status");
    }

    [Test]
    public async Task GetPresenceDetails_SnapshotUnavailable_AllLastSeenAtNull()
    {
        const string CallerTag = "caller#1";
        const string FriendOfflineTag = "friendoffline#2";
        const string FriendOnlineTag = "friendonline#3";
        _friends[CallerTag] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            FriendOfflineTag, FriendOnlineTag,
        };

        // Populate REAL directory data + live online state for BOTH targets — if the caller's snapshot
        // were available, both would be eligible (actual friends) with real LastSeenAt values.
        var offlineFriend = await Connect("conn-friend-offline", FriendOfflineTag);
        SetTime(30);
        await offlineFriend.OnDisconnectedAsync(null);
        await Connect("conn-friend-online", FriendOnlineTag);

        // The relationship source now fails EVERY fetch — no snapshot can ever be cached for the caller
        // (including via the connect-time fire-and-forget prefetch, which fails silently/non-fatally).
        _relationshipSource.ShouldThrow = true;

        var caller = await Connect("conn-caller", CallerTag);
        var result = await caller.GetPresenceDetails(new[] { FriendOfflineTag, FriendOnlineTag });

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        var byTag = result.Details.ToDictionary(d => d.BattleTag, StringComparer.OrdinalIgnoreCase);
        Assert.That(byTag[FriendOfflineTag].Online, Is.False);
        Assert.That(byTag[FriendOfflineTag].LastSeenAt, Is.Null,
            "an unavailable snapshot must fail LastSeenAt closed even for an ACTUAL friend with real directory data");
        Assert.That(byTag[FriendOnlineTag].Online, Is.True,
            "Online must still be honestly reported even when the relationship snapshot is unavailable");
        Assert.That(byTag[FriendOnlineTag].LastSeenAt, Is.Null, "fails closed regardless of online status");
    }

    [Test]
    public async Task GetPresenceDetails_NullArray_HubException()
    {
        var caller = await Connect("conn-caller", "Caller#1");

        Assert.ThrowsAsync<HubException>(async () => await caller.GetPresenceDetails(null));
    }

    [Test]
    public async Task GetPresenceDetails_OverCap_HubException()
    {
        var caller = await Connect("conn-caller", "Caller#1");
        var tooMany = Enumerable.Range(0, ChatLimits.PresenceQueryMaxBattleTags + 1)
            .Select(i => $"tag{i}#1")
            .ToArray();

        Assert.ThrowsAsync<HubException>(async () => await caller.GetPresenceDetails(tooMany));
    }

    [Test]
    public async Task GetPresenceDetails_Empty_OkEmpty()
    {
        var caller = await Connect("conn-caller", "Caller#1");

        var result = await caller.GetPresenceDetails(Array.Empty<string>());

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(result.Details, Is.Empty);
    }

    [Test]
    public async Task GetPresenceDetails_FailClosedSession_PermissionDenied()
    {
        var hub = BuildHub("conn-ghost"); // never connected — no live session

        var result = await hub.GetPresenceDetails(new[] { "someone#1" });

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.PermissionDenied));
    }

    [Test]
    public void GetPresenceDetails_FailClosedSession_PrecedesArgValidation_EvenForNullArray()
    {
        // Review-note follow-up: proves the session check strictly precedes the malformed-arg guards —
        // a ghost connection with a NULL array must still get PermissionDenied, never a HubException.
        var hub = BuildHub("conn-ghost");

        GetPresenceDetailsResult result = null;
        Assert.DoesNotThrowAsync(async () => result = await hub.GetPresenceDetails(null));

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.PermissionDenied));
    }

    [Test]
    public async Task GetPresenceDetails_DuplicateAndBlankEntries_HandledGracefully()
    {
        const string CallerTag = "caller#1";
        const string FriendTag = "friend#2";
        _friends[CallerTag] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { FriendTag };

        var friend = await Connect("conn-friend", FriendTag);
        _ = friend;
        var connectInstant = Now;
        var caller = await Connect("conn-caller", CallerTag);

        // A duplicate friend tag and a blank/whitespace entry must never crash the read.
        var result = await caller.GetPresenceDetails(new[] { FriendTag, FriendTag, string.Empty, "  " });

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(result.Details, Has.Count.EqualTo(4), "one row per requested entry, duplicates included");
        Assert.That(
            result.Details.Where(d => d.BattleTag == FriendTag),
            Has.All.Matches<PresenceDetailsDto>(d => d.Online && d.LastSeenAt == connectInstant),
            "a duplicated friend tag resolves independently (and correctly) for each occurrence");
        Assert.That(
            result.Details.Where(d => string.IsNullOrWhiteSpace(d.BattleTag)),
            Has.All.Matches<PresenceDetailsDto>(d => !d.Online && d.LastSeenAt == null),
            "a blank/whitespace entry must degrade to offline + null LastSeenAt rather than throwing");
    }

    // ================================================================================================
    // No write path — neither method may ever touch the directory (D12/D14 boundary).
    // ================================================================================================

    [Test]
    public async Task NeitherMethod_EverWritesToTheDirectory()
    {
        const string CallerTag = "caller#1";
        const string FriendTag = "friend#2";
        _friends[CallerTag] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { FriendTag };

        var friend = await Connect("conn-friend", FriendTag);
        await friend.OnDisconnectedAsync(null);
        var caller = await Connect("conn-caller", CallerTag);

        // Connect/disconnect above legitimately wrote to the directory (Task 3) — reset the counters
        // to isolate exactly what GetPresence/GetPresenceDetails themselves do.
        var upsertBefore = _userDirectory.UpsertCallCount;
        var setLastSeenBefore = _userDirectory.SetLastSeenCallCount;

        await caller.GetPresence(new[] { FriendTag, CallerTag });
        await caller.GetPresenceDetails(new[] { FriendTag, CallerTag });

        Assert.That(_userDirectory.UpsertCallCount, Is.EqualTo(upsertBefore),
            "GetPresence/GetPresenceDetails must never call UserDirectoryRepository.Upsert");
        Assert.That(_userDirectory.SetLastSeenCallCount, Is.EqualTo(setLastSeenBefore),
            "GetPresence/GetPresenceDetails must never call UserDirectoryRepository.SetLastSeen");
    }
}
