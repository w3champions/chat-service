using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
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
using W3ChampionsChatService.Sessions;
using W3ChampionsChatService.Users;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// C3 Task 20 — the TWO-CLIENT INTEGRATION + RECONNECT acceptance suite. This is the end-to-end
/// validation of the whole C3 hub protocol against the spec's acceptance criteria, driving MULTIPLE
/// real <see cref="ChatHub"/> instances (2–3 connections) through the SHIPPED connect/send/focus/
/// mark-read/disconnect/reconnect behavior while SHARING one instance of every singleton — the
/// registries, engine, limiter, coalescer, accumulator, <see cref="TicketStore"/>,
/// <see cref="SessionRegistry"/>, <see cref="ConnectionMapping"/>, the repos on the shared
/// <see cref="IntegrationTestBase.MongoClient"/> — plus ONE shared <see cref="HubPushCaptureHarness"/>
/// whose <see cref="HubPushCaptureHarness.HubContext"/> is handed to the
/// <see cref="FanOutEngine"/>/<see cref="ActivityCoalescer"/>/<see cref="ViewersAccumulator"/> so every
/// fan-out push lands in one capture. This is the <see cref="ChatHubConnectionTests"/> multi-connection
/// idiom, scaled up to several focused/level-configured members.
/// <para>
/// TIME is DETERMINISTIC: a single <see cref="FakeTimeProvider"/> drives every hub's clock, and the
/// coalescer/accumulator windows are advanced by calling <see cref="ActivityCoalescer.FlushDue"/> /
/// <see cref="ViewersAccumulator.FlushDue"/> DIRECTLY with an explicit <c>now</c> — the deterministic
/// idiom the unit suites use. There are NO wall-clock sleeps and the background
/// <see cref="FanOutFlushService"/> timer is never relied on here.
/// </para>
/// <para>
/// One deliberate real-clock seam: <see cref="ChatHub.OnConnectedAsync"/> consumes the one-time ticket
/// with <c>DateTime.UtcNow</c> (the ticket TTL is wall-clock, independent of the fan-out coalescing
/// windows), so tickets are minted with <c>DateTime.UtcNow</c> too — mirroring
/// <see cref="ChatHubConnectionTests"/>. The <see cref="FakeTimeProvider"/> governs only the fan-out /
/// assembler clock.
/// </para>
/// TWO capture surfaces, both queried per connectionId: the shared <see cref="HubPushCaptureHarness"/>
/// records the <c>IHubContext</c> fan-out pushes (<c>MessageReceived</c>/<c>ChannelActivity</c>/
/// <c>ViewersChanged</c>), while each hub's own capturing <see cref="IHubCallerClients"/> records the
/// connect-path <c>Clients.Caller</c>/<c>Clients.Client</c> pushes (<c>SessionState</c>/
/// <c>ConnectionDisplaced</c>) and any <c>Context.Abort()</c> into the shared <see cref="_hubSends"/>.
/// </summary>
public class HubProtocolIntegrationTests : IntegrationTestBase
{
    private static readonly DateTime T0 = new(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc);

    // ---- shared singletons (one instance each, shared across every hub built in a test) ------------
    private FakeTimeProvider _time;
    private HubPushCaptureHarness _harness;

    private TicketStore _ticketStore;
    private SessionRegistry _sessionRegistry;
    private ConnectionMapping _connectionMapping;
    private FocusRegistry _focusRegistry;
    private OnlineMemberRegistry _onlineMemberRegistry;
    private MessageRateLimiter _messageRateLimiter;
    private ChannelCreationRateLimiter _channelCreationRateLimiter;
    private ActivityCoalescer _activityCoalescer;
    private FanOutEngine _fanOutEngine;
    private ViewersAccumulator _viewersAccumulator;

    private ChatHistory _chatHistory;
    private UserDirectoryRepository _userDirectory;
    private MuteRepository _muteRepository;
    private MuteReconciliationService _reconcileService;
    private ChannelRepository _channelRepository;
    private MembershipRepository _membershipRepository;
    private MessageRepository _messageRepository;
    private SessionStateAssembler _assembler;
    private Mock<IChatAuthenticationService> _authService;

