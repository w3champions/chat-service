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
    // C7 Task 7 — ApplyMembersDelta: add creates membership + pushes ChannelAdded
    // ============================================================================================

    [Test]
    public async Task Delta_AddCreatesMembership_AndPushesChannelAdded()
    {
        const string bt = "Alice#1";
        RegisterOnline("conn-alice", bt);

        // The channel already exists (mm's initial CreateOrGet POST already landed) before the delta arrives.
        var channel = await _service.CreateOrGet("match-1", "Match 1", Members(), focus: false);

        await _service.ApplyMembersDelta("match-1", add: Members(bt), remove: Members(), focus: true);

        var membership = await _membershipRepository.Load(channel.Id, bt);
        Assert.That(membership, Is.Not.Null, "the add creates a durable membership");
        Assert.That(_harness.SignalCount("conn-alice", ChatEvents.ChannelAdded), Is.EqualTo(1),
            "ChannelAdded is pushed for the added member");
    }

    // ============================================================================================
    // ApplyMembersDelta — remove deletes membership + pushes ChannelRemoved + force-unfocuses the
    // removed user's connection (acceptance 4)
    // ============================================================================================

    [Test]
    public async Task Delta_RemoveDeletesMembership_PushesChannelRemoved_AndUnfocusesRemovedUsersConnection()
    {
        const string bt = "Alice#1";
        RegisterOnline("conn-alice", bt);

        var channel = await _service.CreateOrGet("match-1", "Match 1", Members(bt), focus: true);
        _focusRegistry.Focus("conn-alice", channel.Id, bt);
        Assert.That(_focusRegistry.GetFocusedChannels("conn-alice"), Does.Contain(channel.Id),
            "precondition: the connection has the channel focused before the removal");

        await _service.ApplyMembersDelta("match-1", add: Members(), remove: Members(bt), focus: false);

        Assert.That(await _membershipRepository.Load(channel.Id, bt), Is.Null, "the membership is deleted");
        Assert.That(_harness.SignalCount("conn-alice", ChatEvents.ChannelRemoved), Is.EqualTo(1),
            "ChannelRemoved is pushed to the removed user's live connection");
        var dto = _harness.PayloadFor("conn-alice", ChatEvents.ChannelRemoved) as ChannelRemovedDto;
        Assert.That(dto, Is.Not.Null);
        Assert.That(dto.ChannelId, Is.EqualTo(channel.Id));

        // Acceptance 4: the removed user's connection is FORCE-UNFOCUSED from the channel — the
        // FocusRegistry.Unfocus tail of FanOutEngine.PushChannelRemoved (Task 5) IS this force-unfocus.
        Assert.That(_focusRegistry.GetFocusedChannels("conn-alice"), Does.Not.Contain(channel.Id),
            "the connection is no longer focused on the removed channel");
    }

    // ============================================================================================
    // ApplyMembersDelta — delta-before-create: create-on-demand shell (ref-as-name, 24h expiry from
    // its OWN creation), and a LATER real CreateOrGet backfills the name WITHOUT resetting expiry (M1)
    // ============================================================================================

    [Test]
    public async Task Delta_BeforeCreate_CreatesShell_WithRefAsName_And24hExpiryFromShellCreation()
    {
        const string bt = "Alice#1";
        RegisterOnline("conn-alice", bt);

        // The delta arrives BEFORE mm's CreateOrGet POST — create-on-demand, never a hard 404 (§3.3, M1).
        await _service.ApplyMembersDelta("match-1", add: Members(bt), remove: Members(), focus: true);

        var shell = await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-1");
        Assert.That(shell, Is.Not.Null, "the shell is created on-demand rather than a hard 404");
        Assert.That(shell.Name, Is.EqualTo("match-1"), "the placeholder name is the ref itself");
        Assert.That(shell.ExpiresAt, Is.Not.Null);
        Assert.That((shell.ExpiresAt.Value - Now.AddHours(24)).Duration(), Is.LessThan(TimeSpan.FromSeconds(1)),
            "the shell's 24h expiry is anchored to its OWN creation time");
        Assert.That(await _membershipRepository.Load(shell.Id, bt), Is.Not.Null, "the add still applies to the newly-created shell");
        Assert.That(_harness.SignalCount("conn-alice", ChatEvents.ChannelAdded), Is.EqualTo(1));

        var shellExpiry = shell.ExpiresAt;

        // The clock moves before the LATER real mm CreateOrGet POST arrives.
        _time.Advance(TimeSpan.FromHours(1));

        var real = await _service.CreateOrGet("match-1", "Real Match Name", Members(), focus: false);

        Assert.That(real.Id, Is.EqualTo(shell.Id), "the same channel (same ref)");
        Assert.That(real.Name, Is.EqualTo("Real Match Name"), "the later create backfills the placeholder name");
        Assert.That(real.ExpiresAt, Is.EqualTo(shellExpiry),
            "the name backfill does NOT reset the shell's original creation-anchored expiry");
    }

    // ============================================================================================
    // ApplyMembersDelta — removing an unknown member is a silent no-op, no push
    // ============================================================================================

    [Test]
    public async Task Delta_RemoveUnknownMember_SilentNoOp_NoPush()
    {
        const string bt = "Ghost#1";
        RegisterOnline("conn-ghost", bt);

        var channel = await _service.CreateOrGet("match-1", "Match 1", Members(), focus: false);

        // bt was never added to this channel — removing them must be a silent no-op.
        await _service.ApplyMembersDelta("match-1", add: Members(), remove: Members(bt), focus: false);

        Assert.That(_harness.AllSignals, Is.Empty, "no push is emitted for removing a member who was never present");
        Assert.That(await _membershipRepository.Load(channel.Id, bt), Is.Null);
    }

    // ============================================================================================
    // ApplyMembersDelta — add honors the one-match-channel-per-user invariant (acceptance 5, delta path)
    // ============================================================================================

    [Test]
    public async Task Delta_AddHonorsOneMatchChannelInvariant()
    {
        const string bt = "Alice#1";
        RegisterOnline("conn-alice", bt);

        var matchA = await _service.CreateOrGet("match-A", "Match A", Members(bt), focus: true);

        await _service.ApplyMembersDelta("match-B", add: Members(bt), remove: Members(), focus: true);

        var matchB = await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-B");
        Assert.That(await _membershipRepository.Load(matchA.Id, bt), Is.Null,
            "the stale match-A membership is swapped out on the delta path too");
        Assert.That(await _membershipRepository.Load(matchB.Id, bt), Is.Not.Null);

        var signals = _harness.AllSignals.Where(s => s.ConnectionId == "conn-alice").ToList();
        var removedAIndex = signals.FindIndex(s =>
            s.Method == ChatEvents.ChannelRemoved && ((ChannelRemovedDto)s.Payload).ChannelId == matchA.Id);
        var addedBIndex = signals.FindIndex(s =>
            s.Method == ChatEvents.ChannelAdded && ((ChannelAddedDto)s.Payload).Channel.Id == matchB.Id);
        Assert.That(removedAIndex, Is.GreaterThanOrEqualTo(0), "ChannelRemoved(A) was emitted");
        Assert.That(addedBIndex, Is.GreaterThanOrEqualTo(0), "ChannelAdded(B) was emitted");
        Assert.That(removedAIndex, Is.LessThan(addedBIndex), "the swap fires on the delta path too");
    }

    // ============================================================================================
    // ApplyMembersDelta — the focus flag is applied to adds
    // ============================================================================================

    [TestCase(true)]
    [TestCase(false)]
    public async Task Delta_FocusFlagAppliedToAdds(bool focus)
    {
        const string bt = "Alice#1";
        RegisterOnline("conn-alice", bt);
        await _service.CreateOrGet("match-1", "Match 1", Members(), focus: false);

        await _service.ApplyMembersDelta("match-1", add: Members(bt), remove: Members(), focus);

        var dto = _harness.PayloadFor("conn-alice", ChatEvents.ChannelAdded) as ChannelAddedDto;
        Assert.That(dto, Is.Not.Null);
        Assert.That(dto.Focus, Is.EqualTo(focus), "the focus directive is applied to adds on the delta path");
    }

    // ============================================================================================
    // ApplyMembersDelta — a battleTag in BOTH lists ends up REMOVED (adds-then-removes, deterministic;
    // mm never sends this, but the behavior must be well-defined per §3.4)
    // ============================================================================================

    [Test]
    public async Task Delta_BattleTagInBothLists_EndsUpRemoved()
    {
        const string bt = "Alice#1";
        RegisterOnline("conn-alice", bt);
        var channel = await _service.CreateOrGet("match-1", "Match 1", Members(), focus: false);

        await _service.ApplyMembersDelta("match-1", add: Members(bt), remove: Members(bt), focus: true);

        Assert.That(await _membershipRepository.Load(channel.Id, bt), Is.Null,
            "adds run first, then removes — a battleTag in both lists ends up removed");
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
}
