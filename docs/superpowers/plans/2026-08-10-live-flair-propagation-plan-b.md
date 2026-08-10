# Live Flair Propagation (Plan B) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When a player changes their portrait, chat colour, chat icons or clan, every other viewer who can see them in a chat roster sees the new flair within about a second — without anyone reconnecting.

**Architecture:** website-backend notifies chat-service at the *persistence boundary*: decorators over `IPersonalSettingsRepository` and `IClanRepository` fire `IFlairChangeNotifier.NotifyChanged(battleTags)` after a successful write, so all five flair-write paths (and any sixth added later) are covered without editing a single command handler. The notifier POSTs HMAC-signed to `/internal/profile-changes` on chat-service, fire-and-forget. Chat-service coalesces the battleTags for one tick, then for each one with a live session re-resolves flair through the existing `GetUserFromIdentity` — refreshing `ConnectionMapping` and the user directory, and pushing a new `FlairChanged` event to the focused connections that can see them. Every layer is best-effort: connect always re-enriches from website-backend, so a dropped ping degrades to today's behaviour and never to a permanently wrong state.

**Tech Stack:** C# / .NET 8, ASP.NET Core, Castle DynamicProxy (website-backend DI), SignalR (chat-service), NUnit + Moq (both; chat-service also uses Testcontainers), TypeScript / React / easy-peasy (launcher-e).

## Global Constraints

- **Spec:** `docs/superpowers/specs/2026-08-09-roster-flair-enrichment-design.md`. This plan implements §3.2, §3.3 and the `FlairChangedDto` row of §4. §3.1 and the roster half of §4 shipped in Plan A (chat-service PR #44, launcher-e PR #850, both merged 2026-08-10).
- **The `FreshFromWb` rule is the single most important behaviour in this plan.** On a refresh, if `GetUserFromIdentity` returns `FreshFromWb == false`, do **nothing at all** — no `RegisterUser`, no directory write, no `FlairChanged`. A website-backend blip would otherwise broadcast a degraded tier-3 profile (the sheep) to every viewer in the channel, turning a transient upstream hiccup into a visible regression for everyone. This is the one place this design could make things worse than today. It gets an explicit test.
- **Never fail a caller's write.** A notifier fault must never surface to a settings save or clan operation. Notification fires only *after* the inner write succeeds, inside its own try/catch.
- **Self-disabling.** Without `CHAT_INTERNAL_API_SECRET` the notifier is a silent no-op with one startup log line. This is what makes the feature deployable dark and enabled by configuration.
- **Reuse, do not reinvent.** `IFlairChangeNotifier` clones the `RelationshipChangeNotifier` triad 1:1 — same `ChatInternalApiSigner` HMAC scheme and headers, same `ChatPingSettings`, same `Task.Run` fire-and-forget with 2 attempts and a 3 s per-attempt cap. The chat-service controller clones `InternalRelationshipChangesController`. The coalescer mirrors `ActivityCoalescer` / `ViewersAccumulator` discipline: mutate state under one lock, send outside it, fault-isolate per connection.
- **Batch cap:** `ChatLimits.InternalMaxMembersPerCall` is **64**. The bulk clan-delete path can exceed that, so the notifier must chunk; the controller rejects any batch over the cap outright with no partial processing.
- **launcher-e has no test runner.** Do not add one and do not write frontend tests. Verification there is `npm run type-check`, `npm run lint:prod`, `npm run dprint`, `npm run check:i18n`.
- **Docker must be running** for the chat-service suite (Testcontainers spins up Mongo). The website-backend suite does **not** need Docker.
- **website-backend test hazard:** any test deriving from `IntegrationTestBase` (`WC3ChampionsStatisticService.UnitTests/IntegrationTestBase.cs`) connects to a **shared remote MongoDB** and calls `DropDatabaseAsync` in `[SetUp]`. Every test this plan adds is a pure Moq/NUnit unit test with no Mongo dependency — model them on `WC3ChampionsStatisticService.UnitTests/Friend/RelationshipChangeNotifierTests.cs`, never on `ClanTests.cs`.
- **Assertion style:** use `Assert.That(x, Is.EqualTo(y))` in both C# repos. Both suites contain older `Assert.AreEqual` call sites; do not add new ones.
- **Branch base:** all three repos branch from their current default (`master` for chat-service and website-backend, `main` for launcher-e). Plan A is already merged into all of them.

---

## File Structure

**website-backend** (branch off `master`)

| File | Responsibility |
|---|---|
| `W3ChampionsStatisticService/Extensions/ServiceCollectionExtensions.cs` | *Modify.* Add a `Decorate<TInterface, TDecorator>` helper that wraps an existing registration in place, preserving the Castle tracing proxy underneath. |
| `W3C.Domain/ChatService/IFlairChangeNotifier.cs` | **Create.** The notification port. |
| `W3C.Domain/ChatService/FlairChangeNotifier.cs` | **Create.** HMAC-signed fire-and-forget POST to chat-service, chunked at 64. |
| `W3ChampionsStatisticService/PersonalSettings/FlairNotifyingPersonalSettingsRepository.cs` | **Create.** Decorator over `IPersonalSettingsRepository`; notifies after `Save` / `SaveMany`. |
| `W3ChampionsStatisticService/Clans/FlairNotifyingClanRepository.cs` | **Create.** Decorator over `IClanRepository`; notifies after `UpsertMemberShip` / `SaveMemberShips`. |
| `W3ChampionsStatisticService/Program.cs` | *Modify.* Register the notifier and apply both decorators. |
| `WC3ChampionsStatisticService.UnitTests/Extensions/ServiceCollectionDecorateTests.cs` | **Create.** Pins the decoration helper. |
| `WC3ChampionsStatisticService.UnitTests/Friend/FlairChangeNotifierTests.cs` | **Create.** Pins the wire format, chunking, retry and self-disable. |
| `WC3ChampionsStatisticService.UnitTests/PersonalSettings/FlairNotifyingRepositoryTests.cs` | **Create.** Pins both decorators' notify-on-success / silent-on-throw behaviour. |

**chat-service** (branch off `master`)

| File | Responsibility |
|---|---|
| `W3ChampionsChatService/Protocol/FlairChangedDto.cs` | **Create.** The new event payload. |
| `W3ChampionsChatService/Protocol/ChatEvents.cs` | *Modify.* Add the `FlairChanged` constant. |
| `W3ChampionsChatService/Chats/UserDirectoryUpsert.cs` | **Create.** The directory-upsert block, extracted from `ChatHub` so the connect path and the refresh path share one implementation of the never-clobber rule instead of duplicating it. |
| `W3ChampionsChatService/Chats/ChatHub.cs` | *Modify.* `UpsertDirectory` delegates to the extracted helper. |
| `W3ChampionsChatService/FanOut/FlairRefresher.cs` | **Create.** Per-battleTag refresh: the `FreshFromWb` rule, `ConnectionMapping` refresh, directory upsert, targeted fan-out. |
| `W3ChampionsChatService/FanOut/FlairRefreshCoalescer.cs` | **Create.** Bounded pending set; collapses a burst for one battleTag into one refresh per tick. |
| `W3ChampionsChatService/FanOut/FanOutFlushService.cs` | *Modify.* Drive the new coalescer alongside the existing two. |
| `W3ChampionsChatService/Domain/ChatLimits.cs` | *Modify.* Add `FlairRefreshPendingCap`. |
| `W3ChampionsChatService/Internal/InternalValidation.cs` | **Create.** The blank/control-char battleTag check, factored out of its two existing private copies rather than adding a third. |
| `W3ChampionsChatService/Internal/InternalRelationshipChangesController.cs` | *Modify.* Delegate to the shared validator. |
| `W3ChampionsChatService/Internal/InternalChannelsController.cs` | *Modify.* Delegate to the shared validator. |
| `W3ChampionsChatService/Internal/InternalProfileChangesController.cs` | **Create.** `POST /internal/profile-changes`, HMAC-gated to `Wb`. |
| `W3ChampionsChatService/Internal/InternalDtos.cs` | *Modify.* Add the request DTO. |
| `W3ChampionsChatService/Startup.cs` | *Modify.* Register the refresher and coalescer. |
| `W3ChampionsChatService.Tests/FlairRefresherTests.cs` | **Create.** The `FreshFromWb` rule and fan-out targeting. |
| `W3ChampionsChatService.Tests/FlairRefreshCoalescerTests.cs` | **Create.** Burst collapsing and overflow. |
| `W3ChampionsChatService.Tests/InternalProfileChangesControllerTests.cs` | **Create.** Validation and enqueue behaviour. |

**launcher-e** (branch off `main`)

| File | Responsibility |
|---|---|
| `src/types/chat-protocol.types.ts` | *Modify.* `IFlairChangedDto` + the `FlairChanged` event name. |
| `src/models/chat-core.ts` | *Modify.* `ingestFlairChanged` action. |
| `src/services/chat.service.ts` | *Modify.* Bind the event. |

---

### Task 1: `Decorate` DI helper

**Files:**
- Modify: `W3ChampionsStatisticService/Extensions/ServiceCollectionExtensions.cs`
- Test: `WC3ChampionsStatisticService.UnitTests/Extensions/ServiceCollectionDecorateTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `public static IServiceCollection Decorate<TInterface, TDecorator>(this IServiceCollection services) where TInterface : class where TDecorator : class, TInterface`

**Why this task exists.** `IPersonalSettingsRepository` and `IClanRepository` are registered via `AddInterceptedTransient` (`Program.cs:178,187`), which registers the interface as a **Castle DynamicProxy** wrapping the concrete class with a `TracingInterceptor`. You cannot decorate that by simply re-registering the interface: `AddInterceptedTransient` resolves the decorator's constructor arguments straight out of the container by type, so a decorator whose parameter is `IPersonalSettingsRepository` would resolve to *itself* and recurse. Depending on the concrete class instead would silently drop tracing from the two most-used repositories in the service. This helper captures the existing descriptor and calls it to build the inner instance, so the proxy — and its tracing — survives underneath the decorator.

- [ ] **Step 1: Write the failing test**

Create `WC3ChampionsStatisticService.UnitTests/Extensions/ServiceCollectionDecorateTests.cs`:

```csharp
using System;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using W3ChampionsStatisticService.Extensions;

namespace WC3ChampionsStatisticService.UnitTests.Extensions;

[TestFixture]
public class ServiceCollectionDecorateTests
{
    private interface IGreeter
    {
        string Greet();
    }

    private class Inner : IGreeter
    {
        public string Greet() => "inner";
    }

    private class Outer(IGreeter inner) : IGreeter
    {
        public string Greet() => $"outer({inner.Greet()})";
    }

    private class Second(IGreeter inner) : IGreeter
    {
        public string Greet() => $"second({inner.Greet()})";
    }

    [Test]
    public void Decorate_WrapsTheExistingRegistration()
    {
        var services = new ServiceCollection();
        services.AddTransient<IGreeter, Inner>();

        services.Decorate<IGreeter, Outer>();

        var resolved = services.BuildServiceProvider().GetRequiredService<IGreeter>();
        Assert.That(resolved.Greet(), Is.EqualTo("outer(inner)"));
    }

    [Test]
    public void Decorate_PreservesAFactoryRegistration()
    {
        // AddInterceptedTransient registers the interface via an ImplementationFactory that builds a
        // Castle proxy. If Decorate ignored the factory and reflected over ImplementationType (null
        // here), the tracing proxy would be silently dropped.
        var services = new ServiceCollection();
        services.AddTransient<IGreeter>(_ => new Inner());

        services.Decorate<IGreeter, Outer>();

        var resolved = services.BuildServiceProvider().GetRequiredService<IGreeter>();
        Assert.That(resolved.Greet(), Is.EqualTo("outer(inner)"));
    }

    [Test]
    public void Decorate_PreservesLifetime()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IGreeter, Inner>();

        services.Decorate<IGreeter, Outer>();

        var provider = services.BuildServiceProvider();
        Assert.That(provider.GetRequiredService<IGreeter>(), Is.SameAs(provider.GetRequiredService<IGreeter>()));
    }

    [Test]
    public void Decorate_Twice_NestsInRegistrationOrder()
    {
        var services = new ServiceCollection();
        services.AddTransient<IGreeter, Inner>();

        services.Decorate<IGreeter, Outer>();
        services.Decorate<IGreeter, Second>();

        var resolved = services.BuildServiceProvider().GetRequiredService<IGreeter>();
        Assert.That(resolved.Greet(), Is.EqualTo("second(outer(inner))"));
    }

    [Test]
    public void Decorate_WithNoExistingRegistration_ThrowsWithAnActionableMessage()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(() => services.Decorate<IGreeter, Outer>());
        Assert.That(ex.Message, Does.Contain("IGreeter"));
        Assert.That(ex.Message, Does.Contain("AFTER"));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
cd "<website-backend worktree>"
dotnet test WC3ChampionsStatisticService.UnitTests --filter "FullyQualifiedName~ServiceCollectionDecorateTests"
```

Expected: FAIL to compile — `'IServiceCollection' does not contain a definition for 'Decorate'`.

- [ ] **Step 3: Implement the helper**

In `W3ChampionsStatisticService/Extensions/ServiceCollectionExtensions.cs`, add these two members to the existing `ServiceCollectionExtensions` class (place them after `AddInterceptedTransient`, before the closing brace):

```csharp
    /// <summary>
    /// Wraps the LAST existing registration of <typeparamref name="TInterface"/> in
    /// <typeparamref name="TDecorator"/>, in place — same position in the collection, same lifetime.
    /// <para>
    /// This exists because <see cref="AddInterceptedTransient{TInterface,TImplementation}"/> registers
    /// the interface as a Castle DynamicProxy over the concrete type. A decorator cannot be layered by
    /// re-registering the interface: that helper resolves constructor arguments straight from the
    /// container by type, so a decorator taking <typeparamref name="TInterface"/> would resolve to
    /// ITSELF and recurse. Building the inner instance from the CAPTURED descriptor is what keeps the
    /// tracing proxy alive underneath the decorator.
    /// </para>
    /// <para>
    /// Must be called AFTER the registration it wraps. Decorating twice nests in call order:
    /// the second decorator wraps the first.
    /// </para>
    /// </summary>
    public static IServiceCollection Decorate<TInterface, TDecorator>(this IServiceCollection services)
        where TInterface : class
        where TDecorator : class, TInterface
    {
        var index = -1;
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(TInterface))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            throw new InvalidOperationException(
                $"Cannot decorate {typeof(TInterface).Name} with {typeof(TDecorator).Name}: it has no existing "
                + "registration. Decorate must be called AFTER the registration it wraps.");
        }

        var inner = services[index];

        services[index] = new ServiceDescriptor(
            typeof(TInterface),
            serviceProvider => ActivatorUtilities.CreateInstance<TDecorator>(
                serviceProvider,
                CreateInner(serviceProvider, inner)),
            inner.Lifetime);

        return services;
    }

    // Rebuilds the captured registration by whichever of the three ServiceDescriptor forms it used.
    private static object CreateInner(IServiceProvider serviceProvider, ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance != null) return descriptor.ImplementationInstance;
        if (descriptor.ImplementationFactory != null) return descriptor.ImplementationFactory(serviceProvider);
        return ActivatorUtilities.CreateInstance(serviceProvider, descriptor.ImplementationType);
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
cd "<website-backend worktree>"
dotnet test WC3ChampionsStatisticService.UnitTests --filter "FullyQualifiedName~ServiceCollectionDecorateTests"
```

Expected: `Failed: 0, Passed: 5`

- [ ] **Step 5: Commit**

```bash
git add W3ChampionsStatisticService/Extensions/ServiceCollectionExtensions.cs WC3ChampionsStatisticService.UnitTests/Extensions/ServiceCollectionDecorateTests.cs
git commit -m "feat(di): add a Decorate helper that wraps an existing registration in place"
```

---

### Task 2: `IFlairChangeNotifier`

**Files:**
- Create: `W3C.Domain/ChatService/IFlairChangeNotifier.cs`
- Create: `W3C.Domain/ChatService/FlairChangeNotifier.cs`
- Test: `WC3ChampionsStatisticService.UnitTests/Friend/FlairChangeNotifierTests.cs`

**Interfaces:**
- Consumes: `ChatPingSettings` (already registered as a singleton in `Program.cs`, with `ChatApiUrl`, `Secret`, `Enabled`); `ChatInternalApiSigner.CreateSignatureHeaderValue(string secret, string timestamp, string rawBody)`, `ChatInternalApiSigner.TimestampHeaderName`, `ChatInternalApiSigner.SignatureHeaderName`.
- Produces:
  - `interface IFlairChangeNotifier { void NotifyChanged(IReadOnlyCollection<string> battleTags); }`
  - `class FlairChangeNotifier(IHttpClientFactory httpClientFactory, ChatPingSettings settings) : IFlairChangeNotifier` with a public `Task LastDispatch { get; }` test seam.

This is a 1:1 clone of `RelationshipChangeNotifier` (`W3C.Domain/ChatService/RelationshipChangeNotifier.cs`) with three differences: a battleTag *list* instead of a type/actor/target triple, a different path, and **chunking at 64** because the bulk clan-delete path can affect more members than the receiver's per-call cap allows.

- [ ] **Step 1: Write the failing test**

Create `WC3ChampionsStatisticService.UnitTests/Friend/FlairChangeNotifierTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Moq.Protected;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using W3C.Domain.ChatService;

