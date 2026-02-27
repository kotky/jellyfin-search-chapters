using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Entities;
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

        // Only intercept GET /Items
        if (!HttpMethods.IsGet(request.Method)
            || !string.Equals(request.Path.Value, "/Items", StringComparison.OrdinalIgnoreCase))
        {
            await next().ConfigureAwait(false);
            return;
        }

        var query = request.Query;
        var searchTerm = query["searchTerm"].ToString();

        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            await next().ConfigureAwait(false);
            return;
        }

        // Do not override explicit ids queries
        if (query.ContainsKey("ids"))
        {
            await next().ConfigureAwait(false);
            return;
        }

        var config = Plugin.Instance?.Configuration;
        var enableFuzzy = config?.EnableFuzzySearch ?? true;

        try
        {
            var rankedItems = SearchItemsAndChapters(searchTerm, enableFuzzy);

            if (rankedItems.Count == 0)
            {
                await next().ConfigureAwait(false);
                return;
            }

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

            dict["ids"] = rankedItems
                .Select(id => id.ToString("N", System.Globalization.CultureInfo.InvariantCulture))
                .ToList();

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
            _logger.LogError(ex, "Error applying SearchChapters interceptor to /Items request");
        }

        await next().ConfigureAwait(false);
    }

    private List<Guid> SearchItemsAndChapters(string query, bool enableFuzzy)
    {
        var queryNorm = Normalize(query);
        var scoresByItem = new Dictionary<Guid, double>();

        // 1. Regular items search via InternalItemsQuery (Videos only).
        var itemMatches = _libraryManager
            .GetItemList(new InternalItemsQuery
            {
                SearchTerm = query,
                IncludeItemTypes = new[] { BaseItemKind.Video },
                IsVirtualItem = false
            })
            .OfType<Video>()
            .ToList();

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
            .GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Video },
                IsVirtualItem = false
            })
            .OfType<Video>()
            .ToList();

        foreach (var video in videos)
        {
            List<ChapterInfo> chapters;

            try
            {
                chapters = BaseItem.ItemRepository.GetChapters(video);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get chapters for item {ItemId}", video.Id);
                continue;
            }

            if (chapters is null || chapters.Count == 0)
            {
                continue;
            }

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

        return scoresByItem
            .OrderByDescending(kvp => kvp.Value)
            .Select(kvp => kvp.Key)
            .ToList();
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
