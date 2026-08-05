using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Driver;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.FanOut;
using W3ChampionsChatService.Internal;
using W3ChampionsChatService.Memberships;
using W3ChampionsChatService.Messages;
using W3ChampionsChatService.Protocol;
using W3ChampionsChatService.Sessions;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// C7 Task 6 — the domain core: <see cref="MatchChannelService.CreateOrGet"/> (idempotent match-channel
/// find-or-create + name backfill) and the shared one-match-channel-per-user invariant (best-effort ordered
/// swap + focus-hinted pushes). Full-stack: real <see cref="ChannelRepository"/> / <see cref="MembershipRepository"/>
/// on the ephemeral <see cref="IntegrationTestBase.MongoClient"/>, a real <see cref="FanOutEngine"/> over the
/// shared registries + a <see cref="HubPushCaptureHarness"/> capturing every push, and a deterministic
/// <see cref="FakeTimeProvider"/>. Mirrors the <see cref="DmGroupIntegrationTests"/> shared-singleton idiom,
/// narrowed to the match-channel surface. NUnit constraint style.
/// </summary>
public class MatchChannelServiceTests : IntegrationTestBase
{
    private static readonly DateTime T0 = new(2026, 7, 5, 12, 0, 0, DateTimeKind.Utc);

    private FakeTimeProvider _time;
    private HubPushCaptureHarness _harness;
    private SessionRegistry _sessionRegistry;
    private FocusRegistry _focusRegistry;
    private OnlineMemberRegistry _onlineMemberRegistry;
    private ActivityCoalescer _activityCoalescer;
    private ViewersAccumulator _viewersAccumulator;
    private FanOutEngine _fanOutEngine;
    private ChannelRepository _channelRepository;
    private MembershipRepository _membershipRepository;
    private MessageRepository _messageRepository;
    private MatchChannelService _service;

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    [SetUp]
    public void SetupBeforeEach()
    {
        _time = new FakeTimeProvider(new DateTimeOffset(T0, TimeSpan.Zero));
        _harness = new HubPushCaptureHarness();
        _sessionRegistry = new SessionRegistry();
        _focusRegistry = new FocusRegistry();
        _onlineMemberRegistry = new OnlineMemberRegistry();
        _activityCoalescer = new ActivityCoalescer(_harness.HubContext, _onlineMemberRegistry);
        _viewersAccumulator = new ViewersAccumulator(_harness.HubContext, _focusRegistry);
        _fanOutEngine = new FanOutEngine(
            _harness.HubContext,
            _focusRegistry,
            _onlineMemberRegistry,
            _activityCoalescer,
            _sessionRegistry,
            new PresenceInterestRegistry(),
            _viewersAccumulator,
            _time);
        _channelRepository = new ChannelRepository(MongoClient);
        _membershipRepository = new MembershipRepository(MongoClient, _channelRepository);
        _messageRepository = new MessageRepository(MongoClient);
        _service = new MatchChannelService(_channelRepository, _membershipRepository, _messageRepository, _fanOutEngine, _time);
    }

    // Registers a live session for battleTag under connectionId — the RegisterOnline idiom
    // ChannelEventEmitterTests/DmGroupIntegrationTests use to make a user "online".
    private void RegisterOnline(string connectionId, string battleTag) =>
        _sessionRegistry.Register(
            connectionId,
            new W3CUserAuthentication { BattleTag = battleTag, Name = battleTag.Split('#')[0] },
            null);

    private static IReadOnlyList<string> Members(params string[] battleTags) => battleTags;

    // Mirrors MessageRepositoryTests.NewMessage — a minimal valid ChannelMessage for seeding.
    private static ChannelMessage NewMessage(string channelId, long seq, string sender = "Peter#123") => new()
    {
        ChannelId = channelId,
        Seq = seq,
        Sender = new MessageSender { BattleTag = sender, Name = sender.Split('#')[0] },
        Content = "hello",
        SentAt = DateTime.UtcNow,
    };

    // ============================================================================================
    // CreateOrGet — new channel shape (acceptance 8)
    // ============================================================================================

    [Test]
    public async Task CreateOrGet_NewChannel_SetsSystemMatchKind_RefAndExpiry24h()
    {
        var channel = await _service.CreateOrGet("match-1", "Match 1", Members(), focus: false);

        Assert.That(channel.Type, Is.EqualTo(ChannelType.System));
        Assert.That(channel.SystemKind, Is.EqualTo(SystemChannelKind.Match));
        Assert.That(channel.SystemRef, Is.EqualTo("match-1"));
        Assert.That(channel.Name, Is.EqualTo("Match 1"));
        Assert.That(channel.ExpiresAt, Is.Not.Null);
        Assert.That((channel.ExpiresAt.Value - Now.AddHours(24)).Duration(), Is.LessThan(TimeSpan.FromSeconds(1)),
            "a new match channel is creation-anchored to a 24h expiry (RetentionPeriods.MatchChannel)");

        var loaded = await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-1");
        Assert.That(loaded, Is.Not.Null, "the channel is durably persisted");
        Assert.That(loaded.Id, Is.EqualTo(channel.Id));
    }

    // ============================================================================================
    // CreateOrGet — duplicate is idempotent (acceptance 2)
    // ============================================================================================

    [Test]
    public async Task CreateOrGet_Duplicate_ReturnsExisting_NoExpiryReset_NoDuplicateMemberships_NoDuplicatePushes()
    {
        const string bt = "Alice#1";
        RegisterOnline("conn-alice", bt);

        var first = await _service.CreateOrGet("match-1", "Match 1", Members(bt), focus: true);
        var firstExpiry = first.ExpiresAt;

        // The clock moves between the two POSTs — a re-get must NOT re-anchor the creation-time expiry.
        _time.Advance(TimeSpan.FromHours(1));

        var second = await _service.CreateOrGet("match-1", "Match 1", Members(bt), focus: true);

        Assert.That(second.Id, Is.EqualTo(first.Id), "the duplicate create returns the SAME channel");
        Assert.That(second.ExpiresAt, Is.EqualTo(firstExpiry), "the 24h expiry is NOT reset on re-get (creation-anchored, $setOnInsert)");

        var memberships = await _membershipRepository.LoadForUser(bt);
        Assert.That(memberships.Count(m => m.ChannelId == first.Id), Is.EqualTo(1),
            "exactly one membership on the match channel — the re-add does not duplicate it");
        Assert.That(_harness.SignalCount("conn-alice", ChatEvents.ChannelAdded), Is.EqualTo(1),
            "ChannelAdded fires once — the idempotent re-add does not re-push");
    }

    // ============================================================================================
    // CreateOrGet — online members receive ChannelAdded, focus honored true AND false (acceptance 3a)
    // ============================================================================================

    [TestCase(true)]
    [TestCase(false)]
    public async Task CreateOrGet_OnlineMembers_ReceiveChannelAdded_WithFocusHonored(bool focus)
    {
        const string bt = "Alice#1";
        RegisterOnline("conn-alice", bt);

        var channel = await _service.CreateOrGet("match-1", "Match 1", Members(bt), focus);

        Assert.That(_harness.SignalCount("conn-alice", ChatEvents.ChannelAdded), Is.EqualTo(1));
        var dto = _harness.PayloadFor("conn-alice", ChatEvents.ChannelAdded) as ChannelAddedDto;
        Assert.That(dto, Is.Not.Null, "the online member's live connection receives a ChannelAddedDto");
        Assert.That(dto.Channel.Id, Is.EqualTo(channel.Id));
        Assert.That(dto.Focus, Is.EqualTo(focus), "the focus directive is honored verbatim");
        Assert.That(dto.Membership.NotificationLevel, Is.EqualTo(NotificationLevel.All),
            "match-channel members default to NotificationLevel.All (spec §7)");
        Assert.That(dto.Membership.Role, Is.EqualTo(MembershipRole.Member));
    }

    // ============================================================================================
    // CreateOrGet — offline members get a membership doc only, zero signals (acceptance 3b)
    // ============================================================================================

    [Test]
    public async Task CreateOrGet_OfflineMembers_GetMembershipDocOnly_NoPush()
    {
        const string bt = "Offline#1";
        // Deliberately NOT registered online — no live connection.

        var channel = await _service.CreateOrGet("match-1", "Match 1", Members(bt), focus: true);

        Assert.That(_harness.AllSignals, Is.Empty, "an offline member receives ZERO live signals");

        var membership = await _membershipRepository.Load(channel.Id, bt);
        Assert.That(membership, Is.Not.Null, "the membership doc is still durably persisted for an offline member");
        Assert.That(membership.NotificationLevel, Is.EqualTo(NotificationLevel.All));
        Assert.That(membership.Role, Is.EqualTo(MembershipRole.Member));
        Assert.That((membership.JoinedAt - Now).Duration(), Is.LessThan(TimeSpan.FromSeconds(1)), "JoinedAt is stamped to now");
    }

    // ============================================================================================
    // Swap — add-to-second-match removes first, ChannelRemoved(A) STRICTLY before ChannelAdded(B),
    // exactly one System+Match membership remains (acceptance 5)
    // ============================================================================================