namespace WC3ChampionsStatisticService.UnitTests.Friend;

[TestFixture]
public class FlairChangeNotifierTests
{
    private List<HttpRequestMessage> _captured;
    private List<string> _capturedBodies;

    private FlairChangeNotifier CreateNotifier(ChatPingSettings settings, HttpStatusCode status = HttpStatusCode.OK)
    {
        _captured = new List<HttpRequestMessage>();
        _capturedBodies = new List<string>();

        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns(async (HttpRequestMessage request, CancellationToken _) =>
            {
                _captured.Add(request);
                _capturedBodies.Add(request.Content == null ? null : await request.Content.ReadAsStringAsync());
                return new HttpResponseMessage(status);
            });

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(handler.Object));

        return new FlairChangeNotifier(factory.Object, settings);
    }

    private static ChatPingSettings Enabled() => new("https://chat.test", "test-secret");

    [Test]
    public async Task NotifyChanged_PostsTheBattleTagsToProfileChanges()
    {
        var notifier = CreateNotifier(Enabled());

        notifier.NotifyChanged(new[] { "Foo#1234", "Bar#5678" });
        await notifier.LastDispatch;

        Assert.That(_captured, Has.Count.EqualTo(1));
        Assert.That(_captured[0].Method, Is.EqualTo(HttpMethod.Post));
        Assert.That(_captured[0].RequestUri.ToString(), Is.EqualTo("https://chat.test/internal/profile-changes"));

        var body = JObject.Parse(_capturedBodies[0]);
        Assert.That(body["battleTags"].Select(t => t.ToString()), Is.EqualTo(new[] { "Foo#1234", "Bar#5678" }));
    }

    [Test]
    public async Task NotifyChanged_SignsWithTheSharedHmacScheme()
    {
        var notifier = CreateNotifier(Enabled());

        notifier.NotifyChanged(new[] { "Foo#1234" });
        await notifier.LastDispatch;

        var timestamp = _captured[0].Headers.GetValues(ChatInternalApiSigner.TimestampHeaderName).Single();
        var signature = _captured[0].Headers.GetValues(ChatInternalApiSigner.SignatureHeaderName).Single();

        // The signature must be over the EXACT body bytes that were sent — a mismatch here is the
        // classic "serialized twice" bug and chat-service would reject every ping.
        Assert.That(signature, Is.EqualTo(
            ChatInternalApiSigner.CreateSignatureHeaderValue("test-secret", timestamp, _capturedBodies[0])));
    }

    [Test]
    public async Task NotifyChanged_ChunksAtSixtyFour()
    {
        // The receiver rejects any batch over ChatLimits.InternalMaxMembersPerCall (64) outright, and a
        // clan delete can affect more members than that — so an unchunked notifier would silently lose
        // the whole notification for exactly the largest clans.
        var notifier = CreateNotifier(Enabled());
        var tags = Enumerable.Range(0, 150).Select(i => $"Player{i}#1").ToArray();

        notifier.NotifyChanged(tags);
        await notifier.LastDispatch;

        Assert.That(_captured, Has.Count.EqualTo(3));
        var sent = _capturedBodies.SelectMany(b => JObject.Parse(b)["battleTags"].Select(t => t.ToString())).ToList();
        Assert.That(sent, Is.EqualTo(tags));
        Assert.That(JObject.Parse(_capturedBodies[0])["battleTags"].Count(), Is.EqualTo(64));
        Assert.That(JObject.Parse(_capturedBodies[2])["battleTags"].Count(), Is.EqualTo(22));
    }

    [Test]
    public async Task NotifyChanged_DedupesAndDropsBlanks()
    {
        var notifier = CreateNotifier(Enabled());

        notifier.NotifyChanged(new[] { "Foo#1234", "foo#1234", "  ", null, "Bar#5678" });
        await notifier.LastDispatch;

        var sent = JObject.Parse(_capturedBodies[0])["battleTags"].Select(t => t.ToString()).ToList();
        Assert.That(sent, Is.EqualTo(new[] { "Foo#1234", "Bar#5678" }));
    }

    [Test]
    public void NotifyChanged_WhenDisabled_SendsNothing()
    {
        var notifier = CreateNotifier(new ChatPingSettings("https://chat.test", null));

        notifier.NotifyChanged(new[] { "Foo#1234" });

        Assert.That(notifier.LastDispatch.IsCompleted, Is.True);
        Assert.That(_captured, Is.Empty);
    }

    [Test]
    public void NotifyChanged_WithNoUsableTags_SendsNothing()
    {
        var notifier = CreateNotifier(Enabled());

        notifier.NotifyChanged(new[] { "   ", null });

        Assert.That(notifier.LastDispatch.IsCompleted, Is.True);
        Assert.That(_captured, Is.Empty);
    }

    [Test]
    public async Task NotifyChanged_OnNonSuccess_RetriesOnceThenGivesUp()
    {
        var notifier = CreateNotifier(Enabled(), HttpStatusCode.InternalServerError);

        notifier.NotifyChanged(new[] { "Foo#1234" });
        await notifier.LastDispatch;

        Assert.That(_captured, Has.Count.EqualTo(2));
    }

    [Test]
    public void NotifyChanged_NeverThrowsToTheCaller()
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Throws(new InvalidOperationException("boom"));

        Assert.DoesNotThrow(() =>
        {
            var notifier = new FlairChangeNotifier(factory.Object, new ChatPingSettings("https://chat.test", null));
            notifier.NotifyChanged(new[] { "Foo#1234" });
        });
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
cd "<website-backend worktree>"
dotnet test WC3ChampionsStatisticService.UnitTests --filter "FullyQualifiedName~FlairChangeNotifierTests"
```

Expected: FAIL to compile — `The type or namespace name 'FlairChangeNotifier' could not be found`.

- [ ] **Step 3: Create the interface**

Create `W3C.Domain/ChatService/IFlairChangeNotifier.cs`:

```csharp
using System.Collections.Generic;

