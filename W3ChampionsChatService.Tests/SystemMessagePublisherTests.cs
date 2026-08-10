using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Driver;
using NUnit.Framework;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.FanOut;
using W3ChampionsChatService.Messages;
using W3ChampionsChatService.Protocol;
using W3ChampionsChatService.Sessions;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// Post-game chat Plan A Task 3 — the server-authored insert path. Full-stack over the ephemeral
/// Mongo, a real <see cref="FanOutEngine"/> and a <see cref="HubPushCaptureHarness"/>, mirroring
/// <see cref="MatchChannelServiceTests"/>'s fixture idiom.
/// </summary>
public class SystemMessagePublisherTests : IntegrationTestBase
{
    private static readonly DateTime T0 = new(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

    private FakeTimeProvider _time;
    private HubPushCaptureHarness _harness;
    private SessionRegistry _sessionRegistry;
    private FocusRegistry _focusRegistry;
    private OnlineMemberRegistry _onlineMemberRegistry;
    private FanOutEngine _fanOutEngine;
    private ChannelRepository _channelRepository;
    private MessageRepository _messageRepository;
    private SystemMessagePublisher _publisher;

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    [SetUp]
    public void SetupBeforeEach()
    {
        _time = new FakeTimeProvider(new DateTimeOffset(T0, TimeSpan.Zero));
        _harness = new HubPushCaptureHarness();
        _sessionRegistry = new SessionRegistry();
        _focusRegistry = new FocusRegistry();
        _onlineMemberRegistry = new OnlineMemberRegistry();
        _fanOutEngine = new FanOutEngine(
            _harness.HubContext, _focusRegistry, _onlineMemberRegistry,
            new ActivityCoalescer(_harness.HubContext, _onlineMemberRegistry),
            _sessionRegistry, new PresenceInterestRegistry(),
            new ViewersAccumulator(_harness.HubContext, _focusRegistry,
                new ViewerResolver(_sessionRegistry, new ConnectionMapping())),
            _time);
        _channelRepository = new ChannelRepository(MongoClient);
        _messageRepository = new MessageRepository(MongoClient);
        _publisher = new SystemMessagePublisher(_messageRepository, _channelRepository, _fanOutEngine, _time);
    }

    private static SystemMessageBody Intro() => new()
    {
        Key = "match_intro",
        Params = new Dictionary<string, string> { ["map"] = "Amazonia" },
        ListParams = new Dictionary<string, List<string>> { ["players"] = ["Grubby#2136", "Happy#2233"] },
        FallbackText = "Match on Amazonia — Grubby#2136, Happy#2233",
    };

    private Task<ChatChannel> NewMatchChannel(string systemRef = "match-1") =>
        _channelRepository.FindOrCreateSystem(SystemChannelKind.Match, systemRef, "Amazonia", Now);

    [Test]
    public async Task Publish_PersistsSystemMessage_AllocatesSeq_AdvancesLastMessageAt()
    {
        var channel = await NewMatchChannel();
        // Advanced BEFORE publish: FindOrCreateSystem's SetOnInsert already stamped LastMessageAt/SentAt
        // at T0, so a frozen clock would let AllocateSeq skip its own stamp and the assertions below
        // would still pass against the creation-time value — this is the only way to prove AllocateSeq
        // (not FindOrCreateSystem) is what advanced them.
        _time.Advance(TimeSpan.FromHours(1));

        var result = await _publisher.Publish(channel, Intro(), dedupeKey: null);

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok), "a fresh publish must report Ok, not a partial-write code");
        Assert.That(result.Seq, Is.EqualTo(1), "the first message in a fresh channel gets seq 1");

        var stored = await _messageRepository.Load(result.MessageId);
        Assert.That(stored.Kind, Is.EqualTo(MessageKind.System), "publish must write a System-kind message");
        Assert.That(stored.Sender, Is.Null, "system messages carry no sender snapshot");
        Assert.That(stored.SystemMessage.Key, Is.EqualTo("match_intro"), "the structured body must round-trip through the insert");
        Assert.That(stored.SentAt, Is.EqualTo(Now), "SentAt must be the publisher's injected clock, not wall time");
        // Pinned to the exact instant, not merely non-null: ExpiryCalculator.ForChannelMessage picks 30d
        // for a System channel and 90d for Dm/GroupDm, and Is.Not.Null would pass just as happily under
        // the wrong branch.
        Assert.That(stored.ExpiresAt, Is.EqualTo(Now + RetentionPeriods.ChannelMessages),
            "system messages follow the 30d CHANNEL-message TTL, not the 90d direct-message one");