    // Every Clients.Caller/Client push + every Context.Abort(), in order, across ALL connections. The
    // fan-out pushes go to _harness instead — see the class doc's "TWO capture surfaces".
    private readonly List<(string ConnectionId, string Method, object Payload)> _hubSends = new();

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    [SetUp]
    public void SetupBeforeEach()
    {
        _hubSends.Clear();
        _time = new FakeTimeProvider(new DateTimeOffset(T0, TimeSpan.Zero));
        _harness = new HubPushCaptureHarness();

        _ticketStore = new TicketStore();
        _sessionRegistry = new SessionRegistry();
        _connectionMapping = new ConnectionMapping();
        _focusRegistry = new FocusRegistry();
        _onlineMemberRegistry = new OnlineMemberRegistry();
        _messageRateLimiter = new MessageRateLimiter();
        _channelCreationRateLimiter = new ChannelCreationRateLimiter();

        _chatHistory = new ChatHistory();
        _userDirectory = new UserDirectoryRepository(MongoClient);
        _muteRepository = new MuteRepository(MongoClient);
        _reconcileService = new MuteReconciliationTestHarness(_connectionMapping, _muteRepository).Service;
        _channelRepository = new ChannelRepository(MongoClient);
        _membershipRepository = new MembershipRepository(MongoClient, _channelRepository);
        _messageRepository = new MessageRepository(MongoClient);

        _authService = new Mock<IChatAuthenticationService>();
        _authService.Setup(m => m.GetUserFromIdentity(It.IsAny<W3CUserAuthentication>()))
            .ReturnsAsync((W3CUserAuthentication id) =>
                new ChatUser(id.BattleTag, id.IsAdmin, null, new ProfilePicture(), null, null));

        _assembler = new SessionStateAssembler(
            _membershipRepository,
            _channelRepository,
            _muteRepository,
            _authService.Object,
            _onlineMemberRegistry,
            _connectionMapping);

        // The three fan-out sinks ALL push through the ONE shared harness and read the SHARED
        // registries the hubs mutate — so every push lands in a single ordered capture and the
        // coalescer/accumulator see the live roster/membership/read-state the hubs produce.
        _activityCoalescer = new ActivityCoalescer(_harness.HubContext, _onlineMemberRegistry);
        _fanOutEngine = new FanOutEngine(
            _harness.HubContext, _focusRegistry, _onlineMemberRegistry, _activityCoalescer, _sessionRegistry);
        _viewersAccumulator = new ViewersAccumulator(_harness.HubContext, _focusRegistry);
    }

    // ============================================================================================
    // Fixture plumbing
    // ============================================================================================

    private static W3CUserAuthentication Identity(string battleTag) =>
        new() { BattleTag = battleTag, Name = battleTag.Split('#')[0] };

    private void SetTime(int addSeconds) =>
        _time.SetUtcNow(new DateTimeOffset(T0.AddSeconds(addSeconds), TimeSpan.Zero));

    private ChatHub BuildHub(string connectionId, string accessToken)
    {
        var hub = new ChatHub(
            _connectionMapping,
            _chatHistory,
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
            _viewersAccumulator,
            new NoOpMentionInboxCleaner());

        var clients = new Mock<IHubCallerClients>();
        clients.Setup(c => c.Caller).Returns(CapturingSingle(connectionId));
        clients.Setup(c => c.Client(It.IsAny<string>())).Returns<string>(CapturingSingle);
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(CapturingGroup());
        hub.Clients = clients.Object;

        var context = new Mock<HubCallerContext>();
        context.Setup(c => c.ConnectionId).Returns(connectionId);
        context.Setup(c => c.Features).Returns(BuildFeatures(accessToken));
        context.Setup(c => c.Abort()).Callback(() => Record(connectionId, "ABORT", null));
        hub.Context = context.Object;
        hub.Groups = new Mock<IGroupManager>().Object;

        return hub;
    }

    // Mints a fresh one-time ticket (real wall-clock, matching OnConnectedAsync's DateTime.UtcNow
    // consumption) and drives the SHIPPED connect path end-to-end: OnConnectedAsync consumes the
    // ticket, registers the session, assembles + seeds, and pushes SessionState to the caller.
    private async Task<ChatHub> Connect(string connectionId, string battleTag)
    {
        var ticket = _ticketStore.Mint(Identity(battleTag), DateTime.UtcNow);
        var hub = BuildHub(connectionId, ticket);
        await hub.OnConnectedAsync();
        return hub;
    }

