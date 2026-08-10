# Roster Flair Enrichment + Live Propagation — Design

**Date:** 2026-08-09
**Repos touched:** `chat-service` (primary), `website-backend`, `launcher-e`
**Status:** Approved, ready for implementation planning

---

## 1. Problem

After the chat revamp shipped to production, users appear in the chat user list with the default
sheep avatar instead of their real profile picture. The avatar corrects itself once that user types
a message.

### Root cause

The roster wire contract carries no flair. `ChannelViewerDto` is a 2-tuple
(`Protocol/ChannelViewerDto.cs:7`):

```csharp
public record ChannelViewerDto(string BattleTag, string Name);
```

`ViewersChangedDto` (`Protocol/ViewersChangedDto.cs:21-24`) likewise carries only bare battleTag
strings, and `PresenceChangedDto` / `FriendPresenceChangedDto` carry only `(battleTag, online)`.

The **only** wire surfaces that carry another user's flair are the per-message
`MessageSender.Flair` snapshot and — for autocomplete only — `MentionCandidateDto.Profile`.

The client compensates in `ChatUserListPanel.tsx:32-38` by scavenging flair out of cached chat
messages:

```ts
const flairByBattleTag = useMemo(() => {
    const map = new Map<string, IChatProfileDto>();
    for (const message of messages) {
        if (message.sender.flair) map.set(message.sender.battleTag.toLowerCase(), message.sender.flair);
    }
    if (ownProfile) map.set(ownProfile.battleTag.toLowerCase(), ownProfile.flair);
    return map;
}, [messages, ownProfile]);
```

A miss falls through to `DEFAULT_CHAT_PROFILE_PICTURE` (`chat-ui.helper.ts:13-17`) — `STARTER` /
`pictureId: 1`, which is literally the sheep asset (`website/public/assets/raceAvatars/STARTER_1.jpg`).

This produces two symptoms:

1. **Transient, on connect.** In `applySessionState`, `FocusChannel` populates `viewersByChannel`
   and `setActiveChannel(active)` fires at `chat-core.ts:689` — *before* the
   `await chatService.getMessages(...)` at `chat-core.ts:704`. The panel renders a full roster
   against an empty flair map.
2. **Permanent, and the reported symptom.** The history seed is only
   `REBUILD_ACTIVE_SEED_LIMIT = 50` messages (`models/chat.ts:14`). Any online user absent from that
   window has no flair source anywhere in the protocol and stays a sheep until they speak.

The gap was known. `ChatUserListPanel.tsx:40-47` says *"until chat-service's roster DTO enrichment
ships"*; commit `8446d527` added the scavenging workaround and the server-side enrichment it was
waiting on never landed.

### Ruled out by production evidence (2026-08-09, 46 min post-release)

| Hypothesis | Evidence |
|---|---|
| wb rate limiting / timeout | `Failed to enrich chat user` = **0**; `Restoring cached directory flair` = **0**; `TaskCanceled\|timed out\|HttpRequestException` = **0**. Warning-level logging demonstrably active in the same window (`Receiver ... failed to authenticate` present), so the zero is meaningful. |
| Endpoint latency | `/api/players/{tag}/clan-and-picture` measured at **42–75 ms** against a 2000 ms client timeout (`WebsiteBackendRepository.cs:37`). |
| Rate limiting configured | None. The repo's `[RateLimit]` attribute is applied only to `ReplaysController`. |
| Misconfiguration | `STATISTIC_SERVICE_URI=https://statistic-service.w3champions.com` — correct. |
| First-launch only | No. `rebuildFromSnapshot` wipes `messagesByChannel` on every connect *and* reconnect (`chat-core.ts:271-274`); nothing chat-related is persisted. |

Separately observed, out of scope: `ProfilePicture.Default()`
(`website-backend`, `PersonalSettings/ProfilePicture.cs:8-16`) calls `new Random()` per invocation,
so settings-less players get a different avatar on every call (measured `pictureId` 1, 2, 3, 1 across
four consecutive requests), and `random.Next(1, 5)` never serves `STARTER_5.jpg`.

