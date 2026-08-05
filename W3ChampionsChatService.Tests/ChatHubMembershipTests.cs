using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Moq;
using MongoDB.Driver;
using NUnit.Framework;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.FanOut;
using W3ChampionsChatService.Mentions;
using W3ChampionsChatService.Memberships;
using W3ChampionsChatService.Mutes;
using W3ChampionsChatService.Protocol;
using W3ChampionsChatService.Sessions;
using W3ChampionsChatService.Users;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// C3 Task 10: <c>JoinChannel</c>/<c>LeaveChannel</c>/<c>SetNotificationLevel</c> — membership
/// self-service, including implicit semiPublic creation. Covers the JoinChannel resolution order
/// (Public full-ban gate, SemiPublic no-gate, ACL-type rejection with NO implicit-create fallthrough,
/// the creation throttle metering ONLY actual creations, the idempotent-already-member short-circuit,
/// and the membership cap), LeaveChannel's registry teardown, and SetNotificationLevel's
/// reject-not-clamp rule for Public channels (acceptance 3, 9, 10).
/// <para>
/// Follows the ChatHubFocusTests idiom: all hubs built in a test SHARE the same registries/repos (set
/// up once in SetUp), and TimeProvider.System is used throughout — the creation-throttle window is an
/// hour, comfortably longer than any single test's real wall-clock execution time.
/// </para>
/// </summary>
public class ChatHubMembershipTests : IntegrationTestBase
{
    private const string BattleTag = "peter#123";

    private ConnectionMapping _connectionMapping;
    private UserDirectoryRepository _userDirectory;
    private MuteRepository _muteRepository;
    private MuteReconciliationService _reconcileService;
    private TicketStore _ticketStore;
    private Mock<IChatAuthenticationService> _authService;

    private ChannelRepository _channelRepository;
    private MembershipRepository _membershipRepository;
    private NotificationPreferenceRepository _notificationPreferenceRepository;
    private SessionRegistry _sessionRegistry;
    private FocusRegistry _focusRegistry;
    private OnlineMemberRegistry _onlineMemberRegistry;
    private MessageRateLimiter _messageRateLimiter;
    private ReadRateLimiter _readRateLimiter;
    private ChannelCreationRateLimiter _channelCreationRateLimiter;
    private SessionStateAssembler _assembler;

    [SetUp]
    public void SetupBeforeEach()
    {
        _connectionMapping = new ConnectionMapping();
        _userDirectory = new UserDirectoryRepository(MongoClient);
        _muteRepository = new MuteRepository(MongoClient);
        _reconcileService = new MuteReconciliationTestHarness(_connectionMapping, _muteRepository).Service;
        _ticketStore = new TicketStore();

        _authService = new Mock<IChatAuthenticationService>();
        _authService.Setup(m => m.GetUserFromIdentity(It.IsAny<W3CUserAuthentication>()))
            .ReturnsAsync((W3CUserAuthentication id) =>
                new ChatUserResolution(new ChatUser(id.BattleTag, id.IsAdmin, id.Name, new ProfilePicture(), null, null), true));

        _channelRepository = new ChannelRepository(MongoClient);
        _membershipRepository = new MembershipRepository(MongoClient, _channelRepository);
        _notificationPreferenceRepository = new NotificationPreferenceRepository(MongoClient);
        _sessionRegistry = new SessionRegistry();
        _focusRegistry = new FocusRegistry();
        _onlineMemberRegistry = new OnlineMemberRegistry();
        _messageRateLimiter = new MessageRateLimiter();
        _readRateLimiter = new ReadRateLimiter();
        _channelCreationRateLimiter = new ChannelCreationRateLimiter();
        _assembler = new SessionStateAssembler(
            _membershipRepository,
            _channelRepository,
            new W3ChampionsChatService.Messages.MessageRepository(MongoClient),
            _muteRepository,
            _onlineMemberRegistry,
            _connectionMapping,
            new MentionInboxRepository(MongoClient));
    }

