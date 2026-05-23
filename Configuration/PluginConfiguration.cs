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

/// <summary>
/// Plugin configuration model.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    public MetadataProviderType PrimaryProvider { get; set; } = MetadataProviderType.Tmdb;

    public string TmdbApiKey { get; set; } = string.Empty;

    public string TvdbApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether dry run mode is active.
    /// When enabled, the plugin logs all actions but makes zero filesystem changes.
    /// Enabled by default so users can review matches before committing.
    /// </summary>
    public bool DryRunMode { get; set; } = true;

    public bool AutoDetectEnabled { get; set; } = true;

    public bool AllowOvaLinking { get; set; } = false;

    public bool RequireDualConfirmation { get; set; } = false;

    public List<LibraryMapping> LibraryMappings { get; set; } = new();

    public List<ForceLinkEntry> ForceLinks { get; set; } = new();

    public List<string> IgnoreList { get; set; } = new();

    public int CleanupIntervalHours { get; set; } = 12;

    public bool AutoDeleteOnRemoval { get; set; } = false;

    public bool TwoWayDeletion { get; set; } = false;

    public int MetadataCacheDays { get; set; } = 7;
}

/// <summary>
/// Maps a source TV library to a destination movie library for hard link placement.
/// </summary>
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