    // Real Context.GetHttpContext() path (mirrors ChatHubConnectionTests): SignalR reads the
    // connection's IHttpContextFeature, never the injected IHttpContextAccessor.
    private static IFeatureCollection BuildFeatures(string accessToken)
    {
        var features = new FeatureCollection();
        var httpContext = new DefaultHttpContext();
        if (accessToken != null)
        {
            httpContext.Request.QueryString = new QueryString($"?access_token={accessToken}");
        }
        var httpContextFeature = new Mock<IHttpContextFeature>();
        httpContextFeature.Setup(f => f.HttpContext).Returns(httpContext);
        features.Set<IHttpContextFeature>(httpContextFeature.Object);
        return features;
    }

    private ISingleClientProxy CapturingSingle(string target)
    {
        var proxy = new Mock<ISingleClientProxy>();
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((method, args, _) => Record(target, method, args.Length > 0 ? args[0] : null))
            .Returns(Task.CompletedTask);
        return proxy.Object;
    }

    private IClientProxy CapturingGroup()
    {
        var proxy = new Mock<IClientProxy>();
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((method, args, _) => Record("group", method, args.Length > 0 ? args[0] : null))
            .Returns(Task.CompletedTask);
        return proxy.Object;
    }

    private void Record(string target, string method, object payload)
    {
        lock (_hubSends)
        {
            _hubSends.Add((target, method, payload));
        }
    }

    // ---- Mongo seed helpers (the "already-joined" state a real prior session would have left) -------

    private async Task<ChatChannel> CreateChannel(string name, ChannelType type = ChannelType.Public, long lastSeq = 0)
    {
        var channel = new ChatChannel
        {
            Type = type,
            Name = name,
            NormalizedName = ChannelNames.Normalize(name),
            LastSeq = lastSeq,
        };
        await _channelRepository.Insert(channel);
        return channel;
    }

    private Task SeedMembership(string channelId, string battleTag, NotificationLevel level = NotificationLevel.All, long lastReadSeq = 0) =>
        _membershipRepository.Insert(new ChannelMembership
        {
            ChannelId = channelId,
            BattleTag = battleTag,
            NotificationLevel = level,
            LastReadSeq = lastReadSeq,
            JoinedAt = Now,
        });

    // ---- capture readers ---------------------------------------------------------------------------

    private int MessageReceivedCount(string connectionId) =>
        _harness.SignalCount(connectionId, ChatEvents.MessageReceived);

    private IReadOnlyList<ChannelActivityDto> ActivityFor(string connectionId) =>
        _harness.SignalsFor(connectionId)
            .Where(s => s.Method == ChatEvents.ChannelActivity)
            .Select(s => (ChannelActivityDto)s.Payload)
            .ToList();

    private IReadOnlyList<ViewersChangedDto> ViewersChangedFor(string connectionId) =>
        _harness.SignalsFor(connectionId)
            .Where(s => s.Method == ChatEvents.ViewersChanged)
            .Select(s => (ViewersChangedDto)s.Payload)
            .ToList();

    private SessionStateDto SessionStateFor(string connectionId)
    {
        lock (_hubSends)
        {
            return _hubSends
                .Where(s => s.ConnectionId == connectionId && s.Method == ChatEvents.SessionState)
                .Select(s => (SessionStateDto)s.Payload)
                .LastOrDefault();
        }
    }

    private static ChannelDto ChannelDtoFor(SessionStateDto dto, string channelId) =>
        dto.Channels.Single(c => c.Channel.Id == channelId);

    private static bool Contains(IEnumerable<string> tags, string battleTag) =>
        tags.Any(t => string.Equals(t, battleTag, StringComparison.OrdinalIgnoreCase));

    // ============================================================================================
    // Scenario 1 — acceptance 1: the focused/unfocused fan-out split, end-to-end across 3 clients.
    // ============================================================================================

