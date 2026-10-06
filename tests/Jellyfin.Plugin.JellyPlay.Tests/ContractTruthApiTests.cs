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
}
