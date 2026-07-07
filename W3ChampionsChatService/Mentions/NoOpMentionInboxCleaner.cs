using System.Collections.Generic;
using System.Threading.Tasks;

namespace W3ChampionsChatService.Mentions;

/// <summary>
/// Placeholder <see cref="IMentionInboxCleaner"/> — the DI registration used until C6 Task 7 landed
/// the real <see cref="MentionInboxCleaner"/> (see that interface's doc comment). No longer the
/// production registration; kept only as a convenient test double for hub tests that need an
/// <see cref="IMentionInboxCleaner"/> constructor argument but don't assert on mention-inbox behavior.
/// </summary>
public class NoOpMentionInboxCleaner : IMentionInboxCleaner
{
    public Task RemoveForMessages(IReadOnlyCollection<string> messageIds) => Task.CompletedTask;
}
