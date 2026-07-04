using System;
using System.Collections.Generic;
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
using W3ChampionsChatService.Mentions;
using W3ChampionsChatService.Messages;
using W3ChampionsChatService.Mutes;
using W3ChampionsChatService.Protocol;
using W3ChampionsChatService.Relationships;
using W3ChampionsChatService.Sessions;
using W3ChampionsChatService.Users;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// C5 Task 3: <c>OpenDm(battleTag)</c> — the DM front door. Covers the consent-creation matrix (friend
/// born-Accepted / stranger dmPrivacy gate), the block-uniform observability pin (D5), the fail-closed
/// stranger-initiation cap (D7), the D14 directory guard, and pair-key concurrency. Direct-hub idiom
/// (mirrors <see cref="ChatHubSendMessageTests"/>); a real <see cref="RelationshipProvider"/> over a
/// <see cref="FakeRelationshipSource"/> (NEVER HTTP) gives per-tag control of friends/blocked/outage, and
/// a <see cref="FakeTimeProvider"/> — SHARED with the provider so snapshot freshness and the hub clock
/// agree — drives time. NUnit constraint style.
/// </summary>
public class ChatHubOpenDmTests : IntegrationTestBase
{
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
    private FakeRelationshipSource _relationshipSource;
    private RelationshipProvider _relationshipProvider;
    private FakeTimeProvider _time;

    // Per-tag friends/blocked, read by the fake source's snapshot factory (OrdinalIgnoreCase).
    private readonly Dictionary<string, HashSet<string>> _friends = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _blocked = new(StringComparer.OrdinalIgnoreCase);

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    [SetUp]
    public void SetupBeforeEach()
    {
        _friends.Clear();
        _blocked.Clear();
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

        _relationshipSource = new FakeRelationshipSource((tag, now) => new RelationshipSnapshot(
            tag,
            _friends.TryGetValue(tag, out var f) ? f : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            _blocked.TryGetValue(tag, out var b) ? b : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            now));
        _relationshipProvider = new RelationshipProvider(_relationshipSource, _time);

        var authService = new Mock<IChatAuthenticationService>();
        authService.Setup(m => m.GetUserFromIdentity(It.IsAny<W3CUserAuthentication>()))
            .ReturnsAsync((W3CUserAuthentication id) =>
                new ChatUser(id.BattleTag, id.IsAdmin, id.Name, new ProfilePicture(), null, null));
        _assembler = new SessionStateAssembler(
            _membershipRepository,
            _channelRepository,
            _messageRepository,
            new MuteRepository(MongoClient),
            authService.Object,
            _onlineMemberRegistry,
            _connectionMapping);
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
            _relationshipProvider,
            _userSettings,
            _dmInitiationTracker);

        var clients = new Mock<IHubCallerClients>();
        clients.Setup(c => c.Caller).Returns(new Mock<ISingleClientProxy>().Object);
        clients.Setup(c => c.Client(It.IsAny<string>())).Returns(new Mock<ISingleClientProxy>().Object);
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

    private Task SeedDirectory(string battleTag) =>
        _userDirectory.Upsert(new UserDirectoryEntry { BattleTag = battleTag, LastSeenAt = Now });

    private Task SeedPrivacy(string battleTag, DmPrivacy privacy) =>
        _userSettings.Upsert(new UserSettings { BattleTag = battleTag, DmPrivacy = privacy });

    private void SetFriends(string battleTag, params string[] friends) =>
        _friends[battleTag] = new HashSet<string>(friends, StringComparer.OrdinalIgnoreCase);

    private void SetBlocked(string battleTag, params string[] blocked) =>
        _blocked[battleTag] = new HashSet<string>(blocked, StringComparer.OrdinalIgnoreCase);

    // ------------------------------------------------------------------------------------------------
    // Creation matrix
    // ------------------------------------------------------------------------------------------------

