namespace W3ChampionsChatService.Authentication;

public class ErrorResult(string error)
{
    public string Error { get; } = error;
}
