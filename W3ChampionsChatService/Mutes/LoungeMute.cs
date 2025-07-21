using System;

namespace W3ChampionsChatService.Mutes;

public class LoungeMuteRequest
{
    public string battleTag { get; set; }
    public string endDate { get; set; }
    public string author { get; set; }
    public string reason { get; set; }
    public bool isShadowBan { get; set; } = false;
}

public class LoungeMute : IIdentifiable
{
    public string Id => battleTag;
    public string battleTag { get; set; }
    public DateTime endDate { get; set; }
    public DateTime insertDate { get; set; }
    public string author { get; set; }
    public string reason { get; set; }
    public bool isShadowBan { get; set; } = false;
}
