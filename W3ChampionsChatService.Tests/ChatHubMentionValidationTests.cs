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
/// C6 Task 4 (D1/D2), amended by the "strip &amp; deliver as plain" decision: the mention-markup gate in
/// <c>SendMessage</c> at step 5.25 — immediately after the channel load and BEFORE the C5 private-lane
/// gates (step 5.5) and the mute gate (step 6). After the amendment the gate validates EXACTLY ONE thing:
/// the mention COUNT cap (an anti-abuse bound). A message is NEVER rejected for the resolvability or
/// access of its mentions — an unresolvable/garbage tag, or a tag naming a non-member, is legal content
/// that delivers VERBATIM and simply never fans out (the membership wall in <see cref="MentionFanOut"/>
/// is the sole authority on who is notified — proven directly in <see cref="MentionFanOutTests"/> and
/// end-to-end in <see cref="ChatHubSendMessageTests"/>). Two properties are load-bearing here: (1) the
/// COUNT cap is content-intrinsic, so a blocked sender and an unblocked sender must get an IDENTICAL
/// outcome for the same over-cap content (running the cap after the private-lane gate's silent
/// short-circuit would leak block state); and (2) a shadow/full-muted sender's over-cap markup must still
/// be rejected normally (rejection is not part of either illusion). Direct-hub idiom mirroring
/// <see cref="ChatHubSendMessageTests"/> (public-channel/mute setup) and <see cref="ChatHubDmSendTests"/>
/// (Dm + a real <see cref="RelationshipProvider"/> over a <see cref="FakeRelationshipSource"/> for the
/// block-ordering pin). NUnit constraint style.
/// </summary>
public class ChatHubMentionValidationTests : IntegrationTestBase
{
    private const string Sender = "peter#123";
    private const string Recipient = "wolf#456";

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
            new NotificationPreferenceRepository(MongoClient));

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

    private static string Mention(string tag) => $"<@{tag}>";

    private void SetBlocked(string battleTag, params string[] blockedTags) =>
        _blocked[battleTag] = new HashSet<string>(blockedTags, StringComparer.OrdinalIgnoreCase);

    // ---------------------------------------------------------------------------------------------
    // Count cap (the ONE retained reject) + strip-and-deliver-as-plain (no reject for access/resolvability)
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Send_SixDistinctMentions_TooLong_NothingPersisted()
    {
        var channel = await CreateChannel("general");
        SeedMember("conn-1", Sender, channel.Id);
        var hub = BuildHub("conn-1");

        // The COUNT cap is the only retained reject — > MaxMentionsPerMessage DISTINCT tags → TooLong.
        var content = string.Join(" ", Enumerable.Range(1, ChatLimits.MaxMentionsPerMessage + 1).Select(i => Mention($"tag{i}#{i}")));
        var result = await hub.SendMessage(channel.Id, content);

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.TooLong));
        var reloaded = await _channelRepository.Load(channel.Id);
        Assert.That(reloaded.LastSeq, Is.EqualTo(0L), "over-cap mention content must not allocate a seq");
        var messages = await _messageRepository.LoadPageBefore(channel.Id, Sender, null, 10);
        Assert.That(messages, Is.Empty, "over-cap mention content must not persist");
    }

    [Test]
    public async Task Send_FiveMentions_AtCap_Ok_Persisted()
    {
        var channel = await CreateChannel("general");
        SeedMember("conn-1", Sender, channel.Id);
        var hub = BuildHub("conn-1");

        // Exactly at the cap → Ok. Resolvability is irrelevant now (no directory seeding) — the tags need
        // not resolve to anyone; the send is accepted purely because the DISTINCT-tag count is <= the cap.
        var content = string.Join(" ", Enumerable.Range(1, ChatLimits.MaxMentionsPerMessage).Select(i => Mention($"target{i}#{i}")));
        var result = await hub.SendMessage(channel.Id, content);

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(result.MessageId, Is.Not.Null);
        var persisted = await _messageRepository.Load(result.MessageId);
        Assert.That(persisted, Is.Not.Null, "exactly-at-cap mentions must persist");
    }

    [Test]
    public async Task Send_UnresolvableMention_StillOk_DeliversVerbatim()
    {
        var channel = await CreateChannel("general");
        SeedMember("conn-1", Sender, channel.Id);
        var hub = BuildHub("conn-1");

        // "ghost#999" resolves to nobody (never seeded anywhere). Strip-and-deliver-as-plain: the send is
        // NOT rejected — the message delivers VERBATIM (the client renders the invalid <@…> as plain text),
        // and the fan-out's membership wall alone drops the (non-member) target. NOT TooLong.
        var content = $"hey {Mention("ghost#999")}";
        var result = await hub.SendMessage(channel.Id, content);

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok),
            "an unresolvable/garbage mention must NEVER reject the send (strip & deliver as plain)");
        var reloaded = await _channelRepository.Load(channel.Id);
        Assert.That(reloaded.LastSeq, Is.EqualTo(1L), "the message persists — an unresolvable mention is legal content");
        var persisted = await _messageRepository.Load(result.MessageId);
        Assert.That(persisted.Content, Is.EqualTo(content), "the message text delivers verbatim — the invalid <@…> token is kept");
    }

    [Test]
    public async Task Send_DuplicateSameTagSixTimes_CountsOnce_Ok()
    {
        var channel = await CreateChannel("general");
        SeedMember("conn-1", Sender, channel.Id);
        var hub = BuildHub("conn-1");

        var content = string.Concat(Enumerable.Repeat(Mention("target#1"), 6));
        var result = await hub.SendMessage(channel.Id, content);

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok),
            "a single tag repeated 6 times dedupes to ONE distinct target (D1) — well under the 5-cap");
    }

    // ---------------------------------------------------------------------------------------------
    // Zero-cost / zero-DB: the send path performs NO directory read — even for a message WITH mentions
    // (the resolvability Load loop was removed by the strip-and-deliver-as-plain amendment).
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Send_WithMentions_PerformsZeroDirectoryReads()
    {
        var channel = await CreateChannel("general");
        SeedMember("conn-1", Sender, channel.Id);
        var countingDirectory = new CountingUserDirectoryRepository(MongoClient);
        var hub = BuildHub("conn-1", countingDirectory);

        // A message carrying real mention markup — under the old gate this would have driven one directory
        // Load per tag. The resolvability check is gone, so the send path must now do ZERO directory reads.
        var result = await hub.SendMessage(channel.Id, $"hey {Mention("wolf#456")} and {Mention("frank#789")}");

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(countingDirectory.LoadCallCount, Is.EqualTo(0),
            "the send path performs ZERO directory reads — resolvability is no longer validated (strip & deliver as plain)");
    }

    // ---------------------------------------------------------------------------------------------
    // Placement: the retained COUNT cap trips BEFORE the block/mute gates, with an outcome identical
    // regardless of block/mute state (D2's load-bearing property). The "invalid content" trigger is now
    // OVER-CAP mention markup (the one reject the amended gate still enforces), not an unresolvable tag.
    // ---------------------------------------------------------------------------------------------

    // > MaxMentionsPerMessage distinct mention tokens — the one content-intrinsic reject (TooLong) the
    // amended step-5.25 gate still enforces, used below to prove the gate's ordering vs. the block/mute gates.
    private static string OverCapMentions() =>
        string.Join(" ", Enumerable.Range(1, ChatLimits.MaxMentionsPerMessage + 1).Select(i => Mention($"tag{i}#{i}")));

    [Test]
    public async Task Send_BlockedDmSender_OverCapMentions_TooLong_SameAsUnblockedControl()
    {
        var overCapContent = OverCapMentions();

        // Blocked lane: the recipient has blocked the sender — if content were deliverable, ApplyPrivateLaneGates
        // would fabricate an Ok (FakeSendAck). The content-intrinsic COUNT cap must intercept BEFORE that gate.
        var blockedChannel = await CreateDm(Sender, Recipient, DmRequestState.Accepted);
        SeedMember("conn-blocked", Sender, blockedChannel.Id, ChannelType.Dm);
        SetBlocked(Recipient, Sender);
        var blockedHub = BuildHub("conn-blocked");

        var blockedResult = await blockedHub.SendMessage(blockedChannel.Id, overCapContent);

        // Unblocked control: identical content, a DIFFERENT DM pair with no block relationship at all
        // (same sender, so a genuinely comparable "what does THIS sender see" control).
        const string UnblockedRecipient = "frank#789";
        var unblockedChannel = await CreateDm(Sender, UnblockedRecipient, DmRequestState.Accepted);
        SeedMember("conn-unblocked", Sender, unblockedChannel.Id, ChannelType.Dm);
        var unblockedHub = BuildHub("conn-unblocked");

        var unblockedResult = await unblockedHub.SendMessage(unblockedChannel.Id, overCapContent);

        Assert.That(blockedResult.Code, Is.EqualTo(ChatResultCode.TooLong),
            "the content-intrinsic COUNT cap runs BEFORE the private-lane block gate (D2) — a blocked sender " +
            "must never observe a different outcome than an unblocked sender for the same over-cap content");
        Assert.That(blockedResult, Is.EqualTo(unblockedResult),
            "a blocked sender and an unblocked sender must get a BYTE-IDENTICAL SendMessageResult " +
            "(Code/RetryAfterSeconds/MessageId/Seq) for the same over-cap content — anything less leaks block state");

        // Neither lane persisted or opened anything.
        Assert.That((await _channelRepository.Load(blockedChannel.Id)).LastSeq, Is.EqualTo(0L));
        Assert.That((await _channelRepository.Load(unblockedChannel.Id)).LastSeq, Is.EqualTo(0L));
    }

    [Test]
    public async Task Send_ShadowMutedSender_OverCapMentions_TooLong_UnderCapMentions_OkFlaggedShadow()
    {
        var channel = await CreateChannel("W3C Lounge");
        SeedMember("conn-1", Sender, channel.Id, mute: MuteStatus.Shadow, muteEnd: Now.AddDays(1));
        var hub = BuildHub("conn-1");

        // Over-cap markup: rejected NORMALLY (TooLong) — a rejection does not break the shadow illusion.
        var invalidResult = await hub.SendMessage(channel.Id, OverCapMentions());
        Assert.That(invalidResult.Code, Is.EqualTo(ChatResultCode.TooLong),
            "a shadow-muted sender's over-cap markup must be rejected exactly like any other sender's");

        // Under-cap markup (a single mention — resolvability is irrelevant): still gets the illusion (Ok)
        // and persists flagged Shadow=true.
        var validResult = await hub.SendMessage(channel.Id, Mention("target#1"));
        Assert.That(validResult.Code, Is.EqualTo(ChatResultCode.Ok), "deliverable content still gets the shadow illusion");
        var persisted = await _messageRepository.Load(validResult.MessageId);
        Assert.That(persisted, Is.Not.Null);
        Assert.That(persisted.Shadow, Is.True, "the accepted send must still persist flagged Shadow=true");

        var reloaded = await _channelRepository.Load(channel.Id);
        Assert.That(reloaded.LastSeq, Is.EqualTo(1L),
            "only the under-cap send allocates a seq; the rejected over-cap send must not");
    }

    [Test]
    public async Task Send_FullMutedSender_OverCapMentions_TooLong()
    {
        var channel = await CreateChannel("W3C Lounge");
        SeedMember("conn-1", Sender, channel.Id, mute: MuteStatus.Full, muteEnd: Now.AddDays(1));
        var hub = BuildHub("conn-1");

        var result = await hub.SendMessage(channel.Id, OverCapMentions());

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.TooLong),
            "the content-intrinsic COUNT cap (step 5.25) precedes the mute gate (step 6) — documented ordering " +
            "consequence (D2): a full-muted sender with over-cap markup gets TooLong, not Muted");
        var reloaded = await _channelRepository.Load(channel.Id);
        Assert.That(reloaded.LastSeq, Is.EqualTo(0L));
    }
}
