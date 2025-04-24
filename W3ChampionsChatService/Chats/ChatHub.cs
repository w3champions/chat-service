using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using W3ChampionsChatService.Mutes;
using W3ChampionsChatService.Settings;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;

// Assume this service exists or will be created
// It should handle storing/retrieving PMs with a 7-day TTL, possibly using IMemoryCache
public interface IPrivateMessageHistoryService
{
    Task AddMessage(string userA, string userB, ChatMessage message);
    Task<List<ChatMessage>> GetMessages(string userA, string userB, int limit = 50);
}

// Placeholder implementation (needs proper implementation using IMemoryCache)
public class InMemoryPrivateMessageHistoryService : IPrivateMessageHistoryService
{
    private readonly ILogger<InMemoryPrivateMessageHistoryService> _logger;
    public InMemoryPrivateMessageHistoryService(ILogger<InMemoryPrivateMessageHistoryService> logger) { _logger = logger; }
    public Task AddMessage(string userA, string userB, ChatMessage message) { 
        _logger.LogInformation($"(Placeholder) Storing PM between {userA} and {userB}");
        return Task.CompletedTask; 
    }
    public Task<List<ChatMessage>> GetMessages(string userA, string userB, int limit = 50) { 
        _logger.LogInformation($"(Placeholder) Getting PMs between {userA} and {userB}");
        return Task.FromResult(new List<ChatMessage>()); 
    }
}

[assembly:InternalsVisibleTo("W3ChampionsChatService.Tests")]
namespace W3ChampionsChatService.Chats
{
    public class ChatHub : Hub
    {
        private readonly IChatAuthenticationService _authenticationService;
        private readonly MuteRepository _muteRepository;
        private readonly SettingsRepository _settingsRepository;
        private readonly ConnectionMapping _connections;
        private readonly ChatHistory _chatHistory;
        private readonly IHttpContextAccessor _contextAccessor;
        private readonly IWebsiteBackendRepository _websiteBackendRepository;
        private readonly BlockRepository _blockRepository;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IMemoryCache _memoryCache;
        private readonly ILogger<ChatHub> _logger;
        private readonly string _websiteBackendUrl;
        private readonly string _internalApiSecret;
        private readonly IPrivateMessageHistoryService _pmHistoryService;

        // Key: ConnectionId, Value: Set<string> of declined BattleTags for this session
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, bool>> _sessionDeclines = new();

        public ChatHub(
            IChatAuthenticationService authenticationService,
            MuteRepository muteRepository,
            SettingsRepository settingsRepository,
            ConnectionMapping connections,
            ChatHistory chatHistory,
            IHttpContextAccessor contextAccessor,
            IWebsiteBackendRepository websiteBackendRepository,
            BlockRepository blockRepository,
            IHttpClientFactory httpClientFactory,
            IMemoryCache memoryCache,
            ILogger<ChatHub> logger,
            IPrivateMessageHistoryService pmHistoryService)
        {
            _authenticationService = authenticationService;
            _muteRepository = muteRepository;
            _settingsRepository = settingsRepository;
            _connections = connections;
            _chatHistory = chatHistory;
            _contextAccessor = contextAccessor;
            _websiteBackendRepository = websiteBackendRepository;
            _blockRepository = blockRepository;
            _httpClientFactory = httpClientFactory;
            _memoryCache = memoryCache;
            _logger = logger;
            _pmHistoryService = pmHistoryService;

            // TODO: Read these from IConfiguration properly
            _websiteBackendUrl = Environment.GetEnvironmentVariable("WEBSITE_BACKEND_URL") ?? "http://localhost:5000";
            _internalApiSecret = Environment.GetEnvironmentVariable("INTERNAL_API_SECRET") ?? "default-secret";
        }

        // Helper to get current user (avoids repetition)
        private ChatUser GetCurrentUser() => _connections.GetUser(Context.ConnectionId);
        private string GetCurrentUserBattleTag() => GetCurrentUser()?.BattleTag;

