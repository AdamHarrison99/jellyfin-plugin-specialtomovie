using Jellyfin.Plugin.SpecialToMovie.Data;
using Jellyfin.Plugin.SpecialToMovie.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpecialToMovie.Providers;

/// <summary>
/// Adds a link to the paired special on a linked movie's detail page.
/// </summary>
/// <remarks>
/// The mirror of <see cref="LinkedMovieUrlProvider"/>; the same construction and failure rules apply.
/// </remarks>
public class LinkedSpecialUrlProvider : IExternalUrlProvider
{
    private readonly IPairStore _pairStore;
    private readonly ILibraryManager _libraryManager;
    private readonly CrossLinkUrlResolver _urlResolver;
    private readonly ILogger<LinkedSpecialUrlProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LinkedSpecialUrlProvider"/> class.
    /// </summary>
    /// <param name="pairStore">The pair store.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="urlResolver">The cross-link URL resolver.</param>
    /// <param name="logger">The logger.</param>
    public LinkedSpecialUrlProvider(
        IPairStore pairStore,
        ILibraryManager libraryManager,
        CrossLinkUrlResolver urlResolver,
        ILogger<LinkedSpecialUrlProvider> logger)
    {
        _pairStore = pairStore;
        _libraryManager = libraryManager;
        _urlResolver = urlResolver;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "TV Special";

    /// <inheritdoc />
    public IEnumerable<string> GetExternalUrls(BaseItem item)
    {
        try
        {
            return Build(item);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cross-link lookup failed for item {ItemId}", item?.Id);
            return Array.Empty<string>();
        }
    }

    private string[] Build(BaseItem item)
    {
        if (item is not Movie)
        {
            return Array.Empty<string>();
        }

        var config = Plugin.Instance?.Configuration;
        if (config == null || !config.ShowCrossLinks)
        {
            return Array.Empty<string>();
        }

        var pair = _pairStore.GetByMovieId(item.Id);
        if (pair == null ||
            pair.Status != PairStatus.Active ||
            pair.EpisodeItemId == Guid.Empty)
        {
            return Array.Empty<string>();
        }

        if (_libraryManager.GetItemById(pair.EpisodeItemId) == null)
        {
            return Array.Empty<string>();
        }

        return [_urlResolver.Resolve(pair.EpisodeItemId, CrossLinkUrlBuilder.SpecialMarker)];
    }
}
