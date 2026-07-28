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
/// C6 Task 8 (D10, acceptance 5): <c>SearchMentionCandidates</c> — the three-tier mention-autocomplete
/// search (<c>ChatHub.Mentions.cs</c>), served entirely from chat's own state (in-memory registries +
/// its own <c>user_directory</c> Mongo collection). Covers: tier priority order (viewer &gt; online &gt;
/// directory) with first-tier-wins dedup, the 90d activity gate applying ONLY to tier 3, enrichment via
/// ONE batch <see cref="UserDirectoryRepository.LoadMany"/> read with graceful degrade (missing row /
/// null cached Profile), the Dm/GroupDm private-lane member-scoping wall, the result cap, and the
/// zero-website-backend-call guarantee. Direct-hub-instantiation idiom (mirrors
/// <see cref="ChatHubMentionInboxTests"/>); a <see cref="FakeTimeProvider"/> drives the clock so the
/// 90d gate is independently assertable.
/// </summary>
public class ChatHubMentionSearchTests : IntegrationTestBase
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

    private ChatHub BuildHub(string connectionId, IChatAuthenticationService authService = null)
    {
        var hub = new ChatHub(
            _connectionMapping,
            _reconcileHarness.Service,
            _ticketStore,
            new W3CAuthenticationService(),
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
            RelationshipProviderTestFactory.CreateIgnored(),
            new UserSettingsRepository(MongoClient),
            new DmInitiationTracker(),
            authService ?? _authService.Object,
            MentionFanOutTestFactory.CreateIgnored(MongoClient),
            new PresenceInterestRegistry(),
            _mentionInboxRepository);

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

    private void JoinChannel(string channelId, string connectionId, string battleTag, ChannelType type = ChannelType.Public) =>
        _onlineMemberRegistry.Join(channelId, connectionId, new MemberState(battleTag, NotificationLevel.All, 0, type));

    private void FocusChannel(string channelId, string connectionId, string battleTag) =>
        _focusRegistry.Focus(connectionId, channelId, battleTag);

    private Task SeedDirectory(string battleTag, DateTime lastSeenAt, ChatProfile profile = null) =>
        _userDirectory.Upsert(new UserDirectoryEntry
        {
            BattleTag = battleTag,
            DisplayBattleTag = battleTag,
            NormalizedName = battleTag.ToLowerInvariant(),
            LastSeenAt = lastSeenAt,
            Profile = profile,
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
    // Acceptance 5 — tier priority order + first-tier-wins dedup + graceful enrichment degrade for a
    // candidate with NO directory row at all.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Search_TierOrdering_ViewerThenOnlineThenDirectory()
    {
        const string channelId = "chan-1";
        RegisterSession("conn-caller", "caller#1");
        JoinChannel(channelId, "conn-caller", "caller#1");
        var hub = BuildHub("conn-caller");

        // Tier 1: an active viewer of THIS channel.
        RegisterSession("conn-viewer", "viktor#100");
        JoinChannel(channelId, "conn-viewer", "viktor#100");
        FocusChannel(channelId, "conn-viewer", "viktor#100");

        // Tier 2: online anywhere, NOT viewing this channel.
        RegisterSession("conn-online", "victoria#200");

        // Tier 3: offline, directory-only match within the 90d window.
        await SeedDirectory("victor#300", Now.AddDays(-1));

        var result = await hub.SearchMentionCandidates(channelId, "vi");

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        var byTag = result.Candidates.ToDictionary(c => c.BattleTag, StringComparer.OrdinalIgnoreCase);
        Assert.That(byTag["viktor#100"].Tier, Is.EqualTo(1));
        Assert.That(byTag["victoria#200"].Tier, Is.EqualTo(2));
        Assert.That(byTag["victor#300"].Tier, Is.EqualTo(3));

        var order = result.Candidates.Select(c => c.BattleTag).ToList();
        Assert.That(order.IndexOf("viktor#100"), Is.LessThan(order.IndexOf("victoria#200")),
            "tier 1 (viewer) must precede tier 2 (online)");
        Assert.That(order.IndexOf("victoria#200"), Is.LessThan(order.IndexOf("victor#300")),
            "tier 2 (online) must precede tier 3 (directory)");

        // Graceful degrade — neither tier-1 nor tier-2 candidate here has ANY directory row at all.
        Assert.That(byTag["viktor#100"].Profile, Is.Null);
        Assert.That(byTag["viktor#100"].Name, Is.EqualTo("viktor"));
        Assert.That(byTag["victoria#200"].Profile, Is.Null);
        Assert.That(byTag["victoria#200"].Name, Is.EqualTo("victoria"));
    }

    [Test]
    public async Task Search_DedupeAcrossTiers_FirstTierWins()
    {
        const string channelId = "chan-1";
        const string vic = "victor#1";
        RegisterSession("conn-caller", "caller#1");
        JoinChannel(channelId, "conn-caller", "caller#1");
        var hub = BuildHub("conn-caller");

        // Eligible for ALL THREE tiers: focused on this channel (1), online (2), and a fresh
        // directory row (3) — dedup must keep exactly the FIRST (tier 1).
        RegisterSession("conn-vic", vic);
        JoinChannel(channelId, "conn-vic", vic);
        FocusChannel(channelId, "conn-vic", vic);
        await SeedDirectory(vic, Now.AddDays(-1));

        var result = await hub.SearchMentionCandidates(channelId, "vic");

        Assert.That(result.Candidates.Count(c => string.Equals(c.BattleTag, vic, StringComparison.OrdinalIgnoreCase)),
            Is.EqualTo(1), "a viewer eligible for all three tiers must appear exactly once");
        Assert.That(result.Candidates.Single().Tier, Is.EqualTo(1),
            "dedup must keep the FIRST tier the candidate was found in — a viewer never re-listed in tier 3");
    }

    // ---------------------------------------------------------------------------------------------
    // Prefix matching — case-insensitive on NormalizedName; the SAME index serves both name-prefix
    // and name#digits-prefix autocomplete.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Search_PrefixMatch_OnNormalizedName_CaseInsensitive()
    {
        const string channelId = "chan-1";
        RegisterSession("conn-caller", "caller#1");
        JoinChannel(channelId, "conn-caller", "caller#1");
        var hub = BuildHub("conn-caller");

        await SeedDirectory("peter#123", Now.AddDays(-2));

        var result = await hub.SearchMentionCandidates(channelId, "PET");

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(result.Candidates.Select(c => c.BattleTag), Does.Contain("peter#123"));
        Assert.That(result.Candidates.Single(c => c.BattleTag == "peter#123").Tier, Is.EqualTo(3));
    }

    [Test]
    public async Task Search_NameHashPrefix_Matches()
    {
        const string channelId = "chan-1";
        RegisterSession("conn-caller", "caller#1");
        JoinChannel(channelId, "conn-caller", "caller#1");
        var hub = BuildHub("conn-caller");

        await SeedDirectory("peter#123", Now.AddDays(-2));

        var result = await hub.SearchMentionCandidates(channelId, "peter#1");

        Assert.That(result.Candidates.Select(c => c.BattleTag), Does.Contain("peter#123"));
    }

    [Test]
    public async Task Search_EmptyPrefix_ReturnsViewers()
    {
        const string channelId = "chan-1";
        const string viewer = "viewer#1";
        RegisterSession("conn-caller", "caller#1");
        JoinChannel(channelId, "conn-caller", "caller#1");
        RegisterSession("conn-viewer", viewer);
        JoinChannel(channelId, "conn-viewer", viewer);
        FocusChannel(channelId, "conn-viewer", viewer);
        var hub = BuildHub("conn-caller");

        var result = await hub.SearchMentionCandidates(channelId, "");

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(result.Candidates.Select(c => c.BattleTag), Does.Contain(viewer));
        Assert.That(result.Candidates.Single(c => c.BattleTag == viewer).Tier, Is.EqualTo(1));
    }

    // ---------------------------------------------------------------------------------------------
    // The 90d activity gate — tier 3 ONLY.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Search_Beyond90d_ExcludedFromTier3()
    {
        const string channelId = "chan-1";
        const string stale = "stale#1";
        RegisterSession("conn-caller", "caller#1");
        JoinChannel(channelId, "conn-caller", "caller#1");
        var hub = BuildHub("conn-caller");

        await SeedDirectory(stale, Now - ChatLimits.MentionCandidateActivityWindow - TimeSpan.FromDays(1));

        var offlineResult = await hub.SearchMentionCandidates(channelId, "sta");
        Assert.That(offlineResult.Candidates.Select(c => c.BattleTag), Does.Not.Contain(stale),
            "beyond the 90d gate and offline, must be excluded from tier 3");

        // The SAME user, now ONLINE — the 90d gate applies ONLY to tier 3; tier 2 never consults it.
        RegisterSession("conn-stale", stale);
        var onlineResult = await hub.SearchMentionCandidates(channelId, "sta");
        var candidate = onlineResult.Candidates.Single(c => c.BattleTag == stale);
        Assert.That(candidate.Tier, Is.EqualTo(2),
            "an online user must appear via tier 2 regardless of how stale their directory LastSeenAt is");
    }

    // ---------------------------------------------------------------------------------------------
    // Enrichment — ONE batch LoadMany read; graceful degrade for a directory row with no cached
    // Profile yet.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Search_EnrichedFromDirectoryCache_ClanLeagueGames()
    {
        const string channelId = "chan-1";
        const string target = "enriched#1";
        RegisterSession("conn-caller", "caller#1");
        JoinChannel(channelId, "conn-caller", "caller#1");
        var hub = BuildHub("conn-caller");

        var profile = new ChatProfile
        {
            ClanId = "ClanX",
            LeagueName = "Grandmaster",
            RankNumber = 1,
            GamesPlayed = 42,
        };
        await SeedDirectory(target, Now.AddDays(-1), profile);

        var result = await hub.SearchMentionCandidates(channelId, "enr");

        var dto = result.Candidates.Single(c => c.BattleTag == target);
        Assert.That(dto.Profile, Is.Not.Null);
        Assert.That(dto.Profile.ClanId, Is.EqualTo("ClanX"));
        Assert.That(dto.Profile.LeagueName, Is.EqualTo("Grandmaster"));
        Assert.That(dto.Profile.RankNumber, Is.EqualTo(1));
        Assert.That(dto.Profile.GamesPlayed, Is.EqualTo(42));
        Assert.That(dto.Name, Is.EqualTo("enriched"));
    }

    [Test]
    public async Task Search_StubbedProfile_GracefullyAbsent()
    {
        const string channelId = "chan-1";
        const string target = "stub#1";
        RegisterSession("conn-caller", "caller#1");
        JoinChannel(channelId, "conn-caller", "caller#1");
        var hub = BuildHub("conn-caller");

        await SeedDirectory(target, Now.AddDays(-1), profile: null);

        var result = await hub.SearchMentionCandidates(channelId, "stu");

        var dto = result.Candidates.Single(c => c.BattleTag == target);
        Assert.That(dto.Profile, Is.Null, "a directory row with no cached Profile yet must degrade to null, never error");
        Assert.That(dto.Name, Is.EqualTo("stub"), "Name is still derived from the tag itself");
    }

    // ---------------------------------------------------------------------------------------------
    // Authorization — the caller must be an actual member of the channel being searched.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Search_NonMemberCaller_NotMember()
    {
        RegisterSession("conn-caller", "caller#1");
        // Deliberately NOT joined via OnlineMemberRegistry.
        var hub = BuildHub("conn-caller");

        var result = await hub.SearchMentionCandidates("chan-1", "x");

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.NotMember));
    }

    [Test]
    public async Task Search_NoSession_PermissionDenied()
    {
        var hub = BuildHub("conn-ghost");

        var result = await hub.SearchMentionCandidates("chan-1", "x");

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.PermissionDenied));
    }

    // ---------------------------------------------------------------------------------------------
    // Private-lane scoping (D10) — Dm/GroupDm restrict EVERY tier to the channel's actual member set.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Search_DmChannel_CandidatesAreMembersOnly()
    {
        const string channelId = "dm-1";
        const string caller = "caller#1";
        const string buddy = "buddy#2";       // actual Dm member, offline but recently active
        const string intruder = "intruder#3"; // NOT a member — online AND directory-fresh

        RegisterSession("conn-caller", caller);
        JoinChannel(channelId, "conn-caller", caller, ChannelType.Dm);
        await SeedMembership(channelId, caller);
        await SeedMembership(channelId, buddy);
        await SeedDirectory(buddy, Now.AddDays(-1));

        RegisterSession("conn-intruder", intruder); // tier-2-eligible if the wall didn't hold
        await SeedDirectory(intruder, Now.AddDays(-1)); // tier-3-eligible if the wall didn't hold

        var hub = BuildHub("conn-caller");
        var result = await hub.SearchMentionCandidates(channelId, "");

        var tags = result.Candidates.Select(c => c.BattleTag).ToList();
        Assert.That(tags, Does.Contain(buddy), "an actual Dm member must still be offered");
        Assert.That(tags, Does.Not.Contain(intruder),
            "a non-member must NEVER be offered inside a Dm, even though it is online and directory-fresh");
    }

    [Test]
    public async Task Search_DmChannel_MemberNotCrowdedOutByUnrelatedDirectoryNoise()
    {
        // Regression (code review finding): tier 3 for a private lane must filter the channel's OWN
        // (small, fully-known) member set in memory — NEVER run the generic UNSCOPED
        // SearchByNormalizedPrefix and hope the real member's row survives ITS OWN Mongo-side cap. Seed
        // far more than ChatLimits.MentionSearchMaxResults unrelated, matching, non-member directory
        // rows BEFORE the real member's own row — if tier 3 ever ran the unscoped global query, the
        // member's row could be crowded out of the top-N Mongo returns entirely.
        const string channelId = "dm-1";
        const string caller = "caller#1";
        const string buddy = "aaa-buddy#2"; // actual Dm member, offline but recently active

        RegisterSession("conn-caller", caller);
        JoinChannel(channelId, "conn-caller", caller, ChannelType.Dm);
        await SeedMembership(channelId, caller);
        await SeedMembership(channelId, buddy);

        // Unrelated noise, seeded FIRST, matching the SAME prefix, well over the result cap.
        for (var i = 0; i < ChatLimits.MentionSearchMaxResults + 10; i++)
        {
            await SeedDirectory($"aaa-noise{i}#1", Now.AddDays(-1));
        }

        // The real member's own row, seeded LAST.
        await SeedDirectory(buddy, Now.AddDays(-1));

        var hub = BuildHub("conn-caller");
        var result = await hub.SearchMentionCandidates(channelId, "aaa");

        Assert.That(result.Candidates.Select(c => c.BattleTag), Does.Contain(buddy),
            "a genuine Dm member must never be crowded out of tier 3 by unrelated directory noise");
    }

    [Test]
    public async Task Search_GroupDm_SameWall()
    {
        const string channelId = "group-1";
        const string caller = "caller#1";
        const string memberA = "membera#2";
        const string memberB = "memberb#3";
        const string intruder = "intruder#4";

        RegisterSession("conn-caller", caller);
        JoinChannel(channelId, "conn-caller", caller, ChannelType.GroupDm);
        await SeedMembership(channelId, caller);
        await SeedMembership(channelId, memberA);
        await SeedMembership(channelId, memberB);
        await SeedDirectory(memberA, Now.AddDays(-1));

        RegisterSession("conn-intruder", intruder);
        RegisterSession("conn-memberb", memberB); // an ONLINE actual member must still be offered
        await SeedDirectory(intruder, Now.AddDays(-1));

        var hub = BuildHub("conn-caller");
        var result = await hub.SearchMentionCandidates(channelId, "");

        var tags = result.Candidates.Select(c => c.BattleTag).ToList();
        Assert.That(tags, Does.Contain(memberA), "an offline-but-recent actual GroupDm member must still be offered");
        Assert.That(tags, Does.Contain(memberB), "an online actual GroupDm member must still be offered");
        Assert.That(tags, Does.Not.Contain(intruder),
            "a non-member must NEVER be offered inside a GroupDm, even though it is online and directory-fresh");
    }

    // ---------------------------------------------------------------------------------------------
    // Result cap.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Search_ResultCap_20()
    {
        const string channelId = "chan-1";
        RegisterSession("conn-caller", "caller#1");
        JoinChannel(channelId, "conn-caller", "caller#1");
        var hub = BuildHub("conn-caller");

        for (var i = 0; i < ChatLimits.MentionSearchMaxResults + 10; i++)
        {
            await SeedDirectory($"zap{i}#1", Now.AddDays(-1));
        }

        var result = await hub.SearchMentionCandidates(channelId, "zap");

        Assert.That(result.Candidates, Has.Count.EqualTo(ChatLimits.MentionSearchMaxResults));
    }

    [Test]
    public async Task Search_ResultCapAndDedupe_HoldUnderLargeOverlappingFixture()
    {
        const string channelId = "chan-1";
        RegisterSession("conn-caller", "caller#1");
        JoinChannel(channelId, "conn-caller", "caller#1");
        var hub = BuildHub("conn-caller");

        // Tier 1: 5 viewers of THIS channel.
        for (var i = 0; i < 5; i++)
        {
            var tag = $"viewer{i}#1";
            RegisterSession($"conn-viewer-{i}", tag);
            JoinChannel(channelId, $"conn-viewer-{i}", tag);
            FocusChannel(channelId, $"conn-viewer-{i}", tag);
        }

        // Tier 2: 10 more users online anywhere (not viewing this channel).
        for (var i = 0; i < 10; i++)
        {
            RegisterSession($"conn-online-{i}", $"online{i}#1");
        }

        // Tier 3: the SAME 15 users above ALSO get fresh directory rows (must collapse into
        // whichever tier already claimed them) PLUS 20 more directory-only users — a pool of 35+
        // distinct battleTags with heavy cross-tier overlap, well over the cap.
        for (var i = 0; i < 5; i++) await SeedDirectory($"viewer{i}#1", Now.AddDays(-1));
        for (var i = 0; i < 10; i++) await SeedDirectory($"online{i}#1", Now.AddDays(-1));
        for (var i = 0; i < 20; i++) await SeedDirectory($"dironly{i}#1", Now.AddDays(-1));

        var result = await hub.SearchMentionCandidates(channelId, ""); // unfiltered — the whole pool is eligible

        Assert.That(result.Candidates.Count, Is.LessThanOrEqualTo(ChatLimits.MentionSearchMaxResults),
            "the result must never exceed the total cap even with a much larger eligible pool");

        var distinctTags = result.Candidates.Select(c => c.BattleTag.ToLowerInvariant()).Distinct().Count();
        Assert.That(distinctTags, Is.EqualTo(result.Candidates.Count), "no battleTag may appear twice in the result");

        // Every tier-1 viewer that made the cut must be tagged Tier 1 — never re-listed under 2/3
        // despite being eligible for both (their directory rows are fresh too).
        foreach (var dto in result.Candidates.Where(c => c.BattleTag.StartsWith("viewer", StringComparison.OrdinalIgnoreCase)))
        {
            Assert.That(dto.Tier, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Search_PublicLane_DirectoryDupesRankedFirst_DoNotStarveOutLaterTier3OnlyCandidate()
    {
        // Regression (task reviewer finding, C6 T8): the SAME bug class the private lane already had
        // fixed (Search_DmChannel_MemberNotCrowdedOutByUnrelatedDirectoryNoise, above) — a public-lane
        // tier 3 directory query must not let rows it is about to discard as dupes of tiers 1/2 consume
        // its own result window ahead of a genuinely new, later-sorting match. This fixture is the
        // OPPOSITE ordering of Search_ResultCapAndDedupe_HoldUnderLargeOverlappingFixture (above), which
        // (by coincidence of alphabetical naming — "dironly" sorts before "online"/"viewer") never
        // actually exercised this: here the tier-1/2 dupes' OWN directory rows are named to sort BEFORE
        // the one genuine tier-3-only candidate.
        const string channelId = "chan-1";
        const string target = "zzz-target#1"; // sorts AFTER every "online"/"viewer" row below.
        RegisterSession("conn-caller", "caller#1");
        JoinChannel(channelId, "conn-caller", "caller#1");
        var hub = BuildHub("conn-caller");

        // Tier 1: 5 viewers of THIS channel.
        for (var i = 0; i < 5; i++)
        {
            var tag = $"viewer{i}#1";
            RegisterSession($"conn-viewer-{i}", tag);
            JoinChannel(channelId, $"conn-viewer-{i}", tag);
            FocusChannel(channelId, $"conn-viewer-{i}", tag);
        }

        // Tier 2: 10 more online anywhere (not viewing this channel) — plus the caller itself, which
        // GetOnlineBattleTags also surfaces, for 16 total candidates filled before tier 3 ever runs.
        for (var i = 0; i < 10; i++)
        {
            RegisterSession($"conn-online-{i}", $"online{i}#1");
        }

        // Every tier-1/2 member ALSO gets a fresh, matching directory row — tier 3's own query will
        // re-discover every one of them, and (alphabetically) ALL of them sort before "zzz-target#1".
        // With only ChatLimits.MentionSearchMaxResults - 16 = 4 slots left, a query that (a) still asks
        // for a full/flat limit while (b) not excluding rows it already knows are dupes would burn its
        // entire window on these re-discovered dupes and never even reach the target.
        for (var i = 0; i < 5; i++) await SeedDirectory($"viewer{i}#1", Now.AddDays(-1));
        for (var i = 0; i < 10; i++) await SeedDirectory($"online{i}#1", Now.AddDays(-1));
        await SeedDirectory(target, Now.AddDays(-1));

        var result = await hub.SearchMentionCandidates(channelId, "");

        Assert.That(result.Candidates.Select(c => c.BattleTag), Does.Contain(target),
            "a genuine tier-3-only candidate must not be starved out by dupes that rank ahead of it " +
            "in the directory query's own sort order");
    }

    // ---------------------------------------------------------------------------------------------
    // Zero cross-service calls — a throwing IWebsiteBackendRepository spy, wired through a REAL
    // ChatAuthenticationService, proves the search path never reaches the website backend.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Search_NoWbCallEver()
    {
        const string channelId = "chan-1";
        RegisterSession("conn-caller", "caller#1");
        JoinChannel(channelId, "conn-caller", "caller#1");

        var throwingWb = new Mock<IWebsiteBackendRepository>();
        throwingWb.Setup(m => m.GetChatDetails(It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("SearchMentionCandidates must never reach the website backend"));
        var realAuthService = new ChatAuthenticationService(MongoClient, throwingWb.Object, _userDirectory);

        var hub = BuildHub("conn-caller", realAuthService);

        await SeedDirectory("target#1", Now.AddDays(-1));

        SearchMentionCandidatesResult result = null;
        Assert.DoesNotThrowAsync(async () => result = await hub.SearchMentionCandidates(channelId, "tar"));

        Assert.That(result, Is.Not.Null);
        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        throwingWb.Verify(m => m.GetChatDetails(It.IsAny<string>()), Times.Never);
    }
}