    [Test]
    public async Task AddToSecondMatchChannel_RemovesFirstMembership_PushesRemovedThenAdded_ExactlyOneMatchMembershipRemains()
    {
        const string bt = "Alice#1";
        RegisterOnline("conn-alice", bt);

        var matchA = await _service.CreateOrGet("match-A", "Match A", Members(bt), focus: true);
        var matchB = await _service.CreateOrGet("match-B", "Match B", Members(bt), focus: true);

        // The membership swapped: A gone, B present.
        Assert.That(await _membershipRepository.Load(matchA.Id, bt), Is.Null, "the stale match-A membership is removed");
        Assert.That(await _membershipRepository.Load(matchB.Id, bt), Is.Not.Null, "the match-B membership is present");

        // Exactly one System+Match membership remains.
        var memberships = await _membershipRepository.LoadForUser(bt);
        var channels = await _channelRepository.LoadByIds(memberships.Select(m => m.ChannelId));
        var matchMembershipCount = channels.Count(c => c.Type == ChannelType.System && c.SystemKind == SystemChannelKind.Match);
        Assert.That(matchMembershipCount, Is.EqualTo(1), "exactly one System+Match membership remains after the swap");

        // ORDER: ChannelRemoved(A) is emitted STRICTLY BEFORE ChannelAdded(B) on the user's connection.
        var signals = _harness.AllSignals.Where(s => s.ConnectionId == "conn-alice").ToList();
        var removedAIndex = signals.FindIndex(s =>
            s.Method == ChatEvents.ChannelRemoved && ((ChannelRemovedDto)s.Payload).ChannelId == matchA.Id);
        var addedBIndex = signals.FindIndex(s =>
            s.Method == ChatEvents.ChannelAdded && ((ChannelAddedDto)s.Payload).Channel.Id == matchB.Id);
        Assert.That(removedAIndex, Is.GreaterThanOrEqualTo(0), "ChannelRemoved(A) was emitted");
        Assert.That(addedBIndex, Is.GreaterThanOrEqualTo(0), "ChannelAdded(B) was emitted");
        Assert.That(removedAIndex, Is.LessThan(addedBIndex),
            "ChannelRemoved(A) is emitted STRICTLY BEFORE ChannelAdded(B) — a user moving A→B never transiently sees both");
    }

    // ============================================================================================
    // Swap — leaves non-match (Public / GroupDm) memberships untouched
    // ============================================================================================

    [Test]
    public async Task Swap_LeavesNonMatchMembershipsUntouched()
    {
        const string bt = "Alice#1";
        RegisterOnline("conn-alice", bt);

        // The user is also in a Public channel and a GroupDm.
        var publicChannel = new ChatChannel { Type = ChannelType.Public, Name = "W3C Lounge", NormalizedName = ChannelNames.Normalize("W3C Lounge") };
        await _channelRepository.Insert(publicChannel);
        await _membershipRepository.Insert(new ChannelMembership { ChannelId = publicChannel.Id, BattleTag = bt, JoinedAt = Now });

        var group = new ChatChannel { Type = ChannelType.GroupDm, Name = "squad", LastMessageAt = Now, ExpiresAt = Now.AddDays(365) };
        await _channelRepository.Insert(group);
        await _membershipRepository.Insert(new ChannelMembership { ChannelId = group.Id, BattleTag = bt, JoinedAt = Now });

        var matchA = await _service.CreateOrGet("match-A", "Match A", Members(bt), focus: false);
        var matchB = await _service.CreateOrGet("match-B", "Match B", Members(bt), focus: false);

        Assert.That(await _membershipRepository.Load(publicChannel.Id, bt), Is.Not.Null, "the Public membership is untouched by the match swap");
        Assert.That(await _membershipRepository.Load(group.Id, bt), Is.Not.Null, "the GroupDm membership is untouched by the match swap");
        Assert.That(await _membershipRepository.Load(matchA.Id, bt), Is.Null, "only the stale match membership is removed");
        Assert.That(await _membershipRepository.Load(matchB.Id, bt), Is.Not.Null, "the new match membership is present");
    }

    // ============================================================================================
    // Duplicate POST — late-repair: additional members are treated as adds, only the NEW ones pushed (§3.3)
    // ============================================================================================

    [Test]
    public async Task DuplicatePost_TreatsMembersAsAdds_OnlyNewMembersPushed()
    {
        const string alice = "Alice#1";
        const string bob = "Bob#2";
        RegisterOnline("conn-alice", alice);
        RegisterOnline("conn-bob", bob);

        // First POST: only Alice.
        var channel = await _service.CreateOrGet("match-1", "Match 1", Members(alice), focus: false);
        Assert.That(_harness.SignalCount("conn-alice", ChatEvents.ChannelAdded), Is.EqualTo(1));
        Assert.That(_harness.SignalCount("conn-bob", ChatEvents.ChannelAdded), Is.EqualTo(0));

        // Duplicate POST (late repair) now lists Alice AND Bob — Alice is an idempotent no-op, Bob is added.
        await _service.CreateOrGet("match-1", "Match 1", Members(alice, bob), focus: false);

        Assert.That(_harness.SignalCount("conn-alice", ChatEvents.ChannelAdded), Is.EqualTo(1),
            "Alice is already a member — she is NOT re-pushed on the duplicate POST");
        Assert.That(_harness.SignalCount("conn-bob", ChatEvents.ChannelAdded), Is.EqualTo(1),
            "only the newly-listed Bob is pushed");
        Assert.That(await _membershipRepository.Load(channel.Id, bob), Is.Not.Null, "Bob's membership is persisted by the duplicate POST");
    }

    // ============================================================================================
    // Duplicate POST — a different name backfills the display name (§3.3)
    // ============================================================================================

    [Test]
    public async Task DuplicatePost_WithDifferentName_BackfillsName()
    {
        var first = await _service.CreateOrGet("match-1", "Placeholder", Members(), focus: false);
        Assert.That(first.Name, Is.EqualTo("Placeholder"));
        var firstExpiry = first.ExpiresAt;

        var second = await _service.CreateOrGet("match-1", "Real Match Name", Members(), focus: false);

        Assert.That(second.Id, Is.EqualTo(first.Id), "the same channel (same ref)");
        Assert.That(second.Name, Is.EqualTo("Real Match Name"), "the placeholder display name is backfilled to the real name");
        Assert.That(second.ExpiresAt, Is.EqualTo(firstExpiry), "the name backfill does NOT touch the 24h creation-anchored expiry");

        var reloaded = await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-1");
        Assert.That(reloaded.Name, Is.EqualTo("Real Match Name"), "the backfilled name is durably persisted");
    }

    // ============================================================================================
    // C7 Task 8 — DeleteChannel: hard-delete teardown (messages + memberships + channel), including
    // physically-present soft-deleted/shadow rows; another channel's data is untouched (acceptance 6a)
    // ============================================================================================

    [Test]
    public async Task Delete_RemovesChannelMembershipsAndMessages()
    {
        const string alice = "Alice#1";
        const string bob = "Bob#2";

        var channel = await _service.CreateOrGet("match-1", "Match 1", Members(alice, bob), focus: false);

        // Seed messages INCLUDING one soft-deleted + one shadow row — both are still PHYSICAL rows
        // (soft-delete is TTL-only; shadow is a visibility flag), so a hard teardown purge must remove
        // them just like any other message in the channel.
        var normal = NewMessage(channel.Id, 1, alice);
        var softDeleted = NewMessage(channel.Id, 2, alice);
        var shadow = NewMessage(channel.Id, 3, bob);
        shadow.Shadow = true;
        await _messageRepository.Insert(normal);
        await _messageRepository.Insert(softDeleted);
        await _messageRepository.Insert(shadow);
        await _messageRepository.MarkDeleted(softDeleted.Id, "Mod#1", Now);

        // Another channel's messages/memberships/channel doc must be completely untouched.
        var otherChannel = await _service.CreateOrGet("match-2", "Match 2", Members(alice), focus: false);
        var otherMessage = NewMessage(otherChannel.Id, 1, alice);
        await _messageRepository.Insert(otherMessage);

        await _service.DeleteChannel("match-1");

        Assert.That(await _messageRepository.Load(normal.Id), Is.Null, "the normal message is hard-purged");
        Assert.That(await _messageRepository.Load(softDeleted.Id), Is.Null,
            "the SOFT-DELETED message is still a physical row pending TTL and must be hard-purged too");
        Assert.That(await _messageRepository.Load(shadow.Id), Is.Null,
            "the SHADOW message is still a physical row and must be hard-purged too");
        Assert.That(await _membershipRepository.LoadForChannel(channel.Id), Is.Empty,
            "every membership of the deleted channel is removed");
        Assert.That(await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-1"), Is.Null,
            "the channel doc itself is removed");

        Assert.That(await _messageRepository.Load(otherMessage.Id), Is.Not.Null, "a different channel's messages are untouched");
        Assert.That(await _membershipRepository.LoadForChannel(otherChannel.Id), Has.Count.EqualTo(1),
            "a different channel's memberships are untouched");
        Assert.That(await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-2"), Is.Not.Null,
            "a different channel is untouched");
    }

    // ============================================================================================
    // DeleteChannel — online members receive ChannelRemoved (acceptance 6b)
    // ============================================================================================

    [Test]
    public async Task Delete_OnlineMembers_ReceiveChannelRemoved()
    {
        const string alice = "Alice#1";
        const string bob = "Bob#2";
        RegisterOnline("conn-alice", alice);
        RegisterOnline("conn-bob", bob);

        var channel = await _service.CreateOrGet("match-1", "Match 1", Members(alice, bob), focus: false);

        await _service.DeleteChannel("match-1");

        Assert.That(_harness.SignalCount("conn-alice", ChatEvents.ChannelRemoved), Is.EqualTo(1),
            "the first online member receives ChannelRemoved");
        Assert.That(_harness.SignalCount("conn-bob", ChatEvents.ChannelRemoved), Is.EqualTo(1),
            "the second online member receives ChannelRemoved");

        var dtoAlice = _harness.PayloadFor("conn-alice", ChatEvents.ChannelRemoved) as ChannelRemovedDto;
        Assert.That(dtoAlice, Is.Not.Null);
        Assert.That(dtoAlice.ChannelId, Is.EqualTo(channel.Id));
    }