    [Test]
    public async Task TwoClients_FocusedGetsMessageReceived_UnfocusedLevelAllGetsCoalescedActivity_MentionsGetsNothing()
    {
        // Acceptance 1: three members of one public channel — one FOCUSED (the active participant who
        // also authors the burst), one UNFOCUSED at level `all`, one UNFOCUSED at level `mentions`. A
        // burst of 4 sends must fan out EXACTLY as: the focused connection receives 4 full
        // MessageReceived (the "no full payloads to unfocused" guardrail's positive side + the sender
        // echo); the level-`all` member receives COALESCED ChannelActivity spaced ≥10s carrying the
        // LATEST seq; the level-`mentions` member receives ZERO signals.
        const string FocusedTag = "focused#1";
        const string AllTag = "all#2";
        const string MentionsTag = "mentions#3";
        const string FocusedConn = "conn-focused";
        const string AllConn = "conn-all";
        const string MentionsConn = "conn-mentions";

        var channel = await CreateChannel("general");
        await SeedMembership(channel.Id, FocusedTag, NotificationLevel.All);
        await SeedMembership(channel.Id, AllTag, NotificationLevel.All);
        await SeedMembership(channel.Id, MentionsTag, NotificationLevel.Mentions);

        var focusedHub = await Connect(FocusedConn, FocusedTag);
        await Connect(AllConn, AllTag);           // connects but never focuses
        await Connect(MentionsConn, MentionsTag); // connects but never focuses

        // Only the focused member opens the channel in the foreground.
        Assert.AreEqual(ChatResultCode.Ok, (await focusedHub.FocusChannel(channel.Id)).Code);

        // Burst 4 sends at T0 (frozen). The focused member authors them, so its own focused connection
        // receives all 4 echoes; the unfocused level-All member is routed to the coalescer per send.
        for (var i = 1; i <= 4; i++)
        {
            var ack = await focusedHub.SendMessage(channel.Id, $"burst-{i}");
            Assert.AreEqual(ChatResultCode.Ok, ack.Code, $"burst send #{i} must succeed (within PerChannelBurst)");
            Assert.AreEqual(i, (int)ack.Seq, "the per-channel seq increments 1..4 across the burst");
        }

        // Focused connection: EXACTLY 4 full MessageReceived, and no coalesced activity (focused
        // connections are excluded from the activity-routing path).
        Assert.AreEqual(4, MessageReceivedCount(FocusedConn), "the focused member receives a full MessageReceived for every one of the 4 burst sends");
        Assert.AreEqual(0, ActivityFor(FocusedConn).Count, "a focused connection is never sent coalesced ChannelActivity");

        // Level-mentions member: ZERO of everything — unfocused (no MessageReceived) AND level Mentions
        // (no ChannelActivity). Its only connect signal, SessionState, went via Clients.Caller, not the
        // fan-out harness, so the harness holds nothing for it.
        Assert.AreEqual(0, _harness.SignalsFor(MentionsConn).Count, "an unfocused level-`mentions` member receives NO fan-out signals at all");

        // Level-all member: the FIRST offer opens the coalescing window and emits immediately (seq 1);
        // sends 2–4 within the 10s window collapse into a single pending keeping only the latest seq.
        var activity = ActivityFor(AllConn);
        Assert.AreEqual(1, activity.Count, "the first offer emits immediately (seq 1); sends 2–4 coalesce into one pending, not four pushes");
        Assert.AreEqual(1, activity[0].LastSeq, "the immediate emission carries seq 1");

        // A flush BEFORE the 10s window has elapsed emits nothing — the ≥10s spacing floor.
        await _activityCoalescer.FlushDue(T0.AddSeconds(5));
        Assert.AreEqual(1, ActivityFor(AllConn).Count, "a flush 5s after the last emit is below the 10s coalescing floor — nothing new");

        // Once the window is due, the coalesced pending flushes as one further activity carrying the
        // LATEST seq of the burst (4) — proving both coalescing (4 sends → 2 activities) and ≥10s spacing.
        await _activityCoalescer.FlushDue(T0.AddSeconds(10));
        activity = ActivityFor(AllConn);
        Assert.AreEqual(2, activity.Count, "the coalesced burst flushes as exactly one further activity once the 10s window is due");
        Assert.AreEqual(4, activity[^1].LastSeq, "the coalesced ChannelActivity carries the LATEST seq of the burst (4)");

        // The mentions member is STILL silent after both flushes.
        Assert.AreEqual(0, _harness.SignalsFor(MentionsConn).Count, "the level-`mentions` member stays silent across the whole burst + flush cycle");
    }

    // ============================================================================================
    // Scenario 2 — acceptance 2: >100-unread suppression, silenced until a MarkRead, end-to-end.
    // ============================================================================================

