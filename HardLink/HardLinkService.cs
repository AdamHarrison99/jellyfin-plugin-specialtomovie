using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using Jellyfin.Plugin.SpecialToMovie.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpecialToMovie.HardLink;

public partial class HardLinkService : IHardLinkService
{
    private const string PluginFolderTag = "[JellyfinPlugin-SpecialToMovie]";

    private static readonly char[] InvalidFileNameChars =
        ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private readonly ILogger<HardLinkService> _logger;

    public HardLinkService(ILogger<HardLinkService> logger)
    {
        _logger = logger;
    }

    public bool Create(string sourcePath, string linkPath)
    {
        try
        {
            var linkDir = Path.GetDirectoryName(linkPath);
            if (!string.IsNullOrEmpty(linkDir))
            {
                Directory.CreateDirectory(linkDir);
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var extSource = ToExtendedLengthPath(sourcePath);
                var extLink = ToExtendedLengthPath(linkPath);
                if (!CreateHardLinkW(extLink, extSource, IntPtr.Zero))
                {
                    var error = Marshal.GetLastWin32Error();
                    _logger.LogError("CreateHardLink failed with Win32 error {Error}: {Source} -> {Link}", error, sourcePath, linkPath);
                    return false;
                }
            }
            else
            {
                var result = LinkPosix(sourcePath, linkPath);
                if (result != 0)
                {
                    var error = Marshal.GetLastWin32Error();
                    _logger.LogError("link() failed with errno {Error}: {Source} -> {Link}", error, sourcePath, linkPath);
                    return false;
                }
            }

            _logger.LogInformation("Created hard link: {Source} -> {Link}", sourcePath, linkPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create hard link: {Source} -> {Link}", sourcePath, linkPath);
            return false;
        }
    }

    public bool Delete(string linkPath)
    {
        try
        {
            if (!File.Exists(linkPath))
            {
                _logger.LogDebug("Hard link already gone: {Path}", linkPath);
                return true;
            }

            File.Delete(linkPath);
            _logger.LogInformation("Deleted hard link: {Path}", linkPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete hard link: {Path}", linkPath);
            return false;
        }
    }

    public bool Exists(string linkPath)
    {
        return File.Exists(linkPath);
    }

    public bool ValidateSameFilesystem(string path1, string path2)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var root1 = Path.GetPathRoot(path1);
                var root2 = Path.GetPathRoot(path2);
                return string.Equals(root1, root2, StringComparison.OrdinalIgnoreCase);
            }

            // Linux/macOS: compare st_dev via stat() to detect same filesystem
            var stat1 = new StatBuf();
            var stat2 = new StatBuf();

            if (StatPosix(path1, ref stat1) != 0 || StatPosix(path2, ref stat2) != 0)
            {
                _logger.LogWarning("stat() failed for filesystem comparison: {Path1}, {Path2}", path1, path2);
                return false;
            }

            return stat1.st_dev == stat2.st_dev;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not validate filesystem for {Path1} and {Path2}", path1, path2);
            return false;
        }
    }

    public string BuildMovieFolderName(string movieTitle, int? year)
    {
        var sanitized = SanitizeFileName(movieTitle);
        var yearSuffix = year.HasValue ? $" ({year.Value})" : string.Empty;
        return $"{sanitized}{yearSuffix} {PluginFolderTag}";
    }

    public string BuildHardLinkPath(string destinationLibraryPath, string movieTitle, int? year, string sourceExtension)
    {
        var folderName = BuildMovieFolderName(movieTitle, year);
        var sanitized = SanitizeFileName(movieTitle);
        var yearSuffix = year.HasValue ? $" ({year.Value})" : string.Empty;
        var fileName = $"{sanitized}{yearSuffix}{sourceExtension}";

        var candidate = Path.Combine(destinationLibraryPath, folderName, fileName);

        // Verify the resolved path is still under the destination library root
        var resolvedPath = Path.GetFullPath(candidate);
        var resolvedRoot = Path.GetFullPath(destinationLibraryPath);
        if (!resolvedPath.StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Constructed path '{resolvedPath}' escapes destination root '{resolvedRoot}'");
        }

        return candidate;
    }

    public void WriteNfoFile(string movieFolderPath, string movieTitle, int? year, MovieMatch match)
    {
        var sanitized = SanitizeFileName(movieTitle);
        var yearSuffix = year.HasValue ? $" ({year.Value})" : string.Empty;
        var nfoPath = Path.Combine(movieFolderPath, $"{sanitized}{yearSuffix}.nfo");

        var settings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = new UTF8Encoding(false),
            OmitXmlDeclaration = false
        };

        using var stream = new FileStream(nfoPath, FileMode.Create, FileAccess.Write);
        using var writer = XmlWriter.Create(stream, settings);

        writer.WriteStartDocument(true);
        writer.WriteStartElement("movie");

        writer.WriteElementString("title", movieTitle);
        if (year.HasValue)
        {
            writer.WriteElementString("year", year.Value.ToString());
        }

        if (!string.IsNullOrEmpty(match.ImdbId))
        {
            writer.WriteStartElement("uniqueid");
            writer.WriteAttributeString("type", "imdb");
            writer.WriteAttributeString("default", "true");
            writer.WriteString(match.ImdbId);
            writer.WriteEndElement();
        }

        if (!string.IsNullOrEmpty(match.TmdbMovieId))
        {
            writer.WriteStartElement("uniqueid");
            writer.WriteAttributeString("type", "tmdb");
            writer.WriteString(match.TmdbMovieId);
            writer.WriteEndElement();
        }

        if (!string.IsNullOrEmpty(match.TvdbMovieId))
        {
            writer.WriteStartElement("uniqueid");
            writer.WriteAttributeString("type", "tvdb");
            writer.WriteString(match.TvdbMovieId);
            writer.WriteEndElement();
        }

        writer.WriteEndElement(); // movie
        writer.WriteEndDocument();

        _logger.LogInformation("Wrote NFO metadata: {Path}", nfoPath);
    }

    public void DeleteMovieFolder(string hardLinkPath)
    {
        var folderPath = Path.GetDirectoryName(hardLinkPath);
        if (string.IsNullOrEmpty(folderPath))
        {
            _logger.LogWarning("Cannot determine folder for hard link: {Path}", hardLinkPath);
            return;
        }

        // Resolve to absolute path to defeat symlink/traversal tricks
        var resolvedFolder = Path.GetFullPath(folderPath);

        var folderName = Path.GetFileName(resolvedFolder);
        if (string.IsNullOrEmpty(folderName) || !folderName.Contains(PluginFolderTag, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Refusing to delete folder not tagged with {Tag}: {Path}",
                PluginFolderTag,
                resolvedFolder);
            return;
        }

        // Verify the resolved folder is still under its expected parent (the library root)
        var parentDir = Path.GetDirectoryName(resolvedFolder);
        if (string.IsNullOrEmpty(parentDir) ||
            !resolvedFolder.StartsWith(Path.GetFullPath(parentDir), StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Resolved folder escapes expected parent: {Path}", resolvedFolder);
            return;
        }

        if (!Directory.Exists(resolvedFolder))
        {
            _logger.LogDebug("Movie folder already gone: {Path}", resolvedFolder);
            return;
        }

        try
        {
            Directory.Delete(resolvedFolder, recursive: true);
            _logger.LogInformation("Deleted movie folder: {Path}", resolvedFolder);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete movie folder: {Path}", resolvedFolder);
        }
    }

    private static string ToExtendedLengthPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return path;
        }

        var full = Path.GetFullPath(path);
        return @"\\?\" + full;
    }

    private static readonly string[] ReservedWindowsNames =
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5",
         "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4",
         "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    private static string SanitizeFileName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            // Strip invalid filename chars and control characters
            if (Array.IndexOf(InvalidFileNameChars, c) >= 0 || char.IsControl(c))
            {
                continue;
            }

            sb.Append(c);
        }

        var result = sb.ToString().Trim('.', ' ');

        // Collapse any ".." sequences that could cause path traversal (loop until stable)
        while (result.Contains("..", StringComparison.Ordinal))
        {
            result = result.Replace("..", ".", StringComparison.Ordinal);
        }

        // Guard against Windows reserved device names
        var nameWithoutExt = result.Split('.')[0].ToUpperInvariant();
        if (Array.Exists(ReservedWindowsNames, r => r == nameWithoutExt))
        {
            result = "_" + result;
        }

        return result;
    }

    // Windows P/Invoke
    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    // Linux/macOS P/Invoke
    [LibraryImport("libc", EntryPoint = "link", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LinkPosix(string oldpath, string newpath);

    // stat() for filesystem device comparison on Linux/macOS
    // Using __xstat with ver=1 on glibc, or stat directly — we use the libc "stat" wrapper.
    // The struct layout varies by platform; we only need st_dev which is the first field.
    [LibraryImport("libc", EntryPoint = "stat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int StatPosix(string path, ref StatBuf buf);

    [StructLayout(LayoutKind.Sequential)]
    private struct StatBuf
    {
        public ulong st_dev;

        // We don't need the rest — pad generously so the kernel doesn't write past our buffer
        private unsafe fixed byte _padding[1024];
    }
}
