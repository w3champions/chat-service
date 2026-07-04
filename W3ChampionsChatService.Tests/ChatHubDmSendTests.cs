using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
/// C5 Task 4: the send-path private-lane gates — <c>SendMessage</c> becomes Dm/GroupDm-aware (step 5.5).
/// Covers the 1:1 block non-delivery (D1 tier b: silent FakeSendAck vs fail-closed Throttled), the pending
/// consent machine (reply-accept, auto-accept-on-friendship, dmPrivacy-recheck silent drop, 25-depth cap),
/// recipient materialization + <c>RequestReceived</c>, and the shell-expiry maintenance the C1 amendment
/// left unwired. Direct-hub idiom (mirrors <see cref="ChatHubSendMessageTests"/> / <see cref="ChatHubOpenDmTests"/>):
/// a real <see cref="RelationshipProvider"/> over a <see cref="FakeRelationshipSource"/> (NEVER HTTP) gives
/// per-tag control of friends/blocked/outage, a <see cref="FakeTimeProvider"/> drives time, a REAL
/// <see cref="FanOutEngine"/> wired to a <see cref="HubPushCaptureHarness"/> captures ChannelAdded/
/// MessageReceived/ChannelActivity, and the hub's own <c>Clients.Client</c> proxy captures the targeted
/// <c>RequestReceived</c>. NUnit constraint style.
/// </summary>
public class ChatHubDmSendTests : IntegrationTestBase
{
    private const string Initiator = "peter#123";
    private const string Recipient = "wolf#456";
    private const string InitiatorConn = "conn-init";
    private const string RecipientConn = "conn-recip";

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

    // Every (connectionId, method, payload) the HUB itself pushed via Clients.Caller/Clients.Client —
    // the surface the targeted RequestReceived transition uses (fan-out pushes are captured by _harness).
    private readonly List<(string ConnectionId, string Method, object Payload)> _hubSends = new();

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    [SetUp]
    public void SetupBeforeEach()
    {
        _friends.Clear();
        _blocked.Clear();
        _hubSends.Clear();
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

        // A REAL FanOutEngine sharing the hub's registries, wired to a capture harness so ChannelAdded /
        // MessageReceived / ChannelActivity emitted post-persist are observable.
        _harness = new HubPushCaptureHarness();
        _coalescer = new ActivityCoalescer(_harness.HubContext, _onlineMemberRegistry);
        _fanOutEngine = new FanOutEngine(_harness.HubContext, _focusRegistry, _onlineMemberRegistry, _coalescer, _sessionRegistry, new PresenceInterestRegistry());

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
        clients.Setup(c => c.Caller).Returns(CapturingProxy(connectionId));
        clients.Setup(c => c.Client(It.IsAny<string>())).Returns<string>(CapturingProxy);
        hub.Clients = clients.Object;

        var context = new Mock<HubCallerContext>();
        context.Setup(c => c.ConnectionId).Returns(connectionId);
        hub.Context = context.Object;
        hub.Groups = new Mock<IGroupManager>().Object;
        return hub;
    }

    // A per-connection proxy that records every SendAsync/SendCoreAsync into _hubSends tagged with connId.
    private ISingleClientProxy CapturingProxy(string connId)
    {
        var proxy = new Mock<ISingleClientProxy>();
        proxy
            .Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((method, args, _) =>
            {
                lock (_hubSends)
                {
                    _hubSends.Add((connId, method, args.Length > 0 ? args[0] : null));
                }
            })
            .Returns(Task.CompletedTask);
        return proxy.Object;
    }

    private void RegisterSession(string connectionId, string battleTag) =>
        _sessionRegistry.Register(
            connectionId,
            new W3CUserAuthentication { BattleTag = battleTag, Name = battleTag.Split('#')[0] },
            null);

