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

    void Remove(Guid pairId);
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
            return _pairs.Find(p => p.Id == pairId);
        }
    }

    public LinkedPair? GetByEpisodeId(Guid episodeItemId)
    {
        lock (_lock)
        {
            return _pairs.Find(p => p.EpisodeItemId == episodeItemId);
        }
    }

    public LinkedPair? GetByMovieId(Guid movieItemId)
    {
        lock (_lock)
        {
            return _pairs.Find(p => p.MovieItemId == movieItemId);
        }
    }

    public LinkedPair? GetByHardLinkPath(string hardLinkPath)
    {
        lock (_lock)
        {
            return _pairs.Find(p =>
                string.Equals(p.HardLinkPath, hardLinkPath, StringComparison.OrdinalIgnoreCase));
        }
    }

    public bool ExistsForEpisode(Guid episodeItemId)
    {
        lock (_lock)
        {
            return _pairs.Exists(p => p.EpisodeItemId == episodeItemId);
        }
    }

    public void Upsert(LinkedPair pair)
    {
        lock (_lock)
        {
            pair.UpdatedUtc = DateTime.UtcNow;

            var index = _pairs.FindIndex(p => p.Id == pair.Id);
            if (index >= 0)
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
                Save();
            }
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
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse pair store, attempting backup restore");
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
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Backup file also corrupt, starting with empty pair store");
            return new List<LinkedPair>();
        }
    }

    private void Save()
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
}
