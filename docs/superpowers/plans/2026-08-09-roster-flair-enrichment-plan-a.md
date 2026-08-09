# Roster Flair Enrichment (Plan A) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a chat channel's viewer roster carry each viewer's flair, so a user renders with their real avatar the moment they appear in the user list instead of only after they send a message.

**Architecture:** `FocusChannel` already resolves each roster entry's display name via an in-memory `ISessionRegistry.GetByBattleTag` lookup. A new `ViewerResolver` service extends that same lookup to also read the viewer's `ChatUser` out of `ConnectionMapping` and map it to a `ChatProfile` — no Mongo read, no website-backend call, and identical by construction to the flair that user's own messages carry. Both `FocusChannel` (initial roster) and `ViewersAccumulator` (roster deltas) build their viewer DTOs through that one resolver. The client stores flair in a user-keyed store slice fed by live sources only, and `ChatUserListPanel` reads it instead of scavenging flair out of cached messages.

**Tech Stack:** C# / .NET 8, ASP.NET Core SignalR, NUnit + Moq + Testcontainers (chat-service); TypeScript, React, easy-peasy (launcher-e).

## Global Constraints

- **Spec:** `docs/superpowers/specs/2026-08-09-roster-flair-enrichment-design.md`. This plan implements §3.1 and §4 only. Live propagation (§3.2, §3.3) is Plan B and is explicitly out of scope here.
- **Worktrees:** chat-service work happens in `C:\Users\Marco\git_projects\w3champions\.worktrees\chat-service-roster-flair`; launcher-e work in `C:\Users\Marco\git_projects\w3champions\.worktrees\launcher-e-roster-flair`. Both are on branch `feat/roster-flair-enrichment`.
- **Wire compatibility:** shape-changing wire edits are permitted. The launcher force-updates before it can connect, so `Joined: string[]` → `Joined: ChannelViewerDto[]` is acceptable. Do not add legacy-compat duplicate fields.
- **Single mapper:** all `ChatUser` → `ChatProfile` conversion goes through `ChatProfileMapper.FromChatUser`. Never hand-roll the mapping; the roster and `sender.flair` must not be able to drift.
- **No DB on the roster path:** `ViewerResolver` must not touch `UserDirectoryRepository`, Mongo, or `IWebsiteBackendRepository`. Roster entries resolve from in-memory registries only.
- **launcher-e has no test runner.** Do not add one and do not write frontend tests. Verification there is `npm run type-check`, `npm run lint:prod`, `npm run dprint`, `npm run check:i18n`.
- **Docker must be running** for the chat-service suite (Testcontainers spins up Mongo).
- **Baseline (verified 2026-08-09):** chat-service 1319 passed / 0 failed; launcher-e type-check, lint:prod, dprint all clean. Any deviation is a regression you introduced.
- **Shell note:** the worktree-isolated session resets the shell cwd back to the chat-service worktree after each command, and rejects compound commands containing redirects. Re-`cd` at the start of every command and keep commands plain.

---

## File Structure

**chat-service** (`.worktrees/chat-service-roster-flair`)

| File | Responsibility |
|---|---|
| `W3ChampionsChatService/Protocol/ChannelViewerDto.cs` | *Modify.* Roster entry wire shape; gains `Profile`. |
| `W3ChampionsChatService/Chats/ViewerResolver.cs` | **Create.** The single place that turns a roster battleTag into a `ChannelViewerDto`. Sole owner of the session→connection→`ChatUser`→`ChatProfile` hop. |
| `W3ChampionsChatService/Chats/ChatHub.Channels.cs` | *Modify.* `FocusChannel` builds its roster via `ViewerResolver`; `ResolveViewerName` is deleted. |
| `W3ChampionsChatService/Protocol/ChatJsonProtocol.cs` | **Create.** Named, testable SignalR JSON payload configuration (null omission). |
| `W3ChampionsChatService/Startup.cs` | *Modify.* Register `ViewerResolver`; apply `ChatJsonProtocol.Configure`. |
| `W3ChampionsChatService/Protocol/ViewersChangedDto.cs` | *Modify.* `Joined` becomes `IReadOnlyList<ChannelViewerDto>`. |
| `W3ChampionsChatService/FanOut/ViewersAccumulator.cs` | *Modify.* Resolves joined entries through `ViewerResolver` at flush. |
| `W3ChampionsChatService.Tests/ChatHubFocusTests.cs` | *Modify.* Roster-flair coverage. |
| `W3ChampionsChatService.Tests/ChatJsonProtocolTests.cs` | **Create.** Pins null-omitting serialization. |
| `W3ChampionsChatService.Tests/ViewersAccumulatorTests.cs` | *Modify.* Joined-carries-flair coverage. |
| `W3ChampionsChatService.Tests/ViewersAccumulatorTestFactory.cs` | *Modify.* Thread the new dependency through the one shared place. |

**launcher-e** (`.worktrees/launcher-e-roster-flair`)

| File | Responsibility |
|---|---|
| `src/types/chat-protocol.types.ts` | *Modify.* `IChannelViewerDto.profile`; `IViewersChangedDto.joined` becomes objects. |
| `src/models/chat-core.ts` | *Modify.* `flairByBattleTag` store slice + its writers. |
| `src/components/chat/ChatUserListPanel.tsx` | *Modify.* Read the store slice; delete the message-scavenging memo. |
| `src/helpers/chat-ui.helper.ts` | *Modify.* Doc-comment correction on `viewerToChatUser` only. |

---

### Task 1: `ViewerResolver` and roster flair on `FocusChannel`