---

## 2. Goals / Non-goals

**Goals**

- A user in a channel roster renders with their real flair immediately on connect, without having
  spoken.
- A flair change (portrait, chat colour, chat icons, clan) propagates to other viewers live, without
  requiring the changed user to reconnect.
- A flair change is reflected in the changed user's own subsequent messages within the same
  connection.
- No new persistent storage, and no additional load on `website-backend` for the roster path.

**Non-goals**

- Retroactive repair of already-rendered history. `ChannelMessage.Sender.Flair` remains an immutable
  send-time snapshot; the message list keeps reading `dto.sender.flair` per message.
- Reducing the existing per-message flair storage cost (see §6).
- Flair for users who are not in a roster (offline, or online but not focused on the channel).
- Fixing the `ProfilePicture.Default()` randomness noted above.

---

## 3. Architecture

Four independent pieces. Each degrades to current behaviour on its own, so they are separately
deployable.

### 3.1 Roster enrichment (the bug fix)

`FocusChannel` already resolves each roster entry's display name through an in-memory session lookup
(`ChatHub.Channels.cs:202-206`):

```csharp
private string ResolveViewerName(string battleTag)
{
    var session = _sessionRegistry.GetByBattleTag(battleTag);
    return session?.Identity?.Name ?? battleTag;
}
```

Every roster entry is by definition online *and* focused, so its `ChatUser` — carrying full flair —
is already in `ConnectionMapping`, keyed by `session.ConnectionId`. Flair therefore rides the lookup
that is already happening:

- **No Mongo read.** No `UserDirectoryRepository` involvement on this path.
- **No website-backend call.**
- **Identical by construction** to what that user's own messages carry, because both go through
  `ChatProfileMapper.FromChatUser` off the same `ConnectionMapping` entry.

### 3.2 Live propagation — website-backend side

There are **five** flair-write paths, not one. Any design hooking only `SetProfilePicture` misses
most of them:

| Path | Location |
|---|---|
| Profile picture | `PortraitCommandHandler.UpdatePicture` (`Rewards/Portraits/PortraitCommandHandler.cs:18-28`) |
| Chat colour / icons | `PersonalSetting.Update` via `PersonalSettingsController.cs:67-80` |
| Reward grant / revoke | `ChatColorRewardModule.cs:53,105`, `ChatIconRewardModule.cs:58,112` — bypasses the controller |
| Clan join / leave / kick | `ClanCommandHandler.cs:30,50,125,156` |
| Clan delete | `ClanCommandHandler.cs:80` — **bulk, many battleTags in one call** |

Plus `PersonalSetting.UpdateSpecialPictures` (`PersonalSetting.cs:92-110`) can silently reset a
picture.

Notification therefore hangs off the **persistence boundary**, which every write path must cross,
implemented as decorators over the existing interfaces rather than as edits to the repositories:

```
FlairNotifyingPersonalSettingsRepository : IPersonalSettingsRepository
FlairNotifyingClanRepository             : IClanRepository
        │ delegates every member to the inner repository
        └─ after a successful Save / SaveMany / UpsertMemberShip / SaveMemberShips
           → IFlairChangeNotifier.NotifyChanged(battleTags)
```

`PersonalSettingsRepository` and `ClanRepository` stay pure. All five paths — including the two
reward modules and the bulk clan delete — are covered because they all persist through these
interfaces, and a sixth path added later is covered for free. `Program.cs:178,187` already resolves
both through `AddInterceptedTransient`, so decoration is consistent with how this codebase composes
services.

**Deliberate over-notification.** The decorator fires on any settings save, not only flair-relevant
ones. Fingerprinting the flair fields to suppress no-op pings was considered and rejected: it avoids
a ~50 ms call that only occurs for users currently online in chat, at the cost of real machinery and
a new way to be subtly wrong. Chat-side coalescing absorbs the volume.

