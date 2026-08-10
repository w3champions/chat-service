# w3champions-chat-service

The chat backend for the W3Champions Launcher. It is a SignalR hub (`/chatHub`) plus a small REST
surface, backed by MongoDB, providing:

- Public/semi-public/system (match/lobby/clan) channels, DMs, and group DMs.
- Durable per-channel message history (soft-delete aware, seq-anchored pagination).
- Presence/focus tracking, coalesced activity notifications, and viewer rosters.
- Moderation: message delete/purge, shadow-bans, and full/shadow lounge mutes.
- A one-time-ticket auth handshake fronting the identification-service JWT (`POST /auth/session` →
  SignalR `access_token`).

Clients are the W3Champions launcher (SignalR) and the website-backend, which proxies a subset of the
moderation surface (see below) to the admin UI.

## Architecture at a glance

- **`ChatHub`** (`Chats/ChatHub.cs` + `ChatHub.Channels.cs` + `ChatHub.Messaging.cs`) — the SignalR
  entry point: connect/disconnect, channel join/leave/focus, send/read, and the moderator trio
  (`DeleteMessage`, `PurgeMessagesFromUser`, `BanUser`).
- **`Channels/`, `Messages/`, `Memberships/`, `Mentions/`, `Mutes/`, `Sessions/`, `Users/`** — the
  Mongo-backed domain repositories and their documents.
- **`FanOut/`** — in-memory, per-process fan-out state (focus registry, online-member registry, the
  message-rate/channel-creation limiters, activity coalescing, viewer-roster batching) and the engine
  that routes a persisted message/event to the right live connections.
- **`Authentication/`** — the ticket-based connect handshake and the two permission gates: the SignalR
  `ChatHubPermissionFilter` (hub methods) and the MVC `UserHasPermissionFilter` (REST controllers), both
  driven by the same `[UserHasPermission(EPermission)]` attribute.
- **`Domain/`** — cross-cutting constants (`ChatLimits`, `RetentionPeriods`), TTL/index management
  (`ChatDomainIndexes`), and the weekly cleanup job.

All durable state lives in MongoDB (`W3Champions-Chat-Service`); fan-out/presence state is in-memory
and does not survive a restart (by design — clients rebuild it via `SessionState` on reconnect). The
service runs single-instance.

## Moderation

### Soft-delete model

Moderator message deletion is **always a soft delete**: `ChannelMessage.Deleted` is set to
`{ by, at }` (`Messages/MessageRepository.cs`, `MarkDeleted`/`MarkDeletedMany`). The document itself,
and its `ExpiresAt`/TTL, are left untouched. **Physical removal happens ONLY via the TTL index** — a
moderator action never triggers a hard delete. A user-facing read (`MessageRepository.UserVisible`)
excludes soft-deleted rows entirely; a moderator read (`LoadForModerator`, `LoadPageBeforeForModerator`)
includes them with the deletion flags intact, so moderator tooling (including the REST history endpoints
below) can always see what was removed, by whom, and when.

Both the single-message delete (`ChatHub.DeleteMessage`) and the bulk delete (`MarkDeletedMany`, used by
the purge below) are **conditional on `Deleted == null`** — a concurrent double-delete never overwrites
the first moderator's attribution and never re-fires the audit/cleanup/fan-out side effects a second
time. Re-running an already-applied delete/purge is a no-op that returns `Ok`.

### Purge scope — the moderation "scope wall"

`ChatHub.PurgeMessagesFromUser` soft-deletes every eligible message a target battleTag sent, across
channels, case-insensitively. "Eligible" is deliberately narrow: **Public, SemiPublic, and
System+Match channels only** — never DM, GroupDm, System+Clan, or System+Lobby content, and never a
message whose channel cannot be resolved (fail-closed — an unresolvable channel is dropped, not purged).
The TTL is what eventually clears private/clan/lobby content, not a moderator action.

This same scope predicate gates the single-message delete, the bulk purge, **and** the REST moderation
message-history read (below). It is defined exactly once, in
[`Channels/ChannelModeration.cs`](W3ChampionsChatService/Channels/ChannelModeration.cs)
(`ChannelModeration.IsModeratable`), specifically so the hub and the REST surface can never drift apart
on what a moderator is allowed to touch.

