using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.SpecialToMovie.Models;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpecialToMovie.Lookup;

public class TvdbLookupService : IMetadataLookupService, IDisposable
{
    private const string BaseUrl = "https://api4.thetvdb.com/v4";
    private const int MaxRetries = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(23);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServerConfigurationManager _configManager;
    private readonly ILogger<TvdbLookupService> _logger;
    private readonly SemaphoreSlim _rateLimiter = new(5, 5);
    private readonly SemaphoreSlim _authLock = new(1, 1);

    private volatile string? _bearerToken;
    private long _tokenExpiryTicks;

    public TvdbLookupService(IHttpClientFactory httpClientFactory, IServerConfigurationManager configManager, ILogger<TvdbLookupService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configManager = configManager;
        _logger = logger;
    }

    public void Dispose()
    {
        _rateLimiter.Dispose();
        _authLock.Dispose();
    }

    public async Task<MovieMatch?> LookupAsync(Episode episode, CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null || string.IsNullOrEmpty(config.TvdbApiKey))
        {
            _logger.LogDebug("TVDB API key not configured, skipping TVDB lookup");
            return null;
        }

        await EnsureAuthenticatedAsync(config.TvdbApiKey, cancellationToken).ConfigureAwait(false);
        var token = _bearerToken;
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var tvdbId = episode.GetProviderId(MetadataProvider.Tvdb);
        if (string.IsNullOrEmpty(tvdbId))
        {
            _logger.LogDebug("No TVDB ID for episode {Name}, skipping TVDB lookup", episode.Name);
            return null;
        }

