using Jellyfin.Plugin.SpecialToMovie.Models;

namespace Jellyfin.Plugin.SpecialToMovie.HardLink;

public interface IHardLinkService
{
    bool Create(string sourcePath, string linkPath);

    bool Exists(string linkPath);

    bool ValidateSameFilesystem(string path1, string path2);

    string BuildMovieFolderName(string movieTitle, int? year);

    string BuildHardLinkPath(string destinationLibraryPath, string movieTitle, int? year, string sourceExtension);

    void WriteNfoFile(string movieFolderPath, string movieTitle, int? year, MovieMatch match);

    List<LinkedSubtitle> LinkSubtitles(string episodePath, string movieFolderPath, string movieTitle, int? year);

    SubtitleSyncResult SyncSubtitles(string episodePath, string hardLinkPath, string movieTitle, int? year, List<LinkedSubtitle> existing);
}

/// <summary>
/// Outcome of a bidirectional subtitle sync. <see cref="Records"/> are the tracked links that
/// still exist (plus any newly created). <see cref="Deletions"/> are links where the user removed
/// one side and the surviving file was confirmed (by content hash) to still be the linked copy —
/// the caller deletes these via Jellyfin's subtitle API.
/// </summary>
public class SubtitleSyncResult
{
    public List<LinkedSubtitle> Records { get; set; } = new();

    public List<SubtitleDeletion> Deletions { get; set; } = new();
}

public class SubtitleDeletion
{
    public required LinkedSubtitle Record { get; init; }

    public bool SurvivingIsEpisode { get; init; }

    public string SurvivingPath { get; init; } = string.Empty;
}
