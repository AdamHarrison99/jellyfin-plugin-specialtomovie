# Jellyfin Plugin: SpecialToMovie

## Context

When a TV special (Season 0 episode) is also a standalone movie (e.g., *El Camino: A Breaking Bad Movie*, *Downton Abbey: A New Era*), Jellyfin users want it in both their TV and Movies libraries. The current workaround — manual hard links — works for file deduplication but **watch status doesn't sync** because Jellyfin tracks play state by internal item ID, not file path. Two directory entries = two unrelated items.

This plugin automates the entire lifecycle: detection, hard link creation, bidirectional watch sync, and cleanup on deletion.

---

## Project Structure

```
Jellyfin.Plugin.SpecialToMovie/
├── Jellyfin.Plugin.SpecialToMovie.csproj
├── Plugin.cs                              # BasePlugin<PluginConfiguration> entry point
├── PluginServiceRegistrator.cs            # IPluginServiceRegistrator — DI registration
│
├── Configuration/
│   ├── PluginConfiguration.cs             # Settings model (movie library path, API keys, overrides)
│   └── configPage.html                    # Admin dashboard UI
│
├── Models/
│   ├── LinkedPair.cs                      # Data model for an episode↔movie pair
│   ├── PairStatus.cs                      # Enum: Pending, Active, Error
│   └── MovieMatch.cs                      # Result from TMDB/TVDB lookup (title, year, IDs)
│
├── Data/
│   └── PairStore.cs                       # JSON file persistence for linked pairs
│
├── Lookup/
│   ├── IMetadataLookupService.cs          # Interface for cross-ref lookups
│   ├── TmdbLookupService.cs              # TMDB /find + /tv/{id}/season/0/episode/{n}/external_ids
│   ├── TvdbLookupService.cs              # TVDB /movies, /search, episode→movie cross-ref
│   └── AggregatedLookupService.cs        # Runs TMDB + TVDB, merges results, deduplicates
│
├── HardLink/
│   ├── IHardLinkService.cs               # Interface
│   └── HardLinkService.cs                # Cross-platform P/Invoke (Windows + Linux)
│
├── Services/
│   ├── SpecialDetectionService.cs        # Core: find Season 0 episodes, check if movie, create link
│   └── WatchSyncService.cs              # Bidirectional watch status mirroring
│
├── EventHandlers/
│   └── LibraryEventHandler.cs            # IHostedService — subscribes to ItemAdded/ItemRemoved
│
├── Tasks/
│   ├── FullScanTask.cs                   # IScheduledTask — periodic full-library detection scan
│   └── CleanupTask.cs                    # IScheduledTask — validate pairs, fix orphans
│
└── build.yaml                             # Plugin metadata (GUID, version, targetAbi)
```

---

## Data Model

### LinkedPair.cs

```csharp
public class LinkedPair
{
    public Guid Id { get; set; }                    // Unique pair ID
    public Guid EpisodeItemId { get; set; }          // Jellyfin Episode item ID
    public Guid? MovieItemId { get; set; }           // Jellyfin Movie item ID (null until scanner picks it up)
    public Guid SourceLibraryId { get; set; }        // TV library that owns the episode
    public Guid DestinationLibraryId { get; set; }   // Movie library (or same library if mixed)
    public string EpisodePath { get; set; }          // Original file path
    public string HardLinkPath { get; set; }         // Path of the created hard link (null if paired to existing movie)
    public bool IsExistingMovie { get; set; }        // True if paired to a pre-existing movie (no hard link created)
    public string MovieTitle { get; set; }           // e.g., "El Camino: A Breaking Bad Movie"
    public int? MovieYear { get; set; }              // Release year
    public string TmdbMovieId { get; set; }          // TMDB movie ID (if matched)
    public string TvdbMovieId { get; set; }          // TVDB movie ID / slug (if matched)
    public string ImdbId { get; set; }               // IMDB ID (shared identifier)
    public PairStatus Status { get; set; }           // Pending / Active / Error
    public string ErrorMessage { get; set; }         // Last error if Status == Error
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public enum PairStatus
{
    DryRun,    // Match detected but dry run mode is on — no hard link created yet
    Pending,   // Hard link created, waiting for movie library scan to pick it up
    Active,    // Both items exist, watch sync enabled
    Error      // Something went wrong (filesystem error, item disappeared, etc.)
}
```

### Persistence: PairStore.cs