    [Test]
    public async Task ActivitySuppression_Over100Unread_SilencedUntilMarkRead()
    {
        // Acceptance 2: an unfocused level-All member whose unread exceeds 100 has its ChannelActivity
        // SUPPRESSED at emit time — silenced — until a MarkRead advances its read cursor back under the
        // threshold, after which the next due offer resumes emission. Driven end-to-end: the offers
        // happen INSIDE SendMessage's fan-out, and the MarkRead goes through the real hub method.
        const string ReaderTag = "reader#1";
        const string WriterTag = "writer#2";
        const string ReaderConn = "conn-reader";
        const string WriterConn = "conn-writer";

        // Channel seeded already far ahead (LastSeq 200) so the reader is deep in unread from the first
        // send — mirroring ChatHubMarkReadTests' pattern of driving suppression without hundreds of sends.
        var channel = await CreateChannel("general", lastSeq: 200);
        await SeedMembership(channel.Id, ReaderTag, NotificationLevel.All, lastReadSeq: 0);
        await SeedMembership(channel.Id, WriterTag, NotificationLevel.Mentions);

        await Connect(ReaderConn, ReaderTag);          // member, unfocused, level All
        var writerHub = await Connect(WriterConn, WriterTag);

        // Send #1 at T0 → allocates seq 201 → offered to the reader. unread = 201 − 0 = 201 > 100 →
        // SUPPRESSED at emit time even though the window is due (first-ever offer).
        var first = await writerHub.SendMessage(channel.Id, "m1");
        Assert.AreEqual(ChatResultCode.Ok, first.Code);
        Assert.AreEqual(201L, first.Seq);
        Assert.AreEqual(0, ActivityFor(ReaderConn).Count, "unread > 100 must SUPPRESS the ChannelActivity emission");

        // The reader marks a cursor that drops unread back under the threshold (clamped ≤ channel.LastSeq).
        var readerHubForMark = BuildHub(ReaderConn, accessToken: null);
        var mark = await readerHubForMark.MarkRead(channel.Id, 150);
        Assert.AreEqual(ChatResultCode.Ok, mark.Code, "the reader marks a partial cursor via the real hub method");

        // Send #2 at T0+11s (≥10s later, so the coalescing window is due again) → seq 202 → offered.
        // unread = 202 − 150 = 52 ≤ 100 → emission RESUMES.
        SetTime(11);
        var second = await writerHub.SendMessage(channel.Id, "m2");
        Assert.AreEqual(ChatResultCode.Ok, second.Code);
        Assert.AreEqual(202L, second.Seq);

        var activity = ActivityFor(ReaderConn);
        Assert.AreEqual(1, activity.Count, "a MarkRead that drops unread to ≤100 must re-enable emission on the next due offer");
        Assert.AreEqual(202L, activity[0].LastSeq, "the resumed ChannelActivity carries the latest seq");
    }

    // ============================================================================================
    // Scenario 3 — acceptance 4: three viewers, a mid-window joiner, roster + overlapping batch.
    // ============================================================================================

    [Test]
    public async Task ViewersChanged_ThreeViewers_MidWindowJoin_RosterPlusOverlappingBatch()
    {
        // Acceptance 4: two viewers are established; a third joins mid-window. The mid-window joiner's
        // FocusChannel response ALREADY carries the full roster (all three active viewers), AND the next
        // batched ViewersChanged REDUNDANTLY re-announces the joiner to everyone. The client applies
        // ViewersChanged.joined as an IDEMPOTENT SET UNION, so a battleTag appearing in BOTH its initial
        // FocusChannel roster and a later `joined` batch is harmless — never a duplicate.
        const string AlphaTag = "alpha#1";
        const string BravoTag = "bravo#2";
        const string CharlieTag = "charlie#3";

        var channel = await CreateChannel("general");
        await SeedMembership(channel.Id, AlphaTag);
        await SeedMembership(channel.Id, BravoTag);
        await SeedMembership(channel.Id, CharlieTag);

        var alphaHub = await Connect("conn-alpha", AlphaTag);
        var bravoHub = await Connect("conn-bravo", BravoTag);
        var charlieHub = await Connect("conn-charlie", CharlieTag);

        // Two established viewers focus at T0.
        Assert.AreEqual(ChatResultCode.Ok, (await alphaHub.FocusChannel(channel.Id)).Code);
        Assert.AreEqual(ChatResultCode.Ok, (await bravoHub.FocusChannel(channel.Id)).Code);

        // Flush the establishing window so alpha+bravo are the baseline-VIEWING set of the next window.
        await _viewersAccumulator.FlushDue(T0.AddSeconds(5));
        Assert.That(ViewersChangedFor("conn-alpha").SelectMany(v => v.Joined),
            Is.EquivalentTo(new[] { AlphaTag, BravoTag }), "the establishing flush announces alpha+bravo joined");

        // Charlie joins mid the next window.
        SetTime(6);
        var charlieFocus = await charlieHub.FocusChannel(channel.Id);
        Assert.AreEqual(ChatResultCode.Ok, charlieFocus.Code);
        Assert.That(charlieFocus.Viewers.Select(v => v.BattleTag),
            Is.EquivalentTo(new[] { AlphaTag, BravoTag, CharlieTag }),
            "the mid-window joiner's FocusChannel roster ALREADY contains all three active viewers");

        // The next shared batch redundantly re-announces charlie's join, delivered to every focused
        // viewer — INCLUDING charlie itself (the overlap the client absorbs via idempotent-set union).
        await _viewersAccumulator.FlushDue(T0.AddSeconds(10));

        var charlieBatches = ViewersChangedFor("conn-charlie");
        Assert.AreEqual(1, charlieBatches.Count, "the joiner receives the shared batch for its own window");
        Assert.IsTrue(Contains(charlieBatches[0].Joined, CharlieTag),
            "the shared batch REDUNDANTLY re-announces the joiner (client absorbs the overlap as an idempotent SET union of `joined`)");
        Assert.IsTrue(Contains(ViewersChangedFor("conn-alpha").Last().Joined, CharlieTag),
            "an already-present viewer receives the SAME batch announcing the new joiner");
    }

