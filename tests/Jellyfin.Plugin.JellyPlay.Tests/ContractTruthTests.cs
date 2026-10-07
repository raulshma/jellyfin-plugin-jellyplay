using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Configuration;
using Jellyfin.Plugin.JellyPlay.Realtime;
using Jellyfin.Plugin.JellyPlay.Services.Anime;
using Jellyfin.Plugin.JellyPlay.Services.Events;
using Jellyfin.Plugin.JellyPlay.Services.Newsletter;
using Jellyfin.Plugin.JellyPlay.Services.Recommendations;
using Jellyfin.Plugin.JellyPlay.Services.Rows;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.JellyPlay.Tests;

/// <summary>New-media audience: "admins" filters, "all" broadcasts (the contract-truth fix for the dead branch).</summary>
public sealed class NewMediaAudienceTests
{
    private static EventService Service(SseHub hub, string audience)
        => new(
            hub,
            () => new EventsConfig { NewMediaEnabled = true, NewMediaAudience = audience },
            () => new List<string> { "admin-1" },
            NullLogger<EventService>.Instance);

    private static EpisodeGroup Group(Guid itemId)
        => new(Guid.NewGuid(), Guid.NewGuid(), "Show", 1, new[] { new EpisodeRef(itemId, "E1") }, null);

    /// <summary>The subscriber's next buffered event, or null after the window (the hub's own read seam).</summary>
    private static Task<SseEvent?> ReadOneAsync(SseHub hub, Guid subscriberId)
        => hub.WaitForEventAsync(subscriberId, TimeSpan.FromSeconds(2), CancellationToken.None);

    private static async Task AssertNothingDeliveredAsync(SseHub hub, Guid subscriberId)
    {
        // The publish already ran; a mis-delivery would be buffered and picked
        // up immediately. A null after the window means the filter held.
        Assert.Null(await hub.WaitForEventAsync(subscriberId, TimeSpan.FromMilliseconds(150), CancellationToken.None));
    }

    [Fact]
    public async Task AdminsAudience_DeliversOnlyToAdminSubscribers()
    {
        var hub = new SseHub(NullLogger<SseHub>.Instance);
        var adminId = hub.Subscribe("admin-1", "events");
        var regularId = hub.Subscribe("regular", "events");
        var service = Service(hub, "admins");

        var delivered = service.PublishNewMedia(Group(Guid.NewGuid()));
        var adminRead = await ReadOneAsync(hub, adminId);

        Assert.Equal(1, delivered);
        Assert.NotNull(adminRead);
        Assert.Equal("new-media", adminRead!.EventName);
        await AssertNothingDeliveredAsync(hub, regularId);

        hub.Unsubscribe(adminId);
        hub.Unsubscribe(regularId);
    }

    [Fact]
    public async Task AllAudience_BroadcastsToEverySubscriber()
    {
        var hub = new SseHub(NullLogger<SseHub>.Instance);
        var adminId = hub.Subscribe("admin-1", "events");
        var regularId = hub.Subscribe("regular", "events");
        var service = Service(hub, "all");

        var delivered = service.PublishNewMedia(Group(Guid.NewGuid()));
        var adminRead = await ReadOneAsync(hub, adminId);
        var regularRead = await ReadOneAsync(hub, regularId);

        Assert.Equal(2, delivered);
        Assert.NotNull(adminRead);
        Assert.NotNull(regularRead);

        hub.Unsubscribe(adminId);
        hub.Unsubscribe(regularId);
    }

    [Fact]
    public void DisabledEvents_PublishNothing()
    {
        var hub = new SseHub(NullLogger<SseHub>.Instance);
        var service = new EventService(
            hub,
            () => new EventsConfig { NewMediaEnabled = false, NewMediaAudience = "admins" },
            () => new List<string> { "admin-1" },
            NullLogger<EventService>.Instance);

        Assert.Equal(0, service.PublishNewMedia(Group(Guid.NewGuid())));
    }

    [Fact]
    public async Task MovieEvents_HonorAudienceToo()
    {
        var hub = new SseHub(NullLogger<SseHub>.Instance);
        var adminId = hub.Subscribe("admin-1", "events");
        var regularId = hub.Subscribe("regular", "events");
        var service = Service(hub, "admins");

        var delivered = service.PublishNewMovie(Guid.NewGuid(), "A Movie", null);
        var adminRead = await ReadOneAsync(hub, adminId);

        Assert.Equal(1, delivered);
        Assert.NotNull(adminRead);
        await AssertNothingDeliveredAsync(hub, regularId);

        hub.Unsubscribe(adminId);
        hub.Unsubscribe(regularId);
    }

