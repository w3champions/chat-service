using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.FanOut;
using W3ChampionsChatService.Internal;
using W3ChampionsChatService.Memberships;
using W3ChampionsChatService.Mentions;
using W3ChampionsChatService.Messages;
using W3ChampionsChatService.Mutes;
using W3ChampionsChatService.Protocol;
using W3ChampionsChatService.Relationships;
using W3ChampionsChatService.Sessions;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// C7 Task 11 — the end-to-end acceptance matrix proving all eight C7 acceptance criteria against the
/// REAL stack: real Testcontainers Mongo (<see cref="IntegrationTestBase"/>), a real
/// <see cref="FanOutEngine"/> over the real in-memory registries, the real <see cref="MatchChannelService"/>,
/// the real <see cref="InternalChannelsController"/> / <see cref="InternalRelationshipChangesController"/>,
/// and — critically — the REAL <see cref="InternalHmacAuthFilter"/>, driven exactly as production would:
/// every request in this file is a hand-signed raw JSON body run THROUGH the filter
/// (<c>OnResourceExecutionAsync</c> against a <see cref="ResourceExecutingContext"/>/<see cref="ResourceExecutionDelegate"/>
/// — the same no-TestServer idiom <see cref="InternalHmacAuthFilterTests"/> established) and, on a pass,
/// the rewound body is deserialized with System.Text.Json and handed to the real controller action. This
/// is the exact production byte-path minus Kestrel (the repo deliberately has no TestServer/WebApplicationFactory).
/// <para>
/// MOCKED: only <see cref="IRelationshipProvider"/> (a Moq double — C7 does not own the cache itself, only
/// the invalidation call, per <see cref="InternalRelationshipChangesControllerTests"/>'s own idiom) and the
/// <see cref="IHubContext{ChatHub}"/>/SignalR transport (<see cref="HubPushCaptureHarness"/> — no live
/// WebSocket exists in a unit-test process; every OTHER layer between the raw bytes and Mongo is real).
/// </para>
/// </summary>
public class InternalApiIntegrationTests : IntegrationTestBase
{
    // The shared clock starts EXACTLY at the pinned vectors' signing instant (unix 1751500000) rather
    // than an arbitrary "today" — FakeTimeProvider is monotonic (SetUtcNow/Advance cannot go backward),
    // and Hmac_PinnedVectors_AcceptedEndToEnd shares this SAME clock with the real MatchChannelService
    // it drives (so the created channel's 24h expiry is anchored to a believable "now"). Every other
    // test in this file only needs a fixed, self-consistent starting point — the actual calendar value
    // is otherwise irrelevant to them.
    private static readonly DateTime T0 = DateTimeOffset.FromUnixTimeSeconds(1751500000).UtcDateTime;

    private const string TimestampHeaderName = "X-W3C-Webhook-Timestamp";
    private const string SignatureHeaderName = "X-W3C-Signature";

    // Two DISTINCT per-caller secrets — InternalCallerSecrets throws if they are equal (Task 1-3
    // security-fix), and a genuinely distinct pair is required for the cross-caller least-privilege test.
    private const string MmSecret = "test-secret";
    private const string WbSecret = "wb-secret-distinct-9f3";

    // ════════════════════════════════════════════════════════════════════════════════════════════
    // §0 PUBLISHED HMAC TEST VECTORS — FOR M1/W2 REUSE (byte-for-byte, reused verbatim from Task 2's
    // HmacSignatureVerifierTests / Task 3's InternalHmacAuthFilterTests; republished here because this
    // suite is the one that proves them end-to-end through the REAL filter + REAL controller).
    //
    //   secret    = "test-secret"
    //   timestamp = "1751500000"                      (X-W3C-Webhook-Timestamp, unix seconds)
    //
    //   CREATE rawBody:
    //     {"kind":"match","ref":"abc123XYZ0","name":"Test Lobby","members":["Foo#1234","Bar#5678"]}
    //   CREATE X-W3C-Signature:
    //     v1=b0acb9b2ba23a8aaf0076c05cd1c9631ac88364dfcebe61352c220f9009e54cd
    //
    //   Empty-body DELETE (signing string "v1.1751500000." with rawBody = ""):
    //   DELETE X-W3C-Signature:
    //     v1=09b6a138e0b80b2d6c4fa412590abcc352953b7e43ba15479020161e944f47a3
    //
    //   Scheme: X-W3C-Signature: "v1=" + hex(HMAC_SHA256(key = UTF8(secret),
    //           msg = UTF8("v1." + timestamp + ".") ++ rawBodyBytes)); hex is lowercase but verified
    //           case-insensitively (Task 2 coordinator override — W2 is C#, Convert.ToHexString emits
    //           uppercase). Freshness window ±300s (ChatLimits.InternalSignatureFreshnessWindow), inclusive.
    // ════════════════════════════════════════════════════════════════════════════════════════════
    private const string PinnedTimestamp = "1751500000";
    private const string PinnedCreateBody =
        "{\"kind\":\"match\",\"ref\":\"abc123XYZ0\",\"name\":\"Test Lobby\",\"members\":[\"Foo#1234\",\"Bar#5678\"]}";
    private const string PinnedCreateSignature =
        "v1=b0acb9b2ba23a8aaf0076c05cd1c9631ac88364dfcebe61352c220f9009e54cd";
    private const string PinnedDeleteSignature =
        "v1=09b6a138e0b80b2d6c4fa412590abcc352953b7e43ba15479020161e944f47a3";

    private static readonly DateTimeOffset PinnedInstant = DateTimeOffset.FromUnixTimeSeconds(1751500000);
    private static readonly TimeSpan Window = ChatLimits.InternalSignatureFreshnessWindow;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // Resolved from the REAL attributes (not a hand-maintained duplicate list) so this suite tracks the
    // production allow-list verbatim — if either controller's attribute ever changes, these follow it.
    private static readonly InternalCaller[] MmOnlyAllowed =
        typeof(InternalChannelsController).GetCustomAttribute<InternalHmacAuthAttribute>()!.AllowedCallers.ToArray();
    private static readonly InternalCaller[] WbOnlyAllowed =
        typeof(InternalRelationshipChangesController).GetCustomAttribute<InternalHmacAuthAttribute>()!.AllowedCallers.ToArray();

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
    private MatchChannelService _matchChannelService;
    private InternalCallerSecrets _secrets;
    private InternalChannelsController _channelsController;
    private Mock<IRelationshipProvider> _relationshipProvider;
    private InternalRelationshipChangesController _relationshipController;
    private MuteRepository _muteRepository;
    private ConnectionMapping _connectionMapping;
    private MentionInboxRepository _mentionInboxRepository;
    private SessionStateAssembler _sessionStateAssembler;

