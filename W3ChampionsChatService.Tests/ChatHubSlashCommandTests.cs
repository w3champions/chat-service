using System;
using System.Collections.Generic;
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
/// The slash-command gate in <c>SendMessage</c> at step 4.5 — after the rate limiter (step 4), before
/// the channel load (step 5), the C5 private-lane gates (step 5.5) and the mute gate (step 6).
/// Two properties are load-bearing: (1) the check is content-intrinsic, so a DM-blocked sender and an
/// unblocked sender must get an IDENTICAL result for identical content (running it after the
/// private-lane gate's silent fake-Ok would leak block state); and (2) a shadow/full-muted sender's
/// command must still be rejected normally — a rejection is not part of either illusion.
/// </summary>
public class ChatHubSlashCommandTests : IntegrationTestBase
{
    private const string Sender = "peter#123";
    private const string Recipient = "wolf#456";

    // U+FEFF ZERO WIDTH NO-BREAK SPACE (byte-order mark) — a Unicode FORMAT character (category Cf),
    // NOT whitespace, so .NET's string.Trim() leaves it exactly where it is (it stopped counting as
    // whitespace in .NET 4.0). Spelled numerically on purpose: an escape-sequence literal renders as an
    // invisible character in this file, which no reviewer or diff can see.
    private const char Bom = (char)0xFEFF;

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
    private UserSettingsRepository _userSettings;
    private DmInitiationTracker _dmInitiationTracker;
    private FakeTimeProvider _time;

    // Per-tag blocked set, read by the fake source's snapshot factory (OrdinalIgnoreCase) — only the
    // block-ordering test populates this; every other test gets an always-empty snapshot.
    private readonly Dictionary<string, HashSet<string>> _blocked = new(StringComparer.OrdinalIgnoreCase);
    private FakeRelationshipSource _relationshipSource;
    private RelationshipProvider _relationshipProvider;

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    [SetUp]
    public void SetupBeforeEach()
    {
        _blocked.Clear();
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
        _userSettings = new UserSettingsRepository(MongoClient);
        _dmInitiationTracker = new DmInitiationTracker();

        _relationshipSource = new FakeRelationshipSource((tag, now) => new RelationshipSnapshot(
            tag,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            _blocked.TryGetValue(tag, out var b) ? b : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            now));
        _relationshipProvider = new RelationshipProvider(_relationshipSource, _time);

        _assembler = new SessionStateAssembler(
            _membershipRepository,
            _channelRepository,
            _messageRepository,
            new MuteRepository(MongoClient),
            _onlineMemberRegistry,
            _connectionMapping,
            new MentionInboxRepository(MongoClient));
    }

    private ChatHub BuildHub(string connectionId, UserDirectoryRepository userDirectory = null)
    {
        var viewerResolver = new ViewerResolver(_sessionRegistry, _connectionMapping);
        var hub = new ChatHub(
            _connectionMapping,
            _reconcileHarness.Service,
            _ticketStore,
            _sessionRegistry,
            userDirectory ?? _userDirectory,
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
            _relationshipProvider,
            _userSettings,
            _dmInitiationTracker,
            _authService.Object,
            MentionFanOutTestFactory.CreateIgnored(MongoClient),
            new PresenceInterestRegistry(),
            new MentionInboxRepository(MongoClient),
            new NotificationPreferenceRepository(MongoClient),
            viewerResolver);

        var clients = new Mock<IHubCallerClients>();
        var callerProxy = new Mock<ISingleClientProxy>();
        callerProxy
            .Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        clients.Setup(c => c.Caller).Returns(callerProxy.Object);
        clients.Setup(c => c.Client(It.IsAny<string>())).Returns(callerProxy.Object);
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

    private async Task<ChatChannel> CreateChannel(string name, ChannelType type = ChannelType.Public)
    {
        var channel = new ChatChannel { Type = type, Name = name, NormalizedName = ChannelNames.Normalize(name) };
        await _channelRepository.Insert(channel);
        return channel;
    }

    private Task<ChatChannel> CreateDm(string initiator, string recipient, DmRequestState state) =>
        _channelRepository.FindOrCreateDm(initiator, recipient, initiator, state, Now);

    // Seeds a connection the SAME way the connect path does: a live session, the connect-time ChatUser
    // (flair snapshot source), the mute cache, and an OnlineMemberRegistry membership for the channel.
    private void SeedMember(
        string connectionId,
        string battleTag,
        string channelId,
        ChannelType type = ChannelType.Public,
        MuteStatus mute = MuteStatus.None,
        DateTime? muteEnd = null)
    {
        RegisterSession(connectionId, battleTag);
        _connectionMapping.RegisterUser(connectionId, new ChatUser(battleTag, false, battleTag.Split('#')[0], new ProfilePicture(), null, null));
        _connectionMapping.SetMute(connectionId, mute, muteEnd ?? DateTime.MinValue);
        _onlineMemberRegistry.Join(channelId, connectionId, new MemberState(battleTag, NotificationLevel.Mentions, 0, type));
    }

    private void SetBlocked(string battleTag, params string[] blockedTags) =>
        _blocked[battleTag] = new HashSet<string>(blockedTags, StringComparer.OrdinalIgnoreCase);

    [TestCase("/w Grubby hi")]
    [TestCase("/whisper Grubby hi")]
    [TestCase("/r thanks")]
    [TestCase("/join channel")]
    [TestCase("/stats")]
    [TestCase("/ю привет")]
    public async Task Send_SlashCommand_UnsupportedCommand_NothingPersisted(string content)
    {
        var channel = await CreateChannel("W3C Lounge");
        SeedMember("conn-1", Sender, channel.Id);
        var hub = BuildHub("conn-1");

        var result = await hub.SendMessage(channel.Id, content);

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.UnsupportedCommand));