JSON file stored in the plugin data directory (`IApplicationPaths.PluginConfigurationsPath`). Simple `List<LinkedPair>` serialized with `System.Text.Json`. Operations:

- `GetAll()` / `GetByEpisodeId(Guid)` / `GetByMovieId(Guid)` / `GetByHardLinkPath(string)`
- `Upsert(LinkedPair)` / `Remove(Guid pairId)`
- `ExistsForEpisode(Guid episodeId) -> bool` — idempotency guard
- File locking via `SemaphoreSlim` for thread safety

---

## Detection Pipeline

### Trigger Points

1. **LibraryEventHandler** (`IHostedService`) — subscribes to `ILibraryManager.ItemAdded`. When a new Season 0 episode appears, queues it for detection.
2. **FullScanTask** (`IScheduledTask`) — runs every 24h (configurable). Scans all Season 0 episodes across all TV libraries, checks each against the pair store, runs detection on new ones.

### Detection Flow

```
Season 0 Episode Found
    │
    ├── Already in PairStore? → SKIP (idempotent)
    │
    ├── Resolve library mapping:
    │    ├── Find which Jellyfin library contains the episode
    │    ├── Look up LibraryMappings for matching SourceLibraryId
    │    └── No mapping found? → SKIP (library not configured)
    │
    ├── In ignore list (config)? → SKIP
    │
    ├── In force-link list (config)? → USE configured movie title/year → HARD LINK (or log if dry run)
    │
    └── Run AggregatedLookupService
         │
         ├── TmdbLookupService (Tier 1: IMDB ID cross-ref)
         │    ├── Episode has IMDB ID in ProviderIds?
         │    │    └── GET /find/{imdb_id}?external_source=imdb_id
         │    │         └── If movie_results[] is non-empty → MATCH (title, year, tmdb_movie_id)
         │    │
         │    └── Episode has TMDB ID?
         │         └── GET /tv/{series_id}/season/0/episode/{ep_num}/external_ids
         │              └── Extract imdb_id → retry /find endpoint
         │
         ├── TvdbLookupService (Tier 2: TVDB cross-ref)
         │    ├── Episode has TVDB ID in ProviderIds?
         │    │    └── GET /episodes/{id}/extended → check "linkedMovie" field
         │    │         └── If linkedMovie exists → GET /movies/{id} → MATCH
         │    │
         │    └── Fallback: search by series + episode name
         │         └── GET /search?query={title}&type=movie
         │              └── Fuzzy match on name + year → MATCH
         │
         └── Merge Results
              ├── Both agree? → High confidence
              ├── Only one matched? → Lower confidence (still proceeds)
              └── Neither matched? → No link created
              │
              ▼ (match found)
         Movie already exists in destination library? (match by TMDB/TVDB/IMDB ID)
              ├── Yes → Skip hard link creation. Pair the existing movie item
              │         with the episode directly (Status=Active). Watch sync begins
              │         immediately. Log: "Existing movie found — paired without hard link"
              │
              └── No → Continue to hard link creation:
                   │
                   Dry Run Mode?
                        ├── Yes → Log: "DRY RUN: Would create hard link for '{EpisodeName}' →
                        │         '{MovieTitle} ({Year})' in {DestinationLibrary}"
                        │         Store pair with Status=DryRun (visible in config UI)
                        │         NO filesystem changes, NO watch sync
                        │
                        └── No → Create hard link, write NFO metadata, store pair as Pending
```

### TMDB Lookup Details

**Primary endpoint**: `GET https://api.themoviedb.org/3/find/{imdb_id}?external_source=imdb_id`
- Returns `{ movie_results: [...], tv_episode_results: [...] }`
- If `movie_results` is non-empty, the IMDB ID belongs to both a movie and an episode
- Extract: `movie_results[0].id`, `movie_results[0].title`, `movie_results[0].release_date`

**Fallback for episodes without IMDB ID**: `GET /tv/{series_tmdb_id}/season/0/episode/{episode_number}/external_ids`
- Returns `{ imdb_id, tvdb_id, ... }`
- Use the retrieved `imdb_id` with the `/find` endpoint above

**Rate limit**: TMDB allows ~40 req/sec. Plugin uses a `SemaphoreSlim(30)` + 1-second sliding window. The FullScanTask processes episodes sequentially with a 100ms delay between lookups.

### TVDB Lookup Details

**TVDB API v4** (requires API key from thetvdb.com):

