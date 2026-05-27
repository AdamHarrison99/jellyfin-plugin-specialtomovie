using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SpecialToMovie.Configuration;
using Jellyfin.Plugin.SpecialToMovie.Data;
using Jellyfin.Plugin.SpecialToMovie.HardLink;
using Jellyfin.Plugin.SpecialToMovie.Lookup;
using Jellyfin.Plugin.SpecialToMovie.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpecialToMovie.Services;

public class SpecialDetectionService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IPairStore _pairStore;
    private readonly AggregatedLookupService _lookupService;
    private readonly IHardLinkService _hardLinkService;
    private readonly WatchSyncService _watchSyncService;
    private readonly ILogger<SpecialDetectionService> _logger;

    public SpecialDetectionService(
        ILibraryManager libraryManager,
        IPairStore pairStore,
        AggregatedLookupService lookupService,
        IHardLinkService hardLinkService,
        WatchSyncService watchSyncService,
        ILogger<SpecialDetectionService> logger)
    {
        _libraryManager = libraryManager;
        _pairStore = pairStore;
        _lookupService = lookupService;
        _hardLinkService = hardLinkService;
        _watchSyncService = watchSyncService;
        _logger = logger;
    }

    public async Task ProcessEpisodeAsync(Episode episode, CancellationToken cancellationToken = default)
    {
        var movies = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie],
            Recursive = true
        });
        await ProcessEpisodeAsync(episode, null, movies, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessEpisodeAsync(Episode episode, ConfigSnapshot? snapshot, IReadOnlyList<BaseItem>? allMovies, IReadOnlyList<VirtualFolderInfo>? virtualFolders, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return;
        }

        // Use a snapshot of mutable collections to avoid race conditions with config saves
        snapshot ??= ConfigSnapshot.From(config);
        virtualFolders ??= _libraryManager.GetVirtualFolders();

        if (_pairStore.ExistsForEpisode(episode.Id))
        {
            return;
        }

        var mapping = ResolveMapping(episode, snapshot, virtualFolders);
        if (mapping == null)
        {
            return;
        }

        var episodeKey = FormatEpisodeKey(episode);

        if (snapshot.IgnoreList.Contains(episodeKey, StringComparer.OrdinalIgnoreCase) ||
            snapshot.IgnoreList.Contains(episode.Id.ToString(), StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Episode {Key} is in ignore list, skipping", episodeKey);
            return;
        }

        // Check force links first
        MovieMatch? match = null;
        var episodeIdStr = episode.Id.ToString();
        var forceLink = snapshot.ForceLinks.FirstOrDefault(f =>
            string.Equals(f.EpisodeKey, episodeKey, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(f.EpisodeKey, episodeIdStr, StringComparison.OrdinalIgnoreCase));
        if (forceLink != null)
        {
            _logger.LogInformation("Using force link for {Key}: {Movie}", episodeKey, forceLink.MovieTitle);

            // Right side is a Jellyfin item ID — link directly to that movie
            if (Guid.TryParse(forceLink.MovieTitle.Trim(), out var movieItemId))
            {
                var movieItem = _libraryManager.GetItemById(movieItemId);
                if (movieItem != null)
                {
                    var directPair = new LinkedPair
                    {
                        Id = Guid.NewGuid(),
                        EpisodeItemId = episode.Id,
                        MovieItemId = movieItem.Id,
                        SourceLibraryId = mapping.SourceLibraryId,
                        DestinationLibraryId = mapping.DestinationLibraryId,
                        EpisodePath = episode.Path,
                        IsExistingMovie = true,
                        MovieTitle = movieItem.Name ?? string.Empty,
                        MovieYear = movieItem.ProductionYear,
                        Status = PairStatus.Active
                    };

                    _pairStore.Upsert(directPair);
                    _watchSyncService.SyncInitialWatchState(directPair);
                    _logger.LogInformation(
                        "Force link: paired {Key} directly to movie item {MovieName} ({MovieId})",
                        episodeKey, movieItem.Name, movieItemId);
                    return;
                }

                _logger.LogWarning("Force link: movie item {Id} not found in library", movieItemId);
                return;
            }

            match = ParseForcedMovie(forceLink.MovieTitle);

            BaseItem? existingForced = null;
            if (match != null)
            {
                var hasProviderIds = !string.IsNullOrEmpty(match.TmdbMovieId) ||
                                     !string.IsNullOrEmpty(match.TvdbMovieId) ||
                                     !string.IsNullOrEmpty(match.ImdbId);
                existingForced = hasProviderIds
                    ? FindExistingMovie(mapping.DestinationLibraryId, match, allMovies, virtualFolders)
                    : FindExistingMovieByTitle(mapping.DestinationLibraryId, match, allMovies, virtualFolders);
            }
            if (existingForced != null)
            {
                _logger.LogInformation(
                    "Force link: existing movie found for {Key}: {MovieName} — paired without hard link",
                    episodeKey, existingForced.Name);

                var existingPair = new LinkedPair
                {
                    Id = Guid.NewGuid(),
                    EpisodeItemId = episode.Id,
                    MovieItemId = existingForced.Id,
                    SourceLibraryId = mapping.SourceLibraryId,
                    DestinationLibraryId = mapping.DestinationLibraryId,
                    EpisodePath = episode.Path,
                    IsExistingMovie = true,
                    MovieTitle = match!.Title,
                    MovieYear = match.Year,
                    Status = PairStatus.Active
                };

                _pairStore.Upsert(existingPair);
                _watchSyncService.SyncInitialWatchState(existingPair);
                return;
            }
        }

        match ??= await _lookupService.LookupAsync(episode, cancellationToken).ConfigureAwait(false);

        if (match == null)
        {
            _logger.LogDebug("No movie match found for {Key}", episodeKey);
            return;
        }

        if (config.RequireDualConfirmation &&
            (string.IsNullOrEmpty(match.TmdbMovieId) || string.IsNullOrEmpty(match.TvdbMovieId)))
        {
            _logger.LogInformation("Dual confirmation required but only one source matched for {Key}, skipping", episodeKey);
            return;
        }

        // Check if the movie already exists in the destination library
        var existingMovie = FindExistingMovie(mapping.DestinationLibraryId, match, allMovies, virtualFolders);
        if (existingMovie != null)
        {
            _logger.LogInformation(
                "Existing movie found for {Key}: {MovieName} — paired without hard link",
                episodeKey, existingMovie.Name);

            var existingPair = new LinkedPair
            {
                Id = Guid.NewGuid(),
                EpisodeItemId = episode.Id,
                MovieItemId = existingMovie.Id,
                SourceLibraryId = mapping.SourceLibraryId,
                DestinationLibraryId = mapping.DestinationLibraryId,
                EpisodePath = episode.Path,
                IsExistingMovie = true,
                MovieTitle = match.Title,
                MovieYear = match.Year,
                TmdbMovieId = match.TmdbMovieId,
                TvdbMovieId = match.TvdbMovieId,
                TvdbMovieSlug = match.TvdbMovieSlug,
                ImdbId = match.ImdbId,
                Status = PairStatus.Active
            };

            _pairStore.Upsert(existingPair);
            _watchSyncService.SyncInitialWatchState(existingPair);
            return;
        }

        // Dry run: log and store as DryRun, no filesystem changes
        if (config.DryRunMode)
        {
            _logger.LogInformation(
                "DRY RUN: Would create hard link for '{EpisodeName}' -> '{MovieTitle} ({Year})' in destination library",
                episode.Name, match.Title, match.Year);

            var dryRunPair = new LinkedPair
            {
                Id = Guid.NewGuid(),
                EpisodeItemId = episode.Id,
                SourceLibraryId = mapping.SourceLibraryId,
                DestinationLibraryId = mapping.DestinationLibraryId,
                EpisodePath = episode.Path,
                MovieTitle = match.Title,
                MovieYear = match.Year,
                TmdbMovieId = match.TmdbMovieId,
                TvdbMovieId = match.TvdbMovieId,
                TvdbMovieSlug = match.TvdbMovieSlug,
                ImdbId = match.ImdbId,
                Status = PairStatus.DryRun
            };

            _pairStore.Upsert(dryRunPair);
            return;
        }

        // Create hard link
        var destinationPath = ResolveDestinationPath(mapping, virtualFolders);
        if (string.IsNullOrEmpty(destinationPath))
        {
            _logger.LogError("Could not resolve destination path for mapping");
            return;
        }

        if (!_hardLinkService.ValidateSameFilesystem(episode.Path, destinationPath))
        {
            _logger.LogWarning(
                "Episode {Path} and destination {Dest} are on different filesystems, skipping",
                episode.Path, destinationPath);

            StorePairWithError(episode, mapping, match, "Source and destination are on different filesystems");
            return;
        }

        var extension = Path.GetExtension(episode.Path);
        var linkPath = _hardLinkService.BuildHardLinkPath(destinationPath, match.Title, match.Year, extension);

        if (!_hardLinkService.Create(episode.Path, linkPath))
        {
            StorePairWithError(episode, mapping, match, "Failed to create hard link");
            return;
        }

        // Write NFO if the destination library uses NFO metadata
        var movieFolderPath = Path.GetDirectoryName(linkPath);
        if (!string.IsNullOrEmpty(movieFolderPath) && ShouldWriteNfo(mapping.DestinationLibraryId, virtualFolders))
        {
            _hardLinkService.WriteNfoFile(movieFolderPath, match.Title, match.Year, match);
        }

        var pair = new LinkedPair
        {
            Id = Guid.NewGuid(),
            EpisodeItemId = episode.Id,
            SourceLibraryId = mapping.SourceLibraryId,
            DestinationLibraryId = mapping.DestinationLibraryId,
            EpisodePath = episode.Path,
            HardLinkPath = linkPath,
            MovieTitle = match.Title,
            MovieYear = match.Year,
            TmdbMovieId = match.TmdbMovieId,
            TvdbMovieId = match.TvdbMovieId,
                TvdbMovieSlug = match.TvdbMovieSlug,
            ImdbId = match.ImdbId,
            Status = PairStatus.Pending
        };

        _pairStore.Upsert(pair);

        _logger.LogInformation(
            "Created hard link for '{EpisodeName}' -> '{MovieTitle} ({Year})': {LinkPath}",
            episode.Name, match.Title, match.Year, linkPath);
    }

    public async Task RunFullScanAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null || config.LibraryMappings.Count == 0)
        {
            _logger.LogWarning("No library mappings configured, skipping full scan");
            return;
        }

        // Snapshot mutable config collections once at scan start
        var snapshot = ConfigSnapshot.From(config);

        var episodes = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            ParentIndexNumber = 0, // Season 0
            Recursive = true
        });

        // Cache virtual folders and batch-fetch all movies once
        var virtualFolders = _libraryManager.GetVirtualFolders();
        var allMovies = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie],
            Recursive = true
        });

        var total = episodes.Count;
        _logger.LogInformation("Full scan: found {Count} Season 0 episodes", total);

        for (int i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (episodes[i] is Episode episode)
            {
                await ProcessEpisodeAsync(episode, snapshot, allMovies, virtualFolders, cancellationToken).ConfigureAwait(false);

                // Small delay between lookups to respect rate limits
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }

            progress?.Report((double)(i + 1) / total * 90);
        }

        EnforceIgnoreList(snapshot, config.AutoDeleteOnRemoval);

        await ProcessForceLinksAsync(snapshot, allMovies, virtualFolders, cancellationToken).ConfigureAwait(false);

        // Promote DryRun pairs if dry run was just disabled
        if (!config.DryRunMode)
        {
            await PromoteDryRunPairsAsync(snapshot, allMovies, virtualFolders, cancellationToken).ConfigureAwait(false);
        }

        // Sync watch state for all Active pairs (catches pairs that existed before watch sync was added,
        // or pairs whose watch state drifted while the plugin was stopped)
        if (!config.DryRunMode)
        {
            SyncAllActivePairs();
        }

        _logger.LogInformation("Full scan complete");
    }

    private async Task PromoteDryRunPairsAsync(ConfigSnapshot snapshot, IReadOnlyList<BaseItem> allMovies, IReadOnlyList<VirtualFolderInfo> virtualFolders, CancellationToken cancellationToken)
    {
        var dryRunPairs = _pairStore.GetAll().Where(p => p.Status == PairStatus.DryRun).ToList();
        if (dryRunPairs.Count == 0)
        {
            return;
        }

        _logger.LogInformation("Promoting {Count} DryRun pairs to active", dryRunPairs.Count);

        foreach (var pair in dryRunPairs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var episode = _libraryManager.GetItemById(pair.EpisodeItemId) as Episode;
            if (episode == null)
            {
                _logger.LogWarning("Episode {Id} no longer exists, removing DryRun pair", pair.EpisodeItemId);
                _pairStore.Remove(pair.Id);
                continue;
            }

            // Remove the DryRun pair so ProcessEpisodeAsync can recreate it as Pending
            _pairStore.Remove(pair.Id);
            await ProcessEpisodeAsync(episode, snapshot, allMovies, virtualFolders, cancellationToken).ConfigureAwait(false);
        }
    }

    private void SyncAllActivePairs()
    {
        var activePairs = _pairStore.GetAll().Where(p => p.Status == PairStatus.Active).ToList();
        if (activePairs.Count == 0)
        {
            return;
        }

        _logger.LogInformation("Syncing watch state for {Count} active pairs", activePairs.Count);
        foreach (var pair in activePairs)
        {
            _watchSyncService.SyncInitialWatchState(pair);
        }
    }

    private LibraryMapping? ResolveMapping(Episode episode, ConfigSnapshot snapshot, IReadOnlyList<VirtualFolderInfo> folders)
    {
        var libraryId = GetLibraryId(episode, folders);
        if (libraryId == null)
        {
            return null;
        }

        return snapshot.LibraryMappings.FirstOrDefault(m =>
            m.Enabled && m.SourceLibraryId == libraryId.Value);
    }

    private static Guid? GetLibraryId(BaseItem item, IReadOnlyList<VirtualFolderInfo> folders)
    {
        if (string.IsNullOrEmpty(item.Path))
        {
            return null;
        }

        foreach (var folder in folders)
        {
            if (folder.Locations.Any(loc =>
                !string.IsNullOrEmpty(loc) && item.Path.StartsWith(loc, StringComparison.OrdinalIgnoreCase)))
            {
                if (Guid.TryParse(folder.ItemId, out var id))
                {
                    return id;
                }
            }
        }

        return null;
    }

    private BaseItem? FindExistingMovie(Guid destinationLibraryId, MovieMatch match, IReadOnlyList<BaseItem>? cachedMovies, IReadOnlyList<VirtualFolderInfo>? virtualFolders)
    {
        IEnumerable<BaseItem> movies;

        if (cachedMovies != null && virtualFolders != null)
        {
            var destFolder = virtualFolders.FirstOrDefault(f =>
                Guid.TryParse(f.ItemId, out var id) && id == destinationLibraryId);
            var destLocations = destFolder?.Locations ?? [];

            movies = cachedMovies.Where(m =>
                !string.IsNullOrEmpty(m.Path) &&
                destLocations.Any(loc =>
                    !string.IsNullOrEmpty(loc) &&
                    m.Path.StartsWith(loc, StringComparison.OrdinalIgnoreCase)));
        }
        else
        {
            movies = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Movie],
                ParentId = destinationLibraryId,
                Recursive = true
            });
        }

        return movies.FirstOrDefault(m =>
            (!string.IsNullOrEmpty(match.TmdbMovieId) &&
             m.GetProviderId(MetadataProvider.Tmdb) == match.TmdbMovieId) ||
            (!string.IsNullOrEmpty(match.TvdbMovieId) &&
             m.GetProviderId(MetadataProvider.Tvdb) == match.TvdbMovieId) ||
            (!string.IsNullOrEmpty(match.ImdbId) &&
             m.GetProviderId(MetadataProvider.Imdb) == match.ImdbId));
    }

    private static BaseItem? FindExistingMovieByTitle(Guid destinationLibraryId, MovieMatch match, IReadOnlyList<BaseItem>? cachedMovies, IReadOnlyList<VirtualFolderInfo>? virtualFolders)
    {
        if (string.IsNullOrEmpty(match.Title))
        {
            return null;
        }

        IEnumerable<BaseItem> movies;

        if (cachedMovies != null && virtualFolders != null)
        {
            var destFolder = virtualFolders.FirstOrDefault(f =>
                Guid.TryParse(f.ItemId, out var id) && id == destinationLibraryId);
            var destLocations = destFolder?.Locations ?? [];

            movies = cachedMovies.Where(m =>
                !string.IsNullOrEmpty(m.Path) &&
                destLocations.Any(loc =>
                    !string.IsNullOrEmpty(loc) &&
                    m.Path.StartsWith(loc, StringComparison.OrdinalIgnoreCase)));
        }
        else
        {
            return null;
        }

        return movies.FirstOrDefault(m =>
            string.Equals(m.Name, match.Title, StringComparison.OrdinalIgnoreCase) &&
            (!match.Year.HasValue || m.ProductionYear == match.Year));
    }

    private static string? ResolveDestinationPath(LibraryMapping mapping, IReadOnlyList<VirtualFolderInfo> folders)
    {
        if (!string.IsNullOrEmpty(mapping.DestinationPath))
        {
            return mapping.DestinationPath;
        }
        var destFolder = folders.FirstOrDefault(f =>
            Guid.TryParse(f.ItemId, out var id) && id == mapping.DestinationLibraryId);

        return destFolder?.Locations.FirstOrDefault();
    }

    private static bool ShouldWriteNfo(Guid libraryId, IReadOnlyList<VirtualFolderInfo> folders)
    {
        var folder = folders.FirstOrDefault(f =>
            Guid.TryParse(f.ItemId, out var id) && id == libraryId);

        if (folder?.LibraryOptions?.MetadataSavers == null)
        {
            return false;
        }

        return folder.LibraryOptions.MetadataSavers
            .Any(s => s.Contains("Nfo", StringComparison.OrdinalIgnoreCase));
    }

    private void StorePairWithError(Episode episode, LibraryMapping mapping, MovieMatch match, string error)
    {
        var pair = new LinkedPair
        {
            Id = Guid.NewGuid(),
            EpisodeItemId = episode.Id,
            SourceLibraryId = mapping.SourceLibraryId,
            DestinationLibraryId = mapping.DestinationLibraryId,
            EpisodePath = episode.Path,
            MovieTitle = match.Title,
            MovieYear = match.Year,
            TmdbMovieId = match.TmdbMovieId,
            TvdbMovieId = match.TvdbMovieId,
                TvdbMovieSlug = match.TvdbMovieSlug,
            ImdbId = match.ImdbId,
            Status = PairStatus.Error,
            ErrorMessage = error
        };

        _pairStore.Upsert(pair);
        _logger.LogError("Pair stored with error for {Name}: {Error}", episode.Name, error);
    }

    private static string FormatEpisodeKey(Episode episode)
    {
        var seriesName = episode.SeriesName ?? "Unknown";
        var episodeNumber = episode.IndexNumber?.ToString("D2") ?? "00";
        return $"{seriesName} S00E{episodeNumber}";
    }

    private static MovieMatch? ParseForcedMovie(string value)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 500)
        {
            return null;
        }

        // IMDB ID: tt followed by digits
        if (trimmed.StartsWith("tt", StringComparison.OrdinalIgnoreCase) &&
            trimmed.Length > 2 && trimmed[2..].All(char.IsDigit))
        {
            return new MovieMatch { ImdbId = trimmed };
        }

        // TMDB ID: tmdb: prefix
        if (trimmed.StartsWith("tmdb:", StringComparison.OrdinalIgnoreCase))
        {
            var id = trimmed[5..].Trim();
            if (id.Length > 0 && id.All(char.IsDigit))
            {
                return new MovieMatch { TmdbMovieId = id };
            }
        }

        // TVDB ID: tvdb: prefix
        if (trimmed.StartsWith("tvdb:", StringComparison.OrdinalIgnoreCase))
        {
            var id = trimmed[5..].Trim();
            if (id.Length > 0 && id.All(char.IsDigit))
            {
                return new MovieMatch { TvdbMovieId = id };
            }
        }

        // Movie Title (Year)
        int? year = null;
        var title = trimmed;

        var lastParen = trimmed.LastIndexOf('(');
        if (lastParen > 0 && trimmed.EndsWith(')'))
        {
            var length = trimmed.Length - lastParen - 2;
            if (length > 0)
            {
                var yearStr = trimmed.Substring(lastParen + 1, length);
                if (int.TryParse(yearStr, out var parsedYear))
                {
                    year = parsedYear;
                    title = trimmed[..lastParen].TrimEnd();
                }
            }
        }

        return new MovieMatch
        {
            Title = title,
            Year = year
        };
    }

    public void EnforceIgnoreList()
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return;
        }

        EnforceIgnoreList(ConfigSnapshot.From(config), config.AutoDeleteOnRemoval);
    }

    private void EnforceIgnoreList(ConfigSnapshot snapshot, bool autoDelete)
    {
        var pairs = _pairStore.GetAll();
        var toRemove = new List<Guid>();

        foreach (var pair in pairs)
        {
            var episode = _libraryManager.GetItemById(pair.EpisodeItemId) as Episode;
            if (episode == null)
            {
                continue;
            }

            var episodeKey = FormatEpisodeKey(episode);
            var isIgnored = snapshot.IgnoreList.Contains(episodeKey, StringComparer.OrdinalIgnoreCase) ||
                            snapshot.IgnoreList.Contains(pair.EpisodeItemId.ToString(), StringComparer.OrdinalIgnoreCase);

            if (!isIgnored)
            {
                continue;
            }

            if (autoDelete && !pair.IsExistingMovie)
            {
                DeleteLinkedMovieItem(pair.MovieItemId);
            }

            toRemove.Add(pair.Id);
            _logger.LogInformation("Removed pair for ignored episode {Key} (pair {PairId})", episodeKey, pair.Id);
        }

        if (toRemove.Count > 0)
        {
            _pairStore.RemoveMany(toRemove);
            _logger.LogInformation("Ignore list enforcement removed {Count} pairs", toRemove.Count);
        }
    }

    public async Task ProcessForceLinksAsync(CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return;
        }

        var snapshot = ConfigSnapshot.From(config);
        var allMovies = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie],
            Recursive = true
        });
        var virtualFolders = _libraryManager.GetVirtualFolders();

        await ProcessForceLinksAsync(snapshot, allMovies, virtualFolders, cancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessForceLinksAsync(ConfigSnapshot snapshot, IReadOnlyList<BaseItem> allMovies, IReadOnlyList<VirtualFolderInfo> virtualFolders, CancellationToken cancellationToken)
    {
        if (snapshot.ForceLinks.Count == 0)
        {
            return;
        }

        var allEpisodes = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            ParentIndexNumber = 0,
            Recursive = true
        });

        var processed = 0;

        foreach (var forceLink in snapshot.ForceLinks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Episode? episode = null;
            if (Guid.TryParse(forceLink.EpisodeKey, out var forcedEpId))
            {
                episode = allEpisodes.OfType<Episode>().FirstOrDefault(e => e.Id == forcedEpId);
            }

            episode ??= allEpisodes
                .OfType<Episode>()
                .FirstOrDefault(e => string.Equals(FormatEpisodeKey(e), forceLink.EpisodeKey, StringComparison.OrdinalIgnoreCase));

            if (episode == null)
            {
                _logger.LogDebug("Force link episode not found: {Key}", forceLink.EpisodeKey);
                continue;
            }

            if (_pairStore.ExistsForEpisode(episode.Id))
            {
                continue;
            }

            await ProcessEpisodeAsync(episode, snapshot, allMovies, virtualFolders, cancellationToken).ConfigureAwait(false);
            processed++;
        }

        if (processed > 0)
        {
            _logger.LogInformation("Processed {Count} force links", processed);
        }
    }

    private void DeleteLinkedMovieItem(Guid? itemId)
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
            _logger.LogInformation("Deleted linked movie {Name} via Jellyfin", item.Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete linked movie item {Id}", itemId);
        }
    }

    /// <summary>
    /// Immutable snapshot of mutable config collections, taken once at scan start.
    /// </summary>
    private sealed class ConfigSnapshot
    {
        public required List<LibraryMapping> LibraryMappings { get; init; }
        public required List<ForceLinkEntry> ForceLinks { get; init; }
        public required List<string> IgnoreList { get; init; }

        public static ConfigSnapshot From(PluginConfiguration config)
        {
            return new ConfigSnapshot
            {
                LibraryMappings = new List<LibraryMapping>(config.LibraryMappings),
                ForceLinks = new List<ForceLinkEntry>(config.ForceLinks),
                IgnoreList = new List<string>(config.IgnoreList)
            };
        }
    }
}