    // Fix round 1 (finding F2b): optional channelRepository lets a single test swap in
    // CountingChannelRepository (spy) without touching the shared _channelRepository field every other
    // test in this file relies on — mirrors the existing notificationPreferenceRepository override.
    private ChatHub BuildHub(
        string connectionId,
        NotificationPreferenceRepository notificationPreferenceRepository = null,
        ChannelRepository channelRepository = null)
    {
        var hub = new ChatHub(
            _connectionMapping,
            _reconcileService,
            _ticketStore,
            _sessionRegistry,
            _userDirectory,
            _assembler,
            _focusRegistry,
            _onlineMemberRegistry,
            _messageRateLimiter,
            _readRateLimiter,
            TimeProvider.System,
            channelRepository ?? _channelRepository,
            _membershipRepository,
            _channelCreationRateLimiter,
            new W3ChampionsChatService.Messages.MessageRepository(MongoClient),
            FanOutEngineTestFactory.CreateIgnored(),
            ViewersAccumulatorTestFactory.CreateIgnored(),
            new NoOpMentionInboxCleaner(),
            RelationshipProviderTestFactory.CreateIgnored(),
            new UserSettingsRepository(MongoClient),
            new DmInitiationTracker(),
            _authService.Object,
            MentionFanOutTestFactory.CreateIgnored(MongoClient),
            new PresenceInterestRegistry(),
            new MentionInboxRepository(MongoClient),
            notificationPreferenceRepository ?? _notificationPreferenceRepository);

        hub.Clients = new Mock<IHubCallerClients>().Object;

        var context = new Mock<HubCallerContext>();
        context.Setup(c => c.ConnectionId).Returns(connectionId);
        hub.Context = context.Object;
        hub.Groups = new Mock<IGroupManager>().Object;

        return hub;
    }

    private void RegisterSession(string connectionId, string battleTag, string name = null) =>
        _sessionRegistry.Register(
            connectionId,
            new W3CUserAuthentication { BattleTag = battleTag, Name = name ?? battleTag.Split('#')[0] },
            null);

    private async Task<ChatChannel> CreateChannel(string name, ChannelType type = ChannelType.Public)
    {
        var channel = new ChatChannel { Type = type, Name = name, NormalizedName = ChannelNames.Normalize(name) };
        await _channelRepository.Insert(channel);
        return channel;
    }

    private async Task<ChatChannel> CreateSystemMatchChannel(string name)
    {
        var channel = new ChatChannel
        {
            Type = ChannelType.System,
            SystemKind = SystemChannelKind.Match,
            SystemRef = "match-1",
            NormalizedName = ChannelNames.Normalize(name),
        };
        await _channelRepository.Insert(channel);
        return channel;
    }

    private Task<ChannelMembership> DirectlyJoin(string channelId, string battleTag) =>
        InsertMembership(channelId, battleTag, NotificationLevel.Mentions);

    private async Task<ChannelMembership> InsertMembership(string channelId, string battleTag, NotificationLevel level)
    {
        var membership = new ChannelMembership
        {
            ChannelId = channelId,
            BattleTag = battleTag,
            NotificationLevel = level,
            JoinedAt = DateTime.UtcNow,
        };
        await _membershipRepository.Insert(membership);
        return membership;
    }

    // ---------------------------------------------------------------------------------------------
    // JoinChannel
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task JoinChannel_PublicByName_CreatesMembership_DefaultLevelMentions()
    {
        var channel = await CreateChannel("general");
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");

        var result = await hub.JoinChannel("general");

        Assert.AreEqual(ChatResultCode.Ok, result.Code);
        Assert.AreEqual(channel.Id, result.Channel.Id);
        Assert.IsNotNull(result.Membership);
        Assert.AreEqual(NotificationLevel.Mentions, result.Membership.NotificationLevel,
            "A freshly joined membership overrides the ChannelMembership model default (All) with Mentions");

        var persisted = await _membershipRepository.Load(channel.Id, BattleTag);
        Assert.IsNotNull(persisted, "The membership must be persisted, not just returned");
        Assert.AreEqual(NotificationLevel.Mentions, persisted.NotificationLevel);

        var registryMember = _onlineMemberRegistry.GetMembers(channel.Id).Single();
        Assert.AreEqual(BattleTag, registryMember.BattleTag, "OnlineMemberRegistry must be seeded for this connection");
        Assert.AreEqual(NotificationLevel.Mentions, registryMember.NotificationLevel);
    }

