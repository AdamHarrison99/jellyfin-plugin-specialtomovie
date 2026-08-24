using Jellyfin.Plugin.SpecialToMovie.Data;
using Jellyfin.Plugin.SpecialToMovie.EventHandlers;
using Jellyfin.Plugin.SpecialToMovie.HardLink;
using Jellyfin.Plugin.SpecialToMovie.Lookup;
using Jellyfin.Plugin.SpecialToMovie.Providers;
using Jellyfin.Plugin.SpecialToMovie.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.SpecialToMovie;

/// <summary>
/// Registers plugin services into the Jellyfin DI container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IPairStore, PairStore>();

        serviceCollection.AddSingleton<ApiResponseCache>();
        serviceCollection.AddSingleton<TmdbLookupService>();
        serviceCollection.AddSingleton<TvdbLookupService>();
        serviceCollection.AddSingleton<AggregatedLookupService>();

        serviceCollection.AddSingleton<IHardLinkService, HardLinkService>();

        // The IExternalUrlProvider implementations are found and constructed by Jellyfin's own
        // part discovery, so they are deliberately not registered here — only the helper they
        // depend on is, because constructor arguments must be resolvable from the container.
        serviceCollection.AddSingleton<CrossLinkUrlResolver>();
        serviceCollection.AddSingleton<IStartupFilter, ScriptInjectionStartupFilter>();

        serviceCollection.AddSingleton<WatchSyncService>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<WatchSyncService>());
        serviceCollection.AddSingleton<SpecialDetectionService>();
        serviceCollection.AddHostedService<LibraryEventHandler>();
    }
}