**Primary endpoint**: `GET https://api4.thetvdb.com/v4/episodes/{tvdb_id}/extended`
- Response includes a `linkedMovie` field when the episode has a corresponding movie entry
- If present: `GET /v4/movies/{linkedMovieId}` → extract title, year, IMDB ID

**Fallback search**: `GET https://api4.thetvdb.com/v4/search?query={episode_name}&type=movie`
- Fuzzy match episode name against movie results
- Filter by year proximity (±1 year) to avoid false positives
- Require Levenshtein distance ≤ 3 or substring containment for title match

**Authentication**: Bearer token from `POST /v4/login` with API key. Token cached for 24h.

**Rate limit**: TVDB allows ~100 req/min. Plugin uses `SemaphoreSlim(5)` for concurrent request limiting.

### Aggregation Strategy (AggregatedLookupService)

```csharp
public async Task<MovieMatch?> LookupAsync(Episode episode)
{
    var tmdbTask = _tmdbService.LookupAsync(episode);
    var tvdbTask = _tvdbService.LookupAsync(episode);

    await Task.WhenAll(tmdbTask, tvdbTask);

    var tmdb = tmdbTask.Result;
    var tvdb = tvdbTask.Result;

    // Both matched — prefer TMDB for movie metadata (better poster/overview data)
    if (tmdb != null && tvdb != null)
        return tmdb with { TvdbMovieId = tvdb.TvdbMovieId };

    // One matched
    return tmdb ?? tvdb;
}
```

Both lookups run in parallel. IMDB ID is the shared key — if both services return an IMDB ID and they match, confidence is highest. If only one returns a result, it's still used.

---

## Hard Link Management

### HardLinkService.cs

```csharp
public interface IHardLinkService
{
    bool Create(string sourcePath, string linkPath);
    bool Delete(string linkPath);
    bool Exists(string linkPath);
    bool ValidateSameFilesystem(string path1, string path2);
}
```

**Cross-platform P/Invoke:**

- **Windows**: `Kernel32.dll` → `CreateHardLink(lpFileName, lpExistingFileName, IntPtr.Zero)`
- **Linux**: `libc` → `link(oldpath, newpath)`
- Detection via `RuntimeInformation.IsOSPlatform()`

**Movie folder naming**: `{MovieTitle} ({Year}) [JellyfinPlugin-SpecialToMovie]/{MovieTitle} ({Year}){extension}`
- The `[JellyfinPlugin-SpecialToMovie]` tag marks plugin-managed folders — makes them easy to identify, filter, and clean up
- Title sanitized: strip `< > : " / \ | ? *` and leading/trailing dots/spaces
- Example: `El Camino A Breaking Bad Movie (2019) [JellyfinPlugin-SpecialToMovie]/El Camino A Breaking Bad Movie (2019).mkv`
- CleanupTask uses the `[JellyfinPlugin-SpecialToMovie]` tag to find orphaned folders on disk that are no longer in the PairStore

**Same-filesystem validation**:
- Windows: compare `Path.GetPathRoot()` for both paths
- Linux: `stat()` both paths, compare `st_dev` (device ID)
- If different filesystems → log warning, skip this pair, set status to Error

### NFO Metadata for Hard-Linked Movies

When creating a hard link, the plugin also writes a `.nfo` file alongside it so Jellyfin can
reliably identify the movie using provider IDs rather than relying solely on folder name + year.

**File**: `{MovieTitle} ({Year}) [JellyfinPlugin-SpecialToMovie]/{MovieTitle} ({Year}).nfo`

