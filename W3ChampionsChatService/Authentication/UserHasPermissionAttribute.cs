using System;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace W3ChampionsChatService.Authentication;

[AttributeUsage(AttributeTargets.Method)]
public class UserHasPermissionAttribute(EPermission permission) : Attribute, IFilterFactory
{
    public EPermission Permission { get; } = permission;
    public bool IsReusable => false;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider)
    {
        var filter = serviceProvider.GetService<UserHasPermissionFilter>();
        filter.Permission = Permission;
        return filter;
    }
}