    [Fact]
    public void ResolveAudienceTargets_NullForBroadcast_AdminSetForAdmins()
    {
        Assert.Null(EventService.ResolveAudienceTargets("all", new[] { "admin-1" }));
        Assert.Null(EventService.ResolveAudienceTargets(null, Array.Empty<string>()));

        var targets = EventService.ResolveAudienceTargets("admins", new[] { "admin-1", "admin-2" });
        Assert.NotNull(targets);
        Assert.Equal(2, targets!.Count);
        Assert.True(targets.Contains("admin-1"));
    }
}

/// <summary>The newsletter 400 contract depends on the pure configured-check.</summary>
public sealed class NewsletterConfiguredTests
{
    [Fact]
    public void HostAndFromPresent_IsConfigured()
    {
        Assert.True(NewsletterService.IsSmtpConfigured(new NewsletterConfig
        {
            SmtpHost = "smtp.example.com",
            FromAddress = "digest@example.com"
        }));
    }

    [Fact]
    public void MissingHostOrFrom_IsNotConfigured()
    {
        Assert.False(NewsletterService.IsSmtpConfigured(new NewsletterConfig { FromAddress = "digest@example.com" }));
        Assert.False(NewsletterService.IsSmtpConfigured(new NewsletterConfig { SmtpHost = "smtp.example.com" }));
        Assert.False(NewsletterService.IsSmtpConfigured(new NewsletterConfig()));
    }
}

/// <summary>TMDB official-list rows (the previously-404ing "tmdb" source).</summary>
public sealed class TmdbListParseTests
{
    private const string Payload = """
        {"id":1,"name":"Staff picks","items":[
          {"id":68718,"title":"Parasite","release_date":"2019-05-30","media_type":"movie"},
          {"id":1399,"name":"Game of Thrones","first_air_date":"2011-04-17","media_type":"tv"},
          {"id":42}
        ]}
        """;

    [Fact]
    public void Parse_MapsMoviesAndTvEntries()
    {
        var items = CustomRowsService.ParseTmdbList(Payload);
        Assert.Equal(2, items.Count);
        Assert.Equal(("Parasite", "2019", "68718"), (items[0].Title, items[0].Year, items[0].TmdbId));
        Assert.Equal(("Game of Thrones", "2011", "1399"), (items[1].Title, items[1].Year, items[1].TmdbId));
        Assert.Null(items[0].ImdbId);
    }

    [Fact]
    public void Parse_MissingOrEmptyItems_YieldsEmptyList()
    {
        Assert.Empty(CustomRowsService.ParseTmdbList("{}"));
        Assert.Empty(CustomRowsService.ParseTmdbList("{\"items\":[]}"));
    }
}

/// <summary>Anime provider-id resolution: any known library id maps through Fribb; the Jellyfin id never passes as a provider id.</summary>
public sealed class AnimeIdResolutionTests
{
    private static readonly FribbEntry EntryA = new("21", "1", "76599", null);
    private static readonly FribbEntry EntryB = new("98292", "5", null, "110492");

    [Fact]
    public void FromProviderIds_ReadsKeysCaseInsensitively_AndTrims()
    {
        var ids = AnimeIdResolver.FromProviderIds(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["AniList"] = " 21 ",
                ["TVDB"] = "76599"
            },
            null);

