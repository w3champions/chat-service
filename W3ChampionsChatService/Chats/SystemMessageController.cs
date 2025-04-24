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
    // DTO (consider placing in a Contracts project/folder)
    public class SystemBroadcastDto
    {
        public string ChatRoomId { get; set; }
        public string MessageKey { get; set; }
        public Dictionary<string, object> MessageParams { get; set; }
        public bool IsVolatile { get; set; }
    }

    // Define a DTO for the SignalR payload
    public class SystemMessagePayload
    {
        public string RoomId { get; set; }
        public string MessageKey { get; set; }
        public Dictionary<string, object> MessageParams { get; set; }
        public DateTime Timestamp { get; set; }
        // Note: IsVolatile is handled server-side for history, 
        // client might not need it unless it maintains its own volatile buffer.
    }

    [ApiController]
    [Route("api/v1/system")]
    public class SystemMessageController : ControllerBase
    {
        private readonly IHubContext<ChatHub> _hubContext;
        private readonly ConnectionMapping _connectionMapping;
        private readonly ChatHistory _chatHistory;
        private readonly ILogger<SystemMessageController> _logger;

        public SystemMessageController(
            IHubContext<ChatHub> hubContext,
            ConnectionMapping connectionMapping,
            ChatHistory chatHistory,
            ILogger<SystemMessageController> logger)
        {
             _hubContext = hubContext;
             _connectionMapping = connectionMapping;
             _chatHistory = chatHistory;
             _logger = logger;
        }

        private bool IsAuthorizedInternal(HttpRequest request)
        {
            var internalSecret = Environment.GetEnvironmentVariable("INTERNAL_API_SECRET") ?? "default-secret";
            // Use case-insensitive comparison for header value
            return request.Headers.TryGetValue("X-Internal-Secret", out var secret) && 
                   string.Equals(secret.FirstOrDefault(), internalSecret, StringComparison.OrdinalIgnoreCase);
        }

        [HttpPost("broadcast")]
        public async Task<IActionResult> BroadcastSystemMessage([FromBody] SystemBroadcastDto broadcastDto)
        {
            if (!IsAuthorizedInternal(Request)) 
            { 
                _logger.LogWarning("Unauthorized system broadcast attempt.");
                return Unauthorized(); 
            }

            if (string.IsNullOrEmpty(broadcastDto.ChatRoomId) || string.IsNullOrEmpty(broadcastDto.MessageKey))
            {
                return BadRequest("ChatRoomId and MessageKey are required.");
            }

            _logger.LogInformation($"Broadcasting system message Key='{broadcastDto.MessageKey}' to Room='{broadcastDto.ChatRoomId}', Volatile={broadcastDto.IsVolatile}");

            var payload = new SystemMessagePayload
            {
                 RoomId = broadcastDto.ChatRoomId, 
                 MessageKey = broadcastDto.MessageKey,
                 MessageParams = broadcastDto.MessageParams ?? new Dictionary<string, object>(),
                 Timestamp = DateTime.UtcNow
            };

            if (!broadcastDto.IsVolatile)
            {
                // Ensure Message class has required fields: IsSystemMessage, SystemMessageKey, SystemMessageParams
                var systemUser = new ChatUser("System", true, null, null); 
                var historyMessage = new Message(systemUser, $"System Message: {broadcastDto.MessageKey}")
                {
                     TimeStamp = payload.Timestamp, // Match timestamp
                     IsSystemMessage = true, 
                     SystemMessageKey = broadcastDto.MessageKey,
                     SystemMessageParams = broadcastDto.MessageParams
                };

                _chatHistory.AddMessage(broadcastDto.ChatRoomId, historyMessage);
                _logger.LogInformation($"Stored non-volatile system message for Room='{broadcastDto.ChatRoomId}'");
            }

            var connectionsInRoom = _connectionMapping.GetConnectionsOfRoom(broadcastDto.ChatRoomId);

            if (!connectionsInRoom.Any())
            {
                 _logger.LogWarning($"No connections found for room {broadcastDto.ChatRoomId} to broadcast system message.");
                 return Ok("No connections found in room.");
            }

            _logger.LogInformation($"Sending system message to {connectionsInRoom.Count} connection(s) in room {broadcastDto.ChatRoomId}.");

            // Send to all connections individually
            var tasks = connectionsInRoom.Select(connectionId => 
                _hubContext.Clients.Client(connectionId).SendAsync("system_message", payload)
            ).ToList();

            try
            {
                 await Task.WhenAll(tasks);
            }
            catch(Exception ex)
            {
                _logger.LogError(ex, "Error occurred while broadcasting system message to clients.");
                // Still return Ok as the request was processed, but log the failure
            }

            return Ok();
        }
    }
} 