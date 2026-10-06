using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Helpers;
using Jellyfin.Plugin.JellyPlay.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.JellyPlay.Api;

/// <summary>
/// Rate limit an authenticated mutating route. The key is the plugin user id
/// (anonymous intake routes key on client identity instead — the Seerr
/// webhook keeps its own inline policy). The 429 body goes through the
/// plugin's camelCase gate ({error: "rate-limited"}), per docs/CONTRACT.md.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RateLimitAttribute : TypeFilterAttribute
{
    /// <param name="limiterType">A DI-registered <see cref="RateLimiter"/> subtype owning the window/budget.</param>
    /// <param name="keyPrefix">Stable key namespace ("settings", "broadcast") — combined with the caller's user id.</param>
    public RateLimitAttribute(Type limiterType, string keyPrefix)
        : base(typeof(RateLimitFilter))
    {
        Arguments = new object[] { limiterType, keyPrefix };
    }
}

/// <summary>Implementation behind <see cref="RateLimitAttribute"/>.</summary>
public sealed class RateLimitFilter : IAsyncActionFilter
{
    private readonly Type _limiterType;
    private readonly string _keyPrefix;

    public RateLimitFilter(Type limiterType, string keyPrefix)
    {
        _limiterType = limiterType;
        _keyPrefix = keyPrefix;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var limiter = (RateLimiter)context.HttpContext.RequestServices.GetRequiredService(_limiterType);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (!limiter.Allow(_keyPrefix + ":" + context.HttpContext.User.GetUserId(), now))
        {
            context.Result = JellyPlayResponses.Error(StatusCodes.Status429TooManyRequests, "rate-limited");
            return;
        }

        await next();
    }
}