        public async Task SendMessage(string message)
        {
            var sender = GetCurrentUser();
            if (sender == null) return; // Should not happen if connected

            var trimmedMessage = message.Trim();
            if (string.IsNullOrEmpty(trimmedMessage)) return;

            var chatRoomId = _connections.GetRoom(Context.ConnectionId);
            if (string.IsNullOrEmpty(chatRoomId)) return; // User not in a room?

            // --- 1. Check Sender Mute Status ---
            var mute = await _muteRepository.GetMutedPlayer(sender.BattleTag);
            bool isMuted = mute != null && mute.endDate > DateTime.UtcNow;

            if (isMuted)
            {
                if (mute.MuteType == MuteTypeEnum.Full)
                {
                    await Clients.Caller.SendAsync("MuteStatus", new { type = "Full", endDate = mute.endDate });
                    return; 
                }

                if (mute.MuteType == MuteTypeEnum.FriendsOnly)
                {
                    // FriendsOnly mute restricts sending in non-PM contexts
                     await Clients.Caller.SendAsync("MuteStatus", new { type = "FriendsOnly", endDate = mute.endDate, message = "Cannot send in public/party channels while muted." });
                     return; 
                }
            }

            // --- 2. Process as Command or Normal Message ---
            var chatMessage = new ChatMessage(sender, trimmedMessage);
            if (await ProcessChatCommand(chatMessage))
            {
                return; 
            }

            // --- 3. Prepare to Send to Room Recipients ---
             // Check if chatRoomId corresponds to a non-volatile room before adding
             // For now, assume all rooms handled by SendMessage might have history
            _chatHistory.AddMessage(chatRoomId, chatMessage); 

            var recipientConnections = _connections.GetConnectionsOfRoom(chatRoomId);

            foreach (var recipientConnectionId in recipientConnections)
            {
                // Don't send message back to sender in public chat
                 if (recipientConnectionId == Context.ConnectionId) continue;

                var recipientUser = _connections.GetUser(recipientConnectionId);
                if (recipientUser == null) continue; 

                // --- 4. Check if Recipient Blocked Sender ---
                bool isBlockedByRecipient = await _blockRepository.IsBlocked(recipientUser.BattleTag, sender.BattleTag);

                // --- 5. Send tailored message ---
                object messageToSend;
                if (isBlockedByRecipient)
                {
                    messageToSend = new ChatMessageWithBlocked(chatMessage) { IsBlocked = true };
                    await Clients.Client(recipientConnectionId).SendAsync("ReceiveMessage", messageToSend);
                    _logger.LogInformation($"Message from {sender.BattleTag} to {recipientUser.BattleTag} in room {chatRoomId} flagged as blocked.");
                }
                else
                {
                    messageToSend = chatMessage;
                    await Clients.Client(recipientConnectionId).SendAsync("ReceiveMessage", messageToSend);
                }
            }
             _logger.LogInformation($"Message from {sender.BattleTag} processed for room {chatRoomId}.");
        }

        /// <summary>
        /// Processes chat commands and returns a boolean indicating whether a command was processed 
        /// or the message should be sent as a normal message.
        /// </summary>
        /// <param name="message">The chat message to process.</param>
        /// <returns>True if a command was processed, false otherwise.</returns>
        private async Task<bool> ProcessChatCommand(ChatMessage message)
        {
            if (!message.Message.StartsWith("/"))
            {
                return false;
            }
            
            var fakeSystemUser = message.User.GenerateFakeSystemUser();
            string messageToSend;

            if (message.Message.StartsWith("/w ") || message.Message.StartsWith("/whisper "))
            {
                 // Extract recipient and message content
                string[] parts = message.Message.Split(' ', 3);
                if (parts.Length >= 3)
                {
                    string recipientTag = parts[1];
                    string pmContent = parts[2];
                    // Call SendPrivateMessage instead of sending placeholder
                    await SendPrivateMessage(recipientTag, pmContent); 
                    return true; // Command handled by SendPrivateMessage logic
                }
                else {
                     messageToSend = "Invalid whisper format. Use /w <BattleTag> <message>";
                }
            }
             // TODO: Handle /r or /reply command by looking up last PM partner
            else if (message.Message.StartsWith("/r ") || message.Message.StartsWith("/reply ")) {
                 messageToSend = "Reply command coming soon!";
            }
            else
            {
                messageToSend = "Chat command not recognized.";
            }

            // Only send message back if whisper parsing failed or it was another command
            await Clients.Caller.SendAsync("ReceiveMessage", new ChatMessage(fakeSystemUser, messageToSend));
            return true; // Indicate command was processed (even if failed)
        }