namespace W3C.Domain.ChatService;

/// <summary>
/// Tells chat-service that one or more players' flair (portrait, chat colour, chat icons, clan) may
/// have changed, so it can re-resolve and push the update to anyone currently viewing them.
/// <para>
/// Fire-and-forget by contract: implementations must never throw and never block the caller. A lost
/// notification degrades to the reconnect backstop — chat-service re-enriches from this service on
/// every connect regardless.
/// </para>
/// </summary>
public interface IFlairChangeNotifier
{
    void NotifyChanged(IReadOnlyCollection<string> battleTags);
}
```

- [ ] **Step 4: Create the notifier**

Create `W3C.Domain/ChatService/FlairChangeNotifier.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Serilog;

namespace W3C.Domain.ChatService;

/// <summary>
/// Clone of <see cref="RelationshipChangeNotifier"/>'s dispatch discipline — same HMAC scheme, same
/// fire-and-forget <see cref="Task.Run"/>, same 2 attempts at 3 s each, same self-disable and the same
/// never-log-the-secret rule — carrying a battleTag list instead of a relationship triple.
/// </summary>
public class FlairChangeNotifier(IHttpClientFactory httpClientFactory, ChatPingSettings settings) : IFlairChangeNotifier
{
    private const int TimeoutSecondsPerAttempt = 3;
    private const int MaxAttempts = 2; // initial + one retry, matching RelationshipChangeNotifier

    // Chat-service rejects any batch above ChatLimits.InternalMaxMembersPerCall outright, with no
    // partial processing. A clan delete can affect more members than that, so an unchunked send would
    // lose the ENTIRE notification for exactly the largest clans.
    private const int MaxBattleTagsPerRequest = 64;

    private readonly ChatPingSettings _settings = settings;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;

    /// <summary>Test seam: the background dispatch started by the most recent call.</summary>
    public Task LastDispatch { get; private set; } = Task.CompletedTask;

    public void NotifyChanged(IReadOnlyCollection<string> battleTags)
    {
        if (!_settings.Enabled) return; // silent no-op; startup already logged once (Program.cs)

        if (battleTags == null) return;

        var usable = battleTags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (usable.Count == 0) return;

        LastDispatch = Task.Run(() => SendAllAsync(usable));
    }

    private async Task SendAllAsync(List<string> battleTags)
    {
        foreach (var chunk in battleTags.Chunk(MaxBattleTagsPerRequest))
        {
            await SendWithRetryAsync(chunk);
        }
    }

    private async Task SendWithRetryAsync(IReadOnlyCollection<string> battleTags)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                // Serialize ONCE and reuse the same string for both the signature and the content —
                // signing a re-serialization would produce a valid-looking but rejected request.
                var body = JsonConvert.SerializeObject(new { battleTags });
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
                var request = new HttpRequestMessage(HttpMethod.Post,
                    $"{_settings.ChatApiUrl}/internal/profile-changes")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                request.Headers.Add(ChatInternalApiSigner.TimestampHeaderName, timestamp);
                request.Headers.Add(ChatInternalApiSigner.SignatureHeaderName,
                    ChatInternalApiSigner.CreateSignatureHeaderValue(_settings.Secret, timestamp, body));

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(TimeoutSecondsPerAttempt));
                var response = await _httpClientFactory.CreateClient().SendAsync(request, cts.Token);
                if (response.IsSuccessStatusCode) return;
            }
            catch (Exception) when (attempt < MaxAttempts)
            {
                // Retry once, immediately — the retry may still succeed, so nothing is logged here.
            }
            catch (Exception e)
            {
                Log.Warning(e, "Flair change-ping failed after {Attempts} attempts for {Count} battleTag(s)",
                    MaxAttempts, battleTags.Count); // never logs the secret, the signature, or the tags
                return;
            }
        }

        Log.Warning("Flair change-ping rejected by chat-service after {Attempts} attempts for {Count} battleTag(s)",
            MaxAttempts, battleTags.Count);
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

```bash
cd "<website-backend worktree>"
dotnet test WC3ChampionsStatisticService.UnitTests --filter "FullyQualifiedName~FlairChangeNotifierTests"
```

Expected: `Failed: 0, Passed: 8`

- [ ] **Step 6: Commit**

```bash
git add W3C.Domain/ChatService/IFlairChangeNotifier.cs W3C.Domain/ChatService/FlairChangeNotifier.cs WC3ChampionsStatisticService.UnitTests/Friend/FlairChangeNotifierTests.cs
git commit -m "feat(chat): notify chat-service when a player's flair changes"
```

---

### Task 3: The repository decorators

**Files:**
- Create: `W3ChampionsStatisticService/PersonalSettings/FlairNotifyingPersonalSettingsRepository.cs`
- Create: `W3ChampionsStatisticService/Clans/FlairNotifyingClanRepository.cs`
- Modify: `W3ChampionsStatisticService/Program.cs` (after the registrations at lines 178 and 187; and beside the `IRelationshipChangeNotifier` registration around line 241-248)
- Test: `WC3ChampionsStatisticService.UnitTests/PersonalSettings/FlairNotifyingRepositoryTests.cs`

**Interfaces:**
- Consumes: `Decorate<TInterface, TDecorator>` (Task 1); `IFlairChangeNotifier.NotifyChanged(IReadOnlyCollection<string>)` (Task 2).
- Produces: nothing consumed by later tasks.

**Which methods notify, and why only these.** `IPersonalSettingsRepository` has eight members but only `Save` and `SaveMany` mutate. `IClanRepository` has nine but only `UpsertMemberShip` and `SaveMemberShips` change a player's *clan membership* — which is the only clan state that appears in flair. `UpsertClan` and `DeleteClan` carry no battleTags, and every path that calls them also persists the affected memberships through the two methods above, so hooking membership persistence covers the bulk clan-delete path (`ClanCommandHandler.DeleteClan` calls `SaveMemberShips` twice — once for members, once for revoked invitees) with no special-casing. Every other member is a pure read and is forwarded untouched.

The identifiers to notify are `PersonalSetting.Id` (the battleTag; `PersonalSettings/PersonalSetting.cs:42`) and `ClanMembership.BattleTag` (`Clans/ClanMembership.cs:14`).

- [ ] **Step 1: Write the failing test**

Create `WC3ChampionsStatisticService.UnitTests/PersonalSettings/FlairNotifyingRepositoryTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using W3C.Domain.ChatService;
using W3ChampionsStatisticService.Clans;
using W3ChampionsStatisticService.PersonalSettings;
using W3ChampionsStatisticService.Ports;

namespace WC3ChampionsStatisticService.UnitTests.PersonalSettings;

[TestFixture]
public class FlairNotifyingRepositoryTests
{
    private Mock<IPersonalSettingsRepository> _settingsInner;
    private Mock<IClanRepository> _clanInner;
    private Mock<IFlairChangeNotifier> _notifier;
    private List<string> _notified;

    [SetUp]
    public void SetUp()
    {
        _settingsInner = new Mock<IPersonalSettingsRepository>();
        _clanInner = new Mock<IClanRepository>();
        _notifier = new Mock<IFlairChangeNotifier>();
        _notified = new List<string>();
        _notifier.Setup(n => n.NotifyChanged(It.IsAny<IReadOnlyCollection<string>>()))
            .Callback((IReadOnlyCollection<string> tags) => _notified.AddRange(tags));
    }

    private FlairNotifyingPersonalSettingsRepository Settings() =>
        new(_settingsInner.Object, _notifier.Object);

    private FlairNotifyingClanRepository Clans() =>
        new(_clanInner.Object, _notifier.Object);

    [Test]
    public async Task Save_NotifiesTheSavedBattleTag()
    {
        await Settings().Save(new PersonalSetting("peter#123"));

        Assert.That(_notified, Is.EqualTo(new[] { "peter#123" }));
    }

    [Test]
    public async Task SaveMany_NotifiesEveryBattleTag()
    {
        await Settings().SaveMany(new List<PersonalSetting>
        {
            new("peter#123"),
            new("alice#456"),
        });

        Assert.That(_notified, Is.EqualTo(new[] { "peter#123", "alice#456" }));
    }

    [Test]
    public async Task UpsertMemberShip_NotifiesTheMember()
    {
        await Clans().UpsertMemberShip(new ClanMembership { BattleTag = "peter#123", ClanId = "W3C" });

        Assert.That(_notified, Is.EqualTo(new[] { "peter#123" }));
    }

    [Test]
    public async Task SaveMemberShips_NotifiesEveryMember()
    {
        // This is the bulk clan-delete path: ClanCommandHandler.DeleteClan persists every former member
        // through SaveMemberShips, so covering this one method covers the whole clan teardown.
        await Clans().SaveMemberShips(new List<ClanMembership>
        {
            new() { BattleTag = "peter#123" },
            new() { BattleTag = "alice#456" },
            new() { BattleTag = "bob#789" },
        });

        Assert.That(_notified, Is.EqualTo(new[] { "peter#123", "alice#456", "bob#789" }));
    }

    [Test]
    public void Save_WhenTheInnerWriteThrows_DoesNotNotify()
    {
        _settingsInner.Setup(r => r.Save(It.IsAny<PersonalSetting>())).ThrowsAsync(new InvalidOperationException("db down"));

        Assert.ThrowsAsync<InvalidOperationException>(() => Settings().Save(new PersonalSetting("peter#123")));
        Assert.That(_notified, Is.Empty);
    }

    [Test]
    public void UpsertMemberShip_WhenTheInnerWriteThrows_DoesNotNotify()
    {
        _clanInner.Setup(r => r.UpsertMemberShip(It.IsAny<ClanMembership>())).ThrowsAsync(new InvalidOperationException("db down"));

        Assert.ThrowsAsync<InvalidOperationException>(() => Clans().UpsertMemberShip(new ClanMembership { BattleTag = "peter#123" }));
        Assert.That(_notified, Is.Empty);
    }

    [Test]
    public void Save_WhenTheNotifierThrows_TheWriteStillSucceeds()
    {
        // A broken notifier must never cost a player their settings save.
        _notifier.Setup(n => n.NotifyChanged(It.IsAny<IReadOnlyCollection<string>>()))
            .Throws(new InvalidOperationException("notifier exploded"));

        Assert.DoesNotThrowAsync(() => Settings().Save(new PersonalSetting("peter#123")));
        _settingsInner.Verify(r => r.Save(It.IsAny<PersonalSetting>()), Times.Once);
    }

    [Test]
    public void SaveMemberShips_WhenTheNotifierThrows_TheWriteStillSucceeds()
    {
        _notifier.Setup(n => n.NotifyChanged(It.IsAny<IReadOnlyCollection<string>>()))
            .Throws(new InvalidOperationException("notifier exploded"));

        Assert.DoesNotThrowAsync(() => Clans().SaveMemberShips(new List<ClanMembership>
        {
            new() { BattleTag = "peter#123" },
        }));
    }

    [Test]
    public async Task ReadMethods_AreForwardedAndDoNotNotify()
    {
        _settingsInner.Setup(r => r.Load("peter#123")).ReturnsAsync(new PersonalSetting("peter#123"));

        var loaded = await Settings().Load("peter#123");

        Assert.That(loaded.Id, Is.EqualTo("peter#123"));
        _settingsInner.Verify(r => r.Load("peter#123"), Times.Once);
        Assert.That(_notified, Is.Empty);
    }

    [Test]
    public async Task DeleteClan_IsForwardedButDoesNotNotifyOnItsOwn()
    {
        // DeleteClan carries no battleTags. Its callers persist the affected memberships through
        // SaveMemberShips, which is where the notification comes from.
        await Clans().DeleteClan("W3C");

        _clanInner.Verify(r => r.DeleteClan("W3C"), Times.Once);
        Assert.That(_notified, Is.Empty);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
cd "<website-backend worktree>"
dotnet test WC3ChampionsStatisticService.UnitTests --filter "FullyQualifiedName~FlairNotifyingRepositoryTests"
```

