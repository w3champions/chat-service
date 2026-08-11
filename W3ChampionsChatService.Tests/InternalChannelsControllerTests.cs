using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.FanOut;
using W3ChampionsChatService.Internal;
using W3ChampionsChatService.Memberships;
using W3ChampionsChatService.Messages;
using W3ChampionsChatService.Mutes;
using W3ChampionsChatService.Sessions;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// C7 Task 9 — the HTTP surface for the match-channel lifecycle. Two groups of coverage:
/// <list type="bullet">
/// <item>BEHAVIORAL: <see cref="InternalChannelsController"/> constructed directly (mirrors
/// <c>ModerationHistoryControllerTests</c>'s no-TestServer idiom) against a REAL
/// <see cref="MatchChannelService"/> wired exactly like <c>MatchChannelServiceTests</c> — real Mongo
/// repositories on the ephemeral <see cref="IntegrationTestBase.MongoClient"/>, a real
/// <see cref="FanOutEngine"/> over the shared in-memory registries, and a deterministic
/// <see cref="FakeTimeProvider"/>.</item>
/// <item>SECURITY (H1, mandatory guardrail): a DYNAMIC reflection sweep over every controller in the
/// production assembly whose route starts with <c>internal/</c> (or lives in the
/// <see cref="InternalChannelsController"/> namespace) — this is NOT a hardcoded controller list, so a
/// FUTURE internal controller (e.g. Task 10's relationship-changes surface) added without
/// <see cref="InternalHmacAuthAttribute"/> fails this sweep automatically. Plus the specific
/// realm-disjointness checks: <see cref="InternalChannelsController"/> is Mm-only, no internal
/// controller carries <see cref="UserHasPermissionAttribute"/>, and the pre-existing JWT/ticket-realm
/// controllers carry no <see cref="InternalHmacAuthAttribute"/>.</item>
/// </list>
/// </summary>
public class InternalChannelsControllerTests : IntegrationTestBase
{
    private static readonly DateTime T0 = new(2026, 7, 5, 12, 0, 0, DateTimeKind.Utc);

    private FakeTimeProvider _time;
    private ChannelRepository _channelRepository;
    private MembershipRepository _membershipRepository;
    private MessageRepository _messageRepository;
    private MatchChannelService _matchChannelService;
    private InternalChannelsController _controller;

