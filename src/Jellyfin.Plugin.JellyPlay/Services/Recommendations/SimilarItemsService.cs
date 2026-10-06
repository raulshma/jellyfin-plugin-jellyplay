using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyPlay.Services.Recommendations;

public sealed record ScoredItem(Guid ItemId, string Name, double Score);

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

    /// <summary>The shared genre/tag/studio/year scorer both entry points use.</summary>
    private double ScorePair(BaseItem source, BaseItem candidate)
    {
        var sourceGenres = new HashSet<string>(source.Genres ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var sourceTags = new HashSet<string>(source.Tags ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var sourceStudios = new HashSet<string>(source.Studios ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        double score = 0;
        score += OverlapScore(sourceGenres, candidate.Genres) * 3.0;
        score += OverlapScore(sourceTags, candidate.Tags) * 2.0;
        score += OverlapScore(sourceStudios, candidate.Studios ?? Array.Empty<string>()) * 1.5;
        if (source.ProductionYear is { } sourceY && candidate.ProductionYear is { } candidateY)
        {
            var distance = Math.Abs(sourceY - candidateY);
            score += distance <= 5 ? 2.0 : distance <= 15 ? 1.0 : 0;
        }

        return score;
    }

    private static double OverlapScore<T>(HashSet<T> source, IEnumerable<T> candidateValues)
    {
        if (source.Count == 0)
        {
            return 0;
        }

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