`IFlairChangeNotifier` clones the `RelationshipChangeNotifier` triad 1:1 — the same
`ChatInternalApiSigner` HMAC scheme and headers, the same `ChatPingSettings` /
`CHAT_INTERNAL_API_SECRET` (already set in production alongside `CHAT_API`), the same
`Task.Run` fire-and-forget with 2 attempts and a 3 s per-attempt cap, and the same self-disable when
the secret is absent.

### 3.3 Live propagation — chat-service side

`POST /internal/profile-changes`, gated `[InternalHmacAuth(InternalCaller.Wb)]`, validated exactly
like `InternalRelationshipChangesController`. Payload `{ battleTags: [...] }`.

Requests feed a coalescer modelled on `ActivityCoalescer` / `ViewersAccumulator` so a burst for one
battleTag collapses into a single refresh. Per battleTag:

- **No live session → no-op.** Their next connect re-enriches anyway. Work is bounded by the set of
  users currently online in chat.
- **Live session →** `GetUserFromIdentity(session.Identity)`; then, **only if `FreshFromWb == true`**
  (see §5), `RegisterUser` → directory upsert → `FlairChanged` fan-out. If `FreshFromWb` is false the
  whole refresh is abandoned with no side effects.

Reusing `GetUserFromIdentity` means admin colour/icon forcing, the three-tier fallback and the
never-clobber invariant all come for free. Because it refreshes `ConnectionMapping`, it also fixes
the case where a user's own subsequent messages still carried their old flair.

### 3.4 Reconnect backstop

Unchanged: connect always re-enriches from wb. Every layer above is therefore free to be
best-effort — a dropped ping degrades to today's behaviour, never to a permanent wrong state.

---

## 4. Wire contract

All three changes are shape-changing. This is acceptable because the launcher force-updates before
it can connect.

| DTO | Change |
|---|---|
| `ChannelViewerDto` | `(BattleTag, Name)` → `(BattleTag, Name, ChatProfile Profile)` |
| `ViewersChangedDto` | `Joined: IReadOnlyList<string>` → `IReadOnlyList<ChannelViewerDto>`; `Left` unchanged (`string[]`) |
| *new* `FlairChangedDto` | `(string BattleTag, ChatProfile Profile)` on a new `FlairChanged` event |

`ViewersAccumulator` computes its `joined` set at flush time from `FocusRegistry` and currently has
no way to resolve flair — it is constructed with `(IHubContext<ChatHub>, FocusRegistry)`
(`FanOut/ViewersAccumulator.cs:45`). Enriching `Joined` therefore requires injecting `ISessionRegistry`
and `ConnectionMapping` into it, using the same lookup as `ResolveViewerName`. Resolution happens at
flush, outside the lock, consistent with the component's existing send discipline.

**Single shared type.** The roster reuses the full `ChatProfile` rather than a trimmed roster type.
The client renders only four of its fields today, but `viewerToChatUser(viewer, flair?: IChatProfileDto)`
already accepts exactly this type, and one shared `ChatProfileMapper.FromChatUser` for both roster
and `sender.flair` is what guarantees the two cannot drift — the codebase already documents that
property as load-bearing.

**Null omission.** The SignalR hub JSON protocol is configured with
`DefaultIgnoreCondition = WhenWritingNull`. `Startup.cs:41-53` currently sets no null handling, so
all 13 flair keys serialize even when null. Most users have null `clanId`, `chatColor`, `chatIcons`
and null rank, and the measured *minimum* flair was still 281 chars — the key names dominate.
Omitting nulls drops a typical flair from ~291 to roughly 110 chars, and shrinks `sender.flair` on
every message and history page for free with no API change. Safe client-side: the TypeScript DTOs
already declare these fields optional (`clanId?: string | null`, `chat-protocol.types.ts:130-138`)
and every read path uses optional chaining with a fallback.

**`FlairChanged` fan-out targeting.** Flair is user-scoped, so the event is not channel-scoped. For a
changed battleTag X with a live session: take `GetFocusedChannels(X.ConnectionId)`, union their
`GetFocusedConnections`, dedupe, and send once per connection — plus X's own connection
unconditionally, so a user focused on nothing still sees their own avatar update.

