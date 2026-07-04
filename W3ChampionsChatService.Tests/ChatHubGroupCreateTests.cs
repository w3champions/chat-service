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
/// C5 Task 7: <c>CreateGroup(name, members)</c> — group creation end-to-end (create-from-scratch only,
/// friends-only initial members, size bounds [3,100], the SHARED 5/hour creation throttle with implicit
/// semiPublic creation (D13), and no-auto-open pushes). Direct-hub idiom mirroring
/// <see cref="ChatHubOpenDmTests"/>/<see cref="ChatHubDmSendTests"/>: a real <see cref="RelationshipProvider"/>
/// over a <see cref="FakeRelationshipSource"/> (NEVER HTTP) gives per-tag control of friends, a
/// <see cref="FakeTimeProvider"/> drives time, and a REAL <see cref="FanOutEngine"/> wired to a
/// <see cref="HubPushCaptureHarness"/> captures ChannelAdded/MessageReceived. NUnit constraint style.
/// </summary>
public class ChatHubGroupCreateTests : IntegrationTestBase
{
    private const string Creator = "peter#123";
    private const string CreatorConn = "conn-creator";

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
    private ActivityCoalescer _coalescer;
    private HubPushCaptureHarness _harness;
    private UserSettingsRepository _userSettings;
    private DmInitiationTracker _dmInitiationTracker;
    private FakeRelationshipSource _relationshipSource;
    private RelationshipProvider _relationshipProvider;
    private FakeTimeProvider _time;
    private Mock<IChatAuthenticationService> _authService;

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
        _userSettings = new UserSettingsRepository(MongoClient);
        _dmInitiationTracker = new DmInitiationTracker();

        // A REAL FanOutEngine sharing the hub's registries + a shared SessionRegistry, wired to a
        // capture harness so ChannelAdded/MessageReceived pushes are observable.
        _harness = new HubPushCaptureHarness();
        _coalescer = new ActivityCoalescer(_harness.HubContext, _onlineMemberRegistry);
        _fanOutEngine = new FanOutEngine(_harness.HubContext, _focusRegistry, _onlineMemberRegistry, _coalescer, _sessionRegistry);

