using System;
using MongoDB.Bson.Serialization.Attributes;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Users;

/// <summary>
/// Directory of every user chat has seen. Upserted at connect AND disconnect (C6);
/// serves mention search, the 90d activity gate, and last-online. Entries are kept —
/// the 90d gate is applied at query time, not via TTL.
/// <para>
/// BATTLETAG KEY CONVENTION (C6 T2 / D8): <see cref="BattleTag"/> (the Mongo <c>_id</c>) is ALWAYS
/// stored LOWERCASED — the same convention <see cref="Memberships.ChannelMembership"/> and
/// <see cref="UserSettings"/> already use (<see cref="UserDirectoryRepository"/>'s class doc has the
/// full rationale). <see cref="DisplayBattleTag"/> preserves the caller's original JWT casing for
/// enrichment/display — it is the only field on this entry that keeps mixed case.
/// <see cref="NormalizedName"/> is the lowercased FULL battleTag (e.g. "peter#123", not just the name
/// part) — one prefix index (<c>ix_normalizedName_lastSeenAt</c>) then serves both name-prefix ("pet…")
/// and name#digits-prefix ("peter#1…") autocomplete (T8).
/// </para>
/// </summary>
public class UserDirectoryEntry
{
    [BsonId]
    public string BattleTag { get; set; }

    public string DisplayBattleTag { get; set; }

    public string NormalizedName { get; set; }

    public DateTime LastSeenAt { get; set; }

    [BsonIgnoreIfNull]
    public ChatProfile Profile { get; set; }
}