    [Test]
    public async Task JoinChannel_UnknownName_CreatesSemiPublic_DefaultLevelMentions()
    {
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");

        var result = await hub.JoinChannel("brand-new-room");

        Assert.AreEqual(ChatResultCode.Ok, result.Code);
        Assert.IsNotNull(result.Channel);
        Assert.AreEqual(ChannelType.SemiPublic, result.Channel.Type,
            "An unknown name must implicitly create a SemiPublic channel");
        Assert.AreEqual("brand-new-room", result.Channel.NormalizedName);
        Assert.AreEqual(NotificationLevel.Mentions, result.Membership.NotificationLevel);

        var stored = await _channelRepository.LoadAnyByNormalizedName("brand-new-room");
        Assert.IsNotNull(stored, "The implicitly created channel must be persisted");
        Assert.AreEqual(ChannelType.SemiPublic, stored.Type);
    }

    [Test]
    public async Task JoinChannel_MatchTypeChannelName_ReturnsPermissionDenied_NotImplicitCreate()
    {
        var systemChannel = await CreateSystemMatchChannel("match-123");
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");

        var result = await hub.JoinChannel("match-123");

        Assert.AreEqual(ChatResultCode.PermissionDenied, result.Code,
            "A name collision with an ACL-governed (System) channel must be denied, not joined or recreated");

        var stillSystem = await _channelRepository.LoadAnyByNormalizedName("match-123");
        Assert.AreEqual(systemChannel.Id, stillSystem.Id, "No new channel must have been created under this name");
        Assert.AreEqual(ChannelType.System, stillSystem.Type);

        var membership = await _membershipRepository.Load(systemChannel.Id, BattleTag);
        Assert.IsNull(membership, "No membership must have been created against the ACL channel");
    }

    // ---------------------------------------------------------------------------------------------
    // Match-channel-hygiene brief (2026-08-05), Part 3 hardening — fix round 1, finding F2b: a
    // null/whitespace-only name is now rejected BEFORE any DB read, returning the SAME PermissionDenied
    // the (still-kept, defense-in-depth) ACL-type denial branch above would otherwise yield reaching it
    // via {NormalizedName: null} matching every absent-field System/Dm/GroupDm document.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task JoinChannel_NullName_ReturnsPermissionDenied_WithZeroChannelCollectionReads()
    {
        var spy = new CountingChannelRepository(MongoClient);
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1", channelRepository: spy);

        var result = await hub.JoinChannel(null);

        Assert.AreEqual(ChatResultCode.PermissionDenied, result.Code);
        Assert.AreEqual(0, spy.LoadAnyByNormalizedNameCallCount,
            "a null name must be rejected before ANY channel-collection read");
    }

    [Test]
    public async Task JoinChannel_WhitespaceOnlyName_ReturnsPermissionDenied_WithZeroChannelCollectionReads()
    {
        var spy = new CountingChannelRepository(MongoClient);
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1", channelRepository: spy);

        var result = await hub.JoinChannel("   ");

        Assert.AreEqual(ChatResultCode.PermissionDenied, result.Code);
        Assert.AreEqual(0, spy.LoadAnyByNormalizedNameCallCount,
            "a whitespace-only name must be rejected before ANY channel-collection read");
    }

    [Test]
    public async Task JoinChannel_51stPublicOrSemiPublic_ReturnsPermissionDenied()
    {
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");

        for (var i = 0; i < ChatLimits.MaxPublicMembershipsPerUser; i++)
        {
            var channel = await CreateChannel($"cap-chan-{i}");
            await DirectlyJoin(channel.Id, BattleTag);
        }

        var fiftyFirst = await CreateChannel("cap-chan-50");
        var result = await hub.JoinChannel("cap-chan-50");

        Assert.AreEqual(ChatResultCode.PermissionDenied, result.Code,
            "A 51st distinct name-joinable membership must be denied once the cap is already saturated");

        var membership = await _membershipRepository.Load(fiftyFirst.Id, BattleTag);
        Assert.IsNull(membership, "No membership must have been created past the cap");
    }

    [Test]
    public async Task JoinChannel_AtCap_NewName_ReturnsPermissionDenied_NoOrphanChannelCreated()
    {
        // A user already at the membership cap joins a brand-new (not-found) name: the cap must be
        // checked BEFORE the creation throttle / actual channel creation, so this returns a
        // deterministic PermissionDenied with NO orphan SemiPublic channel persisted and NO
        // creation-throttle token consumed.
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");

        for (var i = 0; i < ChatLimits.MaxPublicMembershipsPerUser; i++)
        {
            var channel = await CreateChannel($"cap3-chan-{i}");
            await DirectlyJoin(channel.Id, BattleTag);
        }

        var result = await hub.JoinChannel("a-brand-new-unique-name");

        Assert.AreEqual(ChatResultCode.PermissionDenied, result.Code,
            "A capped user joining a brand-new name must be denied before any channel is created");

        var orphan = await _channelRepository.LoadAnyByNormalizedName(ChannelNames.Normalize("a-brand-new-unique-name"));
        Assert.IsNull(orphan, "No orphan SemiPublic channel must have been created for a capped user");
    }