        public override async Task OnConnectedAsync()
        {
            bool oauth = Environment.GetEnvironmentVariable("BNET_OAUTH") == "true";
            if (oauth)
            {
                var accessToken = _contextAccessor?.HttpContext?.Request.Query["access_token"];
                var user = await _authenticationService.GetUser(accessToken);
                if (user == null)
                {
                    await Clients.Caller.SendAsync("AuthorizationFailed");
                    Context.Abort();
                    return;
                }
                await LoginAsAuthenticated(user);
            }
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception exception)
        {
            var user = GetCurrentUser();
            var connectionId = Context.ConnectionId;
            if (user != null)
            {
                var chatRoom = _connections.GetRoom(connectionId);
                 _logger.LogInformation($"User disconnected: {user.BattleTag} ({connectionId}) from room {chatRoom}");
                _connections.Remove(connectionId);
                // Clean up session declines for this connection
                _sessionDeclines.TryRemove(connectionId, out _);
                await Groups.RemoveFromGroupAsync(connectionId, chatRoom); 
                await Clients.Group(chatRoom).SendAsync("UserLeft", user);
            }
            else {
                 _logger.LogWarning($"Disconnect event for unknown ConnectionId: {connectionId}");
            }
            // Clean up session declines even if user was null?
             _sessionDeclines.TryRemove(connectionId, out _);

            await base.OnDisconnectedAsync(exception);
        }

        public async Task SwitchRoom(string chatRoom)
        {
            var oldRoom = _connections.GetRoom(Context.ConnectionId);
            var user = _connections.GetUser(Context.ConnectionId);

            _connections.Remove(Context.ConnectionId);
            _connections.Add(Context.ConnectionId, chatRoom, user);

            await Groups.RemoveFromGroupAsync(Context.ConnectionId, oldRoom);
            await Groups.AddToGroupAsync(Context.ConnectionId, chatRoom);

            var usersOfRoom = _connections.GetUsersOfRoom(chatRoom);
            await Clients.Group(oldRoom).SendAsync("UserLeft", user);
            await Clients.Group(chatRoom).SendAsync("UserEntered", user);
            await Clients.Caller.SendAsync("StartChat", usersOfRoom, _chatHistory.GetMessages(chatRoom), chatRoom);

            var memberShip = await _settingsRepository.Load(user.BattleTag) ?? new ChatSettings(user.BattleTag);
            memberShip.Update(chatRoom);
            await _settingsRepository.Save(memberShip);
        }

        // used when OAuth is off, invoked from ingame-client
        public async Task LoginAs(string battleTag, bool isAdmin)
        {
            var userDetails = await _websiteBackendRepository.GetChatDetails(battleTag);
            var chatUser = new ChatUser(battleTag, isAdmin, userDetails?.ClanId, userDetails?.ProfilePicture);
            await LoginAsAuthenticated(chatUser);
        }

        internal async Task LoginAsAuthenticated(ChatUser user)
        {
            var memberShip = await _settingsRepository.Load(user.BattleTag) ?? new ChatSettings(user.BattleTag);

            var mute = await _muteRepository.GetMutedPlayer(user.BattleTag);

            if (mute != null && DateTime.Compare(mute.endDate, DateTime.UtcNow) > 0)
            {
                await Clients.Caller.SendAsync("PlayerBannedFromChat", mute);
                Context.Abort();
            }
            else
            {
                _connections.Add(Context.ConnectionId, memberShip.DefaultChat, user);
                await Groups.AddToGroupAsync(Context.ConnectionId, memberShip.DefaultChat);
                var usersOfRoom = _connections.GetUsersOfRoom(memberShip.DefaultChat);
                await Clients.Group(memberShip.DefaultChat).SendAsync("UserEntered", user);
                await Clients.Caller.SendAsync("StartChat", usersOfRoom, _chatHistory.GetMessages(memberShip.DefaultChat), memberShip.DefaultChat);
            }
        }

