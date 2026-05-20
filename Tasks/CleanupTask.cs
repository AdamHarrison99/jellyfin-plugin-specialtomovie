using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SpecialToMovie.Data;
using Jellyfin.Plugin.SpecialToMovie.HardLink;
using Jellyfin.Plugin.SpecialToMovie.Models;
using Jellyfin.Plugin.SpecialToMovie.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpecialToMovie.Tasks;

public class CleanupTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IPairStore _pairStore;
    private readonly IHardLinkService _hardLinkService;
    private readonly WatchSyncService _watchSyncService;
    private readonly ILogger<CleanupTask> _logger;

    public CleanupTask(
        ILibraryManager libraryManager,
        IPairStore pairStore,
        IHardLinkService hardLinkService,
        WatchSyncService watchSyncService,
        ILogger<CleanupTask> logger)
    {
        _libraryManager = libraryManager;
        _pairStore = pairStore;
        _hardLinkService = hardLinkService;
        _watchSyncService = watchSyncService;
        _logger = logger;
    }

    public string Name => "SpecialToMovie: Cleanup";

    public string Key => "SpecialToMovieCleanup";

    public string Description => "Validates all linked pairs: promotes pending pairs to active, recreates missing hard links, and retries errors. Orphaned hard link folders are only deleted when 'auto-delete on removal' is enabled in the plugin settings.";

    public string Category => "SpecialToMovie";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting SpecialToMovie cleanup");
        progress.Report(0);

        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return Task.CompletedTask;
        }

        var pairs = _pairStore.GetAll();
        var total = pairs.Count;

        // Batch-fetch all episodes and movies once to avoid N+1 queries
        var allEpisodes = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            ParentIndexNumber = 0,
            Recursive = true
        }).ToDictionary(e => e.Id);

        var moviesByPath = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie],
            Recursive = true
        }).Where(m => !string.IsNullOrEmpty(m.Path))
         .GroupBy(m => m.Path, StringComparer.OrdinalIgnoreCase)
         .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pair = pairs[i];
            ValidatePair(pair, config.DryRunMode, allEpisodes, moviesByPath);

            progress.Report((double)(i + 1) / total * 100);
        }

        _logger.LogInformation("SpecialToMovie cleanup finished, validated {Count} pairs", total);
        return Task.CompletedTask;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        var intervalHours = Math.Max(1, Plugin.Instance?.Configuration.CleanupIntervalHours ?? 12);

        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(intervalHours).Ticks
        };
    }

    private void ValidatePair(LinkedPair pair, bool dryRunMode, Dictionary<Guid, BaseItem> allEpisodes, Dictionary<string, BaseItem> moviesByPath)
    {
        allEpisodes.TryGetValue(pair.EpisodeItemId, out var episode);

        // Episode gone — clean up
        if (episode == null)
        {
            _logger.LogInformation("Episode {Id} no longer exists, removing pair {PairId}", pair.EpisodeItemId, pair.Id);

            if (!dryRunMode && !pair.IsExistingMovie && !string.IsNullOrEmpty(pair.HardLinkPath))
            {
                _hardLinkService.DeleteMovieFolder(pair.HardLinkPath);
            }

            _pairStore.Remove(pair.Id);
            return;
        }

        // Skip further checks for DryRun pairs or when in dry run mode
        if (pair.Status == PairStatus.DryRun || dryRunMode)
        {
            return;
        }

        // Hard link gone but episode still exists — recreate
        if (!pair.IsExistingMovie && !string.IsNullOrEmpty(pair.HardLinkPath) &&
            !_hardLinkService.Exists(pair.HardLinkPath))
        {
            _logger.LogInformation("Hard link missing at {Path}, recreating", pair.HardLinkPath);

            if (_hardLinkService.Create(episode.Path, pair.HardLinkPath))
            {
                pair.Status = PairStatus.Pending;
                pair.MovieItemId = null;
                pair.ErrorMessage = null;
                _pairStore.Upsert(pair);
            }
            else
            {
                pair.Status = PairStatus.Error;
                pair.ErrorMessage = "Failed to recreate hard link";
                _pairStore.Upsert(pair);
            }

            return;
        }

        // Pending pair — check if movie has been scanned
        if (pair.Status == PairStatus.Pending && pair.MovieItemId == null)
        {
            var movieAtPath = FindMovieAtPath(pair.HardLinkPath, moviesByPath);
            if (movieAtPath != null)
            {
                pair.MovieItemId = movieAtPath.Id;
                pair.Status = PairStatus.Active;
                pair.ErrorMessage = null;
                _pairStore.Upsert(pair);
                _watchSyncService.SyncInitialWatchState(pair);
                _logger.LogInformation("Promoted pending pair to active: {Title}", pair.MovieTitle);
            }
        }

        // Error pair — retry if conditions may have changed
        if (pair.Status == PairStatus.Error)
        {
            if (!string.IsNullOrEmpty(pair.HardLinkPath) && _hardLinkService.Exists(pair.HardLinkPath))
            {
                var movieAtPath = FindMovieAtPath(pair.HardLinkPath, moviesByPath);
                if (movieAtPath != null)
                {
                    pair.MovieItemId = movieAtPath.Id;
                    pair.Status = PairStatus.Active;
                    pair.ErrorMessage = null;
                    _pairStore.Upsert(pair);
                    _watchSyncService.SyncInitialWatchState(pair);
                    _logger.LogInformation("Error pair recovered: {Title}", pair.MovieTitle);
                }
            }
        }
    }

    private static BaseItem? FindMovieAtPath(string? hardLinkPath, Dictionary<string, BaseItem> moviesByPath)
    {
        if (string.IsNullOrEmpty(hardLinkPath))
        {
            return null;
        }

        moviesByPath.TryGetValue(hardLinkPath, out var movie);
        return movie;
    }
}