**Content** (Kodi-compatible XML that Jellyfin's NFO parser reads):

```xml
<?xml version="1.0" encoding="utf-8" standalone="yes"?>
<movie>
  <title>El Camino: A Breaking Bad Movie</title>
  <year>2019</year>
  <uniqueid type="imdb" default="true">tt9243946</uniqueid>
  <uniqueid type="tmdb">559969</uniqueid>
  <uniqueid type="tvdb">14541</uniqueid>
</movie>
```

- Only includes IDs that were found during the TMDB/TVDB lookup (omits missing ones)
- The `<uniqueid>` tags map directly to Jellyfin's `ProviderIds` dictionary
- This ensures Jellyfin fetches the correct movie metadata (poster, overview, cast) on first scan
- Without the NFO, Jellyfin would guess based on folder name alone — prone to misidentification for titles with common words or remakes

**NFO is only written if the destination library has NFO saving enabled** in its Jellyfin library
settings (the "Nfo" metadata saver). The plugin checks this at runtime via
`ILibraryManager.GetVirtualFolders()` → inspect the library's `LibraryOptions.MetadataSavers`
for the presence of `"Nfo"`. If the library doesn't use NFO files, the plugin skips writing one
and relies on Jellyfin's online metadata providers to identify the movie from the folder name —
the `[JellyfinPlugin-SpecialToMovie]` tag is ignored by metadata matchers so it won't interfere.

**Cleanup**: See Hard Link Folder Cleanup section below — the entire `[JellyfinPlugin-SpecialToMovie]` folder is deleted, not just individual files.

### Existing Movie Detection (Skip Hard Link)

Before creating a hard link, the plugin checks whether the destination library already contains
a movie matching the detected TMDB, TVDB, or IMDB ID:

```csharp
var existingMovie = _libraryManager.GetItemList(new InternalItemsQuery
{
    IncludeItemTypes = new[] { BaseItemKind.Movie },
    ParentId = destinationLibraryId,
    Recursive = true
}).FirstOrDefault(m =>
    m.GetProviderId(MetadataProvider.Tmdb) == match.TmdbMovieId ||
    m.GetProviderId(MetadataProvider.Tvdb) == match.TvdbMovieId ||
    m.GetProviderId(MetadataProvider.Imdb) == match.ImdbId);
```

If found, the plugin skips hard link creation entirely and pairs the existing movie with the
episode directly (Status = `Active`). This handles the case where a user already has the movie
in their library through any means (manual add, separate download, existing hard link, etc.).
Watch sync begins immediately for these pairs.

---

## Watch Status Sync

### WatchSyncService.cs — IHostedService

Subscribes to `IUserDataManager.UserDataSaved` event on startup.

**Event handler flow:**

```
UserDataSaved fires (userId, item, userData)
    │
    ├── Is item.Id in PairStore (as episode or movie)?
    │    └── No → return
    │
    ├── Get paired item ID from PairStore
    │
    ├── Is "{userId}:{pairedItemId}" in _reentrancyGuard HashSet?
    │    └── Yes → return (we triggered this ourselves)
    │
    ├── Add "{userId}:{pairedItemId}" to _reentrancyGuard
    │
    ├── Copy UserData to paired item:
    │    ├── Played (bool)
    │    ├── PlaybackPositionTicks (long)
    │    ├── PlayCount (int)
    │    ├── LastPlayedDate (DateTime?)
    │    └── IsFavorite (bool)
    │
    ├── Save via IUserDataManager.SaveUserData()
    │
    └── finally: Remove "{userId}:{pairedItemId}" from _reentrancyGuard
```

**Reentrancy prevention**: When we save user data on the paired item, Jellyfin fires another `UserDataSaved` event. The `HashSet<string>` guard breaks the infinite loop. Thread-safe via `ConcurrentHashSet` or `lock`.

**All users synced**: The event fires per-user. Each user's watch state is synced independently — User A marking it watched doesn't affect User B.

---

## Deletion Handling

### LibraryEventHandler — ItemRemoved subscription

```
ItemRemoved fires (item)
    │
    ├── Dry run mode? → Log what would happen, return
    │
    ├── Is item an Episode with a pair in PairStore?
    │    └── Yes:
    │         ├── AutoDeleteOnRemoval enabled?
    │         │    ├── Yes → Delete entire [JellyfinPlugin-SpecialToMovie] folder
    │         │    └── No  → Log: "Orphaned folder left on disk (auto-delete disabled)"
    │         ├── Remove pair from PairStore
    │         └── (Movie item will be cleaned up by next movie library scan
    │              if folder was deleted)
    │
    ├── Is item a Movie with a pair in PairStore?
    │    └── Yes (and IsExistingMovie == false):
    │         ├── AutoDeleteOnRemoval enabled?
    │         │    ├── Yes → Delete entire [JellyfinPlugin-SpecialToMovie] folder
    │         │    │    └── TwoWayDeletion enabled?
    │         │    │         ├── Yes → Also delete the original episode file
    │         │    │         └── No  → Episode file untouched
    │         │    └── No  → Log: "Orphaned folder left on disk (auto-delete disabled)"
    │         ├── Remove pair from PairStore
    │    └── Yes (and IsExistingMovie == true):
    │         ├── TwoWayDeletion enabled AND AutoDeleteOnRemoval enabled?
    │         │    ├── Yes → Delete the original episode file
    │         │    └── No  → Episode file untouched
    │         ├── Remove pair from PairStore only
    │
    └── Neither → return
```

### Hard Link Folder Cleanup

When removing a hard link (via deletion event, "Remove All Hard Links", or cleanup task),
the plugin **deletes the entire `[JellyfinPlugin-SpecialToMovie]` folder**, not individual files.

Jellyfin and its metadata providers accumulate additional files in movie folders over time:
`.nfo` metadata, trickplay images, subtitle files, backdrop images, `poster.jpg`, etc.
Deleting only the hard link file would leave all of this behind as orphaned data.

**Deletion logic:**

1. Resolve the parent folder of the `HardLinkPath`
2. Safety check: folder name must contain `[JellyfinPlugin-SpecialToMovie]` — refuse to delete otherwise
3. `Directory.Delete(folderPath, recursive: true)` — removes the folder and everything inside it
4. If the folder doesn't exist, log and continue (already cleaned up)

**What this catches:** `.nfo`, trickplay directories, subtitles (`.srt`, `.ass`), images
(`poster.jpg`, `fanart.jpg`, `backdrop.jpg`), `.xml` metadata, and any other files Jellyfin
or its plugins may have created inside the movie folder.

**What this never touches:** The original episode file and its parent folder in the TV library.
Only the plugin-created `[JellyfinPlugin-SpecialToMovie]` folder in the destination library is removed.

### CleanupTask — IScheduledTask (every 12 hours)

Validates all pairs in the store:

1. **Episode gone**: Episode item no longer in library → delete hard link, remove pair
2. **Hard link gone**: File at `HardLinkPath` missing → recreate it (episode still exists)
3. **Movie gone but hard link exists**: Movie scanner hasn't picked it up yet or user deleted from UI → if hard link exists and episode exists, trigger a movie library scan; if hard link is gone, remove pair
4. **Pending pairs**: `MovieItemId` is null → check if a Movie item now exists at `HardLinkPath` → if so, set `MovieItemId`, transition to Active
5. **Stale errors**: Retry Error pairs if the underlying condition may have resolved

---

## Configuration

### PluginConfiguration.cs

```csharp
public class PluginConfiguration : BasePluginConfiguration
{
    /// TMDB API key (v3). Free at https://www.themoviedb.org/settings/api
    public string TmdbApiKey { get; set; } = "";

    /// TVDB API key (v4). Free at https://thetvdb.com/api-information
    public string TvdbApiKey { get; set; } = "";

    /// Dry run mode — logs all hard links that WOULD be created without actually
    /// creating them. Enabled by default so the user can review the plugin's decisions
    /// before committing. Must be manually disabled by the user to activate real linking.
    public bool DryRunMode { get; set; } = true;

    /// Enable automatic detection on library scan events
    public bool AutoDetectEnabled { get; set; } = true;

    /// Minimum confidence: require both TMDB and TVDB to agree before creating a link
    public bool RequireDualConfirmation { get; set; } = false;

    /// Library routing rules — maps source TV libraries to destination movie libraries.
    /// Each rule specifies a TV library (source of Season 0 episodes) and a movie library
    /// (destination for hard links). A "mixed" rule targets the same library for both.
    public List<LibraryMapping> LibraryMappings { get; set; } = new();

    /// Manual force-link overrides. Key = "SeriesName S00E##", Value = "Movie Title (Year)"
    public Dictionary<string, string> ForceLinks { get; set; } = new();

    /// Episodes to never link, even if detected as movies.
    /// Format: "SeriesName S00E##" or Jellyfin item ID
    public List<string> IgnoreList { get; set; } = new();

    /// Create a subfolder per movie (recommended). If false, places files directly in target library root.
    public bool CreateMovieSubfolders { get; set; } = true;

    /// Full scan interval in hours (default 24)
    public int FullScanIntervalHours { get; set; } = 24;

    /// Cleanup task interval in hours (default 12)
    public int CleanupIntervalHours { get; set; } = 12;

    /// When enabled, automatically delete the [JellyfinPlugin-SpecialToMovie] folder
    /// when either the episode or movie in a pair is removed from Jellyfin.
    /// When disabled, orphaned hard link folders remain on disk until manually cleaned
    /// up via "Remove All Hard Links" or the user enables this setting.
    /// Disabled by default — users must opt in to automatic filesystem deletion.
    public bool AutoDeleteOnRemoval { get; set; } = false;

    /// When enabled (requires AutoDeleteOnRemoval), deleting the movie also deletes
    /// the original TV episode file. When disabled, only deleting the TV episode
    /// removes the linked movie — deleting the movie leaves the episode untouched.
    /// Disabled by default. UI tooltip: "Deleting either item removes both.
    /// Without this, only removing the TV episode removes the linked movie."
    public bool TwoWayDeletion { get; set; } = false;
}
```

### Clean Uninstall

The config page includes a **"Remove All Hard Links"** button with a confirmation dialog:

> "This will delete all hard links created by this plugin and clear the pair database.
> Your original TV episode files will NOT be affected. The linked movie entries will
> disappear from your movie libraries on the next library scan. This cannot be undone."

**Flow:**

1. User clicks "Remove All Hard Links" → confirmation dialog
2. On confirm, plugin iterates all pairs in PairStore with a `HardLinkPath`
3. For each pair: delete the hard link file from disk
4. If `CreateMovieSubfolders` was used: delete the empty `[JellyfinPlugin-SpecialToMovie]` parent folder
5. Transition affected pairs from `Active`/`Pending` back to `DryRun` status (hard link gone, match data preserved)
6. Log summary: "Removed {N} hard links. Original episode files are untouched. Pairs preserved in database."

**The database is NOT cleared.** All match data (TMDB/TVDB IDs, movie titles, library mappings) is
retained. The next FullScanTask run will see the existing pairs in `DryRun` status and recreate the
hard links automatically — so if the user changes their mind, they just run the scan again.

To truly start fresh, the user can manually clear the database from the config page ("Clear Database"
as a separate action) or simply uninstall the plugin.

**Safety:**

- Only deletes files at paths tracked in the PairStore — never walks the filesystem blindly
- As a secondary safety check, only deletes files whose parent folder contains `[JellyfinPlugin-SpecialToMovie]`
- Original episode files are never touched (hard link deletion only removes the directory entry, not the underlying data)
- Dry run mode disables this button. The UI shows it greyed out with an explanation: "Dry run mode is a filesystem safety lock — disable it above before removing hard links." This is intentional: dry run is a safety switch that prevents ALL filesystem modifications, including manual cleanup

This is also exposed as a scheduled task (`RemoveAllLinksTask`) that can be triggered via the Jellyfin admin dashboard scheduled tasks page, not just the plugin config UI.

/// Maps a source TV library to a destination movie library for hard link placement.
/// Set source and destination to the same library for mixed-library support.
public class LibraryMapping
{
    /// Jellyfin library ID of the source library (scanned for Season 0 episodes)
    public Guid SourceLibraryId { get; set; }

    /// Jellyfin library ID of the destination library (receives hard links).
    /// Can be the same as SourceLibraryId for mixed libraries.
    public Guid DestinationLibraryId { get; set; }

    /// Filesystem path where hard links are created. Resolved from the destination
    /// library's root folder at runtime.
    public string DestinationPath { get; set; } = "";

    /// Enable/disable this mapping without deleting it
    public bool Enabled { get; set; } = true;
}
```

### Library Mapping: How It Works

The user creates a list of mappings, each routing Season 0 episodes from a source library
to a destination library. Source and destination can be the same library (mixed content).

**Examples:**

- TV Shows → Movies
- Anime Shows → Anime Movies
- Mixed Library → Mixed Library (same ID for both)

**Resolution** when a Season 0 episode is detected:

1. Find which Jellyfin library contains the episode (via `ILibraryManager`)
2. Look up `LibraryMappings` for an enabled mapping where `SourceLibraryId` matches
3. No mapping → skip (library not configured)
4. Mapping found → create hard link in `DestinationPath` with `[JellyfinPlugin-SpecialToMovie]` folder naming

**Config UI** auto-populates library dropdowns from `ILibraryManager.GetVirtualFolders()`.
The `DestinationPath` is resolved from the destination library's root folder — no manual path entry.

### Dry Run Mode

**Enabled by default.** When active, the plugin performs **zero filesystem operations** — no hard link
creation, no hard link deletion, no file cleanup. The plugin is read-only to the disk. Specifically:

- Detection runs normally (TMDB/TVDB lookups) but matches are stored as `DryRun` pairs only
- Watch sync is disabled (no UserData writes)
- CleanupTask skips all filesystem operations (no orphan deletion, no hard link recreation)
- Deletion events (`ItemRemoved`) log what would happen but do NOT delete hard links or remove pairs from the store
- Existing pairs in the DB (from a previous non-dry-run session) are preserved — never deleted or modified while dry run is on

**What it does:**

1. Logs every action at INFO level: `"DRY RUN: Would create/delete/sync ..."`
2. Stores new matches as `DryRun` pairs in the PairStore — visible in the config page's pair status table
3. Config page shows a banner: "Dry run mode is ON — the plugin will not modify your filesystem. Review the matches below, then disable to activate."

**When the user disables dry run mode**, the next FullScanTask (or a manual "Run Now" click) picks up
all `DryRun` pairs and promotes them: creates the hard link, transitions status to `Pending`, and
normal processing continues. This avoids re-running TMDB/TVDB lookups for already-detected matches.

### configPage.html

Standard Jellyfin plugin config page with:

- **Library Mappings section**: Table with dropdown selectors for source/destination libraries (auto-populated from Jellyfin's library list). Add/remove rows. User can select the same library for both source and destination.
- Text fields for TmdbApiKey, TvdbApiKey
- Checkbox for AutoDetectEnabled, RequireDualConfirmation, CreateMovieSubfolders
- **Auto-delete on removal** checkbox (disabled by default). Label: "Remove linked files when an item is deleted." Description: "When the TV episode is removed, its linked movie folder is also deleted."
- **Two-way deletion** checkbox (disabled by default, greyed out unless auto-delete is enabled). Label: "Two-way deletion." Description: "Deleting either item removes both. Without this, only removing the TV episode removes the linked movie."
- Number inputs for scan intervals
- Table editor for ForceLinks (add/remove rows)
- Table editor for IgnoreList
- "Test Connection" buttons for TMDB and TVDB API keys
- "Run Full Scan Now" button that triggers the FullScanTask
- Read-only table showing current linked pairs and their status

---

## Service Registration

### PluginServiceRegistrator.cs

```csharp
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost appHost)
    {
        // Data
        services.AddSingleton<IPairStore, PairStore>();

        // Lookup
        services.AddSingleton<TmdbLookupService>();
        services.AddSingleton<TvdbLookupService>();
        services.AddSingleton<AggregatedLookupService>();

        // Filesystem
        services.AddSingleton<IHardLinkService, HardLinkService>();

        // Core services
        services.AddSingleton<SpecialDetectionService>();
        services.AddHostedService<WatchSyncService>();
        services.AddHostedService<LibraryEventHandler>();
    }
}
```

Scheduled tasks (`FullScanTask`, `CleanupTask`) are auto-discovered by Jellyfin via `IScheduledTask` interface — no explicit registration needed.

---

## Event Flow: End-to-End Scenarios

### Scenario 1: New TV Show Added with a Movie Special

```
Config: LibraryMappings = [{ Source: "TV Shows", Destination: "Movies" }]

