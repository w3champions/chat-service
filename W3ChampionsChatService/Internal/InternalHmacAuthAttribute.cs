using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace W3ChampionsChatService.Internal;

/// <summary>
/// Marks an <c>/internal/*</c> controller as requiring HMAC authentication and pins WHICH callers it
/// permits (least privilege). An <see cref="IFilterFactory"/> that resolves the DI-registered
/// <see cref="InternalHmacAuthFilter"/> and stamps the allow-list onto it — mirroring the repo's
/// <c>UserHasPermissionAttribute</c> precedent exactly (non-reusable, resolve-then-configure).
///
/// <para>Class-only usage: it attaches to the internal REST controllers (Task 9) and must NEVER touch the
/// SignalR hub or the moderation controllers, which live in the disjoint JWT/ticket auth realm.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class InternalHmacAuthAttribute(params InternalCaller[] allowedCallers) : Attribute, IFilterFactory
{
    /// <summary>The callers this endpoint permits — a valid signature from any other caller is rejected.</summary>
    public IReadOnlyList<InternalCaller> AllowedCallers { get; } = allowedCallers;

    public bool IsReusable => false;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider)
    {
        var filter = serviceProvider.GetRequiredService<InternalHmacAuthFilter>();
        filter.AllowedCallers = AllowedCallers;
        return filter;
    }
}