    [SetUp]
    public void SetupBeforeEach()
    {
        _time = new FakeTimeProvider(new DateTimeOffset(T0, TimeSpan.Zero));
        _harness = new HubPushCaptureHarness();
        _sessionRegistry = new SessionRegistry();
        _focusRegistry = new FocusRegistry();
        _onlineMemberRegistry = new OnlineMemberRegistry();
        _connectionMapping = new ConnectionMapping();
        _activityCoalescer = new ActivityCoalescer(_harness.HubContext, _onlineMemberRegistry);
        _viewersAccumulator = new ViewersAccumulator(
            _harness.HubContext, _focusRegistry, new ViewerResolver(_sessionRegistry, _connectionMapping));
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
        _matchChannelService = new MatchChannelService(_channelRepository, _membershipRepository, _messageRepository, _fanOutEngine, _time);

        // The REAL InternalCallerSecrets registry — both callers configured with DISTINCT secrets, the
        // exact env-only seam Startup.cs builds in production.
        _secrets = new InternalCallerSecrets(MmSecret, WbSecret);

        // Task 4's SystemMessagePublisher is wired over the SAME repositories/registries as everything
        // else in this suite — no mock, the real publish→persist→fan-out path.
        var systemMessagePublisher = new SystemMessagePublisher(_messageRepository, _channelRepository, _fanOutEngine, _time);
        _channelsController = new InternalChannelsController(_matchChannelService, _channelRepository, systemMessagePublisher)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        _relationshipProvider = new Mock<IRelationshipProvider>();
        _relationshipController = new InternalRelationshipChangesController(_relationshipProvider.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        // The offline half of acceptance 3 — a real SessionStateAssembler over the SAME repositories/registries.
        _muteRepository = new MuteRepository(MongoClient);
        _mentionInboxRepository = new MentionInboxRepository(MongoClient);
        _sessionStateAssembler = new SessionStateAssembler(
            _membershipRepository,
            _channelRepository,
            _messageRepository,
            _muteRepository,
            _onlineMemberRegistry,
            _connectionMapping,
            _mentionInboxRepository);
    }

    // ────────────────────────────────────────────────────────────────────────────────────────────
    // Signing + filter-invocation harness — builds a hand-signed DefaultHttpContext, runs the REAL
    // InternalHmacAuthFilter against it (the exact idiom InternalHmacAuthFilterTests established), and —
    // only on a pass — hands the rewound raw body to System.Text.Json and invokes the real controller
    // action. This is the "signed DefaultHttpContext → real filter → real controller" byte-path the
    // brief specifies (the production path minus Kestrel).
    // ────────────────────────────────────────────────────────────────────────────────────────────

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    /// <summary>Reproduces the PRODUCTION signing math independently (never delegates to
    /// HmacSignatureVerifier) — this is the caller-side (mm/wb) computation the filter must accept.</summary>
    private static string SignBody(string secret, string timestamp, byte[] rawBody)
    {
        var prefix = Utf8($"v1.{timestamp}.");
        var message = new byte[prefix.Length + rawBody.Length];
        Buffer.BlockCopy(prefix, 0, message, 0, prefix.Length);
        Buffer.BlockCopy(rawBody, 0, message, prefix.Length, rawBody.Length);
        var mac = HMACSHA256.HashData(Utf8(secret), message);
        return "v1=" + Convert.ToHexString(mac).ToLowerInvariant();
    }

    private string NowTimestamp() => _time.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    private static DefaultHttpContext BuildHttpContext(byte[] body, string timestamp, string signature, string method, string path)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        http.Request.Path = path;
        if (timestamp != null)
        {
            http.Request.Headers[TimestampHeaderName] = timestamp;
        }
        if (signature != null)
        {
            http.Request.Headers[SignatureHeaderName] = signature;
        }
        http.Request.Body = new System.IO.MemoryStream(body);
        return http;
    }

    /// <summary>Runs the REAL <see cref="InternalHmacAuthFilter"/> — the auth-realm boundary — against
    /// the given context. Defaults to sharing THIS test's <see cref="_time"/> (the same TimeProvider seam
    /// the filter and the business logic share in production DI); a scenario that needs a clock reading
    /// EARLIER than the shared monotonic <see cref="_time"/>'s current value (e.g. a "future timestamp"
    /// rejection, which requires the filter's "now" to sit BEFORE the signed timestamp) passes its own
    /// dedicated <paramref name="timeProviderOverride"/> instead — mirroring
    /// <see cref="InternalHmacAuthFilterTests"/>'s per-scenario <c>new FakeTimeProvider(now)</c> idiom.
    /// Returns whether the request passed (next() was invoked and no short-circuit Result was set) — a
    /// false return IS the production 401.</summary>
    private async Task<bool> RunFilterAsync(DefaultHttpContext http, InternalCaller[] allowedCallers, TimeProvider timeProviderOverride = null)
    {
        var filter = new InternalHmacAuthFilter(_secrets, timeProviderOverride ?? _time) { AllowedCallers = allowedCallers };
        var actionContext = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var ctx = new ResourceExecutingContext(actionContext, new List<IFilterMetadata>(), new List<IValueProviderFactory>());

        var nextCalled = false;
        ResourceExecutionDelegate next = () =>
        {
            nextCalled = true;
            return Task.FromResult(new ResourceExecutedContext(actionContext, ctx.Filters));
        };

        await filter.OnResourceExecutionAsync(ctx, next);
        return nextCalled && ctx.Result == null;
    }

    private async Task<(bool Passed, DefaultHttpContext Http)> ThroughFilter(
        string method, string path, byte[] body, string secret, string timestamp, InternalCaller[] allowedCallers)
    {
        var signature = SignBody(secret, timestamp, body);
        var http = BuildHttpContext(body, timestamp, signature, method, path);
        var passed = await RunFilterAsync(http, allowedCallers);
        return (passed, http);
    }

    private async Task<IActionResult> PostChannelsCreate(string bodyJson, string secret, string timestamp, InternalCaller[] allowed = null)
    {
        var (passed, http) = await ThroughFilter("POST", "/internal/channels", Utf8(bodyJson), secret, timestamp, allowed ?? MmOnlyAllowed);
        if (!passed)
        {
            return new UnauthorizedResult();
        }

        var dto = await JsonSerializer.DeserializeAsync<InternalChannelCreateRequest>(http.Request.Body, JsonOptions);
        _channelsController.ControllerContext.HttpContext = http;
        return await _channelsController.Create(dto);
    }

    private async Task<IActionResult> DeleteChannelThroughFilter(string @ref, string secret, string timestamp, InternalCaller[] allowed = null)
    {
        var (passed, http) = await ThroughFilter("DELETE", $"/internal/channels/{@ref}", Array.Empty<byte>(), secret, timestamp, allowed ?? MmOnlyAllowed);
        if (!passed)
        {
            return new UnauthorizedResult();
        }

        _channelsController.ControllerContext.HttpContext = http;
        return await _channelsController.Delete(@ref);
    }

    // 2026-08-05 reconciliation spec, Task 6 — the same hand-signed-through-the-real-filter idiom as the
    // two helpers above, for the roster-assertion and epoch-sync endpoints.

