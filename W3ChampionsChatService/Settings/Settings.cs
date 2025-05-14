namespace W3ChampionsChatService.Settings;

public class ChatSettings(string battleTag) : IIdentifiable
{
    public string Id => BattleTag;

    public string BattleTag { get; set; } = battleTag;
    public string DefaultChat { get; set; } = "W3C Lounge";

    public void Update(string defaultChat)
    {
        DefaultChat = defaultChat;
    }
}
