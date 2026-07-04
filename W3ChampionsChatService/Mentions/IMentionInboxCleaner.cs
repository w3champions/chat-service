using System.Collections.Generic;
using System.Threading.Tasks;

namespace W3ChampionsChatService.Mentions;

/// <summary>
/// C4/C6 coordination surface (C4-plan.md decision D10): when moderation deletes or purges messages
/// (<c>ChatHub.DeleteMessage</c>/<c>PurgeMessagesFromUser</c>), any mention-inbox entries referencing
/// those message ids must be removed too — but C4 must not reach into C6's mention-inbox internals to
/// do it. This interface is the ONLY coordination point: C4 calls <see cref="RemoveForMessages"/>
/// STRICTLY AFTER its moderation audit log write and still BEFORE its fan-out event — an ordering C6
/// never touches. C6 Task 1 registered the placeholder <see cref="NoOpMentionInboxCleaner"/>; C6 Task 7
/// swapped the DI registration for the real <see cref="MentionInboxCleaner"/> (a mention_inbox
/// DeleteMany), now load-bearing for both call sites.
/// </summary>
public interface IMentionInboxCleaner
{
    Task RemoveForMessages(IReadOnlyCollection<string> messageIds);
}
