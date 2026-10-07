using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.JellyPlay.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Jellyfin.Plugin.JellyPlay.Tests;

/// <summary>
/// Executable mirror of docs/CONTRACT.md — the plugin's deepest interface.
/// Route spellings, the contract version and the feature keys are pinned
/// bidirectionally: a controller route missing from the doc (or a documented
/// route no controller serves) fails here, so the doc cannot silently drift
/// from what the wire actually serves.
/// </summary>
public sealed class ContractTruthApiTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string ContractMarkdown = File.ReadAllText(Path.Combine(RepoRoot, "docs", "CONTRACT.md"));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "CONTRACT.md")))
        {
            dir = dir.Parent!;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>Contract route spellings parsed from the doc's code fences: (method, normalized path).</summary>
    private static ImmutableHashSet<(string Method, string Path)> DocumentedRoutes()
    {
        var routes = new HashSet<(string, string)>();
        foreach (var line in ContractMarkdown.Split('\n'))
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                line,
                @"^\s*(GET|POST|PUT|DELETE|PATCH|ANY)\s+jellyplay/(\S*)");
            if (!match.Success)
            {
                continue;
            }

            var path = NormalizePath(match.Groups[2].Value);
            if (match.Groups[1].Value == "ANY")
            {
                foreach (var method in new[] { "GET", "POST", "PUT", "DELETE", "PATCH" })
                {
                    routes.Add((method, path));
                }
            }
            else
            {
                routes.Add((match.Groups[1].Value, path));
            }
        }

        return routes.ToImmutableHashSet();
    }

    /// <summary>(method, path) pairs served by the plugin's controllers, via reflection over route attributes. Paths are stored relative to the "jellyplay" prefix, matching <see cref="DocumentedRoutes"/>.</summary>
    private static ImmutableHashSet<(string Method, string Path)> ServedRoutes()
    {
        var routes = new HashSet<(string, string)>();
        var controllerTypes = typeof(JellyPlayContract).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract);

        foreach (var controller in controllerTypes)
        {
            var controllerTemplate = controller.GetCustomAttribute<RouteAttribute>()?.Template ?? string.Empty;
            foreach (var action in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (var httpAttribute in action.GetCustomAttributes<HttpMethodAttribute>(inherit: true))
                {
                    var template = controllerTemplate.Length == 0
                        ? httpAttribute.Template ?? string.Empty
                        : httpAttribute.Template is { Length: > 0 } actionTemplate
                            ? controllerTemplate + "/" + actionTemplate
                            : controllerTemplate;
                    template = template.TrimEnd('/');

                    // Root-level exceptions (the newsletter stubs live outside
                    // the "jellyplay" prefix and are documented separately).
                    if (!template.StartsWith("jellyplay/", StringComparison.Ordinal) && template != "jellyplay")
                    {
                        continue;
                    }

                    var relative = template == "jellyplay"
                        ? string.Empty
                        : template["jellyplay/".Length..];

                    foreach (var httpMethod in httpAttribute.HttpMethods)
                    {
                        routes.Add((httpMethod.ToUpperInvariant(), NormalizePath(relative)));
                    }
                }
            }
        }

        return routes.ToImmutableHashSet();
    }

    /// <summary>
    /// Path normalization for comparison: query strings dropped, "**" catch-alls
    /// and optional markers folded, and every placeholder collapsed to "{}" —
    /// parameter NAMES are not wire-relevant, only position count.
    /// </summary>
    private static string NormalizePath(string path)
    {
        path = path.Replace("?}", "}", StringComparison.Ordinal); // "{profile?}" optional marker
        path = path.Replace("**", string.Empty, StringComparison.Ordinal); // "{**path}" catch-all
        path = path.Split('?')[0]; // query strings
        path = System.Text.RegularExpressions.Regex.Replace(path, @"\{[^}]*\}", "{}");
        return path.TrimEnd('/');
    }

    [Fact]
    public void EveryControllerRouteIsDocumented()
    {
        var documented = DocumentedRoutes();
        var missing = ServedRoutes().Where(route => !documented.Contains(route)).OrderBy(route => route.Path).ToList();
        Assert.True(
            missing.Count == 0,
            "Routes served but missing from docs/CONTRACT.md (document them, or they are not contract):\n  "
            + string.Join("\n  ", missing.Select(r => r.Method + " jellyplay/" + r.Path)));
    }

    [Fact]
    public void EveryDocumentedRouteIsServed()
    {
        var served = ServedRoutes();
        var missing = DocumentedRoutes().Where(route => !served.Contains(route)).OrderBy(route => route.Path).ToList();
        Assert.True(
            missing.Count == 0,
            "Routes documented in docs/CONTRACT.md but served by no controller (stale doc):\n  "
            + string.Join("\n  ", missing.Select(r => r.Method + " jellyplay/" + r.Path)));
    }

    [Fact]
    public void ContractVersionInDocMatchesCode()
        => Assert.Contains(
            "contractVersion: " + JellyPlayContract.ContractVersion,
            ContractMarkdown,
            StringComparison.Ordinal);

    [Fact]
    public void EveryFeatureKeyIsDocumented()
    {
        var missing = typeof(JellyPlayContract.Features).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => (string)field.GetValue(null)!)
            .Where(key => !ContractMarkdown.Contains("`" + key + "`", StringComparison.Ordinal))
            .ToList();
        Assert.True(
            missing.Count == 0,
            "Feature keys not documented in docs/CONTRACT.md: " + string.Join(", ", missing));
    }

    /// <summary>The ONE error-body shape through the camelCase gate — StatusCode(...) calls leak PascalCase and are forbidden.</summary>
    [Fact]
    public void ErrorBodiesGoThroughTheCamelGate()
    {
        var result = JellyPlayResponses.Error(429, "rate-limited");
        Assert.Equal(429, result.StatusCode);
        var body = JObject.Parse(result.Content!);
        Assert.Equal("rate-limited", body["error"]!.ToString());
        Assert.Single(body.Properties());
    }

    /// <summary>Every plugin controller, via the same reflection as the route mirror.</summary>
    private static IEnumerable<Type> ControllerTypes()
        => typeof(JellyPlayContract).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract);

    /// <summary>
    /// Gate exclusivity, type level: no controller ACTION may declare an
    /// ObjectResult-derived return type — bodies cross the gate as
    /// ContentResult, and a lying ObjectResult signature is exactly the
    /// pipeline the host serializes PascalCase. (Only routes are checked —
    /// the inherited ControllerBase helpers are not actions.)
    /// </summary>
    [Fact]
    public void NoControllerActionDeclaresARawObjectResultType()
    {
        var offenders = ControllerTypes()
            .SelectMany(controller => controller.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Where(action => action.GetCustomAttributes<HttpMethodAttribute>(inherit: true).Any())
            .Select(action => action.ReturnType)
            .Where(returnType => typeof(ObjectResult).IsAssignableFrom(returnType))
            .Distinct()
            .OrderBy(type => type.Name)
            .ToList();
        Assert.True(
            offenders.Count == 0,
            "Controller actions must not declare ObjectResult-derived return types (route bodies through JellyPlayResponses instead): "
            + string.Join(", ", offenders.Select(type => type.Name)));
    }

    /// <summary>
    /// Gate coverage, type level: every controller the host will discover —
    /// anything carrying [ApiController] or deriving ControllerBase, via the
    /// same reflection the route mirror uses — must derive
    /// <see cref="JellyPlayControllerBase"/>. That base is what carries the
    /// response-gate filter, so a controller deriving ControllerBase directly
    /// would silently skip the safety net.
    /// </summary>
    [Fact]
    public void EveryControllerDerivesJellyPlayControllerBase()
    {
        var controllers = typeof(JellyPlayContract).Assembly.GetTypes()
            .Where(type => !type.IsAbstract
                && (type.IsDefined(typeof(ApiControllerAttribute), inherit: true)
                    || typeof(ControllerBase).IsAssignableFrom(type)))
            .ToList();

        Assert.NotEmpty(controllers); // discovery sanity: the mirror must see the real controllers
        var offenders = controllers
            .Where(type => !typeof(JellyPlayControllerBase).IsAssignableFrom(type))
            .OrderBy(type => type.Name)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Every controller must derive JellyPlayControllerBase (it carries the response-gate filter): "
            + string.Join(", ", offenders.Select(type => type.Name)));
    }

    /// <summary>
    /// Gate exclusivity, source level (ADR-0001's "forbidden bypass"): no file
    /// outside the gate module may produce an object-body result — the host's
    /// serializer is PascalCase, so anything raw leaks. SeerrProxyService's
    /// WriteErrorAsync call is the allowlisted middleware-style write (it IS
    /// the gate). Every file is normalized before scanning — comments
    /// stripped, whitespace runs collapsed to one space — so a violation
    /// cannot evade the pin by splitting a pattern across lines, and the
    /// occurrence-level allowlist (a <c>JellyPlayResponses.</c> prefix) still
    /// exempts the gate's own calls. These patterns are cheap and honest about
    /// what they pin: they fail CI on a re-introduced raw body, not on intent.
    /// </summary>
    [Fact]
    public void ObjectBodiesAreOnlyProducedByTheGate()
    {
        var forbidden = new (string Pattern, string Why)[]
        {
            ("BadRequest(new ", "raw object body (host serializer)"),
            ("Unauthorized(new ", "raw object body (host serializer)"),
            ("Ok(new ", "raw object body (host serializer)"),
            ("Accepted(new ", "raw object body (host serializer)"),
            ("new ObjectResult(", "raw ObjectResult"),
            ("new BadRequestObjectResult(", "raw ObjectResult"),
            ("new OkObjectResult(", "raw ObjectResult"),
            ("new UnauthorizedObjectResult(", "raw ObjectResult"),
            ("new AcceptedResult(", "raw ObjectResult"),
            ("new StatusCodeResult(", "raw status result"),
            ("WriteAsJsonAsync(", "host serializer (use JellyPlayResponses.WriteErrorAsync)")
        };

        // Whitespace-tolerant matchers: the normalized text collapses line
        // breaks to single spaces, so a pattern must match with optional
        // whitespace at any of its token boundaries ("Ok(\n    new {").
        var forbiddenMatchers = forbidden
            .Select(pattern => (
                pattern.Pattern,
                Regex: new System.Text.RegularExpressions.Regex(WhitespaceTolerant(pattern.Pattern)),
                pattern.Why))
            .ToList();

        var offenders = new List<string>();
        var pluginRoot = Path.Combine(RepoRoot, "src", "Jellyfin.Plugin.JellyPlay");
        foreach (var file in Directory.EnumerateFiles(pluginRoot, "*.cs", SearchOption.AllDirectories))
        {
            var normalized = file.Replace('\\', '/');
            if (normalized.EndsWith("/JellyPlayResponses.cs", StringComparison.Ordinal)
                || normalized.Contains("/obj/", StringComparison.Ordinal)
                || normalized.Contains("/bin/", StringComparison.Ordinal))
            {
                continue; // the gate module itself (and build output) is exempt
            }

            var source = NormalizeSource(File.ReadAllText(file));
            foreach (var (pattern, regex, why) in forbiddenMatchers)
            {
                foreach (System.Text.RegularExpressions.Match match in regex.Matches(source))
                {
                    if (!source[..match.Index].EndsWith("JellyPlayResponses.", StringComparison.Ordinal))
                    {
                        offenders.Add($"{Path.GetFileName(file)}: {pattern}  ({why})");
                    }
                }
            }

            if (System.Text.RegularExpressions.Regex.IsMatch(
                    source,
                    @"(?<!JellyPlayResponses\.)StatusCode\([^)]*new\s*\{"))
            {
                offenders.Add($"{Path.GetFileName(file)}: StatusCode with an object body — use the gate");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Object bodies must be produced by JellyPlayResponses (the one serialization gate):\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Strips comments (block <c>/*…*/</c> and <c>//</c> to end of line, with
    /// the <c>://</c> of URLs guarded) and collapses whitespace runs to single
    /// spaces, so the forbidden patterns are matched across line breaks and
    /// comments may still name them to explain them.
    /// </summary>
    private static string NormalizeSource(string source)
    {
        var withoutBlockComments = System.Text.RegularExpressions.Regex.Replace(
            source, @"/\*.*?\*/", " ", System.Text.RegularExpressions.RegexOptions.Singleline);
        var withoutLineComments = System.Text.RegularExpressions.Regex.Replace(
            withoutBlockComments, @"(?<!:)//[^\n]*", " ");
        return System.Text.RegularExpressions.Regex.Replace(withoutLineComments, @"\s+", " ");
    }

    /// <summary>Builds a whitespace-tolerant matcher for a source pattern: spaces and word/symbol boundaries become optional whitespace runs.</summary>
    private static string WhitespaceTolerant(string pattern)
        => string.Join(
            @"\s*",
            System.Text.RegularExpressions.Regex
                .Split(pattern, @" |(?<=\w)(?=\W)|(?<=\W)(?=\w)")
                .Where(token => token.Length > 0)
                .Select(System.Text.RegularExpressions.Regex.Escape));
}
