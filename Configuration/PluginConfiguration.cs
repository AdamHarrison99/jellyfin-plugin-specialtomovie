using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SpecialToMovie.Configuration;

/// <summary>
/// Plugin configuration model.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    public string TmdbApiKey { get; set; } = string.Empty;

    public string TvdbApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether dry run mode is active.
    /// When enabled, the plugin logs all actions but makes zero filesystem changes.
    /// Enabled by default so users can review matches before committing.
    /// </summary>
    public bool DryRunMode { get; set; } = true;

    public bool AutoDetectEnabled { get; set; } = true;

    public bool RequireDualConfirmation { get; set; } = false;

    public List<LibraryMapping> LibraryMappings { get; set; } = new();

    public Dictionary<string, string> ForceLinks { get; set; } = new();

    public List<string> IgnoreList { get; set; } = new();

    public int CleanupIntervalHours { get; set; } = 12;

    public bool AutoDeleteOnRemoval { get; set; } = false;

    public bool TwoWayDeletion { get; set; } = false;
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
