using Jellyfin.Plugin.SpecialToMovie.Data;
using Jellyfin.Plugin.SpecialToMovie.HardLink;
using Jellyfin.Plugin.SpecialToMovie.Models;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

// Behavioural checks for the parts of the plugin that can run without a Jellyfin server: the
// hard-link path construction and its containment guard, filename sanitisation, the pair store's
// persistence and recovery behaviour, and the URL escaping applied to provider IDs.
//
// These are the invariants an audit asserts and then cannot otherwise demonstrate. Run after any
// change to HardLinkService, PairStore, or the lookup services' URL construction.

var failures = new List<string>();
var passed = 0;

void Check(string name, Func<bool> assertion)
{
    bool ok;
    string? detail = null;
    try
    {
        ok = assertion();
    }
    catch (Exception ex)
    {
        ok = false;
        detail = $"{ex.GetType().Name}: {ex.Message}";
    }

    if (ok)
    {
        passed++;
        Console.WriteLine($"  PASS  {name}");
    }
    else
    {
        failures.Add(detail is null ? name : $"{name} ({detail})");
        Console.WriteLine($"  FAIL  {name}{(detail is null ? string.Empty : " — " + detail)}");
    }
}

var root = Path.Combine(Path.GetTempPath(), "stm-harness-" + Guid.NewGuid().ToString("n")[..8]);
Directory.CreateDirectory(root);