    // Seeds a connection the SAME way the connect path does for the SENDER: a live session, the cached
    // ChatUser (sender-snapshot source), and an OnlineMemberRegistry entry so step-3 IsMember passes.
    // C5 (Task 5, D11): the registry entry's ChannelType defaults to Dm — the overwhelming majority of
    // this file's tests seed a Dm sender — with the Group/Public tests passing their own channel's type
    // explicitly.
    private void SeedMember(string connectionId, string battleTag, string channelId, ChannelType type = ChannelType.Dm)
    {
        RegisterSession(connectionId, battleTag);
        _connectionMapping.RegisterUser(connectionId, new ChatUser(battleTag, false, battleTag.Split('#')[0], new ProfilePicture(), null, null));
        _onlineMemberRegistry.Join(channelId, connectionId, new MemberState(battleTag, NotificationLevel.All, 0, type));
    }

    private Task SeedPrivacy(string battleTag, DmPrivacy privacy) =>
        _userSettings.Upsert(new UserSettings { BattleTag = battleTag, DmPrivacy = privacy });

    private void SetFriends(string battleTag, params string[] friends) =>
        _friends[battleTag] = new HashSet<string>(friends, StringComparer.OrdinalIgnoreCase);

    private void SetBlocked(string battleTag, params string[] blocked) =>
        _blocked[battleTag] = new HashSet<string>(blocked, StringComparer.OrdinalIgnoreCase);

    private Task<ChatChannel> CreateDm(DmRequestState state) =>
        _channelRepository.FindOrCreateDm(Initiator, Recipient, Initiator, state, Now);

    // Bumps a channel's LastSeq to a chosen value via direct repo AllocateSeq (no rate limiter, no
    // shell-expiry) — used to place a pending shell exactly at/under the 25-message cap boundary.
    private async Task BumpLastSeq(string channelId, int count)
    {
        for (var i = 0; i < count; i++)
        {
            await _channelRepository.AllocateSeq(channelId, Now);
        }
    }

    private int HubSignalCount(string connectionId, string method)
    {
        lock (_hubSends)
        {
            return _hubSends.Count(s => s.ConnectionId == connectionId && s.Method == method);
        }
    }

    private object HubPayloadFor(string connectionId, string method)
    {
        lock (_hubSends)
        {
            return _hubSends.Where(s => s.ConnectionId == connectionId && s.Method == method).Select(s => s.Payload).FirstOrDefault();
        }
    }

    // ------------------------------------------------------------------------------------------------
    // (1) Block gate — 1:1 non-delivery (fabricated Ok) and the fail-closed no-snapshot case (Throttled)
    // ------------------------------------------------------------------------------------------------

    [Test]
    public async Task DmSend_BlockedByRecipient_ReturnsOkShape_NothingPersisted_NothingDelivered_NoRecipientMembership()
    {
        var channel = await CreateDm(DmRequestState.Accepted);
        SeedMember(InitiatorConn, Initiator, channel.Id);
        // The recipient is ONLINE (so a leak would be observable) and has BLOCKED the sender.
        RegisterSession(RecipientConn, Recipient);
        SetBlocked(Recipient, Initiator);
        var hub = BuildHub(InitiatorConn);

        var result = await hub.SendMessage(channel.Id, "are you there?");

        // Silent-drop uniformity: an Ok shape with a FABRICATED (non-null) id/seq — a null id/seq would leak.
        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(result.MessageId, Is.Not.Null.And.Not.Empty, "a blocked send returns a fabricated messageId (D6)");
        Assert.That(result.Seq, Is.Not.Null, "a blocked send returns a fabricated seq (D6)");

        // Nothing persisted, nothing delivered, no lane opened for the recipient.
        Assert.That(await _messageRepository.LoadForModerator(channel.Id), Is.Empty, "a blocked 1:1 send stores NOTHING");
        var reloaded = await _channelRepository.Load(channel.Id);
        Assert.That(reloaded.LastSeq, Is.EqualTo(0L), "a blocked send never allocates a seq");
        Assert.That(await _membershipRepository.Load(channel.Id, Recipient), Is.Null, "no recipient membership is materialized on a blocked send");
        Assert.That(_harness.SignalsFor(RecipientConn), Is.Empty, "the recipient connection receives ZERO fan-out events");
        Assert.That(HubSignalCount(RecipientConn, ChatEvents.RequestReceived), Is.EqualTo(0), "the recipient receives no RequestReceived");
    }

