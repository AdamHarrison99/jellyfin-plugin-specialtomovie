using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SpecialToMovie.Configuration;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MetadataProviderType
{
    Tmdb = 0,
    Tvdb = 1
}

public class PluginConfiguration : BasePluginConfiguration
{
    public MetadataProviderType PrimaryProvider { get; set; } = MetadataProviderType.Tmdb;

    public string TmdbApiKey { get; set; } = string.Empty;

    public string TvdbApiKey { get; set; } = string.Empty;

    // ! Defaults on: a new install logs its matches and touches no files until this is cleared.
    public bool DryRunMode { get; set; } = true;

    public bool AutoDetectEnabled { get; set; } = true;

    public bool AllowOvaLinking { get; set; } = false;

    public bool RequireDualConfirmation { get; set; } = false;

    public List<LibraryMapping> LibraryMappings { get; set; } = new();

    public List<ForceLinkEntry> ForceLinks { get; set; } = new();

    public List<string> IgnoreList { get; set; } = new();

    public int CleanupIntervalHours { get; set; } = 6;

    public bool AutoDeleteOnRemoval { get; set; } = false;

    public bool TwoWayDeletion { get; set; } = false;

    public bool WatchStatusOnly { get; set; } = false;

    public int MetadataCacheDays { get; set; } = 7;

    // Read per call: toggling it takes effect with no server restart.
    public bool ShowCrossLinks { get; set; } = true;

    // Governs the injected script only. Cleared, the cross-links stay as plain text links.
    public bool InjectClientScript { get; set; } = true;
}

// Maps one source TV library to one destination movie library for hard link placement.
public class LibraryMapping
{
    public Guid SourceLibraryId { get; set; }

    public Guid DestinationLibraryId { get; set; }

    public string DestinationPath { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
}

public class ForceLinkEntry
{
    public string EpisodeKey { get; set; } = string.Empty;

    public string MovieTitle { get; set; } = string.Empty;
}
