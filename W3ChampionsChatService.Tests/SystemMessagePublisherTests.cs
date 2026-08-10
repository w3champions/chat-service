using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
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

        var result = await _publisher.Publish(channel, Intro(), dedupeKey: "match_intro");

        Assert.That(result.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(result.Seq, Is.EqualTo(1), "the first message in a fresh channel gets seq 1");

        var stored = await _messageRepository.Load(result.MessageId);
        Assert.That(stored.Kind, Is.EqualTo(MessageKind.System), "publish must write a System-kind message");
        Assert.That(stored.Sender, Is.Null, "system messages carry no sender snapshot");
        Assert.That(stored.SystemMessage.Key, Is.EqualTo("match_intro"), "the structured body must round-trip through the insert");
        Assert.That(stored.SentAt, Is.EqualTo(Now), "SentAt must be the publisher's injected clock, not wall time");
        Assert.That(stored.ExpiresAt, Is.Not.Null, "system messages follow the normal 30d message TTL");

        var reloaded = await _channelRepository.Load(channel.Id);
        Assert.That(reloaded.LastSeq, Is.EqualTo(1), "AllocateSeq ran — paging and unread both key off it");
        Assert.That(reloaded.LastMessageAt, Is.EqualTo(Now), "AllocateSeq must stamp LastMessageAt on every insert path");
    }

    [Test]
    public async Task Publish_NeverTouchesChannelExpiresAt()
    {
        var channel = await NewMatchChannel();
        var expiryBefore = channel.ExpiresAt;

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

        var first = await _publisher.Publish(channel, Intro(), dedupeKey: "match_intro");
        var second = await _publisher.Publish(channel, Intro(), dedupeKey: "match_intro");

        Assert.That(second.Code, Is.EqualTo(ChatResultCode.Ok));
        Assert.That(second.MessageId, Is.EqualTo(first.MessageId), "a retry returns the original message");
        Assert.That(second.Seq, Is.EqualTo(first.Seq), "a retry must not allocate or return a new seq");

        var all = await _messageRepository.LoadForModerator(channel.Id);
        Assert.That(all, Has.Count.EqualTo(1), "mm retries on timeout — the intro must never double-post");
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
        var dto = _harness.PayloadFor("conn-alice", ChatEvents.MessageReceived) as MessageDto;
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
}