**Files:**
- Create: `W3ChampionsChatService/Chats/ViewerResolver.cs`
- Modify: `W3ChampionsChatService/Protocol/ChannelViewerDto.cs`
- Modify: `W3ChampionsChatService/Chats/ChatHub.Channels.cs` (roster build at lines 116-118; delete `ResolveViewerName` at lines 202-206)
- Modify: `W3ChampionsChatService/Startup.cs` (register `ViewerResolver`)
- Test: `W3ChampionsChatService.Tests/ChatHubFocusTests.cs`

**Interfaces:**
- Consumes: `ISessionRegistry.GetByBattleTag(string) : ChatSession`; `ChatSession.ConnectionId`, `ChatSession.Identity`; `ConnectionMapping.GetUser(string connectionId) : ChatUser`; `ChatProfileMapper.FromChatUser(ChatUser) : ChatProfile`.
- Produces:
  - `record ChannelViewerDto(string BattleTag, string Name, ChatProfile Profile = null)`
  - `class ViewerResolver(ISessionRegistry sessionRegistry, ConnectionMapping connections)` with `ChannelViewerDto Resolve(string battleTag)`

- [ ] **Step 1: Write the failing test**

Append to `W3ChampionsChatService.Tests/ChatHubFocusTests.cs`, immediately before the closing `}` of the class:

```csharp
    [Test]
    public async Task FocusChannel_Roster_CarriesEachViewersFlair()
    {
        var channel = await CreateChannel();

        RegisterSession("conn-peter", BattleTag, "Peter");
        RegisterSession("conn-alice", OtherBattleTag, "Alice");
        SeedMembership("conn-peter", channel.Id, BattleTag);
        SeedMembership("conn-alice", channel.Id, OtherBattleTag);

        // ConnectionMapping is what BuildSenderSnapshot reads for message flair, so seeding it here
        // pins that the roster resolves from the SAME source — the two can never disagree.
        _connectionMapping.RegisterUser("conn-alice", new ChatUser(
            OtherBattleTag,
            false,
            "W3C",
            new ProfilePicture { Race = AvatarCategory.NE, PictureId = 7, IsClassic = false },
            new ChatColor("chat_color_purple"),
            [new ChatIcon("chat_icon_crown")]));

        var aliceHub = BuildHub("conn-alice");
        await aliceHub.FocusChannel(channel.Id);

        var peterHub = BuildHub("conn-peter");
        var result = await peterHub.FocusChannel(channel.Id);

        Assert.AreEqual(ChatResultCode.Ok, result.Code);
        var alice = result.Viewers.Single(v => v.BattleTag == OtherBattleTag);
        Assert.IsNotNull(alice.Profile, "A roster entry for an online, focused viewer must carry flair");
        Assert.AreEqual(AvatarCategory.NE, alice.Profile.ProfilePicture.Race);
        Assert.AreEqual(7, alice.Profile.ProfilePicture.PictureId);
        Assert.AreEqual("W3C", alice.Profile.ClanId);
        Assert.AreEqual("chat_color_purple", alice.Profile.ChatColor.ColorId);
        Assert.AreEqual("chat_icon_crown", alice.Profile.ChatIcons.Single().IconId);
    }

    [Test]
    public async Task FocusChannel_Roster_ViewerWithNoConnectionMappingEntry_YieldsNullProfile_NotAnException()
    {
        // Teardown race: a session exists but its ConnectionMapping entry is already gone. The entry
        // must survive with a null Profile (the client falls back to its default avatar) rather than
        // being dropped or throwing — mirroring the pre-existing battleTag name fallback.
        var channel = await CreateChannel();

        RegisterSession("conn-peter", BattleTag, "Peter");
        SeedMembership("conn-peter", channel.Id, BattleTag);

        var hub = BuildHub("conn-peter");
        var result = await hub.FocusChannel(channel.Id);

        Assert.AreEqual(ChatResultCode.Ok, result.Code);
        var peter = result.Viewers.Single();
        Assert.AreEqual(BattleTag, peter.BattleTag);
        Assert.AreEqual("Peter", peter.Name, "The display name must still resolve from the live session");
        Assert.IsNull(peter.Profile);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
cd "C:/Users/Marco/git_projects/w3champions/.worktrees/chat-service-roster-flair"
dotnet test --filter "FullyQualifiedName~ChatHubFocusTests.FocusChannel_Roster" --nologo
```

Expected: FAIL to compile — `'ChannelViewerDto' does not contain a definition for 'Profile'`.

- [ ] **Step 3: Add `Profile` to the roster DTO**

Replace the entire contents of `W3ChampionsChatService/Protocol/ChannelViewerDto.cs`:

```csharp
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Protocol;

/// <summary>
/// Active-viewer roster entry for <see cref="FocusChannelResult"/> and <see cref="ViewersChangedDto"/>.
/// <para>
/// <see cref="Profile"/> is the viewer's flair, resolved in-memory by
/// <see cref="Chats.ViewerResolver"/> from the same <c>ConnectionMapping</c> entry that
/// <c>ChatHub.BuildSenderSnapshot</c> reads for message flair — so a user's roster avatar and their
/// message avatar are the same value by construction, never merely by convention. NULL only for a
/// viewer whose live session or connection entry vanished mid-call (a teardown race); clients render
/// their default avatar for that case. Defaulted so existing 2-arg construction still compiles.
/// </para>
/// </summary>
public record ChannelViewerDto(string BattleTag, string Name, ChatProfile Profile = null);
```

- [ ] **Step 4: Create the resolver**

Create `W3ChampionsChatService/Chats/ViewerResolver.cs`:

```csharp
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Protocol;
using W3ChampionsChatService.Sessions;

namespace W3ChampionsChatService.Chats;

/// <summary>
/// Builds a <see cref="ChannelViewerDto"/> for a roster battleTag. The SINGLE place that knows the
/// session → connection → <see cref="ChatUser"/> → <see cref="ChatProfile"/> hop, shared by
/// <c>ChatHub.FocusChannel</c> (initial roster) and <see cref="FanOut.ViewersAccumulator"/> (roster
/// deltas) so the two can never construct roster entries differently.
/// <para>
/// PURELY IN-MEMORY by design: a roster entry is by definition an online AND focused viewer, so its
/// <see cref="ChatUser"/> is already cached in <see cref="ConnectionMapping"/> from that user's own
/// connect. This must never grow a Mongo or website-backend read — <c>FocusChannel</c> is a hot path
/// and the roster can be several hundred entries.
/// </para>
/// <para>
/// Degradation is per-field and never throws: a missing session falls back to the battleTag as the
/// display name (pre-existing behaviour, preserved), and a missing <see cref="ChatUser"/> yields a
/// null <see cref="ChannelViewerDto.Profile"/>. Dropping the entry would be worse — the viewer would
/// silently vanish from the roster.
/// </para>
/// </summary>
public class ViewerResolver(ISessionRegistry sessionRegistry, ConnectionMapping connections)
{
    private readonly ISessionRegistry _sessionRegistry = sessionRegistry;
    private readonly ConnectionMapping _connections = connections;

    public ChannelViewerDto Resolve(string battleTag)
    {
        var session = _sessionRegistry.GetByBattleTag(battleTag);
        if (session == null)
        {
            return new ChannelViewerDto(battleTag, battleTag, null);
        }

        var name = session.Identity?.Name ?? battleTag;
        var chatUser = _connections.GetUser(session.ConnectionId);

        return new ChannelViewerDto(
            battleTag,
            name,
            chatUser == null ? null : ChatProfileMapper.FromChatUser(chatUser));
    }
}
```

- [ ] **Step 5: Use the resolver in `FocusChannel` and delete the old helper**

In `W3ChampionsChatService/Chats/ChatHub.Channels.cs`, replace the roster build (currently lines 116-118):

```csharp
        var viewers = _focusRegistry.GetRoster(channelId)
            .Select(rosterBattleTag => new ChannelViewerDto(rosterBattleTag, ResolveViewerName(rosterBattleTag)))
            .ToList();
```

with:

```csharp
        var viewers = _focusRegistry.GetRoster(channelId)
            .Select(_viewerResolver.Resolve)
            .ToList();
```

Then delete this method entirely (currently lines 202-206) — `_viewerResolver` subsumes it and it has no other call site:

```csharp
    private string ResolveViewerName(string battleTag)
    {
        var session = _sessionRegistry.GetByBattleTag(battleTag);
        return session?.Identity?.Name ?? battleTag;
    }
```

- [ ] **Step 6: Give `ChatHub` a resolver WITHOUT changing its constructor signature**

`ChatHub` already receives `connections` and `sessionRegistry` as primary-constructor parameters (lines 24 and 27), so the resolver can be built in a field initializer from those existing parameters. **Do not add a constructor parameter** — 31 test files construct `new ChatHub(...)`, and changing the signature would churn every one of them for no behavioural gain.

In `W3ChampionsChatService/Chats/ChatHub.cs`, add this field immediately after `_sessionRegistry` (line 109):

```csharp
    // Built from the primary-constructor params rather than injected, deliberately: adding a ctor
    // parameter would force an edit to all 31 test files that construct a ChatHub. ViewerResolver is
    // stateless (it holds only references to the two singletons above), so this instance and the
    // DI-registered singleton the ViewersAccumulator receives are interchangeable.
    private readonly ViewerResolver _viewerResolver = new(sessionRegistry, connections);
```

- [ ] **Step 7: Register the resolver for `ViewersAccumulator`**

Task 3 injects `ViewerResolver` into `ViewersAccumulator` through DI, which needs a registration. In `W3ChampionsChatService/Startup.cs`, immediately after the `ConnectionMapping` registration (line 175):

```csharp
        // Singleton: holds only the singleton ConnectionMapping + ISessionRegistry. Consumed by
        // ViewersAccumulator; ChatHub builds its own equivalent instance (see ChatHub.cs).
        services.AddSingleton<ViewerResolver>();
```

- [ ] **Step 8: Run the new tests to verify they pass**

```bash
cd "C:/Users/Marco/git_projects/w3champions/.worktrees/chat-service-roster-flair"
dotnet test --filter "FullyQualifiedName~ChatHubFocusTests.FocusChannel_Roster" --nologo
```

Expected: `Passed! - Failed: 0, Passed: 2`

- [ ] **Step 9: Run the full suite for regressions**

```bash
cd "C:/Users/Marco/git_projects/w3champions/.worktrees/chat-service-roster-flair"
dotnet test --nologo
```

Expected: `Failed: 0, Passed: 1321` (the 1319 baseline plus the 2 new tests).

- [ ] **Step 10: Commit**

```bash
cd "C:/Users/Marco/git_projects/w3champions/.worktrees/chat-service-roster-flair"
git add W3ChampionsChatService/Chats/ViewerResolver.cs W3ChampionsChatService/Protocol/ChannelViewerDto.cs W3ChampionsChatService/Chats/ChatHub.Channels.cs W3ChampionsChatService/Chats/ChatHub.cs W3ChampionsChatService/Startup.cs W3ChampionsChatService.Tests
git commit -m "feat(chat): carry viewer flair on the FocusChannel roster"
```

---

### Task 2: Null-omitting wire serialization

**Files:**
- Create: `W3ChampionsChatService/Protocol/ChatJsonProtocol.cs`
- Modify: `W3ChampionsChatService/Startup.cs` (the `AddSignalR` block, lines 41-54)
- Test: `W3ChampionsChatService.Tests/ChatJsonProtocolTests.cs`

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces: `static class ChatJsonProtocol` with `static void Configure(JsonHubProtocolOptions options)`.

