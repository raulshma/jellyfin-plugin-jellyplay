using System;
using System.Linq;
using Jellyfin.Plugin.JellyPlay.Api;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Jellyfin.Plugin.JellyPlay.Tests;

/// <summary>
/// The curated hero endpoint's serve contract (the `hero` feature): weight
/// descending with config order breaking ties (the LINQ sort is stable), blank
/// item ids never serving (they can never resolve client-side), the
/// 10-candidate cap, and the optional headline/subhead copy riding the
/// camelCase gate verbatim. The route touches only the hero config Func — the
/// controller's row dependencies stand in as nulls, the same host-free shape
/// the row-source parse tests use.
/// </summary>
public sealed class HeroEndpointTests
{
    private static RowsController Controller(Configuration.HeroConfig hero)
        => new(
            rows: null!,
            seasonal: null!,
            config: () => new Configuration.RowsConfig(),
            hero: () => hero);

    private static JArray Candidates(Configuration.HeroConfig hero)
    {
        var result = Controller(hero).GetHero();
        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal(200, content.StatusCode);
        return (JArray)JObject.Parse(content.Content!)["candidates"]!;
    }

    [Fact]
    public void WeightDescending_ConfigOrderBreaksTies()
    {
        var candidates = Candidates(new Configuration.HeroConfig
        {
            Candidates =
            {
                new() { ItemId = "b", Weight = 5 },
                new() { ItemId = "a", Weight = 5 },
                new() { ItemId = "c", Weight = 9 },
            }
        });

        Assert.Equal(new[] { "c", "b", "a" }, candidates.Select(c => (string)c["itemId"]!));
    }

    [Fact]
    public void BlankItemIds_NeverServe()
    {
        var candidates = Candidates(new Configuration.HeroConfig
        {
            Candidates = { new() { ItemId = "   " }, new() { ItemId = "kept" } }
        });

        Assert.Equal(new[] { "kept" }, candidates.Select(c => (string)c["itemId"]!));
    }

    [Fact]
    public void UnconfiguredHero_ServesAnEmptyCandidatesArray()
    {
        var candidates = Candidates(new Configuration.HeroConfig());

        Assert.Empty(candidates);
    }

    [Fact]
    public void ServesAtMostTen_AndCarriesTheOptionalCopy()
    {
        var hero = new Configuration.HeroConfig();
        for (var i = 0; i < 14; i++)
        {
            hero.Candidates.Add(new Configuration.HeroCandidateDefinition { ItemId = $"id-{i}", Weight = i });
        }

        hero.Candidates.Add(new Configuration.HeroCandidateDefinition
        {
            ItemId = "copy",
            Headline = "Night shift",
            Subhead = "Staff pick",
            Weight = 1
        });

        var candidates = Candidates(hero);

        Assert.Equal(10, candidates.Count);
        Assert.Equal("id-13", (string)candidates[0]["itemId"]!);
        var copy = candidates.Single(c => (string)c["itemId"]! == "copy");
        Assert.Equal("Night shift", (string)copy["headline"]!);
        Assert.Equal("Staff pick", (string)copy["subhead"]!);
        // NullValueHandling.Ignore drops absent copy — the camel gate's contract.
        Assert.Null(((JObject)candidates[1])["headline"]);
    }
}
