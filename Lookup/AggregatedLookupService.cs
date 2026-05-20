using Jellyfin.Plugin.SpecialToMovie.Models;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpecialToMovie.Lookup;

public class AggregatedLookupService
{
    private readonly TmdbLookupService _tmdbService;
    private readonly TvdbLookupService _tvdbService;
    private readonly ILogger<AggregatedLookupService> _logger;

    public AggregatedLookupService(
        TmdbLookupService tmdbService,
        TvdbLookupService tvdbService,
        ILogger<AggregatedLookupService> logger)
    {
        _tmdbService = tmdbService;
        _tvdbService = tvdbService;
        _logger = logger;
    }

    public async Task<MovieMatch?> LookupAsync(Episode episode, CancellationToken cancellationToken = default)
    {
        var tmdbTask = _tmdbService.LookupAsync(episode, cancellationToken);
        var tvdbTask = _tvdbService.LookupAsync(episode, cancellationToken);

        await Task.WhenAll(tmdbTask, tvdbTask).ConfigureAwait(false);

        var tmdb = tmdbTask.Result;
        var tvdb = tvdbTask.Result;

        if (tmdb != null && tvdb != null)
        {
            _logger.LogInformation(
                "Both TMDB and TVDB matched for {Name}: TMDB={TmdbTitle}, TVDB={TvdbTitle}",
                episode.Name, tmdb.Title, tvdb.Title);

            // Prefer TMDB for primary metadata, merge in TVDB movie ID
            return tmdb with { TvdbMovieId = tvdb.TvdbMovieId, TvdbMovieSlug = tvdb.TvdbMovieSlug };
        }

        if (tmdb != null)
        {
            _logger.LogInformation("Only TMDB matched for {Name}: {Title}", episode.Name, tmdb.Title);
            return tmdb;
        }

        if (tvdb != null)
        {
            _logger.LogInformation("Only TVDB matched for {Name}: {Title}", episode.Name, tvdb.Title);
            return tvdb;
        }

        _logger.LogDebug("No match found for episode {Name}", episode.Name);
        return null;
    }
}
