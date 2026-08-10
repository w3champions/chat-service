# Post-Game Chat Persistence + System Messages — Design

**Date:** 2026-08-10
**Repos touched:** `chat-service` (primary), `matchmaking-service`, `launcher-e`
**Status:** Approved, ready for implementation planning

---

## 1. Problem

Players close the post-match score screen quickly and lose the post-game chat. Messages sent by
other players *after* the screen is closed are never seen, because the match chat has no surface
anywhere else in the launcher.

### What is actually broken (and what is not)

The channel itself is fine. A per-match room already exists as `ChannelType.System` +
`SystemChannelKind.Match`, keyed by `SystemRef` = the matchmaking match id, created by
matchmaking-service and explicitly designed to outlive the game
(`Internal/MatchChannelService.cs:166-168`). It rides the `SessionState` snapshot in full
(`Protocol/SessionStateAssembler.cs:206-241`, rule (a) keeps every non-`Dm` channel), stays in
`chatStore.channels`, and `orderJoinedChannels` pins it *first* in the `/chat` channel switcher
(`launcher-e/src/helpers/chat-ui.helper.ts:256-262`). Its unread badge already works.

Three concrete gaps produce the reported symptom:

1. **No surface outside `/chat`.** `ChatChannel` — the only component rendering the channel list and
   its aggregate badge — is mounted only in the non-compact `ChatPanel`
   (`ChatPanel.tsx:346`: `{!compact && <ChatChannel …/>}`), and `/chat` is the only route that
   renders one. `MiniChat` is `<ChatPanel compact />` and suppresses the switcher entirely. The
   always-visible taskbar badge counts *mention-inbox* entries only
   (`TaskbarSection.tsx:91-95`). `totalUnread` exists in the store (`chat-core.ts:231`) and is read
   by nothing.

2. **No notification, by design.** `routeChatNotification` is hard-gated to
   `"mention" | "dmActivity" | "expandedDmMessage"` with a pinned guardrail comment
   (`chat-notification.helper.ts:39, 93`). Both call sites bail before reaching it for a System
   channel: `ingestChannelActivity` early-returns on `!activity.preview`
   (`chat-messages.ts:508-515`) — and the server never attaches a preview for non-DM activity — while
   `MessageReceived` early-returns on `!isDmLike` (`chat-messages.ts:576`). A plain post-game message
   fires **zero** toast, sound and OS notification.

3. **The DM bar is unmounted while the score screen is up.** `ScoreScreen` replaces the entire
   `mainContent` branch that contains `<DmBar />` (`App.tsx:190-216`), so the only live chat surfaces
   during the score screen are its own embedded `MatchChatPanel` and the app-global
   `ChatNotificationToast`.

### The flooding concern does not apply

`AddMemberWithInvariant` (`Internal/MatchChannelService.cs:205-231`) guarantees a user holds **at
most one** `System/Match` membership. Starting the next match evicts the previous room with a
`ChannelRemoved` push, strictly before the new `ChannelAdded`, and the eviction deliberately ignores
`Detached` (`Channels/ChatChannel.cs:84-91`). Pinned by
`MatchChannelServiceTests.cs:194, 229, 520`. Both the create path (`:171-177`) and the
roster-assertion path (`:390-392`) go through it; no production path adds a Match membership without
it. Semi-ephemeral rooms therefore cannot accumulate, and cannot crowd out real conversations.

---

## 2. Decisions

| # | Decision | Rationale |
|---|---|---|
| D1 | Surface the room as a **dedicated match pill on the DM bar**, never as a row in the Conversations list | One pill by server invariant; keeps `classifyDmChannel` and the DM tray uncontaminated |
| D2 | Pill appears **after every game**, self-removes after **10 min idle** | Discoverable; a dead room does not linger |
| D3 | Intro is a **first-class system message** in chat-service, not a client-rendered banner | Reusable capability; survives reconnect; visible to any future client and to moderation |
| D4 | System content is **structured key + params + server-rendered English `fallbackText`** | Launcher ships 13 locales with a `check:i18n` gate; a stored English string is untranslatable forever |
| D5 | Notifications: **one nudge, then badge-only** | Solves "I missed the gg" without letting a post-game argument spam four toasts |
| D6 | **Retention unchanged** (24h from channel creation, no extension on post) | Explicit call; the client gains a dead-channel path, which it needs regardless |
| D7 | Website is **out of scope** | It has no chat client of any kind — no SignalR dependency, no store, no unread concept |

