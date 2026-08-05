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
    // Tier 1 (live viewers) is EXEMPT from the candidate-side re-check by construction (see the class
    // doc on ChatHub.Mentions.cs's SearchMentionCandidates): reaching tier 1 requires a successful
    // FocusChannel call, which itself requires OnlineMemberRegistry membership — always seeded from a
    // durable row. Fix round 1 (finding F6b): this test only pins the POSITIVE half of that invariant —
    // a genuine tier-1 (focused) member is still returned by the SemiPublic lane. The NEGATIVE half (a
    // non-member somehow reaching tier 1 and wrongly surviving the exemption) is not something this test
    // CAN exercise: the registry invariant makes that state impossible to construct in the first place,
    // not merely untested here.
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

    // ---------------------------------------------------------------------------------------------
    // Fix round 1, finding F1 — the member-scoped RECALL BACKFILL. Filtering tiers 2/3 down to real
    // members (the tests above) can, on a big room, leave a short prefix with near-nothing: the global
    // tiers rank+cap against the WORLD before the member filter ever runs, so non-member noise can
    // crowd a genuine member entirely out of the pre-filter candidate window. These prove the backfill
    // (MembershipRepository.SearchMemberBattleTagsByPrefix) restores recall for that member without
    // ever loading the whole room.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Search_SemiPublicChannel_ShortPrefix_BigRoomNoise_MemberStillOffered_ViaBackfill()
    {
        const string channelId = "semi-1";
        const string caller = "caller#1";
        const string member = "m-buddy#2"; // actual member: OFFLINE, no directory row — findable ONLY via the backfill

        RegisterSession("conn-caller", caller);
        JoinChannel(channelId, "conn-caller", caller, ChannelType.SemiPublic);

        // Global noise matching the SAME 1-char prefix, sized well past the result cap — enough to fill
        // tier 2 (online anywhere) BEFORE the member-scope filter ever runs, so the member (who is never
        // registered as online) has zero chance of reaching `candidates` through the global tiers at all.
        for (var i = 0; i < ChatLimits.MentionSearchMaxResults + 10; i++)
        {
            RegisterSession($"conn-noise-{i}", $"m-noise{i}#1");
        }

        await SeedMembership(channelId, member);

        var hub = BuildHub("conn-caller");
        var result = await hub.SearchMentionCandidates(channelId, "m");

        Assert.That(result.Candidates.Select(c => c.BattleTag), Does.Contain(member),
            "F1: a member matching a short prefix must be recalled via the backfill even when global-tier " +
            "noise fully dominates the pre-filter candidate window");
    }

    [Test]
    public async Task Search_SystemChannel_ShortPrefix_BigRoomNoise_MemberStillOffered_ViaBackfill()
    {
        const string channelId = "system-1";
        const string caller = "caller#1";
        const string member = "m-buddy#2";

        RegisterSession("conn-caller", caller);
        JoinChannel(channelId, "conn-caller", caller, ChannelType.System);

        for (var i = 0; i < ChatLimits.MentionSearchMaxResults + 10; i++)
        {
            RegisterSession($"conn-noise-{i}", $"m-noise{i}#1");
        }

        await SeedMembership(channelId, member);

        var hub = BuildHub("conn-caller");
        var result = await hub.SearchMentionCandidates(channelId, "m");

        Assert.That(result.Candidates.Select(c => c.BattleTag), Does.Contain(member),
            "F1: the same recall backfill applies to System channels (any SystemChannelKind)");
    }

    [Test]
    public async Task Search_SemiPublicChannel_ShortPrefix_NonMemberStillAbsent_ViaBackfill()
    {
        const string channelId = "semi-1";
        const string caller = "caller#1";
        const string intruder = "m-intruder#9"; // matches the prefix, has a directory row, but NO membership

        RegisterSession("conn-caller", caller);
        JoinChannel(channelId, "conn-caller", caller, ChannelType.SemiPublic);
        await SeedDirectory(intruder, Now.AddDays(-1));

        var hub = BuildHub("conn-caller");
        var result = await hub.SearchMentionCandidates(channelId, "m");

        Assert.That(result.Candidates.Select(c => c.BattleTag), Does.Not.Contain(intruder),
            "F1: the backfill queries channel_memberships DIRECTLY, so it can never surface a non-member " +
            "regardless of prefix or directory freshness");
    }

    [Test]
    public async Task Search_SemiPublicChannel_Backfill_BoundedAndNeverCallsLoadForChannel()
    {
        const string channelId = "semi-1";
        const string caller = "caller#1";

        RegisterSession("conn-caller", caller);
        JoinChannel(channelId, "conn-caller", caller, ChannelType.SemiPublic);

        // More matching members than the result cap — the backfill's own `limit` (remaining slots) must
        // bound the read, never the room's total membership.
        for (var i = 0; i < ChatLimits.MentionSearchMaxResults + 10; i++)
        {
            await SeedMembership(channelId, $"m-member{i}#1");
        }

        var countingMembership = new CountingMembershipRepository(MongoClient, _channelRepository);
        var hub = BuildHub("conn-caller", countingMembership);

        var result = await hub.SearchMentionCandidates(channelId, "m");

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(result.Candidates.Count, Is.LessThanOrEqualTo(ChatLimits.MentionSearchMaxResults),
            "F1: the backfill's own limit (remaining cap slots) must bound the result, never the room's total membership");
        Assert.That(countingMembership.LoadForChannelCallCount, Is.EqualTo(0),
            "F1: the backfill must stay bounded via SearchMemberBattleTagsByPrefix — it must NEVER fall back to LoadForChannel's full-room scan");
    }

    // ---------------------------------------------------------------------------------------------
    // Fix round 1, finding F6a — Public is the ONLY lane that must never perform ANY membership-scoping
    // read at all (not just never the full-room LoadForChannel — the batched LoadMemberBattleTags check
    // itself must never run either).
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Search_PublicChannel_PerformsNoMembershipScopingRead()
    {
        const string channelId = "pub-1";
        const string caller = "caller#1";
        const string stranger = "stranger#2";

        RegisterSession("conn-caller", caller);
        JoinChannel(channelId, "conn-caller", caller, ChannelType.Public);
        RegisterSession("conn-stranger", stranger);

        var countingMembership = new CountingMembershipRepository(MongoClient, _channelRepository);
        var hub = BuildHub("conn-caller", countingMembership);

        var result = await hub.SearchMentionCandidates(channelId, "");

        Assert.That(result.Candidates.Select(c => c.BattleTag), Does.Contain(stranger));
        Assert.That(countingMembership.LoadMemberBattleTagsCallCount, Is.EqualTo(0),
            "F6a: Public is the universe-wide lane — it must never perform ANY membership-scoping read, batched or otherwise");
        Assert.That(countingMembership.LoadForChannelCallCount, Is.EqualTo(0));
    }
}
