﻿namespace W3ChampionsChatService.Settings
{
    public class ChatSettings : IIdentifiable
    {
        public ChatSettings(string battleTag)
        {
            BattleTag = battleTag;
            DefaultChat = "W3C Lounge";
        }

        public string Id => BattleTag;

        public string BattleTag { get; set; }
        public string DefaultChat { get; set; }

        public void Update(string defaultChat)
        {
            DefaultChat = defaultChat;
        }
    }
}