        public async Task UpdateUserProfilePicture(string chatRoom, ProfilePicture profilePicture)
        {
            var user = _connections.GetUser(Context.ConnectionId);
            user.ProfilePicture = profilePicture;
            await Clients.Group(chatRoom).SendAsync("UserUpdated", user);
        }

        // --- SendPrivateMessage Implementation ---
        [HubMethodName("SendPrivateMessage")] // Explicit name can help client mapping
        public async Task SendPrivateMessage(string recipientBattleTag, string message)
        {
             var sender = GetCurrentUser();
             if (sender == null || string.IsNullOrEmpty(recipientBattleTag) || string.IsNullOrEmpty(message)) 
             {
                 _logger.LogWarning($"SendPrivateMessage validation failed: sender={sender?.BattleTag}, recipient={recipientBattleTag}, message empty={string.IsNullOrEmpty(message)}");
                 return; // Basic validation
             }
              
             var trimmedMessage = message.Trim();
             if (string.IsNullOrEmpty(trimmedMessage)) return;

             _logger.LogInformation($"Processing PM from {sender.BattleTag} to {recipientBattleTag}");

            // 0. Cannot PM yourself
             if (string.Equals(sender.BattleTag, recipientBattleTag, StringComparison.OrdinalIgnoreCase)) {
                 await Clients.Caller.SendAsync("ReceiveMessage", new ChatMessage(sender.GenerateFakeSystemUser(), "You cannot send a private message to yourself."));
                 return;
             }

             // 1. Check Sender Mute Status
             var senderMute = await _muteRepository.GetMutedPlayer(sender.BattleTag);
             bool isSenderMuted = senderMute != null && senderMute.endDate > DateTime.UtcNow;
             if (isSenderMuted) {
                 if (senderMute.MuteType == MuteTypeEnum.Full) {
                     await Clients.Caller.SendAsync("MuteStatus", new { type = "Full", endDate = senderMute.endDate, message = "Cannot send messages while fully muted." });
                     _logger.LogWarning($"PM blocked: Sender {sender.BattleTag} is fully muted.");
                     return;
                 }
                 if (senderMute.MuteType == MuteTypeEnum.FriendsOnly) {
                     bool areFriends = await AreFriends(sender.BattleTag, recipientBattleTag);
                     if (!areFriends) {
                        await Clients.Caller.SendAsync("MuteStatus", new { type = "FriendsOnly", endDate = senderMute.endDate, message = $"Cannot send PM to non-friend {recipientBattleTag} while muted." });
                         _logger.LogWarning($"PM blocked: Sender {sender.BattleTag} is friends-only muted, recipient {recipientBattleTag} is not a friend.");
                        return;
                     }
                     // If friends, proceed
                 }
             }

             // 2. Check if Sender Blocked Recipient
             bool senderBlockedRecipient = await _blockRepository.IsBlocked(sender.BattleTag, recipientBattleTag);
             if (senderBlockedRecipient) {
                 await Clients.Caller.SendAsync("PrivateMessageFailed", new { recipientId = recipientBattleTag, reason = "blocked_by_sender" });
                 _logger.LogInformation($"PM from {sender.BattleTag} to {recipientBattleTag} failed: Sender blocked recipient.");
                 return;
             }

             // 3. Check Recipient Online Status
             var recipientConnection = _connections.GetConnection(recipientBattleTag);
             if (recipientConnection == null) {
                 await Clients.Caller.SendAsync("PrivateMessageFailed", new { recipientId = recipientBattleTag, reason = "offline" });
                 _logger.LogInformation($"PM from {sender.BattleTag} to {recipientBattleTag} failed: Recipient offline.");
                 return;
             }
             var recipientUser = _connections.GetUser(recipientConnection.ConnectionId); 
              if (recipientUser == null) { // Consistency check
                  _logger.LogError($"Recipient {recipientBattleTag} found in connections but user object is null. ConnectionId: {recipientConnection.ConnectionId}");
                  await Clients.Caller.SendAsync("PrivateMessageFailed", new { recipientId = recipientBattleTag, reason = "internal_error" });
                  return;
              }


             // 4. Check if Recipient Blocked Sender
             bool recipientBlockedSender = await _blockRepository.IsBlocked(recipientBattleTag, sender.BattleTag);
             if (recipientBlockedSender) {
                  await Clients.Caller.SendAsync("PrivateMessageFailed", new { recipientId = recipientBattleTag, reason = "blocked_by_recipient" });
                  _logger.LogInformation($"PM from {sender.BattleTag} to {recipientBattleTag} failed: Recipient blocked sender.");
                  return;
             }

             // 5. Check Friendship Status
             bool areUsersFriends = await AreFriends(sender.BattleTag, recipientBattleTag);

             // 6. Handle Non-Friends (Check Session Decline & Send Notification)
             if (!areUsersFriends) {
                 bool recipientDeclinedSession = false;
                 if (_sessionDeclines.TryGetValue(recipientConnection.ConnectionId, out var declines) && declines.ContainsKey(sender.BattleTag.ToLowerInvariant())) {
                      recipientDeclinedSession = true;
                 }

                 if (recipientDeclinedSession) {
                     await Clients.Caller.SendAsync("PrivateMessageFailed", new { recipientId = recipientBattleTag, reason = "recipient_declined_session" });
                     _logger.LogInformation($"PM from {sender.BattleTag} to {recipientBattleTag} failed: Recipient declined for this session.");
                     return;
                 }

                 // Send notification instead of message
                 await Clients.Client(recipientConnection.ConnectionId).SendAsync("PmNotification", new {
                     senderId = sender.BattleTag,
                     senderName = sender.BattleTag, // Use BattleTag as name for now
                     timestamp = DateTime.UtcNow
                 });
                 _logger.LogInformation($"Sent PM notification from {sender.BattleTag} to {recipientBattleTag}.");
                 await Clients.Caller.SendAsync("ReceiveMessage", new ChatMessage(sender.GenerateFakeSystemUser(), $"Sent a message request to {recipientBattleTag}. They need to accept before seeing your message."));
                 return; 
             }

             // 7. Send PM to Friend
             var chatMessage = new ChatMessage(sender, trimmedMessage);
             await Clients.Client(recipientConnection.ConnectionId).SendAsync("PrivateMessage", chatMessage);
             // Send confirmation back to sender including the message object
             await Clients.Caller.SendAsync("PrivateMessageSent", new { recipientId = recipientBattleTag, message = chatMessage }); 

             // 8. Store in History
             await _pmHistoryService.AddMessage(sender.BattleTag, recipientBattleTag, chatMessage);
             _logger.LogInformation($"Sent PM from {sender.BattleTag} to {recipientBattleTag} successfully.");
        }

