# Post-Game Chat — Plan A (chat-service): First-Class System Messages

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a first-class, server-authored system-message capability to chat-service, and make match-channel activity carry a preview so a post-game message can be notified on.

**Architecture:** `ChannelMessage` gains an explicit `MessageKind` discriminator and a structured `SystemMessageBody` (template key + params + English fallback), so system content is localizable by any client. A dedicated `SystemMessagePublisher` owns the insert path — no session, no rate limiter, no mute gate, no mentions — but still allocates a seq and fans out. It is exposed in-process and over the existing HMAC internal REST realm. Nothing consumes it yet; this stage ships dark.

**Tech Stack:** .NET 8, MongoDB.Driver, SignalR, NUnit 3 + Moq + AutoFixture + `Microsoft.Extensions.TimeProvider.Testing`, Testcontainers.MongoDb (`mongo:7.0`).

**Source spec:** `docs/superpowers/specs/2026-08-10-post-game-chat-persistence-design.md`

## Global Constraints

- **Docker daemon must be running.** `MongoTestServer` is an NUnit `[SetUpFixture]` that starts one ephemeral `mongo:7.0` container per run. Without Docker every integration test errors.
- **Run tests with:** `dotnet test` from `chat-service/`. SDK 8.0.x per `.tool-versions`.
- **CI enforces formatting:** `dotnet format --verify-no-changes` runs before `dotnet test`. Run `dotnet format` before every commit.
- **NUnit constraint model** (`Assert.That(x, Is.EqualTo(y), "why")`) for all new tests, with a trailing explanation string. `StartupDependencyInjectionTests.cs` uses the classic model — match the file you are editing.
- **battleTags are stored lowercased** in memberships/directory/pair-keys; live session tags keep display casing. System messages have no battleTag at all.
- **Durable writes always precede pushes.** Fan-out is best-effort and per-recipient fault-isolated.
- **Soft-delete only.** `OldProtocolRemovedTests.ModerationNeverHardDeletes` reflects over `MessageRepository`'s public surface and fails the build for any new method whose name contains delete/remove/drop outside the allowlist `{MarkDeleted, MarkDeletedMany, DeleteAllForChannel}`. **Do not add such a method in this plan.**
- **Retention is deliberately unchanged** (spec D6). `AllocateSeq` must be called with `shellExpiresAt: null` for System channels. Do not touch `ExpiryCalculator` or `RetentionPeriods`.
- **Comments in this repo are contract-bearing.** They cite plan decisions (D1–D19) and pin named tests. When you change behaviour a comment describes, update the comment in the same commit.

## Corrections to the spec (found during planning — the spec is stale on three points)

1. **§3.6 is wrong about where the preview decision lives.** `ActivityCoalescer` is preview-agnostic — it carries whatever `object preview` it is handed (`ActivityCoalescer.cs:238`). The DM-only decision is in `FanOutEngine.cs:209-211`. Task 6 changes `FanOutEngine`, not the coalescer.
2. **§3.5 overstates the test blast radius.** `OldProtocolRemovedTests` has **no** test reflecting over message *insert* paths — `ModerationNeverHardDeletes` is a delete-verb name pin only. Adding a second `Insert` caller does not trip it. `StartupDependencyInjectionTests` **does** need a new test (it is a hand-maintained list, not a reflection sweep).
3. **`ChatChannel.Ladder` already exists**, along with `InternalChannelCreateRequest.Ladder` and `ChannelModeration.IsMuteEnforced`. No work needed; do not re-add it.

Additionally, the spec's wire sketch names the field `system`. **Use `SystemMessage` instead** — a C# property named `System` shadows the `System` namespace inside its declaring class and is a live footgun for any future qualified reference. Wire name becomes `systemMessage`. No client depends on either name yet (launcher work is Plan C).

## File Structure

| File | Responsibility |
|---|---|
| `W3ChampionsChatService/Domain/ChatEnums.cs` (modify) | Add `MessageKind` |
| `W3ChampionsChatService/Messages/SystemMessageBody.cs` (create) | Structured system content |
| `W3ChampionsChatService/Messages/ChannelMessage.cs` (modify) | `Kind`, `SystemMessage`, `DedupeKey` |
| `W3ChampionsChatService/Domain/ChatDomainIndexes.cs` (modify) | `ux_channelId_dedupeKey` partial unique index |
| `W3ChampionsChatService/Messages/MessageRepository.cs` (modify) | `LoadByDedupeKey` |
| `W3ChampionsChatService/Protocol/MessageDto.cs` (modify) | Project `Kind`/`SystemMessage` |
| `W3ChampionsChatService/Messages/SystemMessagePublisher.cs` (create) | The insert path |
| `W3ChampionsChatService/Internal/InternalDtos.cs` (modify) | `InternalSystemMessageRequest` |
| `W3ChampionsChatService/Internal/InternalChannelsController.cs` (modify) | `POST {ref}/system-message` |
| `W3ChampionsChatService/Chats/ChatHub.cs` (modify) | System-message guard in `DeleteMessage` |
| `W3ChampionsChatService/FanOut/FanOutEngine.cs` (modify) | Match-channel activity preview |
| `W3ChampionsChatService/Startup.cs` (modify) | Register `SystemMessagePublisher` |

---

### Task 1: Model + dedupe index

**Files:**
- Modify: `W3ChampionsChatService/Domain/ChatEnums.cs`
- Create: `W3ChampionsChatService/Messages/SystemMessageBody.cs`
- Modify: `W3ChampionsChatService/Messages/ChannelMessage.cs:25-50`
- Modify: `W3ChampionsChatService/Domain/ChatDomainIndexes.cs:116-151`
- Test: `W3ChampionsChatService.Tests/SystemMessageModelTests.cs` (create)

**Interfaces:**
- Produces: `MessageKind { User, System }`; `SystemMessageBody { string Key; Dictionary<string,string> Params; Dictionary<string,List<string>> ListParams; string FallbackText; }`; `ChannelMessage.Kind` (default `User`), `ChannelMessage.SystemMessage`, `ChannelMessage.DedupeKey`; index `ux_channelId_dedupeKey`.

- [ ] **Step 1: Write the failing test**

Create `W3ChampionsChatService.Tests/SystemMessageModelTests.cs`:

```csharp
using System.Collections.Generic;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using NUnit.Framework;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Messages;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// Post-game chat Plan A Task 1 — the system-message shape on <see cref="ChannelMessage"/>:
/// BSON round-trip, the legacy-document default, and the dedupe uniqueness guarantee.
/// </summary>
public class SystemMessageModelTests : IntegrationTestBase
{
    private MessageRepository _messages;

    [SetUp]
    public void SetupBeforeEach() => _messages = new MessageRepository(MongoClient);

    private static ChannelMessage NewSystemMessage(string channelId, long seq, string dedupeKey = null) => new()
    {
        ChannelId = channelId,
        Seq = seq,
        Kind = MessageKind.System,
        SystemMessage = new SystemMessageBody
        {
            Key = "match_intro",
            Params = new Dictionary<string, string> { ["map"] = "Amazonia" },
            ListParams = new Dictionary<string, List<string>> { ["players"] = ["Grubby#2136", "Happy#2233"] },
            FallbackText = "Match on Amazonia — Grubby#2136, Happy#2233",
        },
        DedupeKey = dedupeKey,
        SentAt = System.DateTime.UtcNow,
    };

    [Test]
    public async Task SystemMessage_RoundTripsThroughMongo_WithNullSenderAndContent()
    {
        var written = NewSystemMessage("chan-1", 1);
        await _messages.Insert(written);

        var read = await _messages.Load(written.Id);

        Assert.That(read, Is.Not.Null);
        Assert.That(read.Kind, Is.EqualTo(MessageKind.System), "Kind survives the round-trip");
        Assert.That(read.Sender, Is.Null, "a system message has no sender snapshot");
        Assert.That(read.Content, Is.Null, "a system message carries no free-form content");
        Assert.That(read.SystemMessage.Key, Is.EqualTo("match_intro"));
        Assert.That(read.SystemMessage.Params["map"], Is.EqualTo("Amazonia"));
        Assert.That(read.SystemMessage.ListParams["players"], Is.EqualTo(new[] { "Grubby#2136", "Happy#2233" }));
        Assert.That(read.SystemMessage.FallbackText, Does.Contain("Amazonia"));
    }

    [Test]
    public async Task LegacyDocumentWithoutKind_DeserializesAsUser()
    {
        // A pre-migration document: no `kind`, no `systemMessage`, no `dedupeKey`.
        var raw = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId().ToString(),
            ["ChannelId"] = "chan-legacy",
            ["Seq"] = 7L,
            ["Sender"] = new BsonDocument { ["BattleTag"] = "Peter#123", ["Name"] = "Peter" },
            ["Content"] = "hello",
            ["SentAt"] = System.DateTime.UtcNow,
            ["Shadow"] = false,
        };
        await MongoClient.GetDatabase(MongoDbRepositoryBase.DatabaseName)
            .GetCollection<BsonDocument>(ChatCollections.Messages)
            .InsertOneAsync(raw);

        var read = await _messages.Load(raw["_id"].AsString);

        Assert.That(read.Kind, Is.EqualTo(MessageKind.User),
            "existing documents must deserialize as User with NO migration — Kind defaults");
        Assert.That(read.SystemMessage, Is.Null);
        Assert.That(read.DedupeKey, Is.Null);
    }

    [Test]
    public async Task DedupeKey_IsUniquePerChannel_ButFreeAcrossChannels()
    {
        await ChatDomainIndexes.EnsureAllAsync(MongoClient);

        await _messages.Insert(NewSystemMessage("chan-1", 1, "match_intro"));
        await _messages.Insert(NewSystemMessage("chan-2", 1, "match_intro"));

        Assert.ThrowsAsync<MongoWriteException>(
            async () => await _messages.Insert(NewSystemMessage("chan-1", 2, "match_intro")),
            "ux_channelId_dedupeKey makes a duplicate (channel, dedupeKey) a hard write error");
    }

    [Test]
    public async Task UserMessagesWithoutDedupeKey_AreNotConstrained()
    {
        await ChatDomainIndexes.EnsureAllAsync(MongoClient);

        await _messages.Insert(new ChannelMessage
        {
            ChannelId = "chan-1", Seq = 1,
            Sender = new MessageSender { BattleTag = "A#1", Name = "A" },
            Content = "one", SentAt = System.DateTime.UtcNow,
        });

        Assert.DoesNotThrowAsync(async () => await _messages.Insert(new ChannelMessage
        {
            ChannelId = "chan-1", Seq = 2,
            Sender = new MessageSender { BattleTag = "B#1", Name = "B" },
            Content = "two", SentAt = System.DateTime.UtcNow,
        }), "the partial index must not constrain ordinary messages, which have no dedupeKey at all");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~SystemMessageModelTests`
Expected: FAIL — compile errors, `MessageKind` and `SystemMessageBody` do not exist.

- [ ] **Step 3: Add `MessageKind` to `Domain/ChatEnums.cs`**

Append to the file:

```csharp
/// <summary>
/// Message authorship discriminator. <see cref="User"/> is a player-authored message with a
/// <c>MessageSender</c> snapshot and free-form <c>Content</c>; <see cref="System"/> is
/// server-authored, has NO sender and NO content, and carries a structured
/// <c>SystemMessageBody</c> instead. Stored as a string so a future kind is additive, and
/// defaulted to <see cref="User"/> on <c>ChannelMessage</c> so every pre-existing document
/// deserializes correctly with no migration.
/// </summary>
public enum MessageKind
{
    User,
    System,
}
```

- [ ] **Step 4: Create `Messages/SystemMessageBody.cs`**

```csharp
using System.Collections.Generic;
using MongoDB.Bson.Serialization.Attributes;

namespace W3ChampionsChatService.Messages;

/// <summary>
/// Structured content of a server-authored system message. Deliberately NOT a pre-rendered string:
/// the launcher ships 13 locales, so a stored English sentence would be permanently untranslatable.
/// Clients render <see cref="Key"/> against their own catalogue using <see cref="Params"/> /
/// <see cref="ListParams"/>, and fall back to <see cref="FallbackText"/> for any key they do not
/// recognise — which is what lets chat-service add new system messages without breaking older clients.
/// <para>
/// Two dictionaries rather than one <c>object</c> bag: both round-trip through BSON and
/// System.Text.Json with no custom converters, and give TypeScript a clean shape. Scalars go in
/// <see cref="Params"/>, lists in <see cref="ListParams"/>.
/// </para>
/// </summary>
public class SystemMessageBody
{
    /// <summary>Template id, e.g. <c>match_intro</c>. Stable — clients key their catalogue off it.</summary>
    public string Key { get; set; }

    [BsonIgnoreIfNull]
    public Dictionary<string, string> Params { get; set; }

    [BsonIgnoreIfNull]
    public Dictionary<string, List<string>> ListParams { get; set; }

    /// <summary>
    /// Server-rendered English. The ONLY thing a client that does not know <see cref="Key"/> can show,
    /// and what the moderation history endpoint reads. Required — never null.
    /// </summary>
    public string FallbackText { get; set; }
}
```

- [ ] **Step 5: Add the three fields to `ChannelMessage`**

In `Messages/ChannelMessage.cs`, add `using W3ChampionsChatService.Domain;` if absent (it is already there), and insert after the `ChannelId`/`Seq` block, before `Sender`:

```csharp
    /// <summary>
    /// Authorship discriminator. <see cref="MessageKind.User"/> ⇒ <see cref="Sender"/> and
    /// <see cref="Content"/> are populated and <see cref="SystemMessage"/> is null;
    /// <see cref="MessageKind.System"/> ⇒ exactly the inverse. Defaulted (and stored as a string) so
    /// every document written before this field existed deserializes as User with no migration.
    /// </summary>
    [BsonRepresentation(BsonType.String)]
    public MessageKind Kind { get; set; } = MessageKind.User;
```