    [Test]
    public async Task JoinChannel_SemiPublicCreation_SixthInHour_ReturnsThrottled()
    {
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");

        for (var i = 0; i < ChatLimits.ChannelCreationPerHour; i++)
        {
            var result = await hub.JoinChannel($"impl-room-{i}");
            Assert.AreEqual(ChatResultCode.Ok, result.Code, $"Creation #{i + 1} is within the per-hour cap and must succeed");
        }

        var sixth = await hub.JoinChannel("impl-room-5");

        Assert.AreEqual(ChatResultCode.Throttled, sixth.Code,
            "A 6th distinct implicit creation within the same hour must be throttled");
        Assert.IsTrue(sixth.RetryAfterSeconds > 0, "A throttled response must carry a positive retry-after");

        var notCreated = await _channelRepository.LoadAnyByNormalizedName("impl-room-5");
        Assert.IsNull(notCreated, "The throttled 6th channel must NOT have been created");
    }

    [Test]
    public async Task JoinChannel_JoiningExistingChannels_NeverCountsAgainstCreationThrottle()
    {
        // Discriminates "only ACTUAL creations are metered" — 5 creations exhaust the per-hour cap,
        // but re-joining EXISTING channels (by a second user) must never touch that counter.
        var existing = await CreateChannel("already-exists");
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");

        for (var i = 0; i < ChatLimits.ChannelCreationPerHour; i++)
        {
            var created = await hub.JoinChannel($"metered-room-{i}");
            Assert.AreEqual(ChatResultCode.Ok, created.Code);
        }

        // The creation budget is exhausted, but joining an EXISTING channel must still succeed.
        var joinExisting = await hub.JoinChannel("already-exists");

        Assert.AreEqual(ChatResultCode.Ok, joinExisting.Code,
            "Joining an EXISTING channel must never be throttled by the creation-only counter");
        Assert.AreEqual(existing.Id, joinExisting.Channel.Id);
    }

    [Test]
    public async Task JoinChannel_FullBanned_PublicChannel_ReturnsPermissionDenied()
    {
        var channel = await CreateChannel("W3C Lounge");
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");

        // Seed the connection's mute cache the SAME way the connect flow does
        // (SessionStateAssembler.SeedLegacyMuteCache -> ConnectionMapping.SetMute).
        _connectionMapping.SetMute("conn-1", MuteStatus.Full, DateTime.UtcNow.AddDays(1));

        var result = await hub.JoinChannel("W3C Lounge");

        Assert.AreEqual(ChatResultCode.PermissionDenied, result.Code,
            "A full-banned user must not be able to join a Public channel");

        var membership = await _membershipRepository.Load(channel.Id, BattleTag);
        Assert.IsNull(membership);
    }

    [Test]
    public async Task JoinChannel_FullBanned_SemiPublicChannel_IsExemptFromMuteGate()
    {
        // SemiPublic is deliberately exempt from the full-ban gate (only Public is gated).
        var channel = await CreateChannel("semi-room", ChannelType.SemiPublic);
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");
        _connectionMapping.SetMute("conn-1", MuteStatus.Full, DateTime.UtcNow.AddDays(1));

        var result = await hub.JoinChannel("semi-room");

        Assert.AreEqual(ChatResultCode.Ok, result.Code, "SemiPublic channels must be exempt from the full-ban gate");
        Assert.AreEqual(channel.Id, result.Channel.Id);
    }

    [Test]
    public async Task JoinChannel_AlreadyMember_ReturnsOk_Idempotent()
    {
        var channel = await CreateChannel("general");
        var existing = await DirectlyJoin(channel.Id, BattleTag);
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");

        var result = await hub.JoinChannel("general");

        Assert.AreEqual(ChatResultCode.Ok, result.Code);
        Assert.AreEqual(existing.Id, result.Membership.Id, "Re-joining must return the EXISTING membership, not a new one");

        var all = await _membershipRepository.LoadForUser(BattleTag);
        Assert.AreEqual(1, all.Count(m => m.ChannelId == channel.Id), "Re-joining must not create a duplicate membership row");
    }

