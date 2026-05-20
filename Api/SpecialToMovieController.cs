using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.SpecialToMovie.Data;
using Jellyfin.Plugin.SpecialToMovie.HardLink;
using Jellyfin.Plugin.SpecialToMovie.Models;
using Jellyfin.Plugin.SpecialToMovie.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Common.Api;
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
    private readonly IHardLinkService _hardLinkService;
    private readonly ILibraryManager _libraryManager;
    private readonly SpecialDetectionService _detectionService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SpecialToMovieController> _logger;

    public SpecialToMovieController(
        IPairStore pairStore,
        IHardLinkService hardLinkService,
        ILibraryManager libraryManager,
        SpecialDetectionService detectionService,
        IHttpClientFactory httpClientFactory,
        ILogger<SpecialToMovieController> logger)
    {
        _pairStore = pairStore;
        _hardLinkService = hardLinkService;
        _libraryManager = libraryManager;
        _detectionService = detectionService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private void RemoveMovieFromLibrary(Guid? movieItemId)
    {
        if (movieItemId == null || movieItemId == Guid.Empty)
        {
            return;
        }

        var item = _libraryManager.GetItemById(movieItemId.Value);
        if (item == null)
        {
            return;
        }

        try
        {
            _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false });
            _logger.LogInformation("Removed movie {Name} from Jellyfin library", item.Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove movie {Id} from Jellyfin library", movieItemId);
        }
    }

    [HttpPost("RemoveAllLinks")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult RemoveAllLinks()
    {

        var pairs = _pairStore.GetAll();
        var removed = 0;

        foreach (var pair in pairs)
        {
            if (!pair.IsExistingMovie && !string.IsNullOrEmpty(pair.HardLinkPath))
            {
                _hardLinkService.DeleteMovieFolder(pair.HardLinkPath);
                RemoveMovieFromLibrary(pair.MovieItemId);
                removed++;
            }

            pair.Status = PairStatus.DryRun;
            pair.HardLinkPath = null;
            pair.MovieItemId = null;
            pair.ErrorMessage = null;
            _pairStore.Upsert(pair);
        }

        _logger.LogInformation(
            "Removed {Removed} hard links. Original episode files are untouched. Pairs preserved in database.",
            removed);

        return Ok(new { Removed = removed, TotalPairs = pairs.Count });
    }

    [HttpPost("RunFullScan")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult RunFullScan()
    {
        _logger.LogInformation("Full scan triggered via API");
        _ = Task.Run(async () =>
        {
            try
            {
                await _detectionService.RunFullScanAsync(null, CancellationToken.None).ConfigureAwait(false);
                _logger.LogInformation("Full scan completed (triggered via API)");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Full scan failed");
            }
        });

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
        var removed = 0;
        var allPairs = _pairStore.GetAll();

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
            }).ToList();

            foreach (var pair in matching)
            {
                if (!pair.IsExistingMovie && !string.IsNullOrEmpty(pair.HardLinkPath))
                {
                    _hardLinkService.DeleteMovieFolder(pair.HardLinkPath);
                    RemoveMovieFromLibrary(pair.MovieItemId);
                }

                _pairStore.Remove(pair.Id);
                removed++;
                _logger.LogInformation("Removed pair for deleted force link {Key}: {Title}", key, pair.MovieTitle);
            }
        }

        return Ok(new { Removed = removed });
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

        _pairStore.Remove(pair.Id);
        _logger.LogInformation("Removed pair {PairId} ({Title}) via API", pair.Id, pair.MovieTitle);
        return Ok(new { Removed = true });
    }

    [HttpPost("ClearDatabase")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult ClearDatabase()
    {
        var pairs = _pairStore.GetAll();
        var count = pairs.Count;

        foreach (var pair in pairs)
        {
            _pairStore.Remove(pair.Id);
        }

        _logger.LogInformation("Database cleared: removed {Count} pairs", count);
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
            return Ok(new { Success = false, Message = $"Connection failed: {ex.Message}" });
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
            return Ok(new { Success = false, Message = $"Connection failed: {ex.Message}" });
        }
    }

    public class TestKeyRequest
    {
        public string ApiKey { get; set; } = string.Empty;
    }

    public class RemovePairRequest
    {
        public Guid PairId { get; set; }
    }

    public class RemoveForceLinkedPairsRequest
    {
        public List<string> EpisodeKeys { get; set; } = new();
    }
}
