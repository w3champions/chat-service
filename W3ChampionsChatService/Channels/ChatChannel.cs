using System;
using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Channels;

public class ChatChannel
{
    public string Id { get; set; } = ObjectId.GenerateNewId().ToString();

    [BsonRepresentation(BsonType.String)]
    public ChannelType Type { get; set; }

    [BsonIgnoreIfNull]
    public string Name { get; set; }

    [BsonIgnoreIfNull]
    public string NormalizedName { get; set; }

    [BsonRepresentation(BsonType.String)]
    [BsonIgnoreIfNull]
    public SystemChannelKind? SystemKind { get; set; }

    /// <summary>lobbyId / matchId / clanId, depending on SystemKind.</summary>
    [BsonIgnoreIfNull]
    public string SystemRef { get; set; }

    /// <summary>
    /// System+Match only — true when mm declared this ref a LADDER match rather than a custom-game
    /// lobby. chat-service uses ONE <see cref="SystemChannelKind.Match"/> for both, and nothing else in
    /// the document distinguishes them (both refs are a bare <c>nanoid(10)</c>, both create through the
    /// same endpoint, and <see cref="Detached"/> is set on BOTH — at birth for ladder, at GAME_STARTED
    /// for a custom lobby), so this flag is the sole discriminator. It exists for exactly one consumer:
    /// <see cref="ChannelModeration.IsMuteEnforced"/>, the send-path mute scope — a ladder match's
    /// in-game/post-game room is moderated like a Public room, a custom lobby's is not.
    /// <para>
    /// STICKY-TRUE FOR THE LIFE OF THE DOCUMENT. Set by <c>MatchChannelService</c> when an internal
    /// create or roster assertion carries <c>ladder: true</c>, and never cleared while the channel
    /// exists: a later call omitting the flag (an older mm, a partial rollout, a retry built from a
    /// stale payload) must not be able to silently un-moderate a room mid-game. There is no
    /// <c>SetLadder(false)</c>, by design.
    /// </para>
    /// <para>
    /// It does NOT survive teardown, and that is the one way a ladder ref can come back unmoderated: an
    /// explicit <c>DELETE /internal/channels/{ref}</c> or an epoch-sync sweep hard-deletes the document,
    /// so a subsequent call that RECREATES the same ref starts from a blank document and must send
    /// <c>ladder: true</c> again. Not defended against here on purpose — a recreated ref is a new room,
    /// and resurrecting classification from a deleted document would need a tombstone this service has
    /// no other use for. Unreachable for ladder in practice: mm never sends DELETE for a ladder ref, and
    /// ladder rooms are born detached, which excludes them from every sweep.
    /// </para>
    /// [BsonIgnoreIfDefault] keeps the field ABSENT on every non-ladder doc, so it costs nothing on the
    /// custom-lobby majority and every pre-existing document reads back as false. [JsonIgnore]: like
    /// the assertion state below, this is mm↔chat bookkeeping, never client protocol (the raw entity
    /// rides ChannelAddedDto/ChannelDto).
    /// </summary>
    [BsonIgnoreIfDefault]
    [JsonIgnore]
    public bool Ladder { get; set; }

    /// <summary>Dm only — see DmPairKey.For. Unique partial index (Type == Dm).</summary>
    [BsonIgnoreIfNull]
    public string PairKey { get; set; }

    [BsonRepresentation(BsonType.String)]
    [BsonIgnoreIfNull]
    public DmRequestState? RequestState { get; set; }

    /// <summary>
    /// Dm only — the battleTag of whoever wrote first, stamped once at pending-shell creation via
    /// $setOnInsert (never mutated afterwards). Serialized to BOTH parties (C5 D3): it rides the raw
    /// <see cref="ChatChannel"/> in <c>ChannelDto</c>/<c>ChannelAddedDto</c>, and both parties already
    /// know who opened the conversation, so this is not a leak. Decline state is placement-DISJOINT
    /// from this field — see <see cref="W3ChampionsChatService.Memberships.ChannelMembership.DeclinedUntil"/>.
    /// </summary>
    [BsonIgnoreIfNull]
    public string RequestInitiatedBy { get; set; }

    /// <summary>Per-channel monotonic message counter — allocated via findOneAndUpdate $inc.</summary>
    public long LastSeq { get; set; }

    [BsonIgnoreIfNull]
    public DateTime? LastMessageAt { get; set; }

    /// <summary>
    /// Dm (accepted) / GroupDm only — the newest user-visible message, denormalized for conversation-list
    /// rendering at rest. Rides the raw <see cref="ChatChannel"/> in <c>ChannelDto</c>/<c>ChannelAddedDto</c>/
    /// <c>GetConversations</c> like every other field here. Absent means "nothing to show yet" (a fresh or
    /// pending shell, a channel whose only messages are shadow or deleted, or a doc written before this
    /// field existed — see <see cref="ChannelLastMessage"/> for the full scope and shadow rules).
    /// </summary>
    [BsonIgnoreIfNull]
    public ChannelLastMessage LastMessage { get; set; }

    /// <summary>Absolute expiry instant (TTL index, expireAfterSeconds 0). Absent = permanent.</summary>
    [BsonIgnoreIfNull]
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// System+Match only — the mm authority EPOCH of the last applied full-set roster assertion
    /// (2026-08-05 reconciliation spec). An opaque token: compared for equality ONLY, never parsed or
    /// ordered. Absent on every channel created without epoch/seq and never since asserted — every read
    /// must tolerate absence. [JsonIgnore]: mm↔chat reconciliation
    /// bookkeeping, never client protocol (the raw entity rides ChannelAddedDto/ChannelDto).
    /// </summary>
    [BsonIgnoreIfNull]
    [JsonIgnore]
    public string AssertEpoch { get; set; }

    /// <summary>
    /// System+Match only — the per-(channel, epoch) sequence number of the last applied roster
    /// assertion. Contract: mm sends seq >= 1, so 0 is the "nothing applied yet under this epoch"
    /// sentinel the epoch sync writes when it re-anchors a spared channel (never $unset — a MISSING
    /// field would not match Mongo's $lt, which does not compare across BSON types, and would wedge
    /// the channel).
    /// </summary>
    [BsonIgnoreIfDefault]
    [JsonIgnore]
    public long AssertSeq { get; set; }

    /// <summary>
    /// System+Match only — true once mm's GAME_STARTED final assertion froze this room: membership is
    /// frozen (every later roster assertion for this ref is discarded), and the channel is excluded from
    /// EVERY sweep including epoch syncs. The 24h creation-anchored TTL is its sole cleanup path.
    /// <para>
    /// DELIBERATE BYPASS (2026-08-05 fix wave, final review N2): the freeze does NOT cover
    /// <c>W3ChampionsChatService.Internal.MatchChannelService.AddMemberWithInvariant</c>'s cross-channel
    /// eviction — when a user's NEXT match starts, the one-match-channel-per-user invariant still deletes
    /// their membership row on THIS (detached, frozen) channel to make room for the new one. This is
    /// intentional, not a hole in the freeze: it is exactly how a post-game room leaves a user's channel
    /// tray once their next game begins, rather than lingering there until the 24h TTL reaps the whole
    /// channel.
    /// </para>
    /// [BsonIgnoreIfDefault] keeps the field ABSENT on non-detached docs, so Ne(Detached, true) matches
    /// every pre-existing document.
    /// </summary>
    [BsonIgnoreIfDefault]
    [JsonIgnore]
    public bool Detached { get; set; }
}
