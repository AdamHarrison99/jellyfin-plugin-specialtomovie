using Jellyfin.Plugin.SpecialToMovie.Models;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.SpecialToMovie.Lookup;

public interface IMetadataLookupService
{
    Task<MovieMatch?> LookupAsync(Episode episode, CancellationToken cancellationToken = default);
}
