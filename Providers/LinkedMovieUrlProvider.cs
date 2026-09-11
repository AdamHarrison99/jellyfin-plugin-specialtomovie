using Jellyfin.Plugin.SpecialToMovie.Data;
using Jellyfin.Plugin.SpecialToMovie.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpecialToMovie.Providers;

// Puts a link to the paired movie on a linked special's detail page.
// See agentic/ARCHITECTURE.md, "Cross-link buttons": part-discovered, must degrade not throw.
public class LinkedMovieUrlProvider : IExternalUrlProvider
{
    private readonly IPairStore _pairStore;
    private readonly ILibraryManager _libraryManager;
    private readonly CrossLinkUrlResolver _urlResolver;
    private readonly ILogger<LinkedMovieUrlProvider> _logger;

    public LinkedMovieUrlProvider(
        IPairStore pairStore,
        ILibraryManager libraryManager,
        CrossLinkUrlResolver urlResolver,
        ILogger<LinkedMovieUrlProvider> logger)
    {
        _pairStore = pairStore;
        _libraryManager = libraryManager;
        _urlResolver = urlResolver;
        _logger = logger;
    }

    // Read at startup to sort providers, and the one part of the link the web client escapes.
    public string Name => "Linked Movie";

    public IEnumerable<string> GetExternalUrls(BaseItem item)
    {
        try
        {
            return Build(item);
        }
        catch (Exception ex)
        {
            // ! Inside the item detail DTO pipeline: a throw here breaks the item's response.
            _logger.LogWarning(ex, "Cross-link lookup failed for item {ItemId}", item?.Id);
            return Array.Empty<string>();
        }
    }

    // ! Materialised, never an iterator. The caller enumerates after GetExternalUrls returns.
    private string[] Build(BaseItem item)
    {
        if (item is not Episode)
        {
            return Array.Empty<string>();
        }

        var config = Plugin.Instance?.Configuration;
        if (config == null || !config.ShowCrossLinks)
        {
            return Array.Empty<string>();
        }

        var pair = _pairStore.GetByEpisodeId(item.Id);
        if (pair == null ||
            pair.Status != PairStatus.Active ||
            pair.MovieItemId == null ||
            pair.MovieItemId == Guid.Empty)
        {
            return Array.Empty<string>();
        }

        // A movie deleted outside the plugin leaves a dead button behind.
        if (_libraryManager.GetItemById(pair.MovieItemId.Value) == null)
        {
            return Array.Empty<string>();
        }

        return [_urlResolver.Resolve(pair.MovieItemId.Value, CrossLinkUrlBuilder.MovieMarker)];
    }
}
