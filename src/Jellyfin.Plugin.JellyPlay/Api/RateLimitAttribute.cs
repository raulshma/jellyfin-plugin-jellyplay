using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Helpers;
using Jellyfin.Plugin.JellyPlay.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.JellyPlay.Api;

/// <summary>How a rate-limited route identifies its caller.</summary>
public enum RateLimitKeyStrategy
{
    /// <summary>The plugin user id (authenticated mutating routes).</summary>
    User,

    /// <summary>
    /// Client identity — the remote address, or the X-Forwarded-For first hop
    /// only when <c>Seerr:TrustProxyHeaders</c> is set (anonymous intake routes;
    /// see <see cref="WebhookSecurity.ClientIpKey"/>).
    /// </summary>
    ClientIdentity
}

/// <summary>
/// Rate limit a mutating route. The key is the plugin user id by default, or
/// the client identity for anonymous intake routes. The 429 body goes through
/// the plugin's camelCase gate ({error: "rate-limited"}), per docs/CONTRACT.md.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RateLimitAttribute : TypeFilterAttribute
{
    /// <param name="kind">Which abuse-containment budget the route draws from (one registry, four budgets).</param>
    /// <param name="keyPrefix">Stable key namespace ("settings", "broadcast", "webhook") — combined with the key.</param>
    /// <param name="strategy">User id (default) or client identity for anonymous routes.</param>
    public RateLimitAttribute(RateLimiterKind kind, string keyPrefix, RateLimitKeyStrategy strategy = RateLimitKeyStrategy.User)
        : base(typeof(RateLimitFilter))
    {
        Arguments = new object[] { kind, keyPrefix, strategy };
    }
}

/// <summary>Implementation behind <see cref="RateLimitAttribute"/>. Runs before the action, so the limit check precedes any secret/cookie work.</summary>
public sealed class RateLimitFilter : IAsyncActionFilter
{
    private readonly RateLimiterKind _kind;
    private readonly string _keyPrefix;
    private readonly RateLimitKeyStrategy _strategy;

    public RateLimitFilter(RateLimiterKind kind, string keyPrefix, RateLimitKeyStrategy strategy)
    {
        _kind = kind;
        _keyPrefix = keyPrefix;
        _strategy = strategy;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var services = context.HttpContext.RequestServices;
        var limiter = services.GetRequiredService<RateLimiterRegistry>().Get(_kind);
        // Limiter ticks through the injectable clock when one is registered
        // (host/tests), so the window math is pinnable like the webhook's.
        var clock = services.GetService<TimeProvider>() ?? TimeProvider.System;
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        if (!limiter.Allow(BuildKey(_strategy, _keyPrefix, context.HttpContext, services), now))
        {
            context.Result = JellyPlayResponses.Error(StatusCodes.Status429TooManyRequests, "rate-limited");
            return;
        }

        await next();
    }

    /// <summary>
    /// The limit key per strategy: user id for authenticated routes, client
    /// identity for the anonymous webhook (proxy-trust read from the Seerr
    /// config). Internal so the derivation stays pinned by tests without a host.
    /// </summary>
    internal static string BuildKey(RateLimitKeyStrategy strategy, string keyPrefix, HttpContext httpContext, IServiceProvider services)
        => strategy == RateLimitKeyStrategy.ClientIdentity
            ? keyPrefix + ":" + Services.Admin.WebhookSecurity.ClientIpKey(
                httpContext,
                services.GetService<Func<Configuration.SeerrConfig>>()?.Invoke().TrustProxyHeaders ?? false)
            : keyPrefix + ":" + httpContext.User.GetUserIdString();
}
