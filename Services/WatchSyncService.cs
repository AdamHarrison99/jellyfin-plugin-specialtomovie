using System.Collections.Concurrent;
using Jellyfin.Plugin.SpecialToMovie.Data;
using Jellyfin.Plugin.SpecialToMovie.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpecialToMovie.Services;

public class WatchSyncService : IHostedService, IDisposable
{
    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IPairStore _pairStore;
    private readonly ILogger<WatchSyncService> _logger;
    private readonly ConcurrentDictionary<string, byte> _reentrancyGuard = new();

    public WatchSyncService(
        IUserDataManager userDataManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        IPairStore pairStore,
        ILogger<WatchSyncService> logger)
    {
        _userDataManager = userDataManager;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _pairStore = pairStore;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved += OnUserDataSaved;
        _logger.LogInformation("WatchSyncService started");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        _logger.LogInformation("WatchSyncService stopped");
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;
    }

    /// <summary>
    /// Performs an initial one-time sync of watch state for all users when a pair first becomes Active.
    /// Copies the "most watched" state — if either item is played, both become played.
    /// </summary>
    public void SyncInitialWatchState(LinkedPair pair)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null || config.DryRunMode)
        {
            return;
        }

        if (pair.MovieItemId == null || pair.MovieItemId == Guid.Empty)
        {
            return;
        }

        var episodeItem = _libraryManager.GetItemById(pair.EpisodeItemId);
        var movieItem = _libraryManager.GetItemById(pair.MovieItemId.Value);
        if (episodeItem == null || movieItem == null)
        {
            return;
        }

        var users = _userManager.Users;
        foreach (var user in users)
        {
            try
            {
                var episodeData = _userDataManager.GetUserData(user, episodeItem);
                var movieData = _userDataManager.GetUserData(user, movieItem);
                if (episodeData == null || movieData == null)
                {
                    continue;
                }

                // Determine which item has more progress and sync to the other
                var (source, target, targetItem) = PickSyncDirection(episodeData, movieData, episodeItem, movieItem);
                if (source == null || target == null || targetItem == null)
                {
                    continue;
                }

                target.Played = source.Played;
                target.PlaybackPositionTicks = source.PlaybackPositionTicks;
                target.PlayCount = source.PlayCount;
                target.LastPlayedDate = source.LastPlayedDate;
                target.IsFavorite = source.IsFavorite;

                // Guard against reentrancy from the save event
                var guardKey = $"{user.Id}:{targetItem.Id}";
                _reentrancyGuard.TryAdd(guardKey, 0);
                try
                {
                    _userDataManager.SaveUserData(
                        user,
                        targetItem,
                        target,
                        UserDataSaveReason.UpdateUserData,
                        CancellationToken.None);
                }
                finally
                {
                    _reentrancyGuard.TryRemove(guardKey, out _);
                }

                _logger.LogDebug(
                    "Initial sync for user {User}: {Source} -> {Target} (Played={Played})",
                    user.Username, source == episodeData ? "episode" : "movie",
                    source == episodeData ? "movie" : "episode", source.Played);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during initial watch sync for user {User}, pair {PairId}",
                    user.Username, pair.Id);
            }
        }
    }

    private static (UserItemData? source, UserItemData? target, BaseItem? targetItem) PickSyncDirection(
        UserItemData episodeData,
        UserItemData movieData,
        BaseItem episodeItem,
        BaseItem movieItem)
    {
        var episodeHasProgress = episodeData.Played || episodeData.PlaybackPositionTicks > 0;
        var movieHasProgress = movieData.Played || movieData.PlaybackPositionTicks > 0;

        if (!episodeHasProgress && !movieHasProgress)
        {
            return (null, null, null);
        }

        // Movie wins only when the episode has no progress at all
        if (movieHasProgress && !episodeHasProgress)
        {
            return (movieData, episodeData, episodeItem);
        }

        // Episode (TV special) has priority in all other cases
        return (episodeData, movieData, movieItem);
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null || config.DryRunMode)
        {
            return;
        }

        var item = e.Item;
        if (item == null)
        {
            return;
        }

        var pair = _pairStore.GetByEpisodeId(item.Id) ?? _pairStore.GetByMovieId(item.Id);
        if (pair == null || pair.Status != PairStatus.Active)
        {
            return;
        }

        var pairedItemId = item.Id == pair.EpisodeItemId ? pair.MovieItemId : pair.EpisodeItemId;
        if (pairedItemId == null || pairedItemId == Guid.Empty)
        {
            return;
        }

        var guardKey = $"{e.UserId}:{pairedItemId}";

        if (!_reentrancyGuard.TryAdd(guardKey, 0))
        {
            return;
        }

        try
        {
            var user = _userManager.GetUserById(e.UserId);
            if (user == null)
            {
                _logger.LogWarning("User {UserId} not found", e.UserId);
                return;
            }

            var pairedItem = _libraryManager.GetItemById(pairedItemId.Value);
            if (pairedItem == null)
            {
                _logger.LogWarning("Paired item {Id} not found in library", pairedItemId);
                return;
            }

            var userData = e.UserData;
            if (userData == null)
            {
                return;
            }

            var pairedUserData = _userDataManager.GetUserData(user, pairedItem);
            if (pairedUserData == null)
            {
                return;
            }

            pairedUserData.Played = userData.Played;
            pairedUserData.PlaybackPositionTicks = userData.PlaybackPositionTicks;
            pairedUserData.PlayCount = userData.PlayCount;
            pairedUserData.LastPlayedDate = userData.LastPlayedDate;
            pairedUserData.IsFavorite = userData.IsFavorite;

            _userDataManager.SaveUserData(
                user,
                pairedItem,
                pairedUserData,
                UserDataSaveReason.UpdateUserData,
                CancellationToken.None);

            _logger.LogDebug(
                "Synced watch state from {SourceId} to {TargetId} for user {UserId} (Played={Played})",
                item.Id, pairedItemId, e.UserId, userData.Played);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error syncing watch state for pair {PairId}", pair.Id);
        }
        finally
        {
            _reentrancyGuard.TryRemove(guardKey, out _);
        }
    }
}
