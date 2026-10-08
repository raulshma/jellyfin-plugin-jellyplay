using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyPlay.Services.Recommendations;

public sealed record ScoredItem(Guid ItemId, string Name, double Score)
{
    /// <summary>
    /// The scored library item itself, threaded from the candidate scan so the
    /// winner list re-fetches nothing by id. Internal: never serialized on the
    /// wire (the API projection carries ItemId/Name/Score only).
    /// </summary>
    internal BaseItem? Item { get; init; }
}

/// <summary>Host-agnostic item features the scorer needs; extracted so scoring is unit-testable.</summary>
public sealed record SimilarityFeatures(
    IReadOnlyCollection<string> Genres,
    IReadOnlyCollection<string> Tags,
    IReadOnlyCollection<string> Studios,
    IReadOnlyCollection<string> People,
    string? FranchiseKey,
    int? ProductionYear);

/// <summary>
/// The source's set-shaped features, built ONCE per request: per-candidate
/// scoring then only walks the candidate's collections instead of rebuilding
/// the source's HashSets for every candidate.
/// </summary>
internal sealed record PreparedSource(
    HashSet<string> Genres,
    HashSet<string> Tags,
    HashSet<string> Studios,
    HashSet<string> People,
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

    /// <summary>Prepares the source for a scoring loop (its sets are loop-invariant).</summary>
    internal static PreparedSource PrepareSource(SimilarityFeatures source) => new(
        new HashSet<string>(source.Genres, StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(source.Tags, StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(source.Studios, StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(source.People, StringComparer.OrdinalIgnoreCase),
        source.FranchiseKey,
        source.ProductionYear);

    /// <summary>Single-shot convenience (tests, one-off callers): prepares the source inline.</summary>
    internal static double Score(SimilarityFeatures source, SimilarityFeatures candidate)
        => Score(PrepareSource(source), candidate);

    internal static double Score(PreparedSource source, SimilarityFeatures candidate)
    {
        double score = Overlap(source.Genres, candidate.Genres) * 3.0;
        score += Overlap(source.Tags, candidate.Tags) * 2.0;
        score += Overlap(source.Studios, candidate.Studios) * 1.5;
        score += Math.Min(Overlap(source.People, candidate.People), SharedPersonCap) * SharedPersonWeight;
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
    /// scores the passed item against candidates straight from the library,
    /// scoped to the requesting user when the host forwarded one (access-aware
    /// candidates, matching host semantics). The manager resolves the user
    /// from the host query (via reflection) and hands it over typed; errors
    /// are the manager's to handle (it degrades to empty).
    /// </summary>
    public Task<IReadOnlyList<BaseItem>> GetSimilarItemsAsync(
        BaseItem source,
        User? user,
        int? limit,
        IReadOnlyList<Guid>? excludeItemIds,
        CancellationToken cancellationToken)
    {
        var scoredItems = ScoreAgainst(source, source.GetBaseItemKind(), limit ?? 12, user, excludeItemIds, cancellationToken);
        // The scan already held every candidate: the threaded BaseItem
        // reference replaces the per-winner GetItemById re-fetch.
        IReadOnlyList<BaseItem> result = scoredItems
            .Select(entry => entry.Item)
            .Where(item => item is not null)
            .Cast<BaseItem>()
            .ToList();
        return Task.FromResult(result);
    }

    private IReadOnlyList<ScoredItem> ScoreAgainst(
        BaseItem source,
        Jellyfin.Data.Enums.BaseItemKind sourceKind,
        int limit,
        User? user = null,
        IReadOnlyList<Guid>? excludeItemIds = null,
        CancellationToken cancellationToken = default)
    {
        // When the host forwarded a user the candidate query is scoped to them
        // (access-aware, like the stock pipeline); paths without a user stay
        // unscoped.
        var candidates = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { sourceKind },
            Limit = 500,
            OrderBy = new List<(Jellyfin.Data.Enums.ItemSortBy, Jellyfin.Database.Implementations.Enums.SortOrder)>
            {
                (Jellyfin.Data.Enums.ItemSortBy.Random, Jellyfin.Database.Implementations.Enums.SortOrder.Ascending)
            }
        });

        // Request-scoped memoization (internal seam, same outputs): people and
        // parent lookups are folded through per-request dictionaries so a repeat
        // scoring of one item never re-crosses the host seam. A true batch
        // (one host query for all candidates) is deliberately NOT built: the
        // host offers no batch people/parent adapter, so that seam would be
        // hypothetical — the memo is the thin, zero-drift step.
        var peopleMemo = new Dictionary<Guid, IReadOnlyCollection<string>>();
        var parentMemo = new Dictionary<Guid, BaseItem?>();

        // The source is loop-invariant: its features (one people lookup per
        // item) are extracted once and set-shaped once, not once per candidate.
        var sourceFeatures = ExtractFeatures(source, peopleMemo, parentMemo);
        var preparedSource = SimilarityScorer.PrepareSource(sourceFeatures);

        var scored = new List<ScoredItem>(candidates.Count);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.Id == source.Id || (excludeItemIds?.Contains(candidate.Id) ?? false))
            {
                continue;
            }

            var score = SimilarityScorer.Score(preparedSource, ExtractFeatures(candidate, peopleMemo, parentMemo));
            if (score <= 0)
            {
                continue;
            }

            scored.Add(new ScoredItem(candidate.Id, candidate.Name ?? string.Empty, Math.Round(score, 2)) { Item = candidate });
        }

        return scored
            .OrderByDescending(entry => entry.Score)
            .Take(Math.Clamp(limit, 1, 100))
            .ToList();
    }

    /// <summary>
    /// Collects the comparable features of one library item (people via the
    /// library manager). The memos are the request's lookup cache: a hit
    /// returns the same features a fresh host read would — same outputs, fewer
    /// host crossings on repeats.
    /// </summary>
    private SimilarityFeatures ExtractFeatures(
        BaseItem item,
        Dictionary<Guid, IReadOnlyCollection<string>> peopleMemo,
        Dictionary<Guid, BaseItem?> parentMemo)
    {
        if (!peopleMemo.TryGetValue(item.Id, out var people))
        {
            people = ReadPeople(item);
            peopleMemo[item.Id] = people;
        }

        return new SimilarityFeatures(
            item.Genres ?? Array.Empty<string>(),
            item.Tags ?? Array.Empty<string>(),
            item.Studios ?? Array.Empty<string>(),
            people,
            FranchiseKey(item, parentMemo),
            item.ProductionYear);
    }

    private IReadOnlyCollection<string> ReadPeople(BaseItem item)
    {
        if (!item.SupportsPeople)
        {
            return Array.Empty<string>();
        }

        try
        {
            return _libraryManager.GetPeople(item)
                .Where(person => !string.IsNullOrEmpty(person.Name))
                .Select(person => person.Name)
                .ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "People lookup failed for {ItemName}", item.Name);
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Franchise identity: the TMDB collection name (movies), the series id
    /// (TV), the album (music) or a physical parent folder. Library views
    /// (aggregate folders, collection folders, user views) are NOT franchises —
    /// everything in one library would otherwise share a key.
    /// </summary>
    private static string? FranchiseKey(BaseItem item, Dictionary<Guid, BaseItem?> parentMemo)
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

        if (!parentMemo.TryGetValue(item.Id, out var parent))
        {
            parent = item.GetParent();
            parentMemo[item.Id] = parent;
        }

        if (parent is Folder folder && folder is not ICollectionFolder && folder is not UserView)
        {
            return "folder:" + folder.Id;
        }

        return null;
    }
}
