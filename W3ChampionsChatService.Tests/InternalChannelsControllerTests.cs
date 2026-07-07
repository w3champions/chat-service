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
        var viewersAccumulator = new ViewersAccumulator(harness.HubContext, focusRegistry);
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

        _controller = new InternalChannelsController(_matchChannelService)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    private static InternalChannelCreateRequest ValidCreateRequest(string @ref = "match-1", string name = "Match One", params string[] members) =>
        new() { Kind = "match", Ref = @ref, Name = name, Members = members.ToList() };

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
    public async Task Post_BlankName_400()
    {
        var request = ValidCreateRequest();
        request.Name = "   ";

        var result = await _controller.Create(request);

        AssertBadRequest(result);
    }

    [Test]
    public async Task Post_NameOver100Chars_400()
    {
        var request = ValidCreateRequest();
        request.Name = new string('a', 101);

        var result = await _controller.Create(request);

        AssertBadRequest(result);
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

    // ── PUT /internal/channels/{ref}/members ────────────────────────────────────────────────────

    [Test]
    public async Task Put_NullArrays_TreatedAsEmpty()
    {
        var request = new InternalMembersDeltaRequest { Add = null, Remove = null };

        var result = await _controller.UpdateMembers("match-null-arrays", request);

        Assert.That(result, Is.InstanceOf<OkResult>(), "null add/remove must be coerced to empty lists, not throw or 400");
    }

    [Test]
    public async Task Put_AddsAndRemoves_AppliesDelta()
    {
        await _controller.Create(ValidCreateRequest(@ref: "match-2", members: "Peter#123"));

        var result = await _controller.UpdateMembers("match-2", new InternalMembersDeltaRequest
        {
            Add = new List<string> { "Wanda#456" },
            Remove = new List<string> { "Peter#123" },
        });

        Assert.That(result, Is.InstanceOf<OkResult>());
        var membership = await _membershipRepository.LoadForUser("Wanda#456");
        Assert.That(membership, Is.Not.Empty, "the add must have been applied");
        var removed = await _membershipRepository.LoadForUser("Peter#123");
        Assert.That(removed, Is.Empty, "the remove must have been applied");
    }

    [TestCaseSource(nameof(InvalidRefs))]
    public async Task Put_RefWithDotSegments_400(string badRef)
    {
        var result = await _controller.UpdateMembers(badRef, new InternalMembersDeltaRequest());

        AssertBadRequest(result);
    }

    [Test]
    public async Task Put_TooManyMembers_400()
    {
        var tooMany = Enumerable.Range(0, ChatLimits.InternalMaxMembersPerCall + 1)
            .Select(i => $"Player{i}#123")
            .ToList();

        var result = await _controller.UpdateMembers("match-1", new InternalMembersDeltaRequest { Add = tooMany });

        AssertBadRequest(result);
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
