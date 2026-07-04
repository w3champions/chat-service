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
/// C5 Task 8: the group-mutation surface — <c>AddGroupMember</c>/<c>RemoveGroupMember</c>/<c>PromoteOwner</c>/
/// <c>RenameGroup</c> plus the <c>LeaveChannel</c> extension (group departure: auto-promotion,
/// empty-deletion, forced-removal push). The full egalitarian owner-set machine (D12): adds are ANY-member +
/// adder's-own-friends; removes/promotes/renames are owner-only; a <see cref="MembershipRole.Owner"/> target
/// is untouchable (incl. self — the pinned anti-coup wall); the last owner leaving auto-promotes the
/// longest-standing member; the last member leaving deletes the channel doc + residual memberships. Dm/GroupDm
/// are excluded from the viewer-roster system (D11) — forced removals never surface a <c>ViewersChanged</c>.
/// <para>
/// Direct-hub idiom mirroring <see cref="ChatHubGroupCreateTests"/> + <see cref="ChatHubDmFocusTests"/>: a
/// REAL <see cref="FanOutEngine"/> wired to a <see cref="HubPushCaptureHarness"/> (captures ChannelAdded/
/// ChannelRemoved), a REAL <see cref="ViewersAccumulator"/> sharing the hubs' <see cref="FocusRegistry"/> (so
/// a stray <c>ViewersChanged</c> is directly observable after a flush), a real
/// <see cref="RelationshipProvider"/> over a <see cref="FakeRelationshipSource"/> (NEVER HTTP), and a
/// <see cref="FakeTimeProvider"/>. NUnit constraint style.
/// </para>
/// </summary>
public class ChatHubGroupManagementTests : IntegrationTestBase
{
    private static readonly DateTimeOffset FixedNow = new(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Flush = ChatLimits.ViewersChangedFlush;

    private FakeTimeProvider _time;
    private HubPushCaptureHarness _harness;
    private ViewersAccumulator _accumulator;
    private FocusRegistry _focusRegistry;
    private OnlineMemberRegistry _onlineMemberRegistry;
    private SessionRegistry _sessionRegistry;
    private ConnectionMapping _connectionMapping;
    private UserDirectoryRepository _userDirectory;
    private MuteRepository _muteRepository;
    private MuteReconciliationService _reconcileService;
    private TicketStore _ticketStore;
    private ChannelRepository _channelRepository;
    private MembershipRepository _membershipRepository;
    private MessageRepository _messageRepository;
    private MessageRateLimiter _messageRateLimiter;
    private ChannelCreationRateLimiter _channelCreationRateLimiter;
    private SessionStateAssembler _assembler;
    private FanOutEngine _fanOutEngine;
    private ActivityCoalescer _coalescer;
    private UserSettingsRepository _userSettings;
    private DmInitiationTracker _dmInitiationTracker;
    private FakeRelationshipSource _relationshipSource;
    private RelationshipProvider _relationshipProvider;
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
        _muteRepository = new MuteRepository(MongoClient);
        _reconcileService = new MuteReconciliationTestHarness(_connectionMapping, _muteRepository).Service;
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

        // A REAL FanOutEngine + a REAL ViewersAccumulator sharing the hubs' registries, both wired to one
        // capture harness so ChannelAdded/ChannelRemoved pushes AND any stray ViewersChanged are observable.
        _harness = new HubPushCaptureHarness();
        _accumulator = new ViewersAccumulator(_harness.HubContext, _focusRegistry);
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
            _muteRepository,
            _onlineMemberRegistry,
            _connectionMapping,
            new MentionInboxRepository(MongoClient));
    }

    private ChatHub BuildHub(string connectionId)
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
            _time,
            _channelRepository,
            _membershipRepository,
            _channelCreationRateLimiter,
            _messageRepository,
            _fanOutEngine,
            _accumulator,
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

    // Inserts a GroupDm channel doc directly (bypassing CreateGroup's [3,100] size floor so leave/whittle
    // scenarios can seed arbitrary member counts), returns it.
    private async Task<ChatChannel> SeedGroupChannel(string name = "Squad")
    {
        var channel = new ChatChannel
        {
            Type = ChannelType.GroupDm,
            Name = name,
            LastSeq = 0,
            LastMessageAt = Now,
        };
        channel.ExpiresAt = ExpiryCalculator.ForChannelShell(channel, Now);
        await _channelRepository.Insert(channel);
        return channel;
    }

    private Task SeedMembership(string channelId, string battleTag, MembershipRole role, DateTime joinedAt) =>
        _membershipRepository.Insert(new ChannelMembership
        {
            ChannelId = channelId,
            BattleTag = battleTag,
            Role = role,
            NotificationLevel = NotificationLevel.All,
            JoinedAt = joinedAt,
        });

    // Registers a live session AND seeds the OnlineMemberRegistry entry (carrying the ChannelType) exactly
    // as SessionStateAssembler/PushChannelAdded would — the zero-DB "IS a member" signal the hub reads.
    private void PutOnline(string connectionId, string channelId, string battleTag, ChannelType type = ChannelType.GroupDm)
    {
        RegisterSession(connectionId, battleTag);
        _onlineMemberRegistry.Join(channelId, connectionId, new MemberState(battleTag, NotificationLevel.All, 0, type));
    }

    // ================================================================================================
    // Group 1 — AddGroupMember (any-member + adder's-own-friends; no block check, D14)
    // ================================================================================================

    [Test]
    public async Task Add_ByAnyMember_OwnFriend_Ok()
    {
        // An ORDINARY member (not the owner) adds their OWN friend — any member may add (D12).
        var channel = await SeedGroupChannel();
        await SeedMembership(channel.Id, "owner#1", MembershipRole.Owner, Now);
        await SeedMembership(channel.Id, "member#2", MembershipRole.Member, Now);
        PutOnline("conn-owner", channel.Id, "owner#1");
        PutOnline("conn-member", channel.Id, "member#2");
        SetFriends("member#2", "newbie#3");
        RegisterSession("conn-newbie", "newbie#3"); // online so the ChannelAdded push is observable

        var result = await BuildHub("conn-member").AddGroupMember(channel.Id, "newbie#3");

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok), "any member may add their own friend");
        var added = await _membershipRepository.Load(channel.Id, "newbie#3");
        Assert.That(added, Is.Not.Null, "the added friend gets a membership row");
        Assert.That(added.Role, Is.EqualTo(MembershipRole.Member), "an add always joins as an ordinary Member");
        Assert.That(added.NotificationLevel, Is.EqualTo(NotificationLevel.All));
        Assert.That(added.JoinedAt, Is.EqualTo(Now));

        var push = _harness.PayloadFor("conn-newbie", ChatEvents.ChannelAdded) as ChannelAddedDto;
        Assert.That(push, Is.Not.Null, "the added member receives a ChannelAdded push");
        Assert.That(push.Focus, Is.False, "no-auto-open: the add never auto-focuses");
        Assert.That(_onlineMemberRegistry.IsMember("conn-newbie", channel.Id), Is.True, "the added member's registry is seeded");
    }

    [Test]
    public async Task Add_NonFriend_PermissionDenied()
    {
        var channel = await SeedGroupChannel();
        await SeedMembership(channel.Id, "owner#1", MembershipRole.Owner, Now);
        PutOnline("conn-owner", channel.Id, "owner#1");
        SetFriends("owner#1", "someoneelse#5"); // "stranger#9" is deliberately NOT a friend

        var result = await BuildHub("conn-owner").AddGroupMember(channel.Id, "stranger#9");

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.PermissionDenied), "the adder can only add their OWN friends");
        Assert.That(await _membershipRepository.Load(channel.Id, "stranger#9"), Is.Null, "nothing persisted on a friends-gate reject");
    }

    [Test]
    public async Task Add_At100_PermissionDenied()
    {
        var channel = await SeedGroupChannel();
        await SeedMembership(channel.Id, "owner#1", MembershipRole.Owner, Now);
        for (var i = 1; i < ChatLimits.MaxGroupSize; i++) // 99 more members => 100 total
        {
            await SeedMembership(channel.Id, $"m{i}#{i}", MembershipRole.Member, Now);
        }
        PutOnline("conn-owner", channel.Id, "owner#1");
        SetFriends("owner#1", "overflow#101"); // a genuine friend — the size cap must reject regardless

        var result = await BuildHub("conn-owner").AddGroupMember(channel.Id, "overflow#101");

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.PermissionDenied), "a group already at MaxGroupSize rejects further adds");
        Assert.That(await _membershipRepository.CountForChannel(channel.Id), Is.EqualTo(ChatLimits.MaxGroupSize), "no 101st member persisted");
    }

    [Test]
    public async Task Add_Duplicate_IdempotentOk()
    {
        var channel = await SeedGroupChannel();
        await SeedMembership(channel.Id, "owner#1", MembershipRole.Owner, Now);
        await SeedMembership(channel.Id, "member#2", MembershipRole.Member, Now);
        PutOnline("conn-owner", channel.Id, "owner#1");
        // No friends set — the duplicate short-circuit MUST fire BEFORE the friends gate (pinned order), so
        // re-adding an existing member is Ok even though member#2 is not (currently) an owner-friend.

        var result = await BuildHub("conn-owner").AddGroupMember(channel.Id, "member#2");

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok), "re-adding an existing member is an idempotent Ok");
        Assert.That(await _membershipRepository.CountForChannel(channel.Id), Is.EqualTo(2), "no duplicate membership row inserted");
        Assert.That(_harness.AllSignals.Any(s => s.Method == ChatEvents.ChannelAdded), Is.False, "no ChannelAdded push on an idempotent re-add");
    }

    [Test]
    public async Task Add_WbDown_ThrottledRetriable()
    {
        var channel = await SeedGroupChannel();
        await SeedMembership(channel.Id, "owner#1", MembershipRole.Owner, Now);
        PutOnline("conn-owner", channel.Id, "owner#1");
        _relationshipSource.ShouldThrow = true; // no cache warmed => the friends gate fails closed

        var result = await BuildHub("conn-owner").AddGroupMember(channel.Id, "newbie#3");

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Throttled), "the add friends-gate fails closed (retriable) when the relationship view is unavailable");
        Assert.That(result.RetryAfterSeconds, Is.EqualTo(ChatLimits.RelationshipRetryAfterSeconds));
        Assert.That(await _membershipRepository.Load(channel.Id, "newbie#3"), Is.Null, "nothing persisted");
    }

    [Test]
    public async Task Add_ByNonMember_PermissionDenied()
    {
        var channel = await SeedGroupChannel();
        await SeedMembership(channel.Id, "owner#1", MembershipRole.Owner, Now);
        RegisterSession("conn-outsider", "outsider#9"); // registered but NOT a group member (no registry seed)
        SetFriends("outsider#9", "target#3");

        var result = await BuildHub("conn-outsider").AddGroupMember(channel.Id, "target#3");

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.PermissionDenied), "a non-member cannot add to a group");
        Assert.That(await _membershipRepository.Load(channel.Id, "target#3"), Is.Null);
    }

    [Test]
    public async Task Add_NullOrWhitespaceTarget_HubException()
    {
        var channel = await SeedGroupChannel();
        await SeedMembership(channel.Id, "owner#1", MembershipRole.Owner, Now);
        PutOnline("conn-owner", channel.Id, "owner#1");
        var hub = BuildHub("conn-owner");

        Assert.That(async () => await hub.AddGroupMember(channel.Id, null), Throws.TypeOf<HubException>());
        Assert.That(async () => await hub.AddGroupMember(channel.Id, "   "), Throws.TypeOf<HubException>());
    }

    // ================================================================================================
    // Group 2 — RemoveGroupMember (owner-only forced removal; the anti-coup owner wall)
    // ================================================================================================

    [Test]
    public async Task Remove_OrdinaryMember_ByOwner_Ok_TargetGetsChannelRemoved_MembershipGone()
    {
        var channel = await SeedGroupChannel();
        await SeedMembership(channel.Id, "owner#1", MembershipRole.Owner, Now);
        await SeedMembership(channel.Id, "member#2", MembershipRole.Member, Now);
        await SeedMembership(channel.Id, "member#3", MembershipRole.Member, Now);
        PutOnline("conn-owner", channel.Id, "owner#1");
        PutOnline("conn-m2", channel.Id, "member#2");
        PutOnline("conn-m3", channel.Id, "member#3");

        var result = await BuildHub("conn-owner").RemoveGroupMember(channel.Id, "member#2");

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(await _membershipRepository.Load(channel.Id, "member#2"), Is.Null, "the removed member's row is gone");
        Assert.That(_harness.SignalCount("conn-m2", ChatEvents.ChannelRemoved), Is.EqualTo(1), "the removed member receives ChannelRemoved");
        Assert.That(_onlineMemberRegistry.IsMember("conn-m2", channel.Id), Is.False, "the removed member's registry entry is cleaned");
        // D11: NO ViewersChanged for a forced removal on a private lane.
        Assert.That(_harness.SignalCount("conn-owner", ChatEvents.ViewersChanged), Is.EqualTo(0));
        Assert.That(_harness.SignalCount("conn-m3", ChatEvents.ViewersChanged), Is.EqualTo(0));
    }

    [Test]
    public async Task Remove_ByNonOwner_PermissionDenied()
    {
        var channel = await SeedGroupChannel();
        await SeedMembership(channel.Id, "owner#1", MembershipRole.Owner, Now);
        await SeedMembership(channel.Id, "member#2", MembershipRole.Member, Now);
        await SeedMembership(channel.Id, "member#3", MembershipRole.Member, Now);
        PutOnline("conn-m2", channel.Id, "member#2");

        // member#2 (non-owner) tries to remove an EXISTING member and a NON-EXISTENT target — both must be
        // an indistinguishable PermissionDenied (a non-owner gets no target-existence oracle).
        var existing = await BuildHub("conn-m2").RemoveGroupMember(channel.Id, "member#3");
        var absent = await BuildHub("conn-m2").RemoveGroupMember(channel.Id, "ghost#9");

        Assert.That(existing.Code, Is.EqualTo(ChatResultCode.PermissionDenied));
        Assert.That(absent.Code, Is.EqualTo(ChatResultCode.PermissionDenied));
        Assert.That(existing.Code, Is.EqualTo(absent.Code), "a non-owner cannot distinguish an existing vs absent target — same code either way");
        Assert.That(await _membershipRepository.Load(channel.Id, "member#3"), Is.Not.Null, "the existing target survives a non-owner remove attempt");
    }

    [Test]
    public async Task Remove_OwnerTarget_PermissionDenied()
    {
        // The pinned anti-coup wall: an owner can never remove ANOTHER owner.
        var channel = await SeedGroupChannel();
        await SeedMembership(channel.Id, "owner#1", MembershipRole.Owner, Now);
        await SeedMembership(channel.Id, "owner#2", MembershipRole.Owner, Now);
        await SeedMembership(channel.Id, "member#3", MembershipRole.Member, Now);
        PutOnline("conn-o1", channel.Id, "owner#1");

        var result = await BuildHub("conn-o1").RemoveGroupMember(channel.Id, "owner#2");

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.PermissionDenied), "owners cannot remove owners (anti-coup wall)");
        var survivor = await _membershipRepository.Load(channel.Id, "owner#2");
        Assert.That(survivor, Is.Not.Null, "the owner target's membership SURVIVES");
        Assert.That(survivor.Role, Is.EqualTo(MembershipRole.Owner), "and keeps its Owner role");
    }

    [Test]
    public async Task Remove_SelfOwner_PermissionDenied()
    {
        // The wall INCLUDES self: an owner cannot remove themselves via RemoveGroupMember (they exit via
        // LeaveChannel, which auto-promotes if they were the last owner).
        var channel = await SeedGroupChannel();
        await SeedMembership(channel.Id, "owner#1", MembershipRole.Owner, Now);
        await SeedMembership(channel.Id, "member#2", MembershipRole.Member, Now);
        await SeedMembership(channel.Id, "member#3", MembershipRole.Member, Now);
        PutOnline("conn-o1", channel.Id, "owner#1");

        var result = await BuildHub("conn-o1").RemoveGroupMember(channel.Id, "owner#1");

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.PermissionDenied), "an owner cannot self-remove via RemoveGroupMember");
        var self = await _membershipRepository.Load(channel.Id, "owner#1");
        Assert.That(self, Is.Not.Null, "the self-owner target's membership SURVIVES");
        Assert.That(self.Role, Is.EqualTo(MembershipRole.Owner));
    }

    // ================================================================================================
    // Group 3 — PromoteOwner (owner-only; additive; idempotent)
    // ================================================================================================

    [Test]
    public async Task Promote_MemberToOwner_ByOwner_Ok()
    {
        var channel = await SeedGroupChannel();
        await SeedMembership(channel.Id, "owner#1", MembershipRole.Owner, Now);
        await SeedMembership(channel.Id, "member#2", MembershipRole.Member, Now);
        PutOnline("conn-o1", channel.Id, "owner#1");

        var result = await BuildHub("conn-o1").PromoteOwner(channel.Id, "member#2");

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That((await _membershipRepository.Load(channel.Id, "member#2")).Role, Is.EqualTo(MembershipRole.Owner), "the target is now a co-owner");
        Assert.That((await _membershipRepository.Load(channel.Id, "owner#1")).Role, Is.EqualTo(MembershipRole.Owner), "promotion is additive — the promoter stays an owner (egalitarian owner-set)");
    }

    [Test]
    public async Task Promote_ByNonOwner_PermissionDenied()
    {
        var channel = await SeedGroupChannel();
        await SeedMembership(channel.Id, "owner#1", MembershipRole.Owner, Now);
        await SeedMembership(channel.Id, "member#2", MembershipRole.Member, Now);
        await SeedMembership(channel.Id, "member#3", MembershipRole.Member, Now);
        PutOnline("conn-m2", channel.Id, "member#2");

        var result = await BuildHub("conn-m2").PromoteOwner(channel.Id, "member#3");

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.PermissionDenied), "only an owner may promote");
        Assert.That((await _membershipRepository.Load(channel.Id, "member#3")).Role, Is.EqualTo(MembershipRole.Member), "the target is not promoted");
    }

    [Test]
    public async Task Promote_AlreadyOwner_IdempotentOk()
    {
        var channel = await SeedGroupChannel();
        await SeedMembership(channel.Id, "owner#1", MembershipRole.Owner, Now);
        await SeedMembership(channel.Id, "owner#2", MembershipRole.Owner, Now);
        PutOnline("conn-o1", channel.Id, "owner#1");

        var result = await BuildHub("conn-o1").PromoteOwner(channel.Id, "owner#2");

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok), "promoting an already-owner is an idempotent Ok");
        Assert.That((await _membershipRepository.Load(channel.Id, "owner#2")).Role, Is.EqualTo(MembershipRole.Owner));
    }

    // ================================================================================================
    // Group 4 — RenameGroup (owner-only; T7 name rules; NormalizedName never set, D16)
    // ================================================================================================

    [Test]
    public async Task Rename_ByOwner_Ok_NoNormalizedName()
    {
        var channel = await SeedGroupChannel();
        await SeedMembership(channel.Id, "owner#1", MembershipRole.Owner, Now);
        PutOnline("conn-o1", channel.Id, "owner#1");

        var result = await BuildHub("conn-o1").RenameGroup(channel.Id, "  New Name  ");

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        var reloaded = await _channelRepository.Load(channel.Id);
        Assert.That(reloaded.Name, Is.EqualTo("New Name"), "the name is trimmed and set");
        Assert.That(reloaded.NormalizedName, Is.Null, "D16: a group's NormalizedName is NEVER set on rename");
    }

    [Test]
    public async Task Rename_ByMember_PermissionDenied()
    {
        var channel = await SeedGroupChannel();
        await SeedMembership(channel.Id, "owner#1", MembershipRole.Owner, Now);
        await SeedMembership(channel.Id, "member#2", MembershipRole.Member, Now);
        PutOnline("conn-m2", channel.Id, "member#2");

        var result = await BuildHub("conn-m2").RenameGroup(channel.Id, "Hacked");

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.PermissionDenied), "only an owner may rename");
        Assert.That((await _channelRepository.Load(channel.Id)).Name, Is.EqualTo("Squad"), "the name is unchanged");
    }

    [Test]
    public async Task Rename_TooLong_TooLong()
    {
        var channel = await SeedGroupChannel();
        await SeedMembership(channel.Id, "owner#1", MembershipRole.Owner, Now);
        PutOnline("conn-o1", channel.Id, "owner#1");

        var over = new string('a', ChatLimits.GroupNameMaxLength + 1);
        var result = await BuildHub("conn-o1").RenameGroup(channel.Id, over);

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.TooLong));
        Assert.That((await _channelRepository.Load(channel.Id)).Name, Is.EqualTo("Squad"), "an over-length rename does not change the name");
    }

    [Test]
    public async Task Rename_EmptyAfterTrim_TooLong()
    {
        var channel = await SeedGroupChannel();
        await SeedMembership(channel.Id, "owner#1", MembershipRole.Owner, Now);
        PutOnline("conn-o1", channel.Id, "owner#1");

        var result = await BuildHub("conn-o1").RenameGroup(channel.Id, "   ");

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.TooLong), "an empty-after-trim name maps to TooLong (T7 rule)");
    }

    // ================================================================================================
    // Group 5 — last-owner-leaves auto-promotion
    // ================================================================================================

    [Test]
    public async Task LastOwnerLeaves_LongestStandingMemberAutoPromoted()
    {
        // The sole owner leaves a group with three ordinary members at staggered JoinedAt — the longest-
        // standing (earliest JoinedAt) is auto-promoted to Owner.
        var channel = await SeedGroupChannel();
        var t0 = Now;
        await SeedMembership(channel.Id, "owner#1", MembershipRole.Owner, t0);
        await SeedMembership(channel.Id, "early#2", MembershipRole.Member, t0.AddMinutes(1));
        await SeedMembership(channel.Id, "mid#3", MembershipRole.Member, t0.AddMinutes(2));
        await SeedMembership(channel.Id, "late#4", MembershipRole.Member, t0.AddMinutes(3));
        PutOnline("conn-o1", channel.Id, "owner#1");

        var result = await BuildHub("conn-o1").LeaveChannel(channel.Id);

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(await _membershipRepository.Load(channel.Id, "owner#1"), Is.Null, "the sole owner's membership is gone after leaving");
        Assert.That((await _membershipRepository.Load(channel.Id, "early#2")).Role, Is.EqualTo(MembershipRole.Owner), "the longest-standing remaining member is auto-promoted");
        Assert.That((await _membershipRepository.Load(channel.Id, "mid#3")).Role, Is.EqualTo(MembershipRole.Member));
        Assert.That((await _membershipRepository.Load(channel.Id, "late#4")).Role, Is.EqualTo(MembershipRole.Member));
        Assert.That((await _membershipRepository.LoadForChannel(channel.Id)).Count(m => m.Role == MembershipRole.Owner), Is.EqualTo(1), "exactly one owner after auto-promotion");
    }

    [Test]
    public async Task AutoPromotion_TieBreak_OrdinalBattleTag()
    {
        // Two remaining members with IDENTICAL JoinedAt — the tie breaks by string.CompareOrdinal(battleTag),
        // lowest wins ("alice#2" < "zoe#3" ordinally, over the already-lowercased stored tags). zoe is
        // inserted FIRST, so a wrong (insertion-order) tie-break would pick zoe — this test discriminates.
        var channel = await SeedGroupChannel();
        var tie = Now.AddMinutes(5);
        await SeedMembership(channel.Id, "owner#1", MembershipRole.Owner, Now);
        await SeedMembership(channel.Id, "zoe#3", MembershipRole.Member, tie);
        await SeedMembership(channel.Id, "alice#2", MembershipRole.Member, tie);
        PutOnline("conn-o1", channel.Id, "owner#1");

        await BuildHub("conn-o1").LeaveChannel(channel.Id);

        Assert.That((await _membershipRepository.Load(channel.Id, "alice#2")).Role, Is.EqualTo(MembershipRole.Owner), "on a JoinedAt tie, the lowest CompareOrdinal battleTag is promoted");
        Assert.That((await _membershipRepository.Load(channel.Id, "zoe#3")).Role, Is.EqualTo(MembershipRole.Member));
    }

    // ================================================================================================
    // Group 6 — last-member-leaves empty-deletion
    // ================================================================================================

    [Test]
    public async Task LastMemberLeaves_ChannelAndMembershipsDeleted_MessagesLeftToTtl()
    {
        var channel = await SeedGroupChannel();
        await SeedMembership(channel.Id, "loner#1", MembershipRole.Owner, Now);
        PutOnline("conn-loner", channel.Id, "loner#1");
        // A message in the group's history — it must SURVIVE (physical removal is TTL-only, never a hard delete).
        var message = new ChannelMessage
        {
            ChannelId = channel.Id,
            Seq = 1,
            Sender = new MessageSender { BattleTag = "loner#1", Name = "loner" },
            Content = "anyone here?",
            SentAt = Now,
        };
        await _messageRepository.Insert(message);

        var result = await BuildHub("conn-loner").LeaveChannel(channel.Id);

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(await _channelRepository.Load(channel.Id), Is.Null, "the last member leaving deletes the channel doc");
        Assert.That(await _membershipRepository.LoadForChannel(channel.Id), Is.Empty, "residual membership rows are removed with the channel");
        Assert.That(await _messageRepository.Load(message.Id), Is.Not.Null, "messages are left to the 90d TTL — never hard-deleted");
    }

    // ================================================================================================
    // Group 7 — a remaining owner suppresses auto-promotion
    // ================================================================================================

    [Test]
    public async Task OwnerLeaves_OtherOwnerRemains_NoAutoPromotion()
    {
        var channel = await SeedGroupChannel();
        await SeedMembership(channel.Id, "owner#1", MembershipRole.Owner, Now);
        await SeedMembership(channel.Id, "owner#2", MembershipRole.Owner, Now.AddMinutes(1));
        await SeedMembership(channel.Id, "member#3", MembershipRole.Member, Now.AddMinutes(2));
        PutOnline("conn-o1", channel.Id, "owner#1");

        await BuildHub("conn-o1").LeaveChannel(channel.Id);

        Assert.That(await _membershipRepository.Load(channel.Id, "owner#1"), Is.Null, "the leaving owner's row is gone");
        Assert.That((await _membershipRepository.Load(channel.Id, "owner#2")).Role, Is.EqualTo(MembershipRole.Owner), "the remaining owner keeps ownership");
        Assert.That((await _membershipRepository.Load(channel.Id, "member#3")).Role, Is.EqualTo(MembershipRole.Member), "no auto-promotion while an owner still remains");
    }

    // ================================================================================================
    // Group 8 — LeaveChannel regressions (public unchanged; DM shell survives)
    // ================================================================================================

    [Test]
    public async Task LeavePublicChannel_BehaviorUnchanged()
    {
        // Regression pin: leaving a PUBLIC channel still routes the roster change through the accumulator
        // (RecordChange BEFORE Unfocus) so a remaining focused viewer receives a `left` batch.
        var pub = new ChatChannel { Type = ChannelType.Public, Name = "general", NormalizedName = "general" };
        await _channelRepository.Insert(pub);
        PutOnline("conn-leaver", pub.Id, "leaver#1", ChannelType.Public);
        PutOnline("conn-observer", pub.Id, "observer#2", ChannelType.Public);
        await _membershipRepository.Insert(new ChannelMembership { ChannelId = pub.Id, BattleTag = "leaver#1", NotificationLevel = NotificationLevel.All, JoinedAt = Now });

        var observerHub = BuildHub("conn-observer");
        var leaverHub = BuildHub("conn-leaver");
        await observerHub.FocusChannel(pub.Id);
        await leaverHub.FocusChannel(pub.Id);
        // Flush so the leaver is an ESTABLISHED viewer at the next window's baseline.
        await _accumulator.FlushDue(Now + Flush);
        var before = _harness.SignalCount("conn-observer", ChatEvents.ViewersChanged);

        var result = await leaverHub.LeaveChannel(pub.Id);
        await _accumulator.FlushDue(Now + Flush + Flush);

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(await _membershipRepository.Load(pub.Id, "leaver#1"), Is.Null, "membership deleted");
        Assert.That(_harness.SignalCount("conn-observer", ChatEvents.ViewersChanged), Is.EqualTo(before + 1),
            "the remaining viewer receives exactly one ViewersChanged batch for the public leave — RecordChange ordering preserved");
        Assert.That(_focusRegistry.GetFocusedChannels("conn-leaver"), Is.Empty, "the leaver is unfocused");
    }

    [Test]
    public async Task LeaveDm_MembershipGone_ShellSurvives_ReopenByPairKeyRestores()
    {
        // A friend DM (born Accepted). The leaver deletes only their membership; the conversation SHELL
        // (channel doc) is UNTOUCHED, so re-opening by pair-key resurrects the SAME conversation. D11: a DM
        // leave never enters the ViewersAccumulator.
        const string me = "me#1";
        const string other = "other#2";
        SetFriends(me, other); // friends => OpenDm born Accepted (friend path)
        RegisterSession("conn-me", me);
        var opened = await BuildHub("conn-me").OpenDm(other);
        Assert.That(opened.Code, Is.EqualTo(ChatResultCode.Ok));
        var channelId = opened.Channel.Id;
        Assert.That(await _membershipRepository.Load(channelId, me), Is.Not.Null);

        var leave = await BuildHub("conn-me").LeaveChannel(channelId);

        Assert.That(leave.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(await _membershipRepository.Load(channelId, me), Is.Null, "the leaver's DM membership is deleted");
        Assert.That(await _channelRepository.Load(channelId), Is.Not.Null, "the DM conversation SHELL survives (never deleted on leave)");
        Assert.That(_accumulator.PendingChangeCount(channelId), Is.EqualTo(0), "a DM leave never records into the ViewersAccumulator (D11)");

        // Re-open by pair-key: the surviving shell is found and the membership is restored (same channel id).
        var reopened = await BuildHub("conn-me").OpenDm(other);
        Assert.That(reopened.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(reopened.Channel.Id, Is.EqualTo(channelId), "re-opening resurrects the SAME conversation via its pair-key");
        Assert.That(await _membershipRepository.Load(channelId, me), Is.Not.Null, "the membership is restored on re-open");
    }

    // ================================================================================================
    // Group 9 — forced removal of a focused member: registry/focus cleaned, no stray ViewersChanged (D11)
    // ================================================================================================

    [Test]
    public async Task ForcedRemoval_OfFocusedMember_CleansRegistryAndFocus_NoStrayViewersChanged()
    {
        var channel = await SeedGroupChannel();
        await SeedMembership(channel.Id, "owner#1", MembershipRole.Owner, Now);
        await SeedMembership(channel.Id, "victim#2", MembershipRole.Member, Now);
        await SeedMembership(channel.Id, "bystander#3", MembershipRole.Member, Now);
        PutOnline("conn-owner", channel.Id, "owner#1");
        PutOnline("conn-victim", channel.Id, "victim#2");
        PutOnline("conn-bystander", channel.Id, "bystander#3");

        // Everyone focuses the group (a GroupDm focus returns empty viewers + never records — T5/D11).
        await BuildHub("conn-owner").FocusChannel(channel.Id);
        await BuildHub("conn-victim").FocusChannel(channel.Id);
        await BuildHub("conn-bystander").FocusChannel(channel.Id);

        var result = await BuildHub("conn-owner").RemoveGroupMember(channel.Id, "victim#2");
        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));

        // Flush the accumulator across two windows — a forced private-lane removal must surface NO ViewersChanged.
        await _accumulator.FlushDue(Now + Flush);
        await _accumulator.FlushDue(Now + Flush + Flush);

        Assert.That(_onlineMemberRegistry.IsMember("conn-victim", channel.Id), Is.False, "the removed member's registry entry is cleaned");
        Assert.That(_focusRegistry.GetFocusedChannels("conn-victim"), Is.Empty, "the removed member's focus entry is cleaned");
        Assert.That(_harness.SignalCount("conn-victim", ChatEvents.ChannelRemoved), Is.EqualTo(1), "the removed member is told to drop the channel");

        Assert.That(_harness.SignalCount("conn-owner", ChatEvents.ViewersChanged), Is.EqualTo(0), "no remaining member receives ViewersChanged for a private-lane removal (D11)");
        Assert.That(_harness.SignalCount("conn-bystander", ChatEvents.ViewersChanged), Is.EqualTo(0));
        Assert.That(_harness.SignalCount("conn-victim", ChatEvents.ViewersChanged), Is.EqualTo(0));
        Assert.That(_accumulator.PendingChangeCount(channel.Id), Is.EqualTo(0), "a private-lane group never enters the ViewersAccumulator at all");
    }

    // ================================================================================================
    // Cross-cutting guards (missing channel / non-GroupDm / fail-closed session)
    // ================================================================================================

    [Test]
    public async Task GroupOps_MissingChannel_NotFound()
    {
        RegisterSession("conn-o1", "owner#1");
        var hub = BuildHub("conn-o1");

        Assert.That((await hub.AddGroupMember("nonexistent", "x#1")).Code, Is.EqualTo(ChatResultCode.NotFound));
        Assert.That((await hub.RemoveGroupMember("nonexistent", "x#1")).Code, Is.EqualTo(ChatResultCode.NotFound));
        Assert.That((await hub.PromoteOwner("nonexistent", "x#1")).Code, Is.EqualTo(ChatResultCode.NotFound));
        Assert.That((await hub.RenameGroup("nonexistent", "New")).Code, Is.EqualTo(ChatResultCode.NotFound));
    }

    [Test]
    public async Task GroupOps_NotAGroupDm_PermissionDenied()
    {
        // A non-GroupDm channel (Public) rejects every group op with PermissionDenied — guards Public/
        // SemiPublic/Dm/System from the group-mutation surface.
        var pub = new ChatChannel { Type = ChannelType.Public, Name = "general", NormalizedName = "general" };
        await _channelRepository.Insert(pub);
        await _membershipRepository.Insert(new ChannelMembership { ChannelId = pub.Id, BattleTag = "owner#1", Role = MembershipRole.Owner, NotificationLevel = NotificationLevel.All, JoinedAt = Now });
        PutOnline("conn-o1", pub.Id, "owner#1", ChannelType.Public);
        SetFriends("owner#1", "x#3");
        var hub = BuildHub("conn-o1");

        Assert.That((await hub.AddGroupMember(pub.Id, "x#3")).Code, Is.EqualTo(ChatResultCode.PermissionDenied));
        Assert.That((await hub.RemoveGroupMember(pub.Id, "owner#1")).Code, Is.EqualTo(ChatResultCode.PermissionDenied));
        Assert.That((await hub.PromoteOwner(pub.Id, "owner#1")).Code, Is.EqualTo(ChatResultCode.PermissionDenied));
        Assert.That((await hub.RenameGroup(pub.Id, "New")).Code, Is.EqualTo(ChatResultCode.PermissionDenied));
    }

    [Test]
    public async Task GroupOps_NoSession_FailClosed_PermissionDenied()
    {
        var hub = BuildHub("conn-ghost"); // no session registered

        Assert.That((await hub.AddGroupMember("c", "x#1")).Code, Is.EqualTo(ChatResultCode.PermissionDenied));
        Assert.That((await hub.RemoveGroupMember("c", "x#1")).Code, Is.EqualTo(ChatResultCode.PermissionDenied));
        Assert.That((await hub.PromoteOwner("c", "x#1")).Code, Is.EqualTo(ChatResultCode.PermissionDenied));
        Assert.That((await hub.RenameGroup("c", "New")).Code, Is.EqualTo(ChatResultCode.PermissionDenied));
    }

    // ================================================================================================
    // Group 10 — post-review hardening fences (owner-oracle ordering; stale-snapshot; push fault isolation)
    // ================================================================================================

    [Test]
    public async Task Remove_AbsentTarget_ByOwner_NotFound()
    {
        // An OWNER clears the owner gate, then the target-existence check fires: removing a battleTag that
        // is NOT a member ⇒ NotFound. Locks the owner-passes-then-target-missing branch (only an owner ever
        // reaches this existence signal — that is WHY the owner gate precedes it).
        var channel = await SeedGroupChannel();
        await SeedMembership(channel.Id, "owner#1", MembershipRole.Owner, Now);
        PutOnline("conn-owner", channel.Id, "owner#1");

        var result = await BuildHub("conn-owner").RemoveGroupMember(channel.Id, "ghost#9");

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.NotFound), "an owner removing a non-member reaches the existence check and gets NotFound");
    }

    [Test]
    public async Task Promote_AbsentTarget_ByOwner_NotFound()
    {
        // Mirror of Remove: an OWNER promoting a non-member reaches the target-existence check ⇒ NotFound.
        var channel = await SeedGroupChannel();
        await SeedMembership(channel.Id, "owner#1", MembershipRole.Owner, Now);
        PutOnline("conn-owner", channel.Id, "owner#1");

        var result = await BuildHub("conn-owner").PromoteOwner(channel.Id, "ghost#9");

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.NotFound), "an owner promoting a non-member reaches the existence check and gets NotFound");
    }

    [Test]
    public async Task Remove_ByNonOwner_NullTarget_PermissionDenied()
    {
        // The anti-oracle wall: a NON-owner passing a null/whitespace target gets PermissionDenied (NOT a
        // HubException) because the caller-owner gate PRECEDES the null-target HubException guard. A non-owner
        // must never get a DIFFERENT signal for a null vs a real target — that asymmetry would itself leak
        // that the owner check ran. RED-verify: hoisting the null-guard above the owner gate flips this to
        // HubException (confirmed during hardening, then reverted).
        var channel = await SeedGroupChannel();
        await SeedMembership(channel.Id, "owner#1", MembershipRole.Owner, Now);
        await SeedMembership(channel.Id, "member#2", MembershipRole.Member, Now);
        PutOnline("conn-m2", channel.Id, "member#2");
        var hub = BuildHub("conn-m2");

        Assert.That((await hub.RemoveGroupMember(channel.Id, null)).Code, Is.EqualTo(ChatResultCode.PermissionDenied), "a non-owner with a null target is denied at the owner gate, never reaching the null-target HubException");
        Assert.That((await hub.RemoveGroupMember(channel.Id, "   ")).Code, Is.EqualTo(ChatResultCode.PermissionDenied), "same for a whitespace target");
    }

    [Test]
    public async Task Promote_ByNonOwner_NullTarget_PermissionDenied()
    {
        // Mirror of Remove: a NON-owner promoting with a null/whitespace target is denied at the owner gate,
        // NOT via the HubException null-guard (which sits after it). RED-verify as for Remove above.
        var channel = await SeedGroupChannel();
        await SeedMembership(channel.Id, "owner#1", MembershipRole.Owner, Now);
        await SeedMembership(channel.Id, "member#2", MembershipRole.Member, Now);
        PutOnline("conn-m2", channel.Id, "member#2");
        var hub = BuildHub("conn-m2");

        Assert.That((await hub.PromoteOwner(channel.Id, null)).Code, Is.EqualTo(ChatResultCode.PermissionDenied), "a non-owner with a null target is denied at the owner gate, never reaching the null-target HubException");
        Assert.That((await hub.PromoteOwner(channel.Id, "   ")).Code, Is.EqualTo(ChatResultCode.PermissionDenied), "same for a whitespace target");
    }

    [Test]
    public async Task Remove_ByOwner_NullTarget_HubException()
    {
        // The owner-side of the asymmetry (D18 client-bug mapping): an OWNER who clears the owner gate then
        // passes a null/whitespace target hits the null-target guard and gets a HubException. Together with
        // Remove_ByNonOwner_NullTarget_PermissionDenied this pins the exact owner-first ordering the
        // anti-oracle wall depends on. Only AddGroupMember previously pinned the owner null-target throw.
        var channel = await SeedGroupChannel();
        await SeedMembership(channel.Id, "owner#1", MembershipRole.Owner, Now);
        PutOnline("conn-owner", channel.Id, "owner#1");
        var hub = BuildHub("conn-owner");

        Assert.That(async () => await hub.RemoveGroupMember(channel.Id, null), Throws.TypeOf<HubException>());
        Assert.That(async () => await hub.RemoveGroupMember(channel.Id, "   "), Throws.TypeOf<HubException>());
    }

    [Test]
    public async Task Promote_ByOwner_NullTarget_HubException()
    {
        // Mirror of Remove: an OWNER promoting with a null/whitespace target throws HubException (D18).
        var channel = await SeedGroupChannel();
        await SeedMembership(channel.Id, "owner#1", MembershipRole.Owner, Now);
        PutOnline("conn-owner", channel.Id, "owner#1");
        var hub = BuildHub("conn-owner");

        Assert.That(async () => await hub.PromoteOwner(channel.Id, null), Throws.TypeOf<HubException>());
        Assert.That(async () => await hub.PromoteOwner(channel.Id, "   "), Throws.TypeOf<HubException>());
    }

    [Test]
    public async Task OrdinaryMemberLeaves_OwnerRemains_NoPromotionNoDeletion()
    {
        // An ORDINARY member leaving a group that still has an owner: HandleGroupDeparture no-ops (an owner
        // remains) — the channel survives, nothing is promoted, remaining members keep their roles, and only
        // the leaver's row is gone. Complements OwnerLeaves_OtherOwnerRemains (owner-leaves path) and
        // LastMemberLeaves (empty-deletion path).
        var channel = await SeedGroupChannel();
        await SeedMembership(channel.Id, "owner#1", MembershipRole.Owner, Now);
        await SeedMembership(channel.Id, "member#2", MembershipRole.Member, Now.AddMinutes(1));
        await SeedMembership(channel.Id, "member#3", MembershipRole.Member, Now.AddMinutes(2));
        PutOnline("conn-m2", channel.Id, "member#2");

        var result = await BuildHub("conn-m2").LeaveChannel(channel.Id);

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(await _channelRepository.Load(channel.Id), Is.Not.Null, "the channel survives an ordinary member's departure");
        Assert.That(await _membershipRepository.Load(channel.Id, "member#2"), Is.Null, "only the leaver's row is gone");
        Assert.That((await _membershipRepository.Load(channel.Id, "owner#1")).Role, Is.EqualTo(MembershipRole.Owner), "the owner remains sole owner — no promotion");
        Assert.That((await _membershipRepository.Load(channel.Id, "member#3")).Role, Is.EqualTo(MembershipRole.Member), "the remaining ordinary member keeps its role");
        Assert.That((await _membershipRepository.LoadForChannel(channel.Id)).Count(m => m.Role == MembershipRole.Owner), Is.EqualTo(1), "exactly one owner (the original) after an ordinary leave");
    }

    [Test]
    public async Task AddGroupMember_StaleSnapshot_ThrottledRetriable_NothingPersisted()
    {
        // Warm a FRESH snapshot for the caller (the target IS the caller's friend) so the provider's cache
        // is populated, then take the source down and advance past RelationshipCacheTtl. The provider's
        // tier-2 refresh fails and it falls back to the STALE last-known snapshot (tier 3, spec §14) rather
        // than throwing — so this exercises AddGroupMember's OWN stricter freshness check
        // (`!snapshot.IsFresh(now)`), distinct from the fully-unavailable/no-cache throw-path covered by
        // Add_WbDown_ThrottledRetriable above. Mirrors CreateGroup_StaleSnapshot_...
        var channel = await SeedGroupChannel();
        await SeedMembership(channel.Id, "owner#1", MembershipRole.Owner, Now);
        PutOnline("conn-owner", channel.Id, "owner#1");
        SetFriends("owner#1", "newbie#3");
        await _relationshipProvider.GetSnapshotAsync("owner#1"); // warm a FRESH snapshot at FixedNow
        _relationshipSource.ShouldThrow = true;
        _time.Advance(ChatLimits.RelationshipCacheTtl + TimeSpan.FromMinutes(1));

        var result = await BuildHub("conn-owner").AddGroupMember(channel.Id, "newbie#3");

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Throttled), "a STALE relationship snapshot fails closed (retriable) — the group add friends-gate requires freshness, unlike the 1:1 delivery block-check");
        Assert.That(result.RetryAfterSeconds, Is.EqualTo(ChatLimits.RelationshipRetryAfterSeconds));
        Assert.That(await _membershipRepository.Load(channel.Id, "newbie#3"), Is.Null, "no membership persisted on a stale-snapshot reject");
    }

    [Test]
    public async Task Remove_ByOwner_TargetPushThrows_StillOk_MembershipGone()
    {
        // PROD FIX 2 (SEC-Low-3, push fault isolation): a torn-down TARGET connection throwing from the
        // ChannelRemoved push must NOT propagate out of RemoveGroupMember — the durable delete already
        // succeeded and the registry/focus were already cleared (both precede the wrapped send), so the
        // owner still gets Ok and the dropped notification heals via SessionState on the target's reconnect.
        // Without PROD FIX 2 the raw SendAsync would throw straight out of RemoveGroupMember and fail here.
        var channel = await SeedGroupChannel();
        await SeedMembership(channel.Id, "owner#1", MembershipRole.Owner, Now);
        await SeedMembership(channel.Id, "victim#2", MembershipRole.Member, Now);
        PutOnline("conn-owner", channel.Id, "owner#1");
        PutOnline("conn-victim", channel.Id, "victim#2");
        await BuildHub("conn-victim").FocusChannel(channel.Id); // so the Unfocus assertion below is meaningful
        _harness.ThrowOnSend("conn-victim"); // the target's live connection faults on the ChannelRemoved push

        var result = await BuildHub("conn-owner").RemoveGroupMember(channel.Id, "victim#2");

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok), "the push fault is isolated — the owner still gets Ok after the durable delete");
        Assert.That(await _membershipRepository.Load(channel.Id, "victim#2"), Is.Null, "the removed member's row is gone despite the push fault");
        Assert.That(_onlineMemberRegistry.IsMember("conn-victim", channel.Id), Is.False, "the registry Leave ran (it precedes the wrapped send)");
        Assert.That(_focusRegistry.GetFocusedChannels("conn-victim"), Is.Empty, "the focus Unfocus ran (it precedes the wrapped send)");
    }
}