and after `ExpiresAt`:

```csharp
    /// <summary>Structured system content. Non-null iff <see cref="Kind"/> is <see cref="MessageKind.System"/>.</summary>
    [BsonIgnoreIfNull]
    public SystemMessageBody SystemMessage { get; set; }

    /// <summary>
    /// Per-channel idempotency key for server-authored messages (System only; null for every user
    /// message). Backed by the partial unique index <c>ux_channelId_dedupeKey</c> — matchmaking-service
    /// retries its publish call on timeout, and without this the post-game intro double-posts.
    /// </summary>
    [BsonIgnoreIfNull]
    public string DedupeKey { get; set; }
```

Note the existing `Sender`/`Content` XML docs now describe a conditional field — amend them to say "User messages only; null for a system message."

- [ ] **Step 6: Add the index in `Domain/ChatDomainIndexes.cs`**

Append to the `CreateManyAsync` array inside `EnsureMessageIndexes` (after the `ttl_expiresAt` entry). Note the **generic** `CreateIndexOptions<ChannelMessage>` — a partial filter requires it:

```csharp
            // Post-game chat Plan A Task 1: per-channel idempotency for server-authored messages.
            // Partial on DedupeKey's existence so it constrains ONLY system messages — ordinary
            // user messages never carry the field and must stay completely unaffected.
            new CreateIndexModel<ChannelMessage>(
                Builders<ChannelMessage>.IndexKeys.Ascending(m => m.ChannelId).Ascending(m => m.DedupeKey),
                new CreateIndexOptions<ChannelMessage>
                {
                    Name = "ux_channelId_dedupeKey",
                    Unique = true,
                    PartialFilterExpression = Builders<ChannelMessage>.Filter.Exists(m => m.DedupeKey),
                }),
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~SystemMessageModelTests`
Expected: PASS, 4 tests.

- [ ] **Step 8: Run the full suite for regressions**

Run: `dotnet test`
Expected: PASS. `ChannelMessage` is deserialized everywhere; a new defaulted field must break nothing.

- [ ] **Step 9: Format and commit**

```bash
dotnet format
git add W3ChampionsChatService/Domain/ChatEnums.cs W3ChampionsChatService/Domain/ChatDomainIndexes.cs W3ChampionsChatService/Messages/SystemMessageBody.cs W3ChampionsChatService/Messages/ChannelMessage.cs W3ChampionsChatService.Tests/SystemMessageModelTests.cs
git commit -m "feat(messages): add MessageKind + SystemMessageBody + dedupe index"
```

---

### Task 2: Project system fields onto `MessageDto`

**Files:**
- Modify: `W3ChampionsChatService/Protocol/MessageDto.cs`
- Test: `W3ChampionsChatService.Tests/ProtocolContractTests.cs` (modify — add tests)

**Interfaces:**
- Consumes: `MessageKind`, `SystemMessageBody`, `ChannelMessage.Kind`, `ChannelMessage.SystemMessage` (Task 1).
- Produces: `MessageDto` with trailing optional params `MessageKind Kind = MessageKind.User, SystemMessageBody SystemMessage = null`, populated by both `ForUserDelivery` and `ForModerator`.

- [ ] **Step 1: Write the failing test**

Append to `W3ChampionsChatService.Tests/ProtocolContractTests.cs` (inside the existing fixture class; add `using W3ChampionsChatService.Domain;` and `using System.Collections.Generic;` to the using block if absent):

```csharp
    [Test]
    public void ForUserDelivery_CarriesSystemKindAndBody()
    {
        var systemMessage = new ChannelMessage
        {
            Id = "m1", ChannelId = "chan1", Seq = 3,
            Kind = MessageKind.System,
            SystemMessage = new SystemMessageBody
            {
                Key = "match_intro",
                Params = new Dictionary<string, string> { ["map"] = "Amazonia" },
                FallbackText = "Match on Amazonia",
            },
            SentAt = DateTime.UtcNow,
        };

        var dto = MessageDto.ForUserDelivery("chan1", systemMessage);

        Assert.That(dto.Kind, Is.EqualTo(MessageKind.System), "the client needs the discriminator to pick a renderer");
        Assert.That(dto.SystemMessage.Key, Is.EqualTo("match_intro"));
        Assert.That(dto.SystemMessage.FallbackText, Is.EqualTo("Match on Amazonia"));
        Assert.That(dto.Sender, Is.Null);
        Assert.That(dto.Content, Is.Null);
    }

    [Test]
    public void ForModerator_CarriesSystemKindAndBody()
    {
        var systemMessage = new ChannelMessage
        {
            Id = "m1", ChannelId = "chan1", Seq = 3,
            Kind = MessageKind.System,
            SystemMessage = new SystemMessageBody { Key = "match_intro", FallbackText = "Match on Amazonia" },
            SentAt = DateTime.UtcNow,
        };

        var dto = MessageDto.ForModerator("chan1", systemMessage);

        Assert.That(dto.Kind, Is.EqualTo(MessageKind.System));
        Assert.That(dto.SystemMessage.FallbackText, Is.EqualTo("Match on Amazonia"),
            "moderation history renders fallbackText — it has no i18n catalogue");
    }

    [Test]
    public void UserMessageProjection_DefaultsToUserKindWithNoSystemBody()
    {
        var userMessage = new ChannelMessage
        {
            Id = "m2", ChannelId = "chan1", Seq = 4,
            Sender = new MessageSender { BattleTag = "A#1", Name = "A" },
            Content = "gg", SentAt = DateTime.UtcNow,
        };

        var dto = MessageDto.ForUserDelivery("chan1", userMessage);

        Assert.That(dto.Kind, Is.EqualTo(MessageKind.User));
        Assert.That(dto.SystemMessage, Is.Null);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~ProtocolContractTests`
Expected: FAIL — `MessageDto` has no `Kind` / `SystemMessage` member.

- [ ] **Step 3: Add the two params and populate them in both factories**

In `Protocol/MessageDto.cs`, add `using W3ChampionsChatService.Domain;`, then extend the record header and both factories:

```csharp
public record MessageDto(
    string Id,
    string ChannelId,
    long Seq,
    MessageSender Sender,
    string Content,
    DateTime SentAt,
    bool Deleted,
    bool Shadow,
    MessageKind Kind = MessageKind.User,
    SystemMessageBody SystemMessage = null)
```

Both new params are **trailing and defaulted** so every existing positional construction site keeps compiling unchanged.

In `ForUserDelivery`, add to the initializer:

```csharp
            Deleted: false,
            Shadow: false,
            Kind: message.Kind,
            SystemMessage: message.SystemMessage);
```

In `ForModerator`, add:

```csharp
            Deleted: message.Deleted != null,
            Shadow: message.Shadow,
            Kind: message.Kind,
            SystemMessage: message.SystemMessage);
```

Amend the record's XML doc: `Sender`/`Content` are null for `Kind == System`, and `SystemMessage` is null for `Kind == User`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~ProtocolContractTests`
Expected: PASS.

- [ ] **Step 5: Run the full suite — one test is a known blast-radius risk**

Run: `dotnet test`
Expected: PASS. Watch `DmGroupIntegrationTests` — line ~773 does a `JsonSerializer.Serialize(MessageDto.ForUserDelivery(...))` byte-comparison. Both sides gain the same two fields, so it should still match; if it fails, the assertion is comparing against a hardcoded JSON literal and that literal needs the two new fields added, not the DTO reverted.

- [ ] **Step 6: Format and commit**

```bash
dotnet format
git add W3ChampionsChatService/Protocol/MessageDto.cs W3ChampionsChatService.Tests/ProtocolContractTests.cs
git commit -m "feat(protocol): project message kind and system body onto MessageDto"
```

---

### Task 3: `SystemMessagePublisher`

**Files:**
- Modify: `W3ChampionsChatService/Messages/MessageRepository.cs`
- Create: `W3ChampionsChatService/Messages/SystemMessagePublisher.cs`
- Modify: `W3ChampionsChatService/Startup.cs:238-245`
- Test: `W3ChampionsChatService.Tests/SystemMessagePublisherTests.cs` (create)
- Test: `W3ChampionsChatService.Tests/StartupDependencyInjectionTests.cs` (modify)

**Interfaces:**
- Consumes: Task 1's model; `ChannelRepository.AllocateSeq(channelId, now, shellExpiresAt)`; `FanOutEngine.OnMessagePersisted(channel, message, senderConnectionId, isShadow, now)`.
- Produces:
  - `MessageRepository.LoadByDedupeKey(string channelId, string dedupeKey) → Task<ChannelMessage>`
  - `record SystemMessagePublishResult(ChatResultCode Code, string MessageId, long Seq)`
  - `SystemMessagePublisher.Publish(ChatChannel channel, SystemMessageBody body, string dedupeKey) → Task<SystemMessagePublishResult>`

It takes a resolved `ChatChannel`, not a channelId — the caller already has one, and this keeps the publisher free of channel-lookup policy.

- [ ] **Step 1: Write the failing test**

Create `W3ChampionsChatService.Tests/SystemMessagePublisherTests.cs`:

```csharp
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
using W3ChampionsChatService.Memberships;
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
                new ViewerResolver(_sessionRegistry, new Chats.ConnectionMapping())),
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
        Assert.That(stored.Kind, Is.EqualTo(MessageKind.System));
        Assert.That(stored.Sender, Is.Null);
        Assert.That(stored.SystemMessage.Key, Is.EqualTo("match_intro"));
        Assert.That(stored.SentAt, Is.EqualTo(Now));
        Assert.That(stored.ExpiresAt, Is.Not.Null, "system messages follow the normal 30d message TTL");

        var reloaded = await _channelRepository.Load(channel.Id);
        Assert.That(reloaded.LastSeq, Is.EqualTo(1), "AllocateSeq ran — paging and unread both key off it");
        Assert.That(reloaded.LastMessageAt, Is.EqualTo(Now));
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
        Assert.That(second.Seq, Is.EqualTo(first.Seq));

        var all = await _messageRepository.LoadForModerator(channel.Id);
        Assert.That(all, Has.Count.EqualTo(1), "mm retries on timeout — the intro must never double-post");
    }

    [Test]
    public async Task Publish_DeliversMessageReceivedToFocusedMembers()
    {
        var channel = await NewMatchChannel();
        _sessionRegistry.Register("conn-alice",
            new W3CUserAuthentication { BattleTag = "Alice#1", Name = "Alice" }, null);
        _onlineMemberRegistry.Join("conn-alice", channel.Id, "Alice#1", NotificationLevel.All, 0, ChannelType.System);
        _focusRegistry.Focus("conn-alice", channel.Id);

        await _publisher.Publish(channel, Intro(), dedupeKey: null);

        Assert.That(_harness.SignalCount("conn-alice", ChatEvents.MessageReceived), Is.EqualTo(1));
        var dto = _harness.PayloadFor("conn-alice", ChatEvents.MessageReceived) as MessageDto;
        Assert.That(dto.Kind, Is.EqualTo(MessageKind.System));
        Assert.That(dto.SystemMessage.FallbackText, Does.Contain("Amazonia"));
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
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~SystemMessagePublisherTests`
Expected: FAIL — `SystemMessagePublisher` does not exist.

If `OnlineMemberRegistry.Join` or `FocusRegistry.Focus` have different signatures, correct the two setup lines to match — the assertions do not depend on their shape.

- [ ] **Step 3: Add `LoadByDedupeKey` to `MessageRepository`**

Insert after `Load` (`MessageRepository.cs:29`):

```csharp
    /// <summary>
    /// The idempotency lookup for server-authored messages (post-game chat Plan A Task 3): the single
    /// message in <paramref name="channelId"/> carrying <paramref name="dedupeKey"/>, or null. Served by
    /// the partial unique index <c>ux_channelId_dedupeKey</c>, so at most one row can ever match.
    /// </summary>
    public Task<ChannelMessage> LoadByDedupeKey(string channelId, string dedupeKey) =>
        Messages.Find(m => m.ChannelId == channelId && m.DedupeKey == dedupeKey).FirstOrDefaultAsync();
```

Name deliberately contains no delete/remove/drop verb — `OldProtocolRemovedTests.ModerationNeverHardDeletes` reflects over this class's public surface.

- [ ] **Step 4: Create `Messages/SystemMessagePublisher.cs`**

```csharp
using System;
using System.Threading.Tasks;
using MongoDB.Driver;
using Serilog;
using W3ChampionsChatService.Channels;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.FanOut;
using W3ChampionsChatService.Protocol;

namespace W3ChampionsChatService.Messages;

/// <summary>Outcome of a system-message publish. <see cref="Seq"/> is 0 when <see cref="Code"/> is not Ok.</summary>
public record SystemMessagePublishResult(ChatResultCode Code, string MessageId, long Seq);

/// <summary>
/// The ONE server-authored message insert path — the second (and only other) caller of
/// <see cref="MessageRepository.Insert"/> besides <c>ChatHub.SendMessage</c>.
/// <para>
/// Deliberately skips every stage of the user send pipeline that exists to police a human: no session
/// lookup, no <c>MessageRateLimiter</c>, no mute gate, no mention extraction, no <c>MentionFanOut</c>.
/// It still does the two things the rest of the system depends on: <see cref="ChannelRepository.AllocateSeq"/>
/// (seq-anchored paging, unread, and <c>LastMessageAt</c> all key off it) and
/// <see cref="FanOutEngine.OnMessagePersisted"/>. Durable write strictly precedes the push, like every
/// other write path here.
/// </para>
/// <para>
/// <c>shellExpiresAt</c> is ALWAYS null: retention is deliberately unchanged (design D6), and System
/// channels are creation-anchored — only Dm/GroupDm sends may re-stamp a channel shell's TTL.
/// <c>senderConnectionId</c> is null and <c>isShadow</c> false; with no shadow the null sender id is
/// inert in fan-out (it only ever participates in reference comparisons).
/// </para>
/// <para>
/// IDEMPOTENCY: when <c>dedupeKey</c> is non-null the publish is at-most-once per (channel, key). The
/// pre-check handles the common retry; the duplicate-key catch handles a genuine concurrent race, since
/// the seq allocation and the insert are not one atomic unit. A race burns a seq number — harmless,
/// because paging is seq-ANCHORED and never assumes contiguity.
/// </para>
/// </summary>
public class SystemMessagePublisher(
    MessageRepository messageRepository,
    ChannelRepository channelRepository,
    FanOutEngine fanOutEngine,
    TimeProvider timeProvider)
{
    public async Task<SystemMessagePublishResult> Publish(ChatChannel channel, SystemMessageBody body, string dedupeKey)
    {
        if (channel == null)
        {
            return new SystemMessagePublishResult(ChatResultCode.NotFound, null, 0);
        }

        if (dedupeKey != null)
        {
            var existing = await messageRepository.LoadByDedupeKey(channel.Id, dedupeKey);
            if (existing != null)
            {
                return new SystemMessagePublishResult(ChatResultCode.Ok, existing.Id, existing.Seq);
            }
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var seq = await channelRepository.AllocateSeq(channel.Id, now, shellExpiresAt: null);

        var message = new ChannelMessage
        {
            ChannelId = channel.Id,
            Seq = seq,
            Kind = MessageKind.System,
            SystemMessage = body,
            DedupeKey = dedupeKey,
            SentAt = now,
            ExpiresAt = ExpiryCalculator.ForChannelMessage(channel.Type, now),
        };

        try
        {
            await messageRepository.Insert(message);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey && dedupeKey != null)
        {
            // Concurrent publish of the same key won the race. Return ITS message, not an error —
            // the caller asked for "this system message exists", and it does.
            var winner = await messageRepository.LoadByDedupeKey(channel.Id, dedupeKey);
            if (winner != null)
            {
                return new SystemMessagePublishResult(ChatResultCode.Ok, winner.Id, winner.Seq);
            }
            throw;
        }

        await fanOutEngine.OnMessagePersisted(channel, message, senderConnectionId: null, isShadow: false, now);

        Log.Information(
            "System message published {Key} channel={ChannelId} seq={Seq} dedupeKey={DedupeKey}",
            body.Key, channel.Id, seq, dedupeKey);

        return new SystemMessagePublishResult(ChatResultCode.Ok, message.Id, seq);
    }
}
```

- [ ] **Step 5: Register in DI**

In `Startup.cs`, immediately before the `services.AddSingleton<MatchChannelService>();` line (~238-245):

```csharp
        // Post-game chat Plan A Task 3: the server-authored message insert path. Singleton for the same
        // reason as MatchChannelService — no per-call state; its MessageRepository/ChannelRepository deps
        // are transient MongoClient wrappers, safe to capture.
        services.AddSingleton<SystemMessagePublisher>();
```

Add `using W3ChampionsChatService.Messages;` to `Startup.cs` if absent.

- [ ] **Step 6: Add the DI coverage test**

`StartupDependencyInjectionTests.cs` is a hand-maintained list, not a reflection sweep. Append (classic assert model — match the file):

```csharp
    [Test]
    public void SystemMessagePublisher_IsSingleton_SharedAcrossResolutions()
    {
        using var provider = BuildProvider();

        var first = provider.GetRequiredService<SystemMessagePublisher>();
        var second = provider.GetRequiredService<SystemMessagePublisher>();

        Assert.AreSame(first, second,
            "SystemMessagePublisher MUST be a singleton — it is the shared server-authored insert path, and a transient would needlessly re-resolve the fan-out graph per publish");
    }
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~SystemMessagePublisherTests|FullyQualifiedName~StartupDependencyInjectionTests"`
Expected: PASS.

- [ ] **Step 8: Format and commit**

```bash
dotnet format
git add W3ChampionsChatService/Messages/SystemMessagePublisher.cs W3ChampionsChatService/Messages/MessageRepository.cs W3ChampionsChatService/Startup.cs W3ChampionsChatService.Tests/SystemMessagePublisherTests.cs W3ChampionsChatService.Tests/StartupDependencyInjectionTests.cs
git commit -m "feat(messages): add SystemMessagePublisher server-authored insert path"
```

---

### Task 4: `POST /internal/channels/{ref}/system-message`

**Files:**
- Modify: `W3ChampionsChatService/Internal/InternalDtos.cs`
- Modify: `W3ChampionsChatService/Internal/InternalChannelsController.cs`
- Test: `W3ChampionsChatService.Tests/InternalApiIntegrationTests.cs` (modify)

**Interfaces:**
- Consumes: `SystemMessagePublisher.Publish` (Task 3); `ChannelRepository.LoadBySystemRef(SystemChannelKind, string)`.
- Produces: HTTP `POST /internal/channels/{systemRef}/system-message` with body `InternalSystemMessageRequest { Key, Params, ListParams, FallbackText, DedupeKey }`. 200 on publish or dedupe hit, 404 for an unknown ref, 400 for validation failure.

- [ ] **Step 1: Write the failing test**

Append to `W3ChampionsChatService.Tests/InternalApiIntegrationTests.cs`, matching that file's existing HMAC-signed request helper (it already has one for the create/roster routes — reuse it verbatim rather than writing a new signer):

```csharp
    [Test]
    public async Task SystemMessage_PublishesIntoAnExistingMatchChannel()
    {
        await CreateChannelViaApi("match-sys-1", members: ["Alice#1"]);

        var response = await SignedPost("/internal/channels/match-sys-1/system-message", new
        {
            key = "match_intro",
            @params = new { map = "Amazonia" },
            listParams = new { players = new[] { "Grubby#2136", "Happy#2233" } },
            fallbackText = "Match on Amazonia — Grubby#2136, Happy#2233",
            dedupeKey = "match_intro",
        });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task SystemMessage_UnknownRef_Is404_AndCreatesNothing()
    {
        var response = await SignedPost("/internal/channels/never-existed/system-message", new
        {
            key = "match_intro",
            fallbackText = "Match on Amazonia",
        });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound),
            "the system-message route is lookup-only — it must NEVER create a channel on demand");
    }

    [Test]
    public async Task SystemMessage_BlankKeyOrFallback_Is400()
    {
        await CreateChannelViaApi("match-sys-2", members: ["Alice#1"]);

        var blankKey = await SignedPost("/internal/channels/match-sys-2/system-message",
            new { key = "   ", fallbackText = "x" });
        var blankFallback = await SignedPost("/internal/channels/match-sys-2/system-message",
            new { key = "match_intro", fallbackText = "" });

        Assert.That(blankKey.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(blankFallback.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest),
            "fallbackText is the only thing a client that does not know the key can render — it is required");
    }

    [Test]
    public async Task SystemMessage_RepeatedWithSameDedupeKey_Is200_AndPublishesOnce()
    {
        await CreateChannelViaApi("match-sys-3", members: ["Alice#1"]);
        var body = new { key = "match_intro", fallbackText = "Match on Amazonia", dedupeKey = "match_intro" };

        var first = await SignedPost("/internal/channels/match-sys-3/system-message", body);
        var second = await SignedPost("/internal/channels/match-sys-3/system-message", body);

        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            "mm retries on timeout — a duplicate publish is a success, never an error");
    }
```

If `InternalApiIntegrationTests` does not already expose `CreateChannelViaApi` / `SignedPost` helpers under those names, use whatever the file's existing create-channel and signed-request helpers are called; do not add a second HMAC signer.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~InternalApiIntegrationTests`
Expected: FAIL — 404 from routing on every case (the route does not exist).

- [ ] **Step 3: Add the request DTO**

Append to `Internal/InternalDtos.cs`:

```csharp
/// <summary>
/// <c>POST /internal/channels/{ref}/system-message</c> request body — a server-authored message
/// published into an EXISTING channel. Lookup-only: unlike the create/roster routes this one never
/// creates a channel, so an unknown ref is a 404 rather than an implicit create.
/// <para>
/// <see cref="Key"/> and <see cref="FallbackText"/> are both REQUIRED: the key is what a client
/// renders through its own locale catalogue, and the fallback is the only thing a client that does not
/// know the key (or the moderation history endpoint, which has no catalogue at all) can display.
/// <see cref="DedupeKey"/> is optional but strongly recommended — mm retries on timeout, and without a
/// key a retried publish posts twice.
/// </para>
/// </summary>
public class InternalSystemMessageRequest
{
    public string Key { get; set; }
    public Dictionary<string, string> Params { get; set; }
    public Dictionary<string, List<string>> ListParams { get; set; }
    public string FallbackText { get; set; }
    public string DedupeKey { get; set; }
}
```

- [ ] **Step 4: Add the controller action**

`InternalChannelsController`'s primary constructor becomes:

```csharp
public class InternalChannelsController(
    MatchChannelService matchChannelService,
    ChannelRepository channelRepository,
    SystemMessagePublisher systemMessagePublisher) : ControllerBase
```

Add `using W3ChampionsChatService.Channels;` and `using W3ChampionsChatService.Messages;`, then add the action:

```csharp
    /// <summary>
    /// Publishes a server-authored system message into the match channel identified by
    /// <paramref name="systemRef"/>. LOOKUP-ONLY — deliberately unlike <c>POST /internal/channels</c>:
    /// a system message is meaningless without the room it narrates, so an unknown ref is a 404 rather
    /// than an implicit create (which would leave a memberless channel nobody can ever see).
    /// Idempotent when the caller supplies a dedupeKey — a retry returns 200 with the original message.
    /// </summary>
    [HttpPost("{systemRef}/system-message")]
    public async Task<IActionResult> PublishSystemMessage(string systemRef, [FromBody] InternalSystemMessageRequest request)
    {
        if (request == null || !IsValidRef(systemRef))
        {
            return BadRequest(new ErrorResult(GenericValidationError));
        }

        var key = request.Key?.Trim();
        var fallbackText = request.FallbackText?.Trim();
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(fallbackText))
        {
            return BadRequest(new ErrorResult(GenericValidationError));
        }

        // Same character class as `ref` — the key is logged and becomes a client catalogue lookup, so it
        // gets the same log-injection / control-char defense.
        if (!IsValidRef(key))
        {
            return BadRequest(new ErrorResult(GenericValidationError));
        }

        var dedupeKey = request.DedupeKey?.Trim();
        if (dedupeKey != null && (dedupeKey.Length == 0 || !IsValidRef(dedupeKey)))
        {
            return BadRequest(new ErrorResult(GenericValidationError));
        }

        try
        {
            var channel = await channelRepository.LoadBySystemRef(SystemChannelKind.Match, systemRef);
            if (channel == null)
            {
                return NotFound(new ErrorResult(GenericValidationError));
            }

            var body = new SystemMessageBody
            {
                Key = key,
                Params = request.Params,
                ListParams = request.ListParams,
                FallbackText = fallbackText,
            };

            var result = await systemMessagePublisher.Publish(channel, body, dedupeKey);
            if (result.Code != ChatResultCode.Ok)
            {
                return NotFound(new ErrorResult(GenericValidationError));
            }

            Log.Information(
                "Internal system message succeeded {Caller} {Verb} {Ref} key={Key} seq={Seq}",
                InternalHmacAuthFilter.ResolveCaller(HttpContext), "POST", systemRef, key, result.Seq);

            return Ok();
        }
        catch (Exception ex)
        {
            LogUnexpected(ex, "POST", systemRef);
            throw;
        }
    }
```

Add `using W3ChampionsChatService.Protocol;` for `ChatResultCode` if absent. Also extend the controller's class-level XML doc to list the fifth route.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~InternalApiIntegrationTests`
Expected: PASS.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test`
Expected: PASS. `InternalChannelsControllerTests` has a reflection sweep asserting every `internal/*` controller carries `[InternalHmacAuth]` — the attribute is at class level so the new action inherits it, and that sweep should stay green.

- [ ] **Step 7: Format and commit**

```bash
dotnet format
git add W3ChampionsChatService/Internal/InternalDtos.cs W3ChampionsChatService/Internal/InternalChannelsController.cs W3ChampionsChatService.Tests/InternalApiIntegrationTests.cs
git commit -m "feat(internal): add system-message publish endpoint"
```

---

### Task 5: Moderation safety for system messages

**Files:**
- Modify: `W3ChampionsChatService/Chats/ChatHub.cs:546-563`
- Test: `W3ChampionsChatService.Tests/SystemMessageModerationTests.cs` (create)

**Interfaces:**
- Consumes: Task 1's `MessageKind`; Task 3's publisher.
- Produces: `ChatHub.DeleteMessage` returns `PermissionDenied` for a system message.

**This is a crash fix, not only a policy choice.** `ChatHub.cs:593` does
`_connections.GetConnectionIdsForUser(message.Sender.BattleTag)` with no null guard. A system message
reaching that line is a `NullReferenceException`. The guard must land before `MarkDeleted`.

- [ ] **Step 1: Write the failing test**

Create `W3ChampionsChatService.Tests/SystemMessageModerationTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Messages;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// Post-game chat Plan A Task 5 — moderation must treat a sender-less message safely. The purge and
/// visibility legs are PINS on existing behaviour (they already work by construction); the
/// DeleteMessage leg is a real crash fix.
/// </summary>
public class SystemMessageModerationTests : IntegrationTestBase
{
    private MessageRepository _messages;

    [SetUp]
    public void SetupBeforeEach() => _messages = new MessageRepository(MongoClient);

    private async Task<ChannelMessage> SeedSystemMessage(string channelId, long seq)
    {
        var message = new ChannelMessage
        {
            ChannelId = channelId, Seq = seq, Kind = MessageKind.System,
            SystemMessage = new SystemMessageBody { Key = "match_intro", FallbackText = "Match on Amazonia" },
            SentAt = DateTime.UtcNow,
        };
        await _messages.Insert(message);
        return message;
    }

    [Test]
    public async Task PurgeBySender_NeverTargetsSystemMessages()
    {
        await SeedSystemMessage("chan-1", 1);
        await _messages.Insert(new ChannelMessage
        {
            ChannelId = "chan-1", Seq = 2,
            Sender = new MessageSender { BattleTag = "Griefer#1", Name = "Griefer" },
            Content = "spam", SentAt = DateTime.UtcNow,
        });

        var targets = await _messages.LoadPurgeableBySender("Griefer#1");

        Assert.That(targets, Has.Count.EqualTo(1),
            "a sender-less system message can never be a purge target — it has no battleTag to match");
    }

    [Test]
    public async Task SystemMessages_AreUserVisible()
    {
        var seeded = await SeedSystemMessage("chan-1", 1);

        var visible = await _messages.LoadForUser("chan-1", "Alice#1");

        Assert.That(visible.Select(m => m.Id), Does.Contain(seeded.Id),
            "UserVisible's Shadow==false disjunct matches a system message (Shadow defaults false), so the sender-regex leg is never needed");
    }

    [Test]
    public async Task SystemMessages_AreVisibleInModeratorHistory()
    {
        var seeded = await SeedSystemMessage("chan-1", 1);

        var page = await _messages.LoadPageBeforeForModerator("chan-1", beforeSeq: null, limit: 50);

        Assert.That(page.Select(m => m.Id), Does.Contain(seeded.Id));
        Assert.That(page.Single(m => m.Id == seeded.Id).SystemMessage.FallbackText, Is.EqualTo("Match on Amazonia"));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~SystemMessageModerationTests`
Expected: PASS for all four — these pin behaviour that already holds. If any FAILS, stop: an assumption in the design is wrong and the guard design needs revisiting before continuing.

- [ ] **Step 3: Add the `DeleteMessage` guard**

In `Chats/ChatHub.cs`, immediately after the `message == null` check (currently ending at line 551) and **before** the channel load at line 559:

```csharp
        // 2.5. Server-authored messages are not moderatable. A system message has no MessageSender, so
        // the author-exclusion fan-out at the tail of this method (GetConnectionIdsForUser on
        // message.Sender.BattleTag) would NullReferenceException — and there is no author to moderate
        // in any case. Rejecting here, before the durable MarkDeleted, keeps the whole method a no-op.
        if (message.Kind == MessageKind.System)
        {
            return new ChannelOperationResult(ChatResultCode.PermissionDenied);
        }
```

Add `using W3ChampionsChatService.Domain;` to `ChatHub.cs` if absent (it is already present). Extend the method's XML doc list with a bullet for the new step.

- [ ] **Step 4: Add the guard's hub-level test**

The guard lives on `ChatHub`, so its test belongs in `W3ChampionsChatService.Tests/ChatHubDeletionTests.cs`, which already owns the 20-dependency `ChatHub` construction plus `CreateChannel()` and `SeedMessage()` helpers. **Do not build a `ChatHub` by hand.**

That file uses the **classic** assert model (`Assert.AreEqual` / `Assert.IsNotNull`) — match it, not the constraint model used elsewhere in this plan.

First add a system-message seeder next to the existing `SeedMessage` (`ChatHubDeletionTests.cs:161`):

```csharp
    // Post-game chat Plan A Task 5: the server-authored counterpart of SeedMessage — same seq-allocation
    // path, but no sender and a structured body instead of content.
    private async Task<ChannelMessage> SeedSystemMessage(string channelId)
    {
        var seq = await _channelRepository.AllocateSeq(channelId, DateTime.UtcNow);
        var message = new ChannelMessage
        {
            ChannelId = channelId,
            Seq = seq,
            Kind = MessageKind.System,
            SystemMessage = new SystemMessageBody { Key = "match_intro", FallbackText = "Match on Amazonia" },
            SentAt = DateTime.UtcNow,
        };
        await _messageRepository.Insert(message);
        return message;
    }
```

Then the test:

```csharp
    [Test]
    public async Task DeleteMessage_OnSystemMessage_ReturnsPermissionDenied_AndDeletesNothing()
    {
        var channel = await CreateChannel();
        var message = await SeedSystemMessage(channel.Id);

        var result = await _chatHub.DeleteMessage(message.Id);

        Assert.AreEqual(ChatResultCode.PermissionDenied, result.Code,
            "a server-authored message has no author to moderate — and the author-exclusion fan-out would null-deref on Sender.BattleTag");

        var reloaded = await _messageRepository.Load(message.Id);
        Assert.IsNull(reloaded.Deleted, "the rejection happens BEFORE MarkDeleted — nothing is soft-deleted");
    }
```

Add `using W3ChampionsChatService.Domain;` to the file's using block if absent.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~SystemMessageModerationTests|FullyQualifiedName~DeleteMessage"`
Expected: PASS.

- [ ] **Step 6: Format and commit**

```bash
dotnet format
git add W3ChampionsChatService/Chats/ChatHub.cs W3ChampionsChatService.Tests/SystemMessageModerationTests.cs
git commit -m "fix(moderation): reject DeleteMessage on system messages before null-deref"
```

---

### Task 6: Match-channel activity preview

**Files:**
- Modify: `W3ChampionsChatService/FanOut/FanOutEngine.cs:205-215`
- Test: `W3ChampionsChatService.Tests/FanOutEngineTests.cs` (modify)

**Interfaces:**
- Consumes: existing `DmActivityPreviewDto(string SenderBattleTag, string SenderName, string Excerpt)` and `Excerpts.Bounded`.
- Produces: `ChannelActivity` for `System` + `SystemChannelKind.Match` channels now carries a preview. Public / SemiPublic / GroupDm / System+Clan stay preview-free.

**Note:** the spec says this change is in `ActivityCoalescer`. It is not — the coalescer is preview-agnostic and merely carries whatever it is handed. The decision is `FanOutEngine.cs:209-211`.

- [ ] **Step 1: Write the failing test**

Append to `W3ChampionsChatService.Tests/FanOutEngineTests.cs`, reusing that file's existing fixture and its `FanOutEngineTestFactory`:

```csharp
    [Test]
    public async Task MatchChannelActivity_CarriesPreview()
    {
        var channel = new ChatChannel
        {
            Id = "chan-match", Type = ChannelType.System, SystemKind = SystemChannelKind.Match,
        };
        // An UNFOCUSED, level-All member is the only one who receives ChannelActivity.
        _onlineMemberRegistry.Join("conn-bob", channel.Id, "Bob#1", NotificationLevel.All, 0, ChannelType.System);

        var message = new ChannelMessage
        {
            Id = "m1", ChannelId = channel.Id, Seq = 1,
            Sender = new MessageSender { BattleTag = "Alice#1", Name = "Alice" },
            Content = "gg wp", SentAt = Now,
        };

        await _engine.OnMessagePersisted(channel, message, senderConnectionId: "conn-alice", isShadow: false, Now);

        var activity = _harness.PayloadFor("conn-bob", ChatEvents.ChannelActivity) as ChannelActivityDto;
        Assert.That(activity, Is.Not.Null, "an unfocused level-All member receives coalesced activity");
        var preview = activity.Preview as DmActivityPreviewDto;
        Assert.That(preview, Is.Not.Null,
            "post-game chat needs a preview so the client can raise its one-time nudge toast");
        Assert.That(preview.SenderName, Is.EqualTo("Alice"));
        Assert.That(preview.Excerpt, Is.EqualTo("gg wp"));
    }

    [Test]
    public async Task PublicChannelActivity_StillCarriesNoPreview()
    {
        var channel = new ChatChannel { Id = "chan-public", Type = ChannelType.Public };
        _onlineMemberRegistry.Join("conn-bob", channel.Id, "Bob#1", NotificationLevel.All, 0, ChannelType.Public);

        var message = new ChannelMessage
        {
            Id = "m1", ChannelId = channel.Id, Seq = 1,
            Sender = new MessageSender { BattleTag = "Alice#1", Name = "Alice" },
            Content = "hello lounge", SentAt = Now,
        };

        await _engine.OnMessagePersisted(channel, message, senderConnectionId: "conn-alice", isShadow: false, Now);

        var activity = _harness.PayloadFor("conn-bob", ChatEvents.ChannelActivity) as ChannelActivityDto;
        Assert.That(activity.Preview, Is.Null,
            "the preview widening is scoped to match channels — a busy lounge must keep its badge-only treatment");
    }

    [Test]
    public async Task ClanChannelActivity_CarriesNoPreview()
    {
        var channel = new ChatChannel
        {
            Id = "chan-clan", Type = ChannelType.System, SystemKind = SystemChannelKind.Clan,
        };
        _onlineMemberRegistry.Join("conn-bob", channel.Id, "Bob#1", NotificationLevel.All, 0, ChannelType.System);

        var message = new ChannelMessage
        {
            Id = "m1", ChannelId = channel.Id, Seq = 1,
            Sender = new MessageSender { BattleTag = "Alice#1", Name = "Alice" },
            Content = "clan night", SentAt = Now,
        };

        await _engine.OnMessagePersisted(channel, message, senderConnectionId: "conn-alice", isShadow: false, Now);

        var activity = _harness.PayloadFor("conn-bob", ChatEvents.ChannelActivity) as ChannelActivityDto;
        Assert.That(activity.Preview, Is.Null,
            "System is not enough — only SystemKind.Match gets a preview");
    }
```

Adapt the fixture field names (`_engine`, `_harness`, `_onlineMemberRegistry`, `Now`) to whatever `FanOutEngineTests` already uses.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~FanOutEngineTests`
Expected: FAIL on `MatchChannelActivity_CarriesPreview` — preview is null. The other two PASS already.

- [ ] **Step 3: Widen the preview condition**

In `FanOut/FanOutEngine.cs`, replace the `dmPreview` block (currently lines 205-211) with:

```csharp
        // C5 (Task 9, D15) + post-game chat Plan A Task 6: the activity preview. Built ONCE per
        // persisted message (identical for every Offer call in the loop below). Two channel classes
        // get one:
        //   - Dm (C5/OQ-7): the original scope. A pending Dm never reaches the Offer call below at all,
        //     so this is only ever OFFERED for an accepted Dm.
        //   - System + Match (post-game chat): the client's ONE-TIME nudge toast after the score screen
        //     closes needs a sender and an excerpt; without a preview it has nothing to render, which is
        //     precisely why post-game messages were previously silent.
        // GroupDm / Public / SemiPublic / System+Clan deliberately stay preview-free — a busy lounge or
        // clan room keeps its badge-only treatment. Sender fields are REUSED from `dto.Sender` (the
        // MessageDto already built above) rather than a fresh lookup — no extra Mongo read.
        // The User conjunct is LOAD-BEARING, not defensive: SystemMessagePublisher calls this method on
        // exactly a System+Match channel, and a system message's Sender is null — without it the
        // dto.Sender dereference below is a guaranteed NullReferenceException on every published intro.
        var wantsPreview = message.Kind == MessageKind.User
            && (channel.Type == ChannelType.Dm
                || (channel.Type == ChannelType.System && channel.SystemKind == SystemChannelKind.Match));
        object activityPreview = wantsPreview
            ? new DmActivityPreviewDto(dto.Sender.BattleTag, dto.Sender.Name, Excerpts.Bounded(message.Content))
            : null;
```

Then rename the single use of `dmPreview` in the `Offer` call at the bottom of the method:

```csharp
            await _activityCoalescer.Offer(connectionId, channel.Id, message.Seq, now, activityPreview);
```

- [ ] **Step 4: Add the system-message guard test**

Append to `FanOutEngineTests.cs`:

```csharp
    [Test]
    public async Task SystemMessageInMatchChannel_ProducesNoPreview_AndDoesNotThrow()
    {
        var channel = new ChatChannel
        {
            Id = "chan-match", Type = ChannelType.System, SystemKind = SystemChannelKind.Match,
        };
        _onlineMemberRegistry.Join("conn-bob", channel.Id, "Bob#1", NotificationLevel.All, 0, ChannelType.System);

        var systemMessage = new ChannelMessage
        {
            Id = "m1", ChannelId = channel.Id, Seq = 1, Kind = MessageKind.System,
            SystemMessage = new SystemMessageBody { Key = "match_intro", FallbackText = "Match on Amazonia" },
            SentAt = Now,
        };

        Assert.DoesNotThrowAsync(async () =>
            await _engine.OnMessagePersisted(channel, systemMessage, senderConnectionId: null, isShadow: false, Now),
            "a system message has a null Sender — the preview build must not dereference it");

        var activity = _harness.PayloadFor("conn-bob", ChatEvents.ChannelActivity) as ChannelActivityDto;
        Assert.That(activity.Preview, Is.Null, "there is no sender to preview");
    }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~FanOutEngineTests`
Expected: PASS.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test`
Expected: PASS, all ~1283 tests.

- [ ] **Step 7: Format and commit**

```bash
dotnet format
git add W3ChampionsChatService/FanOut/FanOutEngine.cs W3ChampionsChatService.Tests/FanOutEngineTests.cs
git commit -m "feat(fanout): carry activity preview for match channels"
```

---

## Done criteria for Plan A

- `dotnet test` green with Docker running.
- `dotnet format --verify-no-changes` clean.
- `POST /internal/channels/{ref}/system-message` publishes an idempotent, structured, fan-out-delivered system message into an existing match channel.
- Match-channel `ChannelActivity` carries a sender + excerpt preview; every other non-DM channel class still does not.
- Nothing user-visible changes yet — no client consumes any of this until Plans B and C.

## Follow-on plans

- **Plan B (matchmaking-service):** flip the channel-name precedence to `mapName || gamename || _id`; publish `match_intro` on ladder match-loaded and on custom-game `GAME_STARTED`, through the existing fire-and-forget `matchChatService` façade with `dedupeKey: "match_intro"`. Must not touch `finishMatch`.
- **Plan C (launcher-e):** the DM-bar match pill, `matchActivity` notification routing with the one-time nudge, the `SYSTEM_MESSAGE_RENDERERS` registry, and the dead-channel path.
