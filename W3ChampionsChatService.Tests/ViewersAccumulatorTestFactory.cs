using W3ChampionsChatService.FanOut;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// Builds a throwaway <see cref="ViewersAccumulator"/> for hub tests that require it as a constructor
/// dependency (Task 14) but do NOT assert on ViewersChanged batching. Its pushes go to an ignored
/// capture sink and it reads an empty <see cref="FocusRegistry"/>, and — crucially — its
/// <see cref="ViewersAccumulator.FlushDue"/> is never driven, so it never emits: the hub's
/// focus/unfocus/disconnect routing simply records into inert state.
/// <para>
/// Centralised so the accumulator's dependency list is threaded through ONE place instead of every hub
/// test setup. Tests that DO assert on ViewersChanged construct it explicitly with their own shared
/// harness + FocusRegistry (see ViewersAccumulatorTests).
/// </para>
/// </summary>
internal static class ViewersAccumulatorTestFactory
{
    internal static ViewersAccumulator CreateIgnored() =>
        new ViewersAccumulator(
            new HubPushCaptureHarness().HubContext,
            new FocusRegistry(),
            EmptyViewerResolver());

    /// <summary>
    /// A <see cref="W3ChampionsChatService.Chats.ViewerResolver"/> over freshly-empty
    /// <see cref="W3ChampionsChatService.Sessions.SessionRegistry"/>/<see cref="W3ChampionsChatService.Chats.ConnectionMapping"/>
    /// instances — correct ONLY where the test asserts nothing about a resolved viewer's display name or
    /// flair (every resolve degrades to <c>ChannelViewerDto(tag, tag, null)</c> regardless of which empty
    /// registries back it, so no test outcome can depend on which instance this is). A test that DOES
    /// assert on a joined viewer's name/flair must wire a resolver over the SAME registries its fixture
    /// seeds instead.
    /// </summary>
    internal static W3ChampionsChatService.Chats.ViewerResolver EmptyViewerResolver() =>
        new W3ChampionsChatService.Chats.ViewerResolver(
            new W3ChampionsChatService.Sessions.SessionRegistry(),
            new W3ChampionsChatService.Chats.ConnectionMapping());
}