    [Test]
    public async Task JoinChannel_AlreadyMember_DoesNotCountAgainstCap()
    {
        // Saturate the cap, then re-join one already-joined channel: must still be Ok because it's
        // not a NEW membership.
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");

        ChatChannel first = null;
        for (var i = 0; i < ChatLimits.MaxPublicMembershipsPerUser; i++)
        {
            var channel = await CreateChannel($"cap2-chan-{i}");
            first ??= channel;
            await DirectlyJoin(channel.Id, BattleTag);
        }

        var rejoin = await hub.JoinChannel("cap2-chan-0");

        Assert.AreEqual(ChatResultCode.Ok, rejoin.Code,
            "Re-joining an already-joined channel at a saturated cap must stay Ok");
        Assert.AreEqual(first.Id, rejoin.Channel.Id);
    }

    [Test]
    public async Task JoinChannel_UnregisteredConnection_ReturnsPermissionDenied_FailClosed()
    {
        await CreateChannel("general");
        var hub = BuildHub("conn-ghost");

        var result = await hub.JoinChannel("general");

        Assert.AreEqual(ChatResultCode.PermissionDenied, result.Code);
    }

    // ---------------------------------------------------------------------------------------------
    // LeaveChannel
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task LeaveChannel_DeletesMembership_UpdatesRegistry_Unfocuses()
    {
        var channel = await CreateChannel("general");
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");
        await hub.JoinChannel("general");
        await hub.FocusChannel(channel.Id);

        var result = await hub.LeaveChannel(channel.Id);

        Assert.AreEqual(ChatResultCode.Ok, result.Code);

        var membership = await _membershipRepository.Load(channel.Id, BattleTag);
        Assert.IsNull(membership, "The membership row must be deleted");

        Assert.IsEmpty(_onlineMemberRegistry.GetMembers(channel.Id), "OnlineMemberRegistry must no longer carry this connection's entry");
        Assert.IsEmpty(_focusRegistry.GetFocusedChannels("conn-1"), "FocusRegistry must be unfocused for this channel");
    }

    [Test]
    public async Task LeaveChannel_NotAMember_StillReturnsOk_Idempotent()
    {
        var channel = await CreateChannel("general");
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");

        var result = await hub.LeaveChannel(channel.Id);

        Assert.AreEqual(ChatResultCode.Ok, result.Code, "Leaving a channel you're not a member of is a no-op, still Ok");
    }

    [Test]
    public async Task LeaveChannel_UnregisteredConnection_ReturnsPermissionDenied_FailClosed()
    {
        var hub = BuildHub("conn-ghost");

        var result = await hub.LeaveChannel("some-channel-id");

        Assert.AreEqual(ChatResultCode.PermissionDenied, result.Code);
    }

    // ---------------------------------------------------------------------------------------------
    // SetNotificationLevel
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task SetNotificationLevel_All_OnPublic_ReturnsPermissionDenied()
    {
        var channel = await CreateChannel("general");
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");
        await hub.JoinChannel("general");

        var result = await hub.SetNotificationLevel(channel.Id, NotificationLevel.All);

        Assert.AreEqual(ChatResultCode.PermissionDenied, result.Code,
            "Public channels support only Mentions/None — All must be REJECTED, not silently clamped");

        var persisted = await _membershipRepository.Load(channel.Id, BattleTag);
        Assert.AreEqual(NotificationLevel.Mentions, persisted.NotificationLevel, "The level must be unchanged after the rejection");
    }

    [Test]
    public async Task SetNotificationLevel_All_OnSemiPublic_Ok()
    {
        var channel = await CreateChannel("semi-room", ChannelType.SemiPublic);
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");
        await hub.JoinChannel("semi-room");

        var result = await hub.SetNotificationLevel(channel.Id, NotificationLevel.All);

        Assert.AreEqual(ChatResultCode.Ok, result.Code, "SemiPublic channels support All");

        var persisted = await _membershipRepository.Load(channel.Id, BattleTag);
        Assert.AreEqual(NotificationLevel.All, persisted.NotificationLevel);
    }

