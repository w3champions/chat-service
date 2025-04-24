using Microsoft.AspNetCore.Mvc;
using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using W3ChampionsChatService.Authentication;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System.Linq;

namespace W3ChampionsChatService.Chats
{
    // DTOs (consider placing in a Contracts project/folder)
    public class PartyChatCreateDto
    {
        public string PartyId { get; set; }
        public List<string> InitialMembers { get; set; }
    }

    public class PartyMemberUpdateDto // Used for adding members
    {
         // No body needed for PUT, info is in URL
    }

    [ApiController]
    [Route("api/v1/internal/party-chats")]
    public class PartyChatController : ControllerBase
    {
        private readonly IHubContext<ChatHub> _hubContext;
        private readonly ConnectionMapping _connectionMapping;
        private readonly ILogger<PartyChatController> _logger;
        private readonly ChatHistory _chatHistory;

        public PartyChatController(
            IHubContext<ChatHub> hubContext,
            ConnectionMapping connectionMapping,
            ChatHistory chatHistory,
            ILogger<PartyChatController> logger)
        {
            _hubContext = hubContext;
            _connectionMapping = connectionMapping;
            _chatHistory = chatHistory;
            _logger = logger;
        }

        private bool IsAuthorizedInternal(HttpRequest request)
        {
            var internalSecret = Environment.GetEnvironmentVariable("INTERNAL_API_SECRET") ?? "default-secret";
            return request.Headers.TryGetValue("X-Internal-Secret", out var secret) && 
                   string.Equals(secret.FirstOrDefault(), internalSecret, StringComparison.OrdinalIgnoreCase);
        }

        private string GetGroupName(string partyId) => $"Party_{partyId}";

        [HttpPost]
        public async Task<IActionResult> CreatePartyChat([FromBody] PartyChatCreateDto partyChatDto)
        {
            if (!IsAuthorizedInternal(Request)) return Unauthorized();
            if (string.IsNullOrEmpty(partyChatDto.PartyId) || partyChatDto.InitialMembers == null || !partyChatDto.InitialMembers.Any())
            {
                return BadRequest("PartyId and at least one InitialMember are required.");
            }

            var groupName = GetGroupName(partyChatDto.PartyId);
            _logger.LogInformation($"Creating party chat group: {groupName}");

            var onlineMembers = new List<ChatUser>();
            var connectionTasks = new List<Task>();

            foreach (var memberBattleTag in partyChatDto.InitialMembers)
            {
                var connection = _connectionMapping.GetConnection(memberBattleTag);
                if (connection != null)
                {
                    onlineMembers.Add(connection.User);
                    connectionTasks.Add(_hubContext.Groups.AddToGroupAsync(connection.ConnectionId, groupName));
                     _logger.LogInformation($"Adding {memberBattleTag} ({connection.ConnectionId}) to group {groupName}");
                }
                else
                {
                     _logger.LogWarning($"Could not find online connection for initial party member {memberBattleTag} for party {partyChatDto.PartyId}");
                }
            }
            
            // Wait for all members to be added to the group
            await Task.WhenAll(connectionTasks);
            
            // Send initial state to members after group setup
             var initialMessages = _chatHistory.GetMessages(groupName); // Get history if needed (likely empty initially)
             var initialStatePayload = new { 
                 users = onlineMembers, 
                 messages = initialMessages, 
                 chatRoom = groupName // Use group name as chatRoom identifier
             };

            // Notify each member they started/joined this specific chat
            foreach(var onlineMember in onlineMembers)
            {
                var conn = _connectionMapping.GetConnection(onlineMember.BattleTag);
                if(conn != null)
                {
                    await _hubContext.Clients.Client(conn.ConnectionId).SendAsync("StartChat", initialStatePayload.users, initialStatePayload.messages, initialStatePayload.chatRoom);
                }
            }

            _logger.LogInformation($"Party chat group {groupName} created and initial state sent.");
            return Ok();
        }

