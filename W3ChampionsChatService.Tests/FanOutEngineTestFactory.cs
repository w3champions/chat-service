using System;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.FanOut;
using W3ChampionsChatService.Sessions;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// Builds a throwaway <see cref="FanOutEngine"/> for hub tests that require the engine as a
/// constructor dependency but do NOT assert on its fan-out delivery. Its pushes go to an ignored
/// capture sink and its registries are empty, so the Task-13 activity routing finds no members to
/// offer — the engine is inert from the test's point of view.
/// <para>
/// Centralised so the engine's dependency list (which grew in Task 13 to include the
/// <see cref="OnlineMemberRegistry"/> + <see cref="ActivityCoalescer"/>, and in Task 18 to include
/// <see cref="ISessionRegistry"/>) is threaded through ONE place instead of every hub-test setup.
/// Tests that DO assert on fan-out delivery construct the engine explicitly with their own shared
/// harness/registries (see FanOutEngineTests / ActivityCoalescerTests / ChannelEventEmitterTests).
/// </para>
/// </summary>
internal static class FanOutEngineTestFactory
{
    internal static FanOutEngine CreateIgnored()
    {
        var harness = new HubPushCaptureHarness();
        var focus = new FocusRegistry();
        var members = new OnlineMemberRegistry();
        return new FanOutEngine(
            harness.HubContext,
            focus,
            members,
            new ActivityCoalescer(harness.HubContext, members),
            new SessionRegistry(),
            new PresenceInterestRegistry(),
            new ViewersAccumulator(harness.HubContext, focus, ViewersAccumulatorTestFactory.EmptyViewerResolver()),
            TimeProvider.System);
    }
}