    [Test]
    public async Task OpenDm_Friend_BornAccepted_AnyPrivacy()
    {
        const string caller = "peter#123";
        const string target = "wolf#456";
        SetFriends(caller, target);
        // A friend bypasses consent — even a target whose dmPrivacy is Nobody yields an Accepted DM.
        await SeedPrivacy(target, DmPrivacy.Nobody);
        RegisterSession("conn-1", caller);
        var hub = BuildHub("conn-1");

        var result = await hub.OpenDm(target);

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(result.Channel.Type, Is.EqualTo(ChannelType.Dm));
        Assert.That(result.Channel.RequestState, Is.EqualTo(DmRequestState.Accepted), "friends' DMs are born Accepted");
        Assert.That(result.Channel.RequestInitiatedBy, Is.EqualTo(caller));
        Assert.That((result.Channel.ExpiresAt.Value - Now.AddDays(365)).Duration(), Is.LessThan(TimeSpan.FromSeconds(1)),
            "an accepted-at-birth shell gets the +1y expiry");
        Assert.That(result.Membership.BattleTag, Is.EqualTo(caller), "OpenDm returns the CALLER's own membership");
        Assert.That(result.Membership.Role, Is.EqualTo(MembershipRole.Member));
        Assert.That(result.Membership.NotificationLevel, Is.EqualTo(NotificationLevel.All));
        Assert.That(_onlineMemberRegistry.IsMember("conn-1", result.Channel.Id), Is.True, "the caller's registry is seeded");
        Assert.That(_dmInitiationTracker.CountActive(caller, Now), Is.EqualTo(0), "the friend path never records an initiation");
    }

    [Test]
    public async Task OpenDm_Stranger_Everyone_CreatesPending()
    {
        const string caller = "peter#123";
        const string target = "wolf#456";
        await SeedDirectory(target);
        await SeedPrivacy(target, DmPrivacy.Everyone);
        RegisterSession("conn-1", caller);
        var hub = BuildHub("conn-1");

        var result = await hub.OpenDm(target);

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(result.Channel.RequestState, Is.EqualTo(DmRequestState.Pending));
        Assert.That(result.Channel.RequestInitiatedBy, Is.EqualTo(caller));
        Assert.That((result.Channel.ExpiresAt.Value - Now.AddDays(30)).Duration(), Is.LessThan(TimeSpan.FromSeconds(1)),
            "a pending shell gets the +30d expiry");
        Assert.That(result.Membership.BattleTag, Is.EqualTo(caller));
        Assert.That(_dmInitiationTracker.CountActive(caller, Now), Is.EqualTo(1), "a NEW stranger shell records one initiation");

        // D4: no recipient membership is materialized at open time.
        var members = await _membershipRepository.LoadForChannel(result.Channel.Id);
        Assert.That(members.Select(m => m.BattleTag), Is.EquivalentTo(new[] { caller }),
            "only the caller's membership exists — the recipient's is materialized later (T4)");
    }

