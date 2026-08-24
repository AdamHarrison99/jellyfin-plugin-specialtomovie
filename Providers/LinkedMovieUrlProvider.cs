using Jellyfin.Plugin.SpecialToMovie.Data;
using Jellyfin.Plugin.SpecialToMovie.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpecialToMovie.Providers;

/// <summary>
/// Adds a link to the paired movie on a linked special's detail page.
/// </summary>
/// <remarks>
/// Jellyfin discovers this class itself and constructs it from the root service provider, so it
/// must not be registered in PluginServiceRegistrator. A throw from the constructor would fail the
/// whole plugin, and a throw from Name or GetExternalUrls would break the item detail response, so
/// both are written to degrade rather than throw.
/// </remarks>
public class LinkedMovieUrlProvider : IExternalUrlProvider
{
    private readonly IPairStore _pairStore;
    private readonly ILibraryManager _libraryManager;
    private readonly CrossLinkUrlResolver _urlResolver;
    private readonly ILogger<LinkedMovieUrlProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LinkedMovieUrlProvider"/> class.
    /// </summary>
    /// <param name="pairStore">The pair store.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="urlResolver">The cross-link URL resolver.</param>
    /// <param name="logger">The logger.</param>
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

    /// <inheritdoc />
    /// <remarks>
    /// A constant. Jellyfin reads this at startup to sort the providers as well as per request, and
    /// it is the one part of the link the web client escapes before rendering.
    /// </remarks>
    public string Name => "Movie Version";

    /// <inheritdoc />
    public IEnumerable<string> GetExternalUrls(BaseItem item)
    {
        try
        {
            return Build(item);
        }
        catch (Exception ex)
        {
            // This runs inside the item detail DTO pipeline; a throw here would break the whole
            // response for the item.
            _logger.LogWarning(ex, "Cross-link lookup failed for item {ItemId}", item?.Id);
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Returns a materialised result rather than an iterator: the caller enumerates the sequence
    /// after GetExternalUrls has returned, which would leave a try/catch in an iterator useless.
    /// </summary>
    /// <param name="item">The item being rendered.</param>
    /// <returns>Zero or one URL.</returns>
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

        // A movie deleted outside the plugin would otherwise render a dead button.
        if (_libraryManager.GetItemById(pair.MovieItemId.Value) == null)
        {
            return Array.Empty<string>();
        }

        return [_urlResolver.Resolve(pair.MovieItemId.Value, CrossLinkUrlBuilder.MovieMarker)];
    }
}
