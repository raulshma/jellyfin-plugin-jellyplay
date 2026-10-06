using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyPlay.Services.Recommendations;

public sealed record ScoredItem(Guid ItemId, string Name, double Score);

/// <summary>Host-agnostic item features the scorer needs; extracted so scoring is unit-testable.</summary>
public sealed record SimilarityFeatures(
    IReadOnlyCollection<string> Genres,
    IReadOnlyCollection<string> Tags,
    IReadOnlyCollection<string> Studios,
    IReadOnlyCollection<string> People,
    string? FranchiseKey,
    int? ProductionYear);

/// <summary>
/// The pure similarity scorer both entry points use: genre ×3, tag ×2, studio
/// ×1.5, shared person ×1.5 (capped at 5 people), year proximity (≤5 years →
/// 2.0, ≤15 → 1.0) and a strong flat franchise/same-parent bonus.
/// </summary>
internal static class SimilarityScorer
{
    internal const double SharedPersonWeight = 1.5;
    internal const int SharedPersonCap = 5;
    internal const double FranchiseBonus = 4.0;

    internal static double Score(SimilarityFeatures source, SimilarityFeatures candidate)
    {
        var sourceGenres = new HashSet<string>(source.Genres, StringComparer.OrdinalIgnoreCase);
        double score = Overlap(sourceGenres, candidate.Genres) * 3.0;
        var sourceTags = new HashSet<string>(source.Tags, StringComparer.OrdinalIgnoreCase);
        score += Overlap(sourceTags, candidate.Tags) * 2.0;
        var sourceStudios = new HashSet<string>(source.Studios, StringComparer.OrdinalIgnoreCase);
        score += Overlap(sourceStudios, candidate.Studios) * 1.5;
        var sourcePeople = new HashSet<string>(source.People, StringComparer.OrdinalIgnoreCase);
        score += Math.Min(Overlap(sourcePeople, candidate.People), SharedPersonCap) * SharedPersonWeight;
        if (source.ProductionYear is { } sourceYear && candidate.ProductionYear is { } candidateYear)
        {
            var distance = Math.Abs(sourceYear - candidateYear);
            score += distance <= 5 ? 2.0 : distance <= 15 ? 1.0 : 0;
        }

        if (!string.IsNullOrEmpty(source.FranchiseKey)
            && string.Equals(source.FranchiseKey, candidate.FranchiseKey, StringComparison.OrdinalIgnoreCase))
        {
            score += FranchiseBonus;
        }

        return score;
    }

    private static int Overlap<T>(HashSet<T> source, IEnumerable<T> candidateValues)
    {
        var hits = 0;
        foreach (var value in candidateValues)
        {
            if (source.Contains(value))
            {
                hits++;
            }
        }

        return hits;
    }
}

/// <summary>
/// Server-scored similar items: genre/tag overlap, shared people, studios,
/// same franchise (parent), year proximity. Runs entirely against the local
/// library — no external calls.
/// </summary>
public sealed class SimilarItemsService
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<SimilarItemsService> _logger;

    public SimilarItemsService(ILibraryManager libraryManager, ILogger<SimilarItemsService> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public Task<IReadOnlyList<ScoredItem>> GetSimilar(Guid itemId, int limit)
    {
        var source = _libraryManager.GetItemById(itemId);
        if (source is null)
        {
            return Task.FromResult<IReadOnlyList<ScoredItem>>(new List<ScoredItem>());
        }

        return Task.FromResult(ScoreAgainst(source, source.GetBaseItemKind(), limit));
    }

    /// <summary>
    /// The Jellyfin-12 provider pipeline entry (SimilarItemsProviderManager):
    /// scores the passed item against candidates straight from the library.
    /// Errors are the manager's to handle (it degrades to empty).
    /// </summary>
    public Task<IReadOnlyList<BaseItem>> GetSimilarItemsAsync(
        BaseItem source,
        object? user,
        int? limit,
        IReadOnlyList<Guid>? excludeItemIds,
        CancellationToken cancellationToken)
    {
        var scoredItems = ScoreAgainst(source, source.GetBaseItemKind(), limit ?? 12, excludeItemIds, cancellationToken);
        IReadOnlyList<BaseItem> result = scoredItems
            .Select(entry => _libraryManager.GetItemById(entry.ItemId))
            .Where(item => item is not null)
            .Cast<BaseItem>()
            .ToList();
        return Task.FromResult(result);
    }

    private IReadOnlyList<ScoredItem> ScoreAgainst(
        BaseItem source,
        Jellyfin.Data.Enums.BaseItemKind sourceKind,
        int limit,
        IReadOnlyList<Guid>? excludeItemIds = null,
        CancellationToken cancellationToken = default)
    {

        var candidates = _libraryManager.GetItemList(new InternalItemsQuery(null)
        {
            IncludeItemTypes = new[] { sourceKind },
            Limit = 500,
            OrderBy = new List<(Jellyfin.Data.Enums.ItemSortBy, Jellyfin.Database.Implementations.Enums.SortOrder)>
            {
                (Jellyfin.Data.Enums.ItemSortBy.Random, Jellyfin.Database.Implementations.Enums.SortOrder.Ascending)
            }
        });

        var scored = new List<ScoredItem>(candidates.Count);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.Id == source.Id || (excludeItemIds?.Contains(candidate.Id) ?? false))
            {
                continue;
            }

            var score = ScorePair(source, candidate);
            if (score <= 0)
            {
                continue;
            }

            scored.Add(new ScoredItem(candidate.Id, candidate.Name ?? string.Empty, Math.Round(score, 2)));
        }

        return scored
            .OrderByDescending(entry => entry.Score)
            .Take(Math.Clamp(limit, 1, 100))
            .ToList();
    }

    /// <summary>The shared scorer both entry points use; the source item itself never reaches here (filtered in ScoreAgainst).</summary>
    private double ScorePair(BaseItem source, BaseItem candidate)
        => SimilarityScorer.Score(ExtractFeatures(source), ExtractFeatures(candidate));

    /// <summary>Collects the comparable features of one library item (people via the library manager).</summary>
    private SimilarityFeatures ExtractFeatures(BaseItem item)
    {
        IReadOnlyCollection<string> people = Array.Empty<string>();
        if (item.SupportsPeople)
        {
            try
            {
                people = _libraryManager.GetPeople(item)
                    .Where(person => !string.IsNullOrEmpty(person.Name))
                    .Select(person => person.Name)
                    .ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "People lookup failed for {ItemName}", item.Name);
            }
        }

        return new SimilarityFeatures(
            item.Genres ?? Array.Empty<string>(),
            item.Tags ?? Array.Empty<string>(),
            item.Studios ?? Array.Empty<string>(),
            people,
            FranchiseKey(item),
            item.ProductionYear);
    }

    /// <summary>
    /// Franchise identity: the TMDB collection name (movies), the series id
    /// (TV), the album (music) or a physical parent folder. Library views
    /// (aggregate folders, collection folders, user views) are NOT franchises —
    /// everything in one library would otherwise share a key.
    /// </summary>
    private static string? FranchiseKey(BaseItem item)
    {
        switch (item)
        {
            case Movie { CollectionName: not null and not "" } movie:
                return "collection:" + movie.CollectionName;
            case Episode { SeriesId: var seriesId } when seriesId != Guid.Empty:
                return "series:" + seriesId;
            case Series series:
                return "series:" + series.Id;
        }

        if (!string.IsNullOrEmpty(item.Album))
        {
            return "album:" + item.Album;
        }

        if (item.GetParent() is Folder parent && parent is not ICollectionFolder && parent is not UserView)
        {
            return "folder:" + parent.Id;
        }

        return null;
    }
}