### Mute scope — where a lounge mute actually bites

There is a **second, narrower** scope wall, defined next to `IsModeratable` in the same file
(`ChannelModeration.IsMuteEnforced`) and read by exactly one call site — step 6 of `ChatHub.SendMessage`.
A lounge mute (full or shadow) is enforced on a send iff the channel is:

- **Public**, or
- a **ladder match room** — `System` + `SystemKind.Match` + `ChatChannel.Ladder`.

Everything else is exempt: SemiPublic, DMs/group DMs, clan and lobby system channels, and — deliberately
— **custom-game match rooms**. A muted player can still talk in the custom lobby that invited them; they
cannot talk in a ladder game's in-game or post-game chat.

The two walls are intentionally different sets and must not be collapsed. `IsModeratable` answers "may a
moderator reach into this room after the fact" (delete/purge/history) and covers SemiPublic and *every*
match room; `IsMuteEnforced` answers "is a muted user silenced while typing here", which is a product
decision about competitive-integrity surface.

**`ChatChannel.Ladder` is the only thing separating ladder from custom.** chat-service uses one
`SystemKind.Match` for both; both refs are a bare `nanoid(10)`, both create through the same endpoint,
and `detached` is set on *both* (at birth for ladder, at game start for a custom lobby), so `detached`
cannot be — and must never be made to — answer "is this ladder". The flag is set only from mm's explicit
`ladder: true` on the internal create/roster-assert calls (below), and is **sticky-true for the life of
the channel document**: no update ever clears it, so an older mm, a partial rollout, or a retry built
from a stale payload can never silently un-moderate a live ladder room. It does not survive teardown —
if a ref is deleted and later recreated, the recreating call must send `ladder: true` again.

### Shadow-ban model

A shadow-banned user's messages are stored normally (`ChannelMessage.Shadow = true`) but routed
selectively on delivery (`FanOut/FanOutEngine.cs`, `OnMessagePersisted`):

- The **author's own** focused connection receives the unflagged "illusion" echo (`Shadow` forced
  `false`) — from their perspective, the message sent normally.
- Any **other focused connection whose session holds the `Moderation` permission** receives the real,
  flagged (`Shadow == true`) copy — moderators see shadow content live, exactly as it exists.
- Every other focused member, and every unfocused member, receives nothing for that message.
- A shadow message **never** generates a `ChannelActivity`/unread ping for anyone (including moderators)
  — it must not surface as "something happened here" to a non-author.
- History reads follow the same rule: a user's own read (`UserVisible`) includes their own shadow
  messages and excludes everyone else's; a moderator read includes every shadow row, flagged, regardless
  of author (`LoadForModerator`, `LoadPageBeforeForModerator`, and the REST message-history endpoint).

### Mute reconciliation

Lounge mutes (full ban or shadow ban) are one row per `battleTag` (`Mutes/LoungeMute.cs`). There are
exactly two ways to write one, and both converge on the same reconciliation:

1. The hub — `ChatHub.BanUser` (moderator, in-band, live).
2. The REST surface — `Mutes/MuteController.cs` (`POST`/`DELETE /api/loungeMute`), used by the
   website-backend's admin tooling.

Both call `MuteReconciliationService.ApplyBanAsync` / `ClearMuteOnLiveConnections`, which does two
things atomically from the caller's point of view: persist the mute to Mongo, **and** reconcile every
currently-live connection of the target — updating the per-connection cache immediately (so enforcement
needs zero per-send DB read) and, for a full ban, pushing `PlayerBannedFromChat` (expiry only — never the
reason or the shadow flag) to the target's live connections. A shadow ban stays completely silent to the
target. At connect time, `SessionStateAssembler` seeds the same cache from Mongo, so a mute set while the
target was offline is picked up on their next connect regardless of which write path created it.

