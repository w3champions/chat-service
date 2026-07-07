using System;
using W3ChampionsChatService.Relationships;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// Builds a throwaway <see cref="IRelationshipProvider"/> for hub tests that need the C5 ctor dependency
/// but do NOT assert on relationship gating (the connect-time prefetch just warms an empty snapshot).
/// Mirrors <see cref="FanOutEngineTestFactory"/>/<see cref="ViewersAccumulatorTestFactory"/> so the
/// provider's dependency list is threaded through ONE place instead of every hub-test setup.
/// </summary>
internal static class RelationshipProviderTestFactory
{
    internal static IRelationshipProvider CreateIgnored() =>
        new RelationshipProvider(new FakeRelationshipSource(), TimeProvider.System);
}
