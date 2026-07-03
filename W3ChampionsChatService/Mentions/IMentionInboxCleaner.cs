using System.Collections.Generic;
using System.Threading.Tasks;

namespace W3ChampionsChatService.Mentions;

/// <summary>
/// C4/C6 coordination surface (C4-plan.md decision D10): when moderation deletes or purges
/// messages (later C4 tasks 3/4), any mention-inbox entries referencing those message ids must be
/// removed too — but C4 must not reach into C6's mention-inbox internals to do it. This interface is
/// the ONLY coordination point: C4 calls <see cref="RemoveForMessages"/> after a moderation delete;
/// C6 owns the real implementation and swaps the DI registration
/// (<see cref="NoOpMentionInboxCleaner"/> here, registered in Task 1) for it later. Task 1 registers
/// the no-op only — nothing calls this interface yet.
/// </summary>
public interface IMentionInboxCleaner
{
    Task RemoveForMessages(IReadOnlyCollection<string> messageIds);
}