1. User adds "Breaking Bad" to "TV Shows" library
2. Library scan runs → Season 0 episodes indexed
3. ItemAdded fires for each S00 episode
4. LibraryEventHandler picks up S00E63 ("El Camino")
5. Episode is in "TV Shows" library → mapping found → destination is "Movies" library
6. PairStore.ExistsForEpisode() → false (new)
7. AggregatedLookupService.LookupAsync():
   a. TMDB: Episode has IMDB ID "tt9243946"
      → GET /find/tt9243946 → movie_results has "El Camino" (TMDB ID 559969)
      → MATCH
   b. TVDB: Episode has TVDB ID 7628839
      → GET /episodes/7628839/extended → linkedMovie exists
      → GET /movies/{id} → "El Camino" confirmed
      → MATCH
   c. Both agree → high confidence
8. HardLinkService.Create(
      source: /tv/Breaking Bad/Season 0/S00E63 - El Camino.mkv
      link:   /movies/El Camino A Breaking Bad Movie (2019) [JellyfinPlugin-SpecialToMovie]/
                      El Camino A Breaking Bad Movie (2019).mkv
   )
9. PairStore.Upsert(pair with Status=Pending)
10. Next "Movies" library scan picks up the [JellyfinPlugin-SpecialToMovie] folder
11. ItemAdded fires for Movie "El Camino"
12. LibraryEventHandler sees new Movie at tracked HardLinkPath
13. PairStore update: MovieItemId set, Status → Active
14. Watch sync now operational for this pair
```

### Scenario 2: User Watches the Movie

```
1. User plays "El Camino" in Movies library, finishes it
2. UserDataSaved fires (userId, movieItem, { Played=true, PlayCount=1 })
3. WatchSyncService handler:
   a. PairStore.GetByMovieId(movieItem.Id) → pair found
   b. Reentrancy check → not in guard set
   c. Add guard key
   d. Copy UserData to episode item
   e. SaveUserData on episode
   f. (Jellyfin fires UserDataSaved for episode → handler sees guard key → returns)
   g. Remove guard key