        Assert.Equal("21", ids.AniListId);
        Assert.Equal("76599", ids.TvdbId);
        Assert.Null(ids.MalId);
        Assert.Null(ids.TmdbId);
        Assert.Null(ids.ExplicitHint);
    }

    [Fact]
    public void FromProviderIds_ExplicitHintIsCarried()
    {
        var ids = AnimeIdResolver.FromProviderIds(null, " 12345 ");
        Assert.Equal("12345", ids.ExplicitHint);
        Assert.Null(ids.AniListId);
    }

    [Fact]
    public void LookupKeys_ResolveInOrder_HintLast()
    {
        var ids = AnimeIdResolver.FromProviderIds(
            new Dictionary<string, string> { ["mal"] = "5" }, "21");
        Assert.Equal(new[] { "5", "21" }, ids.LookupKeys());
    }

    [Fact]
    public void FindMapping_MatchesAnyKnownId()
    {
        var entries = new[] { EntryA, EntryB };

        Assert.Same(EntryA, AnimeIdResolver.FindMapping(entries, AnimeIdResolver.FromProviderIds(
            new Dictionary<string, string> { ["anilist"] = "21" }, null)));
        Assert.Same(EntryB, AnimeIdResolver.FindMapping(entries, AnimeIdResolver.FromProviderIds(
            new Dictionary<string, string> { ["mal"] = "5" }, null)));
        Assert.Same(EntryA, AnimeIdResolver.FindMapping(entries, AnimeIdResolver.FromProviderIds(
            new Dictionary<string, string> { ["tvdb"] = "76599" }, null)));
        Assert.Same(EntryB, AnimeIdResolver.FindMapping(entries, AnimeIdResolver.FromProviderIds(
            new Dictionary<string, string> { ["tmdb"] = "110492" }, null)));
    }

    [Fact]
    public void FindMapping_NoMatch_ReturnsNull()
    {
        var entries = new[] { EntryA, EntryB };
        Assert.Null(AnimeIdResolver.FindMapping(entries, AnimeIdResolver.FromProviderIds(
            new Dictionary<string, string> { ["tmdb"] = "999999" }, null)));
        Assert.Null(AnimeIdResolver.FindMapping(Array.Empty<FribbEntry>(), AnimeIdResolver.FromProviderIds(
            new Dictionary<string, string> { ["mal"] = "5" }, null)));
    }

    [Fact]
    public void SlugifyName_KebabCasesTheSeriesTitle()
    {
        Assert.Equal("one-punch-man", AnimeMarkersService.SlugifyName("One Punch Man"));
        Assert.Equal("steinsgate", AnimeMarkersService.SlugifyName("Steins;Gate"));
        Assert.Equal("mushoku-tensei-jobless-reincarnation", AnimeMarkersService.SlugifyName("Mushoku Tensei: Jobless Reincarnation"));
        Assert.Equal("one-punch-man", AnimeMarkersService.SlugifyName("one-punch-man"));
    }
}

/// <summary>
/// Anime override resolution precedence: explicit admin override (matched by
/// seriesId) > provider-id auto-derive, with an override that carries no
/// usable provider id degrading to the derived ids. The config lookup is a
/// parameter, so the whole precedence chain is testable host-free.
/// </summary>
public sealed class AnimeOverrideResolutionTests
{
    private static List<Configuration.AnimeSeriesOverride> Overrides(params Configuration.AnimeSeriesOverride[] entries)
        => new(entries);

    private static AnimeProviderIds Derived(IReadOnlyDictionary<string, string>? providerIds = null, string? hint = null)
        => AnimeIdResolver.FromProviderIds(providerIds, hint);

    [Fact]
    public void MatchingOverride_WinsOverDerived()
    {
        var overrides = Overrides(new Configuration.AnimeSeriesOverride
        {
            SeriesId = "series-1",
            AniListId = " 21 ",
            MalId = "5"
        });

        var (ids, overridden) = AnimeIdResolver.Resolve(
            overrides, "series-1", Derived(new Dictionary<string, string> { ["anilist"] = "999", ["tvdb"] = "76599" }));

        Assert.True(overridden);
        Assert.Equal("21", ids.AniListId); // trimmed
        Assert.Equal("5", ids.MalId);
        Assert.Equal("76599", ids.TvdbId); // library ids stay available
        Assert.Null(ids.TmdbId);
    }

    [Fact]
    public void MatchingOverride_PreservesTheExplicitHint()
    {
        var overrides = Overrides(new Configuration.AnimeSeriesOverride { SeriesId = "s1", AniListId = "21" });

        var (ids, overridden) = AnimeIdResolver.Resolve(overrides, "s1", Derived(hint: "some-slug"));

        Assert.True(overridden);
        Assert.Equal("21", ids.AniListId);
        Assert.Equal("some-slug", ids.ExplicitHint); // keeps its filler-slug role
    }

