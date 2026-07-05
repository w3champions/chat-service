using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.FanOut;
using W3ChampionsChatService.Internal;
using W3ChampionsChatService.Memberships;
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
        _service = new MatchChannelService(_channelRepository, _membershipRepository, _fanOutEngine, _time);
    }

    // Registers a live session for battleTag under connectionId — the RegisterOnline idiom
    // ChannelEventEmitterTests/DmGroupIntegrationTests use to make a user "online".
    private void RegisterOnline(string connectionId, string battleTag) =>
        _sessionRegistry.Register(
            connectionId,
            new W3CUserAuthentication { BattleTag = battleTag, Name = battleTag.Split('#')[0] },
            null);

    private static IReadOnlyList<string> Members(params string[] battleTags) => battleTags;

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
}