        // --- HandlePmResponse Implementation ---
        [HubMethodName("HandlePmResponse")] // Explicit name
        public async Task HandlePmResponse(string senderBattleTag, string responseType)
        {
            var recipient = GetCurrentUser(); // The user responding
            var recipientConnectionId = Context.ConnectionId;
            if (recipient == null || string.IsNullOrEmpty(senderBattleTag) || string.IsNullOrEmpty(responseType)) return;

            _logger.LogInformation($"{recipient.BattleTag} responding '{responseType}' to PM request from {senderBattleTag}");

            var senderConnection = _connections.GetConnection(senderBattleTag); // Check if original sender is still online

            switch(responseType.ToLowerInvariant())
            {
                case "accept":
                    if (_sessionDeclines.TryGetValue(recipientConnectionId, out var declines)) {
                        declines.TryRemove(senderBattleTag.ToLowerInvariant(), out _);
                    }
                    _logger.LogInformation($"{recipient.BattleTag} accepted PM session with {senderBattleTag}.");
                    await Clients.Caller.SendAsync("PmSessionAccepted", new { partnerId = senderBattleTag });
                    if (senderConnection != null) {
                         await Clients.Client(senderConnection.ConnectionId).SendAsync("PmRequestAccepted", new { partnerId = recipient.BattleTag }); // Notify sender
                    }
                    // Optionally: Send recent history
                    var history = await _pmHistoryService.GetMessages(senderBattleTag, recipient.BattleTag);
                    await Clients.Caller.SendAsync("PrivateMessageHistory", new { partnerId = senderBattleTag, messages = history });
                    break;

                case "decline":
                    var declineSet = _sessionDeclines.GetOrAdd(recipientConnectionId, _ => new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase));
                    declineSet.TryAdd(senderBattleTag.ToLowerInvariant(), true);
                     _logger.LogInformation($"{recipient.BattleTag} declined PM session with {senderBattleTag}.");
                    await Clients.Caller.SendAsync("PmSessionDeclined", new { partnerId = senderBattleTag });
                    if (senderConnection != null) {
                         await Clients.Client(senderConnection.ConnectionId).SendAsync("PmRequestDeclined", new { partnerId = recipient.BattleTag });
                    }
                    break;

                case "block":
                    try {
                         // Ensure block is added via repository
                         var block = new ChatBlock(recipient.BattleTag, senderBattleTag);
                         await _blockRepository.AddBlock(block); 
                         _logger.LogInformation($"{recipient.BattleTag} blocked {senderBattleTag}.");
                         await Clients.Caller.SendAsync("BlockUserSuccess", new { blockedUserId = senderBattleTag });
                          // Silently fail future messages from sender now
                    } catch(Exception ex) {
                         _logger.LogError(ex, $"Failed to block {senderBattleTag} for {recipient.BattleTag}.");
                         await Clients.Caller.SendAsync("BlockUserFailed", new { userId = senderBattleTag, error = "Failed to apply block." });
                    }
                    break;

                default:
                     _logger.LogWarning($"Received unknown PM response type '{responseType}' from {recipient.BattleTag} for {senderBattleTag}.");
                    break;
            }
        }

