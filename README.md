# Jellyfin Plugin: Special To Movie

Automatically detects TV specials (Season 0 episodes) that are also standalone movies, creates hard links in your movie library, and syncs watch status bidirectionally.

## About

Many TV specials are also standalone movies — *El Camino: A Breaking Bad Movie*, *Downton Abbey: A New Era*, *Serenity*, etc. Jellyfin users want these in both their TV and Movies libraries, but Jellyfin tracks watch status by internal item ID. Two separate library entries means two unrelated items with independent watch states.

Manual hard links solve the file deduplication problem, but **watch status still doesn't sync**. This plugin handles both — it creates the hard links and keeps watch state in sync bidirectionally.

## Features

- **Automatic detection** — identifies Season 0 episodes that are also movies using TMDB and TVDB cross-referencing
- **Hard links** — no disk space wasted, same file with two directory entries
- **Bidirectional watch sync** — mark the movie as watched and the episode updates too, and vice versa
- **Dry run mode** (on by default) — review what the plugin would do before enabling real linking
- **Existing movie detection** — if the movie already exists in your library, pairs it directly without creating a hard link
- **Library mapping** — route specials from specific TV libraries to specific movie libraries
- **NFO metadata** — writes Kodi-compatible NFO files so Jellyfin identifies linked movies correctly
- **Force links & ignore list** — manually override or exclude specific episodes
- **Safe deletion** (when enabled) — only removes files in plugin-managed `[JellyfinPlugin-SpecialToMovie]` folders
- **Automatic cleanup** — validates pairs periodically and fixes orphaned entries

## Requirements