    // ============================================================================================
    // DeleteChannel — delete-before-create: no channel for the ref is a silent no-op, zero pushes
    // ============================================================================================

    [Test]
    public async Task Delete_UnknownRef_NoOp_NoPushes()
    {
        const string alice = "Alice#1";
        RegisterOnline("conn-alice", alice);

        await _service.DeleteChannel("never-existed");

        Assert.That(_harness.AllSignals, Is.Empty,
            "no channel exists for the ref — DELETE arriving before the create is a silent no-op, never a hard 404");
    }

    // ============================================================================================
    // DeleteChannel — offline members: no push, no error, teardown still completes
    // ============================================================================================

    [Test]
    public async Task Delete_OfflineMembers_NoPush_NoError()
    {
        const string offline = "Offline#1";
        // Deliberately NOT registered online — no live connection.

        var channel = await _service.CreateOrGet("match-1", "Match 1", Members(offline), focus: false);

        Assert.DoesNotThrowAsync(async () => await _service.DeleteChannel("match-1"));

        Assert.That(_harness.AllSignals, Is.Empty, "an offline member receives zero live signals, and no error is thrown");
        Assert.That(await _membershipRepository.LoadForChannel(channel.Id), Is.Empty, "the teardown still completes for an offline-only channel");
    }

    // ============================================================================================
    // 2026-08-05 reconciliation — ApplyRosterAssertion: diff + idempotency (plan D3, D4, D10)
    // ============================================================================================

    [Test]
    public async Task Assert_AddsMissingMembers_PushesChannelAdded_NeverFocused()
    {
        const string alice = "Alice#1";
        RegisterOnline("conn-alice", alice);
        var channel = await _service.CreateOrGet("match-1", "Match 1", Members(), focus: false);

        var outcome = await _service.ApplyRosterAssertion("match-1", "e1", 1, Members(alice), name: null, detached: false);

        // 2026-08-05 fix wave (final review M2): the outcome the controller now logs instead of an
        // unconditional "succeeded" line.
        Assert.That(outcome, Is.EqualTo(RosterAssertionOutcome.Applied));
        Assert.That(await _membershipRepository.Load(channel.Id, alice), Is.Not.Null, "the missing member is added");
        Assert.That(_harness.SignalCount("conn-alice", ChatEvents.ChannelAdded), Is.EqualTo(1));
        var dto = _harness.PayloadFor("conn-alice", ChatEvents.ChannelAdded) as ChannelAddedDto;
        Assert.That(dto, Is.Not.Null);
        Assert.That(dto.Focus, Is.False, "the roster assertion contract carries no focus field — adds are always focus:false");
    }

    [Test]
    public async Task Assert_RemovesExtraMembers_DeletesRow_PushesChannelRemoved_AndUnfocuses()
    {
        const string alice = "Alice#1";
        RegisterOnline("conn-alice", alice);
        var channel = await _service.CreateOrGet("match-1", "Match 1", Members(alice), focus: true);
        _focusRegistry.Focus("conn-alice", channel.Id, alice);
        Assert.That(_focusRegistry.GetFocusedChannels("conn-alice"), Does.Contain(channel.Id), "precondition: focused");

        await _service.ApplyRosterAssertion("match-1", "e1", 1, Members(), name: null, detached: false);

        Assert.That(await _membershipRepository.Load(channel.Id, alice), Is.Null, "the extra member is removed");
        Assert.That(_harness.SignalCount("conn-alice", ChatEvents.ChannelRemoved), Is.EqualTo(1));
        var dto = _harness.PayloadFor("conn-alice", ChatEvents.ChannelRemoved) as ChannelRemovedDto;
        Assert.That(dto, Is.Not.Null);
        Assert.That(dto.ChannelId, Is.EqualTo(channel.Id));
        Assert.That(_focusRegistry.GetFocusedChannels("conn-alice"), Does.Not.Contain(channel.Id),
            "the removed member's connection is force-unfocused");
    }

    [Test]
    public async Task Assert_ReAssertingIdenticalSet_IsIdempotent_ZeroPushes()
    {
        const string alice = "Alice#1";
        const string bob = "Bob#2";
        RegisterOnline("conn-alice", alice);
        RegisterOnline("conn-bob", bob);
        await _service.CreateOrGet("match-1", "Match 1", Members(), focus: false);
        await _service.ApplyRosterAssertion("match-1", "e1", 1, Members(alice, bob), name: null, detached: false);
        var pushCountAfterFirst = _harness.AllSignals.Count;

        await _service.ApplyRosterAssertion("match-1", "e1", 2, Members(alice, bob), name: null, detached: false);

        Assert.That(_harness.AllSignals.Count, Is.EqualTo(pushCountAfterFirst), "an identical re-assertion pushes nothing new");
        var channel = await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-1");
        Assert.That(await _membershipRepository.LoadForChannel(channel.Id), Has.Count.EqualTo(2), "membership rows are unchanged");
    }

    [Test]
    public async Task Assert_MixedAddAndRemove_ConvergesInOneCall()
    {
        const string alice = "Alice#1";
        const string bob = "Bob#2";
        RegisterOnline("conn-alice", alice);
        RegisterOnline("conn-bob", bob);
        var channel = await _service.CreateOrGet("match-1", "Match 1", Members(alice), focus: false);

        await _service.ApplyRosterAssertion("match-1", "e1", 1, Members(bob), name: null, detached: false);

        Assert.That(await _membershipRepository.Load(channel.Id, alice), Is.Null, "alice is removed");
        Assert.That(await _membershipRepository.Load(channel.Id, bob), Is.Not.Null, "bob is added");
        Assert.That(_harness.SignalCount("conn-alice", ChatEvents.ChannelRemoved), Is.EqualTo(1));
        Assert.That(_harness.SignalCount("conn-bob", ChatEvents.ChannelAdded), Is.EqualTo(1));
    }

    [Test]
    public async Task Assert_EmptyMemberSet_RemovesEveryMember()
    {
        const string alice = "Alice#1";
        const string bob = "Bob#2";
        var channel = await _service.CreateOrGet("match-1", "Match 1", Members(alice, bob), focus: false);

        await _service.ApplyRosterAssertion("match-1", "e1", 1, Members(), name: null, detached: false);

        Assert.That(await _membershipRepository.LoadForChannel(channel.Id), Is.Empty,
            "D7: an empty member set is meaningful and removes every member — it is NOT a no-op");
    }

    [Test]
    public async Task Assert_CaseOnlyDifference_DoesNotChurnMembership()
    {
        RegisterOnline("conn-alice", "Alice#1");
        await _service.CreateOrGet("match-1", "Match 1", Members(), focus: false);
        // Seeded so the stored row is the lowercased key "alice#1"...
        await _service.ApplyRosterAssertion("match-1", "e1", 1, Members("alice#1"), name: null, detached: false);
        var pushCountAfterFirst = _harness.AllSignals.Count;

        // ...and re-asserted with mm's JWT casing. THIS is the direction that occurs in production.
        await _service.ApplyRosterAssertion("match-1", "e1", 2, Members("Alice#1"), name: null, detached: false);

        Assert.That(_harness.AllSignals.Count, Is.EqualTo(pushCountAfterFirst),
            "a case-only difference between stored (lowercased) and asserted (JWT-cased) battleTags must not churn membership");
        var channel = await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-1");
        Assert.That(await _membershipRepository.LoadForChannel(channel.Id), Has.Count.EqualTo(1),
            "exactly one membership row — no delete+re-add");
    }

    [Test]
    public async Task Assert_HonorsOneMatchChannelInvariant_RemovedBeforeAdded()
    {
        const string alice = "Alice#1";
        RegisterOnline("conn-alice", alice);
        var matchA = await _service.CreateOrGet("match-A", "Match A", Members(alice), focus: true);
        await _service.CreateOrGet("match-B", "Match B", Members(), focus: false);

        await _service.ApplyRosterAssertion("match-B", "e1", 1, Members(alice), name: null, detached: false);

        var matchB = await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-B");
        Assert.That(await _membershipRepository.Load(matchA.Id, alice), Is.Null, "the stale match-A membership is evicted");
        Assert.That(await _membershipRepository.Load(matchB.Id, alice), Is.Not.Null);

        var signals = _harness.AllSignals.Where(s => s.ConnectionId == "conn-alice").ToList();
        var removedAIndex = signals.FindIndex(s =>
            s.Method == ChatEvents.ChannelRemoved && ((ChannelRemovedDto)s.Payload).ChannelId == matchA.Id);
        var addedBIndex = signals.FindIndex(s =>
            s.Method == ChatEvents.ChannelAdded && ((ChannelAddedDto)s.Payload).Channel.Id == matchB.Id);
        Assert.That(removedAIndex, Is.GreaterThanOrEqualTo(0), "ChannelRemoved(A) was emitted");
        Assert.That(addedBIndex, Is.GreaterThanOrEqualTo(0), "ChannelAdded(B) was emitted");
        Assert.That(removedAIndex, Is.LessThan(addedBIndex),
            "ChannelRemoved(A) is emitted STRICTLY BEFORE ChannelAdded(B) on the assertion path too");
    }

