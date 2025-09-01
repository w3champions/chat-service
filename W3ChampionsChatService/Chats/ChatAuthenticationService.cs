using System;
using System.Threading.Tasks;
using MongoDB.Driver;
using W3ChampionsChatService.Authentication;
using Serilog;

namespace W3ChampionsChatService.Chats;

public interface IChatAuthenticationService
{
    Task<ChatUser> GetUser(string chatKey);
}

public class ChatAuthenticationService(
    MongoClient mongoClient,
    IW3CAuthenticationService authenticationService,
    IWebsiteBackendRepository websiteBackendRepository
) : MongoDbRepositoryBase(mongoClient), IChatAuthenticationService
{
    private readonly IW3CAuthenticationService _authenticationService = authenticationService;
    private readonly IWebsiteBackendRepository _websiteBackendRepository = websiteBackendRepository;

    public async Task<ChatUser> GetUser(string chatKey)
    {
        try
        {
            var user = _authenticationService.GetUserByToken(chatKey);
            if (user == null) return null;
            var userDetails = await _websiteBackendRepository.GetChatDetails(user.BattleTag);
            var chatColor = userDetails?.ChatColor;
            var chatIcons = userDetails?.ChatIcons ?? [];
            if (user.IsAdmin)
            {
                chatColor = ChatColor.AdminColor;
                chatIcons = [ChatIcon.AdminIcon, .. chatIcons];
            }
            return new ChatUser(user.BattleTag, user.IsAdmin, userDetails?.ClanId, userDetails?.ProfilePicture, chatColor, chatIcons);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error getting user by token");
            return null;
        }
    }
}
