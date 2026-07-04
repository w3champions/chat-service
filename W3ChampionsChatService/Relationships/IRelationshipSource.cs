using System;
using System.Threading.Tasks;

namespace W3ChampionsChatService.Relationships;

/// <summary>
/// The swappable read side of the relationship provider (C5/D2): fetches a fresh
/// <see cref="RelationshipSnapshot"/> for one player from the source of truth (the website backend).
/// Isolated behind this interface so the provider's cache + fail-closed policy is testable without any
/// HTTP — every test substitutes a fake; the only production implementation is
/// <see cref="WebsiteBackendRelationshipSource"/>. Implementations throw on any transport/parse failure;
/// <see cref="RelationshipProvider"/> translates a throw into a last-known fallback or a fail-closed error.
/// </summary>
public interface IRelationshipSource
{
    /// <summary>
    /// Fetches the current snapshot for <paramref name="battleTag"/>, stamping <paramref name="now"/> as
    /// its <see cref="RelationshipSnapshot.FetchedAt"/>. Throws on failure (never returns null).
    /// </summary>
    Task<RelationshipSnapshot> FetchAsync(string battleTag, DateTime now);
}