        [HttpPut("{partyId}/members/{battleTag}")]
        public async Task<IActionResult> AddPartyMember(string partyId, string battleTag)
        {
            if (!IsAuthorizedInternal(Request)) return Unauthorized();
             if (string.IsNullOrEmpty(partyId) || string.IsNullOrEmpty(battleTag))
            {
                return BadRequest("PartyId and BattleTag are required.");
            }

            var groupName = GetGroupName(partyId);
            var connection = _connectionMapping.GetConnection(battleTag);

            if (connection != null)
            {
                await _hubContext.Groups.AddToGroupAsync(connection.ConnectionId, groupName);
                _logger.LogInformation($"Added {battleTag} ({connection.ConnectionId}) to group {groupName}");
                
                // Notify the new member they joined and send current state
                var usersOfRoom = _connectionMapping.GetUsersOfRoom(groupName); // Get current users AFTER adding
                var messagesOfRoom = _chatHistory.GetMessages(groupName);
                await _hubContext.Clients.Client(connection.ConnectionId).SendAsync("StartChat", usersOfRoom, messagesOfRoom, groupName);

                // Notify existing members that a new user entered
                await _hubContext.Clients.GroupExcept(groupName, connection.ConnectionId).SendAsync("UserEntered", connection.User);
            }
            else
            {
                 _logger.LogWarning($"Could not find online connection for {battleTag} to add to party {partyId}");
            }

            return Ok();
        }

        [HttpDelete("{partyId}/members/{battleTag}")]
        public async Task<IActionResult> RemovePartyMember(string partyId, string battleTag)
        {
            if (!IsAuthorizedInternal(Request)) return Unauthorized();
             if (string.IsNullOrEmpty(partyId) || string.IsNullOrEmpty(battleTag))
            {
                return BadRequest("PartyId and BattleTag are required.");
            }

            var groupName = GetGroupName(partyId);
            var connection = _connectionMapping.GetConnection(battleTag);
            var user = connection?.User; // Get user info before potentially removing connection

            if (connection != null && user != null)
            {
                // Notify group *before* removing the user
                await _hubContext.Clients.GroupExcept(groupName, connection.ConnectionId).SendAsync("UserLeft", user);

                await _hubContext.Groups.RemoveFromGroupAsync(connection.ConnectionId, groupName);
                _logger.LogInformation($"Removed {battleTag} ({connection.ConnectionId}) from group {groupName}");
                
                // Optionally notify the user they were removed
                // await _hubContext.Clients.Client(connection.ConnectionId).SendAsync("RemovedFromPartyChat", new { partyId });
            }
             else
            {
                 _logger.LogWarning($"Could not find online connection for {battleTag} to remove from party {partyId}");
            }

            return Ok();
        }

        [HttpDelete("{partyId}")]
        public async Task<IActionResult> DeletePartyChat(string partyId)
        {
            if (!IsAuthorizedInternal(Request)) return Unauthorized();
            if (string.IsNullOrEmpty(partyId)) return BadRequest("PartyId is required.");

            var groupName = GetGroupName(partyId);
            _logger.LogInformation($"Disbanding party chat group: {groupName}");

            // Get connections *before* potentially clearing history or other state
            var connectionsInGroup = _connectionMapping.GetConnectionsOfRoom(groupName);

            // Notify members they are being removed/chat disbanded
            var tasks = connectionsInGroup.Select(connectionId => 
                 _hubContext.Clients.Client(connectionId).SendAsync("PartyChatDisbanded", new { chatRoomId = groupName })
                 // We don't explicitly remove from group, disconnecting/switching room handles it
                 // Groups.RemoveFromGroupAsync(connectionId, groupName) 
            ).ToList();

            try {
                await Task.WhenAll(tasks);
            } catch(Exception ex) {
                 _logger.LogError(ex, $"Error notifying clients about party chat disband for {groupName}");
            }

            // Optional: Clear chat history for the disbanded group
            _chatHistory.ClearHistory(groupName);
             _logger.LogInformation($"Cleared chat history for disbanded group {groupName}");

            return Ok();
        }
    }
} 