using System.Collections.Generic;
using System.Threading.Tasks;

namespace W3ChampionsChatService.Mentions;

/// <summary>
/// Placeholder <see cref="IMentionInboxCleaner"/> registered until C6 implements the real
/// mention-inbox purge and swaps this DI registration (see that interface's doc comment).
/// </summary>
public class NoOpMentionInboxCleaner : IMentionInboxCleaner
{
    public Task RemoveForMessages(IReadOnlyCollection<string> messageIds) => Task.CompletedTask;
}
