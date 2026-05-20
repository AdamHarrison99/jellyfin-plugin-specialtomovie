using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.SpecialToMovie.Models;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpecialToMovie.Lookup;

public class TvdbLookupService : IMetadataLookupService, IDisposable
{
    private const string BaseUrl = "https://api4.thetvdb.com/v4";
    private const int MaxRetries = 3;
    private const int MaxLevenshteinDistance = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(23);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TvdbLookupService> _logger;
    private readonly SemaphoreSlim _rateLimiter = new(5, 5);
    private readonly SemaphoreSlim _authLock = new(1, 1);

    private volatile string? _bearerToken;
    private long _tokenExpiryTicks;

    public TvdbLookupService(IHttpClientFactory httpClientFactory, ILogger<TvdbLookupService> logger)
    {
        _httpClientFactory = httpClientFactory;
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

        // Primary: check for linkedMovie via episode extended endpoint
        var tvdbId = episode.GetProviderId(MetadataProvider.Tvdb);
        if (!string.IsNullOrEmpty(tvdbId))
        {
            var match = await FindLinkedMovieAsync(tvdbId, token, cancellationToken).ConfigureAwait(false);
            if (match != null)
            {
                return match;
            }
        }

        // Fallback: search by episode name
        return await SearchByNameAsync(episode, token, cancellationToken).ConfigureAwait(false);
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
        var linkedMovieId = episodeData?.Data?.LinkedMovie;
        if (linkedMovieId == null)
        {
            _logger.LogDebug("TVDB episode {Id} has no linkedMovie", episodeTvdbId);
            return null;
        }

        return await GetMovieByIdAsync(linkedMovieId.Value, token, cancellationToken).ConfigureAwait(false);
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

        _logger.LogInformation(
            "TVDB matched linkedMovie: {Title} ({Year}), TVDB ID {TvdbId}",
            movie.Name, year, movie.Id);

        return new MovieMatch
        {
            Title = movie.Name ?? string.Empty,
            Year = year,
            TvdbMovieId = movie.Id?.ToString(),
            ImdbId = imdbId
        };
    }

    private async Task<MovieMatch?> SearchByNameAsync(Episode episode, string token, CancellationToken cancellationToken)
    {
        var name = episode.Name;
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var url = $"{BaseUrl}/search?query={Uri.EscapeDataString(name)}&type=movie";
        var response = await SendWithRetryAsync(url, token, cancellationToken).ConfigureAwait(false);
        if (response == null)
        {
            return null;
        }

        var searchResult = JsonSerializer.Deserialize<TvdbResponse<List<TvdbSearchResult>>>(response, JsonOptions);
        if (searchResult?.Data == null || searchResult.Data.Count == 0)
        {
            _logger.LogDebug("TVDB search returned no results for {Name}", name);
            return null;
        }

        // Find best match by title similarity and year proximity
        int? episodeYear = episode.PremiereDate?.Year;

        foreach (var result in searchResult.Data)
        {
            if (string.IsNullOrEmpty(result.Name))
            {
                continue;
            }

            var titleMatch = IsCloseMatch(name, result.Name);
            if (!titleMatch)
            {
                continue;
            }

            // Year proximity check if both have years
            int? resultYear = null;
            if (!string.IsNullOrEmpty(result.Year) &&
                int.TryParse(result.Year, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ry))
            {
                resultYear = ry;
            }

            if (episodeYear.HasValue && resultYear.HasValue &&
                Math.Abs(episodeYear.Value - resultYear.Value) > 1)
            {
                continue;
            }

            _logger.LogInformation(
                "TVDB search matched {EpisodeName} to movie: {MovieTitle} ({Year})",
                name, result.Name, result.Year);

            // Retrieve full movie details if we have a TVDB movie ID
            if (!string.IsNullOrEmpty(result.TvdbId) &&
                long.TryParse(result.TvdbId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var movieId))
            {
                return await GetMovieByIdAsync(movieId, token, cancellationToken).ConfigureAwait(false);
            }

            return new MovieMatch
            {
                Title = result.Name,
                Year = resultYear,
                TvdbMovieId = result.TvdbId
            };
        }

        _logger.LogDebug("TVDB search found no close match for {Name}", name);
        return null;
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

    private static bool IsCloseMatch(string source, string candidate)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        // Substring containment
        if (candidate.Contains(source, StringComparison.OrdinalIgnoreCase) ||
            source.Contains(candidate, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Skip expensive Levenshtein for very long titles
        if (source.Length > 200 || candidate.Length > 200)
        {
            return false;
        }

        // Levenshtein distance on normalized strings
        var a = source.ToUpperInvariant();
        var b = candidate.ToUpperInvariant();
        return LevenshteinDistance(a, b) <= MaxLevenshteinDistance;
    }

    private static int LevenshteinDistance(string s, string t)
    {
        var n = s.Length;
        var m = t.Length;

        if (Math.Abs(n - m) > MaxLevenshteinDistance)
        {
            return Math.Abs(n - m);
        }

        var prev = new int[m + 1];
        var curr = new int[m + 1];

        for (var j = 0; j <= m; j++)
        {
            prev[j] = j;
        }

        for (var i = 1; i <= n; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= m; j++)
            {
                var cost = s[i - 1] == t[j - 1] ? 0 : 1;
                curr[j] = Math.Min(
                    Math.Min(prev[j] + 1, curr[j - 1] + 1),
                    prev[j - 1] + cost);
            }

            (prev, curr) = (curr, prev);
        }

        return prev[m];
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
    }

    private sealed class TvdbMovie
    {
        [JsonPropertyName("id")]
        public long? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

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

    private sealed class TvdbSearchResult
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("year")]
        public string? Year { get; set; }

        [JsonPropertyName("tvdb_id")]
        public string? TvdbId { get; set; }
    }
}
