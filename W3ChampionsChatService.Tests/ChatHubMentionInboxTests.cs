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
/// C6 Task 6 (D6): the mention-inbox READ/ACK surface — <c>GetMentionInbox</c>,
/// <c>MarkMentionsRead</c>, <c>MarkAllMentionsRead</c> (<c>ChatHub.Mentions.cs</c>). Entries are
/// seeded DIRECTLY via <see cref="MentionInboxRepository.Insert"/> — this task owns reads/acks, not
/// the fan-out WRITE path (that's <c>MentionFanOutTests</c>/Task 5's job). Direct-hub-instantiation
/// idiom (mirrors <see cref="ChatHubMarkReadTests"/>); a <see cref="FakeTimeProvider"/> drives the
/// clock so each ack's <c>ReadAt</c> is independently assertable.
/// </summary>
public class ChatHubMentionInboxTests : IntegrationTestBase
{
    private const string Owner = "peter#123";
    private const string Other = "wolf#456";

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
            _readRateLimiter,
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

    private async Task<MentionInboxEntry> SeedEntry(string battleTag, DateTime createdAt)
    {
        var entry = new MentionInboxEntry
        {
            BattleTag = battleTag.ToLowerInvariant(),
            ChannelId = "chan-1",
            MessageId = Guid.NewGuid().ToString(),
            Seq = 7,
            AuthorBattleTag = "author#1",
            AuthorName = "Author",
            Excerpt = "hey check this out",
            CreatedAt = createdAt,
            ExpiresAt = createdAt.AddDays(30),
        };
        await _mentionInboxRepository.Insert(entry);
        return entry;
    }

    // ---------------------------------------------------------------------------------------------
    // The no-seq-auto-ack pin (acceptance 2): acking the NEWER of two unread mentions must never
    // ack the OLDER, still-unseen one — a deliberate departure from regular seq-derived channel
    // read-state.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task MarkMentionsRead_SetsReadAt_OnlyForListedIds()
    {
        RegisterSession("conn-1", Owner);
        var older = await SeedEntry(Owner, Now.AddMinutes(-10));
        var newer = await SeedEntry(Owner, Now);
        var hub = BuildHub("conn-1");

        var result = await hub.MarkMentionsRead(new[] { newer.Id });

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        var inbox = (await hub.GetMentionInbox()).Entries;
        Assert.That(inbox.Single(e => e.Id == newer.Id).ReadAt, Is.Not.Null,
            "the explicitly-listed newer entry must be acked");
        Assert.That(inbox.Single(e => e.Id == older.Id).ReadAt, Is.Null,
            "an older, UNLISTED entry must stay unread — there is no seq-derived auto-ack");
    }

    // ---------------------------------------------------------------------------------------------
    // Idempotency: a second ack of an already-read id is a no-op; ReadAt keeps its FIRST-seen value.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task MarkMentionsRead_Idempotent_SecondCallOk_ReadAtUnchanged()
    {
        RegisterSession("conn-1", Owner);
        var entry = await SeedEntry(Owner, Now);
        var hub = BuildHub("conn-1");

        var first = await hub.MarkMentionsRead(new[] { entry.Id });
        Assert.That(first.Code, Is.EqualTo(ChatResultCode.Ok));
        var firstReadAt = (await hub.GetMentionInbox()).Entries.Single(e => e.Id == entry.Id).ReadAt;
        Assert.That(firstReadAt, Is.Not.Null);

        _time.Advance(TimeSpan.FromMinutes(5));
        var second = await hub.MarkMentionsRead(new[] { entry.Id });

        Assert.That(second.Code, Is.EqualTo(ChatResultCode.Ok), "a re-ack of an already-read id must still return Ok, never an error");
        var secondReadAt = (await hub.GetMentionInbox()).Entries.Single(e => e.Id == entry.Id).ReadAt;
        Assert.That(secondReadAt, Is.EqualTo(firstReadAt), "a second ack must never move ReadAt forward");
    }

