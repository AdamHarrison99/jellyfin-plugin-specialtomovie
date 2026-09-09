using System.Text.Json;
using Jellyfin.Plugin.SpecialToMovie.Models;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpecialToMovie.Data;

public interface IPairStore
{
    List<LinkedPair> GetAll();

    LinkedPair? GetById(Guid pairId);

    LinkedPair? GetByEpisodeId(Guid episodeItemId);

    LinkedPair? GetByMovieId(Guid movieItemId);

    LinkedPair? GetByHardLinkPath(string hardLinkPath);

    bool ExistsForEpisode(Guid episodeItemId);

    void Upsert(LinkedPair pair);

    void UpsertMany(IEnumerable<LinkedPair> pairs);

    void Remove(Guid pairId);

    void RemoveMany(IEnumerable<Guid> pairIds);

    int Clear();
}

public class PairStore : IPairStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _dataFilePath;
    private readonly string _backupFilePath;
    private readonly object _lock = new();
    private readonly ILogger<PairStore> _logger;
    private List<LinkedPair> _pairs;

    // Lookup indexes over _pairs, rebuilt wholesale on every mutation.
    // See agentic/ARCHITECTURE.md, "Persistence".
    private Dictionary<Guid, LinkedPair> _byEpisodeId = new();
    private Dictionary<Guid, LinkedPair> _byMovieId = new();
    private Dictionary<string, LinkedPair> _byHardLinkPath = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<Guid, int> _positionById = new();

    public PairStore(IApplicationPaths applicationPaths, ILogger<PairStore> logger)
    {
        _logger = logger;

        var pluginDataDir = Path.Combine(applicationPaths.PluginConfigurationsPath, "SpecialToMovie");
        Directory.CreateDirectory(pluginDataDir);

        _dataFilePath = Path.Combine(pluginDataDir, "pairs.json");
        _backupFilePath = Path.Combine(pluginDataDir, "pairs.backup.json");

        // Clean up stale temp file from a previous crash
        var tempPath = _dataFilePath + ".tmp";
        if (File.Exists(tempPath))
        {
            try
            {
                File.Delete(tempPath);
                _logger.LogInformation("Cleaned up stale temp file: {Path}", tempPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up stale temp file: {Path}", tempPath);
            }
        }

        _pairs = Load();
        RebuildIndexes();
    }

    // ! Callers must hold _lock. First entry wins on a duplicate key.
    private void RebuildIndexes()
    {
        _byEpisodeId = new Dictionary<Guid, LinkedPair>(_pairs.Count);
        _byMovieId = new Dictionary<Guid, LinkedPair>(_pairs.Count);
        _byHardLinkPath = new Dictionary<string, LinkedPair>(_pairs.Count, StringComparer.OrdinalIgnoreCase);
        _positionById = new Dictionary<Guid, int>(_pairs.Count);

        for (var i = 0; i < _pairs.Count; i++)
        {
            var pair = _pairs[i];

            _byEpisodeId.TryAdd(pair.EpisodeItemId, pair);
            _positionById.TryAdd(pair.Id, i);

            if (pair.MovieItemId is { } movieId && movieId != Guid.Empty)
            {
                _byMovieId.TryAdd(movieId, pair);
            }

            if (!string.IsNullOrEmpty(pair.HardLinkPath))
            {
                _byHardLinkPath.TryAdd(pair.HardLinkPath, pair);
            }
        }
    }

    public List<LinkedPair> GetAll()
    {
        lock (_lock)
        {
            return new List<LinkedPair>(_pairs);
        }
    }

    public LinkedPair? GetById(Guid pairId)
    {
        lock (_lock)
        {
            return _positionById.TryGetValue(pairId, out var index) ? _pairs[index] : null;
        }
    }

    public LinkedPair? GetByEpisodeId(Guid episodeItemId)
    {
        lock (_lock)
        {
            return _byEpisodeId.GetValueOrDefault(episodeItemId);
        }
    }

    public LinkedPair? GetByMovieId(Guid movieItemId)
    {
        lock (_lock)
        {
            return _byMovieId.GetValueOrDefault(movieItemId);
        }
    }

    public LinkedPair? GetByHardLinkPath(string hardLinkPath)
    {
        lock (_lock)
        {
            return string.IsNullOrEmpty(hardLinkPath)
                ? null
                : _byHardLinkPath.GetValueOrDefault(hardLinkPath);
        }
    }

    public bool ExistsForEpisode(Guid episodeItemId)
    {
        lock (_lock)
        {
            return _byEpisodeId.ContainsKey(episodeItemId);
        }
    }

    public void Upsert(LinkedPair pair)
    {
        lock (_lock)
        {
            pair.UpdatedUtc = DateTime.UtcNow;

            if (_positionById.TryGetValue(pair.Id, out var index))
            {
                _pairs[index] = pair;
            }
            else
            {
                if (pair.CreatedUtc == default)
                {
                    pair.CreatedUtc = DateTime.UtcNow;
                }

                _pairs.Add(pair);
            }

            RebuildIndexes();
            Save();
        }
    }

    public void UpsertMany(IEnumerable<LinkedPair> pairs)
    {
        lock (_lock)
        {
            foreach (var pair in pairs)
            {
                pair.UpdatedUtc = DateTime.UtcNow;

                if (_positionById.TryGetValue(pair.Id, out var index))
                {
                    _pairs[index] = pair;
                }
                else
                {
                    if (pair.CreatedUtc == default)
                    {
                        pair.CreatedUtc = DateTime.UtcNow;
                    }

                    // ! Keeps the map usable for the rest of the batch: a pair named
                    // twice in one call updates in place.
                    _positionById[pair.Id] = _pairs.Count;
                    _pairs.Add(pair);
                }
            }

            RebuildIndexes();
            Save();
        }
    }

    public void Remove(Guid pairId)
    {
        lock (_lock)
        {
            var removed = _pairs.RemoveAll(p => p.Id == pairId);
            if (removed > 0)
            {
                RebuildIndexes();
                Save();
            }
        }
    }

    public void RemoveMany(IEnumerable<Guid> pairIds)
    {
        lock (_lock)
        {
            var idSet = new HashSet<Guid>(pairIds);
            var removed = _pairs.RemoveAll(p => idSet.Contains(p.Id));
            if (removed > 0)
            {
                RebuildIndexes();
                Save();
            }
        }
    }

    public int Clear()
    {
        lock (_lock)
        {
            var count = _pairs.Count;
            if (count > 0)
            {
                _pairs.Clear();
                RebuildIndexes();
                Save();
            }

            return count;
        }
    }

    private List<LinkedPair> Load()
    {
        if (!File.Exists(_dataFilePath))
        {
            return new List<LinkedPair>();
        }

        try
        {
            var json = File.ReadAllText(_dataFilePath);
            return JsonSerializer.Deserialize<List<LinkedPair>>(json, SerializerOptions)
                   ?? new List<LinkedPair>();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // ! Runs from the constructor: an escaping failure takes the whole plugin down.
            // Locked, truncated and unreadable all land here, not only malformed JSON.
            _logger.LogError(ex, "Failed to read pair store, attempting backup restore");
            return LoadBackup();
        }
    }

    private List<LinkedPair> LoadBackup()
    {
        if (!File.Exists(_backupFilePath))
        {
            _logger.LogWarning("No backup file found, starting with empty pair store");
            return new List<LinkedPair>();
        }

        try
        {
            var json = File.ReadAllText(_backupFilePath);
            var pairs = JsonSerializer.Deserialize<List<LinkedPair>>(json, SerializerOptions)
                        ?? new List<LinkedPair>();
            _logger.LogInformation("Restored {Count} pairs from backup", pairs.Count);

            // Restore the primary file from backup
            File.Copy(_backupFilePath, _dataFilePath, overwrite: true);
            return pairs;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Backup pair store unreadable, starting with empty pair store");
            return new List<LinkedPair>();
        }
    }

    // ! Never throws: a write failure is logged and the in-memory state kept.
    // See agentic/ARCHITECTURE.md, "Persistence".
    private void Save()
    {
        try
        {
            // Atomic backup: copy to temp, then rename
            if (File.Exists(_dataFilePath))
            {
                var backupTemp = _backupFilePath + ".tmp";
                File.Copy(_dataFilePath, backupTemp, overwrite: true);
                File.Move(backupTemp, _backupFilePath, overwrite: true);
            }

            // Write to temp file first, then atomic rename to avoid partial writes on crash
            var tempPath = _dataFilePath + ".tmp";
            var json = JsonSerializer.Serialize(_pairs, SerializerOptions);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _dataFilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(
                ex,
                "Failed to persist the pair store to {Path}. {Count} pairs are held in memory and " +
                "will be written again by the next change; they are lost if the server restarts first.",
                _dataFilePath,
                _pairs.Count);
        }
    }
}
