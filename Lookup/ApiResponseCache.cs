using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpecialToMovie.Lookup;

public sealed class ApiResponseCache : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly string _filePath;
    private readonly ILogger<ApiResponseCache> _logger;
    private readonly object _saveLock = new();
    private Timer? _saveTimer;
    private volatile bool _dirty;
    private volatile bool _disposed;

    public ApiResponseCache(IApplicationPaths applicationPaths, ILogger<ApiResponseCache> logger)
    {
        _logger = logger;
        var pluginDataDir = Path.Combine(applicationPaths.PluginConfigurationsPath, "SpecialToMovie");
        Directory.CreateDirectory(pluginDataDir);
        _filePath = Path.Combine(pluginDataDir, "api_cache.json");
        LoadFromDisk();
    }

    public string? Get(string key, int cacheDays)
    {
        if (!_cache.TryGetValue(key, out var entry))
        {
            return null;
        }

        if (cacheDays > 0 && (DateTime.UtcNow.Ticks - entry.Timestamp) >= TimeSpan.FromDays(cacheDays).Ticks)
        {
            _cache.TryRemove(key, out _);
            return null;
        }

        return entry.Response;
    }

    public bool IsCached(string key, int cacheDays)
    {
        if (!_cache.TryGetValue(key, out var entry))
        {
            return false;
        }

        if (cacheDays > 0 && (DateTime.UtcNow.Ticks - entry.Timestamp) >= TimeSpan.FromDays(cacheDays).Ticks)
        {
            _cache.TryRemove(key, out _);
            return false;
        }

        return true;
    }

    public void Add(string key, string? response)
    {
        _cache[key] = new CacheEntry { Timestamp = DateTime.UtcNow.Ticks, Response = response };
        ScheduleSave();
    }

    public int Clear()
    {
        var count = _cache.Count;
        _cache.Clear();
        ScheduleSave();
        return count;
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
            var entries = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(json);
            if (entries == null)
            {
                return;
            }

            var cacheDays = Plugin.Instance?.Configuration.MetadataCacheDays ?? 7;
            var maxAge = cacheDays <= 0 ? long.MaxValue : TimeSpan.FromDays(cacheDays).Ticks;
            var now = DateTime.UtcNow.Ticks;
            int loaded = 0;

            foreach (var (key, entry) in entries)
            {
                if ((now - entry.Timestamp) < maxAge)
                {
                    _cache[key] = entry;
                    loaded++;
                }
            }

            _logger.LogInformation("Loaded {Count} API cache entries from disk", loaded);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load API cache from disk, starting fresh");
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
            var cacheDays = Plugin.Instance?.Configuration.MetadataCacheDays ?? 7;
            var maxAge = cacheDays <= 0 ? long.MaxValue : TimeSpan.FromDays(cacheDays).Ticks;
            var now = DateTime.UtcNow.Ticks;

            var entries = new Dictionary<string, CacheEntry>();
            foreach (var (key, entry) in _cache)
            {
                if ((now - entry.Timestamp) < maxAge)
                {
                    entries[key] = entry;
                }
            }

            var tmpPath = _filePath + ".tmp";
            File.WriteAllText(tmpPath, JsonSerializer.Serialize(entries, JsonOptions));
            File.Move(tmpPath, _filePath, overwrite: true);

            _logger.LogDebug("Saved {Count} API cache entries to disk", entries.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save API cache to disk");
        }
    }

    private sealed class CacheEntry
    {
        [JsonPropertyName("t")]
        public long Timestamp { get; set; }

        [JsonPropertyName("r")]
        public string? Response { get; set; }
    }
}
