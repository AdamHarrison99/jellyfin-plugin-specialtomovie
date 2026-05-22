using System.Collections.Concurrent;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpecialToMovie.Lookup;

public sealed class NotFoundCache : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly ConcurrentDictionary<string, long> _cache = new();
    private readonly string _filePath;
    private readonly ILogger<NotFoundCache> _logger;
    private readonly object _saveLock = new();
    private Timer? _saveTimer;
    private volatile bool _dirty;
    private volatile bool _disposed;

    public NotFoundCache(IApplicationPaths applicationPaths, ILogger<NotFoundCache> logger)
    {
        _logger = logger;
        var pluginDataDir = Path.Combine(applicationPaths.PluginConfigurationsPath, "SpecialToMovie");
        Directory.CreateDirectory(pluginDataDir);
        _filePath = Path.Combine(pluginDataDir, "notfound_cache.json");
        LoadFromDisk();
    }

    public bool IsNotFound(string key, int cacheDays)
    {
        if (_cache.TryGetValue(key, out var cachedTicks) &&
            (DateTime.UtcNow.Ticks - cachedTicks) < TimeSpan.FromDays(cacheDays).Ticks)
        {
            return true;
        }

        return false;
    }

    public void Add(string key)
    {
        _cache[key] = DateTime.UtcNow.Ticks;
        ScheduleSave();
    }

    public void Dispose()
    {
        lock (_saveLock)
        {
            _disposed = true;
            Interlocked.Exchange(ref _saveTimer, null)?.Dispose();
            if (_dirty)
            {
                SaveToDiskCore();
            }
        }
    }

    private void ScheduleSave()
    {
        lock (_saveLock)
        {
            if (_disposed)
            {
                return;
            }

            _dirty = true;
            Interlocked.Exchange(ref _saveTimer, null)?.Dispose();
            _saveTimer = new Timer(_ => SaveToDisk(), null, TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
        }
    }

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            var json = File.ReadAllText(_filePath);
            var entries = JsonSerializer.Deserialize<Dictionary<string, long>>(json);
            if (entries == null)
            {
                return;
            }

            var maxAge = TimeSpan.FromDays(Math.Max(1, Plugin.Instance?.Configuration.NotFoundCacheDays ?? 14)).Ticks;
            var now = DateTime.UtcNow.Ticks;
            int loaded = 0;

            foreach (var (key, ticks) in entries)
            {
                if ((now - ticks) < maxAge)
                {
                    _cache[key] = ticks;
                    loaded++;
                }
            }

            _logger.LogInformation("Loaded {Count} not-found cache entries from disk", loaded);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load not-found cache from disk, starting fresh");
        }
    }

    private void SaveToDisk()
    {
        lock (_saveLock)
        {
            SaveToDiskCore();
        }
    }

    private void SaveToDiskCore()
    {
        try
        {
            _dirty = false;
            var maxAge = TimeSpan.FromDays(Math.Max(1, Plugin.Instance?.Configuration.NotFoundCacheDays ?? 14)).Ticks;
            var now = DateTime.UtcNow.Ticks;

            var entries = new Dictionary<string, long>();
            foreach (var (key, ticks) in _cache)
            {
                if ((now - ticks) < maxAge)
                {
                    entries[key] = ticks;
                }
            }

            var tmpPath = _filePath + ".tmp";
            File.WriteAllText(tmpPath, JsonSerializer.Serialize(entries, JsonOptions));
            File.Move(tmpPath, _filePath, overwrite: true);

            _logger.LogDebug("Saved {Count} not-found cache entries to disk", entries.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save not-found cache to disk");
        }
    }
}
