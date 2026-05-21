using Jellyfin.Plugin.SpecialToMovie.Data;
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
    private readonly SpecialDetectionService _detectionService;
    private readonly WatchSyncService _watchSyncService;
    private readonly ILogger<LibraryEventHandler> _logger;
    private CancellationTokenSource? _cts;

    public LibraryEventHandler(
        ILibraryManager libraryManager,
        IPairStore pairStore,
        SpecialDetectionService detectionService,
        WatchSyncService watchSyncService,
        ILogger<LibraryEventHandler> logger)
    {
        _libraryManager = libraryManager;
        _pairStore = pairStore;
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

            // Remove pair first to prevent cascading events when Jellyfin fires ItemRemoved for the movie
            _pairStore.Remove(pair.Id);

            if (config.AutoDeleteOnRemoval && !pair.IsExistingMovie)
            {
                DeleteItemWithFiles(pair.MovieItemId);
            }
            else if (!string.IsNullOrEmpty(pair.HardLinkPath))
            {
                _logger.LogInformation("Orphaned folder left on disk (auto-delete disabled): {Path}", pair.HardLinkPath);
            }

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

            // Remove pair first to prevent cascading events when Jellyfin fires ItemRemoved for the episode
            _pairStore.Remove(pair.Id);

            if (config.AutoDeleteOnRemoval && config.TwoWayDeletion)
            {
                DeleteItemWithFiles(pair.EpisodeItemId);
            }

            _logger.LogInformation("Removed pair for deleted movie: {Title}", pair.MovieTitle);
        }
    }

    private void DeleteItemWithFiles(Guid? itemId)
    {
        if (itemId == null || itemId == Guid.Empty)
        {
            return;
        }

        var item = _libraryManager.GetItemById(itemId.Value);
        if (item == null)
        {
            return;
        }

        try
        {
            _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = true });
            _logger.LogInformation("Deleted {Name} with files via Jellyfin", item.Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete item {Id} via Jellyfin", itemId);
        }
    }

    private void DeleteItemWithFiles(Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item == null)
        {
            return;
        }

        try
        {
            _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = true });
            _logger.LogInformation("Deleted {Name} with files via Jellyfin", item.Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete item {Id} via Jellyfin", itemId);
        }
    }
}