    [Fact]
    public void NonMatchingSeries_FallsBackToDerived()
    {
        var overrides = Overrides(new Configuration.AnimeSeriesOverride { SeriesId = "other", MalId = "5" });
        var derived = Derived(new Dictionary<string, string> { ["mal"] = "9" });

        var (ids, overridden) = AnimeIdResolver.Resolve(overrides, "s1", derived);

        Assert.False(overridden);
        Assert.Same(derived, ids);
        Assert.Equal("9", ids.MalId);
    }

    [Fact]
    public void OverrideWithoutAnyProviderId_FallsBackToDerived()
    {
        var overrides = Overrides(
            new Configuration.AnimeSeriesOverride { SeriesId = "s1", Label = "note only" },
            new Configuration.AnimeSeriesOverride { SeriesId = "s2", AniListId = "   " });
        var derived = Derived(new Dictionary<string, string> { ["anilist"] = "999" });

        Assert.False(AnimeIdResolver.Resolve(overrides, "s1", derived).Overridden);
        Assert.False(AnimeIdResolver.Resolve(overrides, "s2", derived).Overridden);
        Assert.Equal("999", AnimeIdResolver.Resolve(overrides, "s2", derived).Ids.AniListId);
    }

    [Fact]
    public void NullOrEmptyOverrideList_FallsBackToDerived()
    {
        var derived = Derived(new Dictionary<string, string> { ["mal"] = "5" });

        var fromNull = AnimeIdResolver.Resolve(null, "s1", derived);
        var fromEmpty = AnimeIdResolver.Resolve(new List<Configuration.AnimeSeriesOverride>(), "s1", derived);

        Assert.False(fromNull.Overridden);
        Assert.False(fromEmpty.Overridden);
        Assert.Equal("5", fromEmpty.Ids.MalId);
    }

    [Fact]
    public void FindOverride_SeriesIdMatchIsTrimmedOrdinal_AndFirstMatchWins()
    {
        var overrides = Overrides(
            new Configuration.AnimeSeriesOverride { SeriesId = " s1 ", AniListId = "21" },
            new Configuration.AnimeSeriesOverride { SeriesId = "s1", MalId = "5" });

        Assert.Same(overrides[0], AnimeIdResolver.FindOverride(overrides, "s1"));
        Assert.Null(AnimeIdResolver.FindOverride(overrides, "S1")); // ordinal: case differs
        Assert.Null(AnimeIdResolver.FindOverride(overrides, "s2"));
    }

    /// <summary>The dashboard and YAML editor round-trip: the whole config serializes and deserializes with the override list intact.</summary>
    [Fact]
    public void AnimeOverrides_YamlRoundTrip_PreservesTheList()
    {
        var config = new PluginConfiguration();
        config.Anime.SeriesOverrides.Add(new Configuration.AnimeSeriesOverride
        {
            SeriesId = "abc",
            AniListId = "21",
            MalId = "5",
            Label = "Fribb maps the wrong show"
        });
        config.Anime.SeriesOverrides.Add(new Configuration.AnimeSeriesOverride { SeriesId = "def", MalId = "9" });

        var yaml = new YamlDotNet.Serialization.SerializerBuilder()
            .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
            .Build()
            .Serialize(config);
        var round = new YamlDotNet.Serialization.DeserializerBuilder()
            .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build()
            .Deserialize<PluginConfiguration>(yaml);

        Assert.Equal(2, round.Anime.SeriesOverrides.Count);
        var first = round.Anime.SeriesOverrides[0];
        Assert.Equal(("abc", "21", "5", "Fribb maps the wrong show"), (first.SeriesId, first.AniListId, first.MalId, first.Label));
        var second = round.Anime.SeriesOverrides[1];
        Assert.Equal(("def", null, "9", null), (second.SeriesId, second.AniListId, second.MalId, second.Label));
    }
}

/// <summary>The scorer terms per contract: genres x3, tags x2, studios x1.5, shared people x1.5 (cap 5), year bands, franchise bonus.</summary>
public sealed class SimilarityScorerTests
{
    private static SimilarityFeatures Feats(
        string[]? genres = null,
        string[]? tags = null,
        string[]? studios = null,
        string[]? people = null,
        string? franchise = null,
        int? year = null)
        => new(genres ?? Array.Empty<string>(), tags ?? Array.Empty<string>(), studios ?? Array.Empty<string>(), people ?? Array.Empty<string>(), franchise, year);