    [SetUp]
    public void SetupBeforeEach()
    {
        _time = new FakeTimeProvider(new DateTimeOffset(T0, TimeSpan.Zero));
        var harness = new HubPushCaptureHarness();
        var sessionRegistry = new SessionRegistry();
        var focusRegistry = new FocusRegistry();
        var onlineMemberRegistry = new OnlineMemberRegistry();
        var activityCoalescer = new ActivityCoalescer(harness.HubContext, onlineMemberRegistry);
        // The accumulator's resolver shares the SAME sessionRegistry passed to the engine below (no test
        // in this file ever registers a session or asserts on ViewersChanged content, so this is currently
        // inert either way — wired for parity with the fixture's other real-registry sharing).
        var viewersAccumulator = new ViewersAccumulator(harness.HubContext, focusRegistry, new ViewerResolver(sessionRegistry, new ConnectionMapping()));
        var fanOutEngine = new FanOutEngine(
            harness.HubContext,
            focusRegistry,
            onlineMemberRegistry,
            activityCoalescer,
            sessionRegistry,
            new PresenceInterestRegistry(),
            viewersAccumulator,
            _time);

        _channelRepository = new ChannelRepository(MongoClient);
        _membershipRepository = new MembershipRepository(MongoClient, _channelRepository);
        _messageRepository = new MessageRepository(MongoClient);
        _matchChannelService = new MatchChannelService(_channelRepository, _membershipRepository, _messageRepository, fanOutEngine, _time);

        // Task 4 added a second, sibling constructor dependency for the system-message route — this
        // file's SUT construction has to keep pace even though none of ITS tests exercise that route.
        var systemMessagePublisher = new SystemMessagePublisher(_messageRepository, _channelRepository, fanOutEngine, _time);
        _controller = new InternalChannelsController(_matchChannelService, _channelRepository, systemMessagePublisher)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    private static InternalChannelCreateRequest ValidCreateRequest(string @ref = "match-1", string name = "Match One", params string[] members) =>
        new() { Kind = "match", Ref = @ref, Name = name, Members = members.ToList() };

    private static InternalRosterAssertRequest ValidRosterRequest(
        string epoch = "e1", long seq = 1, string name = null, bool? detached = null, params string[] members) =>
        new() { Epoch = epoch, Seq = seq, Members = members.ToList(), Name = name, Detached = detached };

    private static InternalEpochSyncRequest ValidEpochSyncRequest(string epoch = "e1", params string[] liveLobbyRefs) =>
        new() { Epoch = epoch, LiveLobbyRefs = liveLobbyRefs.ToList() };

    private static void AssertBadRequest(IActionResult result)
    {
        var badRequest = result as BadRequestObjectResult;
        Assert.That(badRequest, Is.Not.Null, "validation failures must be a 400 (BadRequestObjectResult)");
        Assert.That(badRequest.Value, Is.InstanceOf<ErrorResult>(), "a 400 body must be the generic ErrorResult shape");
    }

    // ── POST /internal/channels ─────────────────────────────────────────────────────────────────

    [Test]
    public async Task Post_ValidBody_ReturnsChannelDto_WithExpiry()
    {
        var request = ValidCreateRequest(members: "Peter#123");

        var result = await _controller.Create(request) as OkObjectResult;

        Assert.That(result, Is.Not.Null, "a valid create must return 200");
        Assert.That(result.StatusCode, Is.EqualTo(200));
        var dto = result.Value as InternalChannelDto;
        Assert.That(dto, Is.Not.Null, "the 200 body must be an InternalChannelDto");
        Assert.That(dto.Ref, Is.EqualTo("match-1"));
        Assert.That(dto.Name, Is.EqualTo("Match One"));
        Assert.That(dto.Id, Is.Not.Null.And.Not.Empty);
        Assert.That(dto.ExpiresAt, Is.Not.Null, "a new match channel carries the 24h creation-anchored expiry");
    }

    [Test]
    public async Task Post_DuplicateCall_Returns200_Idempotent()
    {
        // Pinned idempotency contract: a duplicate mm POST for the same ref is 200, not a conflict.
        var first = await _controller.Create(ValidCreateRequest(members: "Peter#123")) as OkObjectResult;
        var second = await _controller.Create(ValidCreateRequest(members: "Peter#123")) as OkObjectResult;

        Assert.That(second, Is.Not.Null, "a duplicate create must ALSO return 200 (idempotent), never 409");
        var firstDto = first.Value as InternalChannelDto;
        var secondDto = second.Value as InternalChannelDto;
        Assert.That(secondDto.Id, Is.EqualTo(firstDto.Id), "duplicate resolves to the SAME channel");
    }

    [Test]
    public async Task Post_UnknownKind_400()
    {
        var request = ValidCreateRequest();
        request.Kind = "lobby";

        var result = await _controller.Create(request);

        AssertBadRequest(result);
    }

    private static IEnumerable<string> InvalidRefs()
    {
        yield return ".."; // dot-segment
        yield return "."; // dot-segment
        yield return "a/b"; // path separator
        yield return new string('a', 65); // over the 64-char cap
        yield return ""; // empty
        // Trailing-newline bypass (M1 regression guard): without RegexOptions.Multiline, .NET's `$`
        // also matches immediately before a single trailing '\n', so an anchor of ^...$ would let
        // these through despite the character class forbidding newlines — log-injection into the
        // Serilog {Ref} sink plus a distinct (polluted) Mongo systemRef key. \A...\z closes this.
        yield return "abc123\n";
        yield return "abc123\r\n";
        yield return "a\nb";
    }

    [TestCaseSource(nameof(InvalidRefs))]
    public async Task Post_RefWithDotSegments_400(string badRef)
    {
        var request = ValidCreateRequest();
        request.Ref = badRef;

        var result = await _controller.Create(request);

        AssertBadRequest(result);
    }

    [Test]
    public async Task Post_TooManyMembers_400()
    {
        var request = ValidCreateRequest();
        request.Members = Enumerable.Range(0, ChatLimits.InternalMaxMembersPerCall + 1)
            .Select(i => $"Player{i}#123")
            .ToList();

        var result = await _controller.Create(request);

        AssertBadRequest(result);
    }

    [Test]
    public async Task Post_BlankName_200_UsesRefPlaceholder()
    {
        // 2026-08-05 fix wave (final review C1): name is cosmetic and must never reject an otherwise-valid
        // create — mm applies no trim/length validation of its own before sending it.
        var request = ValidCreateRequest();
        request.Name = "   ";

        var result = await _controller.Create(request) as OkObjectResult;

        Assert.That(result, Is.Not.Null, "a whitespace-only name must normalize, never 400");
        var dto = result.Value as InternalChannelDto;
        Assert.That(dto.Name, Is.EqualTo("match-1"), "empty-after-trim falls back to the ref placeholder");
    }

    [Test]
    public async Task Post_NameOver100Chars_200_TruncatesName()
    {
        var request = ValidCreateRequest();
        request.Name = new string('a', 101);

        var result = await _controller.Create(request) as OkObjectResult;

        Assert.That(result, Is.Not.Null, "an overlong name must normalize (truncate), never 400");
        var dto = result.Value as InternalChannelDto;
        Assert.That(dto.Name, Is.EqualTo(new string('a', 100)), "clamped to the 100-char cap, not rejected");
    }

    [Test]
    public async Task Post_NullMembers_400()
    {
        var request = ValidCreateRequest();
        request.Members = null;

        var result = await _controller.Create(request);

        AssertBadRequest(result);
    }

    [Test]
    public async Task Post_BlankMemberEntry_400()
    {
        var request = ValidCreateRequest();
        request.Members = new List<string> { "Peter#123", "   " };

        var result = await _controller.Create(request);

        AssertBadRequest(result);
    }

    [Test]
    public async Task Post_MemberEntryWithControlChar_400()
    {
        // 2026-08-05 fix wave (final review M5): mirrors PutRoster_MemberEntryWithControlChar_400 —
        // IsValidMembers must reject a control-char member entry, not just a blank one.
        var request = ValidCreateRequest();
        request.Members = new List<string> { "Peter#123", "Wanda\n#456" };

        var result = await _controller.Create(request);

        AssertBadRequest(result);
    }

    // ── PUT /internal/channels/{ref}/roster ─────────────────────────────────────────────────────

    [Test]
    public async Task PutRoster_ValidBody_Returns200_AndAppliesMembership()
    {
        var request = ValidRosterRequest(members: "Peter#123");

        var result = await _controller.AssertRoster("match-1", request);

        Assert.That(result, Is.InstanceOf<OkResult>());
        var membership = await _membershipRepository.LoadForUser("Peter#123");
        Assert.That(membership, Is.Not.Empty, "the assertion must have applied the member");
    }

    [TestCaseSource(nameof(InvalidRefs))]
    public async Task PutRoster_InvalidRef_400(string badRef)
    {
        var result = await _controller.AssertRoster(badRef, ValidRosterRequest());

        AssertBadRequest(result);
    }

    [TestCaseSource(nameof(InvalidRefs))]
    public async Task PutRoster_InvalidEpoch_400(string badEpoch)
    {
        var request = ValidRosterRequest();
        request.Epoch = badEpoch;

        var result = await _controller.AssertRoster("match-1", request);

        AssertBadRequest(result);
    }

    [Test]
    public async Task PutRoster_NullMembers_400()
    {
        var request = ValidRosterRequest();
        request.Members = null;

        var result = await _controller.AssertRoster("match-1", request);

        AssertBadRequest(result);
    }

    [Test]
    public async Task PutRoster_EmptyMembers_200_AndClearsMembership()
    {
        await _controller.Create(ValidCreateRequest(@ref: "match-1", members: "Peter#123"));

        var result = await _controller.AssertRoster("match-1", ValidRosterRequest());

        Assert.That(result, Is.InstanceOf<OkResult>());
        var membership = await _membershipRepository.LoadForUser("Peter#123");
        Assert.That(membership, Is.Empty, "an empty asserted set is legal and must clear existing membership (D7)");
    }

    [Test]
    public async Task PutRoster_TooManyMembers_400()
    {
        var request = ValidRosterRequest();
        request.Members = Enumerable.Range(0, ChatLimits.InternalMaxMembersPerCall + 1)
            .Select(i => $"Player{i}#123")
            .ToList();

        var result = await _controller.AssertRoster("match-1", request);

        AssertBadRequest(result);
    }

    [Test]
    public async Task PutRoster_BlankMemberEntry_400()
    {
        var request = ValidRosterRequest();
        request.Members = new List<string> { "Peter#123", "   " };

        var result = await _controller.AssertRoster("match-1", request);

        AssertBadRequest(result);
    }

    [Test]
    public async Task PutRoster_MemberEntryWithControlChar_400()
    {
        // 2026-08-05 fix wave (final review M5): IsValidMembers is shared across routes — pin it here too.
        var request = ValidRosterRequest();
        request.Members = new List<string> { "Peter#123", "Wanda\r#456" };

        var result = await _controller.AssertRoster("match-1", request);

        AssertBadRequest(result);
    }

    [TestCase(0L)]
    [TestCase(-1L)]
    public async Task PutRoster_SeqBelowOne_400(long badSeq)
    {
        var request = ValidRosterRequest();
        request.Seq = badSeq;

        var result = await _controller.AssertRoster("match-1", request);

        AssertBadRequest(result);
    }

    [Test]
    public async Task PutRoster_UnknownRef_200_CreatesOnDemand()
    {
        var request = ValidRosterRequest(name: "Boot-Race Lobby", members: "Peter#123");

        var result = await _controller.AssertRoster("does-not-exist-ref", request);

        Assert.That(result, Is.InstanceOf<OkResult>(), "an assertion for an unknown ref must never 404");
        var channel = await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "does-not-exist-ref");
        Assert.That(channel, Is.Not.Null);
        Assert.That(channel.Name, Is.EqualTo("Boot-Race Lobby"));
    }