4. TV library now shows S00E63 as watched
```

### Scenario 3: User Deletes Movie from UI

```
1. User deletes "El Camino" from Movies library
2. ItemRemoved fires for Movie item
3. LibraryEventHandler:
   a. PairStore.GetByMovieId(movieId) → pair found
   b. Delete hard link file from disk
   c. Remove pair from PairStore
4. Original S00E63 episode file untouched in TV library
5. Episode retains its independent watch status
```

---

## Error Handling

| Scenario | Handling |
|---|---|
| TMDB rate limit (429) | Exponential backoff: 1s → 2s → 4s → 8s, max 3 retries |
| TVDB rate limit | Same backoff strategy |
| TVDB auth token expired | Auto-refresh via `/login` endpoint, cache for 23h |
| Hard link fails (cross-filesystem) | Log warning, set pair Status=Error with message, skip |
| Hard link fails (permissions) | Log error, set Status=Error, surface in config page |
| Episode has no TMDB/TVDB/IMDB IDs | Skip silently (common for unmatched specials) |
| No library mapping for episode's library | Skip silently — library not configured for linking |
| No library mappings configured at all | Log warning on startup, disable auto-detection |
| Movie scanner hasn't run yet | Pair stays Pending; CleanupTask resolves on next run |
| Race condition: ItemAdded during scan | PairStore.ExistsForEpisode() prevents duplicates |
| Plugin data file corrupted | Backup on every write; restore from backup on parse failure |

---

## Implementation Order

### Phase 1: Skeleton + Config (Day 1)
1. `Jellyfin.Plugin.SpecialToMovie.csproj` — target net8.0, reference Jellyfin.Controller + Jellyfin.Model
2. `Plugin.cs` — BasePlugin entry point with GUID
3. `build.yaml` — plugin metadata
4. `PluginConfiguration.cs` — all settings with defaults
5. `configPage.html` — basic config UI
6. `PluginServiceRegistrator.cs` — wire up DI

### Phase 2: Data Layer (Day 1)
7. `Models/LinkedPair.cs` + `PairStatus.cs`
8. `Data/PairStore.cs` — JSON persistence with file locking

### Phase 3: Hard Links (Day 2)
9. `HardLink/IHardLinkService.cs` + `HardLinkService.cs` — P/Invoke, same-fs validation, movie folder naming

### Phase 4: Metadata Lookup (Day 2-3)
10. `Lookup/IMetadataLookupService.cs` — shared interface
11. `Lookup/TmdbLookupService.cs` — /find endpoint + fallback via external_ids
12. `Lookup/TvdbLookupService.cs` — /episodes/extended + linkedMovie + search fallback
13. `Lookup/AggregatedLookupService.cs` — parallel execution, merge, dedup

### Phase 5: Core Services (Day 3-4)
14. `Services/SpecialDetectionService.cs` — orchestrate: query episodes → check store → lookup → hard link → persist
15. `Services/WatchSyncService.cs` — IHostedService, UserDataSaved subscription, reentrancy guard

### Phase 6: Event Handlers + Tasks (Day 4)
16. `EventHandlers/LibraryEventHandler.cs` — IHostedService, ItemAdded/ItemRemoved subscriptions
17. `Tasks/FullScanTask.cs` — periodic full-library detection
18. `Tasks/CleanupTask.cs` — pair validation and orphan cleanup

### Phase 7: Config UI Polish (Day 5)
19. Finish `configPage.html` — pair status table, test buttons, manual scan trigger

---

## Verification

1. **Unit tests**: Mock ILibraryManager, IUserDataManager, IHttpClientFactory — test detection logic, watch sync reentrancy, pair store CRUD
2. **Integration test**: Install plugin on a test Jellyfin instance with a TV show that has a known movie special (e.g., Breaking Bad + El Camino). Verify:
   - Episode detected and hard link created in movie library
   - Movie appears after library scan
   - Marking movie watched → episode shows watched
   - Marking episode watched → movie shows watched
   - Deleting movie → hard link removed, episode untouched
   - Deleting episode → hard link removed, movie disappears on next scan
3. **Cross-platform**: Test hard link creation on both Windows and Linux (Docker)
4. **Edge cases**: Episode with no external IDs, episode already manually linked, movie library on different filesystem
