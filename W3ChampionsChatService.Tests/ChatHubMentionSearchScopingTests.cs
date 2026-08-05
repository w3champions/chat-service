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
using W3ChampionsChatService.Mentions;
using W3ChampionsChatService.Messages;
using W3ChampionsChatService.Mutes;
using W3ChampionsChatService.Protocol;
using W3ChampionsChatService.Sessions;
using W3ChampionsChatService.Users;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// D1 (2026-08-05 follow-up, mention-canonicalization brief, Part 2): <c>SearchMentionCandidates</c>
/// (<c>ChatHub.Mentions.cs</c>) must never offer someone who isn't a legal mention target for the
/// channel. <see cref="ChatHubMentionSearchTests"/> already covers tier ordering, dedup, prefix
/// matching, the 90d gate, enrichment, the Dm/GroupDm private lane, and the result cap — this file is
/// scoped narrowly to the NEW SemiPublic/System member-scoping lane added by D1: candidate-side
/// filtering via <see cref="MembershipRepository.LoadMemberBattleTags"/>, deliberately NOT
/// <see cref="MembershipRepository.LoadForChannel"/> (which the Dm/GroupDm lane still uses safely,
/// since those channels are small and ACL-bound) — a SemiPublic/System room can be unboundedly large.
/// Direct-hub-instantiation idiom, mirroring <see cref="ChatHubMentionSearchTests"/> exactly.
/// </summary>
public class ChatHubMentionSearchScopingTests : IntegrationTestBase
{
    private static readonly DateTimeOffset FixedNow = new(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);

    private ConnectionMapping _connectionMapping;
    private UserDirectoryRepository _userDirectory;
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
    private ReadRateLimiter _readRateLimiter;
    private ChannelCreationRateLimiter _channelCreationRateLimiter;
    private SessionStateAssembler _assembler;
    private FanOutEngine _fanOutEngine;
    private MentionInboxRepository _mentionInboxRepository;
    private FakeTimeProvider _time;

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    [SetUp]
    public void SetupBeforeEach()
    {
        _time = new FakeTimeProvider(FixedNow);

        _connectionMapping = new ConnectionMapping();
        _userDirectory = new UserDirectoryRepository(MongoClient);
        _reconcileHarness = new MuteReconciliationTestHarness(_connectionMapping, new MuteRepository(MongoClient));
        _ticketStore = new TicketStore();

        _authService = new Mock<IChatAuthenticationService>();
        _authService.Setup(m => m.GetUserFromIdentity(It.IsAny<W3CUserAuthentication>()))
            .ReturnsAsync((W3CUserAuthentication id) =>
                new ChatUserResolution(new ChatUser(id.BattleTag, id.IsAdmin, id.Name, new ProfilePicture(), null, null), true));

        _channelRepository = new ChannelRepository(MongoClient);
        _membershipRepository = new MembershipRepository(MongoClient, _channelRepository);
        _messageRepository = new MessageRepository(MongoClient);
        _sessionRegistry = new SessionRegistry();
        _focusRegistry = new FocusRegistry();
        _onlineMemberRegistry = new OnlineMemberRegistry();
        _messageRateLimiter = new MessageRateLimiter();
        _readRateLimiter = new ReadRateLimiter();
        _channelCreationRateLimiter = new ChannelCreationRateLimiter();
        _fanOutEngine = FanOutEngineTestFactory.CreateIgnored();
        _mentionInboxRepository = new MentionInboxRepository(MongoClient);

        _assembler = new SessionStateAssembler(
            _membershipRepository,
            _channelRepository,
            _messageRepository,
            new MuteRepository(MongoClient),
            _onlineMemberRegistry,
            _connectionMapping,
            _mentionInboxRepository);
    }