    [Test]
    public async Task SetNotificationLevel_PersistsAndUpdatesRegistry()
    {
        var channel = await CreateChannel("general");
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");
        await hub.JoinChannel("general");

        var result = await hub.SetNotificationLevel(channel.Id, NotificationLevel.None);

        Assert.AreEqual(ChatResultCode.Ok, result.Code);

        var persisted = await _membershipRepository.Load(channel.Id, BattleTag);
        Assert.AreEqual(NotificationLevel.None, persisted.NotificationLevel, "The new level must be persisted to Mongo");

        var registryMember = _onlineMemberRegistry.GetMembers(channel.Id).Single();
        Assert.AreEqual(NotificationLevel.None, registryMember.NotificationLevel, "OnlineMemberRegistry must reflect the new level immediately");
    }

    [Test]
    public async Task SetNotificationLevel_NonMember_ReturnsNotMember()
    {
        var channel = await CreateChannel("general");
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");
        // Deliberately no JoinChannel call — the caller has a live session but is not a member.

        var result = await hub.SetNotificationLevel(channel.Id, NotificationLevel.None);

        Assert.AreEqual(ChatResultCode.NotMember, result.Code);
    }

    [Test]
    public async Task SetNotificationLevel_UnregisteredConnection_ReturnsPermissionDenied_FailClosed()
    {
        var hub = BuildHub("conn-ghost");

        var result = await hub.SetNotificationLevel("some-channel-id", NotificationLevel.None);

        Assert.AreEqual(ChatResultCode.PermissionDenied, result.Code);
    }

    // ---------------------------------------------------------------------------------------------
    // PR36 follow-up (D2) — notification-preference persistence across leave/rejoin. SetNotificationLevel
    // writes the pref (Public/SemiPublic only), LeaveChannel stays a hard membership delete (unchanged),
    // and JoinChannel seeds a (re)joined membership from the persisted pref when one exists. The
    // "mention stays suppressed after leaving" leg builds a REAL MentionFanOut (mirrors the
    // MentionFanOutTests.cs harness) over the SAME _membershipRepository/_notificationPreferenceRepository
    // this hub uses, so the consult path is exercised end-to-end rather than asserted only at the
    // repository layer.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task SetNotificationLevel_Public_UpsertsPreference()
    {
        var channel = await CreateChannel("general");
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");
        await hub.JoinChannel("general");

        await hub.SetNotificationLevel(channel.Id, NotificationLevel.None);

        var pref = await _notificationPreferenceRepository.Load(BattleTag, channel.Id);
        Assert.IsNotNull(pref, "an explicit set on a Public channel must persist a NotificationPreference row");
        Assert.AreEqual(NotificationLevel.None, pref.NotificationLevel);
    }

    [Test]
    public async Task SetNotificationLevel_SemiPublic_UpsertsPreference()
    {
        var channel = await CreateChannel("semi-room", ChannelType.SemiPublic);
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");
        await hub.JoinChannel("semi-room");

        await hub.SetNotificationLevel(channel.Id, NotificationLevel.All);

        var pref = await _notificationPreferenceRepository.Load(BattleTag, channel.Id);
        Assert.IsNotNull(pref, "SemiPublic is name-joinable too — the pref must persist");
        Assert.AreEqual(NotificationLevel.All, pref.NotificationLevel);
    }

    [Test]
    public async Task SetNotificationLevel_GroupDm_NoPreferenceWritten()
    {
        // ACL-governed types are not user-leavable in the room-catalog sense — the pref collection must
        // stay bounded to the room catalog (Public/SemiPublic only).
        var channel = await CreateChannel("group-1", ChannelType.GroupDm);
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");
        await DirectlyJoin(channel.Id, BattleTag);

        var result = await hub.SetNotificationLevel(channel.Id, NotificationLevel.None);

        Assert.AreEqual(ChatResultCode.Ok, result.Code, "None is a valid level on a GroupDm (no Public All-gate)");
        var pref = await _notificationPreferenceRepository.Load(BattleTag, channel.Id);
        Assert.IsNull(pref, "a GroupDm level change must NOT write a NotificationPreference row");
    }