    // ============================================================================================
    // Scenario 4 — acceptance 8: kill the socket mid-session, reconnect reconstructs everything;
    //              NO replay buffer (history only via GetMessages).
    // ============================================================================================

    [Test]
    public async Task Reconnect_KillSocketMidSession_NewSessionStateReconstructsEverything()
    {
        // Acceptance 8: a client connects, is joined to channels, sends/receives, and marks a PARTIAL
        // read cursor; its socket is then KILLED. On reconnect (a FRESH ticket + new connectionId through
        // the SAME shared singletons) the new SessionState reproduces the channel list + unread math
        // EXACTLY (unread = channel.LastSeq − membership.LastReadSeq). And there is NO replay buffer: the
        // reconnected connection receives NO MessageReceived for messages sent while it was gone — that
        // history is available ONLY by pulling it back with GetMessages.
        const string MemberTag = "member#1";
        const string WriterTag = "writer#2";

        var general = await CreateChannel("general");
        var clan = await CreateChannel("clan", ChannelType.SemiPublic);
        await SeedMembership(general.Id, MemberTag, NotificationLevel.All);
        await SeedMembership(clan.Id, MemberTag, NotificationLevel.All);
        await SeedMembership(general.Id, WriterTag, NotificationLevel.Mentions);

        // --- live session on conn-a1 ---
        var memberHub = await Connect("conn-a1", MemberTag);
        await Connect("conn-writer", WriterTag);

        // The member focuses general and sends 2 messages (seq 1,2) — receiving its own echoes live.
        Assert.AreEqual(ChatResultCode.Ok, (await memberHub.FocusChannel(general.Id)).Code);
        Assert.AreEqual(1L, (await memberHub.SendMessage(general.Id, "hi")).Seq);
        Assert.AreEqual(2L, (await memberHub.SendMessage(general.Id, "there")).Seq);
        Assert.AreEqual(2, MessageReceivedCount("conn-a1"), "the focused member receives its own 2 sends live");

        // Partial read cursor: caught up to seq 1 only (of 2).
        Assert.AreEqual(ChatResultCode.Ok, (await memberHub.MarkRead(general.Id, 1)).Code);

        // --- KILL the socket ---
        await memberHub.OnDisconnectedAsync(null);

        // While gone, the writer posts 2 more messages (seq 3,4). With no live focused connection for the
        // member, these produce NO MessageReceived to it (there is no replay buffer to backfill later).
        var writerHub2 = BuildHub("conn-writer", accessToken: null);
        Assert.AreEqual(3L, (await writerHub2.SendMessage(general.Id, "while-gone-1")).Seq);
        Assert.AreEqual(4L, (await writerHub2.SendMessage(general.Id, "while-gone-2")).Seq);

        // --- RECONNECT on conn-a2 with a FRESH ticket, same battleTag, same shared singletons ---
        var reconnectHub = await Connect("conn-a2", MemberTag);

        var session = SessionStateFor("conn-a2");
        Assert.IsNotNull(session, "the reconnect pushes a fresh SessionState to the new caller");

        // Channel list reconstructed EXACTLY, with unread = channel.LastSeq − membership.LastReadSeq.
        var generalDto = ChannelDtoFor(session, general.Id);
        Assert.AreEqual(3L, generalDto.UnreadCount, "general unread = LastSeq(4) − LastReadSeq(1) = 3, reconstructed from the durable stores");
        Assert.IsTrue(generalDto.HasUnread);
        var clanDto = ChannelDtoFor(session, clan.Id);
        Assert.AreEqual(0L, clanDto.UnreadCount, "clan had no messages — unread 0");
        Assert.IsFalse(clanDto.HasUnread);
        Assert.AreEqual(2, session.Channels.Count, "the reconnect reproduces the FULL channel list (general + clan)");

        // NO replay buffer: the reconnected connection got its SessionState but ZERO MessageReceived —
        // the messages sent while it was gone are NOT pushed retroactively.
        Assert.AreEqual(0, MessageReceivedCount("conn-a2"),
            "there is NO replay buffer — the reconnect receives no MessageReceived for messages sent while disconnected");

        // History for the missed messages is available ONLY by pulling it back via GetMessages.
        var page = await reconnectHub.GetMessages(general.Id, beforeSeq: null, aroundSeq: null, limit: 100);
        Assert.AreEqual(ChatResultCode.Ok, page.Code);
        var seqs = page.Messages.Select(m => m.Seq).ToList();
        Assert.That(seqs, Is.EquivalentTo(new[] { 1L, 2L, 3L, 4L }), "GetMessages returns the full history, including the messages missed while disconnected");
        Assert.IsTrue(seqs.Contains(3L) && seqs.Contains(4L), "the while-disconnected messages (seq 3,4) are reachable ONLY through GetMessages");
    }