    [Test]
    public async Task OpenDm_Stranger_FriendsOnly_PermissionDenied()
    {
        const string caller = "peter#123";
        const string target = "wolf#456";
        await SeedDirectory(target);
        await SeedPrivacy(target, DmPrivacy.Friends);
        RegisterSession("conn-1", caller);
        var hub = BuildHub("conn-1");

        var result = await hub.OpenDm(target);

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.PermissionDenied));
        Assert.That(await _channelRepository.LoadByPairKey(caller, target), Is.Null, "no shell is created on a privacy reject");
        Assert.That(_dmInitiationTracker.CountActive(caller, Now), Is.EqualTo(0), "a privacy reject records no initiation");
    }

    [Test]
    public async Task OpenDm_Stranger_Nobody_PermissionDenied()
    {
        const string caller = "peter#123";
        const string target = "wolf#456";
        await SeedDirectory(target);
        await SeedPrivacy(target, DmPrivacy.Nobody);
        RegisterSession("conn-1", caller);
        var hub = BuildHub("conn-1");

        var result = await hub.OpenDm(target);

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.PermissionDenied));
        Assert.That(await _channelRepository.LoadByPairKey(caller, target), Is.Null);
    }

    [Test]
    public async Task OpenDm_ExistingConversation_ReturnsSameChannel_NoCapCheck_NoNewInitiationRecorded()
    {
        const string caller = "peter#123";
        const string target = "wolf#456";
        await SeedDirectory(target);
        await SeedPrivacy(target, DmPrivacy.Everyone);
        // A shell already exists (created by an earlier open); the tracker is SATURATED at the cap.
        var existing = await _channelRepository.FindOrCreateDm(caller, target, caller, DmRequestState.Pending, Now);
        for (var i = 0; i < ChatLimits.StrangerDmInitiationCap; i++)
        {
            _dmInitiationTracker.Record(caller, $"dummy{i}#0", Now);
        }
        RegisterSession("conn-1", caller);
        var hub = BuildHub("conn-1");

        var result = await hub.OpenDm(target);

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok), "an existing conversation skips the cap even when saturated");
        Assert.That(result.Channel.Id, Is.EqualTo(existing.Id), "the same channel is returned");
        Assert.That(_dmInitiationTracker.CountActive(caller, Now), Is.EqualTo(ChatLimits.StrangerDmInitiationCap),
            "re-opening an existing conversation records NO new initiation");
    }

    // ------------------------------------------------------------------------------------------------
    // D8 / OQ-6 — a later dmPrivacy tightening never retro-gates an EXISTING conversation. Re-opening an
    // already-created shell (pending OR accepted) short-circuits the directory + dmPrivacy + cap gates.
    // ------------------------------------------------------------------------------------------------

    [Test]
    public async Task OpenDm_ExistingAcceptedShell_NonFriend_TargetNowNobody_ReturnsOkSameChannel()
    {
        const string caller = "peter#123";
        const string target = "wolf#456";
        // A shell already exists between two non-friends and was ACCEPTED (consent granted earlier).
        await SeedDirectory(target);
        var existing = await _channelRepository.FindOrCreateDm(caller, target, caller, DmRequestState.Pending, Now);
        Assert.That(await _channelRepository.SetRequestAccepted(existing.Id, Now), Is.True, "the shell is flipped to Accepted");
        // The target LATER tightens dmPrivacy to Nobody. Re-opening the established lane must NOT retro-gate
        // (D8/OQ-6: accepted = "normal forever"; re-opening an existing shell is not a creation).
        await SeedPrivacy(target, DmPrivacy.Nobody);
        RegisterSession("conn-1", caller);
        var hub = BuildHub("conn-1");

        var result = await hub.OpenDm(target);

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok),
            "an existing accepted conversation re-opens even after the target tightens dmPrivacy to Nobody (D8/OQ-6)");
        Assert.That(result.Channel.Id, Is.EqualTo(existing.Id), "the same channel is returned");
        Assert.That(result.Channel.RequestState, Is.EqualTo(DmRequestState.Accepted));
        Assert.That(_dmInitiationTracker.CountActive(caller, Now), Is.EqualTo(0),
            "re-opening an existing conversation records NO new initiation");
    }

    [Test]
    public async Task OpenDm_ExistingPendingShell_TargetNowNobody_ReturnsOkSameChannel()
    {
        const string caller = "peter#123";
        const string target = "wolf#456";
        // A PENDING shell already exists (the initiator opened earlier while the target allowed Everyone).
        await SeedDirectory(target);
        var existing = await _channelRepository.FindOrCreateDm(caller, target, caller, DmRequestState.Pending, Now);
        // The target then tightens to Nobody. Re-opening the same pending lane never re-gates here (pending-
        // phase DELIVERY still re-checks dmPrivacy in the T4 send path — that gate is separate/unchanged).
        await SeedPrivacy(target, DmPrivacy.Nobody);
        RegisterSession("conn-1", caller);
        var hub = BuildHub("conn-1");

        var result = await hub.OpenDm(target);

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok),
            "re-opening an existing pending shell never re-gates on a later dmPrivacy tightening (D8/OQ-6)");
        Assert.That(result.Channel.Id, Is.EqualTo(existing.Id), "the same channel is returned");
        Assert.That(result.Channel.RequestState, Is.EqualTo(DmRequestState.Pending));
        Assert.That(_dmInitiationTracker.CountActive(caller, Now), Is.EqualTo(0),
            "re-opening records no new initiation");
    }

    // ------------------------------------------------------------------------------------------------
    // D5 — block-uniform observability
    // ------------------------------------------------------------------------------------------------

    [Test]
    public async Task OpenDm_BlockedByTarget_ResultIdenticalToUnblockedStranger()
    {
        // CONTROL: an ordinary stranger open (target does not block the caller).
        const string callerA = "alice#1";
        const string targetA = "wolf#456";
        await SeedDirectory(targetA);
        await SeedPrivacy(targetA, DmPrivacy.Everyone);
        RegisterSession("conn-a", callerA);
        var control = await BuildHub("conn-a").OpenDm(targetA);

        // BLOCKED: identical setup, except the target has BLOCKED the caller. OpenDm never consults the
        // block (it fetches only the CALLER's snapshot, never the target's), so the result is identical.
        const string callerB = "bob#2";
        const string targetB = "fox#789";
        await SeedDirectory(targetB);
        await SeedPrivacy(targetB, DmPrivacy.Everyone);
        SetBlocked(targetB, callerB);
        RegisterSession("conn-b", callerB);
        var blocked = await BuildHub("conn-b").OpenDm(targetB);

        // The observable result shape is byte-identical modulo the (caller, channelId) identities.
        Assert.That(blocked.Code, Is.EqualTo(control.Code));
        Assert.That(blocked.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(blocked.RetryAfterSeconds, Is.EqualTo(control.RetryAfterSeconds));
        Assert.That(blocked.Channel.Type, Is.EqualTo(control.Channel.Type));
        Assert.That(blocked.Channel.RequestState, Is.EqualTo(control.Channel.RequestState));
        Assert.That(blocked.Channel.RequestInitiatedBy, Is.EqualTo(callerB));
        Assert.That(control.Channel.RequestInitiatedBy, Is.EqualTo(callerA));
        Assert.That(blocked.Membership.Role, Is.EqualTo(control.Membership.Role));
        Assert.That(blocked.Membership.NotificationLevel, Is.EqualTo(control.Membership.NotificationLevel));
        Assert.That(blocked.Membership.BattleTag, Is.EqualTo(callerB), "OpenDm returns the caller's own membership even when blocked");
        // Both created a genuine shell and recorded one initiation — the block changed nothing.
        Assert.That(_dmInitiationTracker.CountActive(callerB, Now), Is.EqualTo(1));
        Assert.That(await _channelRepository.LoadByPairKey(callerB, targetB), Is.Not.Null);
    }

    // ------------------------------------------------------------------------------------------------
    // D7 — stranger-initiation cap
    // ------------------------------------------------------------------------------------------------

    [Test]
    public async Task OpenDm_TenthUnacceptedInitiationWithin8h_EleventhRejectedThrottled()
    {
        const string caller = "peter#123";
        RegisterSession("conn-1", caller);
        var hub = BuildHub("conn-1");

        // Ten distinct stranger targets, all reachable (directory + Everyone) — ten NEW shells.
        for (var i = 0; i < ChatLimits.StrangerDmInitiationCap; i++)
        {
            var target = $"stranger{i}#0";
            await SeedDirectory(target);
            await SeedPrivacy(target, DmPrivacy.Everyone);
            var ok = await hub.OpenDm(target);
            Assert.That(ok.Code, Is.EqualTo(ChatResultCode.Ok), $"initiation #{i + 1} is within the cap");
        }

        const string eleventh = "stranger-eleven#0";
        await SeedDirectory(eleventh);
        await SeedPrivacy(eleventh, DmPrivacy.Everyone);

        var rejected = await hub.OpenDm(eleventh);

        Assert.That(rejected.Code, Is.EqualTo(ChatResultCode.Throttled), "the 11th unaccepted initiation within 8h is throttled");
        Assert.That(rejected.RetryAfterSeconds, Is.GreaterThan(0), "a throttled initiation carries a positive retry-after");
        Assert.That(await _channelRepository.LoadByPairKey(caller, eleventh), Is.Null, "the throttled initiation creates no shell");
    }

    // ------------------------------------------------------------------------------------------------
    // D1 — fail-closed relationship policy
    // ------------------------------------------------------------------------------------------------

    [Test]
    public async Task OpenDm_NewStrangerInitiation_WbUnavailable_ThrottledRetriable()
    {
        const string caller = "peter#123";
        const string target = "wolf#456";
        await SeedDirectory(target);
        await SeedPrivacy(target, DmPrivacy.Everyone);
        _relationshipSource.ShouldThrow = true; // no cache warmed => GetSnapshotAsync fails closed
        RegisterSession("conn-1", caller);
        var hub = BuildHub("conn-1");

        var result = await hub.OpenDm(target);

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Throttled), "a stranger initiation fails closed (retriable) when the relationship view is unavailable");
        Assert.That(result.RetryAfterSeconds, Is.EqualTo(ChatLimits.RelationshipRetryAfterSeconds));
        Assert.That(await _channelRepository.LoadByPairKey(caller, target), Is.Null, "nothing is created on a fail-closed reject");
    }

    [Test]
    public async Task OpenDm_Friend_CachedSnapshot_ProceedsDuringOutage()
    {
        const string caller = "peter#123";
        const string target = "wolf#456";
        SetFriends(caller, target);

        // Warm the cache with a friend snapshot, then take the source down and let the snapshot go stale.
        await _relationshipProvider.GetSnapshotAsync(caller);
        _relationshipSource.ShouldThrow = true;
        _time.Advance(ChatLimits.RelationshipCacheTtl + TimeSpan.FromMinutes(1));

        RegisterSession("conn-1", caller);
        var hub = BuildHub("conn-1");

        var result = await hub.OpenDm(target);

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok),
            "a cached friend snapshot proceeds even during an outage (friend-cache hits win over the outage)");
        Assert.That(result.Channel.RequestState, Is.EqualTo(DmRequestState.Accepted));
    }

    // ------------------------------------------------------------------------------------------------
    // Pair-key concurrency
    // ------------------------------------------------------------------------------------------------

    [Test]
    public async Task OpenDm_ConcurrentFromBothSides_OneChannel_BothMemberships()
    {
        await ChatDomainIndexes.EnsureAllAsync(MongoClient);
        const string a = "alice#1";
        const string b = "bob#2";
        SetFriends(a, b);
        SetFriends(b, a);
        RegisterSession("conn-a", a);
        RegisterSession("conn-b", b);
        var hubA = BuildHub("conn-a");
        var hubB = BuildHub("conn-b");

        var results = await Task.WhenAll(hubA.OpenDm(b), hubB.OpenDm(a));

        Assert.That(results.Select(r => r.Code), Is.All.EqualTo(ChatResultCode.Ok));
        Assert.That(results.Select(r => r.Channel.Id).Distinct().Count(), Is.EqualTo(1), "both sides resolve to ONE channel");
        Assert.That((await _channelRepository.LoadAllOfType(ChannelType.Dm)).Count, Is.EqualTo(1));

        var members = await _membershipRepository.LoadForChannel(results[0].Channel.Id);
        Assert.That(members.Select(m => m.BattleTag), Is.EquivalentTo(new[] { a, b }),
            "each side creates its OWN membership — both memberships exist on the one channel");
    }

    // ------------------------------------------------------------------------------------------------
    // D14 + argument / session guards
    // ------------------------------------------------------------------------------------------------

    [Test]
    public async Task OpenDm_TargetNeverSeen_NotFound()
    {
        const string caller = "peter#123";
        const string target = "ghost#999"; // deliberately NOT in user_directory
        await SeedPrivacy(target, DmPrivacy.Everyone);
        RegisterSession("conn-1", caller);
        var hub = BuildHub("conn-1");

        var result = await hub.OpenDm(target);

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.NotFound), "a stranger target with no user_directory row is NotFound (D14)");
        Assert.That(await _channelRepository.LoadByPairKey(caller, target), Is.Null);
    }

    [Test]
    public async Task OpenDm_Self_PermissionDenied()
    {
        const string caller = "peter#123";
        RegisterSession("conn-1", caller);
        var hub = BuildHub("conn-1");

        Assert.That((await hub.OpenDm(caller)).Code, Is.EqualTo(ChatResultCode.PermissionDenied));
        Assert.That((await hub.OpenDm("PETER#123")).Code, Is.EqualTo(ChatResultCode.PermissionDenied),
            "self-DM is rejected case-insensitively");
    }

    [Test]
    public void OpenDm_NullOrEmpty_HubException()
    {
        RegisterSession("conn-1", "peter#123");
        var hub = BuildHub("conn-1");

        Assert.That(async () => await hub.OpenDm(null), Throws.TypeOf<HubException>());
        Assert.That(async () => await hub.OpenDm(""), Throws.TypeOf<HubException>());
        Assert.That(async () => await hub.OpenDm("   "), Throws.TypeOf<HubException>());
    }

    [Test]
    public async Task OpenDm_NoSession_FailClosed_PermissionDenied()
    {
        var hub = BuildHub("conn-ghost"); // no session registered

        var result = await hub.OpenDm("wolf#456");

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.PermissionDenied));
    }

    [Test]
    public async Task OpenDm_CreatesNoRecipientMembership_TargetSessionStateUnchanged()
    {
        const string caller = "peter#123";
        const string target = "wolf#456";
        await SeedDirectory(target);
        await SeedPrivacy(target, DmPrivacy.Everyone);
        // The target is ONLINE (has a live session + connection) but must remain oblivious pre-message.
        RegisterSession("conn-target", target);
        RegisterSession("conn-1", caller);
        var hub = BuildHub("conn-1");

        var result = await hub.OpenDm(target);
        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));

        Assert.That(await _membershipRepository.Load(result.Channel.Id, target), Is.Null,
            "no recipient membership row is created at open time (D4)");
        var members = await _membershipRepository.LoadForChannel(result.Channel.Id);
        Assert.That(members.Select(m => m.BattleTag), Is.EquivalentTo(new[] { caller }));
        Assert.That(_onlineMemberRegistry.IsMember("conn-target", result.Channel.Id), Is.False,
            "the target's in-memory session state is untouched — the opened-but-unmessaged DM is invisible to them");
    }
}
