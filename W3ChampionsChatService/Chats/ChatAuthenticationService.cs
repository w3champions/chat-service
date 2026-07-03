using System;
using System.Threading.Tasks;
using MongoDB.Driver;
using W3ChampionsChatService.Authentication;
using Serilog;

namespace W3ChampionsChatService.Chats;

public interface IChatAuthenticationService
{
    Task<ChatUser> GetUserFromIdentity(W3CUserAuthentication identity);
}

public class ChatAuthenticationService(
    MongoClient mongoClient,
    IWebsiteBackendRepository websiteBackendRepository
) : MongoDbRepositoryBase(mongoClient), IChatAuthenticationService
{
    private readonly IWebsiteBackendRepository _websiteBackendRepository = websiteBackendRepository;

    // HARD CUTOVER (C2): the JWT decode is GONE — it happened once, at ticket mint. Here we only
    // ENRICH an already-proven identity snapshot (from the consumed ticket) with wb flair.
    public async Task<ChatUser> GetUserFromIdentity(W3CUserAuthentication identity)
    {
        try
        {
            var userDetails = await _websiteBackendRepository.GetChatDetails(identity.BattleTag);
            var chatColor = userDetails?.ChatColor;
            var chatIcons = userDetails?.ChatIcons ?? [];
            if (identity.IsAdmin)
            {
                chatColor = ChatColor.AdminColor;
                chatIcons = [ChatIcon.AdminIcon, .. chatIcons];
            }
            return new ChatUser(identity.BattleTag, identity.IsAdmin, userDetails?.ClanId, userDetails?.ProfilePicture, chatColor, chatIcons);
        }
        catch (Exception ex)
        {
            // Decision 11: the ticket ALREADY proved identity, so a wb outage must NOT fail a
            // proven-authenticated connect. Log and connect with a plain fallback — NEVER null (the
            // old GetUser returned null on failure → connect rejected; this is a deliberate
            // resilience improvement). Full flair is restored on the next successful enrichment.
            Log.Warning(ex, "Failed to enrich chat user {BattleTag} from wb — connecting with plain fallback", identity.BattleTag);
            return new ChatUser(identity.BattleTag, identity.IsAdmin, null, new ProfilePicture(), null, null);
        }
    }
}
