using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.JellyPlay.Api;

/// <summary>
/// The gate's safety net (ADR-0001): rewrites any ObjectResult an action (or
/// the framework) produced OUTSIDE the gate through the gate's serializer, so
/// a forgotten raw body cannot leak the host's PascalCase options onto the
/// wire. A net, not a second gate: CONTRACT.md pinning stays on the explicit
/// <see cref="JellyPlayResponses.Camel"/>/<see cref="JellyPlayResponses.Error"/>
/// calls, which this never touches — they are ContentResults, and only
/// ObjectResults are rewritten.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class JellyPlayResponseGateAttribute : TypeFilterAttribute
{
    public JellyPlayResponseGateAttribute()
        : base(typeof(JellyPlayResponseFilter))
    {
    }
}

public sealed class JellyPlayResponseFilter : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
        if (context.Result is not ObjectResult objectResult
            || objectResult.Value is null)
        {
            return; // no body, or not an ObjectResult for the net to rewrite
        }

        context.Result = JellyPlayResponses.Camel(objectResult.Value, objectResult.StatusCode ?? StatusCodes.Status200OK);
    }
}

/// <summary>
/// Common base for every plugin controller: carries the gate-net filter once
/// (filter attributes on a base controller apply to every derived controller
/// and its actions), so there is no per-controller wiring to forget. Abstract,
/// hence not a controller itself — no routes, never instantiated.
/// </summary>
[JellyPlayResponseGate]
public abstract class JellyPlayControllerBase : ControllerBase
{
}
