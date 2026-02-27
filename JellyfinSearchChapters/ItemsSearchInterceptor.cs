using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SearchChapters;

/// <summary>
/// Intercepts /Items search to apply combined fuzzy item + chapter search.
/// </summary>
public sealed class ItemsSearchInterceptor : IAsyncActionFilter
{
    private const double ChapterMinScore = 0.6;

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ItemsSearchInterceptor> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ItemsSearchInterceptor"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="logger">The logger.</param>
    public ItemsSearchInterceptor(
        ILibraryManager libraryManager,
        ILogger<ItemsSearchInterceptor> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var request = context.HttpContext.Request;
        var path = request.Path.Value ?? string.Empty;

        // Only intercept GET /Items (path can be "/Items" or "/api/Items" etc.)
        if (!HttpMethods.IsGet(request.Method))
        {
            await next().ConfigureAwait(false);
            return;
        }

        if (!path.TrimEnd('/').EndsWith("Items", StringComparison.OrdinalIgnoreCase))
        {
            await next().ConfigureAwait(false);
            return;
        }

        var query = request.Query;
        var searchTerm = query["searchTerm"].ToString();

        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            _logger.LogDebug("[SearchChapters] /Items path matched but no searchTerm, passing through. Path=\"{Path}\"", path);
            await next().ConfigureAwait(false);
            return;
        }

        // Do not override explicit ids queries
        if (query.ContainsKey("ids"))
        {
            _logger.LogDebug("[SearchChapters] /Items has ids param, skipping intercept");
            await next().ConfigureAwait(false);
            return;
        }

        var config = Plugin.Instance?.Configuration;
        var enableFuzzy = config?.EnableFuzzySearch ?? true;

        _logger.LogInformation("[SearchChapters] Intercepting /Items searchTerm=\"{SearchTerm}\" EnableFuzzy={EnableFuzzy}", searchTerm, enableFuzzy);

        try
        {
            var rankedItems = SearchItemsAndChapters(searchTerm, enableFuzzy);

            if (rankedItems.Count == 0)
            {
                _logger.LogInformation("[SearchChapters] No ranked items for \"{SearchTerm}\", passing through to default search", searchTerm);
                await next().ConfigureAwait(false);
                return;
            }

            _logger.LogInformation("[SearchChapters] Rewriting /Items query with {Count} ranked item ids", rankedItems.Count);

            // Rewrite query: remove searchTerm, inject ids with ranked item ids.
            var dict = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in query)
            {
                if (string.Equals(kvp.Key, "searchTerm", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!dict.TryGetValue(kvp.Key, out var list))
                {
                    list = new List<string>();
                    dict[kvp.Key] = list;
                }

                foreach (var v in kvp.Value)
                {
                    if (!string.IsNullOrEmpty(v))
                    {
                        list.Add(v);
                    }
                }
            }

            // Jellyfin Items API expects ids as a single comma-separated value (CommaDelimitedCollectionModelBinder).
            var idsValue = string.Join(
                ",",
                rankedItems.Select(id => id.ToString("N", System.Globalization.CultureInfo.InvariantCulture)));
            dict["ids"] = new List<string> { idsValue };

            var parts = new List<string>();
            foreach (var kvp in dict)
            {
                foreach (var v in kvp.Value)
                {
                    parts.Add($"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(v)}");
                }
            }

            request.QueryString = new QueryString("?" + string.Join("&", parts));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SearchChapters] Error applying interceptor to /Items request");
        }

        await next().ConfigureAwait(false);
    }

    private List<Guid> SearchItemsAndChapters(string query, bool enableFuzzy)
    {
        var queryNorm = Normalize(query);
        var scoresByItem = new Dictionary<Guid, double>();

        // 1. Regular items search via InternalItemsQuery (Videos only).
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

        _logger.LogDebug("[SearchChapters] SearchItemsAndChapters: item search returned {Count} videos", itemMatches.Count);

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

            UpdateBestScore(scoresByItem, video.Id, itemScore);
        }

        // 2. Chapter-based search: rank videos by best matching chapter.
        var videos = _libraryManager
            .GetItemsResult(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Video },
                IsVirtualItem = false
            })
            .Items
            .OfType<Video>()
            .ToList();

        _logger.LogDebug("[SearchChapters] SearchItemsAndChapters: scanning chapters in {VideoCount} videos", videos.Count);

        var videosWithChapters = 0;

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

                UpdateBestScore(scoresByItem, video.Id, score);
            }
        }

        _logger.LogDebug("[SearchChapters] SearchItemsAndChapters: {VideosWithChapters} videos had chapters, {RankedCount} ranked items", videosWithChapters, scoresByItem.Count);

        return scoresByItem
            .OrderByDescending(kvp => kvp.Value)
            .Select(kvp => kvp.Key)
            .ToList();
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

    private static void UpdateBestScore(Dictionary<Guid, double> scores, Guid id, double score)
    {
        if (scores.TryGetValue(id, out var existing))
        {
            if (score > existing)
            {
                scores[id] = score;
            }
        }
        else
        {
            scores[id] = score;
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