    [Test]
    public async Task SetNotificationLevel_Public_RepeatedSets_LastWriteWins()
    {
        var channel = await CreateChannel("general");
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");
        await hub.JoinChannel("general");

        await hub.SetNotificationLevel(channel.Id, NotificationLevel.None);
        await hub.SetNotificationLevel(channel.Id, NotificationLevel.Mentions);
        await hub.SetNotificationLevel(channel.Id, NotificationLevel.None);

        var pref = await _notificationPreferenceRepository.Load(BattleTag, channel.Id);
        Assert.IsNotNull(pref);
        Assert.AreEqual(NotificationLevel.None, pref.NotificationLevel, "the LAST set must win, not accumulate duplicate rows");

        var db = MongoClient.GetDatabase(MongoDbRepositoryBase.DatabaseName);
        var rowCount = await db.GetCollection<NotificationPreference>(ChatCollections.NotificationPreferences)
            .CountDocumentsAsync(p => p.BattleTag == BattleTag.ToLowerInvariant() && p.ChannelId == channel.Id);
        Assert.AreEqual(1, rowCount, "repeated sets must upsert the SAME row, never accumulate duplicates");
    }

    [Test]
    public async Task JoinChannel_Rejoin_SeedsMembership_FromPersistedPreference_None()
    {
        var channel = await CreateChannel("general");
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");
        await hub.JoinChannel("general");
        await hub.SetNotificationLevel(channel.Id, NotificationLevel.None);
        await hub.LeaveChannel(channel.Id);

        // Between leave and rejoin: no membership row, but the pref survives independently.
        Assert.IsNull(await _membershipRepository.Load(channel.Id, BattleTag), "leave must still hard-delete the membership row");
        Assert.AreEqual(NotificationLevel.None, (await _notificationPreferenceRepository.Load(BattleTag, channel.Id)).NotificationLevel);

        var rejoin = await hub.JoinChannel("general");

        Assert.AreEqual(ChatResultCode.Ok, rejoin.Code);
        Assert.AreEqual(NotificationLevel.None, rejoin.Membership.NotificationLevel,
            "a rejoin must seed from the persisted preference, not the fresh-join Mentions default");
        var persisted = await _membershipRepository.Load(channel.Id, BattleTag);
        Assert.AreEqual(NotificationLevel.None, persisted.NotificationLevel);
        var registryMember = _onlineMemberRegistry.GetMembers(channel.Id).Single();
        Assert.AreEqual(NotificationLevel.None, registryMember.NotificationLevel,
            "OnlineMemberRegistry must be seeded with the persisted level too, not just the returned DTO");
    }

    [Test]
    public async Task JoinChannel_FirstJoin_NoPersistedPreference_DefaultsToMentions()
    {
        // Control for the rejoin test above: a genuinely first-time join (no prior SetNotificationLevel
        // for this user+channel) must still fall back to the Mentions default, not e.g. an implicit None.
        var channel = await CreateChannel("brand-new-room-2");
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");

        var result = await hub.JoinChannel("brand-new-room-2");

        Assert.AreEqual(ChatResultCode.Ok, result.Code);
        Assert.AreEqual(NotificationLevel.Mentions, result.Membership.NotificationLevel);
        Assert.IsNull(await _notificationPreferenceRepository.Load(BattleTag, channel.Id),
            "no pref was ever set — nothing should have been written by the seed-path read");
    }

    [Test]
    public async Task Lifecycle_SetNone_Leave_MentionInThatRoom_StaysSuppressed_ViaPersistedPreference()
    {
        // D2 end-to-end: join -> set None -> leave -> a THIRD party's mention in that room must still be
        // suppressed for the departed user, via the persisted pref (not membership, which is now gone).
        var channel = await CreateChannel("general");
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");
        await hub.JoinChannel("general");
        await hub.SetNotificationLevel(channel.Id, NotificationLevel.None);
        await hub.LeaveChannel(channel.Id);

        Assert.IsNull(await _membershipRepository.Load(channel.Id, BattleTag));

        // The Public non-member branch also requires directory-resolvability (follow-up spec §4) —
        // independent of the pref, mirroring MentionFanOutTests' SeedDirectory convention.
        await _userDirectory.Upsert(new UserDirectoryEntry
        {
            BattleTag = BattleTag.ToLowerInvariant(),
            DisplayBattleTag = BattleTag,
            NormalizedName = BattleTag.ToLowerInvariant(),
            LastSeenAt = DateTime.UtcNow,
        });

        var harness = new HubPushCaptureHarness();
        var fanOut = new MentionFanOut(
            harness.HubContext, _sessionRegistry, _membershipRepository, new MentionInboxRepository(MongoClient),
            _userDirectory, RelationshipProviderTestFactory.CreateIgnored(), _notificationPreferenceRepository);

        var now = DateTime.UtcNow;
        var message = new W3ChampionsChatService.Messages.ChannelMessage
        {
            ChannelId = channel.Id,
            Seq = 1,
            Sender = new W3ChampionsChatService.Messages.MessageSender { BattleTag = "someone#1", Name = "Someone" },
            Content = "hey there",
            SentAt = now,
            ExpiresAt = ExpiryCalculator.ForChannelMessage(ChannelType.Public, now),
        };
        await fanOut.NotifyAsync(channel, message, new[] { BattleTag }, now);

        var inbox = new MentionInboxRepository(MongoClient);
        Assert.That(await inbox.LoadForUser(BattleTag.ToLowerInvariant()), Is.Empty,
            "the persisted None preference keeps a mention suppressed for a room the user already left");
    }