        // Non-persistence is the load-bearing assertion, not the return code: fan-out
        // (FanOutEngine.OnMessagePersisted, ChatHub.Messaging.cs:341) and the mention fan-out (:368) are
        // strictly downstream of the persist at :311, so "no seq allocated and nothing in the page"
        // proves the message was never broadcast to anyone. There is no fan-out mock to assert on here —
        // FanOutEngineTestFactory.CreateIgnored() is an inert engine, not a verifiable Mock.
        var reloaded = await _channelRepository.Load(channel.Id);
        Assert.That(reloaded.LastSeq, Is.EqualTo(0L), "a rejected command must not allocate a seq");
        var messages = await _messageRepository.LoadPageBefore(channel.Id, Sender, null, 10);
        Assert.That(messages, Is.Empty, "a rejected command must not persist");
    }

    [TestCase("/usr/local/bin")]
    [TestCase("//note")]
    [TestCase("/")]
    [TestCase("/ 10 gold")]
    [TestCase("/10 min")]
    [TestCase("10/10 game")]
    [TestCase("gg /w hi")]
    [TestCase("gg wp")]
    public async Task Send_OrdinaryContent_Ok_Persisted(string content)
    {
        var channel = await CreateChannel("W3C Lounge");
        SeedMember("conn-1", Sender, channel.Id);
        var hub = BuildHub("conn-1");

        var result = await hub.SendMessage(channel.Id, content);

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok),
            "content that merely contains a slash is ordinary chat and must deliver unchanged");
        var persisted = await _messageRepository.Load(result.MessageId);
        Assert.That(persisted, Is.Not.Null);
    }

    [Test]
    public async Task Send_SlashCommandWithLeadingWhitespace_UnsupportedCommand()
    {
        var channel = await CreateChannel("W3C Lounge");
        SeedMember("conn-1", Sender, channel.Id);
        var hub = BuildHub("conn-1");

        var result = await hub.SendMessage(channel.Id, "   /w Grubby hi");

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.UnsupportedCommand),
            "step 2 trims before step 4.5 runs, so padding a command must not smuggle it past the gate");
        var reloaded = await _channelRepository.Load(channel.Id);
        Assert.That(reloaded.LastSeq, Is.EqualTo(0L));
    }

    [Test]
    public async Task Send_SlashCommandToGhostChannel_UnsupportedCommand_NotNotFound()
    {
        // N1 ORDERING PIN — the only test in this file that notices if step 4.5 moves BELOW the channel
        // load. Every other test here creates a real channel first, so the guard would keep returning
        // UnsupportedCommand from anywhere between step 4 and step 5.5 and they would all stay green;
        // the "rejection costs no database read" requirement would die silently.
        // The lever is the member-of-a-deleted-channel edge (the same fixture shape as
        // ChatHubSendMessageTests.Send_UnknownChannel_ReturnsNotFound): the OnlineMemberRegistry says
        // "member", but no channel doc exists. Membership (step 3) is an in-memory lookup, so the FIRST
        // thing that touches Mongo on this path is the channel load at step 5 — and that load is exactly
        // what returns NotFound. So UnsupportedCommand here PROVES the reject happened before any DB
        // read; move the guard one line below the load and this test flips to NotFound.
        const string GhostChannelId = "ghost-channel-id";
        SeedMember("conn-1", Sender, GhostChannelId);
        var hub = BuildHub("conn-1");

        var result = await hub.SendMessage(GhostChannelId, "/w Grubby hi");

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.UnsupportedCommand),
            "step 4.5 must precede the channel load at step 5 — NotFound here would mean the reject " +
            "paid for a Mongo read first (N1)");
    }

    // ---------------------------------------------------------------------------------------------
    // Leading Unicode FORMAT characters (category Cf). The detector's pattern is anchored, so anything
    // before the "/" defeats it — and string.Trim() does NOT strip U+FEFF (it stopped counting as
    // whitespace in .NET 4.0). Before ChatHub.NormalizeSendContent existed, a BOM-bearing paste of
    // "[BOM]/w Grubby <secret>" sailed past step 4.5 and was persisted and fanned out to the whole
    // channel — the exact leak the guard exists to prevent. These tests pin the normalization that
    // closed it.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Send_BomPrefixedSlashCommand_UnsupportedCommand_NothingPersisted()
    {
        var channel = await CreateChannel("W3C Lounge");
        SeedMember("conn-1", Sender, channel.Id);
        var hub = BuildHub("conn-1");

        var result = await hub.SendMessage(channel.Id, Bom + "/w Grubby hi");

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.UnsupportedCommand),
            "step 2 strips leading format characters, so a BOM cannot smuggle a command past the " +
            "anchored guard — Trim() alone would have left the BOM in place and let this through");
        var reloaded = await _channelRepository.Load(channel.Id);
        Assert.That(reloaded.LastSeq, Is.EqualTo(0L), "a rejected command must not allocate a seq");
        var messages = await _messageRepository.LoadPageBefore(channel.Id, Sender, null, 10);
        Assert.That(messages, Is.Empty, "a BOM-prefixed command must not persist — F1/F2");
    }

    // Both interleavings, because the normalization strips whitespace and format characters in ONE
    // alternating pass rather than one pass of each: doing whitespace-then-format would leave the
    // second case intact, and format-then-whitespace would leave the first.
    [TestCase(true, TestName = "Send_BomThenSpacesBeforeSlashCommand_UnsupportedCommand")]
    [TestCase(false, TestName = "Send_SpacesThenBomBeforeSlashCommand_UnsupportedCommand")]
    public async Task Send_InterleavedBomAndWhitespaceBeforeSlashCommand_UnsupportedCommand(bool bomFirst)
    {
        var channel = await CreateChannel("W3C Lounge");
        SeedMember("conn-1", Sender, channel.Id);
        var hub = BuildHub("conn-1");
        var content = bomFirst ? Bom + "  /w hi" : "  " + Bom + "/w hi";

        var result = await hub.SendMessage(channel.Id, content);

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.UnsupportedCommand),
            "leading whitespace and leading format characters must be stripped in one interleaved " +
            "pass — either ordering has to normalize to \"/w hi\"");
        var reloaded = await _channelRepository.Load(channel.Id);
        Assert.That(reloaded.LastSeq, Is.EqualTo(0L));
    }

    [Test]
    public async Task Send_AstralFormatCharacterPrefixedSlashCommand_UnsupportedCommand_NothingPersisted()
    {
        // Pins the SURROGATE-PAIR ADVANCE in NormalizeSendContent (the `? 2 : 1` step). U+E0001
        // LANGUAGE TAG is a FORMAT character (Cf) outside the BMP, so it occupies TWO chars. With a
        // bare `1` advance the scan consumes the high surrogate, then meets the orphaned LOW surrogate
        // — category Surrogate, neither Format nor whitespace — and stops, leaving the content still
        // prefixed so the anchored pattern misses it and the send sails past the step-4.5 guard. That
        // is a narrower re-opening of the very BOM hole this normalization closed, and NO other test
        // notices: every other astral assertion here is BMP-only.
        // (Verified by mutation. Downstream of the missed guard the send does not actually persist —
        // the orphaned \uDC01 fails BSON serialization with an EncoderFallbackException — so the
        // mutant dies on an exception rather than on the Assert below. Either way the load-bearing
        // property is the same and only this test observes it: the command was NOT rejected.)
        var channel = await CreateChannel("W3C Lounge");
        SeedMember("conn-1", Sender, channel.Id);
        var hub = BuildHub("conn-1");
        var content = char.ConvertFromUtf32(0xE0001) + "/w Grubby hi";

        var result = await hub.SendMessage(channel.Id, content);

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.UnsupportedCommand),
            "a leading ASTRAL format character defeats the anchor exactly like a BOM does, so the " +
            "normalization must advance by whole code points, not by single chars");
        var reloaded = await _channelRepository.Load(channel.Id);
        Assert.That(reloaded.LastSeq, Is.EqualTo(0L), "a rejected command must not allocate a seq");
        var messages = await _messageRepository.LoadPageBefore(channel.Id, Sender, null, 10);
        Assert.That(messages, Is.Empty, "an astral-prefixed command must not persist — F1/F2");
    }

    [Test]
    public async Task Send_BomPrefixedOrdinaryContent_Ok_PersistedWithBomStripped()
    {
        var channel = await CreateChannel("W3C Lounge");
        SeedMember("conn-1", Sender, channel.Id);
        var hub = BuildHub("conn-1");

        var result = await hub.SendMessage(channel.Id, Bom + "gg wp");

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok),
            "stripping the leading BOM must not turn ordinary content into a reject");
        var persisted = await _messageRepository.Load(result.MessageId);
        Assert.That(persisted.Content, Is.EqualTo("gg wp"),
            "the normalization runs before persist, so the leading BOM is gone from the stored content");
    }

    [Test]
    public async Task Send_FormatCharacterInsideContent_Ok_PersistedVerbatim()
    {
        var channel = await CreateChannel("W3C Lounge");
        SeedMember("conn-1", Sender, channel.Id);
        var hub = BuildHub("conn-1");
        var content = "gg" + Bom + "wp";

        var result = await hub.SendMessage(channel.Id, content);

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        var persisted = await _messageRepository.Load(result.MessageId);
        Assert.That(persisted.Content, Is.EqualTo(content),
            "only LEADING format characters are stripped — one in the middle of a message is the " +
            "sender's content and must survive byte-for-byte");
    }

    [Test]
    public async Task Send_SlashCommandInDm_UnsupportedCommand_NotFakeSendAck()
    {
        var channel = await CreateDm(Sender, Recipient, DmRequestState.Accepted);
        SeedMember("conn-1", Sender, channel.Id, ChannelType.Dm);
        var hub = BuildHub("conn-1");

        var result = await hub.SendMessage(channel.Id, "/w Grubby hi");

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.UnsupportedCommand),
            "the gate applies to every channel type — a DM command must be rejected, not silently " +
            "fake-acked by ApplyPrivateLaneGates");
        Assert.That(result.MessageId, Is.Null, "a reject carries no message id (a FakeSendAck would)");
        var reloaded = await _channelRepository.Load(channel.Id);
        Assert.That(reloaded.LastSeq, Is.EqualTo(0L));
    }

    [Test]
    public async Task Send_BlockedDmSender_SlashCommand_SameResultAsUnblockedControl()
    {
        const string Command = "/w Grubby hi";

        // Blocked lane: the recipient has blocked the sender — if the content were deliverable,
        // ApplyPrivateLaneGates would fabricate an Ok. The content-intrinsic gate must intercept first.
        var blockedChannel = await CreateDm(Sender, Recipient, DmRequestState.Accepted);
        SeedMember("conn-blocked", Sender, blockedChannel.Id, ChannelType.Dm);
        SetBlocked(Recipient, Sender);
        var blockedHub = BuildHub("conn-blocked");

        var blockedResult = await blockedHub.SendMessage(blockedChannel.Id, Command);

        // Armed-block control: proves SetBlocked is actually live, not merely configured-but-inert. If
        // the block wiring were ever defanged, the "blocked" lane above would behave exactly like an
        // unblocked one for a COMMAND too — both would return UnsupportedCommand, and the equality
        // assertion below would pass having proven nothing about the leak property this test is named
        // for. The disambiguator is ORDINARY content: a genuinely blocked DM never reaches the real
        // persist path — it short-circuits through ApplyPrivateLaneGates.FakeSendAck (ChatHub.Dm.cs:368),
        // which fabricates Ok with a non-null MessageId (a fresh ObjectId) and a Seq computed as
        // LastSeq+1, WITHOUT ever calling ChannelRepository.AllocateSeq. A live send, by contrast, always
        // allocates a real seq. So Ok + non-null MessageId + an UNMOVED LastSeq is a signature only the
        // fake ack produces — if the block were dead, this send would persist for real and bump LastSeq
        // to 1, failing the check below. Must run on blockedHub/blockedChannel BEFORE the unblocked lane
        // is seeded: SessionRegistry is single-session-per-battleTag, so seeding "conn-unblocked" for the
        // SAME Sender below would displace conn-blocked's session and fail-closed this hub's calls.
        var armedControlResult = await blockedHub.SendMessage(blockedChannel.Id, "gg wp");
        Assert.That(armedControlResult.Code, Is.EqualTo(ChatResultCode.Ok),
            "a blocked DM's ordinary content is silently swallowed as a fabricated ack, not rejected");
        Assert.That(armedControlResult.MessageId, Is.Not.Null,
            "FakeSendAck fabricates a MessageId even though nothing was persisted");
        Assert.That((await _channelRepository.Load(blockedChannel.Id)).LastSeq, Is.EqualTo(0L),
            "a fabricated ack must not allocate a seq — a real send would have bumped LastSeq to 1");

        // Unblocked control: identical content, same sender, a different DM pair with no block at all.
        const string UnblockedRecipient = "frank#789";
        var unblockedChannel = await CreateDm(Sender, UnblockedRecipient, DmRequestState.Accepted);
        SeedMember("conn-unblocked", Sender, unblockedChannel.Id, ChannelType.Dm);
        var unblockedHub = BuildHub("conn-unblocked");

        var unblockedResult = await unblockedHub.SendMessage(unblockedChannel.Id, Command);

        Assert.That(blockedResult.Code, Is.EqualTo(ChatResultCode.UnsupportedCommand));
        Assert.That(blockedResult, Is.EqualTo(unblockedResult),
            "a blocked and an unblocked sender must get a BYTE-IDENTICAL SendMessageResult for the same " +
            "command content — anything less leaks block state");

        Assert.That((await _channelRepository.Load(blockedChannel.Id)).LastSeq, Is.EqualTo(0L));
        Assert.That((await _channelRepository.Load(unblockedChannel.Id)).LastSeq, Is.EqualTo(0L));
    }

    [Test]
    public async Task Send_ShadowMutedSender_SlashCommand_UnsupportedCommand()
    {
        var channel = await CreateChannel("W3C Lounge");
        SeedMember("conn-1", Sender, channel.Id, mute: MuteStatus.Shadow, muteEnd: Now.AddDays(1));
        var hub = BuildHub("conn-1");

        var commandResult = await hub.SendMessage(channel.Id, "/w Grubby hi");
        Assert.That(commandResult.Code, Is.EqualTo(ChatResultCode.UnsupportedCommand),
            "step 4.5 precedes the mute gate at step 6 — a shadow-muted sender's command is rejected " +
            "exactly like anyone else's; a rejection does not break the shadow illusion");

        // Ordinary content from the same sender still gets the illusion and persists flagged.
        var ordinaryResult = await hub.SendMessage(channel.Id, "gg wp");
        Assert.That(ordinaryResult.Code, Is.EqualTo(ChatResultCode.Ok));
        var persisted = await _messageRepository.Load(ordinaryResult.MessageId);
        Assert.That(persisted.Shadow, Is.True);

        var reloaded = await _channelRepository.Load(channel.Id);
        Assert.That(reloaded.LastSeq, Is.EqualTo(1L),
            "only the ordinary send allocates a seq; the rejected command must not");
    }

    [Test]
    public async Task Send_FullMutedSender_SlashCommand_UnsupportedCommand_NotMuted()
    {
        var channel = await CreateChannel("W3C Lounge");
        SeedMember("conn-1", Sender, channel.Id, mute: MuteStatus.Full, muteEnd: Now.AddDays(1));
        var hub = BuildHub("conn-1");

        var result = await hub.SendMessage(channel.Id, "/w Grubby hi");

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.UnsupportedCommand),
            "documented ordering consequence: step 4.5 precedes step 6, so a full-muted sender typing a " +
            "command gets UnsupportedCommand, not Muted");
        var reloaded = await _channelRepository.Load(channel.Id);
        Assert.That(reloaded.LastSeq, Is.EqualTo(0L));

        // Armed-mute control: proves the fixture is actually full-muted, not merely configured-but-inert
        // — mirrors how Send_ShadowMutedSender_SlashCommand_UnsupportedCommand self-validates above.
        // Ordinary content from the SAME sender must still hit the real step-6 mute gate and come back
        // Muted; if the mute wiring were dead, this would return Ok instead, and the UnsupportedCommand
        // result asserted above would look identical whether or not the sender is actually muted.
        var ordinaryResult = await hub.SendMessage(channel.Id, "gg wp");
        Assert.That(ordinaryResult.Code, Is.EqualTo(ChatResultCode.Muted),
            "control: the same sender's ordinary content must hit the real mute gate at step 6");
    }
}
