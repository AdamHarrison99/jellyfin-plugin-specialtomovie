using Jellyfin.Plugin.SpecialToMovie.Data;
using Jellyfin.Plugin.SpecialToMovie.EventHandlers;
using Jellyfin.Plugin.SpecialToMovie.HardLink;
using Jellyfin.Plugin.SpecialToMovie.Lookup;
using Jellyfin.Plugin.SpecialToMovie.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
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

        serviceCollection.AddSingleton<NotFoundCache>();
        serviceCollection.AddSingleton<TmdbLookupService>();
        serviceCollection.AddSingleton<TvdbLookupService>();
        serviceCollection.AddSingleton<AggregatedLookupService>();

        serviceCollection.AddSingleton<IHardLinkService, HardLinkService>();

        serviceCollection.AddSingleton<WatchSyncService>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<WatchSyncService>());
        serviceCollection.AddSingleton<SpecialDetectionService>();
        serviceCollection.AddHostedService<LibraryEventHandler>();
    }
}