        _relationshipSource = new FakeRelationshipSource((tag, now) => new RelationshipSnapshot(
            tag,
            _friends.TryGetValue(tag, out var f) ? f : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            _blocked.TryGetValue(tag, out var b) ? b : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            now));
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
            _dmInitiationTracker,
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
        hub.Context = context.Object;
        hub.Groups = new Mock<IGroupManager>().Object;
        return hub;
    }

    private void RegisterSession(string connectionId, string battleTag) =>
        _sessionRegistry.Register(
            connectionId,
            new W3CUserAuthentication { BattleTag = battleTag, Name = battleTag.Split('#')[0] },
            null);

    private void SetFriends(string battleTag, params string[] friends) =>
        _friends[battleTag] = new HashSet<string>(friends, StringComparer.OrdinalIgnoreCase);

    private void SetBlocked(string battleTag, params string[] blocked) =>
        _blocked[battleTag] = new HashSet<string>(blocked, StringComparer.OrdinalIgnoreCase);

    private static string[] DistinctFriendTags(int count) =>
        Enumerable.Range(0, count).Select(i => $"friend{i}#{i}").ToArray();

    private ChatHub CreatorHub()
    {
        RegisterSession(CreatorConn, Creator);
        return BuildHub(CreatorConn);
    }

    // ------------------------------------------------------------------------------------------------
    // Happy path
    // ------------------------------------------------------------------------------------------------

    [Test]
    public async Task CreateGroup_HappyPath_CreatorOwner_MembersMember_AllLevelAll_Expiry1y()
    {
        SetFriends(Creator, "wolf#456", "fox#789");
        var hub = CreatorHub();

        var result = await hub.CreateGroup("Squad", new[] { "wolf#456", "fox#789" });

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(result.Channel.Type, Is.EqualTo(ChannelType.GroupDm));
        Assert.That(result.Channel.Name, Is.EqualTo("Squad"));
        Assert.That(result.Channel.NormalizedName, Is.Null, "D16: a group's NormalizedName is never set");
        Assert.That(result.Channel.LastSeq, Is.EqualTo(0L));
        Assert.That((result.Channel.ExpiresAt.Value - Now.AddDays(365)).Duration(), Is.LessThan(TimeSpan.FromSeconds(1)),
            "a fresh group shell gets the +1y expiry");
        Assert.That(result.Membership.BattleTag, Is.EqualTo(Creator), "CreateGroup returns the CALLER's own membership");
        Assert.That(result.Membership.Role, Is.EqualTo(MembershipRole.Owner));
        Assert.That(result.Membership.NotificationLevel, Is.EqualTo(NotificationLevel.All));

        var members = await _membershipRepository.LoadForChannel(result.Channel.Id);
        Assert.That(members.Select(m => m.BattleTag), Is.EquivalentTo(new[] { Creator, "wolf#456", "fox#789" }));
        var creatorRow = members.Single(m => string.Equals(m.BattleTag, Creator, StringComparison.OrdinalIgnoreCase));
        Assert.That(creatorRow.Role, Is.EqualTo(MembershipRole.Owner));
        foreach (var memberTag in new[] { "wolf#456", "fox#789" })
        {
            var row = members.Single(m => string.Equals(m.BattleTag, memberTag, StringComparison.OrdinalIgnoreCase));
            Assert.That(row.Role, Is.EqualTo(MembershipRole.Member), $"{memberTag} joins as an ordinary Member");
            Assert.That(row.NotificationLevel, Is.EqualTo(NotificationLevel.All));
            Assert.That(row.JoinedAt, Is.EqualTo(Now));
        }

        // T5 caution: the caller's OWN registry entry is seeded (via PushChannelAdded, GroupDm type) —
        // EnsureCallerMembership (hardcoded Dm) must never be reused for groups.
        Assert.That(_onlineMemberRegistry.IsMember(CreatorConn, result.Channel.Id), Is.True,
            "the creator's registry is seeded with a GroupDm entry");
    }

    // ------------------------------------------------------------------------------------------------
    // Size bounds [3, 100]
    // ------------------------------------------------------------------------------------------------

    [Test]
    public async Task CreateGroup_TwoTotal_Rejected()
    {
        SetFriends(Creator, "wolf#456");
        var hub = CreatorHub();

        var result = await hub.CreateGroup("Squad", new[] { "wolf#456" });

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.PermissionDenied), "creator + 1 member = 2 total, below GroupMinSize");
        Assert.That(await _channelRepository.LoadAllOfType(ChannelType.GroupDm), Is.Empty, "nothing is persisted on a size reject");
    }

    [Test]
    public async Task CreateGroup_101Total_Rejected()
    {
        var tags = DistinctFriendTags(100);
        SetFriends(Creator, tags);
        var hub = CreatorHub();

        var result = await hub.CreateGroup("Huge", tags);

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.PermissionDenied), "creator + 100 members = 101 total, above MaxGroupSize");
        Assert.That(await _channelRepository.LoadAllOfType(ChannelType.GroupDm), Is.Empty);
    }

    [Test]
    public async Task CreateGroup_Exactly100_Ok()
    {
        var tags = DistinctFriendTags(99);
        SetFriends(Creator, tags);
        var hub = CreatorHub();

        var result = await hub.CreateGroup("Full", tags);

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok), "creator + 99 members = 100 total, exactly at MaxGroupSize");
        var members = await _membershipRepository.LoadForChannel(result.Channel.Id);
        Assert.That(members, Has.Count.EqualTo(100));
    }

    [Test]
    public async Task CreateGroup_Exactly3_Ok()
    {
        SetFriends(Creator, "wolf#456", "fox#789");
        var hub = CreatorHub();

        var result = await hub.CreateGroup("Trio", new[] { "wolf#456", "fox#789" });

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok), "creator + 2 members = 3 total, exactly at GroupMinSize");
        var members = await _membershipRepository.LoadForChannel(result.Channel.Id);
        Assert.That(members, Has.Count.EqualTo(3));
    }

    // ------------------------------------------------------------------------------------------------
    // Friends gate
    // ------------------------------------------------------------------------------------------------

    [Test]
    public async Task CreateGroup_NonFriendMember_PermissionDenied_NothingPersisted()
    {
        SetFriends(Creator, "wolf#456"); // "fox#789" is deliberately NOT a friend
        var hub = CreatorHub();

        var result = await hub.CreateGroup("Squad", new[] { "wolf#456", "fox#789" });

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.PermissionDenied), "every member must be a friend of the creator");
        Assert.That(await _channelRepository.LoadAllOfType(ChannelType.GroupDm), Is.Empty, "no channel persisted on a friends-gate reject");
        Assert.That(await _membershipRepository.LoadForUser(Creator), Is.Empty, "no membership persisted either");
    }

    [Test]
    public async Task CreateGroup_WbUnavailable_ThrottledRetriable_NothingPersisted()
    {
        SetFriends(Creator, "wolf#456", "fox#789");
        _relationshipSource.ShouldThrow = true; // no cache warmed => GetSnapshotAsync fails closed
        var hub = CreatorHub();

        var result = await hub.CreateGroup("Squad", new[] { "wolf#456", "fox#789" });

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Throttled), "the friends-gate fails closed (retriable) when the relationship view is unavailable");
        Assert.That(result.RetryAfterSeconds, Is.EqualTo(ChatLimits.RelationshipRetryAfterSeconds));
        Assert.That(await _channelRepository.LoadAllOfType(ChannelType.GroupDm), Is.Empty);
    }

    [Test]
    public async Task CreateGroup_StaleSnapshot_ThrottledRetriable_NothingPersisted()
    {
        SetFriends(Creator, "wolf#456", "fox#789");
        // Warm a FRESH snapshot for the caller (members are genuinely friends) so the provider's cache is
        // populated, then take the source down and advance past RelationshipCacheTtl. The provider's own
        // refresh attempt (tier 2) fails and it falls back to the STALE last-known snapshot (tier 3, spec
        // §14) rather than throwing — so this exercises CreateGroup's OWN stricter freshness check
        // (`!snapshot.IsFresh(now)`), distinct from the fully-unavailable/no-cache case covered by
        // CreateGroup_WbUnavailable_ThrottledRetriable_NothingPersisted above.
        await _relationshipProvider.GetSnapshotAsync(Creator);
        _relationshipSource.ShouldThrow = true;
        _time.Advance(ChatLimits.RelationshipCacheTtl + TimeSpan.FromMinutes(1));
        var hub = CreatorHub();

        var result = await hub.CreateGroup("Squad", new[] { "wolf#456", "fox#789" });

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Throttled), "a STALE relationship snapshot fails closed (retriable) — the group friends-gate requires freshness, unlike the 1:1 delivery block-check");
        Assert.That(result.RetryAfterSeconds, Is.EqualTo(ChatLimits.RelationshipRetryAfterSeconds));
        Assert.That(await _channelRepository.LoadAllOfType(ChannelType.GroupDm), Is.Empty, "no channel persisted on a stale-snapshot reject");
        Assert.That(await _membershipRepository.LoadForUser(Creator), Is.Empty, "no membership persisted either");
    }

    // ------------------------------------------------------------------------------------------------
    // D13 — shared creation throttle
    // ------------------------------------------------------------------------------------------------

    [Test]
    public async Task CreateGroup_SharesCreationBudgetWithSemiPublic()
    {
        SetFriends(Creator, "wolf#456", "fox#789");
        var hub = CreatorHub();

        // Exhaust the shared 5/hour budget via implicit semiPublic creation (JoinChannel on brand-new names).
        for (var i = 0; i < ChatLimits.ChannelCreationPerHour; i++)
        {
            var joined = await hub.JoinChannel($"room-{i}");
            Assert.That(joined.Code, Is.EqualTo(ChatResultCode.Ok), $"semiPublic creation #{i + 1} is within the shared budget");
        }

        var result = await hub.CreateGroup("Squad", new[] { "wolf#456", "fox#789" });

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Throttled), "CreateGroup shares the SAME 5/hour budget as implicit semiPublic creation (D13)");
        Assert.That(result.RetryAfterSeconds, Is.GreaterThan(0));
        Assert.That(await _channelRepository.LoadAllOfType(ChannelType.GroupDm), Is.Empty, "the throttled call creates no group");
    }

    // ------------------------------------------------------------------------------------------------
    // No-auto-open pushes
    // ------------------------------------------------------------------------------------------------

    [Test]
    public async Task CreateGroup_PushesChannelAddedFocusFalse_ToOnlineMembers_OfflineMembersSeeItInSessionState()
    {
        const string onlineMember = "wolf#456";
        const string onlineMemberConn = "conn-wolf";
        const string offlineMember = "fox#789";
        SetFriends(Creator, onlineMember, offlineMember);
        RegisterSession(onlineMemberConn, onlineMember);
        var hub = CreatorHub();

        var result = await hub.CreateGroup("Squad", new[] { onlineMember, offlineMember });
        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));

        // The CALLER (creator) is pushed a ChannelAdded too (plan step 5: "for EACH member AND the caller").
        var creatorAdded = _harness.PayloadFor(CreatorConn, ChatEvents.ChannelAdded) as ChannelAddedDto;
        Assert.That(creatorAdded, Is.Not.Null, "the creator receives a ChannelAdded on group creation");
        Assert.That(creatorAdded.Focus, Is.False, "no-auto-open: ChannelAdded.Focus is always false");

        // The ONLINE member is pushed a ChannelAdded (focus:false), and their registry is seeded.
        var onlineAdded = _harness.PayloadFor(onlineMemberConn, ChatEvents.ChannelAdded) as ChannelAddedDto;
        Assert.That(onlineAdded, Is.Not.Null, "an online member receives a ChannelAdded push");
        Assert.That(onlineAdded.Focus, Is.False, "group adds never auto-open (no-auto-open pinned)");
        Assert.That(_onlineMemberRegistry.IsMember(onlineMemberConn, result.Channel.Id), Is.True);

        // The OFFLINE member gets nothing live (no session) — but the group is durably persisted and
        // shows up in their SessionState the next time they connect.
        Assert.That(_harness.AllSignals.Any(s => s.Method == ChatEvents.ChannelAdded && s.ConnectionId == "conn-fox"), Is.False);
        var offlineIdentity = new W3CUserAuthentication { BattleTag = offlineMember, Name = "fox" };
        var (dto, _) = await _assembler.AssembleAndSeed(offlineIdentity, "conn-fox-reconnect", Now,
            new ChatUser(offlineIdentity.BattleTag, offlineIdentity.IsAdmin, offlineIdentity.Name, new ProfilePicture(), null, null));
        Assert.That(dto.Channels.Select(c => c.Channel.Id), Does.Contain(result.Channel.Id),
            "the offline member sees the group in their SessionState.Channels on their next connect");
    }

    // ------------------------------------------------------------------------------------------------
    // D16 — group Name is never normalized
    // ------------------------------------------------------------------------------------------------

    [Test]
    public async Task CreateGroup_NameNeverNormalized_JoinChannelSameName_CreatesIndependentSemiPublic()
    {
        SetFriends(Creator, "wolf#456", "fox#789");
        var hub = CreatorHub();
        var group = await hub.CreateGroup("Foo", new[] { "wolf#456", "fox#789" });
        Assert.That(group.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(group.Channel.NormalizedName, Is.Null);

        // A DIFFERENT user joins a channel by the SAME display name — must resolve to an INDEPENDENT
        // semiPublic channel, never collide with the group (D16: NormalizedName is never set on GroupDm).
        const string otherUser = "otheruser#1";
        RegisterSession("conn-other", otherUser);
        var joinResult = await BuildHub("conn-other").JoinChannel("Foo");

        Assert.That(joinResult.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(joinResult.Channel.Type, Is.EqualTo(ChannelType.SemiPublic));
        Assert.That(joinResult.Channel.Id, Is.Not.EqualTo(group.Channel.Id), "JoinChannel creates an INDEPENDENT semiPublic channel, not a collision");
    }

    // ------------------------------------------------------------------------------------------------
    // De-dupe + creator self-entry
    // ------------------------------------------------------------------------------------------------

    [Test]
    public async Task CreateGroup_DuplicateAndCreatorEntriesInMembers_Deduped()
    {
        SetFriends(Creator, "wolf#456", "fox#789");
        var hub = CreatorHub();

        var members = new[] { "wolf#456", "Wolf#456", "fox#789", "PETER#123" }; // dup + creator's own tag, mixed case
        var result = await hub.CreateGroup("Squad", members);

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        var persisted = await _membershipRepository.LoadForChannel(result.Channel.Id);
        Assert.That(persisted, Has.Count.EqualTo(3), "creator + 2 distinct friends — duplicates and the creator's own entry are dropped");
        Assert.That(persisted.Select(m => m.BattleTag.ToLowerInvariant()), Is.EquivalentTo(new[] { Creator, "wolf#456", "fox#789" }));
    }

    // ------------------------------------------------------------------------------------------------
    // Argument / session guards
    // ------------------------------------------------------------------------------------------------

    [Test]
    public void CreateGroup_NullMembers_HubException()
    {
        var hub = CreatorHub();

        Assert.That(async () => await hub.CreateGroup("Squad", null), Throws.TypeOf<HubException>());
    }

    [Test]
    public async Task CreateGroup_EmptyName_TooLong()
    {
        SetFriends(Creator, "wolf#456", "fox#789");
        var hub = CreatorHub();

        var result = await hub.CreateGroup("   ", new[] { "wolf#456", "fox#789" });

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.TooLong));
    }

    [Test]
    public async Task CreateGroup_NameOverMaxLength_TooLong()
    {
        SetFriends(Creator, "wolf#456", "fox#789");
        var hub = CreatorHub();

        var result = await hub.CreateGroup(new string('a', ChatLimits.GroupNameMaxLength + 1), new[] { "wolf#456", "fox#789" });

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.TooLong));
    }

    [Test]
    public async Task CreateGroup_NoSession_FailClosed_PermissionDenied()
    {
        var hub = BuildHub("conn-ghost"); // no session registered

        var result = await hub.CreateGroup("Squad", new[] { "wolf#456", "fox#789" });

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.PermissionDenied));
    }

    // ------------------------------------------------------------------------------------------------
    // Acceptance 3 (group leg): a group message is delivered to ALL members, even one who blocked the sender
    // ------------------------------------------------------------------------------------------------

    [Test]
    public async Task GroupMessage_DeliveredToAllMembers_IncludingOnesWhoBlockedSender()
    {
        const string blocker = "wolf#456";
        const string blockerConn = "conn-wolf";
        const string other = "fox#789";
        const string otherConn = "conn-fox";
        SetFriends(Creator, blocker, other);
        // The blocker has a GENUINE 1:1 block against the sender (Creator) — a real relationship-service
        // block edge, not a hypothetical one. Group delivery must still reach them (block=non-delivery is
        // 1:1-only, per T4; the group send path never consults blocks at all — see ChatHub.Dm.cs
        // ApplyPrivateLaneGates, which short-circuits to null for GroupDm before any block/friend read).
        SetBlocked(blocker, Creator);
        RegisterSession(blockerConn, blocker);
        RegisterSession(otherConn, other);
        var hub = CreatorHub();

        var group = await hub.CreateGroup("Squad", new[] { blocker, other });
        Assert.That(group.Code, Is.EqualTo(ChatResultCode.Ok));

        // Both members focus the channel so the message is delivered as a full MessageReceived payload
        // (never mind that the blocker has blocked the sender — group delivery ignores blocks entirely,
        // in direct contrast to the 1:1 non-delivery pin in T4).
        Assert.That((await BuildHub(blockerConn).FocusChannel(group.Channel.Id)).Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That((await BuildHub(otherConn).FocusChannel(group.Channel.Id)).Code, Is.EqualTo(ChatResultCode.Ok));

        var send = await hub.SendMessage(group.Channel.Id, "team update");
        Assert.That(send.Code, Is.EqualTo(ChatResultCode.Ok));

        var blockerReceived = _harness.SignalsFor(blockerConn).Where(s => s.Method == ChatEvents.MessageReceived).ToList();
        Assert.That(blockerReceived, Has.Count.EqualTo(1), "the member who blocked the sender STILL receives the group message in full");
        var otherReceived = _harness.SignalsFor(otherConn).Where(s => s.Method == ChatEvents.MessageReceived).ToList();
        Assert.That(otherReceived, Has.Count.EqualTo(1));
    }
}
