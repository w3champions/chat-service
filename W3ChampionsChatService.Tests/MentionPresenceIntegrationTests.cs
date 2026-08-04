using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
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
/// C6 Task 12 — the END-TO-END MENTIONS + DIRECTORY + PRESENCE acceptance suite. Drives MULTIPLE real
/// <see cref="ChatHub"/> instances (one per simulated connection) through the SHIPPED C6 pipeline while
/// SHARING one instance of every singleton — the registries, the engine, the repos on the shared
/// <see cref="IntegrationTestBase.MongoClient"/>, and crucially ONE <see cref="HubPushCaptureHarness"/>
/// (its <see cref="HubPushCaptureHarness.HubContext"/> is the push channel of both the
/// <see cref="MentionFanOut"/> and the <see cref="FanOutEngine"/>, so every cross-connection push —
/// <c>MentionNotified</c> / <c>PresenceChanged</c> / <c>FriendPresenceChanged</c> — lands in one
/// per-connection capture), ONE <see cref="PresenceInterestRegistry"/> (shared by every hub AND the
/// engine, so <c>FocusChannel</c>'s interest grant and the engine's interest read see one index), and ONE
/// real <see cref="RelationshipProvider"/> over a <see cref="FakeRelationshipSource"/> (per-tag friends
/// control). This is the <see cref="ModerationIntegrationTests"/> multi-instance + shared-singleton idiom,
/// extended with the C6 collaborators and the <see cref="ChatHubPresenceTests"/> /
/// <see cref="ChatHubFriendPresenceTests"/> presence wiring.
/// <para>
/// These are ACCEPTANCE tests over already-shipped code (Tasks 1-11), so they were GREEN on write. TIME is
/// DETERMINISTIC via a single <see cref="FakeTimeProvider"/>; the one real-clock seam is the one-time ticket
/// (<see cref="ChatHub.OnConnectedAsync"/> consumes it with <c>DateTime.UtcNow</c>, so tickets are minted
/// with <c>DateTime.UtcNow</c> too — exactly as <see cref="HubProtocolIntegrationTests"/>/
/// <see cref="ModerationIntegrationTests"/> handle it). Two capture surfaces, both keyed by connectionId:
/// the shared <see cref="HubPushCaptureHarness"/> records the cross-connection <c>IHubContext</c> fan-out
/// pushes; each hub's own capturing <see cref="IHubCallerClients"/> records the connect-path
/// <c>Clients.Caller</c> pushes (<c>SessionState</c>) into the shared <see cref="_hubSends"/>. The friend
/// push rides the connect/disconnect fire-and-forget prefetch, made SYNCHRONOUS by
/// <see cref="FakeRelationshipSource.ReleaseGate"/> = <see cref="Task.CompletedTask"/> (mirrors
/// <see cref="ChatHubFriendPresenceTests"/>), so it is observable immediately after Connect/disconnect
/// returns — no sleeps, no polling.
/// </para>
/// </summary>
public class MentionPresenceIntegrationTests : IntegrationTestBase
{
    private static readonly DateTime T0 = new(2026, 7, 4, 12, 0, 0, DateTimeKind.Utc);

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
    private PresenceInterestRegistry _presenceInterestRegistry; // the shared index under test
    private UserDirectoryRepository _userDirectory;
    private MuteRepository _muteRepository;
    private MuteReconciliationService _reconcileService;
    private ChannelRepository _channelRepository;
    private MembershipRepository _membershipRepository;
    private MessageRepository _messageRepository;
    private MentionInboxRepository _mentionInboxRepository;
    private MentionFanOut _mentionFanOut;       // the REAL C6 T5 writer, pushing through the shared harness
    private MentionInboxCleaner _mentionCleaner; // the REAL C6 T7 cleaner (physical mention_inbox delete)
    private SessionStateAssembler _assembler;
    private Mock<IChatAuthenticationService> _authService;

    private FakeRelationshipSource _relationshipSource;
    private RelationshipProvider _relationshipProvider;

    // Per-tag friends, read by the fake source's snapshot factory (OrdinalIgnoreCase) — only the friend-
    // presence test populates this; every other test's connects resolve an empty friends set (no push).
    private readonly Dictionary<string, HashSet<string>> _friends = new(StringComparer.OrdinalIgnoreCase);

    // Every Clients.Caller/Client push + every Context.Abort(), in order, across ALL connections. The
    // cross-connection fan-out pushes (MentionNotified/PresenceChanged/FriendPresenceChanged) go to
    // _harness instead.
    private readonly List<(string ConnectionId, string Method, object Payload)> _hubSends = new();

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    [SetUp]
    public void SetupBeforeEach()
    {
        _hubSends.Clear();
        _friends.Clear();
        _time = new FakeTimeProvider(new DateTimeOffset(T0, TimeSpan.Zero));
        _harness = new HubPushCaptureHarness();

        _ticketStore = new TicketStore();
        _sessionRegistry = new SessionRegistry();
        _connectionMapping = new ConnectionMapping();
        _focusRegistry = new FocusRegistry();
        _onlineMemberRegistry = new OnlineMemberRegistry();
        _messageRateLimiter = new MessageRateLimiter();
        _channelCreationRateLimiter = new ChannelCreationRateLimiter();
        _presenceInterestRegistry = new PresenceInterestRegistry();
        _userDirectory = new UserDirectoryRepository(MongoClient);
        _muteRepository = new MuteRepository(MongoClient);
        _reconcileService = new MuteReconciliationTestHarness(_connectionMapping, _muteRepository).Service;
        _channelRepository = new ChannelRepository(MongoClient);
        _membershipRepository = new MembershipRepository(MongoClient, _channelRepository);
        _messageRepository = new MessageRepository(MongoClient);
        _mentionInboxRepository = new MentionInboxRepository(MongoClient);
        // The REAL C6 T5 writer, wired to the SHARED harness + session registry + membership/inbox repos —
        // so a hub's own SendMessage(<@tag>) fans out a genuine mention-inbox entry AND a capturable,
        // targeted MentionNotified through the one shared capture (unlike the CreateIgnored factory the
        // moderation/DM suites use, whose push goes to a throwaway sink and whose session registry is empty).
        _mentionFanOut = new MentionFanOut(_harness.HubContext, _sessionRegistry, _membershipRepository, _mentionInboxRepository, _userDirectory);
        // The REAL C6 T7 cleaner, so a moderator DeleteMessage/PurgeMessagesFromUser physically removes the
        // referenced mention-inbox rows in this suite too (acceptance 3).
        _mentionCleaner = new MentionInboxCleaner(MongoClient);

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
            _mentionInboxRepository);

        // The engine + coalescer + accumulator ALL push through the ONE shared harness and read the SHARED
        // registries the hubs mutate, so every push lands in a single ordered capture. The engine shares the
        // SAME PresenceInterestRegistry the hubs mutate, so RegisterFocus (hub) and GetInterestedConnections
        // (engine's PushPresenceChanged) see one consistent index.
        _activityCoalescer = new ActivityCoalescer(_harness.HubContext, _onlineMemberRegistry);
        _viewersAccumulator = new ViewersAccumulator(_harness.HubContext, _focusRegistry);
        _fanOutEngine = new FanOutEngine(
            _harness.HubContext, _focusRegistry, _onlineMemberRegistry, _activityCoalescer, _sessionRegistry, _presenceInterestRegistry, _viewersAccumulator, _time);