    // ============================================================================================
    // Scenario 5 — the C2 amendment (Task 14), end-to-end: a displaced-then-reconnected-and-refocused
    //              viewer within the window nets to NO ViewersChanged{left}.
    // ============================================================================================

    [Test]
    public async Task Displacement_ReconnectIsNotALeave()
    {
        // The end-to-end version of Task 14's C2 contract: bravo is an established, focused viewer whose
        // socket is DISPLACED by a second connection for the same battleTag; the displaced socket's
        // disconnect and the new socket's re-focus BOTH land within one ViewersChanged flush window. The
        // net roster delta must be EMPTY — the remaining viewer (charlie) is NEVER told bravo left — and
        // the roster still holds bravo EXACTLY once (not duplicated by the old+new sockets).
        const string CharlieTag = "charlie#1"; // stable observer
        const string BravoTag = "bravo#2";     // the socket that gets displaced

        var channel = await CreateChannel("general");
        await SeedMembership(channel.Id, CharlieTag);
        await SeedMembership(channel.Id, BravoTag);

        var charlieHub = await Connect("conn-charlie", CharlieTag);
        var oldHub = await Connect("conn-old", BravoTag);
        await charlieHub.FocusChannel(channel.Id);
        await oldHub.FocusChannel(channel.Id);

        // Flush so bravo is an ESTABLISHED viewer at the next window's baseline (a prior batch already
        // told everyone bravo joined).
        await _viewersAccumulator.FlushDue(T0.AddSeconds(5));
        Assert.IsTrue(Contains(ViewersChangedFor("conn-charlie").Last().Joined, BravoTag),
            "sanity: the establishing flush announced bravo joined to the observer");
        var charlieBatchesBefore = ViewersChangedFor("conn-charlie").Count;

        SetTime(6);

        // A second connection for bravo DISPLACES conn-old (SessionRegistry.Register semantics inside
        // OnConnectedAsync — conn-old receives ConnectionDisplaced and is aborted).
        var newHub = await Connect("conn-new", BravoTag);
        Assert.IsTrue(_hubSends.Any(s => s.ConnectionId == "conn-old" && s.Method == ChatEvents.ConnectionDisplaced),
            "the displaced OLD socket receives ConnectionDisplaced");

        // conn-old's disconnect routes its focus removal through the accumulator BEFORE FocusRegistry
        // clears it — baseline captured = VIEWING (the removal hasn't happened yet).
        await oldHub.OnDisconnectedAsync(null);

        // The NEW connection re-focuses the SAME channel with the SAME battleTag WITHIN the window —
        // restoring bravo to the roster (current = viewing again).
        Assert.AreEqual(ChatResultCode.Ok, (await newHub.FocusChannel(channel.Id)).Code);

        // The final flush: current (viewing, via conn-new) == baseline (viewing) → NO delta.
        await _viewersAccumulator.FlushDue(T0.AddSeconds(10));

        Assert.AreEqual(charlieBatchesBefore, ViewersChangedFor("conn-charlie").Count,
            "the displaced-then-reconnected viewer nets to no delta — the observer receives NO further batch");
        Assert.IsFalse(ViewersChangedFor("conn-charlie").Any(v => Contains(v.Left, BravoTag)),
            "a reconnect within the window is NOT a leave — the remaining viewer is never told bravo left");

        // The roster still holds bravo EXACTLY once — the old+new sockets collapse to one entry.
        var roster = _focusRegistry.GetRoster(channel.Id);
        Assert.AreEqual(1, roster.Count(t => string.Equals(t, BravoTag, StringComparison.OrdinalIgnoreCase)),
            "the roster contains the reconnected battleTag exactly once");
        Assert.IsTrue(Contains(roster, CharlieTag), "the observer is still on the roster");
    }

