using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.SearchChapters;

/// <summary>
/// Registers the interceptor with the Jellyfin DI container and MVC pipeline.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ItemsSearchInterceptor>();

        serviceCollection.PostConfigure<MvcOptions>(options =>
        {
            options.Filters.AddService<ItemsSearchInterceptor>();
        });
    }
}
