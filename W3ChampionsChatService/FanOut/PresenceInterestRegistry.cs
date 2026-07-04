namespace W3ChampionsChatService.FanOut;

/// <summary>
/// C6 (spec §9, C6-plan.md D11) — the presence-interest index. Presence interest derives EXCLUSIVELY
/// from the focused set (a connection focused on a Dm/GroupDm channel is interested in that channel's
/// other members); there is deliberately NO client-facing subscribe API, so clients can never manage
/// their own subscriptions.
/// <para>
/// SEAM STATE (C6 Task 5, D15): this type is created and DI-registered NOW purely so
/// <see cref="Chats.ChatHub"/>'s constructor grows EXACTLY ONCE — a single sweep of every test
/// construction site (the C5 D19 single-ctor-growth discipline) rather than two separate sweeps — even
/// though its first CONSUMER lands in Task 9. Task 9 fills in the D11 state (connection→channel→watched
/// tags; reverse tag→connections; watchers-by-channel) and API (RegisterFocus / RevokeFocus /
/// OnMemberAdded / OnMemberRemoved / RemoveChannel / RemoveConnection / GetInterestedConnections) under
/// the single-lock idiom of <see cref="Sessions.SessionRegistry"/> / <see cref="FocusRegistry"/>. Until
/// then it holds no state and exposes no behavior — nothing in the T5 send path touches it.
/// </para>
/// </summary>
public class PresenceInterestRegistry
{
}