Expected: FAIL to compile — `The type or namespace name 'FlairNotifyingPersonalSettingsRepository' could not be found`.

- [ ] **Step 3: Create the personal-settings decorator**

Create `W3ChampionsStatisticService/PersonalSettings/FlairNotifyingPersonalSettingsRepository.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
using W3C.Domain.ChatService;
using W3ChampionsStatisticService.Ports;

namespace W3ChampionsStatisticService.PersonalSettings;

/// <summary>
/// Fires a flair change-ping after a successful settings write, so chat-service can push the new
/// portrait / chat colour / chat icons to anyone currently viewing that player.
/// <para>
/// This hangs off the PERSISTENCE boundary rather than the command handlers deliberately. There are
/// five separate flair-write paths — the portrait handler, the settings controller, both reward
/// modules (which bypass the controller entirely) and the clan handler — and hooking them
/// individually means a sixth added later is silently missed. Every one of them crosses this
/// interface, so covering it here cannot be forgotten.
/// </para>
/// <para>
/// Deliberately over-notifies: it fires on ANY settings save, not only flair-relevant ones.
/// Fingerprinting the flair fields to suppress no-op pings was considered and rejected — it saves a
/// call that only happens for players currently online in chat, at the cost of real machinery and a
/// new way to be subtly wrong. Chat-side coalescing absorbs the volume.
/// </para>
/// </summary>
public class FlairNotifyingPersonalSettingsRepository(
    IPersonalSettingsRepository inner,
    IFlairChangeNotifier notifier) : IPersonalSettingsRepository
{
    public Task<PersonalSetting> Load(string battletag) => inner.Load(battletag);
    public Task<PersonalSetting> LoadOrCreate(string battletag) => inner.LoadOrCreate(battletag);
    public Task<PersonalSetting> Find(string battletag) => inner.Find(battletag);
    public Task<List<PersonalSetting>> LoadSince(DateTimeOffset from) => inner.LoadSince(from);
    public Task<List<PersonalSetting>> LoadMany(string[] battletags) => inner.LoadMany(battletags);
    public Task<List<PersonalSetting>> LoadAll() => inner.LoadAll();

    public async Task Save(PersonalSetting setting)
    {
        await inner.Save(setting);
        Notify(new[] { setting?.Id });
    }

    public async Task SaveMany(List<PersonalSetting> settings)
    {
        await inner.SaveMany(settings);
        Notify(settings?.Select(s => s?.Id).ToList());
    }

    // Notification is strictly after a successful write and can never fail it: if the inner call
    // throws we never get here, and if the notifier throws we swallow it. A player must not lose a
    // settings save because chat-service is unreachable.
    private void Notify(IReadOnlyCollection<string> battleTags)
    {
        if (battleTags == null || battleTags.Count == 0) return;

        try
        {
            notifier.NotifyChanged(battleTags);
        }
        catch (Exception e)
        {
            Log.Warning(e, "Flair change-ping dispatch failed after a personal-settings write");
        }
    }
}
```

- [ ] **Step 4: Create the clan decorator**

Create `W3ChampionsStatisticService/Clans/FlairNotifyingClanRepository.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
using W3C.Domain.ChatService;
using W3ChampionsStatisticService.Ports;

namespace W3ChampionsStatisticService.Clans;

/// <summary>
/// Fires a flair change-ping after a successful clan-membership write. Clan tag is part of a player's
/// chat flair, so joining, leaving, being kicked, or having the clan deleted under them all need to
/// reach chat-service.
/// <para>
/// Only the two MEMBERSHIP-persisting methods notify. <c>UpsertClan</c> and <c>DeleteClan</c> carry no
/// battleTags, and every path that calls them also persists the affected memberships through
/// <see cref="UpsertMemberShip"/> or <see cref="SaveMemberShips"/> — including the bulk teardown in
/// <c>ClanCommandHandler.DeleteClan</c>, which calls <see cref="SaveMemberShips"/> twice (former
/// members, then revoked invitees). So the bulk path is covered with no special-casing.
/// </para>
/// </summary>
public class FlairNotifyingClanRepository(
    IClanRepository inner,
    IFlairChangeNotifier notifier) : IClanRepository
{
    public Task TryInsertClan(Clan clan) => inner.TryInsertClan(clan);
    public Task<Clan> LoadClan(string clanId) => inner.LoadClan(clanId);
    public Task UpsertClan(Clan clan) => inner.UpsertClan(clan);
    public Task<ClanMembership> LoadMemberShip(string battleTag) => inner.LoadMemberShip(battleTag);
    public Task DeleteClan(string clanId) => inner.DeleteClan(clanId);
    public Task<List<ClanMembership>> LoadMemberShips(List<string> clanMembers) => inner.LoadMemberShips(clanMembers);
    public Task<List<ClanMembership>> LoadMemberShipsSince(DateTimeOffset from) => inner.LoadMemberShipsSince(from);

    public async Task UpsertMemberShip(ClanMembership clanMemberShip)
    {
        await inner.UpsertMemberShip(clanMemberShip);
        Notify(new[] { clanMemberShip?.BattleTag });
    }

    public async Task SaveMemberShips(List<ClanMembership> clanMembers)
    {
        await inner.SaveMemberShips(clanMembers);
        Notify(clanMembers?.Select(m => m?.BattleTag).ToList());
    }

    private void Notify(IReadOnlyCollection<string> battleTags)
    {
        if (battleTags == null || battleTags.Count == 0) return;

        try
        {
            notifier.NotifyChanged(battleTags);
        }
        catch (Exception e)
        {
            Log.Warning(e, "Flair change-ping dispatch failed after a clan-membership write");
        }
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

```bash
cd "<website-backend worktree>"
dotnet test WC3ChampionsStatisticService.UnitTests --filter "FullyQualifiedName~FlairNotifyingRepositoryTests"
```

Expected: `Failed: 0, Passed: 10`

- [ ] **Step 6: Wire the DI**

In `W3ChampionsStatisticService/Program.cs`, find the existing chat-ping block (around lines 241-248, registering `ChatPingSettings` and `IRelationshipChangeNotifier`) and add the flair notifier beside it:

```csharp
builder.Services.AddSingleton<IFlairChangeNotifier, FlairChangeNotifier>();
```

Then, **after both** `AddInterceptedTransient` calls (currently lines 178 and 187), add:

```csharp
// Flair change-pings hang off the persistence boundary rather than the five separate command paths
// that write flair — see FlairNotifyingPersonalSettingsRepository's class doc. Decorate MUST run
// after the AddInterceptedTransient registrations above: it wraps whatever is registered at the time
// it is called, and building the inner instance from that captured registration is what keeps the
// Castle tracing proxy alive underneath the decorator.
builder.Services.Decorate<IPersonalSettingsRepository, FlairNotifyingPersonalSettingsRepository>();
builder.Services.Decorate<IClanRepository, FlairNotifyingClanRepository>();
```

Add `using W3C.Domain.ChatService;`, `using W3ChampionsStatisticService.Extensions;`, `using W3ChampionsStatisticService.PersonalSettings;` and `using W3ChampionsStatisticService.Clans;` to `Program.cs` if any are missing.

- [ ] **Step 7: Run the full suite**

```bash
cd "<website-backend worktree>"
dotnet test WC3ChampionsStatisticService.UnitTests
```

Expected: the baseline you recorded before starting, plus the 23 tests added by Tasks 1-3. Report both numbers so they reconcile. `dotnet format --verify-no-changes` must also pass.

- [ ] **Step 8: Commit**

```bash
git add W3ChampionsStatisticService/PersonalSettings/FlairNotifyingPersonalSettingsRepository.cs W3ChampionsStatisticService/Clans/FlairNotifyingClanRepository.cs W3ChampionsStatisticService/Program.cs WC3ChampionsStatisticService.UnitTests/PersonalSettings/FlairNotifyingRepositoryTests.cs
git commit -m "feat(chat): ping chat-service from the settings and clan persistence boundary"
```

---

### Task 4: `FlairRefresher` — the refresh and the `FreshFromWb` rule

**Files:**
- Create: `W3ChampionsChatService/Protocol/FlairChangedDto.cs`
- Modify: `W3ChampionsChatService/Protocol/ChatEvents.cs`
- Create: `W3ChampionsChatService/Chats/UserDirectoryUpsert.cs`
- Modify: `W3ChampionsChatService/Chats/ChatHub.cs` (the private `UpsertDirectory` method, currently lines 287-306)
- Create: `W3ChampionsChatService/FanOut/FlairRefresher.cs`
- Modify: `W3ChampionsChatService/Startup.cs`
- Test: `W3ChampionsChatService.Tests/FlairRefresherTests.cs`

**Interfaces:**
- Consumes: `ISessionRegistry.GetByBattleTag(string) : ChatSession` with `ChatSession.ConnectionId` / `.Identity`; `IChatAuthenticationService.GetUserFromIdentity(W3CUserAuthentication) : Task<ChatUserResolution>` where `ChatUserResolution` is `record ChatUserResolution(ChatUser User, bool FreshFromWb)`; `ConnectionMapping.RegisterUser(string connectionId, ChatUser user)`; `FocusRegistry.GetFocusedChannels(string connectionId) : IReadOnlyCollection<string>` and `GetFocusedConnections(string channelId) : IReadOnlyCollection<string>`; `ChatProfileMapper.FromChatUser(ChatUser) : ChatProfile`; `UserDirectoryRepository.Load/Upsert`.
- Produces:
  - `record FlairChangedDto(string BattleTag, ChatProfile Profile)`
  - `ChatEvents.FlairChanged`
  - `static class UserDirectoryUpsert` with `static Task Apply(UserDirectoryRepository userDirectory, string battleTag, ChatUserResolution resolution, DateTime now)`
  - `interface IFlairRefresher { Task Refresh(string battleTag); }` and `class FlairRefresher(...) : IFlairRefresher`

- [ ] **Step 1: Write the failing test**

Create `W3ChampionsChatService.Tests/FlairRefresherTests.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.FanOut;
using W3ChampionsChatService.Protocol;
using W3ChampionsChatService.Sessions;
using W3ChampionsChatService.Users;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// The FreshFromWb rule is the single most important behaviour here: a website-backend blip must never
/// broadcast a degraded profile to every viewer in a channel.
/// </summary>
public class FlairRefresherTests : IntegrationTestBase
{
    private const string ChangedTag = "peter#123";
    private const string ViewerTag = "alice#456";

