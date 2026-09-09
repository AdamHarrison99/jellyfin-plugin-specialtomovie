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

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IPairStore, PairStore>();

        serviceCollection.AddSingleton<ApiResponseCache>();
        serviceCollection.AddSingleton<TmdbLookupService>();
        serviceCollection.AddSingleton<TvdbLookupService>();
        serviceCollection.AddSingleton<AggregatedLookupService>();

        serviceCollection.AddSingleton<IHardLinkService, HardLinkService>();

        // Jellyfin's part discovery constructs the IExternalUrlProvider pair itself.
        // See agentic/ARCHITECTURE.md, "Cross-link buttons".
        serviceCollection.AddSingleton<CrossLinkUrlResolver>();
        serviceCollection.AddSingleton<IStartupFilter, ScriptInjectionStartupFilter>();

        serviceCollection.AddSingleton<WatchSyncService>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<WatchSyncService>());
        serviceCollection.AddSingleton<SpecialDetectionService>();
        serviceCollection.AddHostedService<LibraryEventHandler>();
    }
}
