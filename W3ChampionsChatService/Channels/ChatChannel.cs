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

    /// <summary>Absolute expiry instant (TTL index, expireAfterSeconds 0). Absent = permanent.</summary>
    [BsonIgnoreIfNull]
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// System+Match only — the mm authority EPOCH of the last applied full-set roster assertion
    /// (2026-08-05 reconciliation spec). An opaque token: compared for equality ONLY, never parsed or
    /// ordered. Absent on every channel that predates the assertion protocol (created via the legacy
    /// create/delta path) — every read must tolerate absence. [JsonIgnore]: mm↔chat reconciliation
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
    /// frozen (later assertions AND legacy deltas are discarded), and the channel is excluded from
    /// EVERY sweep including epoch syncs. The 24h creation-anchored TTL is its sole cleanup path.
    /// [BsonIgnoreIfDefault] keeps the field ABSENT on non-detached docs, so Ne(Detached, true) matches
    /// every pre-existing document.
    /// </summary>
    [BsonIgnoreIfDefault]
    [JsonIgnore]
    public bool Detached { get; set; }
}