        _relationshipSource = new FakeRelationshipSource((tag, now) => new RelationshipSnapshot(
            tag,
            _friends.TryGetValue(tag, out var f) ? f : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            now));
        // Makes every fetch resolve SYNCHRONOUSLY (no genuine suspension), so the fire-and-forget friend push
        // is deterministically observable right after Connect/disconnect return (mirrors ChatHubFriendPresenceTests).
        _relationshipSource.ReleaseGate = Task.CompletedTask;
        _relationshipProvider = new RelationshipProvider(_relationshipSource, _time);
    }

    // ============================================================================================
    // Fixture plumbing (union of ModerationIntegrationTests / ChatHubPresenceTests /
    // ChatHubFriendPresenceTests)
    // ============================================================================================

    private static W3CUserAuthentication Identity(string battleTag) =>
        new() { BattleTag = battleTag, Name = battleTag.Split('#')[0] };

    // A permissioned moderator identity — HasPermission (IsAdmin ∧ Permissions.Contains) is what the
    // moderation trio keys on.
    private static W3CUserAuthentication ModeratorIdentity(string battleTag) =>
        new()
        {
            BattleTag = battleTag,
            Name = battleTag.Split('#')[0],
            IsAdmin = true,
            Permissions = new HashSet<EPermission> { EPermission.Moderation },
        };

    private ChatHub BuildHub(string connectionId, string accessToken)
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
            _viewersAccumulator,
            _mentionCleaner,                    // REAL cleaner (physical mention_inbox delete)
            _relationshipProvider,              // REAL provider over the fake source
            new UserSettingsRepository(MongoClient),
            new DmInitiationTracker(),
            _authService.Object,
            _mentionFanOut,                     // REAL fan-out through the shared harness
            _presenceInterestRegistry,          // SHARED — same instance the engine reads
            _mentionInboxRepository);           // SHARED — the read/ack store

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

    private async Task<ChatHub> Connect(string connectionId, string battleTag) =>
        await ConnectWith(connectionId, Identity(battleTag));

    private async Task<ChatHub> ConnectModerator(string connectionId, string battleTag) =>
        await ConnectWith(connectionId, ModeratorIdentity(battleTag));

    private async Task<ChatHub> ConnectWith(string connectionId, W3CUserAuthentication identity)
    {
        var ticket = _ticketStore.Mint(identity, DateTime.UtcNow);
        var hub = BuildHub(connectionId, ticket);
        await hub.OnConnectedAsync();
        return hub;
    }

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

    // ---- Mongo seed helpers ------------------------------------------------------------------------

    private async Task<ChatChannel> CreateChannel(string name, ChannelType type = ChannelType.Public)
    {
        var channel = new ChatChannel
        {
            Type = type,
            Name = name,
            NormalizedName = type is ChannelType.Public or ChannelType.SemiPublic ? ChannelNames.Normalize(name) : null,
            LastSeq = 0,
        };
        if (type == ChannelType.Dm)
        {
            channel.RequestState = DmRequestState.Accepted;
        }
        await _channelRepository.Insert(channel);
        return channel;
    }

    private Task SeedMembership(
        string channelId,
        string battleTag,
        NotificationLevel level = NotificationLevel.All,
        MembershipRole role = MembershipRole.Member) =>
        _membershipRepository.Insert(new ChannelMembership
        {
            ChannelId = channelId,
            BattleTag = battleTag,
            NotificationLevel = level,
            Role = role,
            JoinedAt = Now,
        });

    // Directory row — the tier-3 search universe (SearchMentionCandidates). NOTE: the send-side mention
    // gate no longer consults the directory (the "strip & deliver as plain" amendment removed the
    // resolvability check — mention eligibility is decided SOLELY by durable membership in the fan-out), so
    // seeding here is only load-bearing for the search legs. LastSeenAt defaults to the fixed clock; the
    // search legs pass an explicit value to exercise the 90d gate.
    private Task SeedDirectory(string battleTag, DateTime? lastSeenAt = null, ChatProfile profile = null) =>
        _userDirectory.Upsert(new UserDirectoryEntry
        {
            BattleTag = battleTag,
            DisplayBattleTag = battleTag,
            NormalizedName = battleTag.ToLowerInvariant(),
            LastSeenAt = lastSeenAt ?? Now,
            Profile = profile,
        });

    private static string Mention(string tag) => $"<@{tag}>";

    // ---- capture readers ---------------------------------------------------------------------------

    private IReadOnlyList<MentionNotifiedDto> MentionNotifiedFor(string connectionId) =>
        _harness.SignalsFor(connectionId)
            .Where(s => s.Method == ChatEvents.MentionNotified)
            .Select(s => (MentionNotifiedDto)s.Payload)
            .ToList();

    private IReadOnlyList<PresenceChangedDto> PresenceFor(string connectionId) =>
        _harness.SignalsFor(connectionId)
            .Where(s => s.Method == ChatEvents.PresenceChanged)
            .Select(s => (PresenceChangedDto)s.Payload)
            .ToList();

    private int PresenceCount(string connectionId, string battleTag) =>
        PresenceFor(connectionId).Count(p => string.Equals(p.BattleTag, battleTag, StringComparison.OrdinalIgnoreCase));

    private int PresenceCount(string connectionId, string battleTag, bool online) =>
        PresenceFor(connectionId).Count(p =>
            string.Equals(p.BattleTag, battleTag, StringComparison.OrdinalIgnoreCase) && p.Online == online);

    private IReadOnlyList<FriendPresenceChangedDto> FriendPresenceFor(string connectionId) =>
        _harness.SignalsFor(connectionId)
            .Where(s => s.Method == ChatEvents.FriendPresenceChanged)
            .Select(s => (FriendPresenceChangedDto)s.Payload)
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

    // ============================================================================================
    // Slate 1 — MentionLifecycle_EndToEnd (acceptance 1 + 2).
    // A mentions B (unfocused) → entry + targeted MentionNotified created → B acks the newest → an OLDER
    // unread entry is NEVER auto-acked → MarkAllMentionsRead clears everything → read entries PERSIST.
    // ============================================================================================

    [Test]
    public async Task MentionLifecycle_EndToEnd()
    {
        const string ATag = "author#1";
        const string BTag = "bob#2";

        var channel = await CreateChannel("W3C Lounge", ChannelType.Public);
        await SeedMembership(channel.Id, ATag);
        await SeedMembership(channel.Id, BTag);
        await SeedDirectory(BTag); // search universe only; B's mention eligibility comes from durable membership

        var aHub = await Connect("conn-a", ATag);
        var bHub = await Connect("conn-b", BTag); // B is UNFOCUSED (never calls FocusChannel)

        // --- Send #1: A mentions B. A durable inbox entry AND a targeted MentionNotified to B's connection.
        var send1 = await aHub.SendMessage(channel.Id, $"hey <@{BTag}> look at this");
        Assert.That(send1.Code, Is.EqualTo(ChatResultCode.Ok));

        Assert.That(MentionNotifiedFor("conn-b"), Has.Count.EqualTo(1),
            "an unfocused mention target receives exactly one targeted MentionNotified");
        Assert.That(MentionNotifiedFor("conn-b").Single().MessageId, Is.EqualTo(send1.MessageId));
        Assert.That(MentionNotifiedFor("conn-a"), Is.Empty, "the sender is never self-notified");

        var inboxAfter1 = (await bHub.GetMentionInbox()).Entries;
        Assert.That(inboxAfter1, Has.Count.EqualTo(1), "the durable entry exists for B");
        var entry1 = inboxAfter1.Single();
        Assert.That(entry1.MessageId, Is.EqualTo(send1.MessageId));
        Assert.That(entry1.ReadAt, Is.Null, "a freshly-created entry is unread");
        Assert.That(await _mentionInboxRepository.CountUnread(BTag), Is.EqualTo(1),
            "the unread count (the number backing SessionState.MentionUnreadCount) is 1");

        // --- B acks entry #1 via MarkMentionsRead → the unread count drops.
        Assert.That((await bHub.MarkMentionsRead(new[] { entry1.Id })).Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(await _mentionInboxRepository.CountUnread(BTag), Is.EqualTo(0), "acking the sole entry drops unread to 0");
        Assert.That((await bHub.GetMentionInbox()).Entries.Single().ReadAt, Is.Not.Null, "entry #1 is now read");

        // --- A mentions B TWICE more, at distinct instants so newest/older is well-defined.
        _time.Advance(TimeSpan.FromMinutes(1));
        var send2 = await aHub.SendMessage(channel.Id, $"still here <@{BTag}>");
        Assert.That(send2.Code, Is.EqualTo(ChatResultCode.Ok));

        _time.Advance(TimeSpan.FromMinutes(1));
        var send3 = await aHub.SendMessage(channel.Id, $"one more <@{BTag}>");
        Assert.That(send3.Code, Is.EqualTo(ChatResultCode.Ok));

        Assert.That(MentionNotifiedFor("conn-b"), Has.Count.EqualTo(3), "each of the three sends pushed exactly one targeted event");
        Assert.That(await _mentionInboxRepository.CountUnread(BTag), Is.EqualTo(2), "the two new mentions are unread");

        var inboxAfter3 = (await bHub.GetMentionInbox()).Entries;
        Assert.That(inboxAfter3.Select(e => e.MessageId), Is.EqualTo(new[] { send3.MessageId, send2.MessageId, send1.MessageId }),
            "the inbox is newest-first");
        var entry2 = inboxAfter3.Single(e => e.MessageId == send2.MessageId); // OLDER of the two new ones
        var entry3 = inboxAfter3.Single(e => e.MessageId == send3.MessageId); // NEWEST

        // --- The no-seq-auto-ack pin (acceptance 2): acking the NEWEST leaves the OLDER unread.
        Assert.That((await bHub.MarkMentionsRead(new[] { entry3.Id })).Code, Is.EqualTo(ChatResultCode.Ok));
        var afterNewestAck = (await bHub.GetMentionInbox()).Entries;
        Assert.That(afterNewestAck.Single(e => e.Id == entry3.Id).ReadAt, Is.Not.Null, "the explicitly-acked newest entry is read");
        Assert.That(afterNewestAck.Single(e => e.Id == entry2.Id).ReadAt, Is.Null,
            "acking a NEWER mention must NEVER auto-ack an OLDER, still-unseen one (no seq-derived ack)");
        Assert.That(await _mentionInboxRepository.CountUnread(BTag), Is.EqualTo(1), "only the older entry remains unread");

        // --- MarkAllMentionsRead clears everything; entries PERSIST (with ReadAt), never deleted.
        Assert.That((await bHub.MarkAllMentionsRead()).Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(await _mentionInboxRepository.CountUnread(BTag), Is.EqualTo(0), "mark-all clears the last unread");

        var finalInbox = (await bHub.GetMentionInbox()).Entries;
        Assert.That(finalInbox, Has.Count.EqualTo(3), "read entries are KEPT (dimmed client-side) — nothing is deleted");
        Assert.That(finalInbox.Select(e => e.ReadAt), Has.All.Not.Null, "every entry now carries a ReadAt");
        Assert.That(await _mentionInboxRepository.LoadForUser(BTag), Has.Count.EqualTo(3),
            "the durable rows all survive until the 30d TTL — read is a field flip, never a delete");
    }

    // ============================================================================================
    // Slate 2 — ModerationDelete_ScrubsInbox_EndToEnd (acceptance 3).
    // A real mention → moderator DeleteMessage physically removes the referenced inbox entry; and a
    // multi-channel PurgeMessagesFromUser scrubs every referencing entry across the purged channels.
    // ============================================================================================

    [Test]
    public async Task ModerationDelete_ScrubsInbox_EndToEnd()
    {
        // ---- Part 1: single-message delete scrubs its one referencing entry. ----
        const string AuthorTag = "delauthor#1";
        const string MentionedTag = "delmentioned#2";
        const string ModTag = "delmod#3";

        var channel = await CreateChannel("W3C Lounge", ChannelType.Public);
        await SeedMembership(channel.Id, AuthorTag);
        await SeedMembership(channel.Id, MentionedTag);
        await SeedDirectory(MentionedTag);

        var authorHub = await Connect("conn-delauthor", AuthorTag);
        await Connect("conn-delmentioned", MentionedTag);
        var modHub = await ConnectModerator("conn-delmod", ModTag);

        var send = await authorHub.SendMessage(channel.Id, $"hey <@{MentionedTag}>");
        Assert.That(send.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(await _mentionInboxRepository.LoadForUser(MentionedTag), Has.Count.EqualTo(1),
            "sanity: a real mention creates a real inbox entry");

        Assert.That((await modHub.DeleteMessage(send.MessageId)).Code, Is.EqualTo(ChatResultCode.Ok));

        Assert.That((await _messageRepository.Load(send.MessageId)).Deleted, Is.Not.Null,
            "the message is soft-deleted (moderation never hard-deletes a message)");
        Assert.That(await _mentionInboxRepository.LoadForUser(MentionedTag), Is.Empty,
            "the real cleaner physically removes the mention-inbox entry the deleted message referenced");

        // ---- Part 2: a cross-channel purge scrubs EVERY referencing entry the purge deleted. ----
        const string SpammerTag = "spammer#10";
        const string TargetTag = "purgetarget#11";
        const string PurgeModTag = "purgemod#12";

        var pub = await CreateChannel("clan-hall", ChannelType.Public);
        var semi = await CreateChannel("clan-den", ChannelType.SemiPublic);
        await SeedMembership(pub.Id, SpammerTag);
        await SeedMembership(pub.Id, TargetTag);
        await SeedMembership(semi.Id, SpammerTag);
        await SeedMembership(semi.Id, TargetTag);
        await SeedDirectory(TargetTag);

        var spammerHub = await Connect("conn-spammer", SpammerTag);
        await Connect("conn-purgetarget", TargetTag);
        var purgeModHub = await ConnectModerator("conn-purgemod", PurgeModTag);

        var pubSend = await spammerHub.SendMessage(pub.Id, $"spam <@{TargetTag}>");
        var semiSend = await spammerHub.SendMessage(semi.Id, $"more spam <@{TargetTag}>");
        Assert.That(pubSend.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(semiSend.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(await _mentionInboxRepository.LoadForUser(TargetTag), Has.Count.EqualTo(2),
            "sanity: the spammer's two mentions created one entry per channel");

        var purge = await purgeModHub.PurgeMessagesFromUser(SpammerTag);
        Assert.That(purge.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(purge.MessagesDeleted, Is.EqualTo(2), "both eligible-channel spam rows are soft-deleted");

        Assert.That(await _mentionInboxRepository.LoadForUser(TargetTag), Is.Empty,
            "the purge's cleaner physically removes EVERY mention-inbox entry that referenced a purged message");
    }

    // ============================================================================================
    // Slate 3 — MentionValidation_EndToEnd (acceptance 4).
    // >5 distinct rejected (the COUNT cap); an unresolvable mention is NOT rejected — it delivers verbatim
    // and simply notifies nobody (strip & deliver as plain); exactly 5 valid mentions fan out to EXACTLY
    // those 5 members (a co-present non-mentioned member and the sender get nothing); a mentioned resolvable
    // NON-member of this PUBLIC channel DOES get notified (follow-up spec §4 — Public rooms are
    // membership-independent for a directory-resolvable target).
    // ============================================================================================

    [Test]
    public async Task MentionValidation_EndToEnd()
    {
        const string ATag = "valauthor#1";
        var members = Enumerable.Range(1, ChatLimits.MaxMentionsPerMessage).Select(i => $"member{i}#{i}").ToArray();
        const string BystanderTag = "bystander#7"; // a co-present, NON-mentioned member
        const string NonMemberTag = "stranger#8";  // resolvable + online but NOT a channel member

        var channel = await CreateChannel("W3C Lounge", ChannelType.Public);
        await SeedMembership(channel.Id, ATag);
        await SeedMembership(channel.Id, BystanderTag);
        foreach (var m in members)
        {
            await SeedMembership(channel.Id, m);
            await SeedDirectory(m);
        }
        await SeedDirectory(NonMemberTag); // resolvable in the directory, but never a member of this channel

        var aHub = await Connect("conn-valauthor", ATag);
        await Connect("conn-bystander", BystanderTag);
        var memberConns = new Dictionary<string, string>();
        for (var i = 0; i < members.Length; i++)
        {
            var conn = $"conn-member-{i}";
            memberConns[members[i]] = conn;
            await Connect(conn, members[i]);
        }
        await Connect("conn-stranger", NonMemberTag);

        // --- Reject leg (the ONE retained reject): SIX distinct mentions → TooLong (the COUNT cap), nothing persists.
        var sixDistinct = string.Join(" ", Enumerable.Range(1, ChatLimits.MaxMentionsPerMessage + 1).Select(i => Mention($"ghost{i}#{i}")));
        var overCap = await aHub.SendMessage(channel.Id, sixDistinct);
        Assert.That(overCap.Code, Is.EqualTo(ChatResultCode.TooLong), "more than 5 distinct mentions is rejected");
        Assert.That((await _channelRepository.Load(channel.Id)).LastSeq, Is.EqualTo(0L),
            "the over-cap send allocated no seq and persisted nothing");

        // --- Strip & deliver as plain: an UNRESOLVABLE mention is NOT rejected — it delivers VERBATIM and
        // simply notifies nobody (the fan-out membership wall drops the non-member target). NOT TooLong.
        var unresolvableContent = "who is <@nobody#404>";
        var unresolvable = await aHub.SendMessage(channel.Id, unresolvableContent);
        Assert.That(unresolvable.Code, Is.EqualTo(ChatResultCode.Ok),
            "an unresolvable battleTag is legal content — the send is never rejected for resolvability");
        Assert.That((await _messageRepository.Load(unresolvable.MessageId)).Content, Is.EqualTo(unresolvableContent),
            "the message delivers verbatim — the invalid <@…> token is kept as plain text");
        Assert.That(await _mentionInboxRepository.LoadForUser("nobody#404"), Is.Empty,
            "the unresolvable target gets no inbox entry (nobody is a member)");
        Assert.That((await _channelRepository.Load(channel.Id)).LastSeq, Is.EqualTo(1L),
            "the unresolvable send DID persist (seq 1) — it is a normal, deliverable message");

        // --- Fan-out leg: exactly 5 valid mentions fan out to EXACTLY those 5 members.
        _time.Advance(TimeSpan.FromSeconds(2));
        var fiveValid = string.Join(" ", members.Select(Mention));
        var validSend = await aHub.SendMessage(channel.Id, fiveValid);
        Assert.That(validSend.Code, Is.EqualTo(ChatResultCode.Ok), "exactly-at-cap resolvable mentions of members are accepted");

        foreach (var m in members)
        {
            Assert.That(MentionNotifiedFor(memberConns[m]), Has.Count.EqualTo(1),
                $"the mentioned member {m} receives exactly one targeted MentionNotified");
            Assert.That(await _mentionInboxRepository.LoadForUser(m), Has.Count.EqualTo(1),
                $"the mentioned member {m} gets exactly one durable inbox entry");
        }
        Assert.That(MentionNotifiedFor("conn-bystander"), Is.Empty,
            "a co-present but NON-mentioned member receives nothing — fan-out is targeted, never a channel broadcast");
        Assert.That(await _mentionInboxRepository.LoadForUser(BystanderTag), Is.Empty, "...and gets no inbox entry");
        Assert.That(MentionNotifiedFor("conn-valauthor"), Is.Empty, "the sender is never self-notified");

        // --- Non-member leg (follow-up spec §4): a mentioned resolvable NON-member of this PUBLIC channel
        // NOW gets notified — the membership wall is widened away for Public rooms specifically, since a
        // directory-resolvable target's tag proves resolvability without needing a membership row.
        _time.Advance(TimeSpan.FromSeconds(2));
        var mixed = await aHub.SendMessage(channel.Id, $"ping <@{members[0]}> and <@{NonMemberTag}>");
        Assert.That(mixed.Code, Is.EqualTo(ChatResultCode.Ok), "mentioning a resolvable non-member is legal content");

        Assert.That(MentionNotifiedFor("conn-stranger"), Has.Count.EqualTo(1),
            "a mentioned NON-member of a PUBLIC channel — resolvable AND online — DOES receive a notification (§4)");
        Assert.That(await _mentionInboxRepository.LoadForUser(NonMemberTag), Has.Count.EqualTo(1),
            "...and gets an inbox entry too");
        Assert.That(MentionNotifiedFor(memberConns[members[0]]), Has.Count.EqualTo(2),
            "the co-mentioned actual member DID get a second event from that same send");
    }

    // ============================================================================================
    // Slate 4 — Search_TiersAndGate_EndToEnd (acceptance 5).
    // A fixture with a viewer (tier 1), an online-elsewhere user (tier 2), an offline-within-90d user
    // (tier 3), and an offline-beyond-90d user (excluded): assert tier ordering, the 90d exclusion, and
    // enrichment presence (a cached profile) / absence (a directory row with no profile).
    // ============================================================================================

    [Test]
    public async Task Search_TiersAndGate_EndToEnd()
    {
        const string CallerTag = "caller#1";      // deliberately does NOT match the "vic" prefix
        const string ViewerTag = "vic-view#100";  // tier 1 — an active viewer of the searched channel
        const string OnlineTag = "vic-online#200"; // tier 2 — online, not viewing the searched channel
        const string FreshTag = "vic-fresh#300";  // tier 3 — offline, active 89d ago (within the gate), enriched
        const string PlainTag = "vic-plain#400";  // tier 3 — offline, active 89d ago, NO cached profile
        const string BoundaryTag = "vic-edge#600"; // tier 3 — offline, active EXACTLY 90d ago (the inclusive '>=' edge)
        const string StaleTag = "vic-old#500";    // excluded — offline, active 91d ago (beyond the gate)
        const string NonMatchTag = "zeta-online#900"; // online, but a NON-matching prefix — must be prefix-excluded

        var channel = await CreateChannel("search-room", ChannelType.Public);
        await SeedMembership(channel.Id, CallerTag);
        await SeedMembership(channel.Id, ViewerTag);

        var caller = await Connect("conn-caller", CallerTag);   // member (via connect seed) → passes the search auth
        var viewerHub = await Connect("conn-viewer", ViewerTag);
        Assert.That((await viewerHub.FocusChannel(channel.Id)).Code, Is.EqualTo(ChatResultCode.Ok)); // tier 1
        await Connect("conn-online", OnlineTag); // online anywhere, never focuses this channel → tier 2
        await Connect("conn-nonmatch", NonMatchTag); // online (a tier-2 candidate) but its tag does NOT start with "vic"

        // Tier 3 / gate fixture — directly-seeded directory rows (never connected), so LastSeenAt + Profile
        // are fully controlled (immune to the connect-time upsert).
        var freshProfile = new ChatProfile { ClanId = "ClanZ", LeagueName = "Grandmaster", RankNumber = 1, GamesPlayed = 42 };
        await SeedDirectory(FreshTag, Now.AddDays(-89), freshProfile);
        await SeedDirectory(PlainTag, Now.AddDays(-89), profile: null);
        // The EXACT 90d edge: the tier-3 gate is Gte(LastSeenAt, now - MentionCandidateActivityWindow)
        // (UserDirectoryRepository.SearchByNormalizedPrefix). The clock never advances in this test, so
        // this row's LastSeenAt (Now.AddDays(-90)) is bit-identical to the query's minLastSeenAt.
        await SeedDirectory(BoundaryTag, Now.AddDays(-90), profile: null);
        await SeedDirectory(StaleTag, Now.AddDays(-91), profile: null);

        var result = await caller.SearchMentionCandidates(channel.Id, "vic");
        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));

        var byTag = result.Candidates.ToDictionary(c => c.BattleTag, StringComparer.OrdinalIgnoreCase);

        // Tier assignment.
        Assert.That(byTag[ViewerTag].Tier, Is.EqualTo(1), "an active viewer of the channel is tier 1");
        Assert.That(byTag[OnlineTag].Tier, Is.EqualTo(2), "an online-elsewhere user is tier 2");
        Assert.That(byTag[FreshTag].Tier, Is.EqualTo(3), "an offline-but-recent directory match is tier 3");

        // Tier ORDERING (viewer > online > directory).
        var order = result.Candidates.Select(c => c.BattleTag).ToList();
        Assert.That(order.IndexOf(ViewerTag), Is.LessThan(order.IndexOf(OnlineTag)), "tier 1 precedes tier 2");
        Assert.That(order.IndexOf(OnlineTag), Is.LessThan(order.IndexOf(FreshTag)), "tier 2 precedes tier 3");

        // The 90d gate (tier 3 ONLY): the 91d-stale user is excluded entirely.
        Assert.That(result.Candidates.Select(c => c.BattleTag), Does.Not.Contain(StaleTag),
            "an offline user last active beyond the 90d window is excluded from tier 3");

        // The EXACT boundary: last active at PRECISELY now-90d falls on the INCLUSIVE side of the '>='
        // gate (Gte in SearchByNormalizedPrefix) — kept, not excluded. Pins which side of the edge the
        // 89d-included / 91d-excluded pair straddles.
        Assert.That(result.Candidates.Select(c => c.BattleTag), Does.Contain(BoundaryTag),
            "a user last active at EXACTLY now-90d is on the inclusive side of the '>=' tier-3 gate — included, not excluded");
        Assert.That(byTag[BoundaryTag].Tier, Is.EqualTo(3), "the exact-boundary user is a tier-3 directory match");

        // The prefix filter genuinely EXCLUDES a non-matching online user (a tier-2 candidate) — the
        // result is not merely "everyone" because every OTHER fixture tag happens to start with "vic".
        Assert.That(result.Candidates.Select(c => c.BattleTag), Does.Not.Contain(NonMatchTag),
            "an online user whose tag does NOT match the 'vic' prefix is prefix-excluded, proving the tier filter actually filters");

        // Enrichment PRESENCE (a cached profile flows through) vs ABSENCE (a bare directory row degrades to null).
        Assert.That(byTag[FreshTag].Profile, Is.Not.Null, "a directory row with a cached profile enriches the candidate");
        Assert.That(byTag[FreshTag].Profile.ClanId, Is.EqualTo("ClanZ"));
        Assert.That(byTag[FreshTag].Profile.LeagueName, Is.EqualTo("Grandmaster"));
        Assert.That(byTag[FreshTag].Profile.GamesPlayed, Is.EqualTo(42));
        Assert.That(byTag[PlainTag].Tier, Is.EqualTo(3));
        Assert.That(byTag[PlainTag].Profile, Is.Null,
            "a directory row with no cached profile yet degrades to a null Profile — never an error, never an exclusion");
    }

    // ============================================================================================
    // Slate 5 — Presence_InterestAndRevocation_EndToEnd (acceptance 6, the derived-interest leg).
    // Focus a DM (derive interest) → subject connect/disconnect reaches the watcher → unfocus revokes →
    // a group re-focus then a forced membership removal revokes again; a genuinely-wired bystander focused
    // elsewhere captures ZERO presence events throughout.
    // ============================================================================================

    [Test]
    public async Task Presence_InterestAndRevocation_EndToEnd()
    {
        const string AliceTag = "Alice#1";     // the watcher
        const string XavierTag = "Xavier#9";   // the subject
        const string BystanderTag = "Cara#3";  // genuinely wired, focused on an UNRELATED DM
        const string YoungTag = "Young#5";

        var dmAx = await CreateChannel("dm-ax", ChannelType.Dm);
        await SeedMembership(dmAx.Id, AliceTag);
        await SeedMembership(dmAx.Id, XavierTag);

        var grpAx = await CreateChannel("grp-ax", ChannelType.GroupDm);
        await SeedMembership(grpAx.Id, AliceTag, role: MembershipRole.Owner);
        await SeedMembership(grpAx.Id, XavierTag);

        var dmCy = await CreateChannel("dm-cy", ChannelType.Dm);
        await SeedMembership(dmCy.Id, BystanderTag);
        await SeedMembership(dmCy.Id, YoungTag);

        var alice = await Connect("conn-alice", AliceTag);
        await alice.FocusChannel(dmAx.Id); // Alice watches Xavier via the DM

        var bystander = await Connect("conn-cara", BystanderTag);
        await bystander.FocusChannel(dmCy.Id); // a genuinely-wired watcher — of Young, NOT Xavier
        Assert.That(_presenceInterestRegistry.GetInterestedConnections(YoungTag), Does.Contain("conn-cara"),
            "sanity: the bystander is a GENUINE live watcher (of Young) — its later 'zero Xavier events' is a real boundary, not a dead wire");

        // --- Positive interest: Xavier's genuine online→offline transitions reach ONLY Alice.
        var xavier1 = await Connect("conn-xavier-1", XavierTag);
        Assert.That(PresenceCount("conn-alice", XavierTag, online: true), Is.EqualTo(1), "the watcher is told Xavier is online");
        await xavier1.OnDisconnectedAsync(null);
        Assert.That(PresenceCount("conn-alice", XavierTag, online: false), Is.EqualTo(1), "...and offline");

        // --- Revocation by unfocus: Alice stops watching the DM; Xavier's next transitions are silent to her.
        Assert.That((await alice.UnfocusChannel(dmAx.Id)).Code, Is.EqualTo(ChatResultCode.Ok));
        var xavier2 = await Connect("conn-xavier-2", XavierTag);
        await xavier2.OnDisconnectedAsync(null);
        Assert.That(PresenceCount("conn-alice", XavierTag), Is.EqualTo(2),
            "after unfocus, no further presence reaches the ex-watcher — she still only saw the two pre-unfocus transitions");

        // --- Revocation by forced membership removal: re-derive interest via the group, then kick Xavier
        // (offline) — the membership-change hook fires before the offline early-return, revoking interest.
        Assert.That((await alice.FocusChannel(grpAx.Id)).Code, Is.EqualTo(ChatResultCode.Ok)); // Alice watches Xavier again (offline → no event)
        Assert.That(_presenceInterestRegistry.GetInterestedConnections(XavierTag), Does.Contain("conn-alice"),
            "sanity: focusing the GROUP genuinely (re-)derived interest in Xavier — so the removal below has a REAL grant to revoke, not a dead wire");
        Assert.That((await alice.RemoveGroupMember(grpAx.Id, XavierTag)).Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(_presenceInterestRegistry.GetInterestedConnections(XavierTag), Does.Not.Contain("conn-alice"),
            "the forced removal actually dropped the grant — the interest is gone at the registry, not merely unobserved");
        await Connect("conn-xavier-3", XavierTag);
        Assert.That(PresenceCount("conn-alice", XavierTag), Is.EqualTo(2),
            "a forced removal revokes the watcher's interest even while the member is offline — Xavier's later connect is silent");

        // --- The strict boundary: the genuinely-wired bystander captured ZERO presence events throughout.
        Assert.That(PresenceFor("conn-cara"), Is.Empty,
            "a genuinely-wired connection focused elsewhere receives ZERO PresenceChanged about Xavier — the who-sees-whom boundary");
    }

    // ============================================================================================
    // Slate 6 — FriendPresence_ConnectAndDisconnect_ExactTargets (acceptance 6, the friends leg).
    // Connect and disconnect a subject with an online friend AND an online non-friend present; only the
    // friend receives events, on BOTH directions.
    // ============================================================================================

    [Test]
    public async Task FriendPresence_ConnectAndDisconnect_ExactTargets()
    {
        const string SubjectTag = "Subject#1";
        const string FriendTag = "Friend#2";
        const string NonFriendTag = "Nonfriend#3";
        _friends[SubjectTag] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { FriendTag };

        await Connect("conn-friend", FriendTag);
        await Connect("conn-nonfriend", NonFriendTag); // genuinely online, but NOT in the subject's friends list

        // --- Connect: online push reaches EXACTLY the online friend.
        var subject = await Connect("conn-subject", SubjectTag);
        Assert.That(FriendPresenceFor("conn-friend").Count(p => p.BattleTag == SubjectTag && p.Online), Is.EqualTo(1),
            "the online friend receives exactly one FriendPresenceChanged(online)");
        Assert.That(FriendPresenceFor("conn-friend").Single().BattleTag, Is.EqualTo(SubjectTag),
            "the payload battleTag is the SUBJECT's display casing");
        Assert.That(FriendPresenceFor("conn-nonfriend"), Is.Empty,
            "an online NON-friend receives NOTHING — the strict friends-only boundary");

        // --- Disconnect: offline push reaches EXACTLY the online friend, in the other direction.
        await subject.OnDisconnectedAsync(null);
        Assert.That(FriendPresenceFor("conn-friend").Count(p => p.BattleTag == SubjectTag && !p.Online), Is.EqualTo(1),
            "the online friend receives exactly one FriendPresenceChanged(offline)");
        Assert.That(FriendPresenceFor("conn-nonfriend"), Is.Empty, "the non-friend still receives nothing on disconnect");
    }

    // ============================================================================================
    // Slate 6b — GetPresenceDetails_LastSeenAt_FriendGated_EndToEnd (acceptance 6, the friend-gated
    // READ leg — DISTINCT from Slate 6's friends-only PUSH). LastSeenAt is the single most
    // privacy-sensitive datum in the presence subsystem: online/offline is UNGATED, but LastSeenAt is
    // populated ONLY for the CALLER's OWN friends (ChatHub.Presence.cs BuildPresenceDetails, sourced
    // from Task 3's disconnect upsert), fails closed to null on a RelationshipUnavailableException, and
    // even then Online is still honestly reported. All three legs (friend / non-friend / outage) over
    // the SAME real RelationshipProvider + real connect/disconnect directory upserts the fixture wires.
    // ============================================================================================

    [Test]
    public async Task GetPresenceDetails_LastSeenAt_FriendGated_EndToEnd()
    {
        const string SubjectTag = "readsubject#1";     // connects then disconnects → a REAL directory LastSeenAt from the disconnect upsert
        const string FriendTag = "readfriend#2";        // the caller who IS friends with the subject (and stays online as a probe target)
        const string StrangerTag = "readstranger#3";    // the caller who is NOT friends with the subject
        const string OutageFriendTag = "readoutage#4";  // an ACTUAL friend of the subject, but connects DURING a relationship outage

        // --- The subject connects, then disconnects. The disconnect upsert (Task 3 SetLastSeen) is the
        // ONLY source of the LastSeenAt the friend leg reads back — advance the clock first so it is
        // unambiguously the disconnect instant, never the earlier connect instant.
        var subject = await Connect("conn-readsubject", SubjectTag);
        _time.Advance(TimeSpan.FromMinutes(5));
        var disconnectInstant = Now;
        await subject.OnDisconnectedAsync(null);

        // --- Leg (a): a caller's OWN friend gets a non-null LastSeenAt (sourced from the disconnect
        // upsert), with Online honestly offline.
        _friends[FriendTag] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SubjectTag };
        var friendHub = await Connect("conn-readfriend", FriendTag);
        var friendResult = await friendHub.GetPresenceDetails(new[] { SubjectTag });
        Assert.That(friendResult.Code, Is.EqualTo(ChatResultCode.Ok));
        var friendView = friendResult.Details.Single();
        Assert.That(friendView.Online, Is.False, "the subject disconnected — Online is honestly offline");
        Assert.That(friendView.LastSeenAt, Is.EqualTo(disconnectInstant),
            "a caller's OWN friend gets a non-null LastSeenAt sourced from the subject's DISCONNECT upsert, not the earlier connect");

        // --- Leg (b): a NON-friend gets a null LastSeenAt, with Online STILL honest. Querying the online
        // FriendTag alongside the offline subject proves Online is computed independently of friendship —
        // it is not merely a hardcoded false suppressed together with the timestamp.
        var strangerHub = await Connect("conn-readstranger", StrangerTag); // _friends[StrangerTag] left unset → no friends
        var strangerResult = await strangerHub.GetPresenceDetails(new[] { SubjectTag, FriendTag });
        Assert.That(strangerResult.Code, Is.EqualTo(ChatResultCode.Ok));
        var strangerByTag = strangerResult.Details.ToDictionary(d => d.BattleTag, StringComparer.OrdinalIgnoreCase);
        Assert.That(strangerByTag[SubjectTag].Online, Is.False, "Online honestly reported for a non-friend (offline subject)");
        Assert.That(strangerByTag[SubjectTag].LastSeenAt, Is.Null,
            "a non-friend's LastSeenAt comes back null even though a REAL disconnect-upsert value exists in the directory");
        Assert.That(strangerByTag[FriendTag].Online, Is.True, "Online honestly reported for a non-friend (online target)");
        Assert.That(strangerByTag[FriendTag].LastSeenAt, Is.Null,
            "LastSeenAt stays suppressed for a non-friend regardless of the target's online status");

        // --- Leg (c): fail closed. The relationship source now faults EVERY fetch, so the outage caller —
        // an ACTUAL friend of the subject — can neither obtain nor have prefetched a snapshot (ShouldThrow
        // is set BEFORE its connect, so the connect-time fire-and-forget prefetch faults too and nothing
        // is cached). LastSeenAt fails closed to null on the sensitive datum ONLY; Online stays honest.
        _friends[OutageFriendTag] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SubjectTag };
        _relationshipSource.ShouldThrow = true;
        var outageHub = await Connect("conn-readoutage", OutageFriendTag);
        var outageResult = await outageHub.GetPresenceDetails(new[] { SubjectTag, FriendTag });
        Assert.That(outageResult.Code, Is.EqualTo(ChatResultCode.Ok));
        var outageByTag = outageResult.Details.ToDictionary(d => d.BattleTag, StringComparer.OrdinalIgnoreCase);
        Assert.That(outageByTag[SubjectTag].LastSeenAt, Is.Null,
            "a RelationshipUnavailableException fails LastSeenAt closed to null even for an ACTUAL friend with real directory data");
        Assert.That(outageByTag[FriendTag].LastSeenAt, Is.Null,
            "fails closed on the timestamp regardless of the target's online status");
        Assert.That(outageByTag[SubjectTag].Online, Is.False, "Online stays honest (offline) even when the relationship snapshot is unavailable");
        Assert.That(outageByTag[FriendTag].Online, Is.True, "Online stays honest (online) even when the relationship snapshot is unavailable");
    }

    // ============================================================================================
    // Slate 7 — Reconnect_SessionState_MentionUnreadCount_Rebuilds.
    // A mention lands while the recipient is OFFLINE; on their next connect, SessionState.MentionUnreadCount
    // reflects it.
    // ============================================================================================

    [Test]
    public async Task Reconnect_SessionState_MentionUnreadCount_Rebuilds()
    {
        const string AuthorTag = "reconauthor#1";
        const string RecipientTag = "reconrecipient#2";

        var channel = await CreateChannel("W3C Lounge", ChannelType.Public);
        await SeedMembership(channel.Id, AuthorTag);
        await SeedMembership(channel.Id, RecipientTag);
        await SeedDirectory(RecipientTag); // resolvable even though the recipient is offline

        // The recipient is OFFLINE. The author mentions them twice — durable entries, but no live push.
        var authorHub = await Connect("conn-reconauthor", AuthorTag);
        Assert.That((await authorHub.SendMessage(channel.Id, $"ping <@{RecipientTag}>")).Code, Is.EqualTo(ChatResultCode.Ok));
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.That((await authorHub.SendMessage(channel.Id, $"again <@{RecipientTag}>")).Code, Is.EqualTo(ChatResultCode.Ok));

        Assert.That(await _mentionInboxRepository.CountUnread(RecipientTag), Is.EqualTo(2),
            "sanity: two durable entries landed while the recipient was offline");

        // The author's OWN SessionState (they were never mentioned) is a built-in control: 0.
        Assert.That(SessionStateFor("conn-reconauthor").MentionUnreadCount, Is.EqualTo(0),
            "control: the author, mentioned by no one, reconnected earlier with a 0 unread-mention count");

        // The recipient connects for the FIRST time — the connect-path SessionState must carry the count.
        await Connect("conn-reconrecipient", RecipientTag);
        var session = SessionStateFor("conn-reconrecipient");
        Assert.That(session, Is.Not.Null, "the connect path pushed a SessionState to the recipient");
        Assert.That(session.MentionUnreadCount, Is.EqualTo(2),
            "SessionState.MentionUnreadCount rebuilds from the durable inbox on the recipient's next connect");
    }

    // ============================================================================================
    // Slate 8 — Guardrail pins (grep / reflection / argument capture).
    // ============================================================================================

    // 8(a): the mention-inbox store exposes NO delete API at all — so no production code path can call one
    // (mirrors OldProtocolRemovedTests.ModerationNeverHardDeletes for MessageRepository).
    [Test]
    public void Guardrail_MentionInboxRepository_ExposesNoDeleteApi()
    {
        var suspicious = typeof(MentionInboxRepository)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Where(m => ContainsDeleteVerb(m.Name))
            .Select(m => m.Name)
            .ToList();

        Assert.That(suspicious, Is.Empty,
            "MentionInboxRepository must expose NO delete/remove API — the ONLY physical mention_inbox delete " +
            $"path is the Task-7 MentionInboxCleaner. Found suspicious method(s): [{string.Join(", ", suspicious)}].");
    }

    // 8(a) continued: source-scan — the ONLY production file that hard-deletes mention_inbox rows is the
    // Task-7 cleaner. Any new file that issues a DeleteMany/DeleteOne against the MentionInboxEntry
    // collection fails this loudly (e.g. a stray delete slipped into the repository or a hub partial).
    [Test]
    public void Guardrail_OnlyTheCleaner_HardDeletesMentionInbox()
    {
        var productionDir = ProductionSourceDir();
        Assert.That(Directory.Exists(productionDir), Is.True,
            $"expected the production project source at '{productionDir}' — the source-scan guardrail cannot run without it");

        var deleters = Directory
            .EnumerateFiles(productionDir, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(path =>
            {
                var text = File.ReadAllText(path);
                var touchesInbox = text.Contains("MentionInboxEntry");
                var hardDeletes = text.Contains("DeleteManyAsync") || text.Contains("DeleteOneAsync");
                return touchesInbox && hardDeletes;
            })
            .Select(Path.GetFileName)
            .OrderBy(name => name)
            .ToList();

        Assert.That(deleters, Is.EqualTo(new[] { "MentionInboxCleaner.cs" }),
            "exactly ONE production file may physically delete mention_inbox rows — the Task-7 MentionInboxCleaner. " +
            $"Found: [{string.Join(", ", deleters)}].");
    }

    // 8(b): MentionNotified is ONLY ever pushed via a targeted single-connection send (Clients.Client(...)),
    // never a broadcast. Argument capture on a STRICT IHubClients mock (any broadcast surface — All/Group/
    // AllExcept/Others/User — is unconfigured and would throw) proves the delivery path is Client(connId).
    [Test]
    public async Task Guardrail_MentionNotified_IsTargetedSingleConnection_NeverBroadcast()
    {
        const string AuthorTag = "pinauthor#1";
        const string TargetTag = "pintarget#2";

        var channel = await CreateChannel("W3C Lounge", ChannelType.Public);
        await SeedMembership(channel.Id, AuthorTag);
        await SeedMembership(channel.Id, TargetTag);
        _sessionRegistry.Register("conn-pintarget", Identity(TargetTag), null); // the target is online

        // A strict IHubClients: only Client(connId) is set up. Any broadcast surface (All/Group/AllExcept/
        // Others/User/…) is unconfigured under MockBehavior.Strict, so reaching for one throws inside the
        // fan-out's per-target try/catch — leaving `capturedConnections` empty and failing the assertion.
        var capturedConnections = new List<string>();
        var capturedMethods = new List<string>();
        var clientsMock = new Mock<IHubClients>(MockBehavior.Strict);
        clientsMock
            .Setup(c => c.Client(It.IsAny<string>()))
            .Returns<string>(connId =>
            {
                capturedConnections.Add(connId);
                var proxy = new Mock<ISingleClientProxy>();
                proxy
                    .Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
                    .Callback<string, object[], CancellationToken>((method, _, _) => capturedMethods.Add(method))
                    .Returns(Task.CompletedTask);
                return proxy.Object;
            });
        var hubContextMock = new Mock<IHubContext<ChatHub>>();
        hubContextMock.Setup(h => h.Clients).Returns(clientsMock.Object);

        var fanOut = new MentionFanOut(hubContextMock.Object, _sessionRegistry, _membershipRepository, _mentionInboxRepository, _userDirectory);
        var message = new ChannelMessage
        {
            ChannelId = channel.Id,
            Seq = 1,
            Sender = new MessageSender { BattleTag = AuthorTag, Name = "pinauthor" },
            Content = $"hey <@{TargetTag}>",
            SentAt = Now,
            Shadow = false,
        };

        await fanOut.NotifyAsync(channel, message, new[] { TargetTag }, Now);

        Assert.That(capturedConnections, Is.EqualTo(new[] { "conn-pintarget" }),
            "MentionNotified is delivered via Clients.Client(targetConnectionId) — a single targeted send, never a broadcast");
        Assert.That(capturedMethods, Is.EqualTo(new[] { ChatEvents.MentionNotified }),
            "the single targeted send carries exactly the MentionNotified event");
    }

    // 8(b) continued: source-scan complement to the behavioral pin above (mirrors 8(a)'s
    // Guardrail_OnlyTheCleaner_HardDeletesMentionInbox). The behavioral pin catches a refactor that
    // REPLACES the targeted Clients.Client(...) with a broadcast, but NOT a broadcast added ALONGSIDE the
    // existing targeted send (it would still capture the targeted connection and pass). This closes that
    // false-negative: NO production file that references the ChatEvents.MentionNotified event may
    // co-locate it with a broadcast client surface (Clients.All/AllExcept/Group/GroupExcept/Others/User).
    // The one legitimate emitter (MentionFanOut) sends ONLY via a targeted Clients.Client(connectionId).
    [Test]
    public void Guardrail_MentionNotified_SourceScan_NeverCoLocatedWithABroadcastSurface()
    {
        var productionDir = ProductionSourceDir();
        Assert.That(Directory.Exists(productionDir), Is.True,
            $"expected the production project source at '{productionDir}' — the source-scan guardrail cannot run without it");

        var broadcasters = Directory
            .EnumerateFiles(productionDir, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(path =>
            {
                var text = File.ReadAllText(path);
                var emitsMentionNotified = text.Contains("ChatEvents.MentionNotified");
                // A "Clients.All" contains-check also catches "Clients.AllExcept"; "Clients.Group" also
                // catches "Clients.GroupExcept" — every broadcast surface in D4's leak boundary. The
                // targeted "Clients.Client(...)" the legitimate emitter uses matches none of these.
                var broadcasts = text.Contains("Clients.All")
                    || text.Contains("Clients.Group")
                    || text.Contains("Clients.Others")
                    || text.Contains("Clients.User");
                return emitsMentionNotified && broadcasts;
            })
            .Select(Path.GetFileName)
            .OrderBy(name => name)
            .ToList();

        Assert.That(broadcasters, Is.Empty,
            "MentionNotified must ONLY ever be pushed via a targeted Clients.Client(connectionId) send — no production " +
            "file that references ChatEvents.MentionNotified may co-locate it with a broadcast surface " +
            $"(Clients.All/AllExcept/Group/GroupExcept/Others/User). Found: [{string.Join(", ", broadcasters)}].");
    }

    // 8(c): the hub surface physically declares all six new C6 client→server methods. The EXACT-count pin
    // lives in OldProtocolRemovedTests.HubSurface_ExactlyMatchesPinnedSet (which already lists all six); this
    // complements it by failing loudly if any C6 method is ever removed from the hub itself.
    [Test]
    public void Guardrail_HubSurface_DeclaresAllSixNewC6Methods()
    {
        var declared = typeof(ChatHub)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .ToHashSet();

        var expectedC6Methods = new[]
        {
            nameof(ChatHub.SearchMentionCandidates),
            nameof(ChatHub.GetMentionInbox),
            nameof(ChatHub.MarkMentionsRead),
            nameof(ChatHub.MarkAllMentionsRead),
            nameof(ChatHub.GetPresence),
            nameof(ChatHub.GetPresenceDetails),
        };

        Assert.That(declared, Is.SupersetOf(expectedC6Methods),
            "all six new C6 hub methods must remain physically declared on ChatHub — a missing one is a real " +
            "surface regression, not just a stale pin list.");
    }

    // ---- guardrail helpers -------------------------------------------------------------------------

    private static bool ContainsDeleteVerb(string methodName) =>
        methodName.Contains("Delete", StringComparison.OrdinalIgnoreCase) ||
        methodName.Contains("Remove", StringComparison.OrdinalIgnoreCase) ||
        methodName.Contains("Drop", StringComparison.OrdinalIgnoreCase);

    // Resolves the production project's source directory from THIS test file's compile-time path
    // (…/W3ChampionsChatService.Tests/MentionPresenceIntegrationTests.cs → repo root → …/W3ChampionsChatService).
    private static string ProductionSourceDir([CallerFilePath] string thisFilePath = null)
    {
        var testsDir = Path.GetDirectoryName(thisFilePath);
        var repoRoot = Path.GetDirectoryName(testsDir);
        return Path.Combine(repoRoot!, "W3ChampionsChatService");
    }
}