    private async Task<IActionResult> PutChannelRoster(string @ref, string bodyJson, string secret, string timestamp, InternalCaller[] allowed = null)
    {
        var (passed, http) = await ThroughFilter("PUT", $"/internal/channels/{@ref}/roster", Utf8(bodyJson), secret, timestamp, allowed ?? MmOnlyAllowed);
        if (!passed)
        {
            return new UnauthorizedResult();
        }

        var dto = await JsonSerializer.DeserializeAsync<InternalRosterAssertRequest>(http.Request.Body, JsonOptions);
        _channelsController.ControllerContext.HttpContext = http;
        return await _channelsController.AssertRoster(@ref, dto);
    }

    private async Task<IActionResult> PostEpochSync(string bodyJson, string secret, string timestamp, InternalCaller[] allowed = null)
    {
        var (passed, http) = await ThroughFilter("POST", "/internal/channels/epoch-sync", Utf8(bodyJson), secret, timestamp, allowed ?? MmOnlyAllowed);
        if (!passed)
        {
            return new UnauthorizedResult();
        }

        var dto = await JsonSerializer.DeserializeAsync<InternalEpochSyncRequest>(http.Request.Body, JsonOptions);
        _channelsController.ControllerContext.HttpContext = http;
        return await _channelsController.EpochSync(dto);
    }

    // Task 4 — the system-message publish route, same hand-signed-through-the-real-filter idiom as the
    // create/roster/epoch-sync helpers above.
    private async Task<IActionResult> PostSystemMessage(string @ref, string bodyJson, string secret, string timestamp, InternalCaller[] allowed = null)
    {
        var (passed, http) = await ThroughFilter("POST", $"/internal/channels/{@ref}/system-message", Utf8(bodyJson), secret, timestamp, allowed ?? MmOnlyAllowed);
        if (!passed)
        {
            return new UnauthorizedResult();
        }

        var dto = await JsonSerializer.DeserializeAsync<InternalSystemMessageRequest>(http.Request.Body, JsonOptions);
        _channelsController.ControllerContext.HttpContext = http;
        return await _channelsController.PublishSystemMessage(@ref, dto);
    }

    private async Task<IActionResult> PostRelationshipChange(string bodyJson, string secret, string timestamp, InternalCaller[] allowed = null)
    {
        var (passed, http) = await ThroughFilter("POST", "/internal/relationship-changes", Utf8(bodyJson), secret, timestamp, allowed ?? WbOnlyAllowed);
        if (!passed)
        {
            return new UnauthorizedResult();
        }

        var dto = await JsonSerializer.DeserializeAsync<InternalRelationshipChangeRequest>(http.Request.Body, JsonOptions);
        _relationshipController.ControllerContext.HttpContext = http;
        return _relationshipController.Post(dto);
    }

    private void RegisterOnline(string connectionId, string battleTag) =>
        _sessionRegistry.Register(
            connectionId,
            new W3CUserAuthentication { BattleTag = battleTag, Name = battleTag.Split('#')[0] },
            null);

    private static ChatUser ChatUserFor(string battleTag) => new(
        battleTag, false, null,
        new ProfilePicture { Race = AvatarCategory.HU, PictureId = 1, IsClassic = true },
        new ChatColor("chat_color_red"),
        Array.Empty<ChatIcon>());

    // ════════════════════════════════════════════════════════════════════════════════════════════
    // ACCEPTANCE 1 — signature matrix + published vectors, driven THROUGH the real filter.
    // ════════════════════════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Hmac_PinnedVectors_AcceptedEndToEnd()
    {
        // The shared clock (T0) already starts EXACTLY at PinnedInstant (see T0's doc comment) — no
        // SetUtcNow needed; Δ == 0, trivially inside the freshness window.
        Assert.That(_time.GetUtcNow(), Is.EqualTo(PinnedInstant));

        // Self-check: this test's OWN signing math (independent of production) reproduces the
        // published vectors byte-for-byte — proves the vectors above are genuinely correct, not just
        // copy-pasted.
        Assert.That(SignBody(MmSecret, PinnedTimestamp, Utf8(PinnedCreateBody)), Is.EqualTo(PinnedCreateSignature));
        Assert.That(SignBody(MmSecret, PinnedTimestamp, Array.Empty<byte>()), Is.EqualTo(PinnedDeleteSignature));

        // CREATE — the pinned vector, through the REAL filter, then the REAL controller.
        var createResult = await PostChannelsCreate(PinnedCreateBody, MmSecret, PinnedTimestamp) as OkObjectResult;
        Assert.That(createResult, Is.Not.Null, "the pinned CREATE vector must verify end-to-end and reach the controller");
        var dto = createResult.Value as InternalChannelDto;
        Assert.That(dto, Is.Not.Null);
        Assert.That(dto.Ref, Is.EqualTo("abc123XYZ0"));
        Assert.That(dto.Name, Is.EqualTo("Test Lobby"));
        Assert.That(await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "abc123XYZ0"), Is.Not.Null,
            "the channel is durably persisted by the real MatchChannelService/ChannelRepository");
        Assert.That((await _membershipRepository.LoadForUser("Foo#1234")).Any(m => m.ChannelId == dto.Id), Is.True,
            "the pinned vector's members are genuinely added");

