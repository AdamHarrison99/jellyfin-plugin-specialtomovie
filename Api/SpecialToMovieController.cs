using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.SpecialToMovie.Data;
using Jellyfin.Plugin.SpecialToMovie.Lookup;
using Jellyfin.Plugin.SpecialToMovie.Models;
using Jellyfin.Plugin.SpecialToMovie.Services;
using Jellyfin.Plugin.SpecialToMovie.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Common.Api;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpecialToMovie.Api;

[ApiController]
[Route("SpecialToMovie")]
[Authorize(Policy = Policies.RequiresElevation)]
public class SpecialToMovieController : ControllerBase
{
    private readonly IPairStore _pairStore;
    private readonly ILibraryManager _libraryManager;
    private readonly ITaskManager _taskManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ApiResponseCache _apiCache;
    private readonly ILogger<SpecialToMovieController> _logger;

    public SpecialToMovieController(
        IPairStore pairStore,
        ILibraryManager libraryManager,
        ITaskManager taskManager,
        IHttpClientFactory httpClientFactory,
        ApiResponseCache apiCache,
        ILogger<SpecialToMovieController> logger)
    {
        _pairStore = pairStore;
        _libraryManager = libraryManager;
        _taskManager = taskManager;
        _httpClientFactory = httpClientFactory;
        _apiCache = apiCache;
        _logger = logger;
    }