    // ---------------------------------------------------------------------------------------------
    // Authorization boundary: caller B acking caller A's entry ids is a silent no-op, never an error —
    // an error (or a divergent code) would be an oracle revealing that the id belongs to someone else.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task MarkMentionsRead_ForeignEntryIds_NotAcked_OwnerFilterHolds()
    {
        RegisterSession("conn-a", Owner);
        RegisterSession("conn-b", Other);
        var ownersEntry = await SeedEntry(Owner, Now);
        var hubForB = BuildHub("conn-b");

        var result = await hubForB.MarkMentionsRead(new[] { ownersEntry.Id });

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok),
            "acking someone else's entry id must return the SAME Ok as any other ack — no oracle");

        var hubForA = BuildHub("conn-a");
        var stillUnread = (await hubForA.GetMentionInbox()).Entries.Single(e => e.Id == ownersEntry.Id);
        Assert.That(stillUnread.ReadAt, Is.Null, "the owner filter must hold — B's ack must never touch A's entry");
    }

    // ---------------------------------------------------------------------------------------------
    // Malformed-arg client-bug mapping.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public void MarkMentionsRead_NullIds_HubException()
    {
        RegisterSession("conn-1", Owner);
        var hub = BuildHub("conn-1");

        Assert.That(async () => await hub.MarkMentionsRead(null), Throws.TypeOf<HubException>());
    }

    [Test]
    public void MarkMentionsRead_OverBatchCap_HubException()
    {
        RegisterSession("conn-1", Owner);
        var hub = BuildHub("conn-1");
        var tooMany = Enumerable.Range(0, ChatLimits.MentionAckBatchMax + 1).Select(i => $"id-{i}").ToArray();

        Assert.That(async () => await hub.MarkMentionsRead(tooMany), Throws.TypeOf<HubException>());
    }

    [Test]
    public async Task MarkMentionsRead_NoSession_PermissionDenied()
    {
        var hub = BuildHub("conn-ghost");

        var result = await hub.MarkMentionsRead(new[] { "some-id" });

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.PermissionDenied));
    }

    // ---------------------------------------------------------------------------------------------
    // MarkAllMentionsRead: everything unread is acked; entries PERSIST (never deleted).
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task MarkAllMentionsRead_AcksEverything_ReadEntriesPersist()
    {
        RegisterSession("conn-1", Owner);
        var first = await SeedEntry(Owner, Now.AddMinutes(-5));
        var second = await SeedEntry(Owner, Now);
        var hub = BuildHub("conn-1");

        var result = await hub.MarkAllMentionsRead();

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        var inbox = (await hub.GetMentionInbox()).Entries;
        Assert.That(inbox, Has.Count.EqualTo(2), "read entries are KEPT — mark-all-read must never delete a row");
        Assert.That(inbox.Single(e => e.Id == first.Id).ReadAt, Is.Not.Null);
        Assert.That(inbox.Single(e => e.Id == second.Id).ReadAt, Is.Not.Null);
    }

    [Test]
    public async Task MarkAllMentionsRead_NoSession_PermissionDenied()
    {
        var hub = BuildHub("conn-ghost");

        var result = await hub.MarkAllMentionsRead();

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.PermissionDenied));
    }

    // ---------------------------------------------------------------------------------------------
    // GetMentionInbox: newest-first, own entries only, and the explicit boundary-privacy projection
    // (no ExpiresAt on the wire).
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task GetMentionInbox_NewestFirst_OwnEntriesOnly_ProjectionShape()
    {
        RegisterSession("conn-1", Owner);
        RegisterSession("conn-2", Other);
        var older = await SeedEntry(Owner, Now.AddMinutes(-10));
        var newer = await SeedEntry(Owner, Now);
        await SeedEntry(Other, Now); // someone else's entry — must never appear in Owner's inbox

        var hub = BuildHub("conn-1");
        var result = await hub.GetMentionInbox();

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(result.Entries.Select(e => e.Id), Is.EqualTo(new[] { newer.Id, older.Id }),
            "newest-first ordering, own entries only");

        var dto = result.Entries.Single(e => e.Id == newer.Id);
        Assert.That(dto.ChannelId, Is.EqualTo(newer.ChannelId));
        Assert.That(dto.MessageId, Is.EqualTo(newer.MessageId));
        Assert.That(dto.Seq, Is.EqualTo(newer.Seq));
        Assert.That(dto.AuthorBattleTag, Is.EqualTo(newer.AuthorBattleTag));
        Assert.That(dto.AuthorName, Is.EqualTo(newer.AuthorName));
        Assert.That(dto.Excerpt, Is.EqualTo(newer.Excerpt));
        Assert.That(dto.CreatedAt, Is.EqualTo(newer.CreatedAt));
        Assert.That(dto.ReadAt, Is.Null);

        // Boundary-privacy (Task 1's explicit projection): the DTO type itself carries NO ExpiresAt
        // member at all — asserted via reflection so a future accidental re-add is caught here too,
        // not just by convention.
        var dtoProperties = typeof(MentionInboxEntryDto).GetProperties().Select(p => p.Name);
        Assert.That(dtoProperties, Does.Not.Contain("ExpiresAt"));
    }

    [Test]
    public async Task GetMentionInbox_IncludesReadEntries_Dimmable()
    {
        RegisterSession("conn-1", Owner);
        var entry = await SeedEntry(Owner, Now);
        var hub = BuildHub("conn-1");
        await hub.MarkMentionsRead(new[] { entry.Id });

        var result = await hub.GetMentionInbox();

        Assert.That(result.Entries.Select(e => e.Id), Does.Contain(entry.Id),
            "a READ entry must still be returned (dimmed client-side) — never dropped from the inbox");
    }

    [Test]
    public async Task GetMentionInbox_NoSession_PermissionDenied()
    {
        var hub = BuildHub("conn-ghost");

        var result = await hub.GetMentionInbox();

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.PermissionDenied));
    }
}
