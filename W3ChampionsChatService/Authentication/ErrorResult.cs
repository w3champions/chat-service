namespace W3ChampionsChatService.Authentication
{
    public class ErrorResult
    {
        public string Error { get; }

        public ErrorResult(string error)
        {
            Error = error;
        }
    }
}