    private HubPushCaptureHarness _harness;
    private SessionRegistry _sessions;
    private ConnectionMapping _connections;
    private FocusRegistry _focus;
    private UserDirectoryRepository _userDirectory;
    private Mock<IChatAuthenticationService> _auth;
    private FlairRefresher _refresher;

    [SetUp]
    public void SetupBeforeEach()
    {
        _harness = new HubPushCaptureHarness();
        _sessions = new SessionRegistry();
        _connections = new ConnectionMapping();
        _focus = new FocusRegistry();
        _userDirectory = new UserDirectoryRepository(MongoClient);
        _auth = new Mock<IChatAuthenticationService>();

        _refresher = new FlairRefresher(
            _sessions, _auth.Object, _connections, _userDirectory, _focus,
            _harness.HubContext, new FakeTimeProvider());
    }

    private static ChatUser UserWith(string battleTag, AvatarCategory race, long pictureId) =>
        new(battleTag, false, "W3C",
            new ProfilePicture { Race = race, PictureId = pictureId, IsClassic = false },
            new ChatColor("chat_color_purple"),
            [new ChatIcon("chat_icon_crown")]);

    private void GoOnline(string connectionId, string battleTag)
    {
        _sessions.Register(connectionId, new W3CUserAuthentication { BattleTag = battleTag, Name = battleTag.Split('#')[0] }, null);
        _connections.RegisterUser(connectionId, UserWith(battleTag, AvatarCategory.HU, 1));
    }

    private void ResolvesTo(ChatUser user, bool freshFromWb) =>
        _auth.Setup(a => a.GetUserFromIdentity(It.IsAny<W3CUserAuthentication>()))
            .ReturnsAsync(new ChatUserResolution(user, freshFromWb));

    [Test]
    public async Task Refresh_WithNoLiveSession_IsANoOp()
    {
        ResolvesTo(UserWith(ChangedTag, AvatarCategory.NE, 7), true);

        await _refresher.Refresh(ChangedTag);

        _auth.Verify(a => a.GetUserFromIdentity(It.IsAny<W3CUserAuthentication>()), Times.Never);
        Assert.That(_harness.AllSignals, Is.Empty);
    }

    [Test]
    public async Task Refresh_UpdatesConnectionMappingSoTheirOwnNextMessageCarriesTheNewFlair()
    {
        GoOnline("conn-peter", ChangedTag);
        ResolvesTo(UserWith(ChangedTag, AvatarCategory.NE, 7), true);

        await _refresher.Refresh(ChangedTag);

        var cached = _connections.GetUser("conn-peter");
        Assert.That(cached.ProfilePicture.Race, Is.EqualTo(AvatarCategory.NE));
        Assert.That(cached.ProfilePicture.PictureId, Is.EqualTo(7));
    }

    [Test]
    public async Task Refresh_EmitsFlairChangedToTheChangedUsersOwnConnection_EvenWithNoFocus()
    {
        // A user focused on nothing must still see their OWN avatar update.
        GoOnline("conn-peter", ChangedTag);
        ResolvesTo(UserWith(ChangedTag, AvatarCategory.NE, 7), true);

        await _refresher.Refresh(ChangedTag);

        var signals = _harness.SignalsFor("conn-peter").Where(s => s.Method == ChatEvents.FlairChanged).ToList();
        Assert.That(signals, Has.Count.EqualTo(1));
        var payload = (FlairChangedDto)signals.Single().Payload;
        Assert.That(payload.BattleTag, Is.EqualTo(ChangedTag));
        Assert.That(payload.Profile.ProfilePicture.PictureId, Is.EqualTo(7));
        Assert.That(payload.Profile.ClanId, Is.EqualTo("W3C"));
    }

    [Test]
    public async Task Refresh_EmitsToEveryConnectionFocusedOnAChannelTheChangedUserIsFocusedOn()
    {
        GoOnline("conn-peter", ChangedTag);
        GoOnline("conn-alice", ViewerTag);
        _focus.Focus("conn-peter", "lounge", ChangedTag);
        _focus.Focus("conn-alice", "lounge", ViewerTag);
        ResolvesTo(UserWith(ChangedTag, AvatarCategory.NE, 7), true);

        await _refresher.Refresh(ChangedTag);

        Assert.That(_harness.SignalsFor("conn-alice").Count(s => s.Method == ChatEvents.FlairChanged), Is.EqualTo(1));
        Assert.That(_harness.SignalsFor("conn-peter").Count(s => s.Method == ChatEvents.FlairChanged), Is.EqualTo(1));
    }

    [Test]
    public async Task Refresh_SendsOncePerConnection_EvenWhenSharingSeveralChannels()
    {
        GoOnline("conn-peter", ChangedTag);
        GoOnline("conn-alice", ViewerTag);
        _focus.Focus("conn-peter", "lounge", ChangedTag);
        _focus.Focus("conn-peter", "clan", ChangedTag);
        _focus.Focus("conn-alice", "lounge", ViewerTag);
        _focus.Focus("conn-alice", "clan", ViewerTag);
        ResolvesTo(UserWith(ChangedTag, AvatarCategory.NE, 7), true);

        await _refresher.Refresh(ChangedTag);

        Assert.That(_harness.SignalsFor("conn-alice").Count(s => s.Method == ChatEvents.FlairChanged), Is.EqualTo(1));
    }

    [Test]
    public async Task Refresh_WhenNotFreshFromWb_DoesNothingAtAll()
    {
        // THE RULE. A wb blip resolves to a degraded tier-3 profile. Acting on it would replace good
        // cached flair and broadcast the default avatar to everyone viewing this user — turning a
        // transient upstream hiccup into a visible regression for the whole channel.
        GoOnline("conn-peter", ChangedTag);
        GoOnline("conn-alice", ViewerTag);
        _focus.Focus("conn-peter", "lounge", ChangedTag);
        _focus.Focus("conn-alice", "lounge", ViewerTag);

        var degraded = new ChatUser(ChangedTag, false, null, new ProfilePicture(), null, null);
        ResolvesTo(degraded, false);

        await _refresher.Refresh(ChangedTag);

        Assert.That(_harness.AllSignals, Is.Empty, "no FlairChanged may be emitted on a stale resolution");

        var cached = _connections.GetUser("conn-peter");
        Assert.That(cached.ProfilePicture.Race, Is.EqualTo(AvatarCategory.HU),
            "the good cached ChatUser must survive — never clobbered by a degraded resolution");
        Assert.That(cached.ClanTag, Is.EqualTo("W3C"));

        var entry = await _userDirectory.Load(ChangedTag);
        Assert.That(entry, Is.Null, "no directory write may happen on a stale resolution");
    }