### Sequencing

Three stages, each independently shippable and independently useful:

1. **chat-service** — system-message capability (§3.1–3.5) and the activity preview (§3.6). Ships
   dark; nothing consumes it yet.
2. **matchmaking-service** — channel name fix and intro publishing (§4). The intro immediately shows
   up in the existing score-screen `MatchChatPanel` and in the `/chat` dropdown, with no launcher
   change.
3. **launcher-e** — pill, notification routing, system-message rendering, dead-channel path (§5).

Stage 3 depends on 1 for the preview payload and renders best after 2, but stages 1 and 2 deliver
visible value on their own.

---

## 3. chat-service — first-class system messages

### 3.1 Model

There is no system-message concept today. `MessageRepository.Insert` has exactly one caller —
`ChatHub.Messaging.cs:311`, inside `SendMessage` — which requires a live `ChatSession` and builds a
mandatory `MessageSender` snapshot from the connection (`BuildSenderSnapshot`,
`ChatHub.Messaging.cs:621`).

`Messages/ChannelMessage.cs` gains an explicit author discriminator rather than a nullable flag hung
off the existing sender:

```csharp
[BsonRepresentation(BsonType.String)]
public MessageKind Kind { get; set; } = MessageKind.User;   // User | System

public MessageSender Sender { get; set; }        // null iff Kind == System
public string Content { get; set; }              // null iff Kind == System

[BsonIgnoreIfNull] public SystemMessageBody System { get; set; }  // non-null iff Kind == System
[BsonIgnoreIfNull] public string DedupeKey { get; set; }          // System only
```

```csharp
public class SystemMessageBody
{
    public string Key { get; set; }                                   // "match_intro"
    public Dictionary<string, string> Params { get; set; }            // { map: "Amazonia" }
    public Dictionary<string, List<string>> ListParams { get; set; }  // { players: [...] }
    public string FallbackText { get; set; }                          // server-rendered English
}
```

Two param dictionaries rather than one `object` bag: both round-trip through BSON and
`System.Text.Json` with no custom converters, and give TypeScript a clean discriminated shape.
`Kind` defaults to `User`, so existing documents deserialize unchanged with no migration.

### 3.2 Publisher

`Messages/SystemMessagePublisher.cs` implementing `ISystemMessagePublisher`:

```csharp
Task<SystemMessagePublishResult> PublishAsync(
    string channelId, SystemMessageBody body, string dedupeKey, CancellationToken ct);
```

Ordered stages — deliberately *not* `SendMessage`'s pipeline:

1. Load channel → `NotFound` if absent.
2. If `dedupeKey != null` and a message with `(ChannelId, DedupeKey)` exists → return `Ok` with the
   existing id. **Idempotency is mandatory**: matchmaking-service retries on timeout, and without
   this the intro double-posts.
3. `ChannelRepository.AllocateSeq(channelId, now, shellExpiresAt: null)` — non-negotiable; TTL,
   seq-anchored paging and `LastMessageAt` all key off it. `null` per D6.
4. `MessageRepository.Insert` with `Kind = System`,
   `ExpiresAt = ExpiryCalculator.ForChannelMessage(channel.Type, now)` (30d, unchanged).
5. `FanOutEngine.OnMessagePersisted(message, senderConnectionId: null)`.

Skipped entirely: session lookup, `MessageRateLimiter`, mute gate, mention extraction and
`MentionFanOut`. Durable write precedes the push, per the repo's existing discipline.

Index: unique partial on `(ChannelId, DedupeKey)` where `DedupeKey` exists, added to
`Domain/ChatDomainIndexes.EnsureAllAsync`.

### 3.3 Exposure

Two entry points:

- **In-process** `ISystemMessagePublisher`, for chat-service's own future events.
- **`POST /internal/channels/{ref}/system-message`** on the existing HMAC realm
  (`[InternalHmacAuth(InternalCaller.Mm)]`, `Internal/InternalChannelsController.cs`). Resolves
  `ref` → channel by **lookup only** — never create-on-demand; a missing ref is `NotFound`.

Request body:

```json
{
  "key": "match_intro",
  "params": { "map": "Amazonia" },
  "listParams": { "players": ["Grubby#2136", "Happy#2233"] },
  "fallbackText": "Match on Amazonia — Grubby#2136, Happy#2233",
  "dedupeKey": "match_intro"
}
```

### 3.4 Projection

`MessageDto` gains `kind` and `system`; `ForUserDelivery` passes both through (it currently forces
`Deleted`/`Shadow` false — system messages follow the same rules). Moderator projections
(`LoadPageBeforeForModerator`, `LoadPageAroundForModerator`) include them so
`GET /api/moderation/channels/{channelId}/messages` renders `fallbackText`.

### 3.5 Moderation semantics — decided, not discovered

- `PurgeMessagesFromUser(battleTag)` filters on `Sender.BattleTag`. System messages have no sender,
  so Mongo's missing-field semantics exclude them naturally. **Verify no null-deref** in the
  projection path rather than assuming.
- `DeleteMessage(messageId)` on a system message → `PermissionDenied`.
- `MessageRepository.Insert` gains its second-ever caller. `OldProtocolRemovedTests`
  (the reflection guard asserting moderation never hard-deletes) and `StartupDependencyInjectionTests`
  must be updated **deliberately** — this repo treats those as contracts, not incidental tests.

### 3.6 Activity preview for match channels

`ActivityCoalescer` attaches a `preview` only for DM activity today, which is precisely why nothing
can toast. Extend it to attach `{ senderName, excerpt }` for `System` + `SystemChannelKind.Match`
channels. Public/SemiPublic/Clan activity keeps its existing badge-only, preview-free treatment.

---

## 4. matchmaking-service — publishing the intro

### 4.1 Channel name fix

`game-creation.flow.ts:245-249` passes `this.match.gamename || this.match.mapName || this.match._id`.
`gamename` is machine-shaped (`w3c-{gateway}-{nanoid}`, `matches.repo.ts:131-137`), so every ladder
room is currently named `w3c-20-V1StGXR8_Z`. Flip the precedence to
`mapName || gamename || _id`.

**Note:** this also renames *live lobby* chat, not just the post-game room. Intended, but it is a
user-visible change beyond the stated scope.

### 4.2 Publish on detach — one rule, both paths

The intro is published at the moment the room freezes, because that is the first moment map and
roster are final:

- **Ladder** — immediately after `notifyLadderMatchLoaded` (`game-creation.flow.ts:245`). The channel
  is already created with `detached: true`, and roster and map are fixed at creation, so detach and
  creation coincide.
- **Custom games** — inside `notifyLobbyDetached` at `GAME_STARTED`
  (`custom-game.flow.ts:1769`). The lobby's map and roster churn while it is open
  (`ApplyRosterAssertion` on every join/leave/kick), so creation-time would be stale.

Params: `map` = `match.mapName`, `players` = the same human battleTag list already sent as `members`.
`fallbackText` is rendered in mm, which owns the data. `dedupeKey: "match_intro"`.

Both go through the existing `matchChatService` façade — returns `void`, never throws, no-ops when
`CHAT_INTERNAL_API_URL`/`CHAT_INTERNAL_API_SECRET` are unset (`chat-config.ts:52-61`).

This deliberately does **not** touch `finishMatch`, so the pinned
`match-chat-failsoft.test.ts:861-905` row *"match finished = NOTHING"* stays green.

---

## 5. launcher-e — the match pill

### 5.1 State lives outside `dmWindows`

Pill state is **not** pushed into `chatStore.dmWindows`. That array is read by `classifyDmChannel`,
`DmListPanel` sectioning, `deriveInitialConversationsCursor` (`chat-ui.helper.ts:508`) and the DM
launcher badge (`DmBar.tsx:64-73`); injecting a System channel ripples into all four and recreates
exactly the Conversations-list contamination D1 avoids.

`chat-match.ts` (already owns match-embed concerns, 125 lines) gains:

```ts
matchPill?: {
    channelId: string;
    expanded: boolean;
    nudged: boolean;        // toast already fired for this channel
    lastActivityAt: number; // drives the idle fade
}
```

`ingestChannelRemoved` (`chat-core.ts:367-383`) is the single choke point that already clears
`dmWindows`, `dmExpandOrder` and `lastDmPreviewByChannel` on eviction. Clearing `matchPill` there
gets the next-match swap for free.

