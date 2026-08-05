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

Optional additive fields (absent ⇒ today's behavior, byte-for-byte):

- `epoch` (string) / `seq` (integer, >= 1) — must arrive **together**; a lone one is a `400`. When
  present they stamp the same `(epoch, seq)` staleness state the roster assertion below uses, so a
  late-landing create retry can never resurrect a member a newer assertion already removed.
- `detached` (bool) — marks the channel born already frozen. **Ladder matches must send `detached: true`
  on create**: chat-service uses one channel kind for both custom lobbies and ladder matches, and ladder
  refs are never part of mm's live-lobby registry, so without birth-detach the first epoch sync after any
  mm restart would tear down every in-progress ladder game's chat.

### `PUT /internal/channels/{ref}/members` — DEPRECATED

> **This endpoint is scheduled for removal.** It exists only so chat-service can serve both mm's current
> (delta-based) and upcoming (assertion-based) membership protocols during the deploy window described
> below. Once mm's deploy to the assertion protocol is confirmed, this route, its request DTO, and the
> corresponding legacy code paths are deleted in a follow-up chat-service PR — every site that must be
> touched is marked in code with a grep-able `TRANSITION (2026-08-05 ...)` comment.

The legacy membership delta: `{ add: string[], remove: string[], focus? }`. `add`/`remove` tolerate a
missing array (treated as empty — "no change" for that half of the call). Tolerant of arriving before the
channel's own `POST` (create-on-demand, using the ref as a placeholder display name).

### `PUT /internal/channels/{ref}/roster`

The **authoritative full-set membership assertion** — the replacement for the delta above. mm sends the
lobby's complete member set; chat-service diffs it against stored membership and converges, idempotently.

```json
{ "epoch": "<opaque token>", "seq": 1, "members": ["Tag#1", "Tag#2"], "name": "My Lobby", "detached": false }
```

- `epoch` — an **opaque string** (the same character class/length cap as `ref`), mm's authority
  generation, fresh per mm boot. Compared for equality only — never parsed or ordered.
- `seq` — a positive integer (`>= 1`), mm's per-`(lobby, epoch)` monotonic counter. `0` is reserved
  server-side as "nothing applied yet under this epoch".
- `members` — the **complete** roster. Unlike the delta's `add`/`remove`, this is **not** null-tolerant:
  omitting it is a `400`, while `[]` is a legal, meaningful value (an empty lobby) and clears every
  existing member.
- `name` — optional; used **only** when the assertion must create the channel on demand (mm's boot-race
  healing, so a recreated room never displays its raw ref as its name). Ignored on an existing channel.
- `detached` — see below.

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
- the deprecated delta endpoint above is discarded too, regardless of which protocol asks;
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

### `DELETE /internal/channels/{ref}`

Explicit, unconditional teardown: messages purged, memberships deleted, the channel doc removed, and
`ChannelRemoved` pushed to every member who was online. `200` even for an unknown `ref` (a `404` would
only trigger a pointless mm retry), and — unlike the roster assertion and the deprecated delta — this
still tears down an **already-frozen** channel: detach guards the automated paths (assertions, sweeps),
not an explicit authoritative teardown mm chooses to send.

### Deploy order

**chat-service ships first.** It accepts both the deprecated delta shape and the new roster-assertion
shape simultaneously, so today's not-yet-deployed mm keeps working unchanged through its own release.
Once mm's deploy to the assertion protocol is confirmed, the delta endpoint and its supporting code are
removed in a follow-up chat-service PR.