### Client state

New `chatStore.flairByBattleTag: Record<string, IChatProfileDto>`, keyed on lowercased battleTag
(matching the server's roster tag casing and the current memo's normalization). Wiped by
`rebuildFromSnapshot` alongside all other chat state.

Written by **live sources only** — `FocusChannel` viewers, `ViewersChanged.joined`,
`SessionState.ownProfile`, and `FlairChanged`. Deliberately **not** by message senders:
`sender.flair` is frozen at send time, so a three-day-old history message could otherwise clobber
fresh flair. It is also unnecessary, since every roster user now arrives with live flair and the
message list reads `dto.sender.flair` per message regardless. This yields one precedence rule
instead of a source-ranking scheme.

`ChatUserListPanel` drops its `flairByBattleTag` `useMemo` entirely and reads the store slice, which
also retires the per-message O(buffer × roster) rebuild.

### Data flow

```
player changes portrait
  → PortraitCommandHandler → IPersonalSettingsRepository.Save
      → [decorator] IFlairChangeNotifier.NotifyChanged(["Foo#123"])
          → POST /internal/profile-changes   (HMAC, fire-and-forget, 2 attempts)
              → coalescer (per-battleTag, collapses bursts)
                  → no live session → no-op        (next connect re-enriches)
                  → live session    → GetUserFromIdentity
                                    → RegisterUser
                                    → directory Upsert (iff FreshFromWb)
                                    → FlairChanged → focused connections
                                         → client: flairByBattleTag[tag] = profile
                                         → user list re-renders
```

---

## 5. Error handling

- **Decorator** — notification fires only after the inner write succeeds, wrapped in its own
  try/catch. A notifier fault can never fail a settings write or clan operation. If the inner write
  throws, no ping is sent.
- **Notifier** — fire-and-forget, 2 attempts, 3 s per-attempt cap, non-2xx falls through to retry
  then `Log.Warning`, never rethrows, never logs the secret. Self-disables without
  `CHAT_INTERNAL_API_SECRET`, with one startup log line.
- **Controller** — HMAC-gated; battleTags validated non-blank and control-char-free including
  U+2028/U+2029; batch capped at `InternalMaxMembersPerCall` (64); generic 400 on any violation with
  no partial processing.
- **Coalescer** — bounded pending set; drop on overflow rather than grow. A dropped refresh degrades
  to the reconnect backstop.
- **Fan-out** — sends outside the lock, fault-isolated per connection, mirroring
  `ViewersAccumulator`.
- **Null `Profile` on a roster entry** — possible when a session disappears mid-call. The client
  keeps `DEFAULT_CHAT_PROFILE_PICTURE` for exactly this case, mirroring the existing precedent where
  `ResolveViewerName` falls back to the raw battleTag rather than dropping the entry.

### The `FreshFromWb` rule

On a refresh, if `GetUserFromIdentity` returns `FreshFromWb == false`, **do nothing at all** — no
`RegisterUser`, no directory write, no `FlairChanged`.

A wb blip during a flair ping would otherwise take a degraded tier-3 profile and actively broadcast
the sheep to every viewer in the channel, converting a transient upstream hiccup into a visible
regression for everyone. This extends the existing never-clobber invariant from the directory to the
connection cache and to the wire. It is the one place this design could make things worse than
today, and it gets an explicit test.

---

## 6. Load and data-transfer analysis

Measured on production, 2026-08-09.

### Storage: no change

| `messages` collection (1000-doc sample) | |
|---|---|
| Avg message document | 593 chars |
| Avg `Sender.Flair` within it | **291 chars — 49% of the document** |
| Avg message content | **18 chars** |

Every chat message already carries a full 13-field flair blob; half of each stored message is flair
while the text averages 18 characters. This is pre-existing and **unchanged** by this design — the
message path is not touched.