### 5.2 Rendering and expansion

`DmBar` renders the pill in its own slot, left of the DM chips. Its label is
`channelDisplayName(channel)` — the map name, once §4.1 lands — plus the unread badge and a dismiss
`x`. Expanded, it is `MatchWindowHeader` + the **existing `DmWindowBody`** (message list + composer).
`DmWindowHeader` is not reused — counterpart resolution, presence dot and `GroupManageMenu` are all
DM-specific.

Dismissing with `x` clears `matchPill` for that channel; a later message re-creates the pill but does
not re-nudge (`nudged` is keyed to the channel, not the pill instance).

Expand/collapse call `bindMatchChannelEmbed` / `releaseMatchChannelEmbed` verbatim
(`chat-match.ts:61-123`). Those already free a slot against the
`CHAT_LIMITS.MAX_FOCUSED_CHANNELS = 10` budget by collapsing the LRU DM window, seed 50 messages via
`getMessages`, and honour invariant R1 — **never touch `activeChannelId`**. No new focus logic is
written.

*Implementation check:* confirm `DmWindowBody` takes `channelId` and does no DM-specific counterpart
resolution internally; if it does, extract the message-list/composer core rather than branching it.

### 5.3 Trigger and idle fade

Created by a thunk dispatched from `leaveScoreScreen()` and the Escape handler
(`ScoreScreen.tsx:199-214`) — the only two exit paths. The client cannot infer post-game state from
the server, because `ChatChannel.Detached` is `[JsonIgnore]`; the local signal avoids exposing it.
A game that never reaches the score screen simply gets no pill.

The channel is resolved with the **same** helper `ScoreScreen` already uses — `findMatchChannelEntry(channels, currentMatch?._id ?? lastMatch?._id ?? customGameLobbyData?.id)`
(`ScoreScreen.tsx:48-53`, `chat-ui.helper.ts:230-242`) — so the pill and the score-screen embed can
never disagree about which room is "this match". A `undefined` result means no pill, which is the
existing designed quiet state, not an error.

Idle fade at 10 min, armed in a `useMatchPillIdleFade` hook inside `DmBar` and re-armed on
`lastActivityAt`. Timers stay out of the store. A message arriving after the fade brings the pill
back but does **not** re-nudge.

`ScoreScreen` itself is unchanged — it already embeds the full `MatchChatPanel`, and `DmBar` stays
unmounted there.

### 5.4 Notification routing

`ChatNotificationKind` gains `"matchActivity"`, and the pinned *"mentions + DMs only — no other
source is ever routed"* guardrail at `chat-notification.helper.ts:39, 93` is amended deliberately.

```ts
case "matchActivity":
    return {
        toast: !ctx.alreadyNudged,
        sound: !ctx.alreadyNudged && ctx.isWindowFocused && !ctx.disableChatSounds ? "message" : undefined,
        osNotify: !ctx.alreadyNudged && !ctx.isWindowFocused && !ctx.disableChatNotifications,
    };
```

Existing top gates still apply first: `isAuthorBlocked || isMatchInProgress → NOTHING`, plus
`isChannelExpanded → NOTHING`. `alreadyNudged` reads `matchPill.nudged`, set on first toast and reset
when a pill is created for a new channel.

`ingestChannelActivity` (`chat-messages.ts:508`) branches on match-vs-DM instead of early-returning
on a missing preview. The two delivery paths are naturally exclusive: a collapsed pill is unfocused
⇒ `ChannelActivity` ⇒ nudge path; an expanded pill is focused ⇒ `MessageReceived` ⇒ no toast, only an
idle-timer reset.

### 5.5 Rendering the intro

`ChatMessage` gains a system branch. **`npm run check:i18n` scans source for `t("...")` literals**, so
`t("chat_system_" + key)` would be invisible to it. An explicit registry is required:

```ts
const SYSTEM_MESSAGE_RENDERERS = {
    match_intro: (p, t) => t("Match on {{map}} — {{players}}", {
        map: p.params.map,
        players: p.listParams.players.join(", "),
    }),
};
```

An unknown key renders `fallbackText`. That is what makes forward-compat real: chat-service can add
system messages an older launcher has never heard of.