    [Test]
    public async Task DmSend_RelationshipUnavailableNoCache_ThrottledRetriable_NothingPersisted()
    {
        var channel = await CreateDm(DmRequestState.Accepted);
        SeedMember(InitiatorConn, Initiator, channel.Id);
        _relationshipSource.ShouldThrow = true; // no cache warmed => the block-check snapshot is unavailable
        var hub = BuildHub(InitiatorConn);

        var result = await hub.SendMessage(channel.Id, "hello?");

        // The ONLY non-silent fail-closed: a total absence of a snapshot (system outage) → typed retriable
        // Throttled. This is NOT a silent drop — it does not leak block/decline state (it is block-agnostic).
        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Throttled));
        Assert.That(result.RetryAfterSeconds, Is.EqualTo(ChatLimits.RelationshipRetryAfterSeconds));
        Assert.That(await _messageRepository.LoadForModerator(channel.Id), Is.Empty);
        var reloaded = await _channelRepository.Load(channel.Id);
        Assert.That(reloaded.LastSeq, Is.EqualTo(0L), "a fail-closed send allocates no seq");
    }

    [Test]
    public async Task DmSend_StaleCachedSnapshot_Proceeds()
    {
        var channel = await CreateDm(DmRequestState.Accepted);
        SeedMember(InitiatorConn, Initiator, channel.Id);

        // Warm the recipient's snapshot (NOT blocking), then take the source down and let it go stale.
        await _relationshipProvider.GetSnapshotAsync(Recipient);
        _relationshipSource.ShouldThrow = true;
        _time.Advance(ChatLimits.RelationshipCacheTtl + TimeSpan.FromMinutes(1));
        var hub = BuildHub(InitiatorConn);

        var result = await hub.SendMessage(channel.Id, "delivered");

        // The delivery block-check accepts a last-known (stale) snapshot — it proceeds.
        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(result.MessageId, Is.Not.Null);
        var persisted = await _messageRepository.LoadForModerator(channel.Id);
        Assert.That(persisted.Count, Is.EqualTo(1), "a stale-but-unblocked snapshot delivers normally");
    }

    // ------------------------------------------------------------------------------------------------
    // (2c) Pending-depth cap (25) — silent drop, uniform Ok shape
    // ------------------------------------------------------------------------------------------------

    [Test]
    public async Task PendingSend_UpToCap25_PersistedWithSeq()
    {
        var channel = await CreateDm(DmRequestState.Pending);
        await BumpLastSeq(channel.Id, ChatLimits.PendingConversationMaxMessages - 1); // 24 already stored
        SeedMember(InitiatorConn, Initiator, channel.Id);
        await SeedPrivacy(Recipient, DmPrivacy.Everyone);
        var hub = BuildHub(InitiatorConn);

        var result = await hub.SendMessage(channel.Id, "the 25th");

        // LastSeq 24 < 25 → the boundary message persists with a real seq.
        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(result.Seq, Is.EqualTo((long)ChatLimits.PendingConversationMaxMessages), "the 25th message allocates seq 25");
        Assert.That(await _messageRepository.Load(result.MessageId), Is.Not.Null, "the 25th message is durably persisted");
    }

    [Test]
    public async Task PendingSend_26th_SilentDropOkShape_LastSeqUnchanged()
    {
        var channel = await CreateDm(DmRequestState.Pending);
        await BumpLastSeq(channel.Id, ChatLimits.PendingConversationMaxMessages); // 25 already stored (at the cap)
        SeedMember(InitiatorConn, Initiator, channel.Id);
        await SeedPrivacy(Recipient, DmPrivacy.Everyone);
        var hub = BuildHub(InitiatorConn);

        var result = await hub.SendMessage(channel.Id, "the 26th");

        // At/over the cap → silent drop with a FAKE ack (uniform with a block/decline drop).
        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(result.MessageId, Is.Not.Null.And.Not.Empty);
        Assert.That(result.Seq, Is.EqualTo((long)ChatLimits.PendingConversationMaxMessages + 1), "the fake seq is LastSeq+1");
        var reloaded = await _channelRepository.Load(channel.Id);
        Assert.That(reloaded.LastSeq, Is.EqualTo((long)ChatLimits.PendingConversationMaxMessages), "the 26th allocates no seq — LastSeq unchanged");
        Assert.That((await _messageRepository.LoadForModerator(channel.Id)).Count, Is.EqualTo(0), "the 26th stores nothing (BumpLastSeq wrote no message rows)");
    }

    [Test]
    public async Task AcceptedConversation_NoCap()
    {
        var channel = await CreateDm(DmRequestState.Accepted);
        await BumpLastSeq(channel.Id, ChatLimits.PendingConversationMaxMessages + 5); // 30 — well over the pending cap
        SeedMember(InitiatorConn, Initiator, channel.Id);
        var hub = BuildHub(InitiatorConn);

        var result = await hub.SendMessage(channel.Id, "accepted, no cap");

        // The cap applies ONLY while pending — an accepted conversation is "normal forever".
        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(result.Seq, Is.EqualTo((long)ChatLimits.PendingConversationMaxMessages + 6));
        Assert.That(await _messageRepository.Load(result.MessageId), Is.Not.Null);
    }

    // ------------------------------------------------------------------------------------------------
    // (2) Consent transitions — reply-accept, auto-accept-on-friendship
    // ------------------------------------------------------------------------------------------------

    [Test]
    public async Task RecipientReply_AutoAccepts_BeforePersist()
    {
        var channel = await CreateDm(DmRequestState.Pending);
        // Simulate the initiation the initiator's OpenDm recorded, so we can prove MarkAccepted frees it.
        _dmInitiationTracker.Record(Initiator, Recipient.ToLowerInvariant(), Now);
        // The RECIPIENT is the sender here (replying to the initiator's pending request).
        SeedMember(RecipientConn, Recipient, channel.Id);
        var hub = BuildHub(RecipientConn);

        var result = await hub.SendMessage(channel.Id, "sure, let's talk");

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        var reloaded = await _channelRepository.Load(channel.Id);
        Assert.That(reloaded.RequestState, Is.EqualTo(DmRequestState.Accepted), "a recipient's reply flips the request to Accepted FIRST");
        Assert.That(await _messageRepository.Load(result.MessageId), Is.Not.Null, "the reply itself is persisted (no cap)");
        Assert.That(reloaded.LastSeq, Is.EqualTo(1L), "the reply allocates the first real seq");
        Assert.That(_dmInitiationTracker.CountActive(Initiator, Now), Is.EqualTo(0), "accepting frees the initiator's stranger-initiation slot");
        // Post-flip shell expiry is the +1y accepted window, not the +30d pending one.
        Assert.That((reloaded.ExpiresAt.Value - Now.AddDays(365)).Duration(), Is.LessThan(TimeSpan.FromSeconds(1)),
            "a reply-accept re-stamps the shell to the +1y accepted expiry");
    }

    [Test]
    public async Task PendingSend_NowFriends_AutoAccepts()
    {
        var channel = await CreateDm(DmRequestState.Pending);
        _dmInitiationTracker.Record(Initiator, Recipient.ToLowerInvariant(), Now);
        // The two became friends mid-pending — the counterpart's snapshot now lists the sender as a friend.
        SetFriends(Recipient, Initiator);
        SeedMember(InitiatorConn, Initiator, channel.Id);
        var hub = BuildHub(InitiatorConn);

        var result = await hub.SendMessage(channel.Id, "hey friend");

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        var reloaded = await _channelRepository.Load(channel.Id);
        Assert.That(reloaded.RequestState, Is.EqualTo(DmRequestState.Accepted), "friends bypass consent — the initiator's send auto-accepts (D8)");
        Assert.That(await _messageRepository.Load(result.MessageId), Is.Not.Null, "the auto-accepting message is delivered");
        Assert.That(_dmInitiationTracker.CountActive(Initiator, Now), Is.EqualTo(0), "auto-accept frees the initiation slot");
        Assert.That((reloaded.ExpiresAt.Value - Now.AddDays(365)).Duration(), Is.LessThan(TimeSpan.FromSeconds(1)));
    }

    // ------------------------------------------------------------------------------------------------
    // (2b) Pending-phase dmPrivacy recheck — silent drop, uniform with a decline
    // ------------------------------------------------------------------------------------------------

    [Test]
    public async Task PendingSend_PrivacyFlippedToNobody_SilentDropOkShape()
    {
        var channel = await CreateDm(DmRequestState.Pending);
        SeedMember(InitiatorConn, Initiator, channel.Id);
        // The recipient tightened dmPrivacy to Nobody AFTER the pending shell was created (D8): the
        // initiator's next message is silently dropped — indistinguishable from a decline.
        await SeedPrivacy(Recipient, DmPrivacy.Nobody);
        var hub = BuildHub(InitiatorConn);

        var result = await hub.SendMessage(channel.Id, "still there?");

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok), "a dmPrivacy-recheck failure is a SILENT drop (uniform Ok shape)");
        Assert.That(result.MessageId, Is.Not.Null.And.Not.Empty);
        Assert.That(await _messageRepository.LoadForModerator(channel.Id), Is.Empty, "nothing is stored");
        var reloaded = await _channelRepository.Load(channel.Id);
        Assert.That(reloaded.LastSeq, Is.EqualTo(0L));
        Assert.That(reloaded.RequestState, Is.EqualTo(DmRequestState.Pending), "the request stays pending — the sender never learns");
    }

    // ------------------------------------------------------------------------------------------------
    // Recipient materialization + RequestReceived transition
    // ------------------------------------------------------------------------------------------------

    [Test]
    public async Task FirstDmMessage_MaterializesRecipientMembership_PushesChannelAddedFocusFalse_AndRequestReceived()
    {
        var channel = await CreateDm(DmRequestState.Pending);
        SeedMember(InitiatorConn, Initiator, channel.Id);
        await SeedPrivacy(Recipient, DmPrivacy.Everyone);
        // The recipient is online so RequestReceived + ChannelAdded reach a live connection.
        RegisterSession(RecipientConn, Recipient);
        var hub = BuildHub(InitiatorConn);

        var result = await hub.SendMessage(channel.Id, "hi, new here");
        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));

        // Membership materialized (level All, Member).
        var recipientMembership = await _membershipRepository.Load(channel.Id, Recipient);
        Assert.That(recipientMembership, Is.Not.Null, "the recipient's membership is materialized on first delivery (D4)");
        Assert.That(recipientMembership.Role, Is.EqualTo(MembershipRole.Member));
        Assert.That(recipientMembership.NotificationLevel, Is.EqualTo(NotificationLevel.All));

        // ChannelAdded with Focus == false (no auto-open) — captured on the fan-out surface.
        var added = _harness.PayloadFor(RecipientConn, ChatEvents.ChannelAdded) as ChannelAddedDto;
        Assert.That(added, Is.Not.Null, "the recipient receives a ChannelAdded on first materialization");
        Assert.That(added.Focus, Is.False, "ChannelAdded.Focus is false — the server never auto-opens the DM");
        Assert.That(_onlineMemberRegistry.IsMember(RecipientConn, channel.Id), Is.True, "PushChannelAdded seeds the recipient's registry");

        // RequestReceived transition — captured on the hub surface (targeted single-connection push).
        Assert.That(HubSignalCount(RecipientConn, ChatEvents.RequestReceived), Is.EqualTo(1), "a fresh pending request fires exactly one RequestReceived");
        var request = HubPayloadFor(RecipientConn, ChatEvents.RequestReceived) as PendingDmRequestDto;
        Assert.That(request, Is.Not.Null);
        Assert.That(request.ChannelId, Is.EqualTo(channel.Id));
        Assert.That(request.FromBattleTag, Is.EqualTo(Initiator), "the request names the initiator (RequestInitiatedBy)");
    }

    [Test]
    public async Task SecondPendingMessage_NoSecondRequestReceived()
    {
        var channel = await CreateDm(DmRequestState.Pending);
        SeedMember(InitiatorConn, Initiator, channel.Id);
        await SeedPrivacy(Recipient, DmPrivacy.Everyone);
        RegisterSession(RecipientConn, Recipient);
        var hub = BuildHub(InitiatorConn);

        await hub.SendMessage(channel.Id, "first");
        await hub.SendMessage(channel.Id, "second");

        // The tray is already live after the first request — subsequent pending messages do NOT re-notify.
        Assert.That(HubSignalCount(RecipientConn, ChatEvents.RequestReceived), Is.EqualTo(1), "RequestReceived fires ONCE, not per message");
        Assert.That(_harness.SignalCount(RecipientConn, ChatEvents.ChannelAdded), Is.EqualTo(1), "ChannelAdded fires ONCE (first materialization only)");
    }

    // ------------------------------------------------------------------------------------------------
    // Shell-expiry maintenance (the C1-amendment gap closed) + regression pins
    // ------------------------------------------------------------------------------------------------

    [Test]
    public async Task DmSend_MaintainsShellExpiry_Pending30d_Accepted1y()
    {
        // PENDING leg: an initiator send re-stamps the +30d pending shell to sendTime+30d.
        var pending = await CreateDm(DmRequestState.Pending);
        SeedMember(InitiatorConn, Initiator, pending.Id);
        await SeedPrivacy(Recipient, DmPrivacy.Everyone);
        _time.Advance(TimeSpan.FromHours(1)); // send-time differs from creation-time so we prove a re-stamp
        var sendTime = Now;
        var pendingResult = await BuildHub(InitiatorConn).SendMessage(pending.Id, "ping");
        Assert.That(pendingResult.Code, Is.EqualTo(ChatResultCode.Ok));
        var reloadedPending = await _channelRepository.Load(pending.Id);
        Assert.That((reloadedPending.ExpiresAt.Value - (sendTime + TimeSpan.FromDays(30))).Duration(), Is.LessThan(TimeSpan.FromSeconds(1)),
            "a pending Dm send maintains the +30d shell expiry off the SEND clock");

        // ACCEPTED leg: an accepted send maintains the +1y shell expiry. A DISTINCT counterpart so it does
        // not collide on the pending leg's pair-key (one conversation per pair, ever).
        const string otherRecipient = "fox#789";
        var accepted = await _channelRepository.FindOrCreateDm(Initiator, otherRecipient, Initiator, DmRequestState.Accepted, Now);
        SeedMember("conn-acc", Initiator, accepted.Id);
        var acceptedResult = await BuildHub("conn-acc").SendMessage(accepted.Id, "pong");
        Assert.That(acceptedResult.Code, Is.EqualTo(ChatResultCode.Ok));
        var reloadedAccepted = await _channelRepository.Load(accepted.Id);
        Assert.That((reloadedAccepted.ExpiresAt.Value - (sendTime + TimeSpan.FromDays(365))).Duration(), Is.LessThan(TimeSpan.FromSeconds(1)),
            "an accepted Dm send maintains the +1y shell expiry");
    }

    [Test]
    public async Task GroupSend_Maintains1y()
    {
        var group = new ChatChannel { Type = ChannelType.GroupDm, Name = "squad", LastSeq = 0, LastMessageAt = Now, ExpiresAt = Now.AddDays(365) };
        await _channelRepository.Insert(group);
        SeedMember(InitiatorConn, Initiator, group.Id, ChannelType.GroupDm);
        _time.Advance(TimeSpan.FromHours(1));
        var sendTime = Now;

        var result = await BuildHub(InitiatorConn).SendMessage(group.Id, "team update");

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        var reloaded = await _channelRepository.Load(group.Id);
        Assert.That((reloaded.ExpiresAt.Value - (sendTime + TimeSpan.FromDays(365))).Duration(), Is.LessThan(TimeSpan.FromSeconds(1)),
            "a GroupDm send maintains the +1y shell expiry off the SEND clock");
        Assert.That(await _messageRepository.Load(result.MessageId), Is.Not.Null);
    }

    [Test]
    public async Task PublicSend_NeverTouchesExpiresAt()
    {
        var channel = new ChatChannel { Type = ChannelType.Public, Name = "general", NormalizedName = ChannelNames.Normalize("general") };
        await _channelRepository.Insert(channel);
        SeedMember(InitiatorConn, Initiator, channel.Id, ChannelType.Public);

        var result = await BuildHub(InitiatorConn).SendMessage(channel.Id, "public message");

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        var reloaded = await _channelRepository.Load(channel.Id);
        Assert.That(reloaded.ExpiresAt, Is.Null, "a public send never writes ExpiresAt (regression pin — creation-anchored/permanent)");
    }

    [Test]
    public async Task DmMessage_ExpiresAt90d()
    {
        var channel = await CreateDm(DmRequestState.Accepted);
        SeedMember(InitiatorConn, Initiator, channel.Id);

        var result = await BuildHub(InitiatorConn).SendMessage(channel.Id, "dm message");

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        var persisted = await _messageRepository.Load(result.MessageId);
        Assert.That(persisted.ExpiresAt, Is.Not.Null);
        Assert.That((persisted.ExpiresAt.Value - Now.AddDays(90)).Duration(), Is.LessThan(TimeSpan.FromSeconds(1)),
            "a Dm MESSAGE gets the 90d retention window (ForChannelMessage Dm leg)");
    }

    // ------------------------------------------------------------------------------------------------
    // battleTag casing — the durable membership key must be casing-agnostic (C5 T4 fix)
    // ------------------------------------------------------------------------------------------------

    [Test]
    public async Task DmSend_MixedCaseRecipient_VisibleInRecipientSessionStateAndTray_NoDuplicateMembership()
    {
        // A real Battle.net recipient whose JWT carries UPPERCASE. DmPairKey lowercases both tags, so the
        // materialized counterpart membership is keyed lowercased — but the recipient's OWN reads use their
        // verbatim JWT casing against a case-sensitive Mongo $eq. Without membership-key normalization those
        // casings disagree: the DM is invisible to the recipient on reconnect and a later JWT-cased
        // self-OpenDm inserts a DUPLICATE row.
        const string mixedRecipient = "Wolf#456";       // verbatim (uppercase) JWT casing
        const string mixedRecipientConn = "conn-recip-mixed";

        var channel = await _channelRepository.FindOrCreateDm(
            Initiator, mixedRecipient, Initiator, DmRequestState.Pending, Now);
        SeedMember(InitiatorConn, Initiator, channel.Id);
        // dmPrivacy is read under the normalized (lowercased) counterpart in the send path, so seed it there.
        await SeedPrivacy(mixedRecipient.ToLowerInvariant(), DmPrivacy.Everyone);

        // (1) The initiator's first DM materializes the recipient's membership (keyed off the pair-key).
        var send = await BuildHub(InitiatorConn).SendMessage(channel.Id, "hi, new here");
        Assert.That(send.Code, Is.EqualTo(ChatResultCode.Ok));

        // (2) The recipient's OWN reads (verbatim JWT casing) must resolve the materialized row.
        var loadedForUser = await _membershipRepository.LoadForUser(mixedRecipient);
        Assert.That(loadedForUser.Select(m => m.ChannelId), Does.Contain(channel.Id),
            "LoadForUser under the recipient's verbatim JWT casing must find their lowercased-stored membership");
        Assert.That(await _membershipRepository.Load(channel.Id, mixedRecipient), Is.Not.Null,
            "Load under the recipient's JWT casing resolves the lowercased-stored row");

        // (3) Reassembling the recipient's SessionState under their JWT-cased identity must surface the DM
        // in Channels AND seed their OnlineMemberRegistry (GetMessages/FocusChannel/tray all depend on this).
        var recipientIdentity = new W3CUserAuthentication { BattleTag = mixedRecipient, Name = "Wolf" };
        var (dto, _) = await _assembler.AssembleAndSeed(recipientIdentity, mixedRecipientConn, Now,
            new ChatUser(recipientIdentity.BattleTag, recipientIdentity.IsAdmin, recipientIdentity.Name, new ProfilePicture(), null, null));
        Assert.That(dto.Channels.Select(c => c.Channel.Id), Does.Contain(channel.Id),
            "the DM appears in the recipient's SessionState.Channels on reconnect");
        Assert.That(_onlineMemberRegistry.IsMember(mixedRecipientConn, channel.Id), Is.True,
            "SessionState seeds the recipient's OnlineMemberRegistry for the DM (drives the tray + GetMessages)");

        // (4) The recipient then OpenDm's the initiator under their JWT casing — the existing shell must
        // resolve to their EXISTING (lowercased) membership, never a second row.
        RegisterSession(mixedRecipientConn, mixedRecipient);
        var open = await BuildHub(mixedRecipientConn).OpenDm(Initiator);
        Assert.That(open.Code, Is.EqualTo(ChatResultCode.Ok));

        var recipientRows = (await _membershipRepository.LoadForChannel(channel.Id))
            .Where(m => string.Equals(m.BattleTag, mixedRecipient, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.That(recipientRows.Count, Is.EqualTo(1),
            "exactly ONE recipient membership — the JWT-cased self-OpenDm did not insert a duplicate row");
    }

    // ------------------------------------------------------------------------------------------------
    // battleTag casing — user_settings (dmPrivacy) must be casing-agnostic too (C5 T4 fix, security re-review)
    // ------------------------------------------------------------------------------------------------

    [Test]
    public async Task PendingRecheck_MixedCaseRecipientTightensToNobody_SubsequentSendSilentlyDropped()
    {
        // A real Battle.net recipient whose JWT carries UPPERCASE. The pending-phase dmPrivacy recheck
        // (ApplyPrivateLaneGates) resolves the counterpart via ResolveDmCounterpart, which returns the
        // LOWERCASED half of the pair-key. If UserSettingsRepository keys on exact case, a setting the
        // recipient stores under their VERBATIM JWT casing is invisible to that lowercased read —
        // LoadOrDefault misses and silently falls back to the Everyone default, letting the initiator's
        // sends through past a tightened Nobody setting.
        const string mixedRecipient = "Wolf#456"; // verbatim (uppercase) JWT casing
        const string mixedRecipientConn = "conn-recip-mixed-privacy";

        var channel = await _channelRepository.FindOrCreateDm(
            Initiator, mixedRecipient, Initiator, DmRequestState.Pending, Now);
        SeedMember(InitiatorConn, Initiator, channel.Id);
        // dmPrivacy starts at the spec default (Everyone), seeded under the recipient's verbatim JWT
        // casing — exactly what a real settings doc would look like before any tightening.
        await SeedPrivacy(mixedRecipient, DmPrivacy.Everyone);
        var initiatorHub = BuildHub(InitiatorConn);

        // (1) Message 1 while dmPrivacy = Everyone: delivered and persisted.
        var first = await initiatorHub.SendMessage(channel.Id, "hi there");
        Assert.That(first.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(await _messageRepository.Load(first.MessageId), Is.Not.Null, "message 1 is persisted while dmPrivacy=Everyone");
        Assert.That((await _channelRepository.Load(channel.Id)).LastSeq, Is.EqualTo(1L));

        // (2) The recipient tightens dmPrivacy to Nobody via the REAL SetDmPrivacy path — persisted under
        // THEIR OWN JWT-cased identity (session.Identity.BattleTag), exactly as production does.
        RegisterSession(mixedRecipientConn, mixedRecipient);
        var recipientHub = BuildHub(mixedRecipientConn);
        var setPrivacy = await recipientHub.SetDmPrivacy(DmPrivacy.Nobody);
        Assert.That(setPrivacy.Code, Is.EqualTo(ChatResultCode.Ok));

        // (3) Message 2: the pending recheck must see the tightened Nobody setting the recipient just
        // stored under their mixed-case identity — NOT silently miss it and fall back to Everyone.
        var second = await initiatorHub.SendMessage(channel.Id, "still there?");

        Assert.That(second.Code, Is.EqualTo(ChatResultCode.Ok), "a dmPrivacy-recheck failure is a SILENT drop (uniform Ok shape)");
        Assert.That(second.MessageId, Is.Not.Null.And.Not.Empty, "a silent drop still fabricates a non-null messageId (D6)");
        Assert.That(second.Seq, Is.Not.Null, "a silent drop still fabricates a non-null seq (D6)");
        var reloaded = await _channelRepository.Load(channel.Id);
        Assert.That(reloaded.LastSeq, Is.EqualTo(1L), "message 2 allocates NO seq — LastSeq unchanged from message 1");
        Assert.That((await _messageRepository.LoadForModerator(channel.Id)).Count, Is.EqualTo(1),
            "message 2 is NOT persisted — only message 1 remains stored");
    }
}