        return await FindLinkedMovieAsync(tvdbId, token, cancellationToken).ConfigureAwait(false);
    }

    private async Task<MovieMatch?> FindLinkedMovieAsync(string episodeTvdbId, string token, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/episodes/{episodeTvdbId}/extended";
        var response = await SendWithRetryAsync(url, token, cancellationToken).ConfigureAwait(false);
        if (response == null)
        {
            return null;
        }

        var episodeData = JsonSerializer.Deserialize<TvdbResponse<TvdbEpisodeExtended>>(response, JsonOptions);
        var episode = episodeData?.Data;
        if (episode == null)
        {
            return null;
        }

        if (episode.LinkedMovie != null)
        {
            return await GetMovieByIdAsync(episode.LinkedMovie.Value, token, cancellationToken).ConfigureAwait(false);
        }

        // Fallback: check if the episode is tagged as "Special Category: Movies"
        var isMovieTag = episode.TagOptions?.Any(t =>
            string.Equals(t.TagName, "Special Category", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(t.Name, "Movies", StringComparison.OrdinalIgnoreCase)) == true;

        if (!isMovieTag)
        {
            _logger.LogDebug("TVDB episode {Id} has no linkedMovie and no movie tag", episodeTvdbId);
            return null;
        }

        _logger.LogInformation(
            "TVDB episode {Id} has Special Category 'Movies' tag, using episode metadata as movie match",
            episodeTvdbId);

        int? year = null;
        if (!string.IsNullOrEmpty(episode.Year) &&
            int.TryParse(episode.Year, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
        {
            year = y;
        }

        var imdbId = episode.RemoteIds?
            .FirstOrDefault(r => string.Equals(r.SourceName, "IMDB", StringComparison.OrdinalIgnoreCase))
            ?.Id;

        var title = episode.Name ?? string.Empty;
        var translatedTitle = await GetEpisodeTranslatedTitleAsync(episodeTvdbId, token, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(translatedTitle))
        {
            title = translatedTitle;
        }

        return new MovieMatch
        {
            Title = title,
            Year = year,
            ImdbId = imdbId
        };
    }

    private async Task<MovieMatch?> GetMovieByIdAsync(long movieId, string token, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/movies/{movieId}";
        var response = await SendWithRetryAsync(url, token, cancellationToken).ConfigureAwait(false);
        if (response == null)
        {
            return null;
        }

        var movieData = JsonSerializer.Deserialize<TvdbResponse<TvdbMovie>>(response, JsonOptions);
        var movie = movieData?.Data;
        if (movie == null)
        {
            return null;
        }

        int? year = null;
        if (!string.IsNullOrEmpty(movie.Year))
        {
            if (int.TryParse(movie.Year, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
            {
                year = y;
            }
        }

        // Extract IMDB ID from remoteIds if available
        var imdbId = movie.RemoteIds?
            .FirstOrDefault(r => string.Equals(r.SourceName, "IMDB", StringComparison.OrdinalIgnoreCase))
            ?.Id;

        // Prefer English title over original language
        var title = movie.Name ?? string.Empty;
        var engTitle = await GetTranslatedTitleAsync(movieId, token, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(engTitle))
        {
            title = engTitle;
        }

        _logger.LogInformation(
            "TVDB matched linkedMovie: {Title} ({Year}), TVDB ID {TvdbId}",
            title, year, movie.Id);

        return new MovieMatch
        {
            Title = title,
            Year = year,
            TvdbMovieId = movie.Id?.ToString(),
            TvdbMovieSlug = movie.Slug,
            ImdbId = imdbId
        };
    }

    private async Task<string?> GetEpisodeTranslatedTitleAsync(string episodeId, string token, CancellationToken cancellationToken)
    {
        var lang = GetTvdbLanguageCode();
        var url = $"{BaseUrl}/episodes/{episodeId}/translations/{lang}";
        var response = await SendWithRetryAsync(url, token, cancellationToken).ConfigureAwait(false);
        if (response != null)
        {
            var translationData = JsonSerializer.Deserialize<TvdbResponse<TvdbTranslation>>(response, JsonOptions);
            if (!string.IsNullOrEmpty(translationData?.Data?.Name))
            {
                return translationData.Data.Name;
            }
        }

        if (!string.Equals(lang, "eng", StringComparison.Ordinal))
        {
            var engUrl = $"{BaseUrl}/episodes/{episodeId}/translations/eng";
            var engResponse = await SendWithRetryAsync(engUrl, token, cancellationToken).ConfigureAwait(false);
            if (engResponse != null)
            {
                var engData = JsonSerializer.Deserialize<TvdbResponse<TvdbTranslation>>(engResponse, JsonOptions);
                if (!string.IsNullOrEmpty(engData?.Data?.Name))
                {
                    return engData.Data.Name;
                }
            }
        }

        return null;
    }

    private async Task<string?> GetTranslatedTitleAsync(long movieId, string token, CancellationToken cancellationToken)
    {
        var lang = GetTvdbLanguageCode();
        var url = $"{BaseUrl}/movies/{movieId}/translations/{lang}";
        var response = await SendWithRetryAsync(url, token, cancellationToken).ConfigureAwait(false);
        if (response != null)
        {
            var translationData = JsonSerializer.Deserialize<TvdbResponse<TvdbTranslation>>(response, JsonOptions);
            if (!string.IsNullOrEmpty(translationData?.Data?.Name))
            {
                return translationData.Data.Name;
            }
        }

        // Fall back to English if preferred language not available
        if (!string.Equals(lang, "eng", StringComparison.Ordinal))
        {
            var engUrl = $"{BaseUrl}/movies/{movieId}/translations/eng";
            var engResponse = await SendWithRetryAsync(engUrl, token, cancellationToken).ConfigureAwait(false);
            if (engResponse != null)
            {
                var engData = JsonSerializer.Deserialize<TvdbResponse<TvdbTranslation>>(engResponse, JsonOptions);
                if (!string.IsNullOrEmpty(engData?.Data?.Name))
                {
                    return engData.Data.Name;
                }
            }
        }

        return null;
    }

    private string GetTvdbLanguageCode()
    {
        var jellyfinLang = _configManager.Configuration.PreferredMetadataLanguage;
        if (string.IsNullOrEmpty(jellyfinLang))
        {
            return "eng";
        }

        // Convert ISO 639-1 (2-letter) to ISO 639-2/B (3-letter) for TVDB
        try
        {
            var culture = new CultureInfo(jellyfinLang);
            return culture.ThreeLetterISOLanguageName;
        }
        catch (CultureNotFoundException)
        {
            return "eng";
        }
    }

    private async Task EnsureAuthenticatedAsync(string apiKey, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_bearerToken) && DateTime.UtcNow.Ticks < Interlocked.Read(ref _tokenExpiryTicks))
        {
            return;
        }

        await _authLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check after acquiring lock
            if (!string.IsNullOrEmpty(_bearerToken) && DateTime.UtcNow.Ticks < Interlocked.Read(ref _tokenExpiryTicks))
            {
                return;
            }

            using var client = _httpClientFactory.CreateClient("SpecialToMovie");
            var loginBody = JsonSerializer.Serialize(new { apikey = apiKey });
            using var content = new StringContent(loginBody, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync($"{BaseUrl}/login", content, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("TVDB authentication failed with status {Status}", response.StatusCode);
                _bearerToken = null;
                return;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var loginResult = JsonSerializer.Deserialize<TvdbResponse<TvdbLoginToken>>(body, JsonOptions);

            _bearerToken = loginResult?.Data?.Token;
            Interlocked.Exchange(ref _tokenExpiryTicks, DateTime.UtcNow.Add(TokenLifetime).Ticks);

            if (string.IsNullOrEmpty(_bearerToken))
            {
                _logger.LogError("TVDB login returned empty token");
            }
            else
            {
                _logger.LogInformation("TVDB authenticated successfully");
            }
        }
        finally
        {
            _authLock.Release();
        }
    }

    private async Task<string?> SendWithRetryAsync(string url, string token, CancellationToken cancellationToken)
    {
        await _rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var client = _httpClientFactory.CreateClient("SpecialToMovie");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
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
                        _logger.LogWarning("TVDB rate limit exceeded after {Retries} retries", MaxRetries);
                        return null;
                    }

                    _logger.LogDebug("TVDB rate limited, retrying in {Delay}", delay);
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    _logger.LogWarning("TVDB token expired, clearing for re-auth");
                    _bearerToken = null;
                    return null;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("TVDB request failed with status {Status}: {Url}", response.StatusCode, url);
                    return null;
                }

                if (response.Content.Headers.ContentLength > 1_000_000)
                {
                    _logger.LogWarning("TVDB response exceeded 1MB size limit");
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

    // TVDB API response models

    private sealed class TvdbResponse<T>
    {
        [JsonPropertyName("data")]
        public T? Data { get; set; }
    }

    private sealed class TvdbLoginToken
    {
        [JsonPropertyName("token")]
        public string? Token { get; set; }
    }

    private sealed class TvdbEpisodeExtended
    {
        [JsonPropertyName("id")]
        public long? Id { get; set; }

        [JsonPropertyName("linkedMovie")]
        public long? LinkedMovie { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("year")]
        public string? Year { get; set; }

        [JsonPropertyName("remoteIds")]
        public List<TvdbRemoteId>? RemoteIds { get; set; }

        [JsonPropertyName("tagOptions")]
        public List<TvdbTagOption>? TagOptions { get; set; }
    }

    private sealed class TvdbTagOption
    {
        [JsonPropertyName("tagName")]
        public string? TagName { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private sealed class TvdbMovie
    {
        [JsonPropertyName("id")]
        public long? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("slug")]
        public string? Slug { get; set; }

        [JsonPropertyName("year")]
        public string? Year { get; set; }

        [JsonPropertyName("remoteIds")]
        public List<TvdbRemoteId>? RemoteIds { get; set; }
    }

    private sealed class TvdbRemoteId
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("sourceName")]
        public string? SourceName { get; set; }
    }

    private sealed class TvdbTranslation
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
