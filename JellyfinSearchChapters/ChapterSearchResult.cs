using System;

namespace Jellyfin.Plugin.SearchChapters;

/// <summary>
/// Result DTO for chapter search.
/// </summary>
public sealed class ChapterSearchResult
{
    /// <summary>
    /// Gets or sets the item id.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the item name.
    /// </summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the zero-based chapter index.
    /// </summary>
    public int ChapterIndex { get; set; }

    /// <summary>
    /// Gets or sets the chapter name.
    /// </summary>
    public string ChapterName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the chapter start position in ticks.
    /// </summary>
    public long StartPositionTicks { get; set; }

    /// <summary>
    /// Gets or sets the match score (0-1).
    /// </summary>
    public double Score { get; set; }
}
