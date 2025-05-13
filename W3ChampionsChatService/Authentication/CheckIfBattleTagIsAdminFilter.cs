using System.Linq;
using System.Threading.Tasks;
using System.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace W3ChampionsChatService.Authentication;

public class CheckIfBattleTagIsAdminFilter(IW3CAuthenticationService authService) : IAsyncActionFilter
{
    private readonly IW3CAuthenticationService _authService = authService;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var queryString = HttpUtility.ParseQueryString(context.HttpContext.Request.QueryString.Value);
        if (queryString.AllKeys.Contains("authorization"))
        {
            var auth = queryString["authorization"];
            var res = _authService.GetUserByToken(auth);
            if (
                res != null
                && !string.IsNullOrEmpty(res.BattleTag)
                && res.IsAdmin)
            {
                context.ActionArguments["battleTag"] = res.BattleTag;
                await next.Invoke();
            }
        }

        var unauthorizedResult = new UnauthorizedObjectResult(new ErrorResult("Sorry H4ckerb0i"));
        context.Result = unauthorizedResult;
    }
}