**Known residual (acceptance 5):** a mute written **directly** to the Mongo `LoungeMute` collection —
bypassing both the hub and the REST controller (e.g. a manual DB edit or an out-of-band migration) —
never invokes `ApplyBanAsync`. It takes effect **only on the target's next reconnect**, when
`SessionStateAssembler` re-seeds the cache from Mongo. This is intentional and documented in code at
`MuteReconciliationService.cs` and `ChatHub.cs` (`BanUser`'s doc comment) — there is no live-reconcile
path for writes that don't go through one of the two in-band ban paths.

## REST API for the website-backend (W3 re-point)

Every endpoint below requires `Authorization: Bearer <JWT>` carrying the `Moderation` permission
(`[UserHasPermission(EPermission.Moderation)]`, enforced by `UserHasPermissionFilter`).

### `GET /api/moderation/channels?limit=`

The channelId-resolution surface the moderation UI needs before it can page history — replaces the
retired room-name-based `GET /api/chat/{chatroom}`. Returns the eligible channels (same scope wall as
above: Public, SemiPublic, System+Match) sorted by `lastMessageAt` **descending** (most recently active
first).

`limit` is clamped to `[1, 500]`, default `100` — out-of-range values are clamped, never rejected.

Response — `200 OK`, a JSON array:

```json
[
  {
    "id": "665f1b2c9a1e4a0012abc123",
    "name": "W3C Lounge",
    "type": "Public",
    "systemKind": null,
    "systemRef": null,
    "lastSeq": 48213,
    "lastMessageAt": "2026-07-03T21:14:02.331Z"
  },
  {
    "id": "665f1b2c9a1e4a0012abc456",
    "name": null,
    "type": "System",
    "systemKind": "Match",
    "systemRef": "match-98213",
    "lastSeq": 42,
    "lastMessageAt": "2026-07-03T20:58:11.004Z"
  }
]
```

### `GET /api/moderation/channels/{channelId}/messages?beforeSeq=&limit=`

Pages the **real, durable history** of one channel — deleted rows and shadow rows included, flags
intact (never filtered like a user read). Seq-anchored, newest-first pagination returned in
**ascending** seq order (oldest to newest within the page), exactly like the hub's `GetMessages`.

- `beforeSeq` (optional) — omit for the newest page; pass the previous page's `nextBeforeSeq` to walk
  further back in history.
- `limit` — clamped to `[1, 100]`, default `100`.

Resolution:
- Unknown/unresolvable `channelId` → **404** (a moderator must never learn whether a private channel
  merely doesn't exist vs. is out of scope).
- A channel that resolves but is **not** in the moderation scope wall (Dm, GroupDm, System+Clan,
  System+Lobby) → **403**.
- Otherwise → **200**, with the page.

Response — `200 OK`:

```json
{
  "channelId": "665f1b2c9a1e4a0012abc123",
  "messages": [
    {
      "id": "665f1b3a9a1e4a0012def001",
      "channelId": "665f1b2c9a1e4a0012abc123",
      "seq": 48198,
      "senderBattleTag": "Peter#123",
      "senderName": "Peter",
      "content": "gl hf",
      "sentAt": "2026-07-03T21:10:00.000Z",
      "deleted": false,
      "deletedBy": null,
      "deletedAt": null,
      "shadow": false
    },
    {
      "id": "665f1b3a9a1e4a0012def002",
      "channelId": "665f1b2c9a1e4a0012abc123",
      "seq": 48199,
      "senderBattleTag": "Spammer#456",
      "senderName": "Spammer",
      "content": "buy gold at ...",
      "sentAt": "2026-07-03T21:10:05.000Z",
      "deleted": true,
      "deletedBy": "mod#1",
      "deletedAt": "2026-07-03T21:11:00.000Z",
      "shadow": false
    },
    {
      "id": "665f1b3a9a1e4a0012def003",
      "channelId": "665f1b2c9a1e4a0012abc123",
      "seq": 48200,
      "senderBattleTag": "Shadow#789",
      "senderName": "Shadow",
      "content": "spam spam spam",
      "sentAt": "2026-07-03T21:12:00.000Z",
      "deleted": false,
      "deletedBy": null,
      "deletedAt": null,
      "shadow": true
    }
  ],
  "nextBeforeSeq": 48198
}
```

`nextBeforeSeq` is the cursor to pass as `beforeSeq` on the next call to walk further back — the minimum
`seq` in the returned page. It is `null` when the page was not full (fewer rows than the clamped limit),
meaning there is no older history left to page through.

### `GET` / `POST` / `DELETE /api/loungeMute` — unchanged

This trio (`Mutes/MuteController.cs`) is **byte-identical** to the pre-rewrite service — routes, request
shape, and response shape are all pinned by regression tests (`LoungeMuteRestContractTests`) and were
**not** touched by the chat-service rewrite. Documented here so the website-backend has the full picture
of what it proxies for moderation.

- **`GET api/loungeMute`** → `200 OK`, a JSON array of every stored lounge mute:

  ```json
  [
    {
      "id": "target#123",
      "battleTag": "target#123",
      "endDate": "2026-08-01T00:00:00Z",
      "insertDate": "2026-07-03T21:00:00Z",
      "author": "mod#1",
      "reason": "spam",
      "isShadowBan": false
    }
  ]
  ```

  (`id` is a derived alias of `battleTag`, not a separate stored field.)

- **`POST api/loungeMute`** — body:

  ```json
  { "battleTag": "target#123", "endDate": "2026-08-01T00:00:00Z", "author": "mod#1", "reason": "spam", "isShadowBan": false }
  ```

  `400 Bad Request` if `battleTag` or `endDate` is empty. Otherwise `200 OK` and the mute is persisted
  **and** reconciled onto the target's live connections immediately (see Mute reconciliation above).

- **`DELETE api/loungeMute/{bTag}`** — `404 Not Found` if no mute exists for `bTag`; otherwise `200 OK`,
  and the target's live connections have their cached mute cleared immediately (the client's hidden
  rooms/ban banner still only refresh on reconnect — no live "ban lifted" event is sent, by design).

## Internal API for the matchmaking service (HMAC realm)

Every endpoint below lives under `/internal/channels` and is gated by a per-caller HMAC signature —
**a disjoint auth realm from the JWT/ticket scheme above**, never `[UserHasPermission]`. mm signs the
raw request body and presents two headers:

- `X-W3C-Webhook-Timestamp` — unix seconds.
- `X-W3C-Signature` — `"v1=" + hex(HMAC_SHA256(key = the caller's configured secret, msg = "v1." +
  timestamp + "." + rawBodyBytes))`, taken over the **exact** raw body bytes (never a re-serialized
  copy). Hex is verified case-insensitively.

A request is rejected with a bare, body-free `401` when either header is missing, the body exceeds the
internal size cap, the signature does not verify, the clock skew between `now` and the timestamp exceeds
a **±300s freshness window**, or the caller the signature resolves to is not on the endpoint's allow-list
(only mm may call this surface). Each caller (mm, website-backend) is provisioned its own secret out of
band — this document intentionally carries no secret value, hostname, or environment name.

Every `ref` (a lobby/match identifier) and `epoch` is re-validated server-side against the same character
class regardless of what the caller sent, and every `400` returns a generic body that never echoes which
rule failed — mm should treat any `4xx` as "fix the request", not parse the message.

### `POST /internal/channels`

Idempotent create-or-get of a match channel: `{ kind: "match", ref, name, members: string[], focus? }`.
A duplicate call for the same `ref` is `200`, not a conflict — same channel, no duplicate memberships, no
re-push for members already present, and the 24h creation-anchored expiry is never reset by a re-get.

`name` is **normalized, never rejected**: trimmed, and clamped to 100 chars. An empty-after-trim name
falls back to `ref` as a placeholder display name — a cosmetic field must never be able to block an
otherwise-valid create.

Optional additive fields (absent ⇒ today's behavior, byte-for-byte):

- `epoch` (string) / `seq` (integer, >= 1) — must arrive **together**; a lone one is a `400`. When
  present they stamp the same `(epoch, seq)` staleness state the roster assertion below uses, so a
  late-landing create retry can never resurrect a member a newer assertion already removed.
- `detached` (bool) — marks the channel born already frozen. **Ladder matches must send `detached: true`
  on create**: chat-service uses one channel kind for both custom lobbies and ladder matches, and ladder
  refs are never part of mm's live-lobby registry, so without birth-detach the first epoch sync after any
  mm restart would tear down every in-progress ladder game's chat.
- `ladder` (bool) — declares this ref a **ladder match** rather than a custom-game lobby. Absent/false ⇒
  custom lobby, today's behavior byte-for-byte. **Ladder matches must send `ladder: true`**: this flag is
  the sole input to the mute scope described under [Mute scope](#mute-scope--where-a-lounge-mute-actually-bites),
  so a ladder match created without it lets lounge-muted and shadow-banned players chat freely in its
  in-game and post-game room. Sticky-true server-side — once set for a ref it is never cleared, so a
  later call that omits it is a harmless no-op rather than a silent un-moderation.

  It is deliberately **separate from `detached`**, even though mm happens to send both together on the
  ladder create path. The two answer different questions and their sets are not the same: `detached` is
  also set on every custom lobby at game start.

### `PUT /internal/channels/{ref}/roster`

The **authoritative full-set membership assertion** — the sole membership-mutation protocol mm drives. mm
sends the lobby's complete member set; chat-service diffs it against stored membership and converges, idempotently.
A user-initiated leave (`ChatHub.LeaveChannel`) on a live match channel is re-converged by the next
assertion, by design (2026-08-05 reconciliation review, H4) — mm is authoritative for lobby membership.

```json
{ "epoch": "<opaque token>", "seq": 1, "members": ["Tag#1", "Tag#2"], "name": "My Lobby", "detached": false, "ladder": false }
```

- `epoch` — an **opaque string** (the same character class/length cap as `ref`), mm's authority
  generation, fresh per mm boot. Compared for equality only — never parsed or ordered.
- `seq` — a positive integer (`>= 1`), mm's per-`(lobby, epoch)` monotonic counter. `0` is reserved
  server-side as "nothing applied yet under this epoch".
- `members` — the **complete** roster. **Not** null-tolerant: omitting it is a `400`, while `[]` is a
  legal, meaningful value (an empty lobby) and clears every existing member.
- `name` — optional; used **only** when the assertion must create the channel on demand (mm's boot-race
  healing, so a recreated room never displays its raw ref as its name). Ignored on an existing channel.
  **Normalized, never rejected**: trimmed and clamped to 100 chars; an empty-after-trim name falls back
  to the ref placeholder, exactly like create-on-demand above. `name` is cosmetic — mm applies no
  length/trim/charset validation of its own to a custom-game lobby name before sending it, and a field
  chat cannot store must never be able to reject the entire authoritative roster.
- `detached` — see below.
- `ladder` — same meaning as on the create call above. Carried here too because this route is *also* a
  channel-creating path: mm's ladder create has a retry-on-failure fallback that converges through
  `PUT .../roster`, and that assertion may be the call that creates the room on demand. Applied **before**
  the detach-freeze and staleness gates, and independently of both — a discarded assertion discards its
  *roster*, not the truth about what kind of room this is.

**Staleness — `(epoch, seq)` admission table**, persisted per channel:

| Stored state | Incoming | Result |
|---|---|---|
| no epoch stored yet | any | **accept**, stamp `(epoch, seq)` — covers channels that predate this protocol |
| same epoch | `seq` strictly greater than stored | **accept**, advance the stamp |
| same epoch | `seq` equal to or lower than stored | **discard** (duplicate/reordered delivery — a no-op) |
| different epoch | any | **accept and re-anchor** to the incoming epoch (logged as an anomaly) — epochs are opaque and unordered, so a discard rule here would permanently wedge a channel that outlived an mm restart |

A **discarded** assertion (stale, duplicate, or against a frozen channel — see below) still returns
`200`: it is a successful no-op, not a failure, and mm must not retry a correctly-discarded assertion.

`detached: true` marks mm's final assertion for a lobby (sent once, at game start): the member set is
applied first, then the channel **freezes**. Once frozen:

- every later roster assertion for that ref is discarded;
- `POST /internal/channels` still find-or-creates and backfills the name, but adds no members;
- an explicit `DELETE` (below) still works — detach freezes assertions and sweeps, not an explicit
  teardown command.

A frozen channel is excluded from every sweep, including the epoch sync below; the 24h creation-anchored
TTL is its sole cleanup path from there.

### `POST /internal/channels/epoch-sync`

mm's boot-time convergence sweep, sent once per mm process start under a fresh `epoch`:

```json
{ "epoch": "<opaque token>", "liveLobbyRefs": ["abc123XYZ0", "..."] }
```

`liveLobbyRefs` is the **complete** set of lobby refs mm still knows about right now (the empty set after
a crash, since lobbies are ephemeral state in mm) — like `members` above, it is **not** null-tolerant
(omitting it is a `400`) but `[]` is honored as a legal, meaningful value. Every entry is re-validated
against the ref character class and the array is capped at 512 entries; an over-cap body is a `400` (mm
should retry rather than send a partial world).

Every non-frozen match channel is then resolved one of two ways:

- **absent** from `liveLobbyRefs` ⇒ torn down (same routine as the explicit `DELETE` below: messages and
  memberships purged, channel doc removed, `ChannelRemoved` pushed to every member who was online).
- **present** ⇒ spared, and re-anchored to the new epoch with its `seq` counter reset, so mm's first
  assertion under the new epoch applies cleanly.

**Frozen (`detached`) channels are never touched by an epoch sync** — not loaded, not torn down, not
re-anchored — regardless of whether their ref appears in `liveLobbyRefs`. A live in-progress or
just-finished game's chat must never be swept away by an mm restart; its 24h TTL is its only cleanup path.

**Only assertion-stamped channels are ever sweep candidates.** A channel becomes eligible for an epoch
sync only once it has participated in the assertion protocol at least once — created with `epoch`/`seq`,
or asserted via the roster endpoint above. A channel created via `POST /internal/channels` without
`epoch`/`seq` and never since asserted carries no stamp at all and is **invisible to the sweep**: it
survives regardless of `liveLobbyRefs` and falls to its own 24h creation-anchored TTL instead. This is
what makes it safe to deploy mm's epoch-sync call against the match channels chat already holds from
before mm's own deploy — the first sync after mm's release simply never sees them, so it cannot tear them
down. No backfill, feature flag, or deploy-order choreography is required for this beyond "chat deploys
first" (see Deploy order below).

### `DELETE /internal/channels/{ref}`

Explicit, unconditional teardown: messages purged, memberships deleted, the channel doc removed, and
`ChannelRemoved` pushed to every member who was online. `200` even for an unknown `ref` (a `404` would
only trigger a pointless mm retry), and — unlike the roster assertion — this still tears down an
**already-frozen** channel: detach guards the automated paths (assertions, sweeps), not an explicit
authoritative teardown mm chooses to send.

### `POST /internal/channels/{ref}/system-message`

Publishes a **server-authored** message into an existing match channel — a message with no sender and no
free-form content, carrying a structured body a client renders against its own i18n catalogue:

```json
{
  "key": "match_intro",
  "params": { "map": "Amazonia" },
  "listParams": { "players": ["Grubby#2136", "Happy#2233"] },
  "fallbackText": "Match on Amazonia — Grubby#2136, Happy#2233",
  "dedupeKey": "match_intro"
}
```

**Lookup-only** — deliberately unlike `POST /internal/channels`, which find-or-creates. An unknown `ref`
is a **`404`**, never an implicit create: a system message is meaningless without the room it narrates,
and creating one here would leave a memberless channel nobody can ever see. The same `404` also covers
the race where the channel is torn down (by `DELETE`, or by its 24h TTL) between the lookup and the
write — mm should treat it as "this room is gone, stop retrying", not as a transient failure.

- `key` — **required**, the client's catalogue lookup token. Trimmed, then validated against **the same
  character class as `ref`**: `[A-Za-z0-9_-]`, 1-64 chars. **`match.intro` is a `400`** — dots are not in
  the class, and dotted keys are the dominant i18n convention, so this is the mistake to expect. Use
  `match_intro`. Empty-after-trim is a `400`.
- `fallbackText` — **required**, the server-rendered English a client that does not recognise `key`
  displays, and the only rendering the moderation history has. Trimmed; empty-after-trim is a `400`.
  **Normalized, never rejected for length**: clamped to 512 chars (the same cap a user message body
  gets), on a UTF-16-safe boundary so a truncated emoji is dropped whole rather than half-persisted.
- `params` (`{string: string}`) / `listParams` (`{string: string[]}`) — both **optional**; absent means
  "no params". When present, every **key** is validated against the same `[A-Za-z0-9_-]` class as `key`
  (keys become BSON element names as well as client placeholders, so a dotted or `$`-prefixed key is
  a `400`). Every **value** / list item must be free of control characters and U+2028/U+2029 (they
  persist and fan out to every channel member as rendered display text), but — unlike a `members`
  entry — **blank or `null` is accepted and stored as-is, never a `400`**: a param value is display text
  `fallbackText` already covers for a client that does not recognise `key`, not an identity a caller
  could usefully retry its way out of rejecting. Neither is length-capped beyond the 64 KB signed-body
  cap.
- `dedupeKey` — **optional**. When supplied, the publish is at-most-once per `(channel, dedupeKey)`:
  a retry returns `200` and re-publishes nothing (mm retries on timeout, and an intro must never
  double-post). Validated against the same `[A-Za-z0-9_-]` class when non-empty, since it becomes a
  Mongo index key. **Absent, empty, and whitespace-only are all equivalent and all mean "no dedupe"** —
  never a `400`, and never deduped against each other, so two blank-key calls persist as two distinct
  messages.

Success is a body-free `200`. Retention is unchanged: the message follows the normal 30d channel-message
TTL and the publish never re-stamps the channel shell's own 24h creation-anchored expiry.

### Deploy order

**Post-game chat ships as one release: chat-service, matchmaking-service and the launcher together.**
The system-message route is the reason, and it is the one route on this surface that is **not
fail-open** — unlike `ladder` (inert until mm sends it) or the old delta endpoint (a harmless `404`),
calling it against a client that does not understand it actively breaks something. A launcher without
system-message support declares `sender` and `content` required and non-nullable on its message type,
and `appendMessage` calls `lastMessageFromMessage` unconditionally — before any kind check — which
dereferences `message.sender.battleTag` and parses `message.content`. This service omits nulls on the
wire, so both arrive **absent** and that throws inside the client's store action: the message never
lands, the rest of the receive handler never runs, and `GetMessages` replays the same poisoned row on
every reconnect. It **breaks the channel for the session** rather than degrading, which is why the
launcher cannot lag the publisher.

Deploying this service on its own is still safe in the meantime — it publishes nothing by itself; every
system message originates in an mm call.

**`ladder` is inert until mm sends it.** The mute gate reads a flag only mm can set, so between this
service's deploy and mm's, ladder match rooms stay exactly as unmoderated as they are today — this
half of the change fixes nothing on its own and needs the matching mm release to take effect. It is
additive and fail-open in that direction by construction: an absent flag reads as "custom lobby". Rooms
created before mm's deploy are never retro-classified (the flag is only ever written by an incoming
call), so the gate starts applying to matches created from mm's deploy onward, not to in-flight ones.

**chat-service deploys before (or with) mm.** Until mm's own deploy lands, its still-running deployment
keeps calling the old membership-delta endpoint this service no longer serves — those calls `404`
harmlessly (fail-soft: no chat functionality depends on them succeeding) until mm's own deploy switches it
over to the roster-assertion protocol above.

**Rollback is a one-way door once mm has deployed the assertion protocol.** After mm starts calling
`PUT .../roster` and `POST .../epoch-sync`, rolling chat-service back below this version `404`s both
routes. mm's assertion scheduler has no attempt cap, so every dirty lobby retries at a steady ~30s cadence
forever and the boot-time epoch sync never converges — a silent, total desync whose only tell is a pegged
`matchmaking_chat_dirty_channels` gauge on the mm side. **Never roll chat-service back below this version
while assertion-era mm is live; if a rollback is ever needed, roll mm back first.**