    [Test]
    public async Task Refresh_WhenFreshFromWb_WritesTheDirectoryProfile()
    {
        GoOnline("conn-peter", ChangedTag);
        ResolvesTo(UserWith(ChangedTag, AvatarCategory.NE, 7), true);

        await _refresher.Refresh(ChangedTag);

        var entry = await _userDirectory.Load(ChangedTag);
        Assert.That(entry, Is.Not.Null);
        Assert.That(entry.Profile.ProfilePicture.PictureId, Is.EqualTo(7));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
cd "<chat-service worktree>"
dotnet test --filter "FullyQualifiedName~FlairRefresherTests" --nologo
```

Expected: FAIL to compile — `The type or namespace name 'FlairRefresher' could not be found`.

- [ ] **Step 3: Add the event name and the payload DTO**

In `W3ChampionsChatService/Protocol/ChatEvents.cs`, add this constant after `FriendPresenceChanged` (before the closing brace):

```csharp
    /// <summary>C7: pushed when a player's flair (portrait, chat colour, chat icons, clan) changes,
    /// to every connection focused on a channel that player is also focused on, plus the player's own
    /// connection. Carries a <c>FlairChangedDto</c>.</summary>
    public const string FlairChanged = nameof(FlairChanged);
```

Create `W3ChampionsChatService/Protocol/FlairChangedDto.cs`:

```csharp
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Protocol;

/// <summary>
/// A live flair update for one player, pushed when website-backend reports that their portrait, chat
/// colour, chat icons or clan changed.
/// <para>
/// <see cref="Profile"/> is built by the same <see cref="ChatProfileMapper.FromChatUser"/> that
/// supplies roster flair and message <c>sender.flair</c>, so a live update cannot render differently
/// from what a fresh roster would have shown. It is never null: this event is only emitted after a
/// resolution that was confirmed fresh from website-backend.
/// </para>
/// </summary>
public record FlairChangedDto(string BattleTag, ChatProfile Profile);
```

- [ ] **Step 4: Extract the directory upsert**

Create `W3ChampionsChatService/Chats/UserDirectoryUpsert.cs`:

```csharp
using System;
using System.Threading.Tasks;
using Serilog;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Users;

namespace W3ChampionsChatService.Chats;

/// <summary>
/// The user-directory write shared by the connect path (<c>ChatHub.UpsertDirectory</c>) and the live
/// flair-refresh path (<c>FanOut.FlairRefresher</c>).
/// <para>
/// Extracted so the NEVER-CLOBBER rule exists once: identity fields are always refreshed, but
/// <see cref="UserDirectoryEntry.Profile"/> is replaced ONLY when the resolution came fresh from
/// website-backend. Two copies of this rule would be two chances to get it wrong, and the failure
/// mode — overwriting a good cached profile with a degraded one — is invisible until a user complains
/// that their avatar reverted.
/// </para>
/// <para>
/// Non-fatal by design: a directory write failure is logged and swallowed. Neither caller should fail
/// because a cache update did.
/// </para>
/// </summary>
public static class UserDirectoryUpsert
{
    public static async Task Apply(
        UserDirectoryRepository userDirectory,
        string battleTag,
        ChatUserResolution resolution,
        DateTime now)
    {
        try
        {
            var entry = await userDirectory.Load(battleTag)
                ?? new UserDirectoryEntry { BattleTag = battleTag };
            entry.DisplayBattleTag = battleTag;
            entry.NormalizedName = battleTag?.Trim().ToLowerInvariant();
            entry.LastSeenAt = now;
            if (resolution.FreshFromWb)
            {
                entry.Profile = ChatProfileMapper.FromChatUser(resolution.User);
            }
            await userDirectory.Upsert(entry);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to upsert user_directory entry for {BattleTag}", battleTag);
        }
    }
}
```

Then in `W3ChampionsChatService/Chats/ChatHub.cs`, replace the entire body of the private `UpsertDirectory` method (currently lines 287-306) so it delegates, keeping the existing method and its call site unchanged:

```csharp
    private Task UpsertDirectory(W3CUserAuthentication identity, ChatUserResolution resolution, DateTime now) =>
        UserDirectoryUpsert.Apply(_userDirectory, identity.BattleTag, resolution, now);
```

- [ ] **Step 5: Create the refresher**

Create `W3ChampionsChatService/FanOut/FlairRefresher.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Serilog;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Protocol;
using W3ChampionsChatService.Sessions;
using W3ChampionsChatService.Users;

namespace W3ChampionsChatService.FanOut;

public interface IFlairRefresher
{
    Task Refresh(string battleTag);
}

/// <summary>
/// Re-resolves one player's flair after website-backend reports a change, then pushes it to everyone
/// who can currently see them.
/// <para>
/// Reuses <see cref="IChatAuthenticationService.GetUserFromIdentity"/> rather than reading settings
/// directly, so admin colour/icon forcing, the three-tier fallback and the never-clobber invariant all
/// come for free and cannot drift from the connect path. Because it also refreshes
/// <see cref="ConnectionMapping"/>, the changed player's own subsequent messages carry the new flair
/// within the same connection.
/// </para>
/// <para>
/// A player with no live session is a no-op: their next connect re-enriches anyway. Work is therefore
/// bounded by the set of players currently online in chat, not by website-backend's write volume.
/// </para>
/// </summary>
public class FlairRefresher(
    ISessionRegistry sessionRegistry,
    IChatAuthenticationService chatAuthenticationService,
    ConnectionMapping connections,
    UserDirectoryRepository userDirectory,
    FocusRegistry focusRegistry,
    IHubContext<ChatHub> hubContext,
    TimeProvider timeProvider) : IFlairRefresher
{
    private readonly ISessionRegistry _sessionRegistry = sessionRegistry;
    private readonly IChatAuthenticationService _chatAuthenticationService = chatAuthenticationService;
    private readonly ConnectionMapping _connections = connections;
    private readonly UserDirectoryRepository _userDirectory = userDirectory;
    private readonly FocusRegistry _focusRegistry = focusRegistry;
    private readonly IHubContext<ChatHub> _hubContext = hubContext;
    private readonly TimeProvider _timeProvider = timeProvider;

    public async Task Refresh(string battleTag)
    {
        var session = _sessionRegistry.GetByBattleTag(battleTag);
        if (session == null) return;

        var resolution = await _chatAuthenticationService.GetUserFromIdentity(session.Identity);

        // THE RULE (spec §5). A wb blip degrades to a tier-3 profile with FreshFromWb false. Acting on
        // it would replace a good cached ChatUser and broadcast the default avatar to every viewer —
        // converting a transient upstream hiccup into a visible regression for the whole channel. Doing
        // nothing costs nothing: the next successful ping, or the player's next connect, re-enriches.
        if (!resolution.FreshFromWb) return;

        _connections.RegisterUser(session.ConnectionId, resolution.User);

        await UserDirectoryUpsert.Apply(
            _userDirectory, battleTag, resolution, _timeProvider.GetUtcNow().UtcDateTime);

        var payload = new FlairChangedDto(battleTag, ChatProfileMapper.FromChatUser(resolution.User));

        // Flair is user-scoped, not channel-scoped: the audience is every connection focused on any
        // channel this player is focused on, deduped, plus their own connection unconditionally so a
        // player focused on nothing still sees their own avatar update.
        var targets = new HashSet<string>(StringComparer.Ordinal) { session.ConnectionId };
        foreach (var channelId in _focusRegistry.GetFocusedChannels(session.ConnectionId))
        {
            foreach (var connectionId in _focusRegistry.GetFocusedConnections(channelId))
            {
                targets.Add(connectionId);
            }
        }

        foreach (var connectionId in targets)
        {
            try
            {
                await _hubContext.Clients.Client(connectionId).SendAsync(ChatEvents.FlairChanged, payload);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Fan-out send of FlairChanged failed for connection {ConnectionId} — skipping", connectionId);
            }
        }
    }
}
```

- [ ] **Step 6: Register it**

In `W3ChampionsChatService/Startup.cs`, immediately after the `ViewerResolver` registration (currently line 181):

```csharp
        // Singleton: holds only singletons plus the hub context. Consumed by FlairRefreshCoalescer.
        services.AddSingleton<IFlairRefresher, FlairRefresher>();
```

- [ ] **Step 7: Run the tests to verify they pass**

```bash
cd "<chat-service worktree>"
dotnet test --filter "FullyQualifiedName~FlairRefresherTests" --nologo
```

Expected: `Failed: 0, Passed: 7`

- [ ] **Step 8: Run the full suite**

```bash
cd "<chat-service worktree>"
dotnet test --nologo
```

Expected: `Failed: 0, Passed: 1365` (the 1358 baseline plus 7). The `ChatHub.UpsertDirectory` extraction is behaviour-preserving, so no existing test may change.

- [ ] **Step 9: Commit**

```bash
git add W3ChampionsChatService/Protocol/FlairChangedDto.cs W3ChampionsChatService/Protocol/ChatEvents.cs W3ChampionsChatService/Chats/UserDirectoryUpsert.cs W3ChampionsChatService/Chats/ChatHub.cs W3ChampionsChatService/FanOut/FlairRefresher.cs W3ChampionsChatService/Startup.cs W3ChampionsChatService.Tests/FlairRefresherTests.cs
git commit -m "feat(chat): re-resolve and push flair when website-backend reports a change"
```

---

### Task 5: `FlairRefreshCoalescer`

**Files:**
- Create: `W3ChampionsChatService/FanOut/FlairRefreshCoalescer.cs`
- Modify: `W3ChampionsChatService/Domain/ChatLimits.cs`
- Modify: `W3ChampionsChatService/FanOut/FanOutFlushService.cs`
- Modify: `W3ChampionsChatService/Startup.cs`
- Test: `W3ChampionsChatService.Tests/FlairRefreshCoalescerTests.cs`

**Interfaces:**
- Consumes: `IFlairRefresher.Refresh(string battleTag) : Task` (Task 4).
- Produces: `class FlairRefreshCoalescer(IFlairRefresher refresher)` with `void RecordChange(string battleTag)`, `Task Flush()`, and `internal int PendingCount`.

`Flush()` deliberately takes no `now` parameter, unlike its two siblings. They coalesce over a multi-second window and need the clock to decide what is due; this one's window *is* the flush tick, so every pending tag is due on every flush and a `now` argument would be dead weight.

- [ ] **Step 1: Write the failing test**

Create `W3ChampionsChatService.Tests/FlairRefreshCoalescerTests.cs`:

```csharp
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.FanOut;

namespace W3ChampionsChatService.Tests;

public class FlairRefreshCoalescerTests
{
    private class RecordingRefresher : IFlairRefresher
    {
        public List<string> Refreshed { get; } = new();
        public bool Throw { get; set; }

        public Task Refresh(string battleTag)
        {
            Refreshed.Add(battleTag);
            if (Throw) throw new System.InvalidOperationException("refresh exploded");
            return Task.CompletedTask;
        }
    }

    private RecordingRefresher _refresher;
    private FlairRefreshCoalescer _coalescer;

    [SetUp]
    public void SetupBeforeEach()
    {
        _refresher = new RecordingRefresher();
        _coalescer = new FlairRefreshCoalescer(_refresher);
    }

    [Test]
    public async Task Flush_RefreshesEachRecordedBattleTagOnce()
    {
        _coalescer.RecordChange("peter#123");
        _coalescer.RecordChange("alice#456");

        await _coalescer.Flush();

        Assert.That(_refresher.Refreshed, Is.EquivalentTo(new[] { "peter#123", "alice#456" }));
    }

    [Test]
    public async Task Flush_CollapsesABurstForOneBattleTagIntoASingleRefresh()
    {
        // Five writes in one tick — e.g. a reward grant that touches colour then icons — must cost one
        // website-backend round-trip, not five.
        for (var i = 0; i < 5; i++) _coalescer.RecordChange("peter#123");

        await _coalescer.Flush();

        Assert.That(_refresher.Refreshed, Is.EqualTo(new[] { "peter#123" }));
    }

    [Test]
    public async Task RecordChange_IsCaseInsensitive()
    {
        _coalescer.RecordChange("Peter#123");
        _coalescer.RecordChange("peter#123");

        await _coalescer.Flush();

        Assert.That(_refresher.Refreshed, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Flush_DrainsThePendingSet()
    {
        _coalescer.RecordChange("peter#123");
        await _coalescer.Flush();
        await _coalescer.Flush();

        Assert.That(_refresher.Refreshed, Is.EqualTo(new[] { "peter#123" }));
        Assert.That(_coalescer.PendingCount, Is.EqualTo(0));
    }

    [Test]
    public void RecordChange_AtCapacity_DropsRatherThanGrows()
    {
        for (var i = 0; i < ChatLimits.FlairRefreshPendingCap + 50; i++)
        {
            _coalescer.RecordChange($"player{i}#1");
        }

        Assert.That(_coalescer.PendingCount, Is.EqualTo(ChatLimits.FlairRefreshPendingCap));
    }

    [Test]
    public void RecordChange_IgnoresBlankTags()
    {
        _coalescer.RecordChange(null);
        _coalescer.RecordChange("   ");

        Assert.That(_coalescer.PendingCount, Is.EqualTo(0));
    }

    [Test]
    public async Task Flush_WhenOneRefreshThrows_StillRefreshesTheRest()
    {
        _refresher.Throw = true;
        _coalescer.RecordChange("peter#123");
        _coalescer.RecordChange("alice#456");

        Assert.DoesNotThrowAsync(() => _coalescer.Flush());
        await Task.CompletedTask;

        Assert.That(_refresher.Refreshed, Has.Count.EqualTo(2),
            "one player's failed refresh must not cancel everyone else's");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
cd "<chat-service worktree>"
dotnet test --filter "FullyQualifiedName~FlairRefreshCoalescerTests" --nologo
```

Expected: FAIL to compile — `The type or namespace name 'FlairRefreshCoalescer' could not be found`.

- [ ] **Step 3: Add the cap constant**

In `W3ChampionsChatService/Domain/ChatLimits.cs`, add beside the other fan-out limits:

```csharp
    /// <summary>
    /// Maximum battleTags the flair-refresh coalescer will hold between flushes. At the cap it DROPS
    /// new tags rather than growing: a dropped refresh degrades to the reconnect backstop, whereas an
    /// unbounded set would let a website-backend write storm consume memory here.
    /// </summary>
    public const int FlairRefreshPendingCap = 512;
```

- [ ] **Step 4: Create the coalescer**

Create `W3ChampionsChatService/FanOut/FlairRefreshCoalescer.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Serilog;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.FanOut;

/// <summary>
/// Collapses a burst of flair-change notifications for the same player into one refresh per flush
/// tick. website-backend deliberately over-notifies (it pings on any settings save, not only
/// flair-relevant ones), and a single user action can cross the persistence boundary several times —
/// this is where that volume is absorbed.
/// <para>
/// Mirrors the discipline of <see cref="ActivityCoalescer"/> and <see cref="ViewersAccumulator"/>:
/// mutate state under one lock, do the work outside it, fault-isolate per item.
/// </para>
/// </summary>
public class FlairRefreshCoalescer(IFlairRefresher refresher)
{
    private readonly IFlairRefresher _refresher = refresher;
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>Test seam.</summary>
    internal int PendingCount
    {
        get { lock (_lock) { return _pending.Count; } }
    }

    public void RecordChange(string battleTag)
    {
        if (string.IsNullOrWhiteSpace(battleTag)) return;

        lock (_lock)
        {
            // At the cap, drop rather than grow. Dropping degrades to the reconnect backstop; growing
            // without bound would let an upstream write storm become a memory problem here.
            if (_pending.Count >= ChatLimits.FlairRefreshPendingCap && !_pending.Contains(battleTag))
            {
                return;
            }

            _pending.Add(battleTag);
        }
    }

    public async Task Flush()
    {
        List<string> due;
        lock (_lock)
        {
            if (_pending.Count == 0) return;
            due = new List<string>(_pending);
            _pending.Clear();
        }

        foreach (var battleTag in due)
        {
            try
            {
                await _refresher.Refresh(battleTag);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Flair refresh failed for {BattleTag} — skipping, the next connect re-enriches", battleTag);
            }
        }
    }
}
```

- [ ] **Step 5: Drive it from the flush host**

In `W3ChampionsChatService/FanOut/FanOutFlushService.cs`, add the coalescer as a third constructor parameter and give it its own independent try/catch, matching the existing two:

```csharp
public class FanOutFlushService(
    ActivityCoalescer coalescer,
    ViewersAccumulator accumulator,
    FlairRefreshCoalescer flairRefreshCoalescer,
    TimeProvider timeProvider) : BackgroundService
```

and inside the `do` block, after the `accumulator.FlushDue(now)` try/catch:

```csharp
            try
            {
                await flairRefreshCoalescer.Flush();
            }
            catch (Exception e)
            {
                Log.Error(e, "FlairRefreshCoalescer flush failed; will retry next tick");
            }
```

- [ ] **Step 6: Register it**

In `W3ChampionsChatService/Startup.cs`, immediately after the `IFlairRefresher` registration added in Task 4:

```csharp
        services.AddSingleton<FlairRefreshCoalescer>();
```

- [ ] **Step 7: Run the tests to verify they pass**

```bash
cd "<chat-service worktree>"
dotnet test --filter "FullyQualifiedName~FlairRefreshCoalescerTests" --nologo
```

Expected: `Failed: 0, Passed: 7`

- [ ] **Step 8: Run the full suite**

```bash
cd "<chat-service worktree>"
dotnet test --nologo
```

Expected: `Failed: 0, Passed: 1372`. If any `FanOutFlushService` test fails to compile, it constructs the service directly and needs the new argument — add it, and report which tests you touched.

- [ ] **Step 9: Commit**

```bash
git add W3ChampionsChatService/FanOut/FlairRefreshCoalescer.cs W3ChampionsChatService/Domain/ChatLimits.cs W3ChampionsChatService/FanOut/FanOutFlushService.cs W3ChampionsChatService/Startup.cs W3ChampionsChatService.Tests
git commit -m "feat(chat): coalesce flair-change notifications into one refresh per tick"
```

---

### Task 6: `POST /internal/profile-changes`

**Files:**
- Create: `W3ChampionsChatService/Internal/InternalValidation.cs`
- Modify: `W3ChampionsChatService/Internal/InternalRelationshipChangesController.cs` (the private `IsValidParticipant`, currently lines 77-78)
- Modify: `W3ChampionsChatService/Internal/InternalChannelsController.cs` (the private `IsValidMemberEntry`, currently lines 302-303)
- Create: `W3ChampionsChatService/Internal/InternalProfileChangesController.cs`
- Modify: `W3ChampionsChatService/Internal/InternalDtos.cs`
- Test: `W3ChampionsChatService.Tests/InternalProfileChangesControllerTests.cs`

**Interfaces:**
- Consumes: `FlairRefreshCoalescer.RecordChange(string battleTag)` (Task 5); `ChatLimits.InternalMaxMembersPerCall` (64).
- Produces: `static class InternalValidation` with `static bool IsValidBattleTag(string value)`; `class InternalProfileChangeRequest { public List<string> BattleTags { get; set; } }`.

The blank/control-char check already exists as two byte-identical private copies (`InternalRelationshipChangesController.IsValidParticipant` and `InternalChannelsController.IsValidMemberEntry`, the latter commented "mirrors ... EXACTLY"). This task factors it out rather than adding a third copy — a rule that must hold identically across every internal endpoint should exist once.

The new controller needs no work to satisfy `InternalChannelsControllerTests`' HMAC sweep: that sweep discovers controllers dynamically off the compiled assembly by namespace or `internal/` route prefix, so living in `W3ChampionsChatService.Internal` is enough. It will *fail* the sweep if the class-level `[InternalHmacAuth(...)]` attribute is missing or its allow-list is empty.

- [ ] **Step 1: Write the failing test**

Create `W3ChampionsChatService.Tests/InternalProfileChangesControllerTests.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using NUnit.Framework;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.FanOut;
using W3ChampionsChatService.Internal;

namespace W3ChampionsChatService.Tests;

public class InternalProfileChangesControllerTests
{
    private class NoOpRefresher : IFlairRefresher
    {
        public System.Threading.Tasks.Task Refresh(string battleTag) => System.Threading.Tasks.Task.CompletedTask;
    }

    private FlairRefreshCoalescer _coalescer;
    private InternalProfileChangesController _controller;

    [SetUp]
    public void SetupBeforeEach()
    {
        _coalescer = new FlairRefreshCoalescer(new NoOpRefresher());
        _controller = new InternalProfileChangesController(_coalescer);
    }

    private static InternalProfileChangeRequest Request(params string[] battleTags) =>
        new() { BattleTags = battleTags.ToList() };

    [Test]
    public void Post_EnqueuesEveryBattleTag()
    {
        var result = _controller.Post(Request("peter#123", "alice#456"));

        Assert.That(result, Is.InstanceOf<OkResult>());
        Assert.That(_coalescer.PendingCount, Is.EqualTo(2));
    }

    [Test]
    public void Post_AtTheCap_IsAccepted()
    {
        var tags = Enumerable.Range(0, ChatLimits.InternalMaxMembersPerCall).Select(i => $"player{i}#1").ToArray();

        Assert.That(_controller.Post(Request(tags)), Is.InstanceOf<OkResult>());
    }

    [Test]
    public void Post_OverTheCap_IsRejectedWithNoPartialProcessing()
    {
        var tags = Enumerable.Range(0, ChatLimits.InternalMaxMembersPerCall + 1).Select(i => $"player{i}#1").ToArray();

        Assert.That(_controller.Post(Request(tags)), Is.InstanceOf<BadRequestObjectResult>());
        Assert.That(_coalescer.PendingCount, Is.EqualTo(0));
    }

    [Test]
    public void Post_WithNullRequest_IsRejected()
    {
        Assert.That(_controller.Post(null), Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public void Post_WithNoBattleTags_IsRejected()
    {
        Assert.That(_controller.Post(new InternalProfileChangeRequest { BattleTags = null }), Is.InstanceOf<BadRequestObjectResult>());
        Assert.That(_controller.Post(Request()), Is.InstanceOf<BadRequestObjectResult>());
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("peter\u0000123")]
    [TestCase("peter\u2028123")]
    [TestCase("peter\u2029123")]
    public void Post_WithAnInvalidBattleTag_RejectsTheWholeBatch(string invalid)
    {
        // No partial processing: one bad entry rejects the batch, and nothing is enqueued.
        Assert.That(_controller.Post(Request("peter#123", invalid)), Is.InstanceOf<BadRequestObjectResult>());
        Assert.That(_coalescer.PendingCount, Is.EqualTo(0));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
cd "<chat-service worktree>"
dotnet test --filter "FullyQualifiedName~InternalProfileChangesControllerTests" --nologo
```

Expected: FAIL to compile — `The type or namespace name 'InternalProfileChangesController' could not be found`.

- [ ] **Step 3: Factor out the shared validator**

Create `W3ChampionsChatService/Internal/InternalValidation.cs`:

```csharp
using System.Linq;

namespace W3ChampionsChatService.Internal;

/// <summary>
/// Validation shared by every <c>internal/*</c> endpoint. This rule must hold IDENTICALLY across all
/// of them, so it lives in one place — it previously existed as two byte-identical private copies,
/// one of which carried a comment promising it mirrored the other exactly.
/// </summary>
public static class InternalValidation
{
    /// <summary>
    /// A usable battleTag: non-blank, and free of control characters. U+2028 and U+2029 are checked
    /// explicitly because <c>char.IsControl</c> classifies them as separators, not controls, yet they
    /// terminate lines in JavaScript sources and log viewers.
    /// </summary>
    public static bool IsValidBattleTag(string value) =>
        !string.IsNullOrWhiteSpace(value) && !value.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029');
}
```

In `W3ChampionsChatService/Internal/InternalRelationshipChangesController.cs`, replace the private helper (currently lines 77-78) with a delegation, leaving both call sites unchanged:

```csharp
    private static bool IsValidParticipant(string value) => InternalValidation.IsValidBattleTag(value);
```

In `W3ChampionsChatService/Internal/InternalChannelsController.cs`, do the same to `IsValidMemberEntry` (currently lines 302-303), and delete the now-inaccurate comment above it that describes mirroring the other controller's copy:

```csharp
    private static bool IsValidMemberEntry(string value) => InternalValidation.IsValidBattleTag(value);
```

- [ ] **Step 4: Add the request DTO**

In `W3ChampionsChatService/Internal/InternalDtos.cs`, add beside `InternalRelationshipChangeRequest`:

```csharp
/// <summary>
/// Body of <c>POST /internal/profile-changes</c>: the players whose flair website-backend believes
/// may have changed. Capped at <see cref="Domain.ChatLimits.InternalMaxMembersPerCall"/>; the sender
/// chunks larger sets into separate requests.
/// </summary>
public class InternalProfileChangeRequest
{
    public List<string> BattleTags { get; set; }
}
```

Ensure the file has `using System.Collections.Generic;`.

- [ ] **Step 5: Create the controller**

Create `W3ChampionsChatService/Internal/InternalProfileChangesController.cs`:

```csharp
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Serilog;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.FanOut;

namespace W3ChampionsChatService.Internal;

/// <summary>
/// Receives flair-change notifications from website-backend. Enqueues each battleTag for a coalesced
/// refresh and returns immediately — the sender is fire-and-forget with a 3 s per-attempt budget, so
/// this must never do the refresh inline.
/// </summary>
[ApiController]
[Route("internal/profile-changes")]
[InternalHmacAuth(InternalCaller.Wb)]
public class InternalProfileChangesController(FlairRefreshCoalescer coalescer) : ControllerBase
{
    private const string GenericValidationError = "Invalid request.";

    [HttpPost]
    public IActionResult Post([FromBody] InternalProfileChangeRequest request)
    {
        // Validate the WHOLE batch before enqueuing any of it — no partial processing, so a malformed
        // request can never leave the coalescer holding half a batch.
        if (request?.BattleTags == null
            || request.BattleTags.Count == 0
            || request.BattleTags.Count > ChatLimits.InternalMaxMembersPerCall
            || request.BattleTags.Any(tag => !InternalValidation.IsValidBattleTag(tag)))
        {
            return BadRequest(new ErrorResult(GenericValidationError));
        }

        foreach (var battleTag in request.BattleTags)
        {
            coalescer.RecordChange(battleTag);
        }

        Log.Information("Internal profile change {Caller} count={Count}",
            InternalHmacAuthFilter.ResolveCaller(HttpContext), request.BattleTags.Count);

        return Ok();
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

```bash
cd "<chat-service worktree>"
dotnet test --filter "FullyQualifiedName~InternalProfileChangesControllerTests" --nologo
```

Expected: `Failed: 0, Passed: 10`

- [ ] **Step 7: Confirm the HMAC sweep covers the new controller**

```bash
cd "<chat-service worktree>"
dotnet test --filter "FullyQualifiedName~InternalChannelsControllerTests.InternalControllers" --nologo
```

Expected: PASS. These tests discover internal controllers by reflection, so they now cover the new one automatically. To prove the sweep is really exercising it, temporarily delete the `[InternalHmacAuth(InternalCaller.Wb)]` line, re-run, confirm it FAILS naming `InternalProfileChangesController`, then restore it. Record that RED/GREEN in your report.

- [ ] **Step 8: Run the full suite**

```bash
cd "<chat-service worktree>"
dotnet test --nologo
```

Expected: `Failed: 0, Passed: 1382`

- [ ] **Step 9: Commit**

```bash
git add W3ChampionsChatService/Internal W3ChampionsChatService.Tests/InternalProfileChangesControllerTests.cs
git commit -m "feat(chat): accept flair-change notifications on /internal/profile-changes"
```

---

### Task 7: launcher-e handles `FlairChanged`

**Files:**
- Modify: `src/types/chat-protocol.types.ts` (the `EChatHubEvent` object, currently lines 352-369; add the DTO near the other event payloads)
- Modify: `src/models/chat-core.ts` (the `ChatCoreStoreModel` action declarations around lines 185-192; the action implementations beside `ingestViewersChanged`)
- Modify: `src/services/chat.service.ts` (the `bindEvents` method, currently around lines 322-376)

**Interfaces:**
- Consumes: the Task 4 wire shape — `FlairChanged` carrying `{ battleTag: string, profile: IChatProfileDto }`.
- Produces: nothing consumed by later tasks.

All work is in the launcher-e worktree. There is no test runner; correctness rests on `tsc` plus the manual checks in Task 8.

- [ ] **Step 1: Add the event name**

In `src/types/chat-protocol.types.ts`, add to the `EChatHubEvent` object after `FriendPresenceChanged`:

```ts
    FlairChanged: "FlairChanged",
```

- [ ] **Step 2: Add the payload DTO**

In the same file, immediately after the `IFriendPresenceChangedDto` declaration:

```ts
/**
 * Wire event payload: `FlairChanged`. Pushed when a player's flair (portrait,
 * chat colour, chat icons, clan) changes, to every connection focused on a
 * channel that player is also focused on, plus the player's own connection.
 *
 * `profile` is built server-side by the same mapper that supplies roster flair
 * and message `sender.flair`, so a live update cannot render differently from
 * what a fresh roster would have shown. It is never null — the server only
 * emits this event after a resolution confirmed fresh from website-backend.
 */
export interface IFlairChangedDto {
    battleTag: string;
    profile: IChatProfileDto;
}
```

- [ ] **Step 3: Declare the action**

In `src/models/chat-core.ts`, add to the `ChatCoreStoreModel` interface immediately after the `ingestViewersChanged` declaration (currently line 188):

```ts
    ingestFlairChanged: Action<ChatStoreModel, IFlairChangedDto>;
```

Add `IFlairChangedDto` to the existing `@/types/chat-protocol.types` import in this file.

- [ ] **Step 4: Implement the action**

In the same file, immediately after the `ingestViewersChanged` action implementation:

```ts
        ingestFlairChanged: action((state, payload) => {
            // A live source, so it may write the flair slice — unlike message senders, whose flair is
            // frozen at send time. Unconditional overwrite: this event is only emitted after the server
            // confirmed a fresh resolution, so it is always newer than whatever is cached here.
            if (!payload.profile) return;
            state.flairByBattleTag[payload.battleTag.toLowerCase()] = payload.profile;
        }),
```

- [ ] **Step 5: Bind the event**

In `src/services/chat.service.ts`, inside `bindEvents`, add beside the other store-routed registrations (next to the `ViewersChanged` line):

```ts
        connection.on(EChatHubEvent.FlairChanged, (dto: IFlairChangedDto) => this.actions.ingestFlairChanged(dto));
```

Add `IFlairChangedDto` to the existing `@/types/chat-protocol.types` import in this file.

- [ ] **Step 6: Verify**

```bash
cd "<launcher-e worktree>"
npm run type-check
```

Expected: no output, exit 0.

```bash
cd "<launcher-e worktree>"
npm run lint:prod
```

Expected: no output, exit 0.

```bash
cd "<launcher-e worktree>"
npm run dprint
```

Expected: no output, exit 0. Run `npm run dprint:fix` if it reports differences. dprint here uses 4-space indent, double quotes, and effectively unlimited line width.

```bash
cd "<launcher-e worktree>"
npm run check:i18n
```

Expected: `0 issues`.

- [ ] **Step 7: Commit**

```bash
cd "<launcher-e worktree>"
git add src/types/chat-protocol.types.ts src/models/chat-core.ts src/services/chat.service.ts
git commit -m "feat(chat): apply live flair updates to the roster"
```

---

### Task 8: End-to-end verification

**Files:** none modified.

**Interfaces:**
- Consumes: everything from Tasks 1-7.
- Produces: nothing.

Every preceding task verified one side of a boundary in isolation. Nothing so far has run a real website-backend against a real chat-service against a real client.

- [ ] **Step 1: Full website-backend suite**

```bash
cd "<website-backend worktree>"
dotnet test WC3ChampionsStatisticService.UnitTests
```

Expected: the recorded baseline plus 23. Report both numbers.

```bash
cd "<website-backend worktree>"
dotnet format --verify-no-changes --verbosity diagnostic
```

Expected: exit 0.

- [ ] **Step 2: Full chat-service suite**

```bash
cd "<chat-service worktree>"
dotnet test --nologo
```

Expected: `Failed: 0, Passed: 1382`

- [ ] **Step 3: Full launcher-e verification set**

Run each separately from the launcher-e worktree: `npm run type-check`, `npm run lint:prod`, `npm run dprint`, `npm run check:i18n`. All four exit 0.

- [ ] **Step 4: Confirm the notification cannot be bypassed**

```bash
cd "<website-backend worktree>"
grep -rn "IPersonalSettingsRepository\|IClanRepository" W3ChampionsStatisticService/Program.cs
```

Expected: the two `AddInterceptedTransient` registrations, followed by the two `Decorate` calls. If any code resolves `PersonalSettingsRepository` or `ClanRepository` by **concrete type** it would bypass the decorator entirely — check for that too:

```bash
cd "<website-backend worktree>"
grep -rn "GetRequiredService<PersonalSettingsRepository>\|GetRequiredService<ClanRepository>\|new PersonalSettingsRepository(\|new ClanRepository(" --include=*.cs W3ChampionsStatisticService W3C.Domain
```

Expected: no matches outside the DI extension itself. Any hit is a write path that will never notify — report it.

- [ ] **Step 5: Confirm the `FreshFromWb` rule has no bypass**

```bash
cd "<chat-service worktree>"
grep -n "FreshFromWb" W3ChampionsChatService/FanOut/FlairRefresher.cs W3ChampionsChatService/Chats/UserDirectoryUpsert.cs
```

Expected: the guard in `FlairRefresher.Refresh` and the conditional in `UserDirectoryUpsert.Apply`. Confirm no code path in `FlairRefresher` reaches `RegisterUser`, the directory write, or a `SendAsync` before that guard.

- [ ] **Step 6: Manual smoke test**

Run all three services together with `CHAT_INTERNAL_API_SECRET` set to the same value on both sides, and confirm:

1. Player A and player B are both in the Lounge, both focused. A changes their portrait on the website. Within ~2 seconds B sees A's avatar change **without either reconnecting**.
2. A's chat colour and icons update the same way.
3. A joins or leaves a clan; B sees A's clan tag update live.
4. A deletes a clan with more than 64 members; every former member's flair updates (this exercises the notifier's chunking).
5. A's own next message carries the new flair — confirming `ConnectionMapping` was refreshed, not just the roster.
6. A player with no channel focused still sees their **own** avatar update.
7. Stop website-backend's chat pings by unsetting `CHAT_INTERNAL_API_SECRET` and restarting it: everything still works exactly as it does today, with one startup log line saying pings are disabled.
8. **The `FreshFromWb` case:** make website-backend's `clan-and-picture` endpoint fail (stop the service or block the route), then trigger a flair change ping by hand. Confirm no `FlairChanged` is emitted and **no viewer's avatar changes to the sheep**. This is the one scenario where this feature could make production worse than before it existed.

- [ ] **Step 7: Report**

Report the two suite counts, the four launcher-e exit codes, the two grep results, and the outcome of each of the eight manual checks. Do not claim completion without the manual results — no automated test in this plan exercises all three services together.

---

## Rollout

This plan is step 4 of the spec's §8 rollout, and steps 1-3 have already shipped. It is deployable **dark**: without `CHAT_INTERNAL_API_SECRET` the notifier self-disables and nothing changes. Deploy chat-service first (the endpoint is inert until something calls it), then website-backend, then set the secret to enable.

Unlike Plan A, nothing here is a breaking wire change. `FlairChanged` is a new event; a client that does not bind it simply ignores it, and the launcher change in Task 7 can ship on any schedule.

## Known and accepted

Carried forward from the spec's non-goals, unchanged by this plan:

- Already-rendered message history is never repaired. `ChannelMessage.Sender.Flair` stays an immutable send-time snapshot.
- The per-message flair storage cost (~49% of a message document) is not addressed.
- `ProfilePicture.Default()` in website-backend still picks a random `STARTER 1-4`.
- Flair is not propagated for players who are offline, or online but not focused on any channel the changed player is in. They pick it up on their next focus or connect.
- The reconnect window noted in Plan A's final review still exists: between `SessionRegistry.Register` and `ConnectionMapping.RegisterUser` there is up to ~4 s of awaited I/O during which a refresh finds a session but no cached `ChatUser`. `FlairRefresher` degrades correctly there (it re-registers from a fresh resolution), so this plan neither widens nor fixes it.
