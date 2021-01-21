using System;
using System.Threading.Tasks;
using MongoDB.Driver;
using W3ChampionsChatService.Authentication;

namespace W3ChampionsChatService.Chats
{
    public interface IChatAuthenticationService
    {
        Task<ChatUser> GetUser(string chatKey);
    }

    public class ChatAuthenticationService : MongoDbRepositoryBase, IChatAuthenticationService
    {
        private readonly IW3CAuthenticationService _authenticationService;
        private readonly IWebsiteBackendRepository _websiteBackendRepository;

        public ChatAuthenticationService(
            MongoClient mongoClient,
            IW3CAuthenticationService authenticationService,
            IWebsiteBackendRepository websiteBackendRepository
            ) : base(mongoClient)
        {
            _authenticationService = authenticationService;
            _websiteBackendRepository = websiteBackendRepository;
        }

        public async Task<ChatUser> GetUser(string chatKey)
        {
            try
            {
                var user = await _authenticationService.GetUserByToken(chatKey);
                if (user == null) return null;
                var userDetails = await _websiteBackendRepository.GetChatDetails(user.BattleTag);
                return new ChatUser(user.BattleTag, userDetails?.ClanId, userDetails?.ProfilePicture);
            }
            catch (Exception)
            {
                return null;
            }
        }

    }
}