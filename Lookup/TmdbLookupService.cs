using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.SpecialToMovie.Models;
using MediaBrowser.Controller.Configuration;
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
    private readonly IServerConfigurationManager _configManager;
    private readonly ILogger<TmdbLookupService> _logger;
    private readonly SemaphoreSlim _rateLimiter = new(30, 30);
    private readonly ConcurrentDictionary<string, long> _notFoundCache = new();

    public TmdbLookupService(IHttpClientFactory httpClientFactory, IServerConfigurationManager configManager, ILogger<TmdbLookupService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configManager = configManager;
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

    public async Task<MovieMatch?> EnrichWithTmdbAsync(MovieMatch match, CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null || string.IsNullOrEmpty(config.TmdbApiKey))
        {
            return match;
        }

        if (!string.IsNullOrEmpty(match.ImdbId))
        {
            var found = await FindMovieByImdbIdAsync(match.ImdbId, config.TmdbApiKey, cancellationToken).ConfigureAwait(false);
            if (found != null)
            {
                return match with { Title = found.Title, TmdbMovieId = found.TmdbMovieId, ImdbId = found.ImdbId };
            }
        }

        if (string.IsNullOrEmpty(match.Title))
        {
            return match;
        }

        var lang = _configManager.Configuration.PreferredMetadataLanguage ?? "en";
        var encodedTitle = Uri.EscapeDataString(match.Title);
        var url = $"{BaseUrl}/search/movie?api_key={config.TmdbApiKey}&query={encodedTitle}&language={lang}";
        if (match.Year.HasValue)
        {
            url += $"&year={match.Year.Value}";
        }

        var response = await SendWithRetryAsync(url, cancellationToken).ConfigureAwait(false);
        if (response == null)
        {
            return match;
        }

        var searchResult = JsonSerializer.Deserialize<TmdbSearchResult>(response, JsonOptions);
        if (searchResult?.Results == null || searchResult.Results.Count == 0)
        {
            _logger.LogDebug("TMDB search returned no results for '{Title}' ({Year})", match.Title, match.Year);
            return match;
        }

        var best = searchResult.Results.FirstOrDefault(m =>
            string.Equals(m.Title, match.Title, StringComparison.OrdinalIgnoreCase));
        best ??= searchResult.Results[0];

        int? year = null;
        if (DateOnly.TryParse(best.ReleaseDate, out var releaseDate))
        {
            year = releaseDate.Year;
        }

        var title = await ResolveLocalizedTitleAsync(best.Id, best.Title, best.OriginalTitle, config.TmdbApiKey, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "TMDB search enriched tag-based match: {Title} ({Year}), TMDB ID {TmdbId}",
            title, year, best.Id);

        return match with { Title = title, TmdbMovieId = best.Id.ToString() };
    }

    private async Task<string?> GetImdbIdFromTmdbAsync(Episode episode, string apiKey, CancellationToken cancellationToken)
    {
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
        var lang = _configManager.Configuration.PreferredMetadataLanguage ?? "en";
        var url = $"{BaseUrl}/find/{imdbId}?api_key={apiKey}&external_source=imdb_id&language={lang}";
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

        var title = await ResolveLocalizedTitleAsync(movie.Id, movie.Title, movie.OriginalTitle, apiKey, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "TMDB matched IMDB {ImdbId} to movie: {Title} ({Year}), TMDB ID {TmdbId}",
            imdbId, title, year, movie.Id);

        return new MovieMatch
        {
            Title = title,
            Year = year,
            TmdbMovieId = movie.Id.ToString(),
            ImdbId = imdbId
        };
    }

    private async Task<string> ResolveLocalizedTitleAsync(int movieId, string title, string? originalTitle, string apiKey, CancellationToken cancellationToken)
    {
        if (!string.Equals(title, originalTitle, StringComparison.Ordinal))
        {
            return title;
        }

        var country = GetUserCountryCode();
        var url = $"{BaseUrl}/movie/{movieId}/alternative_titles?api_key={apiKey}";
        var response = await SendWithRetryAsync(url, cancellationToken).ConfigureAwait(false);
        if (response == null)
        {
            return title;
        }

        var altTitles = JsonSerializer.Deserialize<TmdbAlternativeTitlesResult>(response, JsonOptions);
        if (altTitles?.Titles == null || altTitles.Titles.Count == 0)
        {
            return title;
        }

        var localized = altTitles.Titles.FirstOrDefault(t =>
            string.Equals(t.Country, country, StringComparison.OrdinalIgnoreCase));
        localized ??= altTitles.Titles.FirstOrDefault(t =>
            string.Equals(t.Country, "US", StringComparison.OrdinalIgnoreCase));
        localized ??= altTitles.Titles.FirstOrDefault(t =>
            string.Equals(t.Country, "GB", StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(localized?.Title))
        {
            _logger.LogDebug("Using alternative title '{AltTitle}' ({Country}) instead of '{Original}'",
                localized.Title, localized.Country, title);
            return localized.Title;
        }

        return title;
    }

    private string GetUserCountryCode()
    {
        var country = _configManager.Configuration.MetadataCountryCode;
        if (!string.IsNullOrEmpty(country))
        {
            return country;
        }

        var lang = _configManager.Configuration.PreferredMetadataLanguage ?? "en";
        return lang switch
        {
            "en" => "US",
            "ja" => "JP",
            "de" => "DE",
            "fr" => "FR",
            "es" => "ES",
            "it" => "IT",
            "pt" => "BR",
            "ko" => "KR",
            "zh" => "CN",
            _ => "US"
        };
    }

    private async Task<string?> SendWithRetryAsync(string url, CancellationToken cancellationToken)
    {
        var cacheKey = StripApiKey(url);
        var cacheDays = Math.Max(1, Plugin.Instance?.Configuration.NotFoundCacheDays ?? 14);
        if (_notFoundCache.TryGetValue(cacheKey, out var cachedTicks) &&
            (DateTime.UtcNow.Ticks - cachedTicks) < TimeSpan.FromDays(cacheDays).Ticks)
        {
            _logger.LogDebug("TMDB cache hit (previous 404): {Path}", cacheKey);
            return null;
        }

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

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    _notFoundCache[cacheKey] = DateTime.UtcNow.Ticks;
                    _logger.LogDebug("TMDB 404 cached: {Path}", cacheKey);
                    return null;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("TMDB request failed with status {Status}: {Path}", response.StatusCode, cacheKey);
                    return null;
                }

                var contentLength = response.Content.Headers.ContentLength;
                if (contentLength.HasValue && contentLength.Value > 1_000_000)
                {
                    _logger.LogWarning("TMDB response exceeded 1MB size limit");
                    return null;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (body.Length > 1_000_000)
                {
                    _logger.LogWarning("TMDB response body exceeded 1MB size limit");
                    return null;
                }

                return body;
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

    private sealed class TmdbSearchResult
    {
        [JsonPropertyName("results")]
        public List<TmdbMovie>? Results { get; set; }
    }

    private sealed class TmdbMovie
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("original_title")]
        public string? OriginalTitle { get; set; }

        [JsonPropertyName("release_date")]
        public string? ReleaseDate { get; set; }
    }

    private sealed class TmdbExternalIds
    {
        [JsonPropertyName("imdb_id")]
        public string? ImdbId { get; set; }
    }

    private sealed class TmdbAlternativeTitlesResult
    {
        [JsonPropertyName("titles")]
        public List<TmdbAlternativeTitle>? Titles { get; set; }
    }

    private sealed class TmdbAlternativeTitle
    {
        [JsonPropertyName("iso_3166_1")]
        public string? Country { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }
    }
}