    [Test]
    public async Task Assert_BeforeCreate_CreatesShell_WithProvidedName_And24hExpiry()
    {
        const string alice = "Alice#1";
        RegisterOnline("conn-alice", alice);

        await _service.ApplyRosterAssertion("match-1", "e1", 1, Members(alice), name: "My Lobby", detached: false);

        var shell = await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-1");
        Assert.That(shell, Is.Not.Null, "the shell is created on-demand rather than a hard 404");
        Assert.That(shell.Name, Is.EqualTo("My Lobby"));
        Assert.That(shell.ExpiresAt, Is.Not.Null);
        Assert.That((shell.ExpiresAt.Value - Now.AddHours(24)).Duration(), Is.LessThan(TimeSpan.FromSeconds(1)),
            "the shell's 24h expiry is anchored to its OWN creation time");
        Assert.That(await _membershipRepository.Load(shell.Id, alice), Is.Not.Null);

        var shellExpiry = shell.ExpiresAt;
        _time.Advance(TimeSpan.FromHours(1));
        var real = await _service.CreateOrGet("match-1", "Real Match Name", Members(), focus: false);
        Assert.That(real.ExpiresAt, Is.EqualTo(shellExpiry), "a later real CreateOrGet does not reset the shell's expiry");
    }

    [Test]
    public async Task Assert_BeforeCreate_NullName_UsesRefPlaceholder()
    {
        await _service.ApplyRosterAssertion("match-1", "e1", 1, Members(), name: null, detached: false);

        var shell = await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-1");
        Assert.That(shell.Name, Is.EqualTo("match-1"), "a null name falls back to the ref placeholder");

        var real = await _service.CreateOrGet("match-1", "Real Match Name", Members(), focus: false);
        Assert.That(real.Name, Is.EqualTo("Real Match Name"), "a later real CreateOrGet backfills the real name");
    }

    [Test]
    public async Task Assert_OnExistingChannel_IgnoresName()
    {
        await _service.CreateOrGet("match-1", "Real Name", Members(), focus: false);

        await _service.ApplyRosterAssertion("match-1", "e1", 1, Members(), name: "Different Name", detached: false);

        var reloaded = await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-1");
        Assert.That(reloaded.Name, Is.EqualTo("Real Name"), "an assertion never renames an EXISTING channel — CreateOrGet remains the name authority");
    }

    // ============================================================================================
    // ApplyRosterAssertion — staleness (plan D3)
    // ============================================================================================

    [Test]
    public async Task Assert_SameEpochLowerSeq_IsDiscarded_MembershipUnchanged()
    {
        const string alice = "Alice#1";
        const string bob = "Bob#2";
        var channel = await _service.CreateOrGet("match-1", "Match 1", Members(), focus: false);
        await _service.ApplyRosterAssertion("match-1", "e1", 5, Members(alice), name: null, detached: false);

        var outcome = await _service.ApplyRosterAssertion("match-1", "e1", 4, Members(bob), name: null, detached: false);

        // 2026-08-05 fix wave (final review M2).
        Assert.That(outcome, Is.EqualTo(RosterAssertionOutcome.DiscardedStale));
        Assert.That(await _membershipRepository.Load(channel.Id, alice), Is.Not.Null, "membership unchanged");
        Assert.That(await _membershipRepository.Load(channel.Id, bob), Is.Null, "the stale assertion never applied");
        var reloaded = await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-1");
        Assert.That(reloaded.AssertSeq, Is.EqualTo(5), "the stamp is not regressed");
    }

    [Test]
    public async Task Assert_SameEpochEqualSeq_IsDiscarded_MembershipUnchanged()
    {
        const string alice = "Alice#1";
        const string bob = "Bob#2";
        var channel = await _service.CreateOrGet("match-1", "Match 1", Members(), focus: false);
        await _service.ApplyRosterAssertion("match-1", "e1", 5, Members(alice), name: null, detached: false);

        await _service.ApplyRosterAssertion("match-1", "e1", 5, Members(bob), name: null, detached: false);

        Assert.That(await _membershipRepository.Load(channel.Id, alice), Is.Not.Null, "membership unchanged");
        Assert.That(await _membershipRepository.Load(channel.Id, bob), Is.Null, "a same-(epoch,seq) replay is a no-op");
    }

    [Test]
    public async Task Assert_SameEpochHigherSeq_Applies()
    {
        const string alice = "Alice#1";
        const string bob = "Bob#2";
        var channel = await _service.CreateOrGet("match-1", "Match 1", Members(), focus: false);
        await _service.ApplyRosterAssertion("match-1", "e1", 1, Members(alice), name: null, detached: false);

        await _service.ApplyRosterAssertion("match-1", "e1", 2, Members(bob), name: null, detached: false);

        Assert.That(await _membershipRepository.Load(channel.Id, alice), Is.Null);
        Assert.That(await _membershipRepository.Load(channel.Id, bob), Is.Not.Null);
        var reloaded = await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-1");
        Assert.That(reloaded.AssertSeq, Is.EqualTo(2));
    }

    [Test]
    public async Task Assert_DifferentEpoch_IsAccepted_AndReAnchors()
    {
        const string alice = "Alice#1";
        const string bob = "Bob#2";
        const string carol = "Carol#3";
        var channel = await _service.CreateOrGet("match-1", "Match 1", Members(), focus: false);
        await _service.ApplyRosterAssertion("match-1", "e1", 9, Members(alice), name: null, detached: false);

        await _service.ApplyRosterAssertion("match-1", "e2", 1, Members(bob), name: null, detached: false);

        var reloaded = await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-1");
        Assert.That(reloaded.AssertEpoch, Is.EqualTo("e2"));
        Assert.That(reloaded.AssertSeq, Is.EqualTo(1));
        Assert.That(await _membershipRepository.Load(channel.Id, bob), Is.Not.Null, "the different-epoch assertion is accepted and applies");

        // A follow-up under the SAME new epoch — proves the re-anchor stuck (a higher seq is now required).
        await _service.ApplyRosterAssertion("match-1", "e2", 2, Members(carol), name: null, detached: false);

        var reloadedAgain = await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-1");
        Assert.That(reloadedAgain.AssertSeq, Is.EqualTo(2));
        Assert.That(await _membershipRepository.Load(channel.Id, carol), Is.Not.Null);
    }

    [Test]
    public async Task Assert_OnChannelWithNoStoredEpoch_Applies()
    {
        const string alice = "Alice#1";
        // Created via the legacy CreateOrGet path — no assertion state stored yet (the transition pin).
        var channel = await _service.CreateOrGet("match-1", "Match 1", Members(), focus: false);

        await _service.ApplyRosterAssertion("match-1", "e1", 1, Members(alice), name: null, detached: false);

        Assert.That(await _membershipRepository.Load(channel.Id, alice), Is.Not.Null);
        var reloaded = await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-1");
        Assert.That(reloaded.AssertEpoch, Is.EqualTo("e1"));
        Assert.That(reloaded.AssertSeq, Is.EqualTo(1));
    }

    // ============================================================================================
    // Detach freeze (plan D4)
    // ============================================================================================

    [Test]
    public async Task Assert_WithDetachedTrue_AppliesFinalSet_ThenFreezes()
    {
        const string alice = "Alice#1";
        var channel = await _service.CreateOrGet("match-1", "Match 1", Members(), focus: false);

        await _service.ApplyRosterAssertion("match-1", "e1", 1, Members(alice), name: null, detached: true);

        Assert.That(await _membershipRepository.Load(channel.Id, alice), Is.Not.Null, "the final set converges FIRST");
        var reloaded = await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-1");
        Assert.That(reloaded.Detached, Is.True, "then the channel is frozen");
    }

    // Records the membership row count at the instant the freeze latch lands — the only way to pin the
    // plan's DETACH-LAST ordering (D4/D10), whose in-process post-conditions are identical either way
    // (nothing between the latch and the member writes reads Detached — see ChannelRepository.SetDetached's
    // doc). Used by both the ApplyRosterAssertion and CreateOrGet detach-ordering tests below.
    private sealed class DetachOrderChannelRepository(MongoClient mongoClient) : ChannelRepository(mongoClient)
    {
        public MembershipRepository Memberships;
        public int RowsAtDetach = -1;

        public override async Task SetDetached(string channelId)
        {
            RowsAtDetach = (await Memberships.LoadForChannel(channelId)).Count;
            await base.SetDetached(channelId);
        }
    }

    [Test]
    public async Task Assert_WithDetachedTrue_DetachLatchLandsAfterTheDiffConverged()
    {
        var repo = new DetachOrderChannelRepository(MongoClient);
        var memberships = new MembershipRepository(MongoClient, repo);
        repo.Memberships = memberships;
        var service = new MatchChannelService(repo, memberships, _messageRepository, _fanOutEngine, _time);
        await service.CreateOrGet("match-1", "Match 1", Members(), focus: false);

        await service.ApplyRosterAssertion("match-1", "e1", 1, Members("Alice#1"), name: null, detached: true);

        Assert.That(repo.RowsAtDetach, Is.EqualTo(1),
            "DETACH LAST (D4): the final member set must already be persisted when the freeze latch lands — "
            + "detach-first plus a crash mid-diff freezes a wrong roster until the 24h TTL");
    }