    [Test]
    public async Task Lifecycle_SetMentions_Leave_MentionInThatRoom_StillDelivered()
    {
        // Only an explicit None suppresses — leaving with any OTHER last-set level must deliver normally.
        var channel = await CreateChannel("general");
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");
        await hub.JoinChannel("general"); // fresh-join default is already Mentions
        await hub.SetNotificationLevel(channel.Id, NotificationLevel.Mentions);
        await hub.LeaveChannel(channel.Id);

        await _userDirectory.Upsert(new UserDirectoryEntry
        {
            BattleTag = BattleTag.ToLowerInvariant(),
            DisplayBattleTag = BattleTag,
            NormalizedName = BattleTag.ToLowerInvariant(),
            LastSeenAt = DateTime.UtcNow,
        });

        var harness = new HubPushCaptureHarness();
        var fanOut = new MentionFanOut(
            harness.HubContext, _sessionRegistry, _membershipRepository, new MentionInboxRepository(MongoClient),
            _userDirectory, RelationshipProviderTestFactory.CreateIgnored(), _notificationPreferenceRepository);

        var now = DateTime.UtcNow;
        var message = new W3ChampionsChatService.Messages.ChannelMessage
        {
            ChannelId = channel.Id,
            Seq = 1,
            Sender = new W3ChampionsChatService.Messages.MessageSender { BattleTag = "someone#1", Name = "Someone" },
            Content = "hey there",
            SentAt = now,
            ExpiresAt = ExpiryCalculator.ForChannelMessage(ChannelType.Public, now),
        };
        await fanOut.NotifyAsync(channel, message, new[] { BattleTag }, now);

        var inbox = new MentionInboxRepository(MongoClient);
        Assert.That(await inbox.LoadForUser(BattleTag.ToLowerInvariant()), Has.Count.EqualTo(1),
            "a persisted level other than None must not keep suppressing a mention after leaving");
    }

    // ---------------------------------------------------------------------------------------------
    // PR36 follow-up (D2), fix round 1 (F5) — the pref upsert is a SECONDARY, best-effort write: a
    // failure there must never turn an already-applied membership level change into an error response.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task SetNotificationLevel_PrefUpsertThrows_MembershipStillAppliesAndReturnsOk()
    {
        var channel = await CreateChannel("general");
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1", new ThrowingNotificationPreferenceRepository(MongoClient));
        await hub.JoinChannel("general");

        var result = await hub.SetNotificationLevel(channel.Id, NotificationLevel.None);

        Assert.AreEqual(ChatResultCode.Ok, result.Code,
            "a failed pref upsert must not fail an already-applied membership level change");
        var persisted = await _membershipRepository.Load(channel.Id, BattleTag);
        Assert.AreEqual(NotificationLevel.None, persisted.NotificationLevel,
            "the membership update itself must still have gone through, unaffected by the pref-write failure");
    }

    // A NotificationPreferenceRepository whose Upsert always throws — simulating a Mongo write failure on
    // the secondary preference-persistence path, to prove SetNotificationLevel's best-effort posture (F5).
    private sealed class ThrowingNotificationPreferenceRepository(MongoClient client) : NotificationPreferenceRepository(client)
    {
        public override Task<NotificationPreference> Upsert(string battleTag, string channelId, NotificationLevel level, DateTime now) =>
            Task.FromException<NotificationPreference>(new InvalidOperationException("simulated NotificationPreference upsert failure"));
    }
}
