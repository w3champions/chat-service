using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Protocol;

/// <summary>
/// One <c>SearchMentionCandidates</c> result row (C6-plan.md D10). <see cref="BattleTag"/> is
/// display casing — autocomplete inserts it verbatim into <c>&lt;@BattleTag#123&gt;</c> markup.
/// <see cref="Tier"/> (1 = active channel viewer, 2 = online user, 3 = directory match within the
/// 90d activity gate) drives grouped autocomplete rendering; candidates are deduped across tiers
/// (first tier wins). <see cref="Profile"/> is nullable — a tier-1/2 tag with no directory row
/// (e.g. its upsert never landed) degrades to a null profile rather than failing the whole search.
/// </summary>
public record MentionCandidateDto(
    string BattleTag,
    string Name,
    int Tier,
    ChatProfile Profile);
