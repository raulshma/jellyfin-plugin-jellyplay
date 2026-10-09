using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyPlay.Helpers;
using Xunit;

namespace Jellyfin.Plugin.JellyPlay.Tests;

/// <summary>
/// The dashboard JS ↔ resx drift tripwire — the ContractTruth move applied to
/// the client side. The pages' fetch/auth/error plumbing now lives in one
/// embedded commons (Pages/jellyplay-common.js, served as
/// configurationpage?name=JellyPlayCommon.js); these tests pin the seams that
/// cross the C#/JS mirror:
///
/// - every i18n key the JS asks for (fmt/alertFmt/alertText literals) and
///   every data-i18n key the HTML carries must exist in DashboardStrings.resx
///   (a renamed or dropped resx key fails here instead of silently serving
///   the JS fallback forever);
/// - exactly one page owns the api plumbing (the commons) — a page that
///   grows its own authHeaders/extractErrorDetail/apiGet copy fails here;
/// - every dashboard page loads the commons before its page script.
/// </summary>
public sealed class DashboardJsTripwireTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string PagesDir = Path.Combine(RepoRoot, "src", "Jellyfin.Plugin.JellyPlay", "Pages");

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

    private static ImmutableHashSet<string> ResxKeys()
        => DashboardStrings.All(CultureInfo.InvariantCulture).Keys.ToImmutableHashSet();

    private static IReadOnlyList<(string File, string Source)> JsSources()
        => Directory.GetFiles(PagesDir, "*.js")
            .Select(path => (Path.GetFileName(path), File.ReadAllText(path)))
            .ToList();

    private static IReadOnlyList<(string File, string Source)> HtmlSources()
        => Directory.GetFiles(PagesDir, "*.html")
            .Select(path => (Path.GetFileName(path), File.ReadAllText(path)))
            .ToList();

    [Fact]
    public void EveryJsStringKey_ExistsInResx()
    {
        var resxKeys = ResxKeys();
        var missing = new List<string>();
        foreach (var (file, source) in JsSources())
        {
            foreach (Match match in Regex.Matches(source, @"\b(?:fmt|alertFmt|alertText)\(\s*'([A-Za-z0-9_.]+)'"))
            {
                if (!resxKeys.Contains(match.Groups[1].Value))
                {
                    missing.Add($"{file}: {match.Groups[1].Value}");
                }
            }
        }

        Assert.True(missing.Count == 0, "JS string keys missing from DashboardStrings.resx:\n" + string.Join('\n', missing));
    }

    [Fact]
    public void EveryHtmlDataI18nKey_ExistsInResx()
    {
        var resxKeys = ResxKeys();
        var missing = new List<string>();
        foreach (var (file, source) in HtmlSources())
        {
            foreach (Match match in Regex.Matches(source, @"data-i18n=""([A-Za-z0-9_.]+)"""))
            {
                if (!resxKeys.Contains(match.Groups[1].Value))
                {
                    missing.Add($"{file}: {match.Groups[1].Value}");
                }
            }
        }

        Assert.True(missing.Count == 0, "HTML data-i18n keys missing from DashboardStrings.resx:\n" + string.Join('\n', missing));
    }

    [Fact]
    public void ApiPlumbing_LivesOnlyInTheCommons()
    {
        // The commons OWNS these functions; every other page must consume
        // them, not re-declare them (the copies drifted once — problems[]
        // parsing existed only on one page).
        var owned = new[] { "authHeaders", "extractErrorDetail", "apiGet", "apiPost", "apiDelete", "applyStrings" };
        var offenders = new List<string>();
        foreach (var (file, source) in JsSources())
        {
            if (file == "jellyplay-common.js")
            {
                continue;
            }

            foreach (var name in owned)
            {
                if (Regex.IsMatch(source, @"function\s+" + name + @"\s*\("))
                {
                    offenders.Add($"{file}: declares its own {name}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "api plumbing leaked out of the commons:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void EveryPageScript_LoadsTheCommonsFirst()
    {
        foreach (var (file, source) in HtmlSources())
        {
            var commonAt = source.IndexOf("name=JellyPlayCommon.js", StringComparison.OrdinalIgnoreCase);
            Assert.True(commonAt >= 0, $"{file}: does not load JellyPlayCommon.js");

            foreach (Match match in Regex.Matches(source, @"name=(JellyPlay[A-Za-z]*\.js)"))
            {
                var script = match.Groups[1].Value;
                if (script == "JellyPlayCommon.js")
                {
                    continue;
                }

                var pageAt = source.IndexOf("name=" + script, StringComparison.OrdinalIgnoreCase);
                Assert.True(pageAt > commonAt, $"{file}: loads {script} before JellyPlayCommon.js");
            }
        }
    }

    [Fact]
    public void Commons_IsRegistered_AsAPluginPage()
    {
        var pluginSource = File.ReadAllText(Path.Combine(RepoRoot, "src", "Jellyfin.Plugin.JellyPlay", "Plugin.cs"));
        Assert.Contains("JellyPlayCommon.js", pluginSource);
        Assert.Contains(".Pages.jellyplay-common.js", pluginSource);
    }
}