Roster flair lives in memory only (`SessionRegistry` + `ConnectionMapping`).
`user_directory.Profile` already exists at 288 chars × 1041 rows = 0.4 MB. The design writes it
slightly more often but adds no field and no document. **Net new persistent storage: zero.**

### Transfer

Inputs: 789 distinct users/hour, ~600 messages/hour, largest channel 992 members (next 59, 55, 36),
`ChatProfile` 291 chars, current `{battleTag, name}` viewer ≈ 45 chars. No metrics endpoint exists
on the container, so **concurrent focused Lounge viewers is estimated at 300**, derived from 789
unique users/hour against 992 memberships. Totals below are order-of-magnitude.

| Lounge focus payload (300 viewers) | Per focus | ~800 focuses/hr |
|---|---|---|
| Today | 13.5 KB | 11 MB/hr |
| + full `ChatProfile`, nulls included | 101 KB | 81 MB/hr |
| **+ full `ChatProfile`, nulls omitted (chosen)** | **~40 KB** | **~32 MB/hr** |

`ViewersChanged` is delta-only on a 5 s batch — a handful of joins per window, negligible.
`FlairChanged` is rare (a portrait change is roughly once per session) but fans out to every focused
connection of the user's channels, so ~100 KB per event in the Lounge; the coalescer bounds bursts.

Null omission also shrinks `sender.flair` on every message and history page, partially offsetting
the pre-existing 49% overhead noted above.

---

## 7. Testing

**chat-service** (`W3ChampionsChatService.Tests`, NUnit) — failing test first:

- `FocusChannel` returns viewers whose `Profile` is populated from the live session (fails against
  today's 2-tuple).
- `ViewersChanged.Joined` carries flair.
- `FlairChanged` reaches exactly the focused connections of the changed user's channels, plus their
  own connection, deduped.
- The `FreshFromWb == false` no-op rule: no `RegisterUser`, no directory write, no emit.
- Coalescing collapses a burst for one battleTag into a single refresh.
- Controller validation and HMAC rejection. **The new internal controller must be added to the
  dynamic reflection sweep in `InternalChannelsControllerTests`** that pins HMAC gating across
  internal controllers.

**website-backend** (`WC3ChampionsStatisticService.UnitTests`, NUnit):

- The decorator notifies on `Save`, `SaveMany`, `UpsertMemberShip`, `SaveMemberShips`.
- The bulk `DeleteClan` path emits every affected battleTag, not just one.
- No notification when the inner repository throws.
- A notifier fault does not surface to the caller.
- Signature vectors are already pinned by `ChatInternalApiSignerTests`.

**launcher-e**: no test runner exists in this repo. Verification is `type-check`, `lint`, `dprint`,
`check:i18n`, plus a manual run confirming (a) a silent user shows their real avatar on connect and
(b) changing a portrait updates it live without a reconnect.

---

## 8. Rollout

Ship in this order. The wire change is breaking, not inert: `ViewersChangedDto.Joined` moves from
`string[]` to an array of objects, and chat-service has no server-side client-version gate (verified
by grep — no `minVersion`/`clientVersion`/`forceUpdate` check exists). An un-updated launcher's
`ingestViewersChanged` keys its viewer Map on an object and then throws on
`v.battleTag.toLowerCase()` in `ChatUserListPanel`, killing the user-list panel. The
`MentionInboxEntryDto.ReadAt` pin matters to old clients too. So the launcher's force-update floor
must be bumped and rolled out *before* chat-service deploys — nothing on the server does that
gating for us:

1. **launcher-e force-update floor bump** — raised past the last pre-change build, and given time to
   reach clients, before the next step.
2. **chat-service roster enrichment + null omission** — safe now that no client below the new floor
   can connect.
3. **launcher-e client** — consumes the roster flair; the reported bug is fixed at this point.
4. **website-backend notifier + chat-service internal endpoint** — self-disables without
   `CHAT_INTERNAL_API_SECRET`, so it can be deployed dark and enabled by configuration.

Steps 2-4 degrade to current behaviour if the next never ships; step 1 is a hard prerequisite for
step 2, not a degrade-gracefully step.