    [Test]
    public async Task Assert_AfterDetach_IsDiscarded_EvenWithHigherSeq_AndNewEpoch()
    {
        const string alice = "Alice#1";
        const string bob = "Bob#2";
        var channel = await _service.CreateOrGet("match-1", "Match 1", Members(), focus: false);
        await _service.ApplyRosterAssertion("match-1", "e1", 1, Members(alice), name: null, detached: true);

        var outcome = await _service.ApplyRosterAssertion("match-1", "e2", 99, Members(bob), name: null, detached: false);

        // 2026-08-05 fix wave (final review M2).
        Assert.That(outcome, Is.EqualTo(RosterAssertionOutcome.DiscardedFrozen));
        Assert.That(await _membershipRepository.Load(channel.Id, alice), Is.Not.Null, "the frozen set is untouched");
        Assert.That(await _membershipRepository.Load(channel.Id, bob), Is.Null, "a post-detach assertion never applies, even with a higher seq and a different epoch");
    }

    // Admits everything, so the CAS's own Ne(Detached, true) backstop cannot mask the DOMAIN-level
    // detach guard this test targets (plan D4 requires BOTH layers — see TryAdvanceAssertion's own
    // "REDUNDANT BY DESIGN" doc for the analogous Task 1 precedent).
    private sealed class AlwaysAdmitChannelRepository(MongoClient mongoClient) : ChannelRepository(mongoClient)
    {
        public override Task<bool> TryAdvanceAssertion(string channelId, string epoch, long seq) => Task.FromResult(true);
    }

    [Test]
    public async Task Assert_AfterDetach_DomainGuardDiscards_WithoutTheCasBackstop()
    {
        var repo = new AlwaysAdmitChannelRepository(MongoClient);
        var memberships = new MembershipRepository(MongoClient, repo);
        var service = new MatchChannelService(repo, memberships, _messageRepository, _fanOutEngine, _time);
        var channel = await service.CreateOrGet("match-1", "Ladder", Members("Alice#1"), focus: false, detached: true);

        await service.ApplyRosterAssertion("match-1", "e2", 99, Members("Bob#2"), name: null, detached: false);

        Assert.That(await memberships.Load(channel.Id, "Bob#2"), Is.Null,
            "the domain-level detach freeze discards on its own — it does not lean on the CAS filter");
        Assert.That(await memberships.Load(channel.Id, "Alice#1"), Is.Not.Null);
    }

    [Test]
    public async Task CreateOrGet_AfterDetach_BackfillsName_ButAddsNoMembers()
    {
        const string alice = "Alice#1";
        const string bob = "Bob#2";
        var channel = await _service.CreateOrGet("match-1", "Placeholder", Members(), focus: false);
        await _service.ApplyRosterAssertion("match-1", "e1", 1, Members(alice), name: null, detached: true);

        var result = await _service.CreateOrGet("match-1", "Real Name", Members(bob), focus: false);

        Assert.That(result.Name, Is.EqualTo("Real Name"), "the name backfill still runs on a detached channel");
        Assert.That(await _membershipRepository.Load(channel.Id, alice), Is.Not.Null, "the frozen membership is untouched");
        Assert.That(await _membershipRepository.Load(channel.Id, bob), Is.Null, "no new members are added to a detached channel");
    }

    [Test]
    public async Task Delete_AfterDetach_StillTearsDownChannel()
    {
        const string alice = "Alice#1";
        await _service.CreateOrGet("match-1", "Match 1", Members(), focus: false);
        await _service.ApplyRosterAssertion("match-1", "e1", 1, Members(alice), name: null, detached: true);

        await _service.DeleteChannel("match-1");

        Assert.That(await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-1"), Is.Null,
            "an explicit DELETE still tears down a detached channel — detach guards assertions/sweeps, not an explicit teardown");
    }

    // ============================================================================================
    // Concurrency (plan D5) — the per-ref gate serializes the whole "admit, diff, converge" operation
    // ============================================================================================

    // Blocks the FIRST TryAdvanceAssertion call it sees, signalling `Entered` on entry and awaiting
    // `Release` before delegating to the real implementation — lets a test prove a second concurrent
    // caller cannot reach this call until the first one (and hence the per-ref gate) releases.
    private sealed class BlockingAssertionChannelRepository(MongoClient mongoClient) : ChannelRepository(mongoClient)
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public override async Task<bool> TryAdvanceAssertion(string channelId, string epoch, long seq)
        {
            if (Interlocked.Increment(ref _callCount) == 1)
            {
                Entered.SetResult();
                await Release.Task;
            }

            return await base.TryAdvanceAssertion(channelId, epoch, seq);
        }
    }

    [Test]
    public async Task ConcurrentAssertions_SameRef_DoNotInterleave()
    {
        const string alice = "Alice#1";
        const string bob = "Bob#2";

        var blockingRepo = new BlockingAssertionChannelRepository(MongoClient);
        var membershipRepo = new MembershipRepository(MongoClient, blockingRepo);
        var messageRepo = new MessageRepository(MongoClient);
        var service = new MatchChannelService(blockingRepo, membershipRepo, messageRepo, _fanOutEngine, _time);

        await service.CreateOrGet("match-1", "Match 1", Members(), focus: false);

        var taskA = service.ApplyRosterAssertion("match-1", "e1", 1, Members(alice), name: null, detached: false);
        await blockingRepo.Entered.Task; // A is confirmed inside TryAdvanceAssertion, holding the per-ref gate.

        var taskB = service.ApplyRosterAssertion("match-1", "e1", 2, Members(bob), name: null, detached: false);

        // Give B every opportunity to (incorrectly) race past the gate while A is still inside its CAS call.
        await Task.WhenAny(taskB, Task.Delay(TimeSpan.FromMilliseconds(300)));
        Assert.That(blockingRepo.CallCount, Is.EqualTo(1), "B must not have entered TryAdvanceAssertion while A still holds the per-ref gate");
        Assert.That(taskB.IsCompleted, Is.False, "B is blocked on the per-ref gate, not merely slow");

        blockingRepo.Release.SetResult();
        await taskA;
        await taskB;

        Assert.That(blockingRepo.CallCount, Is.EqualTo(2));
        var channel = await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-1");
        Assert.That(channel.AssertSeq, Is.EqualTo(2), "B's assertion (the later, higher seq) is the final stamped state");
        Assert.That(await _membershipRepository.Load(channel.Id, alice), Is.Null, "B's full-set assertion supersedes A's — alice is not in B's set");
        Assert.That(await _membershipRepository.Load(channel.Id, bob), Is.Not.Null);
    }

    // ============================================================================================
    // Create-route stamping (plan D10)
    // ============================================================================================

    [Test]
    public async Task CreateOrGet_WithDetached_AppliesMembers_ThenFreezes()
    {
        const string alice = "Alice#1";
        const string bob = "Bob#2";
        RegisterOnline("conn-alice", alice);

        var channel = await _service.CreateOrGet("match-1", "Ladder Match", Members(alice), focus: false, detached: true);

        Assert.That(await _membershipRepository.Load(channel.Id, alice), Is.Not.Null, "members are added at birth");
        Assert.That(_harness.SignalCount("conn-alice", ChatEvents.ChannelAdded), Is.EqualTo(1));
        var reloaded = await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-1");
        Assert.That(reloaded.Detached, Is.True, "a ladder-match channel is born detached");

        // A follow-up assertion must be discarded — the channel is frozen from birth.
        await _service.ApplyRosterAssertion("match-1", "e1", 1, Members(bob), name: null, detached: false);
        Assert.That(await _membershipRepository.Load(channel.Id, bob), Is.Null);
    }

    [Test]
    public async Task CreateOrGet_WithDetached_LatchLandsAfterTheBirthAdds()
    {
        var repo = new DetachOrderChannelRepository(MongoClient);
        var memberships = new MembershipRepository(MongoClient, repo);
        repo.Memberships = memberships;
        var service = new MatchChannelService(repo, memberships, _messageRepository, _fanOutEngine, _time);

        await service.CreateOrGet("match-1", "Ladder Match", Members("Alice#1"), focus: false, detached: true);

        Assert.That(repo.RowsAtDetach, Is.EqualTo(1),
            "D10 adds-before-detach: a crashed-then-retried create only converges because the adds always precede the latch");
    }

    [Test]
    public async Task CreateOrGet_DetachedRetry_OnAlreadyDetachedChannel_IsIdempotent()
    {
        const string alice = "Alice#1";
        RegisterOnline("conn-alice", alice);
        var first = await _service.CreateOrGet("match-1", "Ladder Match", Members(alice), focus: false, detached: true);
        var pushCountAfterFirst = _harness.AllSignals.Count;

        var second = await _service.CreateOrGet("match-1", "Ladder Match", Members(alice), focus: false, detached: true);

        Assert.That(second.Id, Is.EqualTo(first.Id));
        Assert.That(_harness.AllSignals.Count, Is.EqualTo(pushCountAfterFirst), "the retried detached create pushes nothing new");
        Assert.That(await _membershipRepository.LoadForChannel(first.Id), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task CreateOrGet_WithEpochSeq_StaleAgainstNewerAssertion_DoesNotResurrectMembers()
    {
        const string alice = "Alice#1";
        var channel = await _service.CreateOrGet("match-1", "Match 1", Members(alice), focus: false, epoch: "e1", seq: 1);
        await _service.ApplyRosterAssertion("match-1", "e1", 5, Members(), name: null, detached: false); // removes alice

        await _service.CreateOrGet("match-1", "Match 1", Members(alice), focus: false, epoch: "e1", seq: 4);

        Assert.That(await _membershipRepository.Load(channel.Id, alice), Is.Null,
            "a late create carrying a STALE (epoch, seq) must not resurrect a member a newer assertion already removed");
    }

    [Test]
    public async Task CreateOrGet_WithEqualSeq_StillAddsMembers()
    {
        const string alice = "Alice#1";
        // Simulates the crashed-first-attempt case: the (epoch, seq) stamp landed, but the process crashed
        // before the member add — so the retry below arrives with the SAME (epoch, seq) and alice missing.
        var channel = await _service.CreateOrGet("match-1", "Match 1", Members(), focus: false, epoch: "e1", seq: 4);

        await _service.CreateOrGet("match-1", "Match 1", Members(alice), focus: false, epoch: "e1", seq: 4);

        Assert.That(await _membershipRepository.Load(channel.Id, alice), Is.Not.Null,
            "an EQUAL stored seq still proceeds with adds — they are idempotent, so a crash between stamp and add never loses the member");
        var reloaded = await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-1");
        Assert.That(reloaded.AssertSeq, Is.EqualTo(4));
    }

    [Test]
    public async Task CreateOrGet_WithoutNewFields_BehavesExactlyAsToday()
    {
        const string alice = "Alice#1";
        RegisterOnline("conn-alice", alice);

        var channel = await _service.CreateOrGet("match-1", "Match 1", Members(alice), focus: true);

        Assert.That(await _membershipRepository.Load(channel.Id, alice), Is.Not.Null);
        Assert.That(_harness.SignalCount("conn-alice", ChatEvents.ChannelAdded), Is.EqualTo(1));
        var reloaded = await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-1");
        Assert.That(reloaded.AssertEpoch, Is.Null, "no (epoch, seq) stamp is written when the fields are omitted — the transition pin");
        Assert.That(reloaded.Detached, Is.False);
    }

    // ============================================================================================
    // Out-of-order heal — assert creates a shell, delete tears it down, a late create heals it
    // idempotently. Narrower unit-level replacement for the delta-path
    // OutOfOrder_PutThenDelete_ThenLatePost_HealsIdempotently test removed in the 2026-08-05
    // delta-deletion round (task-7-report.md, "Judgment calls"), rewritten onto the surviving
    // roster-assertion + D10 create-stamping protocol instead of the retired delta endpoint.
    // ============================================================================================

    [Test]
    public async Task OutOfOrder_AssertThenDelete_ThenLateCreate_HealsWithFreshChannel()
    {
        const string alice = "Alice#1";
        RegisterOnline("conn-alice", alice);

        // (1) An assertion for an UNKNOWN ref arrives before mm's create POST — the create-on-demand
        // path stamps (e1, 1) and creates the shell.
        await _service.ApplyRosterAssertion("match-1", "e1", 1, Members(), name: "My Lobby", detached: false);
        var shell = await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-1");
        Assert.That(shell, Is.Not.Null, "precondition: the out-of-order assertion created the shell on demand");
        var shellId = shell.Id;
        var shellExpiry = shell.ExpiresAt;

        // The clock moves before the delete+late-create below, so a fresh 24h expiry is distinguishable
        // from the shell's own creation-anchored one.
        _time.Advance(TimeSpan.FromHours(3));

        // (2) A hard DELETE tears the shell down completely — doc, memberships, messages all gone.
        await _service.DeleteChannel("match-1");
        Assert.That(await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-1"), Is.Null,
            "the channel doc is torn down");
        Assert.That(await _membershipRepository.LoadForChannel(shellId), Is.Empty, "its memberships are torn down");

        // (3) A LATE create for the same ref now arrives, carrying the (epoch, seq) pair from BEFORE the
        // delete (e1, 1) — stale coordinates relative to the vanished channel, but there is no live document
        // at all for TryAdvanceAssertion to compare against, so it must heal idempotently rather than throw.
        ChatChannel healed = null;
        Assert.DoesNotThrowAsync(
            async () => healed = await _service.CreateOrGet("match-1", "My Lobby", Members(alice), focus: false, epoch: "e1", seq: 1),
            "the late create must heal idempotently — never throw on a stale (epoch, seq) against a fresh doc");

        Assert.That(healed, Is.Not.Null);
        Assert.That(healed.Id, Is.Not.EqualTo(shellId), "the heal produces a BRAND-NEW channel doc, not the deleted one");

        // Exactly one physical doc for the ref — the heal does not leave a duplicate/orphaned row behind.
        var db = MongoClient.GetDatabase(MongoDbRepositoryBase.DatabaseName);
        var docCount = await db.GetCollection<ChatChannel>(ChatCollections.Channels).CountDocumentsAsync(c =>
            c.Type == ChannelType.System && c.SystemKind == SystemChannelKind.Match && c.SystemRef == "match-1");
        Assert.That(docCount, Is.EqualTo(1), "exactly one channel doc exists for the ref after the heal");

        // The fresh doc carries NO stored stamp going in, so the D10 member-add gate admits (CreateOrGetLocked's
        // skipAdds reads AssertEpoch off the FRESH doc, which is null) and alice's membership is applied.
        Assert.That(await _membershipRepository.Load(healed.Id, alice), Is.Not.Null,
            "the fresh doc's D10 gate admits — members are applied on the healed channel");
        Assert.That(_harness.SignalCount("conn-alice", ChatEvents.ChannelAdded), Is.EqualTo(1),
            "alice's live connection receives ChannelAdded for the healed channel");

        // A fresh 24h expiry, anchored to the heal's OWN creation time — not a reuse of the deleted shell's
        // now-stale expiry.
        Assert.That((healed.ExpiresAt.Value - Now.AddHours(24)).Duration(), Is.LessThan(TimeSpan.FromSeconds(1)),
            "the healed channel's expiry is freshly anchored to ITS OWN creation time");
        Assert.That(healed.ExpiresAt, Is.Not.EqualTo(shellExpiry),
            "the healed channel's expiry is NOT the stale, deleted shell's expiry");
    }

    // ============================================================================================
    // 2026-08-05 reconciliation — ApplyEpochSync: startup teardown of orphaned lobby channels
    // (plan D8, Task 4)
    // ============================================================================================

    [Test]
    public async Task EpochSync_TearsDownChannelsNotInLiveList()
    {
        const string alice = "Alice#1";
        // 2026-08-05 fix wave (final review H1, plan D8 amendment): stamped via epoch/seq on create — an
        // UNSTAMPED channel is invisible to the sweep entirely (see the dedicated survives-test below),
        // so a meaningful "gets torn down" test needs a channel that has participated in the assertion
        // protocol at least once.
        var gone = await _service.CreateOrGet("match-gone", "Gone Match", Members(alice), focus: false, epoch: "e1", seq: 1);
        var kept = await _service.CreateOrGet("match-kept", "Kept Match", Members(alice), focus: false, epoch: "e1", seq: 1);
        var message = NewMessage(gone.Id, 1, alice);
        await _messageRepository.Insert(message);

        await _service.ApplyEpochSync("e2", Members("match-kept"));

        Assert.That(await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-gone"), Is.Null,
            "the unlisted channel doc is torn down");
        Assert.That(await _membershipRepository.LoadForChannel(gone.Id), Is.Empty, "its memberships are gone");
        Assert.That(await _messageRepository.Load(message.Id), Is.Null, "its messages are purged");

        Assert.That(await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-kept"), Is.Not.Null,
            "the listed channel is fully intact");
        Assert.That(await _membershipRepository.Load(kept.Id, alice), Is.Not.Null);
    }

    [Test]
    public async Task EpochSync_PushesChannelRemovedToOnlineMembersOfTornDownChannels()
    {
        const string alice = "Alice#1";
        const string bob = "Bob#2";
        RegisterOnline("conn-alice", alice);
        RegisterOnline("conn-bob", bob);

        var gone = await _service.CreateOrGet("match-gone", "Gone Match", Members(alice), focus: false, epoch: "e1", seq: 1);
        var kept = await _service.CreateOrGet("match-kept", "Kept Match", Members(bob), focus: false, epoch: "e1", seq: 1);
        var detachedRef = "match-detached";
        await _service.CreateOrGet(detachedRef, "Detached Match", Members(), focus: false, detached: true);

        await _service.ApplyEpochSync("e2", Members("match-kept"));

        Assert.That(_harness.SignalCount("conn-alice", ChatEvents.ChannelRemoved), Is.EqualTo(1),
            "the torn-down channel's online member receives ChannelRemoved");
        var dto = _harness.PayloadFor("conn-alice", ChatEvents.ChannelRemoved) as ChannelRemovedDto;
        Assert.That(dto, Is.Not.Null);
        Assert.That(dto.ChannelId, Is.EqualTo(gone.Id));

        Assert.That(_harness.SignalCount("conn-bob", ChatEvents.ChannelRemoved), Is.EqualTo(0),
            "the spared channel's member receives nothing");
        Assert.That(kept.Id, Is.Not.Null);
    }

    [Test]
    public async Task EpochSync_SparesDetachedChannels()
    {
        const string alice = "Alice#1";
        RegisterOnline("conn-alice", alice);
        var detached = await _service.CreateOrGet("match-detached", "Ladder", Members(alice), focus: false, detached: true);
        var message = NewMessage(detached.Id, 1, alice);
        await _messageRepository.Insert(message);

        // The detached channel's ref is NOT in the live list — the empty live set is the post-crash case.
        await _service.ApplyEpochSync("e2", Members());

        Assert.That(await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-detached"), Is.Not.Null,
            "a detached channel survives an epoch sync even when absent from liveLobbyRefs");
        Assert.That(await _membershipRepository.Load(detached.Id, alice), Is.Not.Null, "its memberships survive");
        Assert.That(await _messageRepository.Load(message.Id), Is.Not.Null, "its messages survive");
        Assert.That(_harness.SignalCount("conn-alice", ChatEvents.ChannelRemoved), Is.EqualTo(0),
            "a spared detached channel's member receives no push");
    }

    [Test]
    public async Task EpochSync_EmptyLiveList_TearsDownEveryNonDetachedMatchChannel_ButNotDetachedOnes()
    {
        const string alice = "Alice#1";
        var matchA = await _service.CreateOrGet("match-a", "Match A", Members(alice), focus: false, epoch: "e1", seq: 1);
        var matchB = await _service.CreateOrGet("match-b", "Match B", Members(alice), focus: false, epoch: "e1", seq: 1);
        var detached = await _service.CreateOrGet("match-detached", "Ladder", Members(alice), focus: false, detached: true);

        await _service.ApplyEpochSync("e2", Members());

        Assert.That(await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-a"), Is.Null);
        Assert.That(await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-b"), Is.Null);
        Assert.That(await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-detached"), Is.Not.Null,
            "the post-crash empty live list still spares detached channels");
        Assert.That(matchA.Id, Is.Not.Null);
        Assert.That(matchB.Id, Is.Not.Null);
        Assert.That(detached.Id, Is.Not.Null);
    }

    [Test]
    public async Task EpochSync_ReStampsSparedChannels_SoNewEpochSeq1Applies()
    {
        const string alice = "Alice#1";
        const string bob = "Bob#2";
        var channel = await _service.CreateOrGet("match-1", "Match 1", Members(), focus: false);
        await _service.ApplyRosterAssertion("match-1", "e1", 9, Members(alice), name: null, detached: false);

        await _service.ApplyEpochSync("e2", Members("match-1"));

        var reloaded = await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-1");
        Assert.That(reloaded.AssertEpoch, Is.EqualTo("e2"), "the spared channel is re-anchored to the new epoch");
        Assert.That(reloaded.AssertSeq, Is.EqualTo(0), "the seq counter is reset to the 0 sentinel");

        await _service.ApplyRosterAssertion("match-1", "e2", 1, Members(bob), name: null, detached: false);

        Assert.That(await _membershipRepository.Load(channel.Id, bob), Is.Not.Null,
            "seq 1 under the new epoch applies cleanly after the re-anchor");
    }

    [Test]
    public async Task EpochSync_StampsSparedChannelThatHasNoStoredEpoch()
    {
        const string bob = "Bob#2";
        // Created via CreateOrGet WITH an epoch/seq stamp (unlike the sibling survives-test below) but
        // never asserted since — AssertEpoch is set directly by the create's own D10 stamp, so this is
        // the $exists:false-turned-true disjunct of StampAssertionEpoch's filter: a channel stamped
        // exactly once, at create time, is still spared and re-anchored like any other candidate.
        var channel = await _service.CreateOrGet("match-1", "Match 1", Members(), focus: false, epoch: "e1", seq: 1);

        await _service.ApplyEpochSync("e2", Members("match-1"));

        var reloaded = await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-1");
        Assert.That(reloaded.AssertEpoch, Is.EqualTo("e2"),
            "a spared channel stamped only once, at create time, must still be re-anchored to the new epoch");
        Assert.That(reloaded.AssertSeq, Is.EqualTo(0), "the seq counter is reset to the 0 sentinel");

        await _service.ApplyRosterAssertion("match-1", "e2", 1, Members(bob), name: null, detached: false);

        Assert.That(await _membershipRepository.Load(channel.Id, bob), Is.Not.Null,
            "seq 1 under the new epoch applies cleanly after the re-anchor, confirming the stamp actually landed");
    }

    [Test]
    public async Task EpochSync_UnstampedChannel_SurvivesEvenWhenAbsentFromLiveList_FallsToTtl()
    {
        const string alice = "Alice#1";
        // Created via CreateOrGet without epoch/seq and NEVER stamped by the assertion protocol at all —
        // AssertEpoch is genuinely ABSENT on the stored document, exactly the shape of a channel minted
        // by an mm that has not yet started sending epoch/seq or asserting a roster for this lobby.
        // 2026-08-05 fix wave (final review H1, plan D8 amendment): such a channel must NOT be swept by
        // an epoch sync — LoadNonDetachedMatchChannels's AssertEpoch-exists filter makes it invisible to
        // the sweep entirely, so it survives regardless of liveLobbyRefs and falls to its own 24h TTL.
        var channel = await _service.CreateOrGet("match-legacy", "Legacy Match", Members(alice), focus: false);

        // Absent from BOTH the live list AND ever having been stamped — the worst case, and exactly the
        // shape of the mm-deploy cutover: mm boots with an empty liveLobbyRefs and a fresh epoch, and
        // every pre-deploy channel in the database looks exactly like this.
        await _service.ApplyEpochSync("e2", Members());

        var reloaded = await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-legacy");
        Assert.That(reloaded, Is.Not.Null, "an unstamped channel must survive an epoch sync even when absent from liveLobbyRefs");
        Assert.That(reloaded.AssertEpoch, Is.Null, "it is left completely untouched — not re-stamped either, since it was never a candidate");
        Assert.That(await _membershipRepository.Load(channel.Id, alice), Is.Not.Null, "its memberships survive");
    }

    [Test]
    public async Task EpochSync_LeavesNonMatchChannelsUntouched()
    {
        const string alice = "Alice#1";

        var publicChannel = new ChatChannel { Type = ChannelType.Public, Name = "W3C Lounge", NormalizedName = ChannelNames.Normalize("W3C Lounge") };
        await _channelRepository.Insert(publicChannel);
        await _membershipRepository.Insert(new ChannelMembership { ChannelId = publicChannel.Id, BattleTag = alice, JoinedAt = Now });

        var dm = new ChatChannel { Type = ChannelType.Dm, PairKey = DmPairKey.For(alice, "Bob#2") };
        await _channelRepository.Insert(dm);
        await _membershipRepository.Insert(new ChannelMembership { ChannelId = dm.Id, BattleTag = alice, JoinedAt = Now });

        var group = new ChatChannel { Type = ChannelType.GroupDm, Name = "squad", LastMessageAt = Now, ExpiresAt = Now.AddDays(365) };
        await _channelRepository.Insert(group);
        await _membershipRepository.Insert(new ChannelMembership { ChannelId = group.Id, BattleTag = alice, JoinedAt = Now });

        var clan = await _channelRepository.FindOrCreateSystem(SystemChannelKind.Clan, "clan-1", "Clan Chat", Now);
        await _membershipRepository.Insert(new ChannelMembership { ChannelId = clan.Id, BattleTag = alice, JoinedAt = Now });

        await _service.ApplyEpochSync("e2", Members());

        Assert.That(await _channelRepository.Load(publicChannel.Id), Is.Not.Null, "Public channels are untouched");
        Assert.That(await _channelRepository.Load(dm.Id), Is.Not.Null, "Dm channels are untouched");
        Assert.That(await _channelRepository.Load(group.Id), Is.Not.Null, "GroupDm channels are untouched");
        Assert.That(await _channelRepository.Load(clan.Id), Is.Not.Null, "System+Clan channels are untouched");
        Assert.That(await _membershipRepository.Load(publicChannel.Id, alice), Is.Not.Null);
        Assert.That(await _membershipRepository.Load(dm.Id, alice), Is.Not.Null);
        Assert.That(await _membershipRepository.Load(group.Id, alice), Is.Not.Null);
        Assert.That(await _membershipRepository.Load(clan.Id, alice), Is.Not.Null);
    }

    [Test]
    public async Task EpochSync_UnknownRefInLiveList_IsANoOp()
    {
        Assert.DoesNotThrowAsync(async () => await _service.ApplyEpochSync("e2", Members("never-existed")));

        Assert.That(await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "never-existed"), Is.Null,
            "listing a ref with no channel neither creates one nor throws");
    }

    [Test]
    public async Task EpochSync_IsIdempotent()
    {
        const string alice = "Alice#1";
        RegisterOnline("conn-alice", alice);
        var gone = await _service.CreateOrGet("match-gone", "Gone Match", Members(alice), focus: false, epoch: "e1", seq: 1);
        await _service.CreateOrGet("match-kept", "Kept Match", Members(), focus: false, epoch: "e1", seq: 1);

        await _service.ApplyEpochSync("e2", Members("match-kept"));
        var pushCountAfterFirst = _harness.AllSignals.Count;
        var keptAfterFirst = await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-kept");

        await _service.ApplyEpochSync("e2", Members("match-kept"));

        Assert.That(await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-gone"), Is.Null,
            "the torn-down channel stays torn down");
        var keptAfterSecond = await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-kept");
        Assert.That(keptAfterSecond.AssertEpoch, Is.EqualTo(keptAfterFirst.AssertEpoch), "same end state on the spared channel");
        Assert.That(keptAfterSecond.AssertSeq, Is.EqualTo(keptAfterFirst.AssertSeq));
        Assert.That(_harness.AllSignals.Count, Is.EqualTo(pushCountAfterFirst), "no additional pushes on the second run");
        Assert.That(gone.Id, Is.Not.Null);
    }

    [Test]
    public async Task EpochSync_LeavesNoOrphanedMembershipRows()
    {
        const string alice = "Alice#1";
        const string bob = "Bob#2";
        await _service.CreateOrGet("match-gone-1", "Gone 1", Members(alice), focus: false, epoch: "e1", seq: 1);
        await _service.CreateOrGet("match-gone-2", "Gone 2", Members(alice, bob), focus: false, epoch: "e1", seq: 1);
        await _service.CreateOrGet("match-kept", "Kept", Members(bob), focus: false);

        await _service.ApplyEpochSync("e2", Members("match-kept"));

        var orphansDeleted = await new CleanupJobs(MongoClient).SweepOrphanedMemberships();

        Assert.That(orphansDeleted, Is.EqualTo(0),
            "the shared TearDownChannel routine deletes messages, memberships AND the channel doc together — "
            + "so no orphaned membership row is left behind for CleanupJobs to find");
    }

    [Test]
    public async Task EpochSync_LiveRefMatching_IsCaseSensitive()
    {
        await _service.CreateOrGet("match-A", "Upper", Members("Alice#1"), focus: false, epoch: "e1", seq: 1);

        // A ref differing only in case is a DIFFERENT lobby — refs are exact Mongo keys drawn from
        // [A-Za-z0-9_-] (mm's nanoids use a mixed-case alphabet), unlike battleTags.
        await _service.ApplyEpochSync("e2", Members("match-a"));

        Assert.That(await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-A"), Is.Null,
            "liveLobbyRefs is matched ORDINALLY — case folding would SPARE an orphaned channel whose ref "
            + "differs only in case from a live one");
    }

    [Test]
    public async Task EpochSync_DoesNotResetAChannelAlreadyAnchoredToTheSyncsOwnEpoch()
    {
        const string alice = "Alice#1";
        const string bob = "Bob#2";
        var channel = await _service.CreateOrGet("match-1", "Match 1", Members(), focus: false);
        // Anchor the channel to "e2" DURING what will be treated as "this same boot" — mirrors a lobby
        // created/asserted while an epoch sync under "e2" was still retrying.
        await _service.ApplyRosterAssertion("match-1", "e2", 5, Members(alice), name: null, detached: false);

        await _service.ApplyEpochSync("e2", Members("match-1"));

        var reloaded = await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-1");
        Assert.That(reloaded.AssertEpoch, Is.EqualTo("e2"));
        Assert.That(reloaded.AssertSeq, Is.EqualTo(5),
            "a channel already anchored to the sync's OWN epoch must be left entirely untouched — "
            + "resetting AssertSeq to 0 here would re-open the duplicate-replay window for assertions "
            + "already applied under this epoch (plan D8 refinement, Task-4 review INFO-1)");

        // If the reset had wrongly landed (AssertSeq -> 0), this duplicate/stale seq-3 assertion would be
        // wrongly re-admitted (3 > 0) instead of discarded (3 <= the real stored seq, 5).
        await _service.ApplyRosterAssertion("match-1", "e2", 3, Members(bob), name: null, detached: false);
        Assert.That(await _membershipRepository.Load(channel.Id, bob), Is.Null,
            "a duplicate (e2, 3) assertion is STILL discarded after the sync — proves AssertSeq was not reset");

        // seq 6 — genuinely newer than the untouched stored seq 5 — still applies normally.
        await _service.ApplyRosterAssertion("match-1", "e2", 6, Members(bob), name: null, detached: false);
        Assert.That(await _membershipRepository.Load(channel.Id, bob), Is.Not.Null,
            "(e2, 6) still applies — the untouched counter continues advancing normally post-sync");
    }

    // ============================================================================================
    // 2026-08-05 fix wave (final review H2) — ApplyEpochSync honors the caller's CancellationToken
    // ============================================================================================

    [Test]
    public async Task EpochSync_PreCancelledToken_StopsLoopEarly_PartialProcessing()
    {
        const string alice = "Alice#1";
        const string bob = "Bob#2";
        await _service.CreateOrGet("match-a", "A", Members(alice), focus: false, epoch: "e1", seq: 1);
        await _service.CreateOrGet("match-b", "B", Members(bob), focus: false, epoch: "e1", seq: 1);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Empty live list: BOTH candidates would normally be torn down. A token that is ALREADY
        // cancelled before the loop's first iteration must bail before touching either one — the
        // check-and-bail happens at the TOP of the loop, between channels, never mid-teardown.
        await _service.ApplyEpochSync("e2", Members(), cts.Token);

        Assert.That(await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-a"), Is.Not.Null,
            "a pre-cancelled token must stop the sweep before it processes ANY candidate — partial (here, zero) "
            + "processing is safe: nothing already-processed is left half-mutated, and the next attempt's "
            + "candidate set is unchanged, so it makes full durable progress");
        Assert.That(await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-b"), Is.Not.Null);
    }

    [Test]
    public async Task EpochSync_NonCancelledToken_CompletesNormally_UnchangedBehavior()
    {
        // Back-compat pin: the default CancellationToken (the overload every pre-H2 caller — including
        // every other test in this file — uses) must behave byte-for-byte as before H2's change.
        const string alice = "Alice#1";
        await _service.CreateOrGet("match-gone", "Gone", Members(alice), focus: false, epoch: "e1", seq: 1);

        await _service.ApplyEpochSync("e2", Members(), CancellationToken.None);

        Assert.That(await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-gone"), Is.Null,
            "an explicit non-cancelled token completes the sweep exactly as the default-token overload does");
    }

    // Fires a one-shot hook the first time a teardown reads a channel's member list — TearDownChannel's
    // very first statement, and the ONLY caller of LoadForChannel on this path. That instant is inside
    // ApplyEpochSync's loop but strictly AFTER its discovery scan, i.e. exactly the TOCTOU window the
    // in-gate re-load exists to cover. (LoadForChannel is already a documented test seam.)
    private sealed class TeardownHookMembershipRepository(
        MongoClient client, ChannelRepository channelRepository, Func<string, Task> onFirstTeardown)
        : MembershipRepository(client, channelRepository)
    {
        private int _calls;

        public override async Task<List<ChannelMembership>> LoadForChannel(string channelId)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                await onFirstTeardown(channelId);
            }

            return await base.LoadForChannel(channelId);
        }
    }

    [Test]
    public async Task EpochSync_ReLoadsInsideTheGate_SoAChannelDetachedAfterTheScanIsSpared()
    {
        var channelRepo = new ChannelRepository(MongoClient);
        string idA = null;
        string idB = null;

        // Detach the OTHER orphan while the first one is being torn down — order-independent, so this
        // does not depend on the natural order LoadNonDetachedMatchChannels returns.
        var memberships = new TeardownHookMembershipRepository(
            MongoClient, channelRepo, async tornDownId => await channelRepo.SetDetached(tornDownId == idA ? idB : idA));
        var service = new MatchChannelService(channelRepo, memberships, _messageRepository, _fanOutEngine, _time);

        var a = await service.CreateOrGet("match-a", "A", Members("Alice#1"), focus: false, epoch: "e1", seq: 1);
        var b = await service.CreateOrGet("match-b", "B", Members("Bob#2"), focus: false, epoch: "e1", seq: 1);
        idA = a.Id;
        idB = b.Id;

        // Empty live list: BOTH refs are orphans at scan time, so both are teardown candidates.
        await service.ApplyEpochSync("e2", Members());

        var survivors = new[]
        {
            await channelRepo.LoadBySystemRef(SystemChannelKind.Match, "match-a"),
            await channelRepo.LoadBySystemRef(SystemChannelKind.Match, "match-b"),
        }.Where(c => c != null).ToList();

        Assert.That(survivors.Count, Is.EqualTo(1),
            "a channel DETACHED between the discovery scan and its own turn must be re-loaded inside the "
            + "per-ref gate and SPARED — acting on the stale scan-time candidate tears down a live room");
        Assert.That(survivors[0].Detached, Is.True, "the survivor is the one that was detached mid-sync");
        Assert.That(await memberships.Load(survivors[0].Id, survivors[0].SystemRef == "match-a" ? "Alice#1" : "Bob#2"),
            Is.Not.Null, "the spared channel keeps its membership rows");
    }

    [Test]
    public async Task EpochSync_ReLoadsInsideTheGate_SoAChannelDeletedAfterTheScanIsSkipped()
    {
        RegisterOnline("conn-alice", "Alice#1");
        RegisterOnline("conn-bob", "Bob#2");
        var channelRepo = new ChannelRepository(MongoClient);
        string idA = null;
        string idB = null;

        var memberships = new TeardownHookMembershipRepository(
            MongoClient, channelRepo, async tornDownId => await channelRepo.Delete(tornDownId == idA ? idB : idA));
        var service = new MatchChannelService(channelRepo, memberships, _messageRepository, _fanOutEngine, _time);

        var a = await service.CreateOrGet("match-a", "A", Members("Alice#1"), focus: false, epoch: "e1", seq: 1);
        var b = await service.CreateOrGet("match-b", "B", Members("Bob#2"), focus: false, epoch: "e1", seq: 1);
        idA = a.Id;
        idB = b.Id;
        var pushesBefore = _harness.AllSignals.Count;

        await service.ApplyEpochSync("e2", Members());

        // The concurrently-deleted channel is skipped outright: no second teardown, no ChannelRemoved
        // for a channel this sync never owned. Exactly ONE teardown ran, so exactly ONE push landed.
        Assert.That(_harness.AllSignals.Count - pushesBefore, Is.EqualTo(1),
            "a channel whose doc vanished between the scan and its turn is skipped, not torn down on stale data");
    }
}