    private ChatHub BuildHub(string connectionId, MembershipRepository membershipRepository = null)
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
            _readRateLimiter,
            _time,
            _channelRepository,
            membershipRepository ?? _membershipRepository,
            _channelCreationRateLimiter,
            _messageRepository,
            _fanOutEngine,
            ViewersAccumulatorTestFactory.CreateIgnored(),
            new NoOpMentionInboxCleaner(),
            RelationshipProviderTestFactory.CreateIgnored(),
            new UserSettingsRepository(MongoClient),
            new DmInitiationTracker(),
            _authService.Object,
            MentionFanOutTestFactory.CreateIgnored(MongoClient),
            new PresenceInterestRegistry(),
            _mentionInboxRepository,
            new NotificationPreferenceRepository(MongoClient));

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

    private void JoinChannel(string channelId, string connectionId, string battleTag, ChannelType type) =>
        _onlineMemberRegistry.Join(channelId, connectionId, new MemberState(battleTag, NotificationLevel.All, 0, type));

    private Task SeedDirectory(string battleTag, DateTime lastSeenAt) =>
        _userDirectory.Upsert(new UserDirectoryEntry
        {
            BattleTag = battleTag,
            DisplayBattleTag = battleTag,
            NormalizedName = battleTag.ToLowerInvariant(),
            LastSeenAt = lastSeenAt,
        });

    private Task SeedMembership(string channelId, string battleTag) =>
        _membershipRepository.Insert(new ChannelMembership
        {
            ChannelId = channelId,
            BattleTag = battleTag,
            Role = MembershipRole.Member,
            NotificationLevel = NotificationLevel.All,
            JoinedAt = Now,
        });

    // ---------------------------------------------------------------------------------------------
    // (i)/(j): SemiPublic and System — a non-member is NEVER offered, even though online AND
    // directory-fresh; an actual member IS still offered.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Search_SemiPublicChannel_NonMemberExcluded_ActualMemberOffered()
    {
        const string channelId = "semi-1";
        const string caller = "caller#1";
        const string buddy = "buddy#2";       // actual member, offline but recently active
        const string intruder = "intruder#3"; // NOT a member — online AND directory-fresh

        RegisterSession("conn-caller", caller);
        JoinChannel(channelId, "conn-caller", caller, ChannelType.SemiPublic);
        await SeedMembership(channelId, buddy);
        await SeedDirectory(buddy, Now.AddDays(-1));

        RegisterSession("conn-intruder", intruder); // tier-2-eligible if scoping didn't hold
        await SeedDirectory(intruder, Now.AddDays(-1)); // tier-3-eligible if scoping didn't hold

        var hub = BuildHub("conn-caller");
        var result = await hub.SearchMentionCandidates(channelId, "");

        var tags = result.Candidates.Select(c => c.BattleTag).ToList();
        Assert.That(tags, Does.Contain(buddy), "an actual SemiPublic member must still be offered");
        Assert.That(tags, Does.Not.Contain(intruder),
            "D1: a non-member must NEVER be offered inside a SemiPublic room, even though it is online and directory-fresh");
    }

    [Test]
    public async Task Search_SystemChannel_NonMemberExcluded_ActualMemberOffered()
    {
        const string channelId = "system-1";
        const string caller = "caller#1";
        const string buddy = "buddy#2";       // actual member, offline but recently active
        const string intruder = "intruder#3"; // NOT a member — online AND directory-fresh

        RegisterSession("conn-caller", caller);
        JoinChannel(channelId, "conn-caller", caller, ChannelType.System);
        await SeedMembership(channelId, buddy);
        await SeedDirectory(buddy, Now.AddDays(-1));

        RegisterSession("conn-intruder", intruder);
        await SeedDirectory(intruder, Now.AddDays(-1));

        var hub = BuildHub("conn-caller");
        var result = await hub.SearchMentionCandidates(channelId, "");

        var tags = result.Candidates.Select(c => c.BattleTag).ToList();
        Assert.That(tags, Does.Contain(buddy), "an actual System-channel member must still be offered");
        Assert.That(tags, Does.Not.Contain(intruder),
            "D1: a non-member must NEVER be offered inside a System channel (any SystemChannelKind), even though it is online and directory-fresh");
    }

    // ---------------------------------------------------------------------------------------------
    // (k): Public is UNCHANGED — still the universe-wide "mention anywhere" lane, no membership read.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Search_PublicChannel_NonMemberStillOffered_Unchanged()
    {
        const string channelId = "pub-1";
        const string caller = "caller#1";
        const string stranger = "stranger#2"; // online, but never a member of this Public channel

        RegisterSession("conn-caller", caller);
        JoinChannel(channelId, "conn-caller", caller, ChannelType.Public);
        RegisterSession("conn-stranger", stranger);

        var hub = BuildHub("conn-caller");
        var result = await hub.SearchMentionCandidates(channelId, "");

        Assert.That(result.Candidates.Select(c => c.BattleTag), Does.Contain(stranger),
            "D1: Public stays the universe-wide, unrestricted lane — a non-member online user is still offered");
    }

    // ---------------------------------------------------------------------------------------------
    // (m): big-room boundedness — the SemiPublic/System lane must never call LoadForChannel (the
    // full-room membership scan); it stays bounded to the already-capped candidate list via
    // LoadMemberBattleTags instead.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Search_SemiPublicChannel_NeverCallsLoadForChannel()
    {
        const string channelId = "semi-1";
        const string caller = "caller#1";
        const string buddy = "buddy#2";

        RegisterSession("conn-caller", caller);
        JoinChannel(channelId, "conn-caller", caller, ChannelType.SemiPublic);
        await SeedMembership(channelId, buddy);
        await SeedDirectory(buddy, Now.AddDays(-1));

        var countingMembership = new CountingMembershipRepository(MongoClient, _channelRepository);
        var hub = BuildHub("conn-caller", countingMembership);

        var result = await hub.SearchMentionCandidates(channelId, "");

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(result.Candidates.Select(c => c.BattleTag), Does.Contain(buddy), "the candidate-side check must still find the real member");
        Assert.That(countingMembership.LoadForChannelCallCount, Is.EqualTo(0),
            "D1: the SemiPublic/System lane must stay bounded to the candidate list via LoadMemberBattleTags — " +
            "it must NEVER fall back to LoadForChannel's full-room membership scan");
    }

    [Test]
    public async Task Search_SystemChannel_NeverCallsLoadForChannel()
    {
        const string channelId = "system-1";
        const string caller = "caller#1";
        const string buddy = "buddy#2";

        RegisterSession("conn-caller", caller);
        JoinChannel(channelId, "conn-caller", caller, ChannelType.System);
        await SeedMembership(channelId, buddy);
        await SeedDirectory(buddy, Now.AddDays(-1));

        var countingMembership = new CountingMembershipRepository(MongoClient, _channelRepository);
        var hub = BuildHub("conn-caller", countingMembership);

        var result = await hub.SearchMentionCandidates(channelId, "");

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(result.Candidates.Select(c => c.BattleTag), Does.Contain(buddy), "the candidate-side check must still find the real member");
        Assert.That(countingMembership.LoadForChannelCallCount, Is.EqualTo(0),
            "D1: the SemiPublic/System lane must stay bounded to the candidate list via LoadMemberBattleTags — " +
            "it must NEVER fall back to LoadForChannel's full-room membership scan");
    }

    // ---------------------------------------------------------------------------------------------
    // Tier 1 (live viewers) is exempt from the candidate-side re-check by construction (see the class
    // doc on ChatHub.Mentions.cs's SearchMentionCandidates): reaching tier 1 requires a successful
    // FocusChannel call, which itself requires OnlineMemberRegistry membership — always seeded from a
    // durable row. This proves an ACTUAL member who is currently focused (tier 1) is still returned by
    // the SemiPublic lane (i.e. the candidate-side filter never accidentally excludes a genuine tier-1 hit).
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Search_SemiPublicChannel_Tier1Viewer_ActualMember_StillOffered()
    {
        const string channelId = "semi-1";
        const string caller = "caller#1";
        const string viewer = "viewer#2";

        RegisterSession("conn-caller", caller);
        JoinChannel(channelId, "conn-caller", caller, ChannelType.SemiPublic);

        RegisterSession("conn-viewer", viewer);
        JoinChannel(channelId, "conn-viewer", viewer, ChannelType.SemiPublic);
        await SeedMembership(channelId, viewer);
        _focusRegistry.Focus("conn-viewer", channelId, viewer);

        var hub = BuildHub("conn-caller");
        var result = await hub.SearchMentionCandidates(channelId, "");

        var candidate = result.Candidates.SingleOrDefault(c => c.BattleTag == viewer);
        Assert.That(candidate, Is.Not.Null, "a genuine tier-1 (focused) member must still be offered by the SemiPublic lane");
        Assert.That(candidate.Tier, Is.EqualTo(1));
    }
}
