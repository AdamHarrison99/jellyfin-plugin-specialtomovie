# HANDOFF — SpecialToMovie Jellyfin Plugin

> If anything in this file doesn't match what you find in the actual code, fix it here rather than just noting the discrepancy.

Read this first in a new session — it's meant to be a comprehensive, self-contained map of the codebase so you rarely need to open source files just to get oriented. Still, this is a snapshot; if something here looks like it might have changed, verify against the actual `.cs` file before relying on it for anything destructive.

## What this is

A Jellyfin plugin (.NET 10, C#) that detects TV specials (Season 0 episodes) that are also standalone movies (e.g. *El Camino: A Breaking Bad Movie*, *Downton Abbey: A New Era*), hard-links them into a movie library, and syncs watch status bidirectionally — because Jellyfin treats the two library entries as unrelated items. Current version: **1.0.16.0** (`Jellyfin.Plugin.SpecialToMovie.csproj` `AssemblyVersion`/`FileVersion`, `build.yaml`'s `version`, and `manifest.json`'s top `versions[0]` entry — check all three before assuming, they must move together on release).

**Targets Jellyfin 12 / .NET 10 as of v1.0.16.0.** The 1.0.16.0 release was a pure framework migration — `net9.0` -> `net10.0` and `Jellyfin.Controller`/`Jellyfin.Model` `10.*` -> `12.0.0-rc4`, with **zero `.cs`/`.html` changes**. Jellyfin 10.11 users are frozen at 1.0.15.0 by design: the manifest keeps every old entry, and Jellyfin's installer only offers versions whose `targetAbi` <= the server version, so 10.11 servers still see 1.0.15.0 and 12 servers see 1.0.16.0.

User-facing feature list, install steps, config walkthrough, deletion behavior table, and FAQ live in [`README.md`](../README.md) — this doc focuses on internals instead of duplicating that.

## Directory reality check

- This file lives in `agentic/`, one level below the repository root. Source paths quoted throughout this doc are relative to the **repo root**, so from here they resolve as `../Services/...`, `../Data/...`, and so on.
- Run git commands from the repository root — the parent of `agentic/`. Full structure diagram and the mandatory release process (version bump → build → zip → checksum → manifest.json entry → commit/push → `gh release create`) are in [`CLAUDE.md`](CLAUDE.md) (auto-loaded into every session's context already — don't duplicate it here, just follow it).
- If the working copy sits on a network share or an external volume, git may error "detected dubious ownership". The fix is `git config --global --add safe.directory '<path>'`, but that's a **global** config change, so ask the user before running it rather than doing it silently.
- Sibling docs in `agentic/`: [`AUDIT.md`](AUDIT.md) (security/efficiency audit history — read before doing a pre-release audit so you don't re-flag accepted risks), [`IDEAS.md`](IDEAS.md) (prioritized feature backlog with effort estimates and file touch-lists), [`JellyfinPlugin-SpecialToMovie plan.md`](JellyfinPlugin-SpecialToMovie%20plan.md) (**original pre-implementation design doc — has drifted from reality**, see Gotchas below).

## Architecture at a glance

```
LibraryEventHandler (ItemAdded/ItemRemoved, IHostedService)  ─┐
FullScanTask (IScheduledTask, daily @ midnight)               ├─→ SpecialDetectionService ─→ AggregatedLookupService (TMDB + TVDB, parallel)
CleanupTask (IScheduledTask, every CleanupIntervalHours)      ─┘                            ─→ HardLinkService (link + NFO + subtitles)
                                                                                             ─→ PairStore (JSON, atomic writes + backup)
WatchSyncService (IHostedService, subscribes IUserDataManager.UserDataSaved)
    → mirrors Played / PlayCount / PlaybackPositionTicks / LastPlayedDate / IsFavorite between paired items, per-user

SpecialToMovieController (REST, [Authorize(Policy = Policies.RequiresElevation)])
    → manual task triggers, pair CRUD, cache clear, API key connectivity tests
```

All services are DI-registered in `PluginServiceRegistrator.cs` (below) — read that file first if you want the whole service graph in one screen.

```csharp
services.AddSingleton<IPairStore, PairStore>();
services.AddSingleton<ApiResponseCache>();
services.AddSingleton<TmdbLookupService>();
services.AddSingleton<TvdbLookupService>();
services.AddSingleton<AggregatedLookupService>();
services.AddSingleton<IHardLinkService, HardLinkService>();
services.AddSingleton<WatchSyncService>();
services.AddHostedService(sp => sp.GetRequiredService<WatchSyncService>());
services.AddSingleton<SpecialDetectionService>();
services.AddHostedService<LibraryEventHandler>();
```

`FullScanTask` and `CleanupTask` implement `IScheduledTask` and are auto-discovered by Jellyfin — no explicit registration.

## File-by-file reference

| File | Purpose |
|---|---|
| `Plugin.cs` | `BasePlugin<PluginConfiguration>` entry point. `Plugin.Instance` is a static accessor used everywhere to reach live config — there's no config-change event, code just reads `Plugin.Instance?.Configuration` fresh each time. |
| `PluginServiceRegistrator.cs` | DI wiring — the fastest way to see every service and its lifetime. |
| `Configuration/PluginConfiguration.cs` | All settings (full field list below). |
| `Configuration/configPage.html` | Admin dashboard UI, embedded resource, served via `Plugin.GetPages()`. |
| `Models/LinkedPair.cs` | Core record: one episode↔movie pairing, plus `LinkedSubtitle` record. |
| `Models/PairStatus.cs` | Enum: `DryRun`, `Pending`, `Active`, `Error`. |
| `Models/MovieMatch.cs` | Lookup result record: `Title`, `Year`, `TmdbMovieId`, `TvdbMovieId`, `TvdbMovieSlug`, `ImdbId`. |
| `Data/PairStore.cs` | JSON persistence (`pairs.json` under `IApplicationPaths.PluginConfigurationsPath/SpecialToMovie/`). Full CRUD API below. |
| `Lookup/IMetadataLookupService.cs` | Shared interface: `Task<MovieMatch?> LookupAsync(Episode, CancellationToken)`. |
| `Lookup/TmdbLookupService.cs` | TMDB provider — details below. |
| `Lookup/TvdbLookupService.cs` | TVDB provider — details below. |
| `Lookup/AggregatedLookupService.cs` | Runs both in parallel, merges by `PrimaryProvider`. |
| `Lookup/ApiResponseCache.cs` | Raw-response cache, keyed by URL (API key stripped) — details below. |
| `Services/SpecialDetectionService.cs` | **Core orchestrator.** Largest file — read it directly for edge cases not covered here. |
| `Services/WatchSyncService.cs` | Bidirectional watch sync — details below. |
| `HardLink/IHardLinkService.cs` | Interface + `SubtitleSyncResult`/`SubtitleDeletion` types. |
| `HardLink/HardLinkService.cs` | Cross-platform P/Invoke hard links, path sanitization, NFO writing, subtitle linking/sync/deletion-verification. |
| `EventHandlers/LibraryEventHandler.cs` | `ItemAdded`/`ItemRemoved` subscriptions — real-time detection + pair activation/cleanup. |
| `Tasks/FullScanTask.cs` | Wraps `SpecialDetectionService.RunFullScanAsync`. Daily @ midnight by default. |
| `Tasks/CleanupTask.cs` | Validates all pairs, repairs inconsistencies, syncs subtitles. Every `CleanupIntervalHours` (default 6). |
| `Api/SpecialToMovieController.cs` | REST endpoints — full table below. |

## Data model

### `LinkedPair` (Models/LinkedPair.cs)

```
Id                 Guid            unique pair ID
EpisodeItemId      Guid            Jellyfin Episode item ID
MovieItemId        Guid?           Jellyfin Movie item ID — null until the library scanner indexes it (Pending) or an existing movie was matched directly
SourceLibraryId    Guid            TV library containing the episode
DestinationLibraryId Guid          movie library receiving the link
EpisodePath        string          original episode file path
HardLinkPath       string?         path of the plugin-created hard link (null if paired to a pre-existing movie)
IsExistingMovie    bool            true = paired to a movie that already existed in the library; the plugin created no file and will never delete one for this pair
MovieTitle         string
MovieYear          int?
TmdbMovieId        string?
TvdbMovieId        string?
TvdbMovieSlug      string?
ImdbId             string?
Status             PairStatus      DryRun | Pending | Active | Error
ErrorMessage       string?
LinkedSubtitles    List<LinkedSubtitle>
CreatedUtc / UpdatedUtc  DateTime  set by PairStore.Upsert
```

`LinkedSubtitle` (record): `EpisodeSidePath`, `MovieSidePath`, `ContentHash` (SHA-256 of the file at link time — used to confirm a surviving file after a one-sided deletion is still the exact linked content before letting Jellyfin delete the other side).

### `PairStatus` lifecycle

`DryRun` (match found, dry run mode on, zero filesystem changes) → `Pending` (hard link created on disk, waiting for the destination library's next scan to index it as a Movie item) → `Active` (both items exist in Jellyfin, watch sync live). `Error` is a side-branch (filesystem/cross-device failure) that `CleanupTask` retries automatically once the underlying condition might have changed.

### `PairStore` API (Data/PairStore.cs)

`GetAll()`, `GetById(Guid)`, `GetByEpisodeId(Guid)`, `GetByMovieId(Guid)`, `GetByHardLinkPath(string)`, `ExistsForEpisode(Guid)`, `Upsert(LinkedPair)`, `UpsertMany(IEnumerable<LinkedPair>)`, `Remove(Guid)`, `RemoveMany(IEnumerable<Guid>)`, `Clear() -> int`.

Persistence: single JSON file (`pairs.json`), `lock (_lock)` around all reads/writes (in-memory `List<LinkedPair>` is the source of truth, disk is a mirror). Every `Save()`: (1) copies current file to `pairs.backup.json` via temp+rename, (2) writes new content to `pairs.json.tmp`, then atomic `File.Move(overwrite: true)` to `pairs.json` — so a crash mid-write never corrupts the live file. On load, a `JsonException` triggers automatic restore from the backup file; if the backup is also corrupt, starts empty (logged as error, not a silent swallow).

## Configuration model (`PluginConfiguration.cs`)

| Field | Default | Notes |
|---|---|---|
| `PrimaryProvider` | `Tmdb` | `MetadataProviderType` enum (`Tmdb`=0, `Tvdb`=1). Wins when both providers match and disagree. |
| `TmdbApiKey` / `TvdbApiKey` | `""` | User-entered via config UI, never hardcoded. |
| `DryRunMode` | **`true`** | Global filesystem safety lock — see Dry Run section below. |
| `AutoDetectEnabled` | `true` | Gates `LibraryEventHandler.OnItemAdded` real-time detection. |
| `AllowOvaLinking` | `false` | TVDB-only: lets episodes tagged `OVA`/`OVAs` (not just `Movies`) be treated as movie matches. |
| `RequireDualConfirmation` | `false` | If true, a match is discarded unless **both** `TmdbMovieId` and `TvdbMovieId` are populated. |
| `LibraryMappings` | `[]` | `List<LibraryMapping>` — see below. Multiple mappings supported (not a single global path). |
| `ForceLinks` | `[]` | `List<ForceLinkEntry> { EpisodeKey, MovieTitle }` — see Force Link resolution below. |
| `IgnoreList` | `[]` | `List<string>` of `"SeriesName S00E##"` keys or Jellyfin item GUIDs. |
| `CleanupIntervalHours` | `6` | Drives `CleanupTask.GetDefaultTriggers()` (`Math.Max(1, …)`). |
| `AutoDeleteOnRemoval` | `false` | Opt-in: deleting one side of a pair deletes the plugin-owned folder on the other side. |
| `TwoWayDeletion` | `false` | Requires `AutoDeleteOnRemoval`. Deleting the *movie* also deletes the original *episode* file (deleting the episode always cascades to the movie regardless of this flag). |
| `WatchStatusOnly` | `false` | If true, `WatchSyncService` only mirrors `Played`/`PlayCount`/`IsFavorite` — skips `PlaybackPositionTicks`/`LastPlayedDate`. Works around duplicate Continue-Watching entries (Jellyfin can't dedupe two independent items). |
| `MetadataCacheDays` | `7` | TTL for `ApiResponseCache` entries. `<= 0` conceptually means "never expire" per the cache's own logic, though the UI likely never sets that. |

`LibraryMapping`: `SourceLibraryId` (Guid), `DestinationLibraryId` (Guid), `DestinationPath` (string, resolved from the destination library's root folder at runtime if empty), `Enabled` (bool). Source and destination can be the **same** library ID for mixed-content libraries.

`ForceLinkEntry`: `EpisodeKey` (string — `"SeriesName S00E##"` or a Jellyfin episode item GUID), `MovieTitle` (string — see resolution rules next).

### Force-link `MovieTitle` resolution (`SpecialDetectionService.ParseForcedMovie` + caller logic)

Tried in this order:
1. **Jellyfin item GUID** — parses as `Guid` → looks up that exact movie item directly via `ILibraryManager.GetItemById`, pairs immediately with `IsExistingMovie = true`, no lookup/hard-link involved.
2. **IMDB ID** — `tt` followed by digits.
3. **TMDB ID** — `tmdb:12345` prefix.
4. **TVDB ID** — `tvdb:12345` prefix.
5. **`"Title (Year)"`** free text — parsed by splitting on the last `(`...`)` pair; year must be numeric or it's treated as part of the title.

For cases 2–5, the plugin then checks whether a movie with those provider IDs (or, if no IDs, exact title+year) already exists in the destination library before falling back to creating a hard link.

## Detection pipeline (`SpecialDetectionService.ProcessEpisodeAsync`)

```
Season 0 episode seen (real-time ItemAdded, or FullScanTask/CleanupTask batch)
  │
  ├─ Already paired (PairStore.ExistsForEpisode)? → skip
  ├─ Resolve LibraryMapping by matching the episode's file path against each virtual
  │  folder's Locations, then finding an Enabled mapping for that library. No match → skip.
  ├─ Episode key ("SeriesName S00E##") or item GUID in IgnoreList? → skip
  ├─ Force link configured for this episode?
  │    └─ yes → resolve per rules above → pair directly or hard-link, done (bypasses lookup entirely)
  ├─ AggregatedLookupService.LookupAsync (TMDB + TVDB run in parallel via Task.WhenAll)
  │    └─ no match from either → skip
  ├─ RequireDualConfirmation on AND only one provider matched? → skip
  ├─ Movie already exists in destination library (matched by TMDB/TVDB/IMDB provider ID)?
  │    └─ yes → pair directly, Status=Active, IsExistingMovie=true, no hard link created,
  │             SyncInitialWatchState runs immediately
  ├─ DryRunMode?
  │    └─ yes → store pair with Status=DryRun, log "DRY RUN: would create...", zero FS writes
  └─ Create hard link:
       ├─ ValidateSameFilesystem(episode.Path, destinationPath) — different device → Status=Error, skip
       ├─ HardLinkService.Create (P/Invoke)  — fails → Status=Error, skip
       ├─ WriteNfoFile if destination library has the "Nfo" metadata saver enabled
       ├─ LinkSubtitles (hard-links matching subtitle files alongside)
       └─ store pair, Status=Pending (MovieItemId still null until the library scans it in)
```

`RunFullScanAsync` (the batch entry point used by `FullScanTask`) additionally: snapshots mutable config lists once at scan start (`ConfigSnapshot` — avoids races with concurrent config saves mid-scan), batch-fetches all Season 0 episodes / all movies / virtual folders up front (avoids N+1 `ILibraryManager` queries), inserts a 100ms delay between episodes to respect provider rate limits, then after the main loop: `EnforceIgnoreList`, `ProcessForceLinksAsync`, and — only if dry run is now **off** — `PromoteDryRunPairsAsync` (removes each `DryRun` pair and re-runs `ProcessEpisodeAsync` so it's recreated as `Pending`/`Active`) and `SyncAllActivePairs` (re-syncs watch state for every `Active` pair, catching drift that happened while the plugin was stopped or dry-run was on).

## Metadata lookup providers

### TMDB (`TmdbLookupService.cs`)

- Base: `https://api.themoviedb.org/3`. Rate limit: `SemaphoreSlim(30, 30)` (concurrent-request cap, not a token bucket).
- **Primary path**: episode's `ProviderIds[Imdb]` → `GET /find/{imdb_id}?external_source=imdb_id` → take `movie_results[0]`.
- **No IMDB ID on the episode?** → `GET /tv/{seriesTmdbId}/season/{season}/episode/{ep}/external_ids` to fetch one, then retry the `/find` call.
- **`EnrichWithTmdbAsync`** (called by `AggregatedLookupService` when only TVDB matched via a category tag, with no provider IDs): tries `/find` by IMDB ID first if present, else `GET /search/movie?query=...&year=...`, picks the exact-title match or first result.
- **Localized title resolution**: if the TMDB title equals the original-language title (i.e. no localization applied), fetches `/movie/{id}/alternative_titles` and prefers the user's configured country, falling back to `US` then `GB`.
- Retry: on HTTP 429, exponential backoff 1s→2s→4s...capped at 30s, up to 3 retries. HTTP 404 is cached as a permanent "no match" (`_apiCache.Add(key, null)`). Responses over 1MB are rejected. Every request has a 15s timeout.
- Every outbound URL is cached via `ApiResponseCache` keyed on the URL with `api_key=...` stripped out (`StripApiKey`) — so the cache file on disk never contains the key.

### TVDB (`TvdbLookupService.cs`)

- Base: `https://api4.thetvdb.com/v4`. Rate limit: `SemaphoreSlim(5, 5)`.
- Auth: `POST /login` with `{ apikey }` → bearer token cached in memory for 23h (`TokenLifetime`), refreshed under `SemaphoreSlim(1,1)` double-checked lock. A 401 response clears the cached token so the next call re-authenticates.
- **Primary path**: episode's `ProviderIds[Tvdb]` → `GET /episodes/{id}/extended`. If `linkedMovie` is present → `GET /movies/{linkedMovieId}` → build the match (title preferring a localized translation, year, TVDB ID+slug, IMDB ID from `remoteIds`).
- **No `linkedMovie`?** Falls back to checking the episode's `tagOptions` for a `"Special Category"` tag: value `"Movies"` always qualifies; `"OVA"`/`"OVAs"` only qualifies if `AllowOvaLinking` is enabled. If tagged, the **episode's own metadata** (name, year, IMDB remote ID) is used as the match directly — no separate TVDB movie entity exists for these.
- Title translation: `GET /episodes/{id}/translations/{lang}` or `/movies/{id}/translations/{lang}`, language derived from Jellyfin's `PreferredMetadataLanguage` (ISO 639-1 → 639-2/B via `CultureInfo.ThreeLetterISOLanguageName`), falling back to `eng` if unavailable.
- Same retry/backoff/404-caching/1MB-limit/15s-timeout behavior as TMDB. Cache key here is the raw URL (no API key ever appears in a TVDB request URL — auth is a bearer header, not a query param).

### Aggregation (`AggregatedLookupService.LookupAsync`)

Runs TMDB and TVDB concurrently (`Task.WhenAll`). If both match: primary provider's record wins, but the *other* provider's IDs are merged in (so the stored pair always has both `TmdbMovieId` and `TvdbMovieId` populated when both matched, regardless of which was primary). If only TVDB matched via a tag (no provider IDs at all), it's enriched with a TMDB search before being returned. If only one provider matched, that result is used as-is. Neither matched → `null`, episode is skipped silently (this is the common/expected case for ordinary non-movie specials).

### `ApiResponseCache` (Lookup/ApiResponseCache.cs)

In-memory `ConcurrentDictionary<string, CacheEntry>` (key → `{Timestamp, Response}`, `Response` can be `null` to represent a cached negative/404) mirrored to `api_cache.json` in the plugin data dir. Writes are debounced: `ScheduleSave()` resets a 30-second `Timer` on every `Add`, so bursts of lookups during a full scan don't hammer disk I/O — it only actually serializes 30s after the last write (or immediately on `Dispose`). `MetadataCacheDays` (from live config, not baked in at load time) governs expiry on both `Get`/`IsCached` (lazy per-entry eviction) and on load/save (entries older than the current TTL are dropped). `Clear()` (used by the "Clear Metadata Cache" button) wipes everything — necessary after changing provider/API key settings since stale cached responses would otherwise mask the new lookup.

## Hard link mechanics (`HardLinkService.cs`)

- **Cross-platform**: `LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", ...)` on Windows (via extended-length `\\?\` paths to dodge `MAX_PATH`), `LibraryImport("libc", EntryPoint = "link", ...)` on POSIX, dispatched via `RuntimeInformation.IsOSPlatform`.
- **Same-filesystem check**: Windows compares `Path.GetPathRoot()`; POSIX calls `stat()` on both paths and compares `st_dev`. Cross-device pairs are never attempted — they'd fail at the OS level anyway — and are stored with `Status=Error`.
- **Folder/file naming**: `{SanitizedTitle} ({Year}) [JellyfinPlugin-SpecialToMovie]/{SanitizedTitle} ({Year}){ext}`. The `[JellyfinPlugin-SpecialToMovie]` tag is the load-bearing safety marker for every destructive operation in this codebase (bulk "Remove All Hard Links", orphan cleanup) — nothing without that tag in its path is ever deleted by the plugin.
- **Sanitization** (`SanitizeFileName`): strips `< > : " / \ | ? *` and control chars, trims leading/trailing `.`/space, collapses `..` sequences (path-traversal guard), and prefixes reserved Windows device names (`CON`, `PRN`, `COM1`...`LPT9`) with `_`.
- **Path-escape guard**: `BuildHardLinkPath` resolves the final path with `Path.GetFullPath` and throws `InvalidOperationException` if it doesn't start with the resolved destination library root — defense in depth on top of sanitization.
- **NFO writing**: Kodi-compatible XML (`<movie><title>…</title><year>…</year><uniqueid type="imdb|tmdb|tvdb">…</uniqueid>...</movie>`), only IDs actually found are included. Only written if the destination library has the `"Nfo"` metadata saver enabled (checked via `VirtualFolderInfo.LibraryOptions.MetadataSavers`) — otherwise Jellyfin's online providers identify the movie from the folder name alone.
- **Subtitle linking** (`LinkSubtitles`, on initial pair creation): scans the episode's directory (plus `Subs`/`Subtitles` subfolders) for files sharing the episode's base filename and a known subtitle extension (`.srt .ass .ssa .sub .idx .vtt .sup .pgs`), hard-links each into the movie folder under the movie's base filename, records `{EpisodeSidePath, MovieSidePath, ContentHash}`.
- **Subtitle sync** (`SyncSubtitles`, run every `CleanupTask` cycle for Active non-existing-movie pairs): for each tracked subtitle, if both sides still exist → keep; if both gone → drop silently; if **one side** was removed by the user → hash the surviving file and compare to the stored `ContentHash` — only if it still matches exactly is a `SubtitleDeletion` emitted (telling the caller it's safe to delete the other side via Jellyfin's API); a mismatch means the user replaced the file, so nothing is deleted, just logged. Separately, if exactly one side currently *has* subtitles and the other doesn't (asymmetric — new subs added to just one side), it links them across one-way.
- Subtitle deletions are executed by `CleanupTask.SyncSubtitlesForActivePairsAsync`, which resolves the actual `MediaStreamType.Subtitle` stream on the surviving item via `IMediaSourceManager.GetMediaStreams` and calls `ISubtitleManager.DeleteSubtitles` — never `File.Delete` directly, so Jellyfin's own stream index stays consistent. If the stream isn't indexed yet, the record is kept for retry next cycle rather than treated as a failure.

## Watch status sync (`WatchSyncService.cs`)

`IHostedService` — subscribes to `IUserDataManager.UserDataSaved` in `StartAsync`, unsubscribes in `StopAsync`/`Dispose`.

**Event-driven sync** (`OnUserDataSaved`): fires per user-item save. Looks up a pair by the saved item as either the episode or movie side; if `Status != Active`, ignored. Copies `Played`, `PlayCount`, `IsFavorite` always; `PlaybackPositionTicks`/`LastPlayedDate` only if `WatchStatusOnly` is off. Guarded against infinite loops by a `ConcurrentDictionary<string,byte>` reentrancy set keyed `"{userId}:{pairedItemId}"` — added before `SaveUserData` (which re-triggers this same event for the paired item), removed in a `finally`. `DryRunMode` disables this entirely (no `UserData` writes while dry run is on).

**Initial sync** (`SyncInitialWatchState`, called once when a pair transitions to `Active` — direct force-link pairing, existing-movie pairing, or `CleanupTask`/`FullScanTask` promoting a pending pair): for every Jellyfin user, picks a sync *direction* via `PickSyncDirection` — if neither side has progress, no-op; if only the movie has progress, movie→episode; **in every other case** (episode has progress, or both do), episode→movie. i.e. the TV-special side is treated as authoritative except when it's completely untouched and the movie isn't.

## Deletion handling

### `LibraryEventHandler.OnItemRemoved`

| Removed item | `DryRunMode` | `AutoDeleteOnRemoval` | `TwoWayDeletion` | Result |
|---|---|---|---|---|
| Episode | on | – | – | log only, pair untouched |
| Episode | off | off | – | pair removed from store; hard-link folder left orphaned on disk (logged) |
| Episode | off | on | – | pair removed; if `!IsExistingMovie`, the plugin-owned movie item+folder is deleted via `ILibraryManager.DeleteItem(..., DeleteFileLocation: true)` |
| Movie | on | – | – | log only, pair untouched |
| Movie | off | off/on | off | pair removed from store; episode never touched regardless of `AutoDeleteOnRemoval` |
| Movie | off | on | on | pair removed; the original episode file is *also* deleted |

Pair is always removed from `PairStore` **before** the cascading delete, specifically to prevent `ItemRemoved` firing again for the paired item and causing a duplicate cleanup attempt.

### `CleanupTask.ValidatePair` (every cycle, for every stored pair)

1. Episode gone from library → if `AutoDeleteOnRemoval` and not `IsExistingMovie`, delete the movie item+files; remove the pair either way.
2. (Skipped entirely if `Status == DryRun` or global `DryRunMode` is on.)
3. Hard link file missing but episode still exists and it's not an existing-movie pair → recreate the hard link, reset to `Pending`.
4. `Pending` with `MovieItemId == null` → check if a Movie now exists at `HardLinkPath` (via a pre-built path→item dictionary) → promote to `Active`, run `SyncInitialWatchState`.
5. `Error` status → retry: if the hard link file now exists and a movie is indexed at that path, promote to `Active` the same way.

### API-triggered deletion (`SpecialToMovieController`)

- `POST RemoveAllLinks` — for every non-`IsExistingMovie` pair, deletes the movie item+files via Jellyfin, then **resets the pair to `DryRun`** (clears `HardLinkPath`/`MovieItemId`/`ErrorMessage`) rather than deleting the pair record — so a subsequent scan recreates everything without re-running lookups. Original episode files and pre-existing-movie pairs are never touched.
- `POST ClearDatabase` — wipes `PairStore` entirely (does **not** touch any files on disk — just forgets the pairing metadata).
- `POST RemoveForceLinkedPairs` — removes pairs matching given episode keys (parsed from `EpisodePath`, format `"{seriesFolder} S00E{n}"`, or by item GUID), deleting the movie side first unless `IsExistingMovie`.
- `POST RemovePair` — removes a single pair by ID from the store only (no file deletion).

None of the destructive paths above ever walk the filesystem blindly — they only ever act on paths already tracked in `PairStore`, and always go through Jellyfin's own `ILibraryManager.DeleteItem`/`ISubtitleManager.DeleteSubtitles` rather than raw `File.Delete`.

## REST API (`Api/SpecialToMovieController.cs`)

Route prefix `/SpecialToMovie`, every endpoint requires `Policies.RequiresElevation` (admin auth).

| Method & route | Body | Effect |
|---|---|---|
| `POST RemoveAllLinks` | – | See above. Returns `{ Removed, TotalPairs }`. |
| `POST RunFullScan` | – | Queues `FullScanTask` via `ITaskManager`. Returns `{ Status: "Started" }`. |
| `POST RunCleanup` | – | Queues `CleanupTask`. Same response shape. |
| `GET Pairs` | – | Returns the full `PairStore.GetAll()` list (drives the config page's pair table). |
| `POST RemoveForceLinkedPairs` | `{ EpisodeKeys: string[] }` | See above. Returns `{ Removed }`. |
| `POST RemovePair` | `{ PairId: Guid }` | Removes one pair. 404 if not found. |
| `POST ClearDatabase` | – | Wipes `PairStore`. Returns `{ Removed }`. |
| `POST ClearMetadataCache` | – | Clears `ApiResponseCache`. Returns `{ Removed }`. |
| `POST TestTmdb` | `{ ApiKey: string }` | Hits `GET /3/configuration` with a 10s timeout to validate a key before saving. Key length capped at 500 chars server-side. |
| `POST TestTvdb` | `{ ApiKey: string }` | Hits `POST /v4/login` with a 10s timeout. Same length cap. |

## Scheduled tasks

| Task | Key | Default trigger | What it does |
|---|---|---|---|
| `FullScanTask` | `SpecialToMovieFullScan` | Daily @ midnight | Full `RunFullScanAsync` — see Detection pipeline above. |
| `CleanupTask` | `SpecialToMovieCleanup` | Every `CleanupIntervalHours` (default 6, min 1) | Validates every pair (`ValidatePair`), syncs subtitles for active pairs, enforces the ignore list, processes force links. |

Both are visible/runnable from Jellyfin's own Scheduled Tasks dashboard, and their intervals can be overridden there.

## Dry Run Mode — what "on" actually guarantees

`DryRunMode` defaults to `true`. While on:
- `SpecialDetectionService` still runs full TMDB/TVDB lookups (so matches populate the config page's pair table) but stores everything as `Status=DryRun` — zero hard links, zero NFO writes, zero subtitle links.
- `WatchSyncService.OnUserDataSaved` and `SyncInitialWatchState` both bail out immediately — no `UserData` is ever written.
- `LibraryEventHandler.OnItemRemoved` only logs what it *would* do.
- `CleanupTask.ValidatePair` skips every check past "episode gone" for `DryRun`-status pairs, and skips **all** filesystem operations globally when `DryRunMode` is on.
- Turning it off doesn't retroactively act on stored `DryRun` pairs by itself — the next `FullScanTask` run (manual or scheduled) is what promotes them via `PromoteDryRunPairsAsync`.

## Known gotchas

- **[`JellyfinPlugin-SpecialToMovie plan.md`](JellyfinPlugin-SpecialToMovie%20plan.md)** is the original pre-implementation design doc and has drifted significantly: it describes `ForceLinks` as a `Dictionary<string,string>` (actual: `List<ForceLinkEntry>`), a `CreateMovieSubfolders` setting that doesn't exist, a `FullScanIntervalHours` setting that doesn't exist (the full scan interval is only configurable via Jellyfin's own scheduled-task trigger UI, not a plugin setting), and deletion mechanics described as raw file I/O when the real code goes through `ILibraryManager.DeleteItem`. Treat it as historical rationale only, never as ground truth for current behavior.
- **The Jellyfin package version is pinned exactly (`12.0.0-rc4`), not floated.** Do not "tidy" it back to a `12.*` wildcard — NuGet floating versions exclude pre-releases, so `12.*` fails to restore entirely while no stable 12.x exists. **When Jellyfin 12.0.0 GA ships, re-pin to it (or switch to `12.*` once a stable 12.x is published) and cut a follow-up release.** The user has accepted the assumption that GA carries no API changes from rc4.
- **Building requires the .NET 10 SDK** (10.0.302 was used for 1.0.16.0). Having only the .NET 10 *runtime* is not enough — a box carrying the 10.x runtime but a 9.x SDK will fail the build until the matching SDK is installed. Check `dotnet --list-sdks` before diagnosing anything else.
- **`build.yaml` had drifted since the initial commit** (`35adaa8`) and was resynced in 1.0.16.0. Nothing consumes it — there is no CI, no `.github/`, and no JPRM setup, so a wrong value produces no visible symptom, which is how it stayed at `1.0.0.0` for fifteen releases. It is now part of the CLAUDE.md release checklist (step 1); keep it in sync.
- `AUDIT.md`, `HANDOFF.md`, `IDEAS.md`, and `CLAUDE.md` live in `agentic/` **inside the repo**, so they are version controlled and are captured by release commits alongside code. That also makes them **public**: never write credentials, absolute local paths, hostnames, IP addresses, or anything else machine-specific into them. Their history before the move into the repo is not in `git log`.
- Mutable config lists (`LibraryMappings`, `ForceLinks`, `IgnoreList`) are snapshotted into an immutable `ConfigSnapshot` at the start of any scan to avoid races with a concurrent config save — follow this pattern if you add new scan-time logic that iterates them.
- `Plugin.Instance` can theoretically be `null` very early in startup; nearly every service null-checks it and no-ops rather than throwing.
- API keys are always user-entered through the config UI — never hardcoded, never logged (URLs are stripped of `api_key=` before being used as cache keys or appearing in TMDB debug logs).

## Release process, versioning, and audits

Fully documented in [`CLAUDE.md`](CLAUDE.md) (auto-loaded into your context already) — don't duplicate it here, just follow it. In short: version bumps are 4-part (`AssemblyVersion`/`FileVersion`), a full security/efficiency audit is **mandatory before every release** and must be presented to the user for approval before any commit, and releases are only cut when the user explicitly asks.