Extracting the configuration into a named static rather than an inline lambda in `Startup` is deliberate: it makes the wire contract directly unit-testable without bootstrapping the DI container.

- [ ] **Step 1: Write the failing test**

Create `W3ChampionsChatService.Tests/ChatJsonProtocolTests.cs`:

```csharp
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using NUnit.Framework;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Domain;
using W3ChampionsChatService.Protocol;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// Pins the hub's JSON payload contract. Flair is the dominant payload on the wire (a roster can be
/// several hundred entries, each carrying a ChatProfile), and most users have null clan/colour/icons
/// and null rank — with nulls serialized, the KEY NAMES alone dominate the payload. Null omission is
/// therefore load-bearing for payload size, not cosmetic.
/// </summary>
public class ChatJsonProtocolTests
{
    private static JsonSerializerOptions ConfiguredOptions()
    {
        var options = new JsonHubProtocolOptions();
        ChatJsonProtocol.Configure(options);
        return options.PayloadSerializerOptions;
    }

    [Test]
    public void Configure_OmitsNullFlairFields()
    {
        var viewer = new ChannelViewerDto("peter#123", "Peter", new ChatProfile
        {
            ProfilePicture = new ProfilePicture { Race = AvatarCategory.HU, PictureId = 2, IsClassic = false },
        });

        var json = JsonSerializer.Serialize(viewer, ConfiguredOptions());

        Assert.That(json, Does.Not.Contain("clanId"), "A null clanId must not occupy wire bytes");
        Assert.That(json, Does.Not.Contain("chatColor"));
        Assert.That(json, Does.Not.Contain("chatIcons"));
        Assert.That(json, Does.Not.Contain("leagueName"));
        Assert.That(json, Does.Not.Contain("gamesPlayed"));
    }

    [Test]
    public void Configure_PreservesNonNullFields()
    {
        var viewer = new ChannelViewerDto("peter#123", "Peter", new ChatProfile
        {
            ClanId = "W3C",
            ProfilePicture = new ProfilePicture { Race = AvatarCategory.NE, PictureId = 7, IsClassic = true },
            ChatColor = new ChatColor("chat_color_purple"),
        });

        var json = JsonSerializer.Serialize(viewer, ConfiguredOptions());

        Assert.That(json, Does.Contain("\"clanId\":\"W3C\""));
        Assert.That(json, Does.Contain("\"pictureId\":7"));
        Assert.That(json, Does.Contain("\"isClassic\":true"));
        Assert.That(json, Does.Contain("chat_color_purple"));
    }

    [Test]
    public void Configure_OmittingNullsDoesNotDropFalseOrZero()
    {
        // WhenWritingNull must not be confused with WhenWritingDefault: `isClassic: false` and
        // `pictureId: 0` are MEANINGFUL values the client renders, not absences.
        var viewer = new ChannelViewerDto("peter#123", "Peter", new ChatProfile
        {
            ProfilePicture = new ProfilePicture { Race = AvatarCategory.RnD, PictureId = 0, IsClassic = false },
        });

        var json = JsonSerializer.Serialize(viewer, ConfiguredOptions());

        Assert.That(json, Does.Contain("\"pictureId\":0"));
        Assert.That(json, Does.Contain("\"isClassic\":false"));
        Assert.That(json, Does.Contain("\"race\":0"));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
cd "C:/Users/Marco/git_projects/w3champions/.worktrees/chat-service-roster-flair"
dotnet test --filter "FullyQualifiedName~ChatJsonProtocolTests" --nologo
```

Expected: FAIL to compile — `The name 'ChatJsonProtocol' does not exist in the current context`.

- [ ] **Step 3: Create the configuration**

Create `W3ChampionsChatService/Protocol/ChatJsonProtocol.cs`:

```csharp
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;

namespace W3ChampionsChatService.Protocol;

/// <summary>
/// The hub's JSON payload contract, extracted from <c>Startup</c> as a named unit so it can be unit
/// tested directly (see <c>ChatJsonProtocolTests</c>) instead of only through a DI bootstrap.
/// </summary>
public static class ChatJsonProtocol
{
    /// <summary>
    /// Omits null properties from every hub payload.
    /// <para>
    /// <see cref="JsonIgnoreCondition.WhenWritingNull"/> — deliberately NOT
    /// <c>WhenWritingDefault</c>, which would also drop <c>false</c> and <c>0</c>. Those are
    /// meaningful for flair: <c>isClassic: false</c> and <c>race: 0</c> (RnD) are real values the
    /// client renders, and dropping them would silently change avatars.
    /// </para>
    /// <para>
    /// Safe for the launcher: every flair field is already declared optional in
    /// <c>chat-protocol.types.ts</c> and every client read path uses optional chaining with a
    /// fallback, so an absent key and an explicit null are indistinguishable there.
    /// </para>
    /// </summary>
    public static void Configure(JsonHubProtocolOptions options)
    {
        options.PayloadSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    }
}
```

- [ ] **Step 4: Apply it in `Startup`**

In `W3ChampionsChatService/Startup.cs`, change the `AddSignalR` registration so the chained
`AddJsonProtocol` call applies the configuration. The closing line of the block (currently line 54) becomes:

```csharp
        })
        .AddJsonProtocol(ChatJsonProtocol.Configure);
```

- [ ] **Step 5: Run the tests to verify they pass**

```bash
cd "C:/Users/Marco/git_projects/w3champions/.worktrees/chat-service-roster-flair"
dotnet test --filter "FullyQualifiedName~ChatJsonProtocolTests" --nologo
```

Expected: `Passed! - Failed: 0, Passed: 3`

- [ ] **Step 6: Run the full suite**

```bash
cd "C:/Users/Marco/git_projects/w3champions/.worktrees/chat-service-roster-flair"
dotnet test --nologo
```

Expected: `Failed: 0, Passed: 1324`

- [ ] **Step 7: Commit**

```bash
cd "C:/Users/Marco/git_projects/w3champions/.worktrees/chat-service-roster-flair"
git add W3ChampionsChatService/Protocol/ChatJsonProtocol.cs W3ChampionsChatService/Startup.cs W3ChampionsChatService.Tests/ChatJsonProtocolTests.cs
git commit -m "feat(chat): omit null fields from hub JSON payloads"
```

---

### Task 3: `ViewersChanged` joins carry flair

**Files:**
- Modify: `W3ChampionsChatService/Protocol/ViewersChangedDto.cs`
- Modify: `W3ChampionsChatService/FanOut/ViewersAccumulator.cs` (constructor at line 45; joined build at lines 123-133)
- Modify: `W3ChampionsChatService.Tests/ViewersAccumulatorTestFactory.cs`
- Test: `W3ChampionsChatService.Tests/ViewersAccumulatorTests.cs`

**Interfaces:**
- Consumes: `ViewerResolver.Resolve(string) : ChannelViewerDto` from Task 1.
- Produces: `record ViewersChangedDto(string ChannelId, IReadOnlyList<ChannelViewerDto> Joined, IReadOnlyList<string> Left)`.

`Left` stays a `string[]` — removing a viewer needs only their battleTag, and sending flair for someone who just left would be pure waste.

- [ ] **Step 1: Write the failing test**

Append to `W3ChampionsChatService.Tests/ViewersAccumulatorTests.cs`, immediately before the closing `}` of the class:

```csharp
    [Test]
    public async Task FlushDue_JoinedEntries_CarryFlairAndDisplayName()
    {
        // This test needs SEEDED registries so the resolver has flair to find, so it builds its own
        // accumulator rather than using NewAccumulator() (which wires empty ones).
        var harness = new HubPushCaptureHarness();
        var focus = new FocusRegistry();
        var sessions = new SessionRegistry();
        var connections = new ConnectionMapping();
        var accumulator = new ViewersAccumulator(
            harness.HubContext, focus, new ViewerResolver(sessions, connections));

        const string joiner = "alice#1";

        sessions.Register("conn-a", new W3CUserAuthentication { BattleTag = joiner, Name = "Alice" }, null);
        connections.RegisterUser("conn-a", new ChatUser(
            joiner,
            false,
            "W3C",
            new ProfilePicture { Race = AvatarCategory.UD, PictureId = 4, IsClassic = false },
            new ChatColor("chat_color_gold"),
            [new ChatIcon("chat_icon_star")]));

        accumulator.RecordChange(ChannelId, joiner, T0);
        focus.Focus("conn-a", ChannelId, joiner);

        await accumulator.FlushDue(T0 + Flush);

        var batch = ViewersChangedFor(harness, "conn-a").Single();
        var joined = batch.Joined.Single();

        Assert.AreEqual(joiner, joined.BattleTag);
        Assert.AreEqual("Alice", joined.Name, "A join must carry the display name, not just the battleTag");
        Assert.IsNotNull(joined.Profile, "A join must carry flair so the roster never renders a default avatar");
        Assert.AreEqual(AvatarCategory.UD, joined.Profile.ProfilePicture.Race);
        Assert.AreEqual(4, joined.Profile.ProfilePicture.PictureId);
        Assert.AreEqual("chat_color_gold", joined.Profile.ChatColor.ColorId);
    }
```

If `ViewersAccumulatorTests.cs` lacks any of these usings, add them: `W3ChampionsChatService.Authentication`, `W3ChampionsChatService.Chats`, `W3ChampionsChatService.Domain`, `W3ChampionsChatService.Sessions`.

- [ ] **Step 2: Run the test to verify it fails**

```bash
cd "C:/Users/Marco/git_projects/w3champions/.worktrees/chat-service-roster-flair"
dotnet test --filter "FullyQualifiedName~ViewersAccumulatorTests.FlushDue_JoinedEntries_CarryFlairAndDisplayName" --nologo
```

Expected: FAIL to compile — the `ViewersAccumulator` constructor takes 2 arguments, not 3.

- [ ] **Step 3: Change the DTO**

In `W3ChampionsChatService/Protocol/ViewersChangedDto.cs`, replace the record declaration (lines 21-24):

```csharp
public record ViewersChangedDto(
    string ChannelId,
    IReadOnlyList<ChannelViewerDto> Joined,
    IReadOnlyList<string> Left);
```

In the same file's doc comment, replace this sentence:

```
/// <see cref="Joined"/>/<see cref="Left"/> are battleTags (NOT display names): the accumulator's only
/// state sources are <see cref="FanOut.FocusRegistry"/> and the emit <c>IHubContext</c> — it never
/// resolves names (that is <c>FocusChannel</c>'s job for the initial roster).
```

with:

```
/// <see cref="Left"/> entries are bare battleTags — removing a viewer needs no more than that.
/// <see cref="Joined"/> entries are full <see cref="ChannelViewerDto"/>s carrying display name and
/// flair, resolved through the same <see cref="Chats.ViewerResolver"/> <c>FocusChannel</c> uses for
/// the initial roster, so a viewer's rendering is identical whether the client learned about them
/// from a focus response or from a later join delta.
```

- [ ] **Step 4: Resolve joined entries in the accumulator**

In `W3ChampionsChatService/FanOut/ViewersAccumulator.cs`, change the class declaration (line 45):

```csharp
public class ViewersAccumulator(
    IHubContext<ChatHub> hubContext,
    FocusRegistry focusRegistry,
    Chats.ViewerResolver viewerResolver)
```

Add a backing field alongside the existing `_focusRegistry` field:

```csharp
    // Resolves a joined battleTag into a full roster entry (display name + flair). Shared with
    // ChatHub.FocusChannel so a join delta and an initial roster can never render differently.
    private readonly Chats.ViewerResolver _viewerResolver = viewerResolver;
```

Change the joined accumulator declaration (line 123) from:

```csharp
                var joined = new List<string>();
```

to:

```csharp
                var joined = new List<ChannelViewerDto>();
```

and the join branch (line 128) from:

```csharp
                        joined.Add(battleTag);
```

to:

```csharp
                        joined.Add(_viewerResolver.Resolve(battleTag));
```

Ensure the file has `using W3ChampionsChatService.Protocol;`.

- [ ] **Step 5: Update the shared test factory**

In `W3ChampionsChatService.Tests/ViewersAccumulatorTestFactory.cs`, replace the factory method:

```csharp
    internal static ViewersAccumulator CreateIgnored() =>
        new ViewersAccumulator(
            new HubPushCaptureHarness().HubContext,
            new FocusRegistry(),
            new W3ChampionsChatService.Chats.ViewerResolver(
                new W3ChampionsChatService.Sessions.SessionRegistry(),
                new W3ChampionsChatService.Chats.ConnectionMapping()));
```

- [ ] **Step 6: Update the existing accumulator test helpers**

Two helpers in `W3ChampionsChatService.Tests/ViewersAccumulatorTests.cs` need adjusting. Both changes are designed so that **no existing test body changes**.

First, `NewAccumulator()` (lines 51-58) gains a resolver over empty registries — the existing tests assert on battleTags only, so a null `Profile` is correct for them:

```csharp
    private static (HubPushCaptureHarness harness, FocusRegistry focus, ViewersAccumulator accumulator)
        NewAccumulator()
    {
        var harness = new HubPushCaptureHarness();
        var focus = new FocusRegistry();
        var accumulator = new ViewersAccumulator(
            harness.HubContext, focus, new ViewerResolver(new SessionRegistry(), new ConnectionMapping()));
        return (harness, focus, accumulator);
    }
```

Second, the `Contains` helper (lines 69-70) is currently called with both `batch.Joined` and `batch.Left`. `Left` is still `IEnumerable<string>`, but `Joined` is now `IEnumerable<ChannelViewerDto>`. **Keep the existing string overload and add a second one** — overload resolution then routes every existing call site correctly with no edits to any test body:

```csharp
    private static bool Contains(IEnumerable<string> tags, string battleTag) =>
        tags.Any(t => string.Equals(t, battleTag, StringComparison.OrdinalIgnoreCase));

    private static bool Contains(IEnumerable<ChannelViewerDto> viewers, string battleTag) =>
        viewers.Any(v => string.Equals(v.BattleTag, battleTag, StringComparison.OrdinalIgnoreCase));
```

- [ ] **Step 7: Run the test to verify it passes**

```bash
cd "C:/Users/Marco/git_projects/w3champions/.worktrees/chat-service-roster-flair"
dotnet test --filter "FullyQualifiedName~ViewersAccumulatorTests" --nologo
```

Expected: all `ViewersAccumulatorTests` pass, including the new one.

- [ ] **Step 8: Run the full suite**

```bash
cd "C:/Users/Marco/git_projects/w3champions/.worktrees/chat-service-roster-flair"
dotnet test --nologo
```

Expected: `Failed: 0, Passed: 1325`

- [ ] **Step 9: Commit**

```bash
cd "C:/Users/Marco/git_projects/w3champions/.worktrees/chat-service-roster-flair"
git add W3ChampionsChatService/Protocol/ViewersChangedDto.cs W3ChampionsChatService/FanOut/ViewersAccumulator.cs W3ChampionsChatService.Tests
git commit -m "feat(chat): carry viewer flair on ViewersChanged joins"
```

---

### Task 4: Client protocol types and the flair store slice

**Files:**
- Modify: `.worktrees/launcher-e-roster-flair/src/types/chat-protocol.types.ts` (lines 213-216, 248-252)
- Modify: `.worktrees/launcher-e-roster-flair/src/models/chat-core.ts` (state type ~line 125; init ~line 213; wipe ~line 274; `ownProfile` assignment line 316; `addFocused` lines 331-337; `ingestViewersChanged` lines 347-359)

**Interfaces:**
- Consumes: the Task 1 and Task 3 wire shapes — `ChannelViewerDto { battleTag, name, profile? }` and `ViewersChangedDto { channelId, joined: ChannelViewerDto[], left: string[] }`.
- Produces: `chatStore.flairByBattleTag: Record<string, IChatProfileDto>`, keyed on **lowercased** battleTag.

All work in this task is in the launcher-e worktree. There is no test runner; correctness is enforced by `tsc` plus the panel change in Task 5.

- [ ] **Step 1: Add `profile` to the viewer DTO**

In `src/types/chat-protocol.types.ts`, replace the `IChannelViewerDto` block (lines 209-216):

```ts
/**
 * Wire DTO: a viewer in a focused channel.
 *
 * `profile` is the viewer's flair, resolved server-side from the same in-memory
 * `ConnectionMapping` entry that supplies `sender.flair` on that user's messages —
 * so a user's roster avatar and their message avatar agree by construction. It is
 * optional because the server sends null for a viewer whose session vanished
 * mid-call, and because null fields are omitted from the wire entirely.
 */
export interface IChannelViewerDto {
    battleTag: string;
    name: string;
    profile?: IChatProfileDto | null;
}
```

- [ ] **Step 2: Make `joined` carry viewers**

In the same file, replace the `IViewersChangedDto` block (lines 245-252):

```ts
/**
 * Wire DTO: viewers changed in a focused channel.
 *
 * `joined` carries full viewer entries (display name + flair) so a user who joins
 * mid-session renders correctly immediately; `left` needs only battleTags.
 */
export interface IViewersChangedDto {
    channelId: string;
    joined: IChannelViewerDto[];
    left: string[];
}
```

- [ ] **Step 3: Declare the store slice**

In `src/models/chat-core.ts`, immediately after the `viewersByChannel` state declaration (line 125):

```ts
    /**
     * Flair keyed by LOWERCASED battleTag. Fed by live sources only — the
     * `FocusChannel` roster, `ViewersChanged` joins, and `ownProfile` — never by
     * message senders: `sender.flair` is frozen at send time, so an old history
     * message would otherwise clobber current flair with a stale snapshot.
     * Message rows read `sender.flair` directly and are unaffected.
     */
    flairByBattleTag: Record<string, IChatProfileDto>;
```

Add `IChatProfileDto` to the existing `chat-protocol.types` import in this file if it is not already imported.

- [ ] **Step 4: Initialise and wipe the slice**

In the same file, immediately after the `viewersByChannel: {},` initialiser (line 213):

```ts
        flairByBattleTag: {},
```

and immediately after `state.viewersByChannel = {};` inside `rebuildFromSnapshot` (line 274):

```ts
            state.flairByBattleTag = {};
```

- [ ] **Step 5: Record own flair from the snapshot**

In the same file, replace the `ownProfile` assignment (line 316):

```ts
            state.ownProfile = snapshot.ownProfile;
            if (snapshot.ownProfile?.flair) {
                state.flairByBattleTag[snapshot.ownProfile.battleTag.toLowerCase()] = snapshot.ownProfile.flair;
            }
```

- [ ] **Step 6: Record flair from the focus roster**

Replace the `addFocused` action (lines 331-337):

```ts
        addFocused: action((state, payload) => {
            if (!state.focusedChannelIds.includes(payload.channelId)) {
                if (state.focusedChannelIds.length >= CHAT_LIMITS.MAX_FOCUSED_CHANNELS) return;
                state.focusedChannelIds.push(payload.channelId);
            }
            state.viewersByChannel[payload.channelId] = payload.viewers;
            for (const viewer of payload.viewers) {
                if (viewer.profile) state.flairByBattleTag[viewer.battleTag.toLowerCase()] = viewer.profile;
            }
        }),
```

- [ ] **Step 7: Record flair from join deltas**

Replace the `ingestViewersChanged` action (lines 347-359):

```ts
        ingestViewersChanged: action((state, payload) => {
            // A late batch arriving after unfocus would otherwise recreate a
            // ghost `viewersByChannel` entry for a channel nobody is viewing
            // anymore — ignore it (state-adjacent guard, race-free).
            if (!state.focusedChannelIds.includes(payload.channelId)) return;
            const map = new Map<string, IChannelViewerDto>();
            for (const viewer of state.viewersByChannel[payload.channelId] ?? []) map.set(viewer.battleTag, viewer);
            for (const viewer of payload.joined ?? []) {
                // Unconditional set (the old code skipped existing keys): a join entry is now
                // strictly richer than what the map may already hold, carrying a real display
                // name and flair rather than a battleTag-only stub.
                map.set(viewer.battleTag, viewer);
                if (viewer.profile) state.flairByBattleTag[viewer.battleTag.toLowerCase()] = viewer.profile;
            }
            for (const battleTag of payload.left ?? []) map.delete(battleTag);
            state.viewersByChannel[payload.channelId] = [...map.values()];
        }),
```

- [ ] **Step 8: Type-check**

```bash
cd "C:/Users/Marco/git_projects/w3champions/.worktrees/launcher-e-roster-flair"
npm run type-check
```

Expected: no output, exit 0. If `ChatUserListPanel.tsx` errors here, leave it — Task 5 fixes it.

- [ ] **Step 9: Commit**

```bash
cd "C:/Users/Marco/git_projects/w3champions/.worktrees/launcher-e-roster-flair"
git add src/types/chat-protocol.types.ts src/models/chat-core.ts
git commit -m "feat(chat): store roster flair in a user-keyed store slice"
```

---

### Task 5: `ChatUserListPanel` reads the store slice

**Files:**
- Modify: `.worktrees/launcher-e-roster-flair/src/components/chat/ChatUserListPanel.tsx` (lines 24-38, 59-62)
- Modify: `.worktrees/launcher-e-roster-flair/src/helpers/chat-ui.helper.ts` (doc comment on `viewerToChatUser`, lines 474-482)

**Interfaces:**
- Consumes: `chatStore.flairByBattleTag` from Task 4.
- Produces: nothing consumed by later tasks.

`viewerToChatUser`'s signature and body are unchanged — only its stale doc comment is corrected. The panel keeps passing flair in; it just gets it from a live store instead of scavenging messages.

- [ ] **Step 1: Replace the scavenging memo with a store read**

In `src/components/chat/ChatUserListPanel.tsx`, delete the entire block from the `// Roster DTOs carry no flair` comment through the closing of the `flairByBattleTag` memo (lines 24-38) and replace it with:

```ts
    // Roster entries carry their own flair (`IChannelViewerDto.profile`), recorded into
    // `flairByBattleTag` by the store's live-source writers. This replaced an earlier workaround
    // that scavenged flair out of cached messages, which left any user absent from the 50-message
    // seed rendering the default avatar until they happened to speak.
    const flairByBattleTag = useStoreState(x => x.chatStore.flairByBattleTag);
```

- [ ] **Step 2: Read flair by lowercased key**

In the same file, replace the `users` memo (lines 59-62):

```ts
    const users = useMemo(
        () => viewers.map(v => viewerToChatUser(v, flairByBattleTag[v.battleTag.toLowerCase()])).sort(compareUsers),
        [viewers, ownBattleTag, flairByBattleTag],
    );
```

- [ ] **Step 3: Remove the now-unused messages selector and imports**

The `messages` selector (line 30) exists only to feed the deleted memo. Delete this line:

```ts
    const messages = useStoreState(x => (activeChannelId ? x.chatStore.messagesByChannel[activeChannelId] : undefined) ?? EMPTY_MESSAGES);
```

`EMPTY_MESSAGES` and `IChatProfileDto` are each referenced **only** inside the block deleted in Step 1 and the line deleted above (verified by grep), so both imports are now unconditionally dead. Change the helper import on line 9 to drop `EMPTY_MESSAGES`:

```ts
import { battleTagsEqual, EMPTY_VIEWERS, viewerToChatUser } from "@/helpers/chat-ui.helper";
```

and delete line 12 entirely:

```ts
import { IChatProfileDto } from "@/types/chat-protocol.types";
```

Keep the `ownProfile` selector — `ownBattleTag` still uses it for the self-pinning sort.

- [ ] **Step 4: Correct the stale helper doc comment**

In `src/helpers/chat-ui.helper.ts`, in the doc comment above `viewerToChatUser`, replace the sentence fragment referring to scavenging:

```
 * `flair`, when the caller can supply it from a source that DOES
 * carry it (the viewer's own `ownProfile.flair`, or a cached message sender's
 * `flair`, see `ChatUserListPanel`), restores the real avatar/color/icons the
 * same way `messageDtoToChatMessage` does for a message row. `undefined` flair
 * degrades to today's default-avatar/no-color behavior.
```

with:

```
 * `flair` comes from the store's `flairByBattleTag` slice, fed by the roster's
 * own `IChannelViewerDto.profile` and by `ownProfile.flair`. `undefined` flair
 * degrades to the default-avatar/no-color behavior — reachable only when the
 * server could not resolve a viewer's live session at roster-build time.
```

- [ ] **Step 5: Verify**

```bash
cd "C:/Users/Marco/git_projects/w3champions/.worktrees/launcher-e-roster-flair"
npm run type-check
```

Expected: no output, exit 0.

```bash
cd "C:/Users/Marco/git_projects/w3champions/.worktrees/launcher-e-roster-flair"
npm run lint:prod
```

Expected: no output, exit 0. An "unused variable" error here means a leftover import from Step 3.

```bash
cd "C:/Users/Marco/git_projects/w3champions/.worktrees/launcher-e-roster-flair"
npm run dprint
```

Expected: no output, exit 0. Run `npm run dprint:fix` if it reports formatting differences.

- [ ] **Step 6: Commit**

```bash
cd "C:/Users/Marco/git_projects/w3champions/.worktrees/launcher-e-roster-flair"
git add src/components/chat/ChatUserListPanel.tsx src/helpers/chat-ui.helper.ts
git commit -m "fix(chat): render roster avatars from server flair, not cached messages"
```

---

### Task 6: End-to-end verification

**Files:** none modified.

**Interfaces:**
- Consumes: everything from Tasks 1-5.
- Produces: nothing.

This task exists because every preceding task verified one side of a wire contract in isolation. Nothing so far has run a real client against a real server.

- [ ] **Step 1: Full chat-service suite**

```bash
cd "C:/Users/Marco/git_projects/w3champions/.worktrees/chat-service-roster-flair"
dotnet test --nologo
```

Expected: `Failed: 0, Passed: 1325`

- [ ] **Step 2: Full launcher-e verification set**

```bash
cd "C:/Users/Marco/git_projects/w3champions/.worktrees/launcher-e-roster-flair"
npm run type-check
```

```bash
cd "C:/Users/Marco/git_projects/w3champions/.worktrees/launcher-e-roster-flair"
npm run lint:prod
```

```bash
cd "C:/Users/Marco/git_projects/w3champions/.worktrees/launcher-e-roster-flair"
npm run dprint
```

```bash
cd "C:/Users/Marco/git_projects/w3champions/.worktrees/launcher-e-roster-flair"
npm run check:i18n
```

Expected: all four exit 0.

- [ ] **Step 3: Confirm no DB access crept onto the roster path**

```bash
cd "C:/Users/Marco/git_projects/w3champions/.worktrees/chat-service-roster-flair"
grep -n "Repository\|Mongo\|await" W3ChampionsChatService/Chats/ViewerResolver.cs
```

Expected: no matches. Any hit means the resolver acquired an I/O dependency and the hot-path guarantee in the spec is broken.

- [ ] **Step 4: Manual smoke test**

Run the launcher against the chat-service build and confirm all four:

1. Connect with the Lounge focused while another user is online and has **not** sent a message in the last 50 messages. That user shows their real avatar immediately, not the sheep.
2. Colour and chat icons render for roster users, not only avatars.
3. A user who joins the channel while you are already focused appears with their real avatar within the 5-second `ViewersChanged` flush window.
4. Message-row avatars are unchanged — they still come from `sender.flair`.

- [ ] **Step 5: Report**

Report the suite counts, the four verification exit codes, and the outcome of each manual check. Do not claim completion without the manual results — no automated test in this plan exercises a real client against a real server.

---

## Notes for Plan B

Plan B (live propagation) builds directly on this work and will need:

- `ViewerResolver` — reuse it to build the `FlairChangedDto` payload so the push and the roster agree.
- `flairByBattleTag` — the `FlairChanged` handler is a one-line write into this slice, which is why it is user-keyed rather than nested under `viewersByChannel`.
- The `FreshFromWb == false` no-op rule in spec §5 — the single most important behaviour in Plan B, and the one place the feature could regress production.
