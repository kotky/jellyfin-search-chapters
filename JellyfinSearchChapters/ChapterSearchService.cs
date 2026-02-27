using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SearchChapters;

/// <summary>
/// API controller that exposes an endpoint for searching over item names and chapter names.
/// </summary>
[ApiController]
[Route("Plugins/SearchChapters")]
public class ChapterSearchService : ControllerBase
{
    private const double ChapterMinScore = 0.6;

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ChapterSearchService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChapterSearchService"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="logger">The logger.</param>
    public ChapterSearchService(
        ILibraryManager libraryManager,
        ILogger<ChapterSearchService> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Handles GET requests for chapter and item search.
    /// </summary>
    /// <param name="query">The search query text.</param>
    /// <returns>The search results.</returns>
    [HttpGet]
    public ActionResult<List<ChapterSearchResult>> Get([FromQuery] string query)
    {
        _logger.LogInformation("[SearchChapters] GET /Plugins/SearchChapters called with query: \"{Query}\"", query ?? "(null)");

        if (string.IsNullOrWhiteSpace(query))
        {
            _logger.LogInformation("[SearchChapters] Empty query, returning no results");
            return Ok(new List<ChapterSearchResult>());
        }

        var queryNorm = Normalize(query);
        var config = Plugin.Instance?.Configuration;
        var enableFuzzy = config?.EnableFuzzySearch ?? true;
        _logger.LogInformation("[SearchChapters] Config: EnableFuzzySearch={EnableFuzzy}", enableFuzzy);

        var results = new List<ChapterSearchResult>();

        // 1. Regular item search (Jellyfin's own search), merged into same result type.
        var itemMatches = _libraryManager
            .GetItemsResult(new InternalItemsQuery
            {
                SearchTerm = query,
                IncludeItemTypes = new[] { BaseItemKind.Video },
                IsVirtualItem = false
            })
            .Items
            .OfType<Video>()
            .ToList();

        _logger.LogInformation("[SearchChapters] Item search (SearchTerm=\"{Query}\") returned {Count} videos", query, itemMatches.Count);

        foreach (var video in itemMatches)
        {
            var itemName = video.Name ?? string.Empty;
            var itemNameNorm = Normalize(itemName);
            var itemScore = enableFuzzy
                ? FuzzyScore(queryNorm, itemNameNorm)
                : (itemNameNorm.Contains(queryNorm, StringComparison.OrdinalIgnoreCase) ? 0.9 : 0);

            if (itemScore <= 0)
            {
                continue;
            }

            results.Add(new ChapterSearchResult
            {
                ItemId = video.Id,
                ItemName = itemName,
                ChapterIndex = -1,
                ChapterName = string.Empty,
                StartPositionTicks = 0,
                Score = itemScore
            });
        }

        var videos = _libraryManager
            .GetItemsResult(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Video },
                IsVirtualItem = false
            })
            .Items
            .OfType<Video>()
            .ToList();

        _logger.LogInformation("[SearchChapters] Scanning chapters across {VideoCount} total videos", videos.Count);

        var videosWithChapters = 0;
        var chapterHits = 0;

        foreach (var video in videos)
        {
            List<ChapterInfo>? chapters;

            try
            {
                chapters = GetChaptersForVideo(video.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SearchChapters] Failed to get chapters for item {ItemId} ({ItemName})", video.Id, video.Name);
                continue;
            }

            if (chapters is null || chapters.Count == 0)
            {
                continue;
            }

            videosWithChapters++;

            for (var i = 0; i < chapters.Count; i++)
            {
                var chapter = chapters[i];
                if (string.IsNullOrWhiteSpace(chapter.Name))
                {
                    continue;
                }

                var chapterNorm = Normalize(chapter.Name);

                double score;
                if (enableFuzzy)
                {
                    score = FuzzyScore(queryNorm, chapterNorm);
                    if (score < ChapterMinScore)
                    {
                        continue;
                    }
                }
                else if (chapterNorm.Contains(queryNorm, StringComparison.OrdinalIgnoreCase))
                {
                    score = 0.9;
                }
                else
                {
                    continue;
                }

                chapterHits++;
                results.Add(new ChapterSearchResult
                {
                    ItemId = video.Id,
                    ItemName = video.Name ?? string.Empty,
                    ChapterIndex = i,
                    ChapterName = chapter.Name!,
                    StartPositionTicks = chapter.StartPositionTicks,
                    Score = score
                });
            }
        }

        _logger.LogInformation(
            "[SearchChapters] Done. Videos with chapters: {VideosWithChapters}, chapter matches: {ChapterHits}, item matches: {ItemMatches}, total results: {Total}",
            videosWithChapters,
            chapterHits,
            results.Count(r => r.ChapterIndex < 0),
            results.Count);

        return Ok(results
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.ItemName)
            .ThenBy(r => r.ChapterIndex)
            .ToList());
    }

    /// <summary>
    /// Gets chapters for a video via BaseItem.ChapterManager (Jellyfin 10.11+) or reflection fallback.
    /// </summary>
    private static List<ChapterInfo>? GetChaptersForVideo(Guid itemId)
    {
        try
        {
            var baseItemType = typeof(BaseItem);
            var prop = baseItemType.GetProperty("ChapterManager", BindingFlags.Public | BindingFlags.Static);
            if (prop?.GetValue(null) is not object chapterManager)
            {
                return null;
            }

            var method = chapterManager.GetType().GetMethod("GetChapters", new[] { typeof(Guid) });
            if (method?.Invoke(chapterManager, new object[] { itemId }) is not System.Collections.IEnumerable enumerable)
            {
                return null;
            }

            var list = new List<ChapterInfo>();
            foreach (var item in enumerable)
            {
                if (item is ChapterInfo info)
                {
                    list.Add(info);
                }
            }

            return list;
        }
        catch
        {
            return null;
        }
    }

    private static string Normalize(string value)
    {
        return value.Trim().ToLowerInvariant();
    }

    private static double FuzzyScore(string query, string candidate)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return 0;
        }

        if (candidate.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 0.9;
        }

        var qChars = query.Distinct().ToHashSet();
        var cChars = candidate.Distinct().ToHashSet();

        var intersection = qChars.Intersect(cChars).Count();
        var union = qChars.Union(cChars).Count();

        if (union == 0)
        {
            return 0;
        }

        return (double)intersection / union;
    }
}
