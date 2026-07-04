using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
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
/// C5 Task 6: the consent state machine's user-facing half — <c>AcceptRequest(channelId)</c> /
/// <c>DeclineRequest(channelId)</c> — plus the byte-equality decline-invisibility proof. Covers the
/// accept transition (permanent flip, tray empties, D4 activity resumes), the guard matrix (initiator
/// cannot accept, already-accepted idempotency, accepted-cannot-be-declined, missing/non-Dm/non-member/
/// no-session rejects), the decline suppression window (tray-removed 24h, storage continues, history
/// readable, resurface after 24h), the channel-doc no-write pin (D3), and the marquee
/// <see cref="Sender_ObservesIdenticalBehavior_PendingVsDeclined"/> — the SENDER observes an IDENTICAL
/// result/event/SessionState surface whether the recipient does nothing or declines.
/// <para>
/// Direct-hub idiom (mirrors <see cref="ChatHubDmSendTests"/>): a real <see cref="RelationshipProvider"/>
/// over a <see cref="FakeRelationshipSource"/> (NEVER HTTP), a <see cref="FakeTimeProvider"/> for the 24h
/// windows, a REAL <see cref="FanOutEngine"/> wired to a <see cref="HubPushCaptureHarness"/> (so
/// ChannelActivity resumption and any sender-visible leak are observable), and the hub's own
/// <c>Clients.Client</c> proxy capturing the targeted <c>RequestReceived</c>. NUnit constraint style.
/// </para>
/// </summary>
public class ChatHubConsentTests : IntegrationTestBase
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

    private readonly Dictionary<string, HashSet<string>> _friends = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _blocked = new(StringComparer.OrdinalIgnoreCase);

    // Every (connectionId, method, payload) the HUB itself pushed via Clients.Caller/Clients.Client.
    private readonly List<(string ConnectionId, string Method, object Payload)> _hubSends = new();

    // ConnectionIds configured (via ThrowOnSend) to fault on every subsequent hub-direct SendAsync/
    // SendCoreAsync — mirrors HubPushCaptureHarness.ThrowOnSend, but for THIS file's own Clients.Client
    // mock (below). The hub's RequestReceived push (MaterializeDmRecipientAndNotify) goes out via the
    // hub's OWN IHubCallerClients — NOT through FanOutEngine's _harness.HubContext — so it is not
    // reachable via _harness.ThrowOnSend; this is the throwing-send hook for THAT path (C5 LOW-1).
    private readonly Dictionary<string, Exception> _throwingConnections = new();

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
        _fanOutEngine = new FanOutEngine(_harness.HubContext, _focusRegistry, _onlineMemberRegistry, _coalescer, _sessionRegistry);

        _relationshipSource = new FakeRelationshipSource((tag, now) => new RelationshipSnapshot(
            tag,
            _friends.TryGetValue(tag, out var f) ? f : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            _blocked.TryGetValue(tag, out var b) ? b : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            now));
        _relationshipProvider = new RelationshipProvider(_relationshipSource, _time);

        var authService = new Mock<IChatAuthenticationService>();
        authService.Setup(m => m.GetUserFromIdentity(It.IsAny<W3CUserAuthentication>()))
            .ReturnsAsync((W3CUserAuthentication id) =>
                new ChatUser(id.BattleTag, id.IsAdmin, id.Name, new ProfilePicture(), null, null));
        _assembler = new SessionStateAssembler(
            _membershipRepository,
            _channelRepository,
            _messageRepository,
            new MuteRepository(MongoClient),
            authService.Object,
            _onlineMemberRegistry,
            _connectionMapping);
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
            _dmInitiationTracker);

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

    private ISingleClientProxy CapturingProxy(string connId)
    {
        var proxy = new Mock<ISingleClientProxy>();
        proxy
            .Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns<string, object[], CancellationToken>((method, args, _) =>
            {
                Exception exceptionToThrow;
                lock (_hubSends)
                {
                    _throwingConnections.TryGetValue(connId, out exceptionToThrow);
                }

                if (exceptionToThrow != null)
                {
                    return Task.FromException(exceptionToThrow);
                }

                lock (_hubSends)
                {
                    _hubSends.Add((connId, method, args.Length > 0 ? args[0] : null));
                }
                return Task.CompletedTask;
            });
        return proxy.Object;
    }

    /// <summary>
    /// Configures every subsequent hub-direct <c>SendAsync</c>/<c>SendCoreAsync</c> call to
    /// <paramref name="connectionId"/> to fault instead of recording — simulating a recipient connection
    /// torn down mid-push (C5 LOW-1: the <see cref="ChatEvents.RequestReceived"/> push in
    /// <c>MaterializeDmRecipientAndNotify</c>). Mirrors <see cref="HubPushCaptureHarness.ThrowOnSend"/>.
    /// </summary>
    private void ThrowOnSend(string connectionId, Exception exception = null)
    {
        lock (_hubSends)
        {
            _throwingConnections[connectionId] = exception ?? new InvalidOperationException($"Simulated send failure for connection '{connectionId}'");
        }
    }

    private void RegisterSession(string connectionId, string battleTag) =>
        _sessionRegistry.Register(
            connectionId,
            new W3CUserAuthentication { BattleTag = battleTag, Name = battleTag.Split('#')[0] },
            null);

    private static W3CUserAuthentication Identity(string battleTag) =>
        new() { BattleTag = battleTag, Name = battleTag.Split('#')[0] };

    // Seeds a connection the way the connect/first-message path does: live session, cached ChatUser, and
    // a Dm OnlineMemberRegistry entry so the hot-path IsMember gate passes.
    private void SeedMember(string connectionId, string battleTag, string channelId, ChannelType type = ChannelType.Dm)
    {
        RegisterSession(connectionId, battleTag);
        _connectionMapping.RegisterUser(connectionId, new ChatUser(battleTag, false, battleTag.Split('#')[0], new ProfilePicture(), null, null));
        _onlineMemberRegistry.Join(channelId, connectionId, new MemberState(battleTag, NotificationLevel.All, 0, type));
    }

    // Lazily materializes the recipient's membership (as T4's first-delivery path would) and seeds their
    // registry + session, so the recipient hub can Accept/Decline without a real send.
    private async Task SeedRecipientMembership(string channelId)
    {
        await _membershipRepository.InsertIfAbsent(new ChannelMembership
        {
            ChannelId = channelId,
            BattleTag = Recipient,
            Role = MembershipRole.Member,
            NotificationLevel = NotificationLevel.All,
            JoinedAt = Now,
        });
        RegisterSession(RecipientConn, Recipient);
        _onlineMemberRegistry.Join(channelId, RecipientConn, new MemberState(Recipient, NotificationLevel.All, 0, ChannelType.Dm));
    }

    private Task SeedDirectory(string battleTag) =>
        _userDirectory.Upsert(new UserDirectoryEntry { BattleTag = battleTag, LastSeenAt = Now });

    private Task SeedPrivacy(string battleTag, DmPrivacy privacy) =>
        _userSettings.Upsert(new UserSettings { BattleTag = battleTag, DmPrivacy = privacy });

    private void SetBlocked(string battleTag, params string[] blocked) =>
        _blocked[battleTag] = new HashSet<string>(blocked, StringComparer.OrdinalIgnoreCase);

    private Task<ChatChannel> CreateDm(DmRequestState state) =>
        _channelRepository.FindOrCreateDm(Initiator, Recipient, Initiator, state, Now);

    private int HubSignalCount(string connectionId, string method)
    {
        lock (_hubSends)
        {
            return _hubSends.Count(s => s.ConnectionId == connectionId && s.Method == method);
        }
    }

    // ------------------------------------------------------------------------------------------------
    // AcceptRequest — happy path + guard matrix
    // ------------------------------------------------------------------------------------------------

    [Test]
    public async Task Accept_FlipsToAccepted_Permanent_TrayEmpties_ActivityResumes()
    {
        var channel = await CreateDm(DmRequestState.Pending);
        _dmInitiationTracker.Record(Initiator, Recipient.ToLowerInvariant(), Now);
        SeedMember(InitiatorConn, Initiator, channel.Id);
        await SeedPrivacy(Recipient, DmPrivacy.Everyone);
        RegisterSession(RecipientConn, Recipient);
        var initiatorHub = BuildHub(InitiatorConn);

        // First (pending) message: materializes the recipient, but pings NO ChannelActivity (D4 suppression).
        await initiatorHub.SendMessage(channel.Id, "hi, new here");
        Assert.That(_harness.SignalCount(RecipientConn, ChatEvents.ChannelActivity), Is.EqualTo(0),
            "a pending request pings no activity to the recipient (D4)");

        // The recipient accepts.
        var recipientHub = BuildHub(RecipientConn);
        var accept = await recipientHub.AcceptRequest(channel.Id);

        Assert.That(accept.Code, Is.EqualTo(ChatResultCode.Ok));
        var reloaded = await _channelRepository.Load(channel.Id);
        Assert.That(reloaded.RequestState, Is.EqualTo(DmRequestState.Accepted), "accept flips the request permanently");
        Assert.That((reloaded.ExpiresAt.Value - Now.AddDays(365)).Duration(), Is.LessThan(TimeSpan.FromSeconds(1)),
            "accept re-stamps the +1y accepted-shell expiry");
        Assert.That(_dmInitiationTracker.CountActive(Initiator, Now), Is.EqualTo(0), "accept frees the initiator's stranger-initiation slot");

        // The tray empties for the recipient; the DM stays a normal channel.
        var (dto, _) = await _assembler.AssembleAndSeed(Identity(Recipient), "conn-tray", Now);
        Assert.That(dto.PendingDmRequests, Is.Empty, "an accepted request no longer appears in the tray");
        Assert.That(dto.Channels.Select(c => c.Channel.Id), Does.Contain(channel.Id), "the accepted DM remains a normal channel");

        // Activity resumes: a new (accepted) message pings the recipient exactly once (first offer emits immediately).
        var second = await initiatorHub.SendMessage(channel.Id, "you there?");
        Assert.That(second.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(_harness.SignalCount(RecipientConn, ChatEvents.ChannelActivity), Is.EqualTo(1),
            "after accept, a new message resumes ChannelActivity to the recipient (D4 suppression lifted)");
    }

    [Test]
    public async Task Accept_ByInitiator_PermissionDenied()
    {
        var channel = await CreateDm(DmRequestState.Pending);
        SeedMember(InitiatorConn, Initiator, channel.Id);
        var hub = BuildHub(InitiatorConn);

        var result = await hub.AcceptRequest(channel.Id);

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.PermissionDenied), "the initiator cannot accept their own request");
        Assert.That((await _channelRepository.Load(channel.Id)).RequestState, Is.EqualTo(DmRequestState.Pending), "the request stays pending");
    }

    [Test]
    public async Task Accept_AlreadyAccepted_IdempotentOk()
    {
        var channel = await CreateDm(DmRequestState.Accepted);
        await SeedRecipientMembership(channel.Id);
        var hub = BuildHub(RecipientConn);

        var result = await hub.AcceptRequest(channel.Id);

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok), "accepting an already-accepted request is an idempotent Ok");
        Assert.That((await _channelRepository.Load(channel.Id)).RequestState, Is.EqualTo(DmRequestState.Accepted));
    }

    [Test]
    public async Task Decline_OnAccepted_PermissionDenied()
    {
        var channel = await CreateDm(DmRequestState.Accepted);
        await SeedRecipientMembership(channel.Id);
        var hub = BuildHub(RecipientConn);

        var result = await hub.DeclineRequest(channel.Id);

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.PermissionDenied), "an accepted conversation cannot be declined");
        Assert.That((await _membershipRepository.Load(channel.Id, Recipient)).DeclinedUntil, Is.Null,
            "no decline window is stamped on an accepted conversation");
    }

    [Test]
    public async Task AcceptAndDecline_MissingChannel_NotFound()
    {
        RegisterSession(RecipientConn, Recipient);
        var hub = BuildHub(RecipientConn);
        var missingId = MongoDB.Bson.ObjectId.GenerateNewId().ToString();

        Assert.That((await hub.AcceptRequest(missingId)).Code, Is.EqualTo(ChatResultCode.NotFound));
        Assert.That((await hub.DeclineRequest(missingId)).Code, Is.EqualTo(ChatResultCode.NotFound));
    }

    [Test]
    public async Task AcceptAndDecline_NonDm_PermissionDenied()
    {
        var channel = new ChatChannel { Type = ChannelType.Public, Name = "general", NormalizedName = ChannelNames.Normalize("general") };
        await _channelRepository.Insert(channel);
        RegisterSession(RecipientConn, Recipient);
        _onlineMemberRegistry.Join(channel.Id, RecipientConn, new MemberState(Recipient, NotificationLevel.All, 0, ChannelType.Public));
        var hub = BuildHub(RecipientConn);

        Assert.That((await hub.AcceptRequest(channel.Id)).Code, Is.EqualTo(ChatResultCode.PermissionDenied));
        Assert.That((await hub.DeclineRequest(channel.Id)).Code, Is.EqualTo(ChatResultCode.PermissionDenied));
    }

    [Test]
    public async Task AcceptAndDecline_NonMember_PermissionDenied()
    {
        var channel = await CreateDm(DmRequestState.Pending);
        // A live session with NO membership for this channel (registry never seeded).
        const string strangerConn = "conn-stranger";
        RegisterSession(strangerConn, "eve#999");
        var hub = BuildHub(strangerConn);

        Assert.That((await hub.AcceptRequest(channel.Id)).Code, Is.EqualTo(ChatResultCode.PermissionDenied));
        Assert.That((await hub.DeclineRequest(channel.Id)).Code, Is.EqualTo(ChatResultCode.PermissionDenied));
    }

    [Test]
    public async Task AcceptAndDecline_NoSession_FailClosed_PermissionDenied()
    {
        var channel = await CreateDm(DmRequestState.Pending);
        var hub = BuildHub("conn-ghost"); // no session registered

        Assert.That((await hub.AcceptRequest(channel.Id)).Code, Is.EqualTo(ChatResultCode.PermissionDenied));
        Assert.That((await hub.DeclineRequest(channel.Id)).Code, Is.EqualTo(ChatResultCode.PermissionDenied));
    }

    // ------------------------------------------------------------------------------------------------
    // DeclineRequest — suppression window, storage continues, history readable, resurface, no channel write
    // ------------------------------------------------------------------------------------------------

    [Test]
    public async Task Decline_RemovesFromTray_For24h_MessagesStillStored_HistoryReadable()
    {
        var channel = await CreateDm(DmRequestState.Pending);
        _dmInitiationTracker.Record(Initiator, Recipient.ToLowerInvariant(), Now);
        SeedMember(InitiatorConn, Initiator, channel.Id);
        await SeedPrivacy(Recipient, DmPrivacy.Everyone);
        RegisterSession(RecipientConn, Recipient);
        var initiatorHub = BuildHub(InitiatorConn);

        await initiatorHub.SendMessage(channel.Id, "message one");
        await initiatorHub.SendMessage(channel.Id, "message two");

        // Pre-decline: the request is in the recipient's tray.
        var (before, _) = await _assembler.AssembleAndSeed(Identity(Recipient), "conn-tray-1", Now);
        Assert.That(before.PendingDmRequests.Select(r => r.ChannelId), Does.Contain(channel.Id), "the request is in the tray before decline");

        // The recipient declines.
        var recipientHub = BuildHub(RecipientConn);
        var decline = await recipientHub.DeclineRequest(channel.Id);
        Assert.That(decline.Code, Is.EqualTo(ChatResultCode.Ok));

        // (a) The tray drops it for 24h, but the DM stays in Channels (open-later shows full history).
        var (after, _) = await _assembler.AssembleAndSeed(Identity(Recipient), "conn-tray-2", Now);
        Assert.That(after.PendingDmRequests, Is.Empty, "a declined request is suppressed from the tray for 24h");
        Assert.That(after.Channels.Select(c => c.Channel.Id), Does.Contain(channel.Id), "the declined DM still appears in Channels");

        // (b) Full history is readable, and (c) FocusChannel still works (Dm returns an empty roster, D11).
        var history = await recipientHub.GetMessages(channel.Id, null, null, 50);
        Assert.That(history.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(history.Messages.Count, Is.EqualTo(2), "both pre-decline messages remain stored and readable");
        var focus = await recipientHub.FocusChannel(channel.Id);
        Assert.That(focus.Code, Is.EqualTo(ChatResultCode.Ok));

        // (d) The sender keeps sending within the cap — decline never blocks storage.
        var third = await initiatorHub.SendMessage(channel.Id, "message three");
        Assert.That(third.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(await _messageRepository.Load(third.MessageId), Is.Not.Null, "post-decline sends within the cap still persist");
    }

    [Test]
    public async Task Decline_After24h_NextMessage_SurfacesFreshRequest()
    {
        var channel = await CreateDm(DmRequestState.Pending);
        _dmInitiationTracker.Record(Initiator, Recipient.ToLowerInvariant(), Now);
        SeedMember(InitiatorConn, Initiator, channel.Id);
        await SeedPrivacy(Recipient, DmPrivacy.Everyone);
        RegisterSession(RecipientConn, Recipient);
        var initiatorHub = BuildHub(InitiatorConn);

        await initiatorHub.SendMessage(channel.Id, "first"); // materialize + RequestReceived #1
        Assert.That(HubSignalCount(RecipientConn, ChatEvents.RequestReceived), Is.EqualTo(1));

        var recipientHub = BuildHub(RecipientConn);
        await recipientHub.DeclineRequest(channel.Id);
        Assert.That((await _membershipRepository.Load(channel.Id, Recipient)).DeclinedUntil, Is.Not.Null, "decline stamps the suppression window");

        // Inside the window: still suppressed, no fresh RequestReceived.
        _time.Advance(TimeSpan.FromHours(23));
        await initiatorHub.SendMessage(channel.Id, "still within window");
        var (mid, _) = await _assembler.AssembleAndSeed(Identity(Recipient), "conn-mid", Now);
        Assert.That(mid.PendingDmRequests, Is.Empty, "still suppressed inside the 24h window");
        Assert.That(HubSignalCount(RecipientConn, ChatEvents.RequestReceived), Is.EqualTo(1), "no fresh RequestReceived inside the window");

        // Past the window: the next message resurfaces a fresh request (clears DeclinedUntil, re-fires, tray re-populates).
        _time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));
        await initiatorHub.SendMessage(channel.Id, "after 24h");
        Assert.That((await _membershipRepository.Load(channel.Id, Recipient)).DeclinedUntil, Is.Null, "the resurface path clears the decline window (T4)");
        Assert.That(HubSignalCount(RecipientConn, ChatEvents.RequestReceived), Is.EqualTo(2), "a fresh RequestReceived fires after the window elapses");
        var (after, _) = await _assembler.AssembleAndSeed(Identity(Recipient), "conn-after", Now);
        Assert.That(after.PendingDmRequests.Select(r => r.ChannelId), Does.Contain(channel.Id), "the tray re-populates after the window");
    }

    [Test]
    public async Task RequestReceivedPushThrows_SendStillOk_MessagePersisted()
    {
        // C5 LOW-1 (security review): the RequestReceived push in MaterializeDmRecipientAndNotify
        // (ChatHub.Dm.cs) is the LONE un-fault-isolated live push on the send path — every sibling live
        // push (FanOutEngine.OnMessagePersisted/PushChannelAdded/PushChannelRemoved) wraps its SendAsync
        // in a best-effort try/catch, but this one is a bare await. If the recipient's connection is torn
        // down mid-push, that exception must NOT propagate out of SendMessage — the message is already
        // durably persisted (step 7, BEFORE this post-persist hook at step 7.5), and the pipeline's own
        // guardrail is that an already-persisted send returns Ok regardless of fan-out hiccups.
        // <para>
        // Exercised on the DECLINE-CORRELATED resurface path specifically — the recipient declines, the
        // 24h suppression window elapses, and the initiator's next message resurfaces a fresh
        // RequestReceived. This is exactly the marquee decline-invisibility surface: were this push to
        // leak an error out to the initiator only on this path, the initiator would observe an error-ack
        // the pure-ignore path never produces — a leak of the recipient's decline.
        // </para>
        var channel = await CreateDm(DmRequestState.Pending);
        _dmInitiationTracker.Record(Initiator, Recipient.ToLowerInvariant(), Now);
        SeedMember(InitiatorConn, Initiator, channel.Id);
        await SeedPrivacy(Recipient, DmPrivacy.Everyone);
        RegisterSession(RecipientConn, Recipient);
        var initiatorHub = BuildHub(InitiatorConn);

        var first = await initiatorHub.SendMessage(channel.Id, "first"); // materializes + RequestReceived #1 (no throw yet)
        Assert.That(first.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(HubSignalCount(RecipientConn, ChatEvents.RequestReceived), Is.EqualTo(1));

        var recipientHub = BuildHub(RecipientConn);
        var decline = await recipientHub.DeclineRequest(channel.Id);
        Assert.That(decline.Code, Is.EqualTo(ChatResultCode.Ok));

        // Past the 24h window: the NEXT message resurfaces a fresh RequestReceived (clears DeclinedUntil,
        // re-fires) — exactly the branch that reaches the throwing SendAsync below.
        _time.Advance(ChatLimits.DmDeclineSuppression + TimeSpan.FromMinutes(1));

        // The recipient's live connection is torn down right as the resurfaced RequestReceived would push.
        ThrowOnSend(RecipientConn);

        SendMessageResult result = null;
        Assert.DoesNotThrowAsync(
            async () => result = await initiatorHub.SendMessage(channel.Id, "resurfacing message"),
            "a torn-down recipient connection during the resurfaced RequestReceived push must not propagate " +
            "out of SendMessage — the message is already persisted (C5 LOW-1 fault-isolation fix)");

        Assert.That(result, Is.Not.Null, "SendMessage must return a typed result, not throw");
        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok), "the initiator still gets Ok even though the recipient's push faulted");
        Assert.That(result.MessageId, Is.Not.Null);
        Assert.That(result.Seq, Is.EqualTo(first.Seq + 1), "the seq advanced — the message was durably persisted despite the push fault");

        // The message IS durably persisted and readable despite the push fault.
        var history = await initiatorHub.GetMessages(channel.Id, null, null, 50);
        Assert.That(history.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(history.Messages.Select(m => m.Content), Does.Contain("resurfacing message"),
            "the message persists even though the recipient's RequestReceived push threw");

        // DeclinedUntil clears BEFORE the throwing push (durable state runs first, the notify is best-effort
        // last) — it must stick even though the push itself failed.
        Assert.That((await _membershipRepository.Load(channel.Id, Recipient)).DeclinedUntil, Is.Null,
            "the resurface path clears the recipient's decline window regardless of the push fault");
    }

    [Test]
    public async Task Decline_NeverWritesChannelDoc()
    {
        var channel = await CreateDm(DmRequestState.Pending);
        await SeedRecipientMembership(channel.Id);
        var before = (await _channelRepository.Load(channel.Id)).ToJson();

        var recipientHub = BuildHub(RecipientConn);
        var decline = await recipientHub.DeclineRequest(channel.Id);
        Assert.That(decline.Code, Is.EqualTo(ChatResultCode.Ok));

        var after = (await _channelRepository.Load(channel.Id)).ToJson();
        Assert.That(after, Is.EqualTo(before), "DeclineRequest must NEVER write the channel doc (D3 placement pin) — decline lives on the recipient's membership only");
        // And the decline DID land where it belongs: the recipient's own membership row.
        Assert.That((await _membershipRepository.Load(channel.Id, Recipient)).DeclinedUntil, Is.Not.Null);
    }

    // ------------------------------------------------------------------------------------------------
    // MARQUEE — the sender observes IDENTICAL behavior whether the recipient does nothing or declines.
    // ------------------------------------------------------------------------------------------------

    [Test]
    public async Task Sender_ObservesIdenticalBehavior_PendingVsDeclined()
    {
        // Two isomorphic fixtures (same battleTags modulo a suffix, same FakeTimeProvider script). Scenario
        // A: the recipient does nothing. Scenario B: the recipient declines. The SENDER's every SendMessage
        // result, every event pushed to its connection, and its assembled SessionState (all id-normalized)
        // must be DEEP-EQUAL — decline writes ONLY the recipient's membership, never anything the sender sees.
        var a = await RunSenderScenario("a", recipientDeclines: false);
        var b = await RunSenderScenario("b", recipientDeclines: true);

        Assert.That(b.SendResults, Is.EqualTo(a.SendResults),
            "the sender's SendMessage results are identical pending-vs-declined");
        Assert.That(b.SenderEvents, Is.EqualTo(a.SenderEvents),
            "the sender receives an identical set of connection events pending-vs-declined (here: none)");
        Assert.That(b.SenderSessionStateJson, Is.EqualTo(a.SenderSessionStateJson),
            "the sender's assembled SessionState is byte-identical pending-vs-declined");
    }

    private sealed record SenderObservation(
        IReadOnlyList<string> SendResults,
        IReadOnlyList<string> SenderEvents,
        string SenderSessionStateJson);

    private async Task<SenderObservation> RunSenderScenario(string suffix, bool recipientDeclines)
    {
        var initiator = $"sender-{suffix}#1";
        var recipient = $"recip-{suffix}#2";
        var initiatorConn = $"init-{suffix}";
        var recipientConn = $"recipient-{suffix}";

        await SeedDirectory(recipient);
        await SeedPrivacy(recipient, DmPrivacy.Everyone);
        RegisterSession(initiatorConn, initiator);
        _connectionMapping.RegisterUser(initiatorConn, new ChatUser(initiator, false, initiator.Split('#')[0], new ProfilePicture(), null, null));
        RegisterSession(recipientConn, recipient);

        var initiatorHub = BuildHub(initiatorConn);
        var open = await initiatorHub.OpenDm(recipient);
        var channelId = open.Channel.Id;

        var sendResults = new List<string>
        {
            NormalizeSendResult(await initiatorHub.SendMessage(channelId, "msg 1")),
        };

        if (recipientDeclines)
        {
            var recipientHub = BuildHub(recipientConn);
            var decline = await recipientHub.DeclineRequest(channelId);
            Assert.That(decline.Code, Is.EqualTo(ChatResultCode.Ok), "the recipient's decline itself succeeds (scenario B)");
        }

        sendResults.Add(NormalizeSendResult(await initiatorHub.SendMessage(channelId, "msg 2")));
        sendResults.Add(NormalizeSendResult(await initiatorHub.SendMessage(channelId, "msg 3")));

        // Every event that reached the SENDER's connection — from the hub surface (RequestReceived etc.)
        // AND the fan-out surface (MessageReceived/ChannelActivity/ChannelAdded). Both are expected empty;
        // ANY sender-visible artifact injected by a broken DeclineRequest would break the A==B equality.
        var senderEvents = new List<string>();
        lock (_hubSends)
        {
            senderEvents.AddRange(_hubSends
                .Where(s => s.ConnectionId == initiatorConn)
                .Select(s => $"{s.Method}|{Normalize(JsonSerializer.Serialize(s.Payload), initiator, recipient, channelId)}"));
        }
        senderEvents.AddRange(_harness.SignalsFor(initiatorConn)
            .Select(s => $"{s.Method}|{Normalize(JsonSerializer.Serialize(s.Payload), initiator, recipient, channelId)}"));
        senderEvents.Sort(StringComparer.Ordinal);

        var (dto, _) = await _assembler.AssembleAndSeed(Identity(initiator), $"assemble-{suffix}", Now);
        var sessionJson = Normalize(JsonSerializer.Serialize(dto), initiator, recipient, channelId);

        return new SenderObservation(sendResults, senderEvents, sessionJson);
    }

    private static string NormalizeSendResult(SendMessageResult r) =>
        $"Code={r.Code};RetryAfter={r.RetryAfterSeconds};MessageIdPresent={r.MessageId is not null};Seq={r.Seq}";

    private static string Normalize(string json, string initiator, string recipient, string channelId) =>
        json
            .Replace(channelId, "<CID>")
            .Replace(initiator, "<INIT>")
            .Replace(recipient, "<RECIP>")
            .Replace(initiator.Split('#')[0], "<INITNAME>")
            .Replace(recipient.Split('#')[0], "<RECIPNAME>");

    // ------------------------------------------------------------------------------------------------
    // Block-uniformity sanity: a blocked recipient can still Accept/Decline — the block never leaks into
    // the consent surface, and accept/decline behave normally (block only silences the DELIVERY, T4).
    // ------------------------------------------------------------------------------------------------

    [Test]
    public async Task Decline_ByBlockedRecipient_Ok_NoChannelWrite()
    {
        var channel = await CreateDm(DmRequestState.Pending);
        await SeedRecipientMembership(channel.Id);
        SetBlocked(Recipient, Initiator); // the recipient blocked the initiator
        var before = (await _channelRepository.Load(channel.Id)).ToJson();

        var result = await BuildHub(RecipientConn).DeclineRequest(channel.Id);

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok), "the block never leaks into the consent surface — decline succeeds normally");
        Assert.That((await _channelRepository.Load(channel.Id)).ToJson(), Is.EqualTo(before), "decline still writes only the recipient's membership");
        Assert.That((await _membershipRepository.Load(channel.Id, Recipient)).DeclinedUntil, Is.Not.Null);
    }
}
