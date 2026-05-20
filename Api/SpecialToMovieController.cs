using Jellyfin.Plugin.SpecialToMovie.Data;
using Jellyfin.Plugin.SpecialToMovie.HardLink;
using Jellyfin.Plugin.SpecialToMovie.Models;
using Jellyfin.Plugin.SpecialToMovie.Services;
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
    private readonly SpecialDetectionService _detectionService;
    private readonly ILogger<SpecialToMovieController> _logger;

    public SpecialToMovieController(
        IPairStore pairStore,
        IHardLinkService hardLinkService,
        SpecialDetectionService detectionService,
        ILogger<SpecialToMovieController> logger)
    {
        _pairStore = pairStore;
        _hardLinkService = hardLinkService;
        _detectionService = detectionService;
        _logger = logger;
    }

    [HttpPost("RemoveAllLinks")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult RemoveAllLinks()
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return BadRequest("Plugin not initialized");
        }

        if (config.DryRunMode)
        {
            return BadRequest("Dry run mode is active — disable it before removing hard links");
        }

        var pairs = _pairStore.GetAll();
        var removed = 0;

        foreach (var pair in pairs)
        {
            if (!pair.IsExistingMovie && !string.IsNullOrEmpty(pair.HardLinkPath))
            {
                _hardLinkService.DeleteMovieFolder(pair.HardLinkPath);
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
    public async Task<ActionResult> RunFullScan()
    {
        _logger.LogInformation("Full scan triggered via API");
        await _detectionService.RunFullScanAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        return Ok(new { Status = "Complete" });
    }

    [HttpGet("Pairs")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetPairs()
    {
        var pairs = _pairStore.GetAll();
        return Ok(pairs);
    }
}