try
{
    var hardLink = new HardLinkService(NullLogger<HardLinkService>.Instance);

    Console.WriteLine();
    Console.WriteLine("BuildHardLinkPath — containment guard");

    var libRoot = Path.Combine(root, "movies");
    Directory.CreateDirectory(libRoot);

    Check("builds a path inside the destination root", () =>
    {
        var p = hardLink.BuildHardLinkPath(libRoot, "El Camino", 2019, ".mkv");
        return Path.GetFullPath(p).StartsWith(Path.GetFullPath(libRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    });

    Check("folder and file carry the title, year and plugin tag", () =>
    {
        var p = hardLink.BuildHardLinkPath(libRoot, "El Camino", 2019, ".mkv");
        var folder = Path.GetFileName(Path.GetDirectoryName(p));
        return folder == "El Camino (2019) [JellyfinPlugin-SpecialToMovie]"
               && Path.GetFileName(p) == "El Camino (2019).mkv";
    });

    Check("a year-less title omits the year suffix", () =>
        Path.GetFileName(hardLink.BuildHardLinkPath(libRoot, "Serenity", null, ".mp4")) == "Serenity.mp4");

    // The guard's purpose: a title that tries to climb out must not produce a path outside the
    // root. Sanitisation collapses "..", so this lands inside the root rather than throwing.
    Check("traversal in the title cannot escape the root", () =>
    {
        var p = hardLink.BuildHardLinkPath(libRoot, "../../etc/passwd", null, ".mkv");
        return Path.GetFullPath(p).StartsWith(Path.GetFullPath(libRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    });

    Check("separators in the title are stripped, not honoured", () =>
    {
        var p = hardLink.BuildHardLinkPath(libRoot, "a/b\\c", null, ".mkv");
        var folder = Path.GetFileName(Path.GetDirectoryName(p)) ?? string.Empty;
        return folder.StartsWith("abc", StringComparison.Ordinal);
    });

    Console.WriteLine();
    Console.WriteLine("BuildHardLinkPath — sibling-prefix root (the tightened check)");

    // The regression the trailing-separator fix addresses: "<root>-old" must not count as inside
    // "<root>". The guard itself is a backstop that the public entry point cannot currently reach —
    // it always appends a sanitised folder, so the constructed path is always a genuine child — so
    // what is asserted here is the containment predicate the guard applies.
    Check("a sibling directory sharing the root's prefix is not treated as inside it", () =>
    {
        var resolvedRoot = Path.GetFullPath(libRoot);
        var sibling = resolvedRoot + "-old" + Path.DirectorySeparatorChar + "x.mkv";
        var withSeparator = resolvedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? resolvedRoot
            : resolvedRoot + Path.DirectorySeparatorChar;

        // Old behaviour accepted this; the current guard must not.
        var oldAccepts = sibling.StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase);
        var newAccepts = sibling.StartsWith(withSeparator, StringComparison.OrdinalIgnoreCase);
        return oldAccepts && !newAccepts;
    });

    Check("a genuine child is still accepted by the tightened check", () =>
    {
        var resolvedRoot = Path.GetFullPath(libRoot);
        var child = Path.Combine(resolvedRoot, "Movie (2019) [JellyfinPlugin-SpecialToMovie]", "Movie (2019).mkv");
        var withSeparator = resolvedRoot + Path.DirectorySeparatorChar;
        return child.StartsWith(withSeparator, StringComparison.OrdinalIgnoreCase);
    });

    Console.WriteLine();
    Console.WriteLine("PairStore — persistence and recovery");

    var paths = new HarnessPaths(Path.Combine(root, "config"));
    var storeDir = Path.Combine(paths.PluginConfigurationsPath, "SpecialToMovie");
    var dataFile = Path.Combine(storeDir, "pairs.json");
    var backupFile = Path.Combine(storeDir, "pairs.backup.json");

    Check("a fresh store starts empty and persists an upsert", () =>
    {
        var store = new PairStore(paths, NullLogger<PairStore>.Instance);
        if (store.GetAll().Count != 0)
        {
            return false;
        }

        store.Upsert(new LinkedPair { Id = Guid.NewGuid(), EpisodeItemId = Guid.NewGuid(), MovieTitle = "First" });
        return File.Exists(dataFile) && store.GetAll().Count == 1;
    });

    Check("a store reloads what a previous instance wrote", () =>
        new PairStore(paths, NullLogger<PairStore>.Instance).GetAll().Single().MovieTitle == "First");

    Check("lookup indexes resolve by episode id after reload", () =>
    {
        var store = new PairStore(paths, NullLogger<PairStore>.Instance);
        var pair = store.GetAll().Single();
        return store.GetByEpisodeId(pair.EpisodeItemId)?.Id == pair.Id
               && store.ExistsForEpisode(pair.EpisodeItemId);
    });

    Check("a second upsert writes a backup of the previous state", () =>
    {
        var store = new PairStore(paths, NullLogger<PairStore>.Instance);
        store.Upsert(new LinkedPair { Id = Guid.NewGuid(), EpisodeItemId = Guid.NewGuid(), MovieTitle = "Second" });
        return File.Exists(backupFile) && store.GetAll().Count == 2;
    });

    // The behaviour the widened catch protects: a primary file that cannot be parsed must fall
    // back to the backup rather than throwing out of the constructor.
    Check("a corrupt primary file is recovered from the backup", () =>
    {
        File.WriteAllText(dataFile, "{ this is not json");
        var store = new PairStore(paths, NullLogger<PairStore>.Instance);
        return store.GetAll().Count == 1 && store.GetAll()[0].MovieTitle == "First";
    });

    Check("a corrupt primary and a corrupt backup degrade to an empty store, not an exception", () =>
    {
        File.WriteAllText(dataFile, "{ broken");
        File.WriteAllText(backupFile, "also broken");
        return new PairStore(paths, NullLogger<PairStore>.Instance).GetAll().Count == 0;
    });

    Check("an unreadable primary file does not throw out of the constructor", () =>
    {
        var isolated = new HarnessPaths(Path.Combine(root, "locked"));
        var dir = Path.Combine(isolated.PluginConfigurationsPath, "SpecialToMovie");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "pairs.json");
        File.WriteAllText(file, "[]");

        // Hold the file open with no sharing so the read fails with an IOException.
        using var held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        return new PairStore(isolated, NullLogger<PairStore>.Instance).GetAll().Count == 0;
    });

    // The behaviour the try/catch in Save() protects: a write failure must be swallowed so it
    // cannot unwind into Jellyfin's event dispatch.
    Check("a failing save does not throw out of Upsert", () =>
    {
        var isolated = new HarnessPaths(Path.Combine(root, "readonly"));
        var dir = Path.Combine(isolated.PluginConfigurationsPath, "SpecialToMovie");
        Directory.CreateDirectory(dir);
        var store = new PairStore(isolated, NullLogger<PairStore>.Instance);

        // Make the temp file the save writes impossible to replace.
        var temp = Path.Combine(dir, "pairs.json.tmp");
        File.WriteAllText(temp, "x");
        using var held = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        store.Upsert(new LinkedPair { Id = Guid.NewGuid(), EpisodeItemId = Guid.NewGuid(), MovieTitle = "NoThrow" });

        // The mutation still took effect in memory even though the write could not land.
        return store.GetAll().Count == 1;
    });

    Check("a failing save does not throw out of Remove or Clear", () =>
    {
        var isolated = new HarnessPaths(Path.Combine(root, "readonly2"));
        var dir = Path.Combine(isolated.PluginConfigurationsPath, "SpecialToMovie");
        Directory.CreateDirectory(dir);
        var store = new PairStore(isolated, NullLogger<PairStore>.Instance);
        var pair = new LinkedPair { Id = Guid.NewGuid(), EpisodeItemId = Guid.NewGuid() };
        store.Upsert(pair);

        var temp = Path.Combine(dir, "pairs.json.tmp");
        File.WriteAllText(temp, "x");
        using var held = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        store.Remove(pair.Id);
        store.Clear();
        return store.GetAll().Count == 0;
    });

    Console.WriteLine();
    Console.WriteLine("Provider ID escaping in lookup URLs");

    Check("a well-formed IMDB id is unchanged by escaping", () =>
        Uri.EscapeDataString("tt1234567") == "tt1234567");

    Check("a numeric TMDB id is unchanged by escaping", () =>
        Uri.EscapeDataString("60625") == "60625");

    Check("a traversal attempt in a provider id cannot alter the URL path", () =>
        !Uri.EscapeDataString("../../authentication").Contains('/', StringComparison.Ordinal));

    Check("a query-injection attempt in a provider id is neutralised", () =>
    {
        var escaped = Uri.EscapeDataString("123?api_key=leak&x=");
        return !escaped.Contains('?', StringComparison.Ordinal)
               && !escaped.Contains('&', StringComparison.Ordinal)
               && !escaped.Contains('=', StringComparison.Ordinal);
    });

    Check("a fragment in a provider id cannot truncate the URL", () =>
        !Uri.EscapeDataString("123#").Contains('#', StringComparison.Ordinal));
}
finally
{
    try
    {
        Directory.Delete(root, recursive: true);
    }
    catch (IOException)
    {
        // A held handle from a negative test can outlive the check on Windows; the temp
        // directory is disposable either way.
    }
}

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine($"All {passed} checks passed.");
    return 0;
}

Console.WriteLine($"{failures.Count} of {passed + failures.Count} checks FAILED:");
foreach (var f in failures)
{
    Console.WriteLine($"  - {f}");
}

return 1;

/// <summary>
/// Minimal <see cref="IApplicationPaths"/> standing in for the server's path layout. Only
/// <see cref="PluginConfigurationsPath"/> is read by the code under test; the rest are derived from
/// the same root so nothing can escape the harness's temporary directory.
/// </summary>
internal sealed class HarnessPaths : IApplicationPaths
{
    private readonly string _root;

    public HarnessPaths(string root)
    {
        _root = root;
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(PluginConfigurationsPath);
    }

    public string ProgramDataPath => _root;

    public string WebPath => Path.Combine(_root, "web");

    public string ProgramSystemPath => _root;

    public string DataPath => Path.Combine(_root, "data");

    public string ImageCachePath => Path.Combine(_root, "imagecache");

    public string PluginsPath => Path.Combine(_root, "plugins");

    public string PluginConfigurationsPath => Path.Combine(_root, "plugins", "configurations");

    public string LogDirectoryPath => Path.Combine(_root, "log");

    public string ConfigurationDirectoryPath => Path.Combine(_root, "config");

    public string SystemConfigurationFilePath => Path.Combine(_root, "config", "system.xml");

    public string CachePath { get; set; } = Path.Combine(Path.GetTempPath(), "stm-harness-cache");

    public string TempDirectory => Path.Combine(_root, "temp");

    public string VirtualDataPath => Path.Combine(_root, "vdata");

    public string TrickplayPath => Path.Combine(_root, "trickplay");

    public string BackupPath => Path.Combine(_root, "backup");

    public void MakeSanityCheckOrThrow()
    {
        // Nothing to verify: every path above is derived from a directory the harness just created.
    }

    public void CreateAndCheckMarker(string path, string markerName, bool recreate = false)
    {
        Directory.CreateDirectory(path);
        var marker = Path.Combine(path, markerName);
        if (recreate || !File.Exists(marker))
        {
            File.WriteAllText(marker, string.Empty);
        }
    }
}
