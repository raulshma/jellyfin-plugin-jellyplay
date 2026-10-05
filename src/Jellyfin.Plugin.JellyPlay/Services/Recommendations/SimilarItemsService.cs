using System;
using System.Collections.Generic;
using System.Linq;
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

        var sourceGenres = new HashSet<string>(source.Genres ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var sourceTags = new HashSet<string>(source.Tags ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var sourceStudios = new HashSet<string>(source.Studios ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var sourceYear = source.ProductionYear;

        var candidates = _libraryManager.GetItemList(new InternalItemsQuery(null)
        {
            IncludeItemTypes = new[] { source.GetBaseItemKind() },
            Limit = 500,
            OrderBy = new List<(Jellyfin.Data.Enums.ItemSortBy, Jellyfin.Database.Implementations.Enums.SortOrder)>
            {
                (Jellyfin.Data.Enums.ItemSortBy.Random, Jellyfin.Database.Implementations.Enums.SortOrder.Ascending)
            }
        });

        var scored = new List<ScoredItem>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (candidate.Id == itemId)
            {
                continue;
            }

            double score = 0;
            score += OverlapScore(sourceGenres, candidate.Genres) * 3.0;
            score += OverlapScore(sourceTags, candidate.Tags) * 2.0;
            score += OverlapScore(sourceStudios, candidate.Studios ?? Array.Empty<string>()) * 1.5;

            if (sourceYear is { } sourceY && candidate.ProductionYear is { } candidateY)
            {
                var distance = Math.Abs(sourceY - candidateY);
                score += distance <= 5 ? 2.0 : distance <= 15 ? 1.0 : 0;
            }

            if (score <= 0)
            {
                continue;
            }

            scored.Add(new ScoredItem(candidate.Id, candidate.Name ?? string.Empty, Math.Round(score, 2)));
        }

        return Task.FromResult<IReadOnlyList<ScoredItem>>(
            scored.OrderByDescending(entry => entry.Score).Take(Math.Clamp(limit, 1, 100)).ToList());
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
