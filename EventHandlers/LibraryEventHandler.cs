using Jellyfin.Plugin.SpecialToMovie.Data;
using Jellyfin.Plugin.SpecialToMovie.HardLink;
using Jellyfin.Plugin.SpecialToMovie.Models;
using Jellyfin.Plugin.SpecialToMovie.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpecialToMovie.EventHandlers;

public class LibraryEventHandler : IHostedService, IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly IPairStore _pairStore;
    private readonly IHardLinkService _hardLinkService;
    private readonly SpecialDetectionService _detectionService;
    private readonly WatchSyncService _watchSyncService;
    private readonly ILogger<LibraryEventHandler> _logger;
    private CancellationTokenSource? _cts;

    public LibraryEventHandler(
        ILibraryManager libraryManager,
        IPairStore pairStore,
        IHardLinkService hardLinkService,
        SpecialDetectionService detectionService,
        WatchSyncService watchSyncService,
        ILogger<LibraryEventHandler> logger)
    {
        _libraryManager = libraryManager;
        _pairStore = pairStore;
        _hardLinkService = hardLinkService;
        _detectionService = detectionService;
        _watchSyncService = watchSyncService;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _libraryManager.ItemAdded += OnItemAdded;
        _libraryManager.ItemRemoved += OnItemRemoved;
        _logger.LogInformation("LibraryEventHandler started");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        _libraryManager.ItemAdded -= OnItemAdded;
        _libraryManager.ItemRemoved -= OnItemRemoved;
        _logger.LogInformation("LibraryEventHandler stopped");
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _libraryManager.ItemAdded -= OnItemAdded;
        _libraryManager.ItemRemoved -= OnItemRemoved;
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null || !config.AutoDetectEnabled)
        {
            return;
        }

        // New Season 0 episode — run detection
        if (e.Item is Episode episode && episode.ParentIndexNumber == 0)
        {
            var token = _cts?.Token ?? CancellationToken.None;
            _ = Task.Run(async () =>
            {
                try
                {
                    await _detectionService.ProcessEpisodeAsync(episode, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected during shutdown
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing new episode {Name}", episode.Name);
                }
            }, token);
        }

        // New movie at a tracked hard link path — promote pair to Active
        if (e.Item is MediaBrowser.Controller.Entities.Movies.Movie movie && !string.IsNullOrEmpty(movie.Path))
        {
            try
            {
                var pair = _pairStore.GetByHardLinkPath(movie.Path);
                if (pair != null && pair.Status == PairStatus.Pending)
                {
                    pair.MovieItemId = movie.Id;
                    pair.Status = PairStatus.Active;
                    _pairStore.Upsert(pair);
                    _watchSyncService.SyncInitialWatchState(pair);
                    _logger.LogInformation(
                        "Movie scanned at hard link path, pair activated: {MovieTitle}", pair.MovieTitle);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error promoting pair for movie {Name}", movie.Name);
            }
        }
    }

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return;
        }

        var item = e.Item;

        // Episode removed
        if (item is Episode)
        {
            var pair = _pairStore.GetByEpisodeId(item.Id);
            if (pair == null)
            {
                return;
            }

            if (config.DryRunMode)
            {
                _logger.LogInformation("DRY RUN: Would clean up pair for removed episode {Name}", item.Name);
                return;
            }

            if (config.AutoDeleteOnRemoval && !pair.IsExistingMovie && !string.IsNullOrEmpty(pair.HardLinkPath))
            {
                _hardLinkService.DeleteMovieFolder(pair.HardLinkPath);
                RemoveMovieFromLibrary(pair.MovieItemId);
            }
            else if (!string.IsNullOrEmpty(pair.HardLinkPath))
            {
                _logger.LogInformation("Orphaned folder left on disk (auto-delete disabled): {Path}", pair.HardLinkPath);
            }

            _pairStore.Remove(pair.Id);
            _logger.LogInformation("Removed pair for deleted episode: {Title}", pair.MovieTitle);
        }

        // Movie removed
        if (item is MediaBrowser.Controller.Entities.Movies.Movie)
        {
            var pair = _pairStore.GetByMovieId(item.Id);
            if (pair == null)
            {
                return;
            }

            if (config.DryRunMode)
            {
                _logger.LogInformation("DRY RUN: Would clean up pair for removed movie {Name}", item.Name);
                return;
            }

            if (!pair.IsExistingMovie && !string.IsNullOrEmpty(pair.HardLinkPath))
            {
                if (config.AutoDeleteOnRemoval)
                {
                    _hardLinkService.DeleteMovieFolder(pair.HardLinkPath);
                }
                else
                {
                    _logger.LogInformation("Orphaned folder left on disk (auto-delete disabled): {Path}", pair.HardLinkPath);
                }

                if (config.AutoDeleteOnRemoval && config.TwoWayDeletion)
                {
                    DeleteOriginalEpisode(pair);
                }
            }
            else if (pair.IsExistingMovie && config.AutoDeleteOnRemoval && config.TwoWayDeletion)
            {
                DeleteOriginalEpisode(pair);
            }

            _pairStore.Remove(pair.Id);
            _logger.LogInformation("Removed pair for deleted movie: {Title}", pair.MovieTitle);
        }
    }

    private void DeleteOriginalEpisode(LinkedPair pair)
    {
        if (string.IsNullOrEmpty(pair.EpisodePath))
        {
            return;
        }

        // Safety check: verify the path is inside a known library location
        var resolvedPath = Path.GetFullPath(pair.EpisodePath);
        var folders = _libraryManager.GetVirtualFolders();
        var isInLibrary = folders.Any(f =>
            f.Locations.Any(loc =>
                resolvedPath.StartsWith(Path.GetFullPath(loc), StringComparison.OrdinalIgnoreCase)));

        if (!isInLibrary)
        {
            _logger.LogWarning(
                "Refusing to delete episode at {Path} — not inside any known library location",
                pair.EpisodePath);
            return;
        }

        try
        {
            var episodeDir = Path.GetDirectoryName(resolvedPath);
            var episodeNameWithoutExt = Path.GetFileNameWithoutExtension(resolvedPath);

            if (string.IsNullOrEmpty(episodeDir) || string.IsNullOrEmpty(episodeNameWithoutExt))
            {
                return;
            }

            // Delete the episode file itself
            if (File.Exists(resolvedPath))
            {
                File.Delete(resolvedPath);
                _logger.LogInformation("Two-way deletion: removed original episode {Path}", resolvedPath);
            }

            // Delete companion files: subtitles (.en.srt, .eng.forced.srt), images, nfo, etc.
            int companionDeleted = 0, companionFailed = 0;
            foreach (var companion in Directory.EnumerateFiles(episodeDir, $"{episodeNameWithoutExt}*"))
            {
                if (string.Equals(Path.GetFullPath(companion), resolvedPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    File.Delete(companion);
                    companionDeleted++;
                }
                catch (Exception ex)
                {
                    companionFailed++;
                    _logger.LogWarning(ex, "Failed to delete companion file {Path}", companion);
                }
            }

            // Delete companion directories (e.g., trickplay folders)
            foreach (var companionDir in Directory.EnumerateDirectories(episodeDir, $"{episodeNameWithoutExt}*"))
            {
                try
                {
                    Directory.Delete(companionDir, recursive: true);
                    companionDeleted++;
                }
                catch (Exception ex)
                {
                    companionFailed++;
                    _logger.LogWarning(ex, "Failed to delete companion directory {Path}", companionDir);
                }
            }

            if (companionDeleted > 0 || companionFailed > 0)
            {
                _logger.LogInformation(
                    "Two-way deletion companion cleanup: {Deleted} removed, {Failed} failed for {Path}",
                    companionDeleted, companionFailed, pair.EpisodePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete original episode {Path}", pair.EpisodePath);
        }
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
}
