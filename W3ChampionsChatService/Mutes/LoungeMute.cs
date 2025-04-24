using System;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace W3ChampionsChatService.Mutes
{
    // Enum definition for Mute Types
    public enum MuteTypeEnum
    {
        Full,         // Cannot send any messages
        FriendsOnly   // Can only message friends
    }

    // Request DTO - might live elsewhere, but needs update
    public class LoungeMuteRequest
    {
        public string battleTag { get; set; }
        public string endDate { get; set; }
        public string author { get; set; }
        [BsonRepresentation(BsonType.String)] // Ensure DTO can receive string for ease of use
        public MuteTypeEnum MuteType { get; set; }
    }

    public class LoungeMute : IIdentifiable
    {
        public string Id => battleTag;
        public string battleTag { get; set; }
        public DateTime endDate { get; set; }
        public DateTime insertDate { get; set; }
        public string author { get; set; }
        
        [BsonRepresentation(BsonType.String)] // Store enum as string in DB
        public MuteTypeEnum MuteType { get; set; }
    }
}