    [Test]
    public async Task PutRoster_StaleAssertion_Returns200()
    {
        await _controller.AssertRoster("match-1", ValidRosterRequest(epoch: "e1", seq: 5, members: "Peter#123"));

        var result = await _controller.AssertRoster("match-1", ValidRosterRequest(epoch: "e1", seq: 1, members: "Wanda#456"));

        Assert.That(result, Is.InstanceOf<OkResult>(), "a discarded stale assertion is still a 200, not an error");
        var membership = await _membershipRepository.LoadForUser("Wanda#456");
        Assert.That(membership, Is.Empty, "the stale assertion's membership must NOT have been applied");
    }

    [Test]
    public async Task PutRoster_NullBody_400()
    {
        var result = await _controller.AssertRoster("match-1", null);

        AssertBadRequest(result);
    }

    [Test]
    public async Task PutRoster_NullName_200()
    {
        var request = ValidRosterRequest(members: "Peter#123");
        request.Name = null;

        var result = await _controller.AssertRoster("brand-new-ref", request);

        Assert.That(result, Is.InstanceOf<OkResult>());
        var channel = await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "brand-new-ref");
        Assert.That(channel.Name, Is.EqualTo("brand-new-ref"), "a null name falls back to the ref placeholder on create-on-demand");
    }

    [Test]
    public async Task PutRoster_WhitespaceOnlyName_200_UsesRefPlaceholder()
    {
        // 2026-08-05 fix wave (final review C1): a name mm sends that chat cannot store must never
        // permanently wedge a lobby's chat — normalize, never reject.
        var request = ValidRosterRequest();
        request.Name = "   ";

        var result = await _controller.AssertRoster("match-1", request);

        Assert.That(result, Is.InstanceOf<OkResult>(), "a whitespace-only name must normalize, never 400");
        var channel = await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-1");
        Assert.That(channel.Name, Is.EqualTo("match-1"), "empty-after-trim falls back to the ref placeholder");
    }

    [Test]
    public async Task PutRoster_OverlongName_200_TruncatesName()
    {
        var request = ValidRosterRequest();
        request.Name = new string('a', 101);

        var result = await _controller.AssertRoster("match-1", request);

        Assert.That(result, Is.InstanceOf<OkResult>(), "an overlong name must normalize (truncate), never 400");
        var channel = await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-1");
        Assert.That(channel.Name, Is.EqualTo(new string('a', 100)), "clamped to the 100-char cap, not rejected");
    }

    // ── POST /internal/channels (D10 epoch/seq/detached) ────────────────────────────────────────

    [Test]
    public async Task PostCreate_WithEpochSeqDetached_200_AndChannelIsBornDetached()
    {
        var request = ValidCreateRequest(@ref: "ladder-1", members: "Peter#123");
        request.Epoch = "e1";
        request.Seq = 1;
        request.Detached = true;

        var result = await _controller.Create(request) as OkObjectResult;

        Assert.That(result, Is.Not.Null);
        var channel = await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "ladder-1");
        Assert.That(channel.Detached, Is.True, "a ladder-match create with detached:true must be born frozen");
        Assert.That(channel.AssertEpoch, Is.EqualTo("e1"), "the request's epoch must be stamped through to the channel");
        Assert.That(channel.AssertSeq, Is.EqualTo(1), "the request's seq must be stamped through to the channel");
        var membership = await _membershipRepository.Load(channel.Id, "Peter#123");
        Assert.That(membership, Is.Not.Null, "birth members are still added before the freeze");
    }

    [Test]
    public async Task PostCreate_EpochWithoutSeq_400()
    {
        var request = ValidCreateRequest();
        request.Epoch = "e1";

        var result = await _controller.Create(request);

        AssertBadRequest(result);
    }

    [Test]
    public async Task PostCreate_SeqWithoutEpoch_400()
    {
        var request = ValidCreateRequest();
        request.Seq = 1;

        var result = await _controller.Create(request);

        AssertBadRequest(result);
    }

    [TestCase(0L)]
    [TestCase(-1L)]
    public async Task PostCreate_SeqBelowOne_400(long badSeq)
    {
        var request = ValidCreateRequest();
        request.Epoch = "e1";
        request.Seq = badSeq;

        var result = await _controller.Create(request);

        AssertBadRequest(result);
    }

    [TestCaseSource(nameof(InvalidRefs))]
    public async Task PostCreate_InvalidEpoch_400(string badEpoch)
    {
        var request = ValidCreateRequest();
        request.Epoch = badEpoch;
        request.Seq = 1;

        var result = await _controller.Create(request);

        AssertBadRequest(result);
    }

    [Test]
    public async Task PostCreate_WithoutNewFields_200_UnchangedBehavior()
    {
        var request = ValidCreateRequest(@ref: "match-legacy", members: "Peter#123");

        var result = await _controller.Create(request) as OkObjectResult;

        Assert.That(result, Is.Not.Null);
        var channel = await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-legacy");
        Assert.That(channel.AssertEpoch, Is.Null, "no stamp is written when epoch/seq are omitted — the back-compat pin for today's mm");
        Assert.That(channel.Detached, Is.False);
        Assert.That(channel.Ladder, Is.False, "and an omitted `ladder` means custom-game lobby — the mute-exempt default");
    }

    // ── `ladder` — the send-path mute scope's discriminator ─────────────────────────────────────

    [Test]
    public async Task PostCreate_WithLadderTrue_200_AndChannelIsMarkedLadder()
    {
        var request = ValidCreateRequest(@ref: "ladder-1", members: "Peter#123");
        request.Detached = true;
        request.Ladder = true;

        var result = await _controller.Create(request) as OkObjectResult;

        Assert.That(result, Is.Not.Null);
        var channel = await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "ladder-1");
        Assert.That(channel.Ladder, Is.True);
        Assert.That(ChannelModeration.IsMuteEnforced(channel), Is.True,
            "which is the whole point: a lounge-muted player is silenced in a ladder match room");
    }

    [Test]
    public async Task PostCreate_WithDetachedButNoLadder_200_AndChannelIsNotMuteGated()
    {
        // `detached` is NOT a ladder signal — mm sets it on every custom lobby at game start too. A
        // custom lobby's post-game room must stay mute-exempt.
        var request = ValidCreateRequest(@ref: "lobby-1", members: "Peter#123");
        request.Detached = true;

        await _controller.Create(request);

        var channel = await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "lobby-1");
        Assert.That(channel.Detached, Is.True);
        Assert.That(ChannelModeration.IsMuteEnforced(channel), Is.False,
            "detach must never be inferred as ladder-ness — the two answer different questions");
    }

    [Test]
    public async Task PutRoster_WithLadderTrue_200_AndChannelIsMarkedLadder()
    {
        // mm's ladder create-failure fallback lands here, and this assertion may itself create the room.
        var request = ValidRosterRequest(detached: true, members: "Peter#123");
        request.Ladder = true;

        var result = await _controller.AssertRoster("ladder-1", request) as OkResult;

        Assert.That(result, Is.Not.Null);
        var channel = await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "ladder-1");
        Assert.That(channel.Ladder, Is.True);
    }

    // ── POST /internal/channels/epoch-sync ──────────────────────────────────────────────────────

    // 2026-08-05 fix wave (final review H1, plan D8 amendment): the sweep only considers channels
    // already stamped by the assertion protocol (AssertEpoch exists), so every seed below that must be
    // torn down carries epoch/seq on its create — an unstamped channel would be invisible to the sweep
    // entirely and would survive regardless of liveLobbyRefs (see the dedicated survives-test below).
    private static InternalChannelCreateRequest StampedCreateRequest(string @ref, params string[] members)
    {
        var request = ValidCreateRequest(@ref: @ref, members: members);
        request.Epoch = "e1";
        request.Seq = 1;
        return request;
    }

    [Test]
    public async Task PostEpochSync_ValidBody_Returns200_AndTearsDownUnlistedChannels()
    {
        await _controller.Create(StampedCreateRequest("match-live", "Peter#123"));
        await _controller.Create(StampedCreateRequest("match-dead", "Wanda#456"));

        var result = await _controller.EpochSync(ValidEpochSyncRequest(liveLobbyRefs: "match-live"));

        Assert.That(result, Is.InstanceOf<OkResult>());
        Assert.That(await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-live"), Is.Not.Null);
        Assert.That(await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-dead"), Is.Null);
    }

    [Test]
    public async Task PostEpochSync_EmptyLiveRefs_Returns200_AndTearsDownEverythingNonDetached()
    {
        await _controller.Create(StampedCreateRequest("match-1", "Peter#123"));

        var result = await _controller.EpochSync(ValidEpochSyncRequest());

        Assert.That(result, Is.InstanceOf<OkResult>());
        Assert.That(await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-1"), Is.Null,
            "an empty live list (the post-crash case) tears down every non-detached, assertion-stamped match channel");
    }

    [Test]
    public async Task PostEpochSync_UnstampedChannel_Returns200_AndSurvives_FallsToTtl()
    {
        // A channel created WITHOUT epoch/seq and never since asserted must be invisible to the sweep.
        await _controller.Create(ValidCreateRequest(@ref: "match-unstamped", members: "Peter#123"));

        var result = await _controller.EpochSync(ValidEpochSyncRequest());

        Assert.That(result, Is.InstanceOf<OkResult>());
        Assert.That(await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-unstamped"), Is.Not.Null,
            "an unstamped channel survives an epoch sync even when absent from liveLobbyRefs — it falls to its own 24h TTL instead");
    }

    [Test]
    public async Task PostEpochSync_NullLiveRefs_400()
    {
        var result = await _controller.EpochSync(new InternalEpochSyncRequest { Epoch = "e1", LiveLobbyRefs = null });

        AssertBadRequest(result);
    }

    [TestCaseSource(nameof(InvalidRefs))]
    public async Task PostEpochSync_InvalidRefInLiveList_400(string badRef)
    {
        await _controller.Create(ValidCreateRequest(@ref: "match-1", members: "Peter#123"));

        var result = await _controller.EpochSync(new InternalEpochSyncRequest { Epoch = "e1", LiveLobbyRefs = new List<string> { badRef } });

        AssertBadRequest(result);
        Assert.That(await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-1"), Is.Not.Null,
            "validation must run BEFORE any domain call — an invalid entry must not trigger a partial teardown");
    }

    [TestCaseSource(nameof(InvalidRefs))]
    public async Task PostEpochSync_InvalidEpoch_400(string badEpoch)
    {
        var result = await _controller.EpochSync(new InternalEpochSyncRequest { Epoch = badEpoch, LiveLobbyRefs = new List<string>() });

        AssertBadRequest(result);
    }

    [Test]
    public async Task PostEpochSync_TooManyLiveRefs_400()
    {
        var tooMany = Enumerable.Range(0, ChatLimits.InternalMaxLiveRefsPerSync + 1)
            .Select(i => $"ref{i}")
            .ToList();

        var result = await _controller.EpochSync(new InternalEpochSyncRequest { Epoch = "e1", LiveLobbyRefs = tooMany });

        AssertBadRequest(result);
    }

    [Test]
    public async Task PostEpochSync_NullBody_400()
    {
        var result = await _controller.EpochSync(null);

        AssertBadRequest(result);
    }

    [Test]
    public void PostEpochSync_RouteDoesNotShadowCreateOrDelete()
    {
        // Cheap regression guard (Task 5): proves the actions carry distinct [Http*] route templates
        // rather than reasoning about ASP.NET's routing table by inspection alone.
        var createRoute = typeof(InternalChannelsController)
            .GetMethod(nameof(InternalChannelsController.Create))
            .GetCustomAttribute<HttpPostAttribute>()?.Template;
        var deleteRoute = typeof(InternalChannelsController)
            .GetMethod(nameof(InternalChannelsController.Delete))
            .GetCustomAttribute<HttpDeleteAttribute>()?.Template;
        var epochSyncRoute = typeof(InternalChannelsController)
            .GetMethod(nameof(InternalChannelsController.EpochSync))
            .GetCustomAttribute<HttpPostAttribute>()?.Template;
        var assertRosterRoute = typeof(InternalChannelsController)
            .GetMethod(nameof(InternalChannelsController.AssertRoster))
            .GetCustomAttribute<HttpPutAttribute>()?.Template;

        Assert.That(createRoute, Is.Null.Or.Empty, "Create is the root POST — no template, distinct from epoch-sync");
        Assert.That(deleteRoute, Is.EqualTo("{ref}"), "Delete's template is a different verb+template from EpochSync's POST epoch-sync");
        Assert.That(epochSyncRoute, Is.EqualTo("epoch-sync"));
        Assert.That(assertRosterRoute, Is.EqualTo("{ref}/roster"));
    }

    // ── DELETE /internal/channels/{ref} ─────────────────────────────────────────────────────────

    [Test]
    public async Task Delete_UnknownRef_200()
    {
        var result = await _controller.Delete("does-not-exist-ref");

        Assert.That(result, Is.InstanceOf<OkResult>(), "deleting an unknown ref must be a no-op 200, never 404");
    }

    [Test]
    public async Task Delete_ExistingRef_RemovesChannel()
    {
        await _controller.Create(ValidCreateRequest(@ref: "match-3", members: "Peter#123"));

        var result = await _controller.Delete("match-3");

        Assert.That(result, Is.InstanceOf<OkResult>());
        var loaded = await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-3");
        Assert.That(loaded, Is.Null, "the channel must be hard-deleted");
    }

    [TestCaseSource(nameof(InvalidRefs))]
    public async Task Delete_RefWithDotSegments_400(string badRef)
    {
        var result = await _controller.Delete(badRef);

        AssertBadRequest(result);
    }

    // ── H1 SECURITY GUARDRAIL — dynamic realm-disjointness sweep ────────────────────────────────

    /// <summary>
    /// Every controller whose route starts with <c>internal/</c> OR lives in
    /// <see cref="InternalChannelsController"/>'s namespace — enumerated DYNAMICALLY off the compiled
    /// production assembly (never a hardcoded type list), so a FUTURE internal controller lacking the
    /// attribute fails this sweep without anyone remembering to update a test.
    /// </summary>
    private static List<Type> DiscoverInternalControllerTypes() =>
        typeof(Startup).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t))
            .Where(IsInternalController)
            .ToList();

    // ASSUMPTION (documented, not enforced by this method): this only inspects a CLASS-LEVEL
    // [Route] attribute. A future internal controller that declared its "internal/..." path solely
    // via an action-level [HttpGet("internal/...")] AND lived outside the InternalChannelsController
    // namespace would slip past the routesUnderInternal check. The namespace check is the intended
    // backstop — internal controllers are expected to live in the Internal namespace and/or carry a
    // class-level internal/ [Route], per this sweep's design.
    private static bool IsInternalController(Type controllerType)
    {
        var routeTemplate = controllerType.GetCustomAttribute<RouteAttribute>()?.Template;
        var routesUnderInternal = routeTemplate != null
            && routeTemplate.StartsWith("internal/", StringComparison.OrdinalIgnoreCase);
        var livesInInternalNamespace = controllerType.Namespace == typeof(InternalChannelsController).Namespace;
        return routesUnderInternal || livesInInternalNamespace;
    }

    [Test]
    public void InternalControllers_DeclareHmacAttributeAtClassLevel_WithMmAllowList()
    {
        var internalControllerTypes = DiscoverInternalControllerTypes();

        Assert.That(internalControllerTypes, Is.Not.Empty,
            "sanity: InternalChannelsController itself must be discovered, or this sweep proves nothing");

        foreach (var type in internalControllerTypes)
        {
            var attribute = type.GetCustomAttribute<InternalHmacAuthAttribute>();

            Assert.That(attribute, Is.Not.Null,
                $"{type.Name} routes under internal/ but does not declare [InternalHmacAuth] at class level — every internal/* controller MUST be HMAC-gated (H1)");
            Assert.That(attribute.AllowedCallers, Is.Not.Empty,
                $"{type.Name}'s [InternalHmacAuth] allow-list must be non-empty — an empty allow-list rejects every caller and signals a misconfiguration, not intent");
        }

        var channelsAttribute = typeof(InternalChannelsController).GetCustomAttribute<InternalHmacAuthAttribute>();
        Assert.That(channelsAttribute.AllowedCallers, Is.EqualTo(new[] { InternalCaller.Mm }),
            "InternalChannelsController must allow EXACTLY Mm (least privilege)");
    }

    [Test]
    public void InternalControllers_CarryNoUserHasPermissionAttribute()
    {
        foreach (var type in DiscoverInternalControllerTypes())
        {
            Assert.That(type.GetCustomAttribute<UserHasPermissionAttribute>(), Is.Null,
                $"{type.Name} must not carry [UserHasPermission] — internal/* controllers live in the HMAC realm, not the JWT/permission realm");

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.That(method.GetCustomAttribute<UserHasPermissionAttribute>(), Is.Null,
                    $"{type.Name}.{method.Name} must not carry [UserHasPermission] — the two auth realms must stay disjoint");
            }
        }
    }

    [TestCase(typeof(ModerationHistoryController))]
    [TestCase(typeof(MuteController))]
    [TestCase(typeof(AuthSessionController))]
    public void ExistingControllers_CarryNoInternalHmacAttribute(Type controllerType)
    {
        Assert.That(controllerType.GetCustomAttribute<InternalHmacAuthAttribute>(), Is.Null,
            $"{controllerType.Name} lives in the JWT/ticket auth realm and must never carry [InternalHmacAuth] — the two realms must stay disjoint");
    }
}
