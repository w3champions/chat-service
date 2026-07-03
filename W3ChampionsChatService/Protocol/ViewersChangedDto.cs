using System.Collections.Generic;

namespace W3ChampionsChatService.Protocol;

/// <summary>
/// The batched, idempotent viewer-roster delta for a channel (C3 Task 14 — the ViewersChanged push).
/// Emitted at most once per <see cref="Domain.ChatLimits.ViewersChangedFlush"/> (5s) per channel by the
/// <see cref="FanOut.ViewersAccumulator"/>, and the SAME payload object is sent to EVERY current focused
/// connection of the channel — there are no per-connection deltas (C3-plan decision 5).
/// <para>
/// <see cref="Joined"/>/<see cref="Left"/> are battleTags (NOT display names): the accumulator's only
/// state sources are <see cref="FanOut.FocusRegistry"/> and the emit <c>IHubContext</c> — it never
/// resolves names (that is <c>FocusChannel</c>'s job for the initial roster). A battleTag whose viewing
/// state at flush equals its state at the START of the window appears in NEITHER list (idempotent — a
/// join+leave or leave+rejoin flap within the window cancels). Clients apply the delta as an idempotent
/// SET operation (union <see cref="Joined"/>, subtract <see cref="Left"/>), so a battleTag redundantly
/// present in both the initial <c>FocusChannel</c> roster and a subsequent <see cref="Joined"/> is
/// harmless.
/// </para>
/// </summary>
public record ViewersChangedDto(
    string ChannelId,
    IReadOnlyList<string> Joined,
    IReadOnlyList<string> Left);