        var reloaded = await _channelRepository.Load(channel.Id);
        Assert.That(reloaded.LastSeq, Is.EqualTo(1), "AllocateSeq ran — paging and unread both key off it");
        Assert.That(reloaded.LastMessageAt, Is.EqualTo(Now), "AllocateSeq must stamp LastMessageAt on every insert path");
    }

    [Test]
    public async Task Publish_NeverTouchesChannelExpiresAt()
    {
        var channel = await NewMatchChannel();
        var expiryBefore = channel.ExpiresAt;
        // Advanced BEFORE publish: at a frozen clock, a re-stamp with shellExpiresAt: now would write the
        // IDENTICAL value FindOrCreateSystem already wrote, and this assertion would pass even under the
        // exact mutation (shellExpiresAt: null -> ExpiryCalculator.ForChannelShell(channel, now)) D6 exists
        // to forbid. Advancing the clock makes a re-stamp observably move ExpiresAt.
        _time.Advance(TimeSpan.FromHours(1));

        await _publisher.Publish(channel, Intro(), dedupeKey: null);

        var reloaded = await _channelRepository.Load(channel.Id);
        Assert.That(reloaded.ExpiresAt, Is.EqualTo(expiryBefore),
            "retention is deliberately unchanged (spec D6) — shellExpiresAt must be null for System channels");
    }

    [Test]
    public async Task Publish_IsIdempotentOnDedupeKey()
    {
        await ChatDomainIndexes.EnsureAllAsync(MongoClient);
        var channel = await NewMatchChannel();
        // Focused-member setup mirrors Publish_DeliversMessageReceivedToFocusedMembers — needed so the
        // signal-count assertion below can distinguish "the retry pushed nothing" from "there was never
        // anyone to push to".
        _sessionRegistry.Register("conn-alice",
            new W3CUserAuthentication { BattleTag = "Alice#1", Name = "Alice" }, null);
        _onlineMemberRegistry.Join(channel.Id, "conn-alice", new MemberState("Alice#1", NotificationLevel.All, 0, ChannelType.System));
        _focusRegistry.Focus("conn-alice", channel.Id, "Alice#1");

        var first = await _publisher.Publish(channel, Intro(), dedupeKey: "match_intro");
        var second = await _publisher.Publish(channel, Intro(), dedupeKey: "match_intro");

        Assert.That(second.Code, Is.EqualTo(ChatResultCode.Ok), "a dedupe retry is a success, not a conflict — the caller asked that the message exist");
        Assert.That(second.MessageId, Is.EqualTo(first.MessageId), "a retry returns the original message");
        Assert.That(second.Seq, Is.EqualTo(first.Seq), "a retry must not allocate or return a new seq");

        var all = await _messageRepository.LoadForModerator(channel.Id);
        Assert.That(all, Has.Count.EqualTo(1), "mm retries on timeout — the intro must never double-post");
        Assert.That(all[0].DedupeKey, Is.EqualTo("match_intro"), "a non-empty dedupeKey argument must be persisted verbatim onto the stored row, not just used for the lookup");
        Assert.That(_harness.SignalCount("conn-alice", ChatEvents.MessageReceived), Is.EqualTo(1),
            "a deduped retry must return before fan-out runs — it must not re-push MessageReceived for a message the client already has");
    }

    [Test]
    public async Task Publish_DeliversMessageReceivedToFocusedMembers()
    {
        var channel = await NewMatchChannel();
        _sessionRegistry.Register("conn-alice",
            new W3CUserAuthentication { BattleTag = "Alice#1", Name = "Alice" }, null);
        _onlineMemberRegistry.Join(channel.Id, "conn-alice", new MemberState("Alice#1", NotificationLevel.All, 0, ChannelType.System));
        _focusRegistry.Focus("conn-alice", channel.Id, "Alice#1");

        await _publisher.Publish(channel, Intro(), dedupeKey: null);

        Assert.That(_harness.SignalCount("conn-alice", ChatEvents.MessageReceived), Is.EqualTo(1), "a focused member must receive exactly one MessageReceived push");
        var payload = _harness.PayloadFor("conn-alice", ChatEvents.MessageReceived);
        Assert.That(payload, Is.TypeOf<MessageDto>(), "the pushed payload for a MessageReceived push must be a MessageDto");
        var dto = (MessageDto)payload;
        Assert.That(dto.Kind, Is.EqualTo(MessageKind.System), "the pushed payload must carry the System kind");
        Assert.That(dto.SystemMessage.FallbackText, Does.Contain("Amazonia"), "the pushed payload must carry the structured system body");
    }

    [Test]
    public async Task Publish_WithNoDedupeKey_AllowsRepeats()
    {
        await ChatDomainIndexes.EnsureAllAsync(MongoClient);
        var channel = await NewMatchChannel();

        await _publisher.Publish(channel, Intro(), dedupeKey: null);
        await _publisher.Publish(channel, Intro(), dedupeKey: null);

        var all = await _messageRepository.LoadForModerator(channel.Id);
        Assert.That(all, Has.Count.EqualTo(2),
            "dedupe is opt-in — a caller that wants repeated system messages passes no key");
    }

    [Test]
    public async Task Publish_WithEmptyDedupeKey_AllowsRepeats()
    {
        await ChatDomainIndexes.EnsureAllAsync(MongoClient);
        var channel = await NewMatchChannel();

        await _publisher.Publish(channel, Intro(), dedupeKey: "");
        await _publisher.Publish(channel, Intro(), dedupeKey: "");

        var all = await _messageRepository.LoadForModerator(channel.Id);
        Assert.That(all, Has.Count.EqualTo(2),
            "an empty string is normalized to \"no dedupe key\", the same as null — it must never be written to DedupeKey or dedupe against itself");
        Assert.That(all, Has.All.Matches<ChannelMessage>(m => m.DedupeKey == null),
            "an empty-string dedupeKey argument must never reach ChannelMessage.DedupeKey as a stored empty string");
    }

    [Test]
    public async Task Publish_WithNullChannel_ReturnsNotFound()
    {
        var result = await _publisher.Publish(null, Intro(), dedupeKey: null);

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.NotFound), "a null channel must be reported as NotFound, not throw or silently no-op");
        Assert.That(result.MessageId, Is.Null, "a NotFound result must carry no message id");
        Assert.That(result.Seq, Is.EqualTo(0), "a NotFound result must carry no seq");
    }

    // The controller validates key/fallbackText before ever calling Publish, but this class is
    // DI-registered and documents itself as "the ONE server-authored message insert path" — an
    // in-process API future code is invited to call. These pin that a malformed body is rejected UP
    // FRONT, before a seq is burned and a half-formed row fans out to every member.

    [Test]
    public async Task Publish_WithNullBody_ReturnsTooLong_AndWritesNothing()
    {
        var channel = await NewMatchChannel();

        var result = await _publisher.Publish(channel, null, dedupeKey: null);

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.TooLong), "a null body must be a typed reject — unguarded it is an NRE raised AFTER the insert and the fan-out push");
        var all = await _messageRepository.LoadForModerator(channel.Id);
        Assert.That(all, Is.Empty, "a rejected publish must not persist a row with a null SystemMessage");
        var reloaded = await _channelRepository.Load(channel.Id);
        Assert.That(reloaded.LastSeq, Is.EqualTo(0), "the guard must run BEFORE AllocateSeq — a rejected publish must not burn a seq");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public async Task Publish_WithBlankFallbackText_ReturnsTooLong_AndWritesNothing(string fallbackText)
    {
        var channel = await NewMatchChannel();
        var body = Intro();
        body.FallbackText = fallbackText;

        var result = await _publisher.Publish(channel, body, dedupeKey: null);

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.TooLong),
            "SystemMessageBody documents FallbackText as required, and it is the moderator's ONLY readable rendering — a blank one must never reach the insert");
        var all = await _messageRepository.LoadForModerator(channel.Id);
        Assert.That(all, Is.Empty, "a rejected publish must not persist an unreadable system message");
    }

    [Test]
    public async Task Publish_ChannelVanishesBeforeAllocateSeq_ReturnsNotFound_NotAnException()
    {
        var channel = await NewMatchChannel();
        // The TOCTOU the guard exists for: mm's DELETE /internal/channels/{ref} (or the TTL) removes the
        // shell after the caller resolved it. Deleting the doc while holding the already-loaded instance
        // reproduces exactly that window — AllocateSeq's $inc then matches no document and throws.
        await _channelRepository.Delete(channel.Id);

        var result = await _publisher.Publish(channel, Intro(), dedupeKey: null);

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.NotFound),
            "a vanished channel must surface as the same typed NotFound a lookup miss produces, not escape as a body-free 500 mm would retry forever");
        Assert.That(result.MessageId, Is.Null, "a NotFound result must carry no message id");
        Assert.That(result.Seq, Is.EqualTo(0), "a NotFound result must carry no seq");
    }

    // Forces the first `missCount` LoadByDedupeKey calls to return null regardless of what is actually
    // stored, letting a Publish call proceed past a lookup that would otherwise resolve immediately —
    // reproducing races and lookup failures without real concurrency or timing. Calls beyond `missCount`
    // delegate to the real lookup. Configurations used by this file:
    //   missCount: 1 — only the publisher's pre-check misses; the catch's own (real, call #2) lookup
    //                  succeeds and finds the winner. Reproduces a genuine concurrent race that
    //                  resolves to Ok.
    //   missCount: 2 — the pre-check AND the catch's post-collision lookup both miss, so the catch
    //                  cannot find a winner. Reproduces the "indexed row is unexpectedly absent" case
    //                  and drives the `throw;` fallback.
    private sealed class MissingDedupeLookupMessageRepository(MongoClient mongoClient, int missCount) : MessageRepository(mongoClient)
    {
        private int _callCount;

        public override Task<ChannelMessage> LoadByDedupeKey(string channelId, string dedupeKey) =>
            Interlocked.Increment(ref _callCount) <= missCount
                ? Task.FromResult<ChannelMessage>(null)
                : base.LoadByDedupeKey(channelId, dedupeKey);
    }

    [Test]
    public async Task Publish_DuplicateKeyRace_ReturnsWinnersMessage_NotAnException()
    {
        await ChatDomainIndexes.EnsureAllAsync(MongoClient);
        var channel = await NewMatchChannel();

        // Seed the "winner" via a normal publish — this is the row the racing call below will collide with.
        var winner = await _publisher.Publish(channel, Intro(), dedupeKey: "match_intro");

        var racingRepository = new MissingDedupeLookupMessageRepository(MongoClient, missCount: 1);
        var racingPublisher = new SystemMessagePublisher(racingRepository, _channelRepository, _fanOutEngine, _time);

        // racingRepository's pre-check (call #1) is forced to miss despite the winner already existing,
        // so this proceeds to AllocateSeq + Insert, which collides on ux_channelId_dedupeKey and must be
        // resolved by the duplicate-key catch — including its own (real, call #2) LoadByDedupeKey lookup.
        var result = await racingPublisher.Publish(channel, Intro(), dedupeKey: "match_intro");

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok), "a duplicate-key race must resolve to Ok, not surface the write exception");
        Assert.That(result.MessageId, Is.EqualTo(winner.MessageId), "the race must return the winner's message, not mint a second one");
        Assert.That(result.Seq, Is.EqualTo(winner.Seq), "the race must return the winner's seq, not the seq this call itself allocated and orphaned");

        var all = await _messageRepository.LoadForModerator(channel.Id);
        Assert.That(all, Has.Count.EqualTo(1), "the loser's insert must never have landed a second row for the same key");

        // If the pre-check ever stopped missing (an off-by-one, or a future refactor that adds a real
        // lookup before it), this call would resolve entirely in the pre-check and never reach
        // AllocateSeq — degrading this test into a duplicate of Publish_IsIdempotentOnDedupeKey while
        // still appearing to cover the catch clause. LastSeq only advances past the winner's own
        // allocation (1) if the racing call actually burned a second seq, so 2 is proof this call went
        // through the insert and the duplicate-key catch, not the pre-check.
        var reloaded = await _channelRepository.Load(channel.Id);
        Assert.That(reloaded.LastSeq, Is.EqualTo(2),
            "the losing call must have allocated and orphaned a seq — proof it went through the insert and the duplicate-key catch, not the pre-check");
    }

    [Test]
    public async Task Publish_DuplicateKeyRace_WinnerLookupAlsoMisses_RethrowsWriteException()
    {
        await ChatDomainIndexes.EnsureAllAsync(MongoClient);
        var channel = await NewMatchChannel();

        // Seed the row this call will collide with.
        await _publisher.Publish(channel, Intro(), dedupeKey: "match_intro");

        var racingRepository = new MissingDedupeLookupMessageRepository(MongoClient, missCount: 2);
        var racingPublisher = new SystemMessagePublisher(racingRepository, _channelRepository, _fanOutEngine, _time);

        // Both the pre-check (call #1) and the catch's post-collision lookup (call #2) are forced to
        // miss, so the catch cannot resolve a winner and must fall through to `throw;`. The catch
        // filters only on Category == DuplicateKey, not on which index collided, so this is the
        // property standing between an unrelated unique-constraint failure and a bogus Ok result.
        Assert.ThrowsAsync<MongoWriteException>(
            () => racingPublisher.Publish(channel, Intro(), dedupeKey: "match_intro"),
            "an unexplained duplicate-key failure whose winner cannot be found must surface, not be swallowed into a fabricated Ok");
    }
}
