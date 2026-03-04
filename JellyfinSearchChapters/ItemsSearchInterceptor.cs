using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Plugin.SearchChapters;

/// <summary>
/// Intercepts /Items search to apply combined fuzzy item + chapter search.
/// </summary>
public sealed class ItemsSearchInterceptor : IAsyncActionFilter
{
    private const double ChapterMinScore = 0.6;

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IDtoService _dtoService;
    private readonly ILogger<ItemsSearchInterceptor> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ItemsSearchInterceptor"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="dtoService">The DTO service.</param>
    /// <param name="logger">The logger.</param>
    public ItemsSearchInterceptor(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IDtoService dtoService,
        ILogger<ItemsSearchInterceptor> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _dtoService = dtoService;
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

            _logger.LogInformation("[SearchChapters] Short-circuiting /Items with {Count} ranked item ids", rankedItems.Count);

            // Resolve user (required for DTOs and access). Prefer route, then query.
            var userIdStr = context.RouteData.Values["userId"]?.ToString()
                ?? query["userId"].ToString();
            if (string.IsNullOrWhiteSpace(userIdStr) || !Guid.TryParse(userIdStr, out var userId))
            {
                _logger.LogDebug("[SearchChapters] No userId in request, passing through");
                await next().ConfigureAwait(false);
                return;
            }

            var user = _userManager.GetUserById(userId);
            if (user is null)
            {
                _logger.LogDebug("[SearchChapters] User {UserId} not found, passing through", userId);
                await next().ConfigureAwait(false);
                return;
            }

            // Load items by id (respects user access), preserving order and chapter names for display.
            var items = new List<BaseItem>();
            var chapterNamesForItems = new List<IReadOnlyList<string>>();
            foreach (var (id, chapterNames) in rankedItems)
            {
                var item = _libraryManager.GetItemById<BaseItem>(id, userId);
                if (item is not null)
                {
                    items.Add(item);
                    chapterNamesForItems.Add(chapterNames);
                }
            }

            if (items.Count == 0)
            {
                _logger.LogDebug("[SearchChapters] No items accessible for user, passing through");
                await next().ConfigureAwait(false);
                return;
            }

            var startIndex = 0;
            var startIndexStr = query["startIndex"].ToString();
            if (!string.IsNullOrWhiteSpace(startIndexStr) && int.TryParse(startIndexStr, out var si))
            {
                startIndex = Math.Max(0, si);
            }

            var dtoOptions = new DtoOptions();
            var dtos = _dtoService.GetBaseItemDtos(items, dtoOptions, user);

            // Add all matching chapter names to each result so they show in search
            // (e.g. in taglines / overview / even the displayed name).
            for (var i = 0; i < dtos.Count && i < chapterNamesForItems.Count; i++)
            {
                var chapterNames = chapterNamesForItems[i];
                if (chapterNames.Count == 0)
                {
                    continue;
                }

                var dto = dtos[i];

                // 1) Taglines: some clients render these under the title.
                var taglines = (dto.Taglines ?? Array.Empty<string>()).ToList();
                foreach (var name in chapterNames)
                {
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        taglines.Add("Chapter: " + name);
                    }
                }

                if (taglines.Count > 0)
                {
                    dto.Taglines = taglines.ToArray();
                }

                // 2) Overview: many clients show this in details / expanded views.
                var chapterSummary = "Chapters: " + string.Join(", ", chapterNames.Where(n => !string.IsNullOrWhiteSpace(n)));
                if (!string.IsNullOrWhiteSpace(chapterSummary))
                {
                    if (string.IsNullOrWhiteSpace(dto.Overview))
                    {
                        dto.Overview = chapterSummary;
                    }
                    else if (!dto.Overview.Contains(chapterSummary, StringComparison.OrdinalIgnoreCase))
                    {
                        dto.Overview = dto.Overview + " | " + chapterSummary;
                    }
                }

                // 3) Name: last resort so that even very minimal search result UIs
                // that only show the item title will still expose the chapter info.
                // Keep it short: show at most the first 3 matching chapter names.
                var shortList = chapterNames
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(3)
                    .ToArray();

                if (shortList.Length > 0)
                {
                    var nameSuffix = " [" + string.Join(", ", shortList) + "]";
                    if (string.IsNullOrWhiteSpace(dto.Name))
                    {
                        dto.Name = chapterSummary;
                    }
                    else if (!dto.Name.Contains(nameSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        dto.Name += nameSuffix;
                    }
                }
            }

            var totalCount = items.Count;
            var result = new QueryResult<BaseItemDto>(startIndex, totalCount, dtos);
            context.Result = new OkObjectResult(result);
            _logger.LogDebug("[SearchChapters] Returning {Count} items directly", items.Count);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SearchChapters] Error applying interceptor to /Items request");
        }

        await next().ConfigureAwait(false);
    }

    /// <summary>
    /// Returns ranked item ids with all matching chapter names per item for display in search results.
    /// </summary>
    private List<(Guid ItemId, IReadOnlyList<string> ChapterNames)> SearchItemsAndChapters(string query, bool enableFuzzy)
    {
        var queryNorm = Normalize(query);
        var scoresByItem = new Dictionary<Guid, double>();
        var chapterNamesByItem = new Dictionary<Guid, List<string>>();

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

        // 2. Chapter-based search: rank videos by best matching chapter and store chapter name for display.
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
                if (!chapterNamesByItem.TryGetValue(video.Id, out var names))
                {
                    names = new List<string>();
                    chapterNamesByItem[video.Id] = names;
                }

                if (!names.Contains(chapter.Name))
                {
                    names.Add(chapter.Name);
                }
            }
        }

        _logger.LogDebug("[SearchChapters] SearchItemsAndChapters: {VideosWithChapters} videos had chapters, {RankedCount} ranked items", videosWithChapters, scoresByItem.Count);

        return scoresByItem
            .OrderByDescending(kvp => kvp.Value)
            .Select(kvp => (kvp.Key, (IReadOnlyList<string>)(chapterNamesByItem.TryGetValue(kvp.Key, out var list) ? list : Array.Empty<string>())))
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

    /// <summary>
    /// Updates the best score for an item. Returns true if this score was the one stored (new or improved).
    /// </summary>
    private static bool UpdateBestScore(Dictionary<Guid, double> scores, Guid id, double score)
    {
        if (scores.TryGetValue(id, out var existing))
        {
            if (score > existing)
            {
                scores[id] = score;
                return true;
            }

            return false;
        }

        scores[id] = score;
        return true;
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