        // DELETE — the pinned empty-body vector, same secret/timestamp, tearing down the just-created channel.
        var deleteResult = await DeleteChannelThroughFilter("abc123XYZ0", MmSecret, PinnedTimestamp);
        Assert.That(deleteResult, Is.InstanceOf<OkResult>(), "the pinned empty-body DELETE vector must verify end-to-end");
        Assert.That(await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "abc123XYZ0"), Is.Null,
            "the real DeleteChannel hard-teardown ran");
    }

    public enum RejectionScenario
    {
        WrongSecret,
        TamperedBody,
        Stale,
        Future,
        ReplayOutOfWindow,
    }

    [TestCase(RejectionScenario.WrongSecret)]
    [TestCase(RejectionScenario.TamperedBody)]
    [TestCase(RejectionScenario.Stale)]
    [TestCase(RejectionScenario.Future)]
    [TestCase(RejectionScenario.ReplayOutOfWindow)]
    public async Task Hmac_RejectionMatrix_All401(RejectionScenario scenario)
    {
        var body = Utf8(PinnedCreateBody);
        var signingSecret = scenario == RejectionScenario.WrongSecret ? "not-the-real-secret" : MmSecret;
        var signature = SignBody(signingSecret, PinnedTimestamp, body);

        // Tamper AFTER signing, so the MAC no longer matches the bytes actually sent.
        if (scenario == RejectionScenario.TamperedBody)
        {
            body[10] ^= 0x01;
        }

        var now = scenario switch
        {
            RejectionScenario.Stale => PinnedInstant + Window + TimeSpan.FromSeconds(1),
            RejectionScenario.Future => PinnedInstant - Window - TimeSpan.FromSeconds(1),
            // A captured, genuinely-valid request replayed long after the window has elapsed —
            // conceptually distinct from "stale" (clock skew) even though it hits the same freshness
            // gate: there is no nonce/replay cache, so a stale check IS the replay defense.
            RejectionScenario.ReplayOutOfWindow => PinnedInstant + Window + TimeSpan.FromHours(6),
            _ => PinnedInstant,
        };

        // A DEDICATED clock for this scenario (never the shared, monotonic _time) — "Future" needs a
        // "now" EARLIER than the pinned signing instant, which a monotonic FakeTimeProvider cannot do
        // via SetUtcNow/Advance. Mirrors InternalHmacAuthFilterTests' per-scenario `new FakeTimeProvider(now)`.
        var http = BuildHttpContext(body, PinnedTimestamp, signature, "POST", "/internal/channels");
        var passed = await RunFilterAsync(http, MmOnlyAllowed, new FakeTimeProvider(now));

        Assert.That(passed, Is.False, $"{scenario} must be rejected (401) by the real filter");
    }

    [Test]
    public async Task CrossCaller_WbSecretOnChannelsEndpoint_401()
    {
        // A cryptographically VALID wb signature (verifies against the registered Wb secret) presented
        // to the Mm-only /internal/channels filter — the caller resolves as Wb, but Wb is not in the
        // Mm-only allow-list (read off the REAL attribute), so the real filter must still reject with
        // 401 (least privilege / auth-realm disjointness, H1).
        var body = "{\"kind\":\"match\",\"ref\":\"cross-caller\",\"name\":\"Cross Caller\",\"members\":[]}";

        var result = await PostChannelsCreate(body, WbSecret, NowTimestamp(), MmOnlyAllowed);

        Assert.That(result, Is.InstanceOf<UnauthorizedResult>());
        Assert.That(await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "cross-caller"), Is.Null,
            "a rejected request must never reach the controller/DB");
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════
    // ACCEPTANCE 2 — duplicate POST is idempotent: same channel, no duplicate memberships, expiry unchanged.
    // ════════════════════════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task DuplicateCreate_ReturnsExisting_NoDupMemberships_ExpiryNotReset()
    {
        const string bt = "Alice#1";
        RegisterOnline("conn-alice", bt);
        var body = "{\"kind\":\"match\",\"ref\":\"dup-1\",\"name\":\"Dup Match\",\"members\":[\"Alice#1\"]}";

        var first = await PostChannelsCreate(body, MmSecret, NowTimestamp()) as OkObjectResult;
        Assert.That(first, Is.Not.Null);
        var firstDto = first.Value as InternalChannelDto;

        // The clock genuinely moves between the two POSTs — a re-get must NOT re-anchor the expiry.
        _time.Advance(TimeSpan.FromHours(1));

        var second = await PostChannelsCreate(body, MmSecret, NowTimestamp()) as OkObjectResult;
        Assert.That(second, Is.Not.Null, "a duplicate mm POST must ALSO return 200 (idempotent), never 409/500");
        var secondDto = second.Value as InternalChannelDto;

        Assert.That(secondDto.Id, Is.EqualTo(firstDto.Id), "the duplicate resolves to the SAME channel");
        Assert.That(secondDto.ExpiresAt, Is.EqualTo(firstDto.ExpiresAt), "the 24h creation-anchored expiry is NOT reset on re-get");

        var memberships = await _membershipRepository.LoadForUser(bt);
        Assert.That(memberships.Count(m => m.ChannelId == firstDto.Id), Is.EqualTo(1), "no duplicate membership row");
        Assert.That(_harness.SignalCount("conn-alice", ChatEvents.ChannelAdded), Is.EqualTo(1), "the idempotent re-add does not re-push");
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════
    // ACCEPTANCE 3 — create-with-members: the ONLINE half gets ChannelAdded (focus honored); the
    // OFFLINE half sees the channel in their next SessionState (via the REAL SessionStateAssembler).
    // ════════════════════════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CreateWithMembers_OnlinePushedWithFocus_OfflineSeeSessionState()
    {
        const string online = "OnlineAC3#1";
        const string offline = "OfflineAC3#2";
        RegisterOnline("conn-online", online);
        // offline is deliberately NEVER registered online — no live connection.

        var body = "{\"kind\":\"match\",\"ref\":\"match-ac3\",\"name\":\"AC3 Match\","
            + "\"members\":[\"OnlineAC3#1\",\"OfflineAC3#2\"],\"focus\":true}";

        var result = await PostChannelsCreate(body, MmSecret, NowTimestamp()) as OkObjectResult;
        Assert.That(result, Is.Not.Null);
        var channelDto = result.Value as InternalChannelDto;

        // ONLINE half: ChannelAdded pushed on the live connection, focus honored verbatim.
        Assert.That(_harness.SignalCount("conn-online", ChatEvents.ChannelAdded), Is.EqualTo(1));
        var pushed = _harness.PayloadFor("conn-online", ChatEvents.ChannelAdded) as ChannelAddedDto;
        Assert.That(pushed, Is.Not.Null);
        Assert.That(pushed.Channel.Id, Is.EqualTo(channelDto.Id));
        Assert.That(pushed.Focus, Is.True, "focus=true on the create request is honored on the live push");

        // OFFLINE half: zero live signal (an exact count — not just "no signal on some other connection
        // id", since offline never registers a connection at all), but the membership is durably persisted...
        Assert.That(_harness.AllSignals.Count(s => s.Method == ChatEvents.ChannelAdded), Is.EqualTo(1),
            "exactly one ChannelAdded total — the offline member never receives a live push of their own");
        Assert.That(await _membershipRepository.Load(channelDto.Id, offline), Is.Not.Null);

        // ...and the REAL SessionStateAssembler.AssembleAndSeed surfaces the match channel on next connect.
        var identity = new W3CUserAuthentication { BattleTag = offline, Name = "OfflineAC3" };
        var (sessionState, _) = await _sessionStateAssembler.AssembleAndSeed(
            identity, "conn-offline-reconnect", _time.GetUtcNow().UtcDateTime, ChatUserFor(offline));

        Assert.That(sessionState.Channels.Any(c => c.Channel.Id == channelDto.Id), Is.True,
            "the offline member sees the match channel in their next SessionState");
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════
    // ACCEPTANCE 5 — one-match-channel-per-user invariant: ChannelRemoved(A) STRICTLY before
    // ChannelAdded(B); exactly one System+Match membership remains.
    // ════════════════════════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task UserMovedBetweenMatches_RemovedThenAdded_ExactlyOneMatchMembership()
    {
        const string bt = "Alice#1";
        RegisterOnline("conn-alice", bt);

        var createA = "{\"kind\":\"match\",\"ref\":\"match-A\",\"name\":\"Match A\",\"members\":[\"Alice#1\"]}";
        var resultA = await PostChannelsCreate(createA, MmSecret, NowTimestamp()) as OkObjectResult;
        var matchA = resultA.Value as InternalChannelDto;

        var createB = "{\"kind\":\"match\",\"ref\":\"match-B\",\"name\":\"Match B\",\"members\":[\"Alice#1\"]}";
        var resultB = await PostChannelsCreate(createB, MmSecret, NowTimestamp()) as OkObjectResult;
        var matchB = resultB.Value as InternalChannelDto;

        Assert.That(await _membershipRepository.Load(matchA.Id, bt), Is.Null, "the stale match-A membership is swapped out");
        Assert.That(await _membershipRepository.Load(matchB.Id, bt), Is.Not.Null, "the match-B membership is present");

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

    // ════════════════════════════════════════════════════════════════════════════════════════════
    // ACCEPTANCE 6 — DELETE hard-removes channel + memberships + messages, pushes ChannelRemoved to
    // online members. GENUINE multi-member teardown: two distinct battleTags, neither ever touched by
    // any OTHER match channel, so the one-match-channel invariant never swaps either of them out before
    // the delete (the pitfall a prior task's DELETE test fell into, per the Task 11 brief).
    // ════════════════════════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Delete_RemovesChannelMembershipsMessages_PushesChannelRemoved()
    {
        const string alice = "AliceAC6#1";
        const string bob = "BobAC6#2";
        RegisterOnline("conn-alice-ac6", alice);
        RegisterOnline("conn-bob-ac6", bob);

        var createBody = "{\"kind\":\"match\",\"ref\":\"match-ac6\",\"name\":\"AC6 Match\",\"members\":[\"AliceAC6#1\",\"BobAC6#2\"]}";
        var createResult = await PostChannelsCreate(createBody, MmSecret, NowTimestamp()) as OkObjectResult;
        var channelDto = createResult.Value as InternalChannelDto;

        // Sanity: no OTHER match channel ever touches alice or bob, so the invariant never swaps either
        // of them out — the channel genuinely still has BOTH members right before the delete. (Verified
        // falsifiable: temporarily creating a second match channel touching alice here drops this count
        // to 1 — exactly the prior task's pitfall the Task 11 brief flagged.)
        var membersBeforeDelete = await _membershipRepository.LoadForChannel(channelDto.Id);
        Assert.That(membersBeforeDelete, Has.Count.EqualTo(2), "the channel must genuinely have 2 members at delete time");

        var message = new ChannelMessage
        {
            ChannelId = channelDto.Id,
            Seq = 1,
            Sender = new MessageSender { BattleTag = alice, Name = "AliceAC6" },
            Content = "gl hf",
            SentAt = _time.GetUtcNow().UtcDateTime,
        };
        await _messageRepository.Insert(message);

        // Also seed a soft-deleted AND a shadow-banned message into the SAME match channel (mirroring
        // MessageRepositoryTests.DeleteAllForChannel_RemovesSoftDeletedAndShadowMessagesToo's C4 fields/
        // flags) — the hard teardown must physically purge these too, not just the plain message above.
        var softDeletedMessage = new ChannelMessage
        {
            ChannelId = channelDto.Id,
            Seq = 2,
            Sender = new MessageSender { BattleTag = bob, Name = "BobAC6" },
            Content = "gg",
            SentAt = _time.GetUtcNow().UtcDateTime,
        };
        await _messageRepository.Insert(softDeletedMessage);
        await _messageRepository.MarkDeleted(softDeletedMessage.Id, "Mod#1", _time.GetUtcNow().UtcDateTime);

        var shadowMessage = new ChannelMessage
        {
            ChannelId = channelDto.Id,
            Seq = 3,
            Sender = new MessageSender { BattleTag = alice, Name = "AliceAC6" },
            Content = "shadowed",
            SentAt = _time.GetUtcNow().UtcDateTime,
            Shadow = true,
        };
        await _messageRepository.Insert(shadowMessage);

        var deleteResult = await DeleteChannelThroughFilter("match-ac6", MmSecret, NowTimestamp());
        Assert.That(deleteResult, Is.InstanceOf<OkResult>());

        Assert.That(_harness.SignalCount("conn-alice-ac6", ChatEvents.ChannelRemoved), Is.EqualTo(1), "the first online member receives ChannelRemoved");
        Assert.That(_harness.SignalCount("conn-bob-ac6", ChatEvents.ChannelRemoved), Is.EqualTo(1), "the second online member receives ChannelRemoved");
        var removedDto = _harness.PayloadFor("conn-alice-ac6", ChatEvents.ChannelRemoved) as ChannelRemovedDto;
        Assert.That(removedDto?.ChannelId, Is.EqualTo(channelDto.Id));

        Assert.That(await _messageRepository.Load(message.Id), Is.Null, "the message is hard-purged");
        // Load() is the repository's unfiltered-by-id read — no Deleted/Shadow predicate — so it would
        // still find these rows if the hard teardown respected moderation state instead of purging it.
        Assert.That(await _messageRepository.Load(softDeletedMessage.Id), Is.Null,
            "a soft-deleted row is still a physical row pending its 30d TTL — the hard teardown must purge it too, not just the plain message");
        Assert.That(await _messageRepository.Load(shadowMessage.Id), Is.Null,
            "a shadow-banned row is an ordinary physical row too — the hard teardown must purge it just like any other message");
        Assert.That(await _membershipRepository.LoadForChannel(channelDto.Id), Is.Empty, "every membership is removed");
        Assert.That(await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-ac6"), Is.Null, "the channel doc itself is removed");
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════
    // ACCEPTANCE 7 — the wb change-ping invokes IRelationshipProvider.Invalidate for BOTH actor and
    // target, via a Moq test double behind the REAL controller behind the REAL (wb-signed) filter.
    // ════════════════════════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task RelationshipPing_InvokesInvalidateForActorAndTarget()
    {
        var body = "{\"type\":\"block\",\"actor\":\"Actor#1\",\"target\":\"Target#2\"}";

        var result = await PostRelationshipChange(body, WbSecret, NowTimestamp());

        Assert.That(result, Is.InstanceOf<OkResult>(), "a valid wb-signed change-ping returns a body-free 200");
        _relationshipProvider.Verify(p => p.Invalidate("Actor#1"), Times.Once);
        _relationshipProvider.Verify(p => p.Invalidate("Target#2"), Times.Once);
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════
    // ACCEPTANCE 8 — a created match channel carries expiresAt = creation + 24h (FakeTimeProvider-pinned).
    // ════════════════════════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CreatedChannel_CarriesExpiresAt_CreationPlus24h()
    {
        var body = "{\"kind\":\"match\",\"ref\":\"match-ac8\",\"name\":\"AC8 Match\",\"members\":[]}";
        var creationInstant = _time.GetUtcNow().UtcDateTime;

        var result = await PostChannelsCreate(body, MmSecret, NowTimestamp()) as OkObjectResult;

        var dto = result.Value as InternalChannelDto;
        Assert.That(dto, Is.Not.Null);
        Assert.That(dto.ExpiresAt, Is.Not.Null);
        Assert.That(dto.ExpiresAt, Is.EqualTo(creationInstant.Add(RetentionPeriods.MatchChannel)),
            "expiresAt is EXACTLY creation + 24h (RetentionPeriods.MatchChannel) on the pinned fake clock");
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════
    // 2026-08-05 RECONCILIATION — Task 6: the two roster-assertion/epoch-sync endpoints proved through
    // the REAL HMAC byte-path (mirrors ACCEPTANCE 1's Hmac_PinnedVectors_AcceptedEndToEnd / CrossCaller
    // shape), plus a handful of create/assertion interaction pins.
    // ════════════════════════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task RosterAssert_SignedRequest_AcceptedEndToEnd()
    {
        const string alice = "Alice#1";
        const string bob = "Bob#2";
        RegisterOnline("conn-alice", alice);
        RegisterOnline("conn-bob", bob);

        var createBody = "{\"kind\":\"match\",\"ref\":\"match-roster-1\",\"name\":\"Roster Match\",\"members\":[\"Alice#1\"]}";
        var createResult = await PostChannelsCreate(createBody, MmSecret, NowTimestamp()) as OkObjectResult;
        Assert.That(createResult, Is.Not.Null);
        var channelDto = createResult.Value as InternalChannelDto;

        // The full-set assertion drops Alice (absent from the asserted set) and adds Bob — through the
        // REAL filter, then the REAL controller/domain/repository stack.
        var rosterBody = "{\"epoch\":\"e1\",\"seq\":1,\"members\":[\"Bob#2\"]}";
        var rosterResult = await PutChannelRoster("match-roster-1", rosterBody, MmSecret, NowTimestamp());

        Assert.That(rosterResult, Is.InstanceOf<OkResult>(), "a correctly signed roster assertion must be accepted end-to-end");
        Assert.That(await _membershipRepository.Load(channelDto.Id, alice), Is.Null, "Alice, absent from the asserted set, converges away");
        Assert.That(await _membershipRepository.Load(channelDto.Id, bob), Is.Not.Null, "Bob, present in the asserted set, is durably added");
        Assert.That(_harness.SignalCount("conn-alice", ChatEvents.ChannelRemoved), Is.EqualTo(1));
        Assert.That(_harness.SignalCount("conn-bob", ChatEvents.ChannelAdded), Is.EqualTo(1));

        var reloaded = await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-roster-1");
        Assert.That(reloaded.AssertEpoch, Is.EqualTo("e1"));
        Assert.That(reloaded.AssertSeq, Is.EqualTo(1L));
    }

    [Test]
    public async Task RosterAssert_WrongSecret_Rejected401_AndNoMembershipChange()
    {
        const string alice = "Alice#1";
        var createBody = "{\"kind\":\"match\",\"ref\":\"match-roster-2\",\"name\":\"Roster Match 2\",\"members\":[\"Alice#1\"]}";
        var createResult = await PostChannelsCreate(createBody, MmSecret, NowTimestamp()) as OkObjectResult;
        Assert.That(createResult, Is.Not.Null);
        var channelDto = createResult.Value as InternalChannelDto;

        // A signature computed with the WRONG secret never verifies — the real filter must reject before
        // model binding, so ApplyRosterAssertion (which would empty the membership below) never runs.
        var rosterBody = "{\"epoch\":\"e1\",\"seq\":1,\"members\":[]}";
        var result = await PutChannelRoster("match-roster-2", rosterBody, "not-the-real-secret", NowTimestamp());

        Assert.That(result, Is.InstanceOf<UnauthorizedResult>());
        Assert.That(await _membershipRepository.Load(channelDto.Id, alice), Is.Not.Null,
            "a rejected request must never reach the domain layer — the pre-existing membership is untouched");
    }

    [Test]
    public async Task RosterAssert_WbCaller_Rejected401()
    {
        // Least privilege (mirror CrossCaller_WbSecretOnChannelsEndpoint_401): a cryptographically VALID
        // wb signature is not enough — wb is not in InternalChannelsController's Mm-only allow-list.
        var rosterBody = "{\"epoch\":\"e1\",\"seq\":1,\"members\":[]}";

        var result = await PutChannelRoster("cross-caller-roster", rosterBody, WbSecret, NowTimestamp(), MmOnlyAllowed);

        Assert.That(result, Is.InstanceOf<UnauthorizedResult>());
        Assert.That(await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "cross-caller-roster"), Is.Null,
            "a rejected request must never reach the controller/DB — not even a create-on-demand shell");
    }

    [Test]
    public async Task EpochSync_SignedRequest_AcceptedEndToEnd()
    {
        const string alice = "Alice#1";
        RegisterOnline("conn-alice", alice);

        // 2026-08-05 fix wave (final review H1, plan D8 amendment): the sweep only considers channels
        // already stamped by the assertion protocol, so the create carries epoch/seq — an unstamped
        // channel would be invisible to the sweep and would survive regardless of liveLobbyRefs.
        var createBody = "{\"kind\":\"match\",\"ref\":\"match-epoch-1\",\"name\":\"Epoch Match\",\"members\":[\"Alice#1\"],\"epoch\":\"e1\",\"seq\":1}";
        var createResult = await PostChannelsCreate(createBody, MmSecret, NowTimestamp());
        Assert.That(createResult, Is.InstanceOf<OkObjectResult>());

        // mm rebooted under a fresh epoch and no longer knows this lobby — the sync tears it down.
        var syncBody = "{\"epoch\":\"e2\",\"liveLobbyRefs\":[]}";
        var result = await PostEpochSync(syncBody, MmSecret, NowTimestamp());

        Assert.That(result, Is.InstanceOf<OkResult>(), "a correctly signed epoch sync must be accepted end-to-end");
        Assert.That(await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-epoch-1"), Is.Null,
            "the channel absent from liveLobbyRefs is torn down");
        Assert.That(_harness.SignalCount("conn-alice", ChatEvents.ChannelRemoved), Is.EqualTo(1),
            "ChannelRemoved reaches the online member's captured connection");
    }

    [Test]
    public async Task EpochSync_WrongSecret_Rejected401_AndNothingTornDown()
    {
        var createBody = "{\"kind\":\"match\",\"ref\":\"match-epoch-2\",\"name\":\"Epoch Match 2\",\"members\":[]}";
        var createResult = await PostChannelsCreate(createBody, MmSecret, NowTimestamp());
        Assert.That(createResult, Is.InstanceOf<OkObjectResult>());

        var syncBody = "{\"epoch\":\"e2\",\"liveLobbyRefs\":[]}";
        var result = await PostEpochSync(syncBody, "not-the-real-secret", NowTimestamp());

        Assert.That(result, Is.InstanceOf<UnauthorizedResult>());
        Assert.That(await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-epoch-2"), Is.Not.Null,
            "a rejected epoch sync must never reach ApplyEpochSync — nothing is torn down");
    }

    [Test]
    public async Task RosterAssert_CreateThenAssertion_SharesOneChannel()
    {
        var createBody = "{\"kind\":\"match\",\"ref\":\"match-roster-3\",\"name\":\"Real Display Name\",\"members\":[]}";
        var createResult = await PostChannelsCreate(createBody, MmSecret, NowTimestamp()) as OkObjectResult;
        Assert.That(createResult, Is.Not.Null);
        var createDto = createResult.Value as InternalChannelDto;

        _time.Advance(TimeSpan.FromHours(1));

        // name is provided but IGNORED — CreateOrGet already established the real name on an existing channel.
        var rosterBody = "{\"epoch\":\"e1\",\"seq\":1,\"members\":[],\"name\":\"Should Be Ignored\"}";
        var rosterResult = await PutChannelRoster("match-roster-3", rosterBody, MmSecret, NowTimestamp());

        Assert.That(rosterResult, Is.InstanceOf<OkResult>());
        var channel = await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-roster-3");
        Assert.That(channel.Id, Is.EqualTo(createDto.Id), "the create and the assertion resolve to the SAME channel doc");
        Assert.That(channel.Name, Is.EqualTo("Real Display Name"), "the real display name is preserved — name is ignored on an existing channel");
        Assert.That(channel.ExpiresAt, Is.EqualTo(createDto.ExpiresAt), "the 24h creation-anchored expiry is NOT reset by the assertion");
    }

    [Test]
    public async Task RosterAssert_AssertionBeforeCreate_ThenCreate_BackfillsNameWithoutResettingExpiry()
    {
        // The assertion arrives BEFORE mm's own create POST (the boot-race healing case) — create-on-demand
        // shell, no name provided, so the ref itself is the placeholder.
        var rosterBody = "{\"epoch\":\"e1\",\"seq\":1,\"members\":[]}";
        var rosterResult = await PutChannelRoster("match-roster-4", rosterBody, MmSecret, NowTimestamp());
        Assert.That(rosterResult, Is.InstanceOf<OkResult>());
        var shell = await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "match-roster-4");
        Assert.That(shell.Name, Is.EqualTo("match-roster-4"), "no name was provided — the ref itself is the placeholder");
        var shellExpiry = shell.ExpiresAt;

        _time.Advance(TimeSpan.FromHours(1));

        var createBody = "{\"kind\":\"match\",\"ref\":\"match-roster-4\",\"name\":\"Real Match Name\",\"members\":[]}";
        var createResult = await PostChannelsCreate(createBody, MmSecret, NowTimestamp()) as OkObjectResult;

        Assert.That(createResult, Is.Not.Null, "the late create must succeed against the assertion-created shell");
        var createDto = createResult.Value as InternalChannelDto;
        Assert.That(createDto.Id, Is.EqualTo(shell.Id), "the create resolves to the SAME shell channel");
        Assert.That(createDto.Name, Is.EqualTo("Real Match Name"), "the real create backfills the placeholder name");
        Assert.That(createDto.ExpiresAt, Is.EqualTo(shellExpiry), "the create does NOT reset the shell's own creation-anchored expiry");
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════
    // Task 4 — POST /internal/channels/{ref}/system-message: lookup-only publish into an EXISTING
    // match channel, proved through the REAL HMAC filter + REAL controller + REAL SystemMessagePublisher.
    // ════════════════════════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task SystemMessage_PublishesIntoAnExistingMatchChannel()
    {
        var createBody = "{\"kind\":\"match\",\"ref\":\"match-sys-1\",\"name\":\"Sys Match\",\"members\":[\"Alice#1\"]}";
        var createResult = await PostChannelsCreate(createBody, MmSecret, NowTimestamp()) as OkObjectResult;
        Assert.That(createResult, Is.Not.Null, "precondition: the match channel must exist before the system-message publish is exercised");
        var channelDto = createResult.Value as InternalChannelDto;

        // `key` carries leading/trailing whitespace so this test also pins the controller's Trim() —
        // the persisted Key below must be the TRIMMED value, never the raw wire string.
        var messageBody = "{\"key\":\"  match_intro  \",\"params\":{\"map\":\"Amazonia\"},"
            + "\"listParams\":{\"players\":[\"Grubby#2136\",\"Happy#2233\"]},"
            + "\"fallbackText\":\"Match on Amazonia \\u2014 Grubby#2136, Happy#2233\",\"dedupeKey\":\"match_intro\"}";

        var result = await PostSystemMessage("match-sys-1", messageBody, MmSecret, NowTimestamp());

        Assert.That(result, Is.InstanceOf<OkResult>(), "a valid signed system-message publish into an existing channel returns a body-free 200");

        // The endpoint's real contract is the DTO -> SystemMessageBody mapping, not the status code —
        // load the persisted row and assert every field the controller is supposed to carry through, so
        // dropping a field (or forwarding the untrimmed key) would fail this test even though the status
        // code stays 200.
        var messages = await _messageRepository.LoadForModerator(channelDto.Id);
        var systemMessage = messages.Single(m => m.Kind == MessageKind.System);
        Assert.That(systemMessage.SystemMessage.Key, Is.EqualTo("match_intro"),
            "Key must be trimmed before it is persisted");
        Assert.That(systemMessage.SystemMessage.Params["map"], Is.EqualTo("Amazonia"),
            "Params must be carried through the DTO -> SystemMessageBody mapping unchanged");
        Assert.That(systemMessage.SystemMessage.ListParams["players"], Is.EqualTo(new List<string> { "Grubby#2136", "Happy#2233" }),
            "ListParams must be carried through the DTO -> SystemMessageBody mapping unchanged, including BOTH entries");
        Assert.That(systemMessage.SystemMessage.FallbackText, Is.EqualTo("Match on Amazonia — Grubby#2136, Happy#2233"),
            "FallbackText must be carried through the DTO -> SystemMessageBody mapping unchanged");
    }

    [Test]
    public async Task SystemMessage_UnknownRef_Is404_AndCreatesNothing()
    {
        var messageBody = "{\"key\":\"match_intro\",\"fallbackText\":\"Match on Amazonia\"}";

        var result = await PostSystemMessage("never-existed", messageBody, MmSecret, NowTimestamp());

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>(),
            "the system-message route is lookup-only — an unknown ref is a 404, never an implicit create");
        Assert.That(await _channelRepository.LoadBySystemRef(SystemChannelKind.Match, "never-existed"), Is.Null,
            "the system-message route is lookup-only — it must NEVER create a channel on demand");
    }

    [Test]
    public async Task SystemMessage_BlankKeyOrFallback_Is400()
    {
        var createBody = "{\"kind\":\"match\",\"ref\":\"match-sys-2\",\"name\":\"Sys Match 2\",\"members\":[\"Alice#1\"]}";
        var createResult = await PostChannelsCreate(createBody, MmSecret, NowTimestamp());
        Assert.That(createResult, Is.InstanceOf<OkObjectResult>(), "precondition: the match channel must exist so the 400s below are genuinely validation failures, not masked 404s");

        var blankKeyBody = "{\"key\":\"   \",\"fallbackText\":\"x\"}";
        var blankKey = await PostSystemMessage("match-sys-2", blankKeyBody, MmSecret, NowTimestamp());

        var blankFallbackBody = "{\"key\":\"match_intro\",\"fallbackText\":\"\"}";
        var blankFallback = await PostSystemMessage("match-sys-2", blankFallbackBody, MmSecret, NowTimestamp());

        Assert.That(blankKey, Is.InstanceOf<BadRequestObjectResult>(), "a whitespace-only key must never reach the publisher");
        Assert.That(blankFallback, Is.InstanceOf<BadRequestObjectResult>(),
            "fallbackText is the only thing a client that does not know the key can render — it is required");
    }

    [Test]
    public async Task SystemMessage_RepeatedWithSameDedupeKey_Is200_AndPublishesOnce()
    {
        var createBody = "{\"kind\":\"match\",\"ref\":\"match-sys-3\",\"name\":\"Sys Match 3\",\"members\":[\"Alice#1\"]}";
        var createResult = await PostChannelsCreate(createBody, MmSecret, NowTimestamp()) as OkObjectResult;
        Assert.That(createResult, Is.Not.Null, "precondition: the match channel must exist before the repeated dedupeKey publish is exercised");
        var channelDto = createResult.Value as InternalChannelDto;

        var messageBody = "{\"key\":\"match_intro\",\"fallbackText\":\"Match on Amazonia\",\"dedupeKey\":\"match_intro\"}";

        var first = await PostSystemMessage("match-sys-3", messageBody, MmSecret, NowTimestamp());
        var second = await PostSystemMessage("match-sys-3", messageBody, MmSecret, NowTimestamp());

        Assert.That(first, Is.InstanceOf<OkResult>(), "the first publish of a fresh dedupeKey must succeed");
        Assert.That(second, Is.InstanceOf<OkResult>(),
            "mm retries on timeout — a duplicate publish is a success, never an error");

        var messages = await _messageRepository.LoadForModerator(channelDto.Id);
        Assert.That(messages.Count(m => m.Kind == MessageKind.System), Is.EqualTo(1),
            "the retried publish shares the SAME dedupeKey — it must be published exactly once, not twice");
    }

    // Review finding 1 (human-ruled): an empty dedupeKey is optional-field noise, not a validation
    // failure — mm has no per-status retry policy, so this must normalize to "no dedupe" (SAME as an
    // absent dedupeKey) rather than 400. Proven both ways: the call itself is a 200, AND two calls with
    // an empty dedupeKey are NOT deduped against each other (empty is not itself a shared dedupe value).
    [Test]
    public async Task SystemMessage_EmptyDedupeKey_Is200_AndDoesNotDedupe()
    {
        var createBody = "{\"kind\":\"match\",\"ref\":\"match-sys-4\",\"name\":\"Sys Match 4\",\"members\":[\"Alice#1\"]}";
        var createResult = await PostChannelsCreate(createBody, MmSecret, NowTimestamp()) as OkObjectResult;
        Assert.That(createResult, Is.Not.Null, "precondition: the match channel must exist before the empty-dedupeKey publish is exercised");
        var channelDto = createResult.Value as InternalChannelDto;

        var messageBody = "{\"key\":\"match_intro\",\"fallbackText\":\"Match on Amazonia\",\"dedupeKey\":\"\"}";

        var first = await PostSystemMessage("match-sys-4", messageBody, MmSecret, NowTimestamp());
        var second = await PostSystemMessage("match-sys-4", messageBody, MmSecret, NowTimestamp());

        Assert.That(first, Is.InstanceOf<OkResult>(),
            "an empty dedupeKey must normalize to \"no dedupe\" and never trigger a 400 — an optional field must never reject the whole call");
        Assert.That(second, Is.InstanceOf<OkResult>(),
            "a second call with an empty dedupeKey must also succeed — empty is not treated as a shared dedupe value between the two calls");

        var messages = await _messageRepository.LoadForModerator(channelDto.Id);
        Assert.That(messages.Count(m => m.Kind == MessageKind.System), Is.EqualTo(2),
            "empty dedupeKey means NO dedupe (unlike a genuine shared key) — both calls must persist as DISTINCT messages");
    }

    // Review finding 4: three DISTINCT rejection decisions, each with its own rationale comment in the
    // controller, none previously pinned by a test. Table-driven so all three close together; the
    // InvalidSystemRef case is the one that matters most to get right — it MUST be 400 (a malformed
    // ref never reaches the LoadBySystemRef lookup that produces a 404), never masquerading as the
    // lookup-miss branch.
    public enum SystemMessageRejectionScenario
    {
        KeyContainsNewline,
        DedupeKeyContainsHash,
        InvalidSystemRef,
    }

    [TestCase(SystemMessageRejectionScenario.KeyContainsNewline)]
    [TestCase(SystemMessageRejectionScenario.DedupeKeyContainsHash)]
    [TestCase(SystemMessageRejectionScenario.InvalidSystemRef)]
    public async Task SystemMessage_RejectionScenarios_All400NotSomethingElse(SystemMessageRejectionScenario scenario)
    {
        var createBody = "{\"kind\":\"match\",\"ref\":\"match-sys-5\",\"name\":\"Sys Match 5\",\"members\":[\"Alice#1\"]}";
        var createResult = await PostChannelsCreate(createBody, MmSecret, NowTimestamp());
        Assert.That(createResult, Is.InstanceOf<OkObjectResult>(),
            "precondition: the match channel must exist so a 400 below is genuinely a validation failure, not a masked 404");

        var (systemRef, body) = scenario switch
        {
            SystemMessageRejectionScenario.KeyContainsNewline =>
                ("match-sys-5", "{\"key\":\"bad\\nkey\",\"fallbackText\":\"x\"}"),
            SystemMessageRejectionScenario.DedupeKeyContainsHash =>
                ("match-sys-5", "{\"key\":\"match_intro\",\"fallbackText\":\"x\",\"dedupeKey\":\"bad#key\"}"),
            // A ref containing a space fails the SAME IsValidRef character class as `key`/`dedupeKey`
            // above — deliberately targeting an EXISTING channel's ref shape rather than "never-existed",
            // so a wrongly-implemented lookup-first ordering would surface this as a 404 instead.
            SystemMessageRejectionScenario.InvalidSystemRef =>
                ("bad ref", "{\"key\":\"match_intro\",\"fallbackText\":\"x\"}"),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        var result = await PostSystemMessage(systemRef, body, MmSecret, NowTimestamp());

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>(),
            $"{scenario} must be a 400 — distinguishing a validation failure from a lookup miss (404) is the entire point of the invalid-systemRef case");
    }
}