    [Fact]
    public void NoOverlap_ScoresZero()
    {
        Assert.Equal(0, SimilarityScorer.Score(Feats(genres: new[] { "drama" }), Feats(genres: new[] { "comedy" })));
        Assert.Equal(0, SimilarityScorer.Score(Feats(), Feats(genres: new[] { "drama" })));
    }

    [Fact]
    public void GenreTagStudio_TermsKeepExistingWeights()
    {
        Assert.Equal(3.0, SimilarityScorer.Score(Feats(genres: new[] { "Action", "Drama" }), Feats(genres: new[] { "action" })), 2);
        Assert.Equal(2.0, SimilarityScorer.Score(Feats(tags: new[] { "heist" }), Feats(tags: new[] { "heist" })), 2);
        Assert.Equal(1.5, SimilarityScorer.Score(Feats(studios: new[] { "Ghibli" }), Feats(studios: new[] { "Ghibli" })), 2);
    }

    [Fact]
    public void SharedPeople_WeightedPerPerson()
    {
        var people = new[] { "Actor A", "Actor B" };
        Assert.Equal(3.0, SimilarityScorer.Score(Feats(people: people), Feats(people: new[] { "Actor A", "Actor B", "Actor C" })), 2);
    }

    [Fact]
    public void SharedPeople_CappedAtFive()
    {
        var people = new[] { "P1", "P2", "P3", "P4", "P5", "P6", "P7" };
        Assert.Equal(SimilarityScorer.SharedPersonCap * SimilarityScorer.SharedPersonWeight,
            SimilarityScorer.Score(Feats(people: people), Feats(people: people)), 2);
    }

    [Fact]
    public void YearProximity_Bands()
    {
        Assert.Equal(2.0, SimilarityScorer.Score(Feats(year: 2020), Feats(year: 2020)), 2);
        Assert.Equal(2.0, SimilarityScorer.Score(Feats(year: 2020), Feats(year: 2025)), 2);
        Assert.Equal(1.0, SimilarityScorer.Score(Feats(year: 2020), Feats(year: 2030)), 2);
        Assert.Equal(0, SimilarityScorer.Score(Feats(year: 1990), Feats(year: 2020)), 2);
    }

    [Fact]
    public void FranchiseKey_MatchAddsFlatBonus_MismatchAddsNothing()
    {
        var baseScore = SimilarityScorer.Score(Feats(genres: new[] { "action" }), Feats(genres: new[] { "action" }));
        Assert.Equal(3.0, baseScore, 2);
        Assert.Equal(baseScore + SimilarityScorer.FranchiseBonus,
            SimilarityScorer.Score(Feats(genres: new[] { "action" }, franchise: "collection:Alien"), Feats(genres: new[] { "action" }, franchise: "Collection:alien")), 2);
        Assert.Equal(baseScore,
            SimilarityScorer.Score(Feats(genres: new[] { "action" }, franchise: "collection:Alien"), Feats(genres: new[] { "action" }, franchise: "collection:Predator")), 2);
        Assert.Equal(baseScore,
            SimilarityScorer.Score(Feats(genres: new[] { "action" }, franchise: "collection:Alien"), Feats(genres: new[] { "action" })), 2);
    }
}

/// <summary>The dashboard's "auto-generate if empty" webhook-secret promise.</summary>
public sealed class WebhookSecretTests
{
    [Fact]
    public void BlankCandidates_GenerateHex32_AndAreDistinct()
    {
        var first = JellyPlayPlugin.NormalizeWebhookSecret(null);
        var second = JellyPlayPlugin.NormalizeWebhookSecret(string.Empty);
        var third = JellyPlayPlugin.NormalizeWebhookSecret("   ");

        Assert.Matches("^[0-9a-f]{32}$", first);
        Assert.Matches("^[0-9a-f]{32}$", second);
        Assert.Matches("^[0-9a-f]{32}$", third);
        Assert.NotEqual(first, second);
        Assert.NotEqual(second, third);
    }

    [Fact]
    public void RealSecret_IsKeptVerbatim()
    {
        Assert.Equal("secret", JellyPlayPlugin.NormalizeWebhookSecret("secret"));
        // Any non-whitespace value counts as real — only blank candidates generate.
        Assert.Equal("  secret  ", JellyPlayPlugin.NormalizeWebhookSecret("  secret  "));
    }
}
