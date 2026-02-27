using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SearchChapters.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        // set default options here
        EnableFuzzySearch = true;
    }

    /// <summary>
    /// Gets or sets a value indicating whether fuzzy search is enabled.
    /// </summary>
    public bool EnableFuzzySearch { get; set; }
}
