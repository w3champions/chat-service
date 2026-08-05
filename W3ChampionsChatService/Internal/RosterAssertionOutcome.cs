namespace W3ChampionsChatService.Internal;

/// <summary>
/// The result of <see cref="MatchChannelService.ApplyRosterAssertion"/> (2026-08-05 fix wave, final
/// review M2). Before this type existed, the controller logged an unconditional "roster-assert
/// succeeded" Information line regardless of what the domain layer actually did, WHILE the domain layer
/// separately logged its own "discarded ..." Information line on the staleness/detach paths — a
/// contradictory pair, both at Information, on exactly the storm paths (an mm retry storm, or mm
/// asserting a frozen lobby) those gates exist to absorb. The controller now logs exactly ONE
/// outcome-tagged line per assertion, and the domain layer's own discard lines are demoted to Debug.
/// </summary>
public enum RosterAssertionOutcome
{
    /// <summary>The (epoch, seq) CAS admitted the assertion and the membership diff was applied.</summary>
    Applied,

    /// <summary>Discarded — the channel was already detached (frozen); membership left untouched.</summary>
    DiscardedFrozen,

    /// <summary>Discarded — stale/duplicate/reordered (epoch, seq); membership left untouched.</summary>
    DiscardedStale,
}