        // --- Helper method for Friend Check (Example) ---
        private async Task<bool> AreFriends(string userA, string userB)
        {
            if (string.Equals(userA, userB, StringComparison.OrdinalIgnoreCase)) return true; 

            // Normalize cache key
            var users = new List<string> { userA.ToLowerInvariant(), userB.ToLowerInvariant() };
            users.Sort(StringComparer.OrdinalIgnoreCase);
            var cacheKey = $"FRIEND_CHECK_{users[0]}_{users[1]}";
            
            if (_memoryCache.TryGetValue(cacheKey, out bool areFriends))
            {
                _logger.LogDebug($"Friend check cache hit for {userA}/{userB}: {areFriends}");
                return areFriends;
            }

             _logger.LogDebug($"Friend check cache miss for {userA}/{userB}. Calling backend.");
            try
            {
                var client = _httpClientFactory.CreateClient("WebsiteBackendClient"); // Use named client if configured
                var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"{_websiteBackendUrl}/api/friends/internal/check?userA={Uri.EscapeDataString(userA)}&userB={Uri.EscapeDataString(userB)}");
                request.Headers.Add("X-Internal-Secret", _internalApiSecret);

                var response = await client.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    bool result = JsonSerializer.Deserialize<bool>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); 
                    
                    _memoryCache.Set(cacheKey, result, TimeSpan.FromMinutes(5)); 
                    _logger.LogInformation($"Friend check successful for {userA}/{userB}: {result}");
                    return result;
                }
                else
                {
                     _logger.LogError($"Friend check API call failed for {userA}/{userB}. Status: {response.StatusCode}, Reason: {response.ReasonPhrase}");
                    return false; 
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception during friend check API call for {userA}/{userB}");
                return false; 
            }
        }

        // --- DTO for tailored message ---
        private class ChatMessageWithBlocked : ChatMessage
        {
             public bool IsBlocked { get; set; }

             // Constructor needs to handle base class construction correctly
             public ChatMessageWithBlocked(ChatMessage original) : base(original.User, original.Message) 
             {
                TimeStamp = original.TimeStamp; 
                // Add other properties if needed
             }
             // Required for deserialization if needed, ensure base properties are handled
             public ChatMessageWithBlocked() : base(null, null) {} 
        }
    }
}