- Jellyfin 10.11.0 or later
- .NET 9.0 runtime (included with Jellyfin 10.11+)
- API key for your primary metadata provider (TMDB by default; both recommended for best accuracy)
  - TMDB API key (free — [get one here](https://www.themoviedb.org/settings/api))
  - TVDB API key (free — [get one here](https://thetvdb.com/api-information))
- Source and destination libraries must be on the **same filesystem** (hard link requirement)

## Installation

### From Plugin Repository (Recommended)

1. Open Jellyfin Dashboard → **Plugins** → **Repositories**
2. Add a new repository with the URL:

   ```text
   https://raw.githubusercontent.com/AdamHarrison99/jellyfin-plugin-specialtomovie/master/manifest.json
   ```

3. Go to **Catalog**, find **SpecialToMovie**, and click **Install**
4. Restart Jellyfin

### Manual Installation

1. Download `Jellyfin.Plugin.SpecialToMovie.dll` from the [latest release](https://github.com/AdamHarrison99/jellyfin-plugin-specialtomovie/releases/latest)
2. Copy it to your Jellyfin plugins directory in a `SpecialToMovie` subfolder:
   - **Linux**: `/var/lib/jellyfin/plugins/SpecialToMovie/`
   - **Windows**: `C:\ProgramData\Jellyfin\Server\plugins\SpecialToMovie\`
   - **Docker**: Mount or copy into `/config/plugins/SpecialToMovie/` inside the container
3. Restart Jellyfin

### Build From Source

```bash
git clone https://github.com/AdamHarrison99/jellyfin-plugin-specialtomovie.git
cd jellyfin-plugin-specialtomovie
dotnet build -c Release
```

Copy `bin/Release/net9.0/Jellyfin.Plugin.SpecialToMovie.dll` to your plugins directory and restart Jellyfin.

## Configuration

After installation, go to **Dashboard → Plugins → SpecialToMovie**.

### 1. Choose Primary Provider & Enter API Keys

Select your **Primary Metadata Provider** (default: TMDB). When both TMDB and TVDB match a special to different movies, the primary provider's result is used.

- **TMDB API Key** — required when TMDB is primary (default)
- **TVDB API Key** — required when TVDB is primary

The non-primary provider's key is optional but recommended — using both providers improves match accuracy. Use the **Test Connection** buttons to verify your keys work.

### 2. Set Up Library Mappings

Add at least one mapping to tell the plugin where to route detected movies:

| Source Library | Destination Library |
|---|---|
| TV Shows | Movies |
| Anime Shows | Anime Movies |

Set source and destination to the same library if you use mixed-content libraries.

### 3. Run a Full Scan

Click **Run Full Scan** on the plugin config page, or go to **Dashboard → Scheduled Tasks** and run **SpecialToMovie Full Scan** manually. This scans all your Season 0 episodes and looks up each one against TMDB/TVDB to find movie matches.

### 4. Review in Dry Run Mode

Dry run mode is **enabled by default**, so the scan will detect matches without creating any files. Go back to the plugin config page to review the detected pairs in the **Linked Pairs** table.

**No files are created or modified** while dry run is active. Use the ignore list, force links, and remove button to adjust any incorrect matches before activating.

### 5. Activate

Once you're satisfied with the detected matches, **uncheck "Dry run mode"** and save. The next scan will create hard links for all detected pairs and activate watch sync.

## Deletion Behavior

| Setting | What Happens |
|---|---|
| Default (auto-delete off) | Removing an episode or movie removes the pair from the database. Hard link folders stay on disk until manually cleaned up. |
| Auto-delete on | Removing an episode deletes its linked movie folder. Removing a movie deletes its linked folder (episode untouched). |
| Auto-delete + two-way | Removing either item deletes both the hard link folder and the original episode file. |

The **Remove All Hard Links** button in the config page deletes all plugin-created folders. Pre-existing movie links and original episode files are never touched by this action.

## Scheduled Tasks

The plugin registers two scheduled tasks in Jellyfin's **Scheduled Tasks** dashboard. These act as a safety net — during normal operation with auto-detect enabled, the event-driven handlers cover most cases in real time.

### Full Scan (defaults to daily at midnight)

Scans all Season 0 episodes across your configured library mappings and looks up each against TMDB/TVDB. This catches anything the real-time event handler missed:

- Episodes that existed before the plugin was installed
- Episodes added while the plugin was stopped or Jellyfin was restarting
- Previously failed lookups that may now succeed (new data on TMDB/TVDB)
- DryRun pairs that need promotion after dry run mode is disabled

Also re-syncs watch state for all active pairs, catching any drift that occurred while the plugin was inactive.

### Cleanup (defaults to every 12 hours)

Validates all existing pairs and repairs inconsistencies:

- Removes pairs whose source episode no longer exists in Jellyfin
- Recreates hard links that were manually deleted from disk
- Promotes Pending pairs to Active once Jellyfin finishes scanning the movie
- Retries Error pairs if the underlying issue has been resolved

Both tasks can be run manually from the Jellyfin Scheduled Tasks dashboard, and their intervals can be adjusted there.

## How It Works

```
Season 0 Episode Found
    ↓
Already paired? → Skip
    ↓
Library mapping exists? → No → Skip
    ↓
In cache? → Yes → Skip (default 14 days, configurable)
    ↓
TMDB + TVDB lookup (parallel)
    ↓
Both match? → Primary provider wins (configurable, default TMDB)
    ↓
Match found? → No → Cache 404 → Skip
    ↓
Movie already in destination library?
    ├── Yes → Pair directly (no hard link needed)
    └── No  → Create hard link + NFO file
              → Pair activates after next library scan
    ↓
Watch sync active — mark one as watched, the other updates
```

## Known Limitations

### Duplicate entries in Next Up / Continue Watching

Because both the episode and its linked movie are fully independent Jellyfin items with synced playback progress, both may appear in the **Next Up** or **Continue Watching** sections simultaneously. Jellyfin's plugin API does not currently provide a way to filter or modify these built-in API responses, so the plugin cannot suppress the duplicate entry.

See [#1](https://github.com/AdamHarrison99/jellyfin-plugin-specialtomovie/issues/1) for details and potential future approaches.

## FAQ

**Q: Does this use extra disk space?**
No. Hard links point to the same data on disk. The movie entry is just another directory entry for the same file.

**Q: What if my movie and TV libraries are on different drives?**
Hard links only work within a single filesystem. The plugin will detect this and skip the pair with an error message.

**Q: Can I use this with Docker?**
Yes. Make sure your TV and movie library paths are on the same Docker volume/mount so they share a filesystem.

**Q: What happens if I uninstall the plugin?**
Hard links remain on disk (they're just regular files). Watch sync stops. You can use "Remove All Hard Links" before uninstalling for a clean removal.

**Q: Will this interfere with my existing metadata?**
No. The plugin creates movies in `[JellyfinPlugin-SpecialToMovie]`-tagged folders and writes NFO files with correct provider IDs. Your existing library entries are not modified.

---

*This project was built with AI code development tools ([Claude Code](https://www.anthropic.com/claude-code)).*