    [HttpPost("RemoveAllLinks")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult RemoveAllLinks()
    {
        var pairs = _pairStore.GetAll();
        var modified = new List<LinkedPair>();
        var movieItemIds = new List<Guid?>();

        foreach (var pair in pairs)
        {
            if (pair.IsExistingMovie)
            {
                continue;
            }

            movieItemIds.Add(pair.MovieItemId);

            pair.Status = PairStatus.DryRun;
            pair.HardLinkPath = null;
            pair.MovieItemId = null;
            pair.ErrorMessage = null;
            modified.Add(pair);
        }

        // Persist the cleared MovieItemIds before deleting, so the ItemRemoved handler can no
        // longer match these pairs by movie ID and cascade into the original episodes.
        if (modified.Count > 0)
        {
            _pairStore.UpsertMany(modified);
        }

        foreach (var movieItemId in movieItemIds)
        {
            LinkedItemDeleter.DeleteWithFiles(_libraryManager, _logger, movieItemId);
        }

        _logger.LogInformation(
            "Removed {Removed} hard links. Original episode files are untouched. Pairs preserved in database.",
            modified.Count);

        return Ok(new { Removed = modified.Count, TotalPairs = pairs.Count });
    }

    [HttpPost("RunFullScan")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult RunFullScan()
    {
        _logger.LogInformation("Full scan triggered via API");
        _taskManager.QueueScheduledTask<FullScanTask>();
        return Ok(new { Status = "Started" });
    }

    [HttpPost("RunCleanup")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult RunCleanup()
    {
        _logger.LogInformation("Cleanup triggered via API");
        _taskManager.QueueScheduledTask<CleanupTask>();
        return Ok(new { Status = "Started" });
    }

    [HttpGet("Pairs")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetPairs()
    {
        var pairs = _pairStore.GetAll();
        return Ok(pairs);
    }

    [HttpPost("RemoveForceLinkedPairs")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult RemoveForceLinkedPairs([FromBody] RemoveForceLinkedPairsRequest request)
    {
        var allPairs = _pairStore.GetAll();
        var toRemove = new List<LinkedPair>();
        var toDelete = new List<Guid?>();

        foreach (var key in request.EpisodeKeys)
        {
            var matching = allPairs.Where(p =>
            {
                if (string.IsNullOrEmpty(p.EpisodePath))
                {
                    return false;
                }

                var parts = p.EpisodePath.Replace('\\', '/').Split('/');
                var fileName = parts.Length > 0 ? parts[^1] : "";
                var seriesFolder = parts.Length >= 3 ? parts[^3] : "";
                var episodeNumber = System.Text.RegularExpressions.Regex.Match(fileName, @"S00E(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Value;
                var generatedKey = !string.IsNullOrEmpty(seriesFolder) && !string.IsNullOrEmpty(episodeNumber)
                    ? seriesFolder + " " + episodeNumber
                    : "";

                return string.Equals(key, generatedKey, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(key, p.EpisodeItemId.ToString(), StringComparison.OrdinalIgnoreCase);
            });

            foreach (var pair in matching)
            {
                if (!pair.IsExistingMovie)
                {
                    toDelete.Add(pair.MovieItemId);
                }

                toRemove.Add(pair);
                _logger.LogInformation("Removed pair for deleted force link {Key}: {Title}", key, pair.MovieTitle);
            }
        }

        // Pairs first, then media: see the note in RemovePair on the ItemRemoved cascade.
        if (toRemove.Count > 0)
        {
            _pairStore.RemoveMany(toRemove.Select(p => p.Id));
        }

        foreach (var movieItemId in toDelete)
        {
            LinkedItemDeleter.DeleteWithFiles(_libraryManager, _logger, movieItemId);
        }

        return Ok(new { Removed = toRemove.Count });
    }

    [HttpPost("RemovePair")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult RemovePair([FromBody] RemovePairRequest request)
    {
        var pair = _pairStore.GetById(request.PairId);
        if (pair == null)
        {
            return NotFound(new { Message = "Pair not found" });
        }

        // Remove the pair before deleting anything: Jellyfin raises ItemRemoved for the movie, and
        // if the pair were still in the store the handler would treat it as a user-initiated
        // removal and cascade into the original episode whenever two-way deletion is enabled.
        _pairStore.Remove(pair.Id);

        // Two guards the request cannot talk its way past. A pre-existing movie belongs to the
        // user's library, not to this plugin. And deletion is gated on the *saved* setting, not on
        // the caller's word for it: the config page tracks the checkbox live, so an unsaved tick
        // would otherwise delete files the stored configuration says to keep.
        var deletedMedia = false;
        if (request.DeleteMedia && !pair.IsExistingMovie &&
            Plugin.Instance?.Configuration.AutoDeleteOnRemoval == true)
        {
            LinkedItemDeleter.DeleteWithFiles(_libraryManager, _logger, pair.MovieItemId);
            deletedMedia = true;
        }

        _logger.LogInformation(
            "Removed pair {PairId} ({Title}) via API, linked item deleted: {DeletedMedia}",
            pair.Id, pair.MovieTitle, deletedMedia);
        return Ok(new { Removed = true, DeletedMedia = deletedMedia });
    }

    [HttpPost("ClearDatabase")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult ClearDatabase()
    {
        var count = _pairStore.Clear();

        _logger.LogInformation("Database cleared: removed {Count} pairs", count);
        return Ok(new { Removed = count });
    }

    [HttpPost("ClearMetadataCache")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult ClearMetadataCache()
    {
        var count = _apiCache.Clear();
        _logger.LogInformation("Metadata cache cleared: removed {Count} entries", count);
        return Ok(new { Removed = count });
    }

    [HttpPost("TestTmdb")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> TestTmdb([FromBody] TestKeyRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ApiKey) || request.ApiKey.Length > 500)
        {
            return BadRequest(new { Success = false, Message = "API key is empty or too long" });
        }

        try
        {
            using var client = _httpClientFactory.CreateClient("SpecialToMovie");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var url = $"https://api.themoviedb.org/3/configuration?api_key={request.ApiKey}";
            using var response = await client.GetAsync(url, cts.Token).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return Ok(new { Success = true, Message = "TMDB connection successful" });
            }

            return Ok(new { Success = false, Message = $"TMDB returned {(int)response.StatusCode}: {response.ReasonPhrase}" });
        }
        catch (TaskCanceledException)
        {
            return Ok(new { Success = false, Message = "TMDB request timed out" });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TMDB connection test failed");
            return Ok(new { Success = false, Message = "Connection failed" });
        }
    }

    [HttpPost("TestTvdb")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> TestTvdb([FromBody] TestKeyRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ApiKey) || request.ApiKey.Length > 500)
        {
            return BadRequest(new { Success = false, Message = "API key is empty or too long" });
        }

        try
        {
            using var client = _httpClientFactory.CreateClient("SpecialToMovie");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var loginBody = JsonSerializer.Serialize(new { apikey = request.ApiKey });
            using var content = new StringContent(loginBody, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("https://api4.thetvdb.com/v4/login", content, cts.Token).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return Ok(new { Success = true, Message = "TVDB connection successful" });
            }

            return Ok(new { Success = false, Message = $"TVDB returned {(int)response.StatusCode}: {response.ReasonPhrase}" });
        }
        catch (TaskCanceledException)
        {
            return Ok(new { Success = false, Message = "TVDB request timed out" });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TVDB connection test failed");
            return Ok(new { Success = false, Message = "Connection failed" });
        }
    }

    public class TestKeyRequest
    {
        public string ApiKey { get; set; } = string.Empty;
    }

    public class RemovePairRequest
    {
        public Guid PairId { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the plugin-managed movie item and its files
        /// should be deleted along with the pair. Ignored for pre-existing movies.
        /// </summary>
        public bool DeleteMedia { get; set; }
    }

    public class RemoveForceLinkedPairsRequest
    {
        public List<string> EpisodeKeys { get; set; } = new();
    }
}
