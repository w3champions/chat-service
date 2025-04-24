using System;
using MongoDB.Bson.Serialization.Attributes;

namespace W3ChampionsChatService.Chats
{
    public class ChatBlock
    {
        [BsonId] // Use Blocker_Blocked as the compound key
        public string Id { get; set; }
        public string BlockerBattleTag { get; set; }
        public string BlockedBattleTag { get; set; }
        public DateTime Timestamp { get; set; }

        public static string CreateId(string blocker, string blocked)
        {
            return $"{blocker.ToLowerInvariant()}_{blocked.ToLowerInvariant()}";
        }

        public ChatBlock(string blockerBattleTag, string blockedBattleTag)
        {
            BlockerBattleTag = blockerBattleTag;
            BlockedBattleTag = blockedBattleTag;
            Id = CreateId(BlockerBattleTag, BlockedBattleTag);
            Timestamp = DateTime.UtcNow;
        }

        // Parameterless constructor for MongoDB deserialization
        public ChatBlock() {}
    }
} 