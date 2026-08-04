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
using W3ChampionsChatService.Mentions;
using W3ChampionsChatService.Memberships;
using W3ChampionsChatService.Messages;
using W3ChampionsChatService.Mutes;
using W3ChampionsChatService.Protocol;
using W3ChampionsChatService.Relationships;
using W3ChampionsChatService.Sessions;
using W3ChampionsChatService.Users;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// Task 8 (2026-08-04 follow-up spec §6): <c>GetConversations</c> — the cursor-paginated read of the
/// caller's OLDER 1:1 Dm shells (the ones the bounded connect snapshot, Task 6, excludes), newest-first
/// by (LastMessageAt, ChannelId). Direct-hub-instantiation idiom; scaffolding copied VERBATIM from
/// <see cref="ChatHubGetMessagesTests"/> (same fields, same <see cref="IntegrationTestBase"/> base, same
/// <c>BuildHub</c> ctor list, same <c>FixedNow</c>/<c>_time</c>), EXTENDED (Task 8 fix round) with a REAL
/// <see cref="RelationshipProvider"/> over a <see cref="FakeRelationshipSource"/> (mirrors
/// <see cref="ChatHubDmSendTests"/>) so blocked-shell exclusion and the relationship-outage fail-closed
/// path are exercisable, instead of the throwaway <see cref="RelationshipProviderTestFactory"/>.
/// </summary>
public class ChatHubGetConversationsTests : IntegrationTestBase
{
    private const string BattleTag = "peter#123";
    private const string OtherPrefix = "friend";

    private static readonly DateTimeOffset FixedNow = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private ConnectionMapping _connectionMapping;
    private UserDirectoryRepository _userDirectory;
    private MuteRepository _muteRepository;
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
    private FakeTimeProvider _time;

    // Task 8 fix round: per-tag blocked lists, read by the fake source's snapshot factory
    // (OrdinalIgnoreCase) — mirrors ChatHubDmSendTests. Keyed by the CALLER's own battleTag (the block
    // check is "does the caller's snapshot list this counterpart as blocked").
    private readonly Dictionary<string, HashSet<string>> _blocked = new(StringComparer.OrdinalIgnoreCase);
    private FakeRelationshipSource _relationshipSource;
    private RelationshipProvider _relationshipProvider;

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    [SetUp]
    public void SetupBeforeEach()
    {
        _time = new FakeTimeProvider(FixedNow);
        _blocked.Clear();

        _connectionMapping = new ConnectionMapping();
        _userDirectory = new UserDirectoryRepository(MongoClient);
        _muteRepository = new MuteRepository(MongoClient);
        _reconcileHarness = new MuteReconciliationTestHarness(_connectionMapping, _muteRepository);
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
        _assembler = new SessionStateAssembler(
            _membershipRepository,
            _channelRepository,
            _messageRepository,
            _muteRepository,
            _onlineMemberRegistry,
            _connectionMapping,
            new MentionInboxRepository(MongoClient));

        _relationshipSource = new FakeRelationshipSource((tag, now) => new RelationshipSnapshot(
            tag,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            _blocked.TryGetValue(tag, out var b) ? b : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            now));
        _relationshipProvider = new RelationshipProvider(_relationshipSource, _time);
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
            new UserSettingsRepository(MongoClient),
            new DmInitiationTracker(),
            _authService.Object,
            MentionFanOutTestFactory.CreateIgnored(MongoClient),
            new PresenceInterestRegistry(),
            new MentionInboxRepository(MongoClient));

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

    // One 1:1 shell (viewer + counterpart) with the viewer's own membership row. Defaults to an
    // ACCEPTED shell initiated by the viewer; Task 8 fix round extends this with requestState/
    // initiatedBy so pending-shell exclusion (both directions) is testable without a second helper.
    private async Task<ChatChannel> CreateDmShell(
        string counterpart, DateTime lastMessageAt, long lastSeq = 0, long lastReadSeq = 0,
        DmRequestState requestState = DmRequestState.Accepted, string initiatedBy = null)
    {
        var channel = new ChatChannel
        {
            Type = ChannelType.Dm,
            PairKey = DmPairKey.For(BattleTag, counterpart),
            RequestState = requestState,
            RequestInitiatedBy = initiatedBy ?? BattleTag,
            LastSeq = lastSeq,
            LastMessageAt = lastMessageAt,
        };
        await _channelRepository.Insert(channel);
        await _membershipRepository.Insert(new ChannelMembership
        {
            ChannelId = channel.Id,
            BattleTag = BattleTag,
            NotificationLevel = NotificationLevel.All,
            LastReadSeq = lastReadSeq,
            JoinedAt = lastMessageAt,
        });
        return channel;
    }

    [Test]
    public async Task GetConversations_PagesNewestFirst_WithoutOverlapOrGaps()
    {
        var t0 = Now;
        var all = new List<ChatChannel>();
        for (var i = 0; i < 25; i++)
        {
            all.Add(await CreateDmShell($"{OtherPrefix}{i}#1", t0.AddMinutes(i)));
        }
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");

        var seen = new List<string>();
        DateTime? cursorTime = null;
        string cursorId = null;
        while (true)
        {
            var page = await hub.GetConversations(cursorTime, cursorId, limit: 10);
            Assert.AreEqual(ChatResultCode.Ok, page.Code);
            if (page.Conversations.Count == 0) break;

            // Newest-first within each page, strictly older than the cursor.
            for (var i = 1; i < page.Conversations.Count; i++)
            {
                Assert.LessOrEqual(
                    page.Conversations[i].Channel.LastMessageAt.Value,
                    page.Conversations[i - 1].Channel.LastMessageAt.Value);
            }
            seen.AddRange(page.Conversations.Select(c => c.Channel.Id));
            var last = page.Conversations[^1].Channel;
            cursorTime = last.LastMessageAt;
            cursorId = last.Id;
            if (page.Conversations.Count < 10) break;
        }

        CollectionAssert.AreEquivalent(all.Select(c => c.Id), seen, "every shell exactly once — no gaps, no dupes");
        Assert.AreEqual(all[24].Id, seen[0], "the newest shell comes first");
    }

    [Test]
    public async Task GetConversations_SeedsRegistry_SoAPagedConversationAcceptsSends()
    {
        var shell = await CreateDmShell($"{OtherPrefix}0#1", Now.AddMinutes(-90));
        RegisterSession("conn-1", BattleTag);
        _connectionMapping.RegisterUser("conn-1", new ChatUser(BattleTag, false, "peter", new ProfilePicture(), null, null));
        _connectionMapping.SetMute("conn-1", MuteStatus.None, DateTime.MinValue);
        var hub = BuildHub("conn-1");

        var page = await hub.GetConversations(null, null, limit: 10);
        Assert.AreEqual(ChatResultCode.Ok, page.Code);
        Assert.IsTrue(_onlineMemberRegistry.IsMember("conn-1", shell.Id),
            "every returned shell is seeded into the caller's registry");

        var send = await hub.SendMessage(shell.Id, "picking this back up");
        Assert.AreEqual(ChatResultCode.Ok, send.Code, "a paged conversation is immediately usable");
    }

    [Test]
    public async Task GetConversations_ReturnsUnreadCounts()
    {
        var shell = await CreateDmShell($"{OtherPrefix}0#1", Now.AddMinutes(-5), lastSeq: 1, lastReadSeq: 0);
        await _messageRepository.Insert(new ChannelMessage
        {
            ChannelId = shell.Id,
            Seq = 1,
            Sender = new MessageSender { BattleTag = $"{OtherPrefix}0#1", Name = "friend0" },
            Content = "unread hello",
            SentAt = Now.AddMinutes(-5),
        });
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");

        var page = await hub.GetConversations(null, null, limit: 10);

        var dto = page.Conversations.Single(c => c.Channel.Id == shell.Id);
        Assert.AreEqual(1, dto.UnreadCount, "the D7 user-visible unread count rides each paged shell");
        Assert.IsTrue(dto.HasUnread);
    }

    [Test]
    public async Task GetConversations_ExcludesNonDmChannels()
    {
        await CreateDmShell($"{OtherPrefix}0#1", Now.AddMinutes(-1));
        var group = new ChatChannel { Type = ChannelType.GroupDm, Name = "the gang", LastMessageAt = Now };
        await _channelRepository.Insert(group);
        await _membershipRepository.Insert(new ChannelMembership
        {
            ChannelId = group.Id, BattleTag = BattleTag, NotificationLevel = NotificationLevel.All, JoinedAt = Now,
        });
        var pub = new ChatChannel { Type = ChannelType.Public, Name = "general", NormalizedName = "general", LastMessageAt = Now };
        await _channelRepository.Insert(pub);
        await _membershipRepository.Insert(new ChannelMembership
        {
            ChannelId = pub.Id, BattleTag = BattleTag, NotificationLevel = NotificationLevel.All, JoinedAt = Now,
        });
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");

        var page = await hub.GetConversations(null, null, limit: 10);

        Assert.AreEqual(1, page.Conversations.Count, "GetConversations pages 1:1 Dm shells ONLY");
        Assert.AreEqual(ChannelType.Dm, page.Conversations[0].Channel.Type);
    }

    [Test]
    public async Task GetConversations_ClampsLimit()
    {
        for (var i = 0; i < ChatLimits.ConversationsPageSize + 5; i++)
        {
            await CreateDmShell($"{OtherPrefix}{i}#1", Now.AddMinutes(i));
        }
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");

        var page = await hub.GetConversations(null, null, limit: 10_000);

        Assert.AreEqual(ChatLimits.ConversationsPageSize, page.Conversations.Count,
            "an oversized limit is clamped down, never rejected (the MessagePageSize precedent)");
    }

    [Test]
    public void GetConversations_HalfCursor_ThrowsHubException()
    {
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");

        Assert.ThrowsAsync<HubException>(() => hub.GetConversations(Now, null, limit: 10),
            "cursorLastMessageAt without cursorChannelId is a client bug");
        Assert.ThrowsAsync<HubException>(() => hub.GetConversations(null, "some-id", limit: 10),
            "cursorChannelId without cursorLastMessageAt is a client bug");
    }

    [Test]
    public async Task GetConversations_NoSession_ReturnsPermissionDenied()
    {
        var hub = BuildHub("conn-ghost");
        var page = await hub.GetConversations(null, null, limit: 10);
        Assert.AreEqual(ChatResultCode.PermissionDenied, page.Code);
        Assert.IsNull(page.Conversations, "a PermissionDenied reject must never carry a payload");
    }

    // ---------------------------------------------------------------------
    // Task 8 fix round — finding 1 (CRITICAL): pending shells must never page in. Pending requests ride
    // the connect snapshot + request tray ONLY, in EITHER direction.
    // ---------------------------------------------------------------------

    [Test]
    public async Task GetConversations_ExcludesPendingShells_BothDirections()
    {
        var incoming = await CreateDmShell($"{OtherPrefix}0#1", Now.AddMinutes(-120),
            requestState: DmRequestState.Pending, initiatedBy: $"{OtherPrefix}0#1");
        var outgoing = await CreateDmShell($"{OtherPrefix}1#1", Now.AddMinutes(-110),
            requestState: DmRequestState.Pending, initiatedBy: BattleTag);
        var accepted = await CreateDmShell($"{OtherPrefix}2#1", Now.AddMinutes(-100));
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");

        var page = await hub.GetConversations(null, null, limit: 10);

        Assert.AreEqual(ChatResultCode.Ok, page.Code);
        var ids = page.Conversations.Select(c => c.Channel.Id).ToList();
        CollectionAssert.DoesNotContain(ids, incoming.Id,
            "an incoming pending request rides the tray only, never GetConversations");
        CollectionAssert.DoesNotContain(ids, outgoing.Id,
            "a caller-initiated pending request rides the tray only, never GetConversations");
        CollectionAssert.Contains(ids, accepted.Id, "an accepted shell still pages normally");
    }

    // ---------------------------------------------------------------------
    // Task 8 fix round — finding 2 (CRITICAL): a blocked counterpart's shell stays Blocked-tray-only
    // (Task 6) — it must never page in as an ordinary, live conversation. A relationship-provider outage
    // must fail closed (Throttled) rather than serve an unfiltered page.
    // ---------------------------------------------------------------------

    [Test]
    public async Task GetConversations_ExcludesBlockedCounterpartShells()
    {
        var blockedCounterpart = $"{OtherPrefix}0#1";
        var unblockedCounterpart = $"{OtherPrefix}1#1";
        _blocked[BattleTag] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { blockedCounterpart };
        var blockedShell = await CreateDmShell(blockedCounterpart, Now.AddMonths(-24));
        var unblockedShell = await CreateDmShell(unblockedCounterpart, Now.AddMonths(-24));
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");

        var page = await hub.GetConversations(null, null, limit: 10);

        Assert.AreEqual(ChatResultCode.Ok, page.Code);
        var ids = page.Conversations.Select(c => c.Channel.Id).ToList();
        CollectionAssert.DoesNotContain(ids, blockedShell.Id,
            "a blocked counterpart's shell stays Blocked-tray-only (Task 6) — never pages in as an ordinary conversation");
        CollectionAssert.Contains(ids, unblockedShell.Id, "an unblocked sibling shell still pages normally");
    }

    [Test]
    public async Task GetConversations_RelationshipProviderUnavailable_ReturnsThrottled()
    {
        await CreateDmShell($"{OtherPrefix}0#1", Now.AddMinutes(-5));
        RegisterSession("conn-1", BattleTag);
        _relationshipSource.ShouldThrow = true;
        var hub = BuildHub("conn-1");

        var page = await hub.GetConversations(null, null, limit: 10);

        Assert.AreEqual(ChatResultCode.Throttled, page.Code,
            "no usable relationship snapshot at all must fail closed rather than risk an unfiltered page");
        Assert.IsNull(page.Conversations);
        Assert.AreEqual(ChatLimits.RelationshipRetryAfterSeconds, page.RetryAfterSeconds);
    }

    // ---------------------------------------------------------------------
    // Task 8 fix round — finding 4 (minor): a null-stamped LastMessageAt is data-impossible in
    // production (FindOrCreateDm always stamps it) but would otherwise dead-end the client's wire
    // cursor — such a shell must be excluded from the page.
    // ---------------------------------------------------------------------

    [Test]
    public async Task GetConversations_ExcludesNullLastMessageAtShells()
    {
        var legacyChannel = new ChatChannel
        {
            Type = ChannelType.Dm,
            PairKey = DmPairKey.For(BattleTag, $"{OtherPrefix}legacy#1"),
            RequestState = DmRequestState.Accepted,
            RequestInitiatedBy = BattleTag,
            LastSeq = 0,
            LastMessageAt = null,
        };
        await _channelRepository.Insert(legacyChannel);
        await _membershipRepository.Insert(new ChannelMembership
        {
            ChannelId = legacyChannel.Id,
            BattleTag = BattleTag,
            NotificationLevel = NotificationLevel.All,
            LastReadSeq = 0,
            JoinedAt = Now.AddYears(-1),
        });
        var normalShell = await CreateDmShell($"{OtherPrefix}0#1", Now.AddMinutes(-5));
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");

        var page = await hub.GetConversations(null, null, limit: 10);

        var ids = page.Conversations.Select(c => c.Channel.Id).ToList();
        CollectionAssert.DoesNotContain(ids, legacyChannel.Id,
            "a null-stamped LastMessageAt is data-impossible in production but must not dead-end the wire cursor");
        CollectionAssert.Contains(ids, normalShell.Id);
    }

    // ---------------------------------------------------------------------
    // Task 8 fix round — finding 7 (minor): remaining test gaps called out in code review.
    // ---------------------------------------------------------------------

    [Test]
    public async Task GetConversations_TieBreaksOnChannelId_NoSkipOrDuplicateAcrossPageBoundary()
    {
        var tied = Now.AddMinutes(-10);
        var shells = new List<ChatChannel>();
        for (var i = 0; i < 3; i++)
        {
            shells.Add(await CreateDmShell($"{OtherPrefix}tie{i}#1", tied));
        }
        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");

        var expectedOrder = shells.OrderByDescending(c => c.Id, StringComparer.Ordinal).Select(c => c.Id).ToList();

        var seen = new List<string>();
        DateTime? cursorTime = null;
        string cursorId = null;
        while (true)
        {
            var page = await hub.GetConversations(cursorTime, cursorId, limit: 2);
            Assert.AreEqual(ChatResultCode.Ok, page.Code);
            if (page.Conversations.Count == 0) break;
            seen.AddRange(page.Conversations.Select(c => c.Channel.Id));
            var last = page.Conversations[^1].Channel;
            cursorTime = last.LastMessageAt;
            cursorId = last.Id;
            if (page.Conversations.Count < 2) break;
        }

        CollectionAssert.AreEqual(expectedOrder, seen,
            "identical LastMessageAt must tie-break deterministically on ChannelId with no skip or duplicate across a page boundary");
    }

    [Test]
    public async Task GetConversations_CrossCallerIsolation_OtherUsersShellsNeverAppear()
    {
        var mine = await CreateDmShell($"{OtherPrefix}0#1", Now.AddMinutes(-5));

        // A completely separate user's own Dm shell + membership row — never the caller's.
        const string otherCaller = "shadow#999";
        var otherChannel = new ChatChannel
        {
            Type = ChannelType.Dm,
            PairKey = DmPairKey.For(otherCaller, $"{OtherPrefix}9#1"),
            RequestState = DmRequestState.Accepted,
            RequestInitiatedBy = otherCaller,
            LastSeq = 0,
            LastMessageAt = Now.AddMinutes(-1),
        };
        await _channelRepository.Insert(otherChannel);
        await _membershipRepository.Insert(new ChannelMembership
        {
            ChannelId = otherChannel.Id,
            BattleTag = otherCaller,
            NotificationLevel = NotificationLevel.All,
            LastReadSeq = 0,
            JoinedAt = Now.AddMinutes(-1),
        });

        RegisterSession("conn-1", BattleTag);
        var hub = BuildHub("conn-1");

        var page = await hub.GetConversations(null, null, limit: 10);

        var ids = page.Conversations.Select(c => c.Channel.Id).ToList();
        CollectionAssert.Contains(ids, mine.Id);
        CollectionAssert.DoesNotContain(ids, otherChannel.Id,
            "GetConversations must only ever page the CALLER's own memberships");
    }
}