    // ============================================================================================
    // Scenario 6 — program acceptance: unread arithmetic is correct across clients.
    // ============================================================================================

    [Test]
    public async Task ReadStateMath_LastSeqMinusLastReadSeq_AcrossClients()
    {
        // Program acceptance: unread is computed as channel.LastSeq − each client's OWN LastReadSeq.
        // Two clients share a channel; sends advance the single channel LastSeq; one client marks a
        // PARTIAL cursor while the other never reads. On reconnect each client's assembled SessionState
        // reflects its OWN unread — MarkRead and reconnect-assembly AGREE, and the two clients diverge
        // exactly by their differing cursors.
        const string XTag = "xavier#1";
        const string YTag = "yolanda#2";

        var channel = await CreateChannel("general");
        await SeedMembership(channel.Id, XTag, NotificationLevel.All);
        await SeedMembership(channel.Id, YTag, NotificationLevel.Mentions);

        var xHub = await Connect("conn-x1", XTag);
        var yHub = await Connect("conn-y1", YTag);

        // Both start caught up: at connect, LastSeq 0 → unread 0 for each.
        Assert.AreEqual(0L, ChannelDtoFor(SessionStateFor("conn-x1"), channel.Id).UnreadCount, "X starts at unread 0");
        Assert.AreEqual(0L, ChannelDtoFor(SessionStateFor("conn-y1"), channel.Id).UnreadCount, "Y starts at unread 0");

        // X sends 5 messages → channel.LastSeq = 5 (shared across both clients).
        for (var i = 1; i <= 5; i++)
        {
            Assert.AreEqual((long)i, (await xHub.SendMessage(channel.Id, $"m{i}")).Seq);
        }

        // X marks a PARTIAL cursor (3 of 5); Y never reads (cursor stays 0).
        Assert.AreEqual(ChatResultCode.Ok, (await xHub.MarkRead(channel.Id, 3)).Code);

        // Cross-check the raw stores: unread must equal channel.LastSeq − each client's own LastReadSeq.
        var reloaded = await _channelRepository.Load(channel.Id);
        var xMembership = await _membershipRepository.Load(channel.Id, XTag);
        var yMembership = await _membershipRepository.Load(channel.Id, YTag);
        Assert.AreEqual(5L, reloaded.LastSeq);
        Assert.AreEqual(3L, xMembership.LastReadSeq, "X's MarkRead(3) persisted");
        Assert.AreEqual(0L, yMembership.LastReadSeq, "Y never read");

        // Reconnect X (kill conn-x1, fresh ticket on conn-x2): unread = 5 − 3 = 2.
        await xHub.OnDisconnectedAsync(null);
        await Connect("conn-x2", XTag);
        var xReconnect = ChannelDtoFor(SessionStateFor("conn-x2"), channel.Id);
        Assert.AreEqual(reloaded.LastSeq - xMembership.LastReadSeq, xReconnect.UnreadCount);
        Assert.AreEqual(2L, xReconnect.UnreadCount, "X reconnect unread = LastSeq(5) − LastReadSeq(3) = 2 — MarkRead and reconnect-assembly agree");
        Assert.IsTrue(xReconnect.HasUnread);

        // Reconnect Y (kill conn-y1, fresh ticket on conn-y2): unread = 5 − 0 = 5.
        await yHub.OnDisconnectedAsync(null);
        await Connect("conn-y2", YTag);
        var yReconnect = ChannelDtoFor(SessionStateFor("conn-y2"), channel.Id);
        Assert.AreEqual(reloaded.LastSeq - yMembership.LastReadSeq, yReconnect.UnreadCount);
        Assert.AreEqual(5L, yReconnect.UnreadCount, "Y reconnect unread = LastSeq(5) − LastReadSeq(0) = 5 — the two clients diverge by exactly their cursors");
        Assert.IsTrue(yReconnect.HasUnread);
    }
}
