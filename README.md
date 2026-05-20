# Jellyfin Plugin: SpecialToMovie

Automatically detects TV specials (Season 0 episodes) that are also standalone movies, creates hard links in your movie library, and syncs watch status bidirectionally.

## The Problem

Many TV specials are also standalone movies — *El Camino: A Breaking Bad Movie*, *Downton Abbey: A New Era*, *Serenity*, etc. Jellyfin users want these in both their TV and Movies libraries, but Jellyfin tracks watch status by internal item ID. Two separate library entries means two unrelated items with independent watch states.

Manual hard links solve the file deduplication problem, but **watch status still doesn't sync**.

## What This Plugin Does

1. **Detects** Season 0 episodes that are also movies using TMDB and TVDB cross-referencing
2. **Creates hard links** in your movie library (no disk space wasted — same file, two directory entries)
3. **Syncs watch status** bidirectionally — mark the movie as watched and the episode updates too, and vice versa
4. **Cleans up** automatically when episodes or movies are removed

## Features

- **Dry run mode** (on by default) — review what the plugin would do before enabling real linking
- **Dual metadata lookup** — cross-references both TMDB and TVDB for high-confidence matching
- **Library mapping** — route specials from specific TV libraries to specific movie libraries
- **Existing movie detection** — if the movie already exists in your library, pairs it directly without creating a hard link
- **NFO metadata** — writes Kodi-compatible NFO files so Jellyfin identifies linked movies correctly
- **Force links & ignore list** — manually override or exclude specific episodes
- **Automatic cleanup** — validates pairs periodically and fixes orphaned entries
- **Safe deletion** — only removes files in plugin-managed `[JellyfinPlugin-SpecialToMovie]` folders

## Requirements

- Jellyfin 10.11.0 or later
- .NET 9.0 runtime (included with Jellyfin 10.11+)
- TMDB API key (free — [get one here](https://www.themoviedb.org/settings/api))
- TVDB API key (free — [get one here](https://thetvdb.com/api-information)) — optional but recommended
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

### 1. Enter API Keys

- **TMDB API Key** — required for movie detection
- **TVDB API Key** — optional but improves match accuracy

Use the **Test Connection** buttons to verify your keys work.

### 2. Set Up Library Mappings

Add at least one mapping to tell the plugin where to route detected movies:

| Source Library | Destination Library |
|---|---|
| TV Shows | Movies |
| Anime Shows | Anime Movies |

Set source and destination to the same library if you use mixed-content libraries.

### 3. Review in Dry Run Mode

Dry run mode is **enabled by default**. The plugin will:
- Scan your Season 0 episodes
- Look up each one against TMDB/TVDB
- Log matches it finds
- Show detected pairs in the config page

**No files are created or modified** while dry run is active.

### 4. Activate

Once you're satisfied with the detected matches, **uncheck "Dry run mode"** and save. The next scan will create hard links for all detected pairs and activate watch sync.

## How It Works

```
Season 0 Episode Found
    ↓
Already paired? → Skip
    ↓
Library mapping exists? → No → Skip
    ↓
TMDB + TVDB lookup (parallel)
    ↓
Match found? → No → Skip
    ↓
Movie already in destination library?
    ├── Yes → Pair directly (no hard link needed)
    └── No  → Create hard link + NFO file
              → Pair activates after next library scan
    ↓
Watch sync active — mark one as watched, the other updates
```

## Deletion Behavior

| Setting | What Happens |
|---|---|
| Default (auto-delete off) | Removing an episode or movie removes the pair from the database. Hard link folders stay on disk until manually cleaned up. |
| Auto-delete on | Removing an episode deletes its linked movie folder. Removing a movie deletes its linked folder (episode untouched). |
| Auto-delete + two-way | Removing either item deletes both the hard link folder and the original episode file. |

The **Remove All Hard Links** button in the config page deletes all plugin-created folders and transitions pairs back to dry run status. Original episode files are never touched by this action.

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
