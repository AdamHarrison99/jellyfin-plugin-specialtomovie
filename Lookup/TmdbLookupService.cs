using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.SpecialToMovie.Models;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpecialToMovie.Lookup;

public class TmdbLookupService : IMetadataLookupService, IDisposable
{
    private const string BaseUrl = "https://api.themoviedb.org/3";
    private const int MaxRetries = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TmdbLookupService> _logger;
    private readonly SemaphoreSlim _rateLimiter = new(30, 30);

    public TmdbLookupService(IHttpClientFactory httpClientFactory, ILogger<TmdbLookupService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public void Dispose()
    {
        _rateLimiter.Dispose();
    }

    public async Task<MovieMatch?> LookupAsync(Episode episode, CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null || string.IsNullOrEmpty(config.TmdbApiKey))
        {
            _logger.LogDebug("TMDB API key not configured, skipping TMDB lookup");
            return null;
        }

        var imdbId = episode.GetProviderId(MetadataProvider.Imdb);

        // If no IMDB ID, try to get it via TMDB external_ids endpoint
        if (string.IsNullOrEmpty(imdbId))
        {
            imdbId = await GetImdbIdFromTmdbAsync(episode, config.TmdbApiKey, cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrEmpty(imdbId))
        {
            _logger.LogDebug("No IMDB ID available for episode {Name}, skipping TMDB lookup", episode.Name);
            return null;
        }

        return await FindMovieByImdbIdAsync(imdbId, config.TmdbApiKey, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> GetImdbIdFromTmdbAsync(Episode episode, string apiKey, CancellationToken cancellationToken)
    {
        var tmdbId = episode.GetProviderId(MetadataProvider.Tmdb);
        if (string.IsNullOrEmpty(tmdbId))
        {
            return null;
        }

        // Need the series TMDB ID and season/episode numbers
        var series = episode.Series;
        if (series == null)
        {
            return null;
        }

        var seriesTmdbId = series.GetProviderId(MetadataProvider.Tmdb);
        if (string.IsNullOrEmpty(seriesTmdbId))
        {
            return null;
        }

        var seasonNumber = episode.ParentIndexNumber ?? 0;
        var episodeNumber = episode.IndexNumber;
        if (episodeNumber == null)
        {
            return null;
        }

        var url = $"{BaseUrl}/tv/{seriesTmdbId}/season/{seasonNumber}/episode/{episodeNumber}/external_ids?api_key={apiKey}";
        var response = await SendWithRetryAsync(url, cancellationToken).ConfigureAwait(false);
        if (response == null)
        {
            return null;
        }

        var externalIds = JsonSerializer.Deserialize<TmdbExternalIds>(response, JsonOptions);
        if (!string.IsNullOrEmpty(externalIds?.ImdbId))
        {
            _logger.LogDebug("Retrieved IMDB ID {ImdbId} from TMDB external_ids for {Name}", externalIds.ImdbId, episode.Name);
            return externalIds.ImdbId;
        }

        return null;
    }

    private async Task<MovieMatch?> FindMovieByImdbIdAsync(string imdbId, string apiKey, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/find/{imdbId}?api_key={apiKey}&external_source=imdb_id";
        var response = await SendWithRetryAsync(url, cancellationToken).ConfigureAwait(false);
        if (response == null)
        {
            return null;
        }

        var findResult = JsonSerializer.Deserialize<TmdbFindResult>(response, JsonOptions);
        if (findResult?.MovieResults == null || findResult.MovieResults.Count == 0)
        {
            _logger.LogDebug("TMDB /find returned no movie results for IMDB ID {ImdbId}", imdbId);
            return null;
        }

        var movie = findResult.MovieResults[0];
        int? year = null;
        if (DateOnly.TryParse(movie.ReleaseDate, out var releaseDate))
        {
            year = releaseDate.Year;
        }

        _logger.LogInformation(
            "TMDB matched IMDB {ImdbId} to movie: {Title} ({Year}), TMDB ID {TmdbId}",
            imdbId, movie.Title, year, movie.Id);

        return new MovieMatch
        {
            Title = movie.Title,
            Year = year,
            TmdbMovieId = movie.Id.ToString(),
            ImdbId = imdbId
        };
    }

    private async Task<string?> SendWithRetryAsync(string url, CancellationToken cancellationToken)
    {
        await _rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var client = _httpClientFactory.CreateClient("SpecialToMovie");
            var delay = TimeSpan.FromSeconds(1);
            for (int attempt = 0; attempt <= MaxRetries; attempt++)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(15));
                using var response = await client.GetAsync(url, cts.Token).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    if (attempt == MaxRetries)
                    {
                        _logger.LogWarning("TMDB rate limit exceeded after {Retries} retries", MaxRetries);
                        return null;
                    }

                    _logger.LogDebug("TMDB rate limited, retrying in {Delay}", delay);
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("TMDB request failed with status {Status}: {Path}", response.StatusCode, StripApiKey(url));
                    return null;
                }

                if (response.Content.Headers.ContentLength > 1_000_000)
                {
                    _logger.LogWarning("TMDB response exceeded 1MB size limit");
                    return null;
                }

                return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            return null;
        }
        finally
        {
            _rateLimiter.Release();
        }
    }

    private static string StripApiKey(string url)
    {
        var idx = url.IndexOf("api_key=", StringComparison.Ordinal);
        if (idx < 0)
        {
            return url;
        }

        var end = url.IndexOf('&', idx);
        return end < 0
            ? url[..idx] + "api_key=***"
            : url[..idx] + "api_key=***" + url[end..];
    }

    // TMDB API response models

    private sealed class TmdbFindResult
    {
        [JsonPropertyName("movie_results")]
        public List<TmdbMovie>? MovieResults { get; set; }
    }

    private sealed class TmdbMovie
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("release_date")]
        public string? ReleaseDate { get; set; }
    }

    private sealed class TmdbExternalIds
    {
        [JsonPropertyName("imdb_id")]
        public string? ImdbId { get; set; }
    }
}
