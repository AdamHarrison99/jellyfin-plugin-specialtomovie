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

    void LinkSubtitles(string episodePath, string movieFolderPath, string movieTitle, int? year);

    void SyncSubtitles(string episodePath, string hardLinkPath, string movieTitle, int? year);
}
