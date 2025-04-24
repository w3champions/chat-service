using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace W3ChampionsChatService.Chats
{
    // Interface definition (can be moved to a separate file)
    public interface IPrivateMessageHistoryService
    {
        Task AddMessage(string userA, string userB, Message message);
        Task<List<Message>> GetMessages(string userA, string userB, int limit = 50);
    }

    public class InMemoryPrivateMessageHistoryService : IPrivateMessageHistoryService
    {
        private readonly IMemoryCache _cache;
        private readonly ILogger<InMemoryPrivateMessageHistoryService> _logger;
        private readonly TimeSpan _historyDuration = TimeSpan.FromDays(7);

        public InMemoryPrivateMessageHistoryService(
            IMemoryCache cache,
            ILogger<InMemoryPrivateMessageHistoryService> logger)
        {
            _cache = cache;
            _logger = logger;
        }

        private string GetCacheKey(string userA, string userB)
        {
            // Ensure consistent key regardless of user order
            var users = new List<string> { userA.ToLowerInvariant(), userB.ToLowerInvariant() };
            users.Sort(StringComparer.OrdinalIgnoreCase);
            return $"PM_HISTORY_{users[0]}_{users[1]}";
        }

        public Task AddMessage(string userA, string userB, Message message)
        {
            var cacheKey = GetCacheKey(userA, userB);
            
            // Use GetOrCreate to handle initialization and concurrent access safely
            var history = _cache.GetOrCreate(cacheKey, entry =>
            {
                _logger.LogInformation($"Creating new PM history cache entry for key: {cacheKey}");
                entry.SetSlidingExpiration(_historyDuration); // Keep cache entry alive as long as accessed
                return new List<Message>();
            });

            // Add message to the list (consider thread safety if list modification needs it, though GetOrCreate helps)
            lock (history) // Lock the list during modification
            {
                 history.Add(message);
                 // Optional: Trim history if it exceeds a max length
                 // if (history.Count > 1000) { history.RemoveRange(0, history.Count - 1000); } 
            }

             // Re-set the cache entry to update its contents (and potentially extend expiration if Sliding was used)
             _cache.Set(cacheKey, history, new MemoryCacheEntryOptions().SetSlidingExpiration(_historyDuration));

            _logger.LogDebug($"Added PM to history cache for key: {cacheKey}");
            return Task.CompletedTask;
        }

        public Task<List<Message>> GetMessages(string userA, string userB, int limit = 50)
        {
            var cacheKey = GetCacheKey(userA, userB);
            
            if (_cache.TryGetValue(cacheKey, out List<Message> history))
            {
                 _logger.LogDebug($"Retrieved PM history from cache for key: {cacheKey}");
                 // Return a copy or a portion of the history
                 lock(history) // Lock during access
                 {
                    // Get the most recent messages up to the limit
                    return Task.FromResult(history.Skip(Math.Max(0, history.Count - limit)).ToList());
                 }
            }
            else
            {
                 _logger.LogDebug($"No PM history found in cache for key: {cacheKey}");
                return Task.FromResult(new List<Message>()); // Return empty list if no history
            }
        }
    }
} 