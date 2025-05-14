using System;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace W3ChampionsChatService.Authentication;

[AttributeUsage(AttributeTargets.Method)]
public class CheckIfBattleTagIsAdmin : Attribute, IFilterFactory
{
    public bool IsReusable => false;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider)
    {
        return serviceProvider.GetService<CheckIfBattleTagIsAdminFilter>();
    }
}