### 5.6 Dead-channel path — load-bearing

Given D6, **TTL expiry is silent**: Mongo drops the channel doc and no `ChannelRemoved` is pushed.
`focusChannel`, `getMessages` and `sendMessage` returning `NotFound` must all converge on: clear
`matchPill`, surface a transient notice through the existing `dmBarNotice` mechanism
("This post-game chat has expired."), auto-clearing after 5s. Plus the belt-and-braces
`channels[matchPill.channelId]` render guard that `DmBar.tsx:58-59` already applies to chips.

Note the 24h is anchored to channel *creation*, not match end, and posting does not extend it
(`AllocateSeq` passes `shellExpiresAt` only for Dm/GroupDm, `ChatHub.Messaging.cs:279-281`). A custom
lobby open for 20h therefore loses its post-game chat 4h later. Accepted under D6; the client path
above is what makes it survivable.

### 5.7 Badge double-count

`ChatChannel.tsx:115-125` excludes only `isDmLike` channels from its aggregate, so match unread
already feeds the `/chat` badge. Exclude the pill's channel while the pill is visible. The channel
**stays** in the `/chat` dropdown — still pinned first by `orderJoinedChannels` — as the fallback
surface after the pill fades.

---

## 6. Testing

**chat-service** — NUnit 3 + Testcontainers (`mongo:7.0`, Docker daemon required), `dotnet test`.
CI enforces `dotnet format --verify-no-changes`.

- `SystemMessagePublisher`: inserts with `Kind = System`; allocates a seq and advances
  `LastMessageAt`; skips rate limiter, mute gate and mention fan-out; fans out to focused members.
- Idempotency: publishing twice with the same `dedupeKey` yields one message and returns the
  existing id.
- `NotFound` for an unknown ref; the internal endpoint never creates a channel.
- Moderation: `PurgeMessagesFromUser` leaves system messages intact; `DeleteMessage` on one returns
  `PermissionDenied`.
- Protocol: `MessageDto.kind`/`system` present in user and moderator projections; a `Kind`-less
  legacy document deserializes as `User`.
- `ActivityCoalescer` attaches a preview for System/Match and still omits it for
  Public/SemiPublic/Clan.
- Update `OldProtocolRemovedTests` and `StartupDependencyInjectionTests` for the new insert caller
  and DI registration.

**matchmaking-service** — full `npm test` (isolated flow files flake).

- Intro published once on ladder match-loaded and once on custom `GAME_STARTED`, with the correct
  map and roster.
- `finishMatch` still touches nothing chat-related — the existing failsoft row-table stays green.
- Fire-and-forget contract holds: a chat-service outage never fails game creation.
- Channel name resolves to `mapName` first.

**launcher-e** — no test runner. Verify with `npm run type-check`, `npm run lint`,
`npm run dprint`, `npm run check:i18n`, plus manual passes: pill appears after a game, idle-fades at
10 min, returns without re-nudging on a late message, swaps on the next match, and degrades cleanly
when the channel is gone.

---

## 7. Out of scope

- **Website.** No chat client exists — no SignalR dependency, no store, no unread or toast
  infrastructure. Surfacing match chat there is a separate project.
- **Retention changes.** D6 keeps 24h-from-creation with no extension on post. Re-anchoring to detach
  time remains available as a follow-up.
- **Non-EN locales.** English keys land with the feature; the other 12 go to a follow-up task via the
  `translation-management` skill, per repo convention.
- **In-game overlay.** No chat overlay exists, and notifications stay suppressed during a match
  (`isMatchInProgress`) with no retroactive flush.

---

## 8. Known risks

| Risk | Mitigation |
|---|---|
| `AddMemberWithInvariant` eviction is best-effort, not transactional — concurrent adds can transiently leave two match memberships (`MatchChannelService.cs:31-51`) | Pill keys off a single `channelId`; a second membership shows only in the `/chat` dropdown and self-heals on the next add |
| Amending the "mentions + DMs only" guardrail could invite future notification creep | The amendment names `matchActivity` explicitly and keeps the union closed |
| Renaming ladder channels to `mapName` changes live lobby chat too | Called out in §4.1; confirm before merge |
| `DmWindowBody` may carry DM-specific assumptions | §5.2 verification step; extract the core rather than branching if so |
