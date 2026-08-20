# Security & Efficiency Audit Log

## Audit: 2026-08-04 (Session 14 — Pre-release v1.0.16.0, Jellyfin 12 migration)

**Scope**: Framework/runtime migration only. `TargetFramework` `net9.0` -> `net10.0`; `Jellyfin.Controller`/`Jellyfin.Model` `10.*` -> pinned `12.0.0-rc4`; `AssemblyVersion`/`FileVersion` -> `1.0.16.0`; `build.yaml` `targetAbi`/`framework` corrected; README requirements + build-output path corrected.
**Triggered by**: User-requested update of the plugin for Jellyfin 12.

**Critical context**: `git diff v1.0.15 -- "*.cs" "*.html"` returns **empty**. No application code changed since the Session 13 audit. This audit therefore assesses what the runtime/SDK move changes about the existing (already-audited) code, not new logic.

---

### Confirmed Issues

None found. No new security or efficiency defect is introduced by the migration.

---

### API Surface Verification (10.11.11 -> 12.0.0-rc4)

Diffed the full documented public surface of `Jellyfin.Controller` + `Jellyfin.Model` between versions: 32 members removed in 12, **none** consumed by this plugin (removals are `ISearchEngine`, `IItemRepository.*`, `IDtoService.GetBaseItemDtos`, `BaseItem.GetExtras`, `Video.GetAdditionalParts`, `ILibraryManager.DeleteItemsUnsafeFast`/`ResolvePath`, `IPlaylistManager.AddItemToPlaylistAsync`, DLNA/session/IPData members).

Every API in the plugin's service graph verified present and signature-identical in 12:

- `ILibraryManager.DeleteItem` / `GetItemById` / `GetVirtualFolders`, `ItemAdded`/`ItemRemoved`
- `IUserDataManager.UserDataSaved`, both `SaveUserData` overloads, `GetUserData` — **changed additively only** (12 adds `GetUserDataBatch`, `GetResumeUserData`, `GetResumeUserDataBatch`, `ResetPlaybackStreamSelections`). `WatchSyncService` unaffected.
- `IScheduledTask`, `ITaskManager`, `ISubtitleManager`, `IMediaSourceManager.GetMediaStreams`
- `IPluginServiceRegistrator.RegisterServices` — byte-identical signature
- `BasePlugin<T>`, `BasePluginConfiguration`, `VirtualFolderInfo.LibraryOptions.MetadataSavers`, `PreferredMetadataLanguage`
- `MediaBrowser.Common.Api.Policies.RequiresElevation` — confirmed present in `Jellyfin.Common` 12.0.0-rc4; all API authorization gating intact.

Build result: **0 warnings, 0 errors** against `12.0.0-rc4` on SDK 10.0.302.

---

### Runtime Verification (.NET 10.0.10 smoke test)

Because the release rests on a runtime change, the two most runtime-sensitive primitives were executed standalone under net10.0 against the **real** `Models/LinkedPair.cs` + `Models/PairStatus.cs` sources (13/13 passed):

- `LibraryImport` `CreateHardLinkW` creates a working hard link; link reflects source mutation (same inode confirmed).
- `CreateHardLinkW` with the `\\?\` extended-length prefix (`ToExtendedLengthPath`) succeeds.
- `PairStore` JSON round-trip preserves Guids, paths, provider IDs, `DateTime`, and the `LinkedSubtitle` record.
- `LinkedSubtitle` record value-equality survives round-trip — the `SequenceEqual` guard added in Session 13 (`CleanupTask.cs:169`) still suppresses redundant upserts.
- **`PairStatus` still serializes numerically** under .NET 10 STJ, so `pairs.json` files written by 1.0.15 and earlier remain readable after upgrade. No migration required.

---

### Items Presented to User (accepted risks / notes, not defects)

1. **Pre-release dependency (MEDIUM — release management, not code)**: `12.0.0-rc4` is the newest published Jellyfin 12 package; no stable `12.0.0` exists. Shipping with `targetAbi 12.0.0.0` targets a server build that is not final, and the API could still shift before GA. **User decision: accepted** — 12.0.0 GA is assumed to carry no API changes from rc4, so no re-verification is planned at GA.
2. **Version pinned exactly, not floated**: `12.0.0-rc4` is pinned rather than `12.*` because NuGet floating versions exclude pre-releases, so `12.*` fails to restore while no stable 12.x exists. **Action required when 12.0.0 GA ships**: re-pin (or move to `12.*`) and re-release.
3. **10.11 users**: per user decision, existing manifest entries are left untouched and no 2.x bump was made. Jellyfin's installer only offers versions whose `targetAbi` <= server version, so 10.11 servers continue to be served 1.0.15.0 and will not see 1.0.16.0. 10.11 is effectively frozen at 1.0.15.0.
4. **Pre-existing uncommitted README copy edits (INFORMATIONAL)**: the working tree carried unrelated wording tweaks from an earlier session (About paragraph, "existing movie detection" phrasing, dry-run step, cleanup wording, AI-tools footer). These are swept into the v1.0.16.0 release commit.
5. **`build.yaml` stale at `1.0.0.0` (INFORMATIONAL, pre-existing) — RESOLVED**: `git log -- build.yaml` shows a single commit (`35adaa8`, initial commit); the file was untouched through all 15 prior releases. Root cause: it is a JPRM build manifest with **no consumer** — there is no `.github/` directory, no CI workflow, and nothing in the repo references it or JPRM. The release process is fully manual and reads `csproj` (DLL assembly metadata) and `manifest.json` instead, so a wrong `build.yaml` produced no user-visible symptom and drifted unnoticed. Three of five meaningful fields were wrong (`version`, `changelog`, plus `targetAbi`/`framework` until this session).
   **User decision: sync and keep synced.** `version` -> `1.0.16.0`, `changelog` -> this release's text, `targetAbi`/`framework` -> Jellyfin 12 / net10.0. A `build.yaml` step was added to the CLAUDE.md release process (step 1) so it cannot drift again.

---

### Previous Audit Items — Status Check

All items from Sessions 2–13 remain fixed (no code changed). All 7 false positives from 2025-05-20 remain valid — notably the `Marshal.GetLastWin32Error()`-on-POSIX entry, whose reasoning (`SetLastError = true` on `LibraryImport` captures errno on Unix) is unchanged in .NET 10. `StatBuf` padding and the `stat` P/Invoke remain as reviewed in Session 5.

---

### Areas Reviewed (No New Issues)

- **Authorization**: all endpoints still gated by `Policies.RequiresElevation`; constant confirmed present in Jellyfin.Common 12.
- **PairStore**: atomic write + backup/restore path unchanged; forward/backward JSON compatibility verified by smoke test.
- **Supply chain**: Jellyfin 12 adds transitive `BitFaster.Caching` 2.6.0 and `Microsoft.Extensions.Configuration.Binder` 10.0.10. The release zip contains **only** `Jellyfin.Plugin.SpecialToMovie.dll` (verified in `bin/Release/net10.0/`), so no third-party assembly is redistributed by this plugin.
- **P/Invoke layer**, **rate limiters**, **response size limits**, **reentrancy guard**, **SanitizeFileName / path-escape guard**: unchanged code, unchanged behavior under .NET 10.

---
---

## Audit: 2026-06-09 (Session 13)

**Scope**: Changes since Session 12: `LinkedSubtitle` changed from class to record with `ContentHash` (SHA-256) property. `SyncSubtitles` no longer calls `File.Delete` -- returns `SubtitleDeletion` instructions with hash verification. New `SubtitleSyncResult`/`SubtitleDeletion` types. `CleanupTask` gains `IMediaSourceManager`/`ISubtitleManager` dependencies; new `SyncSubtitlesForActivePairsAsync` resolves subtitle streams by path, deletes via `ISubtitleManager.DeleteSubtitles`. `SequenceEqual` guard prevents unconditional upsert.
**Triggered by**: User-requested full audit with 7 critical focus areas.

---

### Confirmed Issues

None found.

---

### Critical Focus Areas -- Findings

1. **Hash verification safety**: SHA-256 is sufficient. Null `ContentHash` (legacy records without hash) is handled at `HardLinkService.cs:254` -- `string.IsNullOrEmpty(sub.ContentHash)` evaluates true, deletion is skipped, and a warning is logged. Legacy records are never deleted, just dropped from tracking. Safe.
2. **Subtitle stream index resolution**: `GetMediaStreams(item.Id)` at `CleanupTask.cs:143-146` filters by `Type == Subtitle && IsExternal && path match`. Jellyfin deduplicates external streams by path; multiple streams cannot share the same path. `FirstOrDefault` is correct. When the stream is not yet indexed (null), the record is retained for retry next cycle (line 151). Safe.
3. **Race condition**: Between `SyncSubtitles` detecting a deletion and `CleanupTask` executing `DeleteSubtitles`, state could change. Mitigated: (a) `DeleteSubtitles` handles missing files gracefully (exception caught at line 160-165, record re-added); (b) Jellyfin's task scheduler serializes scheduled task execution -- no concurrent cleanup. Safe.
4. **DI registration**: `IMediaSourceManager` and `ISubtitleManager` are Jellyfin core services registered by the server. The plugin does not register them and does not need to. Confirmed in `PluginServiceRegistrator.cs`. Safe.
5. **`LinkedSubtitle` as record**: Compiler-generated value-based `Equals`/`GetHashCode` covers all three properties (`EpisodeSidePath`, `MovieSidePath`, `ContentHash`). `SequenceEqual` at `CleanupTask.cs:169` correctly compares by value. `System.Text.Json` serializes records identically to classes. Safe.
6. **SHA-256 efficiency**: Subtitle files are typically < 1 MB. Hashing runs every 6 hours during `SyncSubtitles`. Negligible overhead. Acceptable.
7. **Finding #1 from Session 12 (unconditional upsert)**: **Fixed** -- `CleanupTask.cs:168-173` uses `SequenceEqual` + count comparison to skip `Upsert` when subtitle list is unchanged.

---

### Previous Audit Items -- Status Check

All items from Sessions 2-12 remain fixed. All 7 false positives from 2025-05-20 remain valid.

---

### New Code Reviewed (No Issues)

- **`ComputeFileHash` (HardLinkService.cs:310-324)**: SHA-256 via `SHA256.Create()`. Both `FileStream` and `SHA256` properly disposed via `using`. Exception caught and logged; returns `null` on failure (caller treats null as "don't delete"). Safe.
- **`SubtitleSyncResult`/`SubtitleDeletion` (IHardLinkService.cs:30-44)**: Plain data classes. `SubtitleDeletion.Record` uses `required init` -- immutable after construction. No security surface.
- **`SyncSubtitlesForActivePairsAsync` (CleanupTask.cs:111-174)**: Re-fetches pairs from store (fresh snapshot after `ValidatePair` loop). Filters to Active, non-existing-movie, non-null-HardLinkPath pairs. For each deletion: resolves item, finds stream by path, deletes via `ISubtitleManager`. On any failure (null item, null stream, exception), record is re-added for retry. Per-pair `Upsert` only when list actually changed -- steady-state is zero writes. Safe and efficient.
- **`LinkSubtitlesFromDir`/`LinkSubtitlesOneWay` ContentHash capture (HardLinkService.cs:372,380,415,422)**: Hash computed on the source file at link time. Stored on the record. Consistent with deletion verification. Safe.
- **Hash comparison in SyncSubtitles (HardLinkService.cs:254-255)**: `OrdinalIgnoreCase` comparison handles `Convert.ToHexString` output (uppercase) vs any stored format. Safe.

### Areas Reviewed (No New Issues)

- **Authorization**: All API endpoints gated by `Policies.RequiresElevation` -- intact.
- **PairStore**: Atomic writes, lock-guarded -- intact.
- **All previously audited infrastructure**: Unchanged.

---
---

## Audit: 2026-06-09 (Session 12)

**Scope**: Changes since Session 11: `LinkedSubtitle` model + `LinkedPair.LinkedSubtitles`; `LinkSubtitles`/`SyncSubtitles` now return/consume `List<LinkedSubtitle>`; new `File.Delete()` deletion logic in `SyncSubtitles`; `LinkSubtitlesOneWay`/`LinkSubtitlesFromDir` record tracking; `IHardLinkService` signature updates; `CleanupTask.ValidatePair` subtitle sync + upsert; `SpecialDetectionService` storing returned records.
**Triggered by**: User-requested full audit, focus on subtitle deletion logic.

---

### Confirmed Issues

#### 1. Unconditional `_pairStore.Upsert(pair)` on every cleanup cycle for active pairs (LOW — efficiency)
- **File**: `Tasks/CleanupTask.cs` lines 191-194
- **Issue**: For every Active, non-existing-movie pair with a hard link, `SyncSubtitles` is called and `pair.LinkedSubtitles` is reassigned then `_pairStore.Upsert(pair)` is always invoked — even when the returned list is unchanged (the common steady-state case). Each `Upsert` serializes the entire pair list to JSON and does an atomic file write. With N active pairs every cleanup cycle does N full-store writes regardless of whether anything changed.
- **Proposed fix**: Only `Upsert` when the subtitle set actually changed (e.g., compare counts/contents, or have `SyncSubtitles` signal whether it mutated). Same class as prior batched-write findings.

#### 2. Path-based subtitle deletion can target an unintended file after a media move (LOW — correctness/safety)
- **File**: `HardLink/HardLinkService.cs` lines 230-259
- **Issue**: Deletion keys off stored string paths, not inodes/markers. If a tracked subtitle's episode side is moved/renamed (so `EpisodeSidePath` no longer exists) but a *different* unrelated file later occupies that old path, the "one side gone" branch deletes the surviving side. The surviving side is always a plugin-created hard link in practice, and `LinkedSubtitle` records only ever hold plugin-linked paths, so real-world risk is low — but deletion is not gated by the `[JellyfinPlugin-SpecialToMovie]` folder marker the way movie-folder operations are. The movie side always lives under the plugin tag folder; the episode side does not, so deleting the episode-side file (when movie side was removed) touches the user's source directory.
- **Proposed fix**: Before `File.Delete(remaining)`, when `remaining` is the episode side, confirm the movie side path contained the plugin tag (it's a plugin-managed pair) — already true by construction — and optionally verify `remaining` is still a hard link to / shares inode with the original. At minimum, gate episode-side deletion behind the same `AutoDeleteOnRemoval`-style intent if desired. Present to user as accepted-risk vs. fix.

---

### Critical Questions — Findings

1. **Crafted/stale record → unintended deletion**: Possible only via stale episode-side path after a move (Issue #2). PairStore is admin-only, not attacker-writable, so not an injection vector. Paths are never derived from external/user free-text input.
2. **Same-library / mixed-content collision**: If `EpisodeSidePath == MovieSidePath`, then `epExists == mvExists` always, so the code takes the "both exist → keep" or "both gone → drop" branch and **never deletes**. No self-deletion. `BuildHardLinkPath` forces the movie under a `[JellyfinPlugin-SpecialToMovie]` tag folder, so collision cannot occur for plugin-created links. Safe.
3. **TOCTOU (deleted between `File.Exists` and `File.Delete`)**: `File.Delete` does not throw if the file is already gone (it no-ops on a missing file), so the race is benign. Safe.
4. **`File.Delete` throws → inconsistent state**: Caught; on failure the record is re-added to `result` (line 257) so it is retried next cycle. Consistent. Good.
5. **Gated to plugin-linked files only**: Movie side always under plugin tag folder (safe). Episode side is the user's source dir — see Issue #2.

---

### Previous Audit Items — Status Check

All items from Sessions 2–11 remain fixed. All 7 false positives from 2025-05-20 remain valid.

---

### New Code Reviewed (No Issues)

- **`LinkedSubtitle` model**: Two string properties, default empty. No deserialization risk beyond existing PairStore JSON (admin-only file).
- **`LinkSubtitlesFromDir` / `LinkSubtitlesOneWay` record tracking**: Records added only after successful `Create` or when link already exists. `epPath`/`mvPath` orientation via `sourceIsEpisode` is correct in both branches. Path traversal properties unchanged from Session 11 (suffix always starts with `.`; whitelist-gated extensions). Safe.
- **`SyncSubtitles` new-link branch (lines 261-296)**: Still gated by `episodeHasSubs != movieHasSubs`. Records appended to `result`. Unchanged safety from Session 11.
- **`SpecialDetectionService` storing records (lines 271-291)**: `linkedSubs ?? new List<LinkedSubtitle>()` — never null. Safe.
- **`IHardLinkService` signatures**: Match implementation.

---
---

## Audit: 2026-06-05 (Session 11)

**Scope**: Full review of changes since Session 10 (2026-05-27): `LinkSubtitles`, `SyncSubtitles`, `HasSubtitles`, `LinkSubtitlesFromDir`, `SyncSubtitlesOneWay` in `HardLinkService.cs`. `SyncSubtitles` call in `CleanupTask.cs`. `LinkSubtitles` call in `SpecialDetectionService.cs`. Force link GUID matching on right side and provider ID parsing in `ParseForcedMovie`. `IHardLinkService.cs` interface additions.
**Triggered by**: User-requested full security and efficiency audit.

---

### Confirmed Issues

None found.

---

### Previous Audit Items -- Status Check

| Previous Finding | Current Status |
|---|---|
| EnforceIgnoreList N individual Remove calls (Session 8 #1) | Still fixed |
| ApiResponseCache timer race condition (Session 6, originally Session 3 #1-2) | Still fixed |
| RemoveForceLinkedPairs N writes (Session 3 #3) | Still fixed |
| CleanupTask AutoDeleteOnRemoval (Session 2 #1) | Still fixed |
| RemoveAllLinks N writes (2026-05-22 #1) | Still fixed |
| FindExistingMovie cross-library match (2026-05-22 #2) | Still fixed |
| All 5 findings from 2026-05-21 | Still fixed |
| All 3 findings from 2025-05-20 | Still fixed |

All 7 false positives from the 2025-05-20 audit remain valid.

---

### New Code Reviewed

- **`LinkSubtitlesFromDir` (HardLinkService.cs lines 295-327)**: Enumerates files in `sourceDir` only (no recursion). `StartsWith(sourceBaseName)` filter prevents matching unrelated files. Extension validated against `SubtitleExtensions` whitelist. Suffix extracted via `fileName[sourceBaseName.Length..]` always starts with `.` (language tag or extension), so `Path.Combine(destDir, destBaseName + suffix)` cannot escape `destDir`. `File.Exists` check prevents overwriting. `Directory.CreateDirectory(destDir)` is safe -- `destDir` is always `movieFolderPath` (validated by `BuildHardLinkPath`) or a hardcoded subfolder ("Subs"/"Subtitles") under it. No path traversal risk.
- **`SyncSubtitlesOneWay` (lines 329-365)**: Identical pattern to `LinkSubtitlesFromDir`. Same safety properties. Bidirectional sync correctly constrained: only called when one side has subtitles and the other does not (lines 229-237 in `SyncSubtitles`).
- **`HasSubtitles` (lines 270-293)**: Uses `Directory.EnumerateFiles` (lazy) with `.Any()` short-circuit. Only checks the directory itself and hardcoded "Subs"/"Subtitles" subfolders. Efficient for typical subtitle directories (<20 files).
- **`SyncSubtitles` (lines 217-268)**: Early exit when both sides already have subtitles or neither has them. Prevents redundant scanning every cleanup cycle. Subfolder names are hardcoded constants, not derived from user input.
- **`LinkSubtitles` (lines 191-215)**: Called after hard link + NFO creation in `SpecialDetectionService.ProcessEpisodeAsync` (line 273). `episodeDir` from `Path.GetDirectoryName(episodePath)` -- Jellyfin-controlled path. `movieFolderPath` from `Path.GetDirectoryName(linkPath)` where `linkPath` was validated by `BuildHardLinkPath`. Safe.
- **`SyncSubtitles` call in CleanupTask (lines 188-191)**: Guarded by `pair.Status == PairStatus.Active && !pair.IsExistingMovie && !string.IsNullOrEmpty(pair.HardLinkPath) && episode != null`. Runs within Jellyfin's serialized task scheduler -- no concurrent access.
- **Force link right-side GUID detection (SpecialDetectionService.cs lines 93-122)**: `Guid.TryParse` on `forceLink.MovieTitle` -- rejects non-GUID strings. `GetItemById` null-checked. Direct pair created as `IsExistingMovie = true` with no filesystem changes. Safe.
- **`SubtitleExtensions` HashSet (line 184)**: `StringComparer.OrdinalIgnoreCase` for case-insensitive matching. Standard subtitle extensions only. No risk from `.srt` files that aren't subtitles -- they become hard links (harmless).
- **Race conditions**: None. `LinkSubtitles` runs during pair creation (single-threaded scan). `SyncSubtitles` runs in `CleanupTask` (serialized by Jellyfin scheduler). No concurrent file creation risk.
- **Symlinks**: `Directory.EnumerateFiles` does not follow symlinks by default on .NET. Even if a symlink target were enumerated, the hard link creation would fail gracefully (logged, returns false).

### Areas Reviewed (No New Issues)

- **Authorization**: All API endpoints gated by `Policies.RequiresElevation` -- intact.
- **PairStore**: Atomic writes, lock-guarded -- intact.
- **All previously audited infrastructure**: Unchanged.

---
---

## Audit: 2026-05-27 (Session 10)

**Scope**: Full review of changes since Session 9 (2026-05-26): `ParseForcedMovie` now detects IMDB (`tt*`), TMDB (`tmdb:*`), TVDB (`tvdb:*`) IDs. Force link matching in `ProcessEpisodeAsync` now matches by episode item ID (GUID). `ProcessForceLinksAsync` finds episodes by GUID. New `FindExistingMovieByTitle` method. Force link flow uses provider-ID or title-only matching to detect pre-existing movies. `configPage.html` placeholder/description updates.
**Triggered by**: User-requested full security and efficiency audit.

---

### Confirmed Issues

None found.

---

### Previous Audit Items -- Status Check

| Previous Finding | Current Status |
|---|---|
| EnforceIgnoreList N individual Remove calls (Session 8 #1) | Still fixed |
| ApiResponseCache timer race condition (Session 6, originally Session 3 #1-2) | Still fixed |
| RemoveForceLinkedPairs N writes (Session 3 #3) | Still fixed |
| CleanupTask AutoDeleteOnRemoval (Session 2 #1) | Still fixed |
| RemoveAllLinks N writes (2026-05-22 #1) | Still fixed |
| FindExistingMovie cross-library match (2026-05-22 #2) | Still fixed |
| All 5 findings from 2026-05-21 | Still fixed |
| All 3 findings from 2025-05-20 | Still fixed |

All 7 false positives from the 2025-05-20 audit remain valid.

---

### New Code Reviewed

- **`ParseForcedMovie` IMDB parsing (SpecialDetectionService.cs lines 533-537)**: Validates `tt` prefix + all-digit suffix via `trimmed[2..].All(char.IsDigit)`. Length guard (`> 500`) at entry prevents excessive input. Case-insensitive `StartsWith`. No injection risk — the resulting `ImdbId` is only used for Jellyfin provider ID lookups, not string interpolation into queries or URLs.
- **`ParseForcedMovie` TMDB/TVDB parsing (lines 540-557)**: Extracts numeric ID after `tmdb:`/`tvdb:` prefix. Validates all-digit via `id.All(char.IsDigit)` and non-empty check. Returns `null` on malformed input. No risk of non-numeric IDs reaching provider lookups.
- **`ParseForcedMovie` title+year fallback (lines 559-583)**: `LastIndexOf('(')` + `TrimEnd()` correctly handles titles with parentheses. `int.TryParse` on year string — no overflow risk. Returns a `MovieMatch` with just `Title`/`Year`, no provider IDs. Safe.
- **Force link GUID matching in `ProcessEpisodeAsync` (lines 84-87)**: `episode.Id.ToString()` compared case-insensitively against `forceLink.EpisodeKey`. GUIDs are always lowercase hex+hyphens. Safe.
- **Force link GUID matching in `ProcessForceLinksAsync` (lines 674-677)**: `Guid.TryParse` on `forceLink.EpisodeKey` — rejects non-GUID strings gracefully. Falls back to episode key string match. Correct.
- **`FindExistingMovieByTitle` (lines 437-466)**: Null/empty title guard at entry. Filters to destination library locations (same pattern as `FindExistingMovie`). Matches by `Name` (case-insensitive) and optional `ProductionYear`. When `match.Year` is null (title-only force link without year), `!match.Year.HasValue` is true so year check is skipped — matches any year. This is intentional for bare title force links. No false positive risk beyond user intent. Returns `null` when `cachedMovies` or `virtualFolders` is null (uncached path) — safe fallback, force links without provider IDs simply skip existing-movie detection in the event handler path.
- **Force link existing-movie branching (lines 93-101)**: `hasProviderIds` check correctly routes to `FindExistingMovie` (provider ID match) vs `FindExistingMovieByTitle` (title-only match). Prevents title-only force links from accidentally matching by empty provider IDs.
- **configPage.html force link placeholder/description (lines 299-302, 653)**: Text-only changes. DOM property assignment. No injection vectors.

### Areas Reviewed (No New Issues)

- **Authorization**: All API endpoints gated by `Policies.RequiresElevation` -- intact.
- **PairStore**: Atomic writes, lock-guarded -- intact.
- **All previously audited infrastructure**: Unchanged.

---
---

## Audit: 2026-05-26 (Session 9)

**Scope**: Full review of all changes since Session 7 (2026-05-23): `WatchStatusOnly` config bool, `CleanupIntervalHours` default change, `WatchSyncService` conditional sync, `EnforceIgnoreList` batch removal refactor, `CleanupTask` renamed + async with `EnforceIgnoreList`/`ProcessForceLinksAsync`, `FullScanTask` description update, `configPage.html` style relocation + checkbox multi-select + bulk remove + three-dot menu + WatchStatusOnly checkbox + filter bar changes.
**Triggered by**: User-requested full security and efficiency audit.

---

### Confirmed Issues

None found.

---

### Previous Audit Items -- Status Check

| Previous Finding | Current Status |
|---|---|
| EnforceIgnoreList N individual Remove calls (Session 8 #1) | **Fixed** -- now collects IDs into `toRemove` list and calls `_pairStore.RemoveMany(toRemove)` once (SpecialDetectionService.cs lines 506, 535-538). |
| ApiResponseCache timer race condition (Session 6, originally Session 3 #1-2) | Still fixed |
| RemoveForceLinkedPairs N writes (Session 3 #3) | Still fixed |
| CleanupTask AutoDeleteOnRemoval (Session 2 #1) | Still fixed |
| RemoveAllLinks N writes (2026-05-22 #1) | Still fixed |
| FindExistingMovie cross-library match (2026-05-22 #2) | Still fixed |
| All 5 findings from 2026-05-21 | Still fixed |
| All 3 findings from 2025-05-20 | Still fixed |

All 7 false positives from the 2025-05-20 audit remain valid.

---

### New Code Reviewed

- **`WatchStatusOnly` config property (PluginConfiguration.cs line 51)**: Simple boolean, default `false`. No new attack surface.
- **`CleanupIntervalHours` default changed from 12 to 6 (line 45)**: Value still clamped to `Math.Max(1, ...)` in `CleanupTask.GetDefaultTriggers`. No issue.
- **`SyncInitialWatchState` WatchStatusOnly check (WatchSyncService.cs lines 100-104)**: Conditionally skips `PlaybackPositionTicks` and `LastPlayedDate`. Always syncs `Played`, `PlayCount`, `IsFavorite`. Config read once at method entry (line 60), no TOCTOU risk.
- **`OnUserDataSaved` WatchStatusOnly check (WatchSyncService.cs lines 224-228)**: Same pattern. Config read at line 162, checked at line 224. Consistent.
- **`EnforceIgnoreList` batch refactor (SpecialDetectionService.cs lines 503-539)**: Collects IDs into `toRemove` list, calls `_pairStore.RemoveMany(toRemove)` once after the loop. `DeleteLinkedMovieItem` calls correctly placed before the batch remove. Efficient and correct.
- **`CleanupTask` changes (CleanupTask.cs)**: Name changed to "Sync & Maintenance" (display only). `SpecialDetectionService` injected. `ExecuteAsync` now calls `EnforceIgnoreList()` at 80% progress and `ProcessForceLinksAsync()` at 90%. Both safe -- Jellyfin serializes scheduled task execution. `ProcessForceLinksAsync` properly awaited with `ConfigureAwait(false)`.
- **`FullScanTask` (FullScanTask.cs)**: Description text updated only. No logic change.
- **configPage.html `<style>` in `<body>`**: Standard Jellyfin plugin practice. No security impact.
- **Checkbox multi-select + bulk remove + three-dot menu**: All DOM rendering uses `textContent` and property assignment. No innerHTML injection. JS single-threaded counter in `removeSelectedPairs` is safe.
- **"Only sync watched/unwatched status" checkbox**: Standard `.checked` load/save pattern.
- **Filter bar changes**: Display-only rewording. Filter logic unchanged.

### Areas Reviewed (No New Issues)

- **Authorization**: All API endpoints gated by `Policies.RequiresElevation` -- intact.
- **PairStore**: Atomic writes, lock-guarded -- intact.
- **All previously audited infrastructure**: Unchanged.

---
---

## Audit: 2026-05-23 (Session 7)

**Scope**: Targeted review of new code: `EnforceIgnoreList()`, `ProcessForceLinksAsync()`, `DeleteLinkedMovieItem()` in `SpecialDetectionService.cs`; async `ExecuteAsync` + new dependency in `CleanupTask.cs`; checkbox selection, `removeSelectedPairs`, three-dot menu, `updateSelectionUI` in `configPage.html`.
**Triggered by**: User-requested audit of new ignore list enforcement, force link processing, and bulk pair removal features.

---

### Confirmed Issues

None found.

---

### Previous Audit Items — Status Check

| Previous Finding | Current Status |
|---|---|
| ApiResponseCache timer race condition (Session 6, originally Session 3 #1-2) | Still fixed |
| RemoveForceLinkedPairs N writes (Session 3 #3) | Still fixed |
| CleanupTask AutoDeleteOnRemoval (Session 2 #1) | Still fixed |
| RemoveAllLinks N writes (2026-05-22 #1) | Still fixed |
| FindExistingMovie cross-library match (2026-05-22 #2) | Still fixed |
| All 5 findings from 2026-05-21 | Still fixed |
| All 3 findings from 2025-05-20 | Still fixed |

All 7 false positives from the 2025-05-20 audit remain valid.

---

### New Code Reviewed

- **`EnforceIgnoreList()` (SpecialDetectionService.cs lines 492-539)**: Null check on config. `GetItemById` cast to `Episode` with null guard. Ignore matching uses `StringComparer.OrdinalIgnoreCase`. `DeleteLinkedMovieItem` only called when `autoDelete && !pair.IsExistingMovie` — correct guard. Per-pair `_pairStore.Remove` is acceptable since ignore lists are small and user-curated.
- **`ProcessForceLinksAsync()` (lines 541-603)**: Config null check. CancellationToken properly propagated and checked per iteration. Episodes fetched once into a list; `FirstOrDefault` per force link is O(N*M) but both sets are small. `ExistsForEpisode` check prevents duplicate processing. Delegates to existing `ProcessEpisodeAsync` which is already audited.
- **`DeleteLinkedMovieItem()` (lines 605-627)**: Null + `Guid.Empty` guard. Null check on `GetItemById`. Exception caught and logged. Identical pattern to `CleanupTask.DeleteItemWithFiles` (previously audited).
- **`CleanupTask.ExecuteAsync` (CleanupTask.cs lines 47-93)**: Now async. `EnforceIgnoreList()` called synchronously after validation loop — safe since Jellyfin task scheduler serializes task execution. `ProcessForceLinksAsync` properly awaited with `ConfigureAwait(false)`. Progress split 80/90/100 is clean.
- **`removeSelectedPairs` (configPage.html lines 1031-1061)**: Fires parallel `ApiClient.ajax` calls with countdown pattern (`remaining--`). JS is single-threaded so no race condition on the counter. `loadPairs()` called exactly once when all complete. Failed count tracked and reported.
- **Checkbox selection + `updateSelectionUI` (lines 862-880, 1019-1029)**: `selectedPairIds` is a `Set`. `loadPairs` prunes stale IDs after refresh. `chkSelectAll` iterates only visible checkboxes. All DOM rendering uses `textContent` and DOM property assignment — no innerHTML injection vectors.
- **`pair.Id` usage**: Serialized via `JSON.stringify({ PairId: pairId })` — no XSS risk. `dataset.pairId` uses DOM property assignment. GUIDs from server, not user input.
- **Three-dot menu (lines 1324-1333)**: `stopPropagation` on menu button, document click closes dropdown. Clean pattern, no injection.

### Areas Reviewed (No New Issues)

- **Authorization**: All API calls go through `ApiClient.ajax` with Jellyfin auth. Server endpoints gated by `Policies.RequiresElevation` — intact.
- **PairStore**: Atomic writes, lock-guarded — intact.
- **All previously audited infrastructure**: Unchanged.

---
---

## Audit: 2026-05-23 (Session 6)

**Scope**: Full codebase review. Changes since last audit: `NotFoundCache` → `ApiResponseCache` (caches all API responses, not just 404s), `MetadataCacheDays` config (default 7, renamed from `NotFoundCacheDays`), `AllowOvaLinking` config + TVDB OVA tag matching, `ClearMetadataCache` API endpoint + UI button, config page label/description updates.
**Triggered by**: Audit following metadata cache expansion and OVA linking feature.

---

### Confirmed Issues

None found.

---

### Previous Audit Items — Status Check

| Previous Finding | Current Status |
|---|---|
| NotFoundCache timer race condition (Session 3 #1-2) | Carried forward to ApiResponseCache — same lock + Interlocked.Exchange + _disposed pattern. Still fixed. |
| RemoveForceLinkedPairs N writes (Session 3 #3) | Still fixed |
| CleanupTask AutoDeleteOnRemoval (Session 2 #1) | Still fixed |
| RemoveAllLinks N writes (2026-05-22 #1) | Still fixed |
| FindExistingMovie cross-library match (2026-05-22 #2) | Still fixed |
| All 5 findings from 2026-05-21 | Still fixed |
| All 3 findings from 2025-05-20 | Still fixed |

All 7 false positives from the 2025-05-20 audit remain valid.

---

### New Code Reviewed

- **`ApiResponseCache`**: Replaces `NotFoundCache`. Stores response bodies alongside timestamps (`CacheEntry` with `t` and `r` fields). `ConcurrentDictionary<string, CacheEntry>` for thread-safe reads. Same `lock(_saveLock)` + `Interlocked.Exchange` + `_disposed` timer safety pattern. Atomic disk writes preserved. `Clear()` method properly clears dictionary and schedules save. New cache file `api_cache.json` — old `notfound_cache.json` is orphaned (harmless).
- **`ClearMetadataCache` endpoint**: Gated by `Policies.RequiresElevation`. Calls `_apiCache.Clear()`, returns count. No destructive side effects beyond cache.
- **`AllowOvaLinking` config**: Simple boolean, default false. TVDB lookup extracts `Special Category` tag value, checks for "Movies" (always) or "OVA" (when enabled). No new attack surface.
- **Config page JS**: `MetadataCacheDays` load uses `!= null` (not `||`) to preserve 0. Save uses `isNaN` fallback to 7. `AllowOvaLinking` checkbox follows existing load/save pattern. Clear cache button handler follows existing AJAX pattern.
- **TMDB/TVDB `SendWithRetryAsync`**: Cache check uses `IsCached()` then `Get()` — two dictionary lookups but negligible overhead. Successful responses cached with `Add(key, body)`. 404s cached with `Add(key, null)`. Callers already handle null returns correctly.

### Areas Reviewed (No New Issues)

- **All API endpoints**: `Policies.RequiresElevation` on controller class — intact. New `ClearMetadataCache` covered.
- **PairStore**: Atomic writes, lock-guarded, backup/restore, UpsertMany, RemoveMany — intact.
- **WatchSyncService**: Reentrancy guard, event lifecycle — intact.
- **LibraryEventHandler**: CancellationTokenSource lifecycle, cascade prevention, AutoDeleteOnRemoval check — intact.
- **HardLinkService**: SanitizeFileName, path traversal guard, P/Invoke — intact.
- **ApiResponseCache**: Timer thread safety (lock + Interlocked.Exchange + _disposed flag), atomic disk writes, expired entry pruning — all correct.
- **Rate limiting**: SemaphoreSlim with try/finally on TMDB (30) and TVDB (5) — intact.
- **Response size limits**: `HasValue` + post-read check on both services — intact.
- **TVDB auth**: Double-checked locking with SemaphoreSlim + volatile token + Interlocked expiry — intact.
- **Config page**: DOM property assignment (no innerHTML for user data), debounce, library load ordering, sort with `.slice()` — all correct.
- **ConfigSnapshot**: Immutable copy of mutable collections at scan start — intact.
- **AggregatedLookupService**: PrimaryProvider conflict resolution, ID merging — intact.

---
---

## Audit: 2026-05-22 (Session 5 — Pre-release v1.0.9.0)

**Scope**: Full codebase review of all .cs files, configPage.html. No code changes since Session 4 (v1.0.8.0).
**Triggered by**: Pre-release audit for v1.0.9.0.

---

### Confirmed Issues

None found. Codebase is unchanged since Session 4.

---

### Previous Audit Items — Status Check

| Previous Finding | Current Status |
|---|---|
| NotFoundCache timer race condition (Session 3 #1-2) | Still fixed |
| RemoveForceLinkedPairs N writes (Session 3 #3) | Still fixed |
| CleanupTask AutoDeleteOnRemoval (Session 2 #1) | Still fixed |
| RemoveAllLinks N writes (2026-05-22 #1) | Still fixed |
| FindExistingMovie cross-library match (2026-05-22 #2) | Still fixed |
| All 5 findings from 2026-05-21 | Still fixed |
| All 3 findings from 2025-05-20 | Still fixed |

All 7 false positives from the 2025-05-20 audit remain valid.

---

### Areas Reviewed (No New Issues)

- **All API endpoints**: `Policies.RequiresElevation` on controller class — intact.
- **PairStore**: Atomic writes, lock-guarded, backup/restore, UpsertMany, RemoveMany — intact.
- **WatchSyncService**: Reentrancy guard, event lifecycle — intact.
- **LibraryEventHandler**: CancellationTokenSource lifecycle, cascade prevention, AutoDeleteOnRemoval check — intact.
- **HardLinkService**: SanitizeFileName, path traversal guard, P/Invoke — intact.
- **NotFoundCache**: Timer thread safety (lock + Interlocked.Exchange + _disposed flag), atomic disk writes, expired entry pruning — intact.
- **Rate limiting**: SemaphoreSlim with try/finally on TMDB (30) and TVDB (5) — intact.
- **Response size limits**: `HasValue` + post-read check on both services — intact.
- **TVDB auth**: Double-checked locking with SemaphoreSlim + volatile token + Interlocked expiry — intact.
- **Config page**: DOM property assignment (no innerHTML for user data), debounce, library load ordering, sort with `.slice()` — all correct.
- **ConfigSnapshot**: Immutable copy of mutable collections at scan start — intact.
- **AggregatedLookupService**: PrimaryProvider conflict resolution, ID merging — intact.

---
---

## Audit: 2026-05-22 (Session 4)

**Scope**: Full codebase review of all .cs files, configPage.html. Changes since last audit: `PrimaryProvider` config option (`MetadataProviderType` enum, config property, `AggregatedLookupService` conflict resolution refactored to be config-driven), `configPage.html` Primary Provider dropdown, simplified API key labels.
**Triggered by**: Pre-release audit following Primary Provider feature addition.

---

### Confirmed Issues

None found.

---

### Previous Audit Items — Status Check

| Previous Finding | Current Status |
|---|---|
| NotFoundCache timer race condition (Session 3 #1-2) | Still fixed |
| RemoveForceLinkedPairs N writes (Session 3 #3) | Still fixed |
| CleanupTask AutoDeleteOnRemoval (Session 2 #1) | Still fixed |
| RemoveAllLinks N writes (2026-05-22 #1) | Still fixed |
| FindExistingMovie cross-library match (2026-05-22 #2) | Still fixed |
| All 5 findings from 2026-05-21 | Still fixed |
| All 3 findings from 2025-05-20 | Still fixed |

All 7 false positives from the 2025-05-20 audit remain valid.

---

### New Code Reviewed

- **`MetadataProviderType` enum**: `[JsonStringEnumConverter]` ensures human-readable serialization. Default `Tmdb = 0` is correct for backwards compatibility (existing configs without the field deserialize to 0).
- **`AggregatedLookupService` conflict resolution**: Switch expression with `_ =>` default falls through to TMDB, making it safe for future enum additions. When primary wins, IDs from the non-primary result are merged — both TMDB and TVDB IDs are preserved on the final `MovieMatch`. Correct behavior.
- **`configPage.html` PrimaryProvider load**: Handles both integer `1` and string `"Tvdb"` deserialization. Save sends string value which `JsonStringEnumConverter` handles correctly.
- **Both providers always called in parallel**: `Task.WhenAll` in `LookupAsync` — secondary with no API key returns `null` immediately (no wasted request). Needed for dual confirmation mode and ID merging.

### Areas Reviewed (No New Issues)

- **All API endpoints**: `Policies.RequiresElevation` on controller class — intact.
- **PairStore**: Atomic writes, lock-guarded, backup/restore, UpsertMany, RemoveMany — intact.
- **WatchSyncService**: Reentrancy guard, event lifecycle — intact.
- **LibraryEventHandler**: CancellationTokenSource lifecycle, cascade prevention, AutoDeleteOnRemoval check — intact.
- **HardLinkService**: SanitizeFileName, path traversal guard, P/Invoke — intact.
- **NotFoundCache**: Timer thread safety (lock + Interlocked.Exchange + _disposed flag), atomic disk writes, expired entry pruning — intact.
- **Rate limiting**: SemaphoreSlim with try/finally on TMDB (30) and TVDB (5) — intact.
- **Response size limits**: `HasValue` + post-read check on both services — intact.
- **TVDB auth**: Double-checked locking with SemaphoreSlim + volatile token + Interlocked expiry — intact.
- **Config page**: DOM property assignment (no innerHTML for user data), debounce, library load ordering, sort with `.slice()` — all correct.
- **ConfigSnapshot**: Immutable copy of mutable collections at scan start — intact.

---
---

## Audit: 2026-05-22 (Session 3)

**Scope**: Full codebase review of all .cs files, configPage.html. Changes since last audit: `NotFoundCache` disk persistence, UI table overflow fix, Created column date+time, column width rebalancing.
**Triggered by**: Pre-release audit following NotFoundCache addition and UI fixes.

---

### Confirmed Issues

#### 1. `NotFoundCache.ScheduleSave` disposes Timer without thread safety (LOW — race condition)
- **File**: `Lookup/NotFoundCache.cs` lines 54-59
- **Issue**: `ScheduleSave()` called `_saveTimer?.Dispose()` then created a new Timer without synchronization. Concurrent `Add()` calls could race on the timer field — one thread disposes a timer the other just created. Similarly, `Dispose()` was not coordinated with `ScheduleSave()`, risking timer creation on an already-disposed object.
- **Status**: Fixed — `ScheduleSave()` and `Dispose()` now both acquire `_saveLock`. Timer swap uses `Interlocked.Exchange` for atomic replacement. A `_disposed` flag prevents timer creation after shutdown.

#### 2. `NotFoundCache.Dispose` not thread-safe with concurrent `Add` (LOW — race condition)
- **File**: `Lookup/NotFoundCache.cs` lines 45-52
- **Issue**: `Dispose()` checked `_dirty` and called `SaveToDisk()` without synchronization. A concurrent `Add()` could set `_dirty = true` after the check and create a new timer on a disposed object.
- **Status**: Fixed — `Dispose()` now holds `_saveLock`, sets `_disposed = true`, and calls `SaveToDiskCore()` directly (lock-free inner method) to avoid deadlock.

#### 3. `RemoveForceLinkedPairs` performs N individual `Remove` calls (LOW — efficiency)
- **File**: `Api/SpecialToMovieController.cs` lines 156-164
- **Issue**: Each `_pairStore.Remove(pair.Id)` in the loop triggered a full JSON serialize + atomic file write. Same class of bug previously fixed for `RemoveAllLinks` and `ClearDatabase`.
- **Status**: Fixed — added `RemoveMany(IEnumerable<Guid>)` to `PairStore` that batches all removals under a single lock + `Save()`. `RemoveForceLinkedPairs` collects pairs to remove and calls `RemoveMany` once.

---

### Previous Audit Items — Status Check

| Previous Finding | Current Status |
|---|---|
| CleanupTask AutoDeleteOnRemoval (Session 2 #1) | Still fixed |
| RemoveAllLinks N writes (2026-05-22 #1) | Still fixed |
| FindExistingMovie cross-library match (2026-05-22 #2) | Still fixed |
| All 5 findings from 2026-05-21 | Still fixed |
| All 3 findings from 2025-05-20 | Still fixed |

All 7 false positives from the 2025-05-20 audit remain valid.

---

### Areas Reviewed (No New Issues)

- **NotFoundCache disk I/O**: Atomic write (`.tmp` + `File.Move`), expired entries pruned on load and save.
- **NotFoundCache shared singleton**: TMDB uses `StripApiKey()` for cache keys; TVDB uses raw URLs (no secrets in URL). Both correct.
- **Table overflow CSS**: `text-overflow: ellipsis` on `td` only (not `th`), hover-expand via `tbody td:hover`. No injection risk.
- **All API endpoints**: `Policies.RequiresElevation` on controller class — intact.
- **PairStore**: Atomic writes, lock-guarded, backup/restore — intact.
- **WatchSyncService**: Reentrancy guard, event lifecycle — intact.
- **LibraryEventHandler**: CancellationTokenSource lifecycle, cascade prevention — intact.
- **HardLinkService**: SanitizeFileName, path traversal guard, P/Invoke — intact.
- **Rate limiting**: SemaphoreSlim with try/finally on TMDB (30) and TVDB (5) — intact.
- **Response size limits**: `HasValue` + post-read check — intact.
- **TVDB auth**: Double-checked locking — intact.

---
---

## Audit: 2026-05-22 (Session 2)

**Scope**: Full codebase review of all .cs files, configPage.html. Second pass after applying fixes from earlier audit (UpsertMany, FindExistingMovie library filtering).
**Triggered by**: Pre-release audit continuation.

---

### Confirmed Issues

#### 1. `CleanupTask` deletes movie files without checking `AutoDeleteOnRemoval` (MEDIUM — correctness)
- **File**: `Tasks/CleanupTask.cs` lines 106-110
- **Issue**: When a source episode no longer exists, `CleanupTask.ValidatePair` unconditionally called `DeleteItemWithFiles(pair.MovieItemId)` for non-dry-run, non-existing-movie pairs — ignoring the user's `AutoDeleteOnRemoval` setting. By contrast, `LibraryEventHandler.OnItemRemoved` correctly gates deletion behind `config.AutoDeleteOnRemoval`. This meant the cleanup task (running every 12 hours) would silently delete movie files even when the user had auto-delete disabled.
- **Status**: Fixed — added `autoDelete` parameter to `ValidatePair`, passed from `config.AutoDeleteOnRemoval`. Deletion now only occurs when the user has explicitly enabled it, matching `LibraryEventHandler` behavior.

---

### Previous Audit Items — Status Check

| Previous Finding | Current Status |
|---|---|
| RemoveAllLinks N writes (2026-05-22 #1) | Still fixed |
| FindExistingMovie cross-library match (2026-05-22 #2) | Still fixed |
| All 5 findings from 2026-05-21 | Still fixed |
| All 3 findings from 2025-05-20 | Still fixed |

All 7 false positives from the 2025-05-20 audit remain valid.

---

### Areas Reviewed (No New Issues)

- **All API endpoints**: `Policies.RequiresElevation` on controller class — covers all endpoints including RunCleanup.
- **PairStore**: Atomic writes, lock-guarded mutations, backup/restore, UpsertMany — all intact.
- **WatchSyncService**: Reentrancy guard, event lifecycle — intact.
- **LibraryEventHandler**: CancellationTokenSource lifecycle, cascade prevention, AutoDeleteOnRemoval check — intact.
- **HardLinkService**: SanitizeFileName, path traversal guard, P/Invoke, StatBuf padding — intact.
- **Rate limiting**: SemaphoreSlim with try/finally on TMDB (30) and TVDB (5) — intact.
- **Response size limits**: `HasValue` + post-read check on both services — intact.
- **TVDB auth**: Double-checked locking with SemaphoreSlim + volatile token + Interlocked expiry — correct.
- **Config page**: DOM property assignment (no innerHTML for user data), debounce, library load ordering, sort with `.slice()` — all correct.
- **ConfigSnapshot**: Immutable copy of mutable collections at scan start — intact.
- **CleanupTask batch queries**: Pre-fetches all episodes and movies into dictionaries — intact.

---

### Build Note

Pre-existing build error in `WatchSyncService.cs:78` — `IUserManager.Users` property not found. This appears to be a Jellyfin SDK API change (may need `GetUsers()` instead). Not introduced by any audit fix.

---
---

## Audit: 2026-05-22

**Scope**: Full codebase review of all .cs files, configPage.html, manifest.json. Focus on changes since last audit: `ITaskManager` refactor for RunFullScan/RunCleanup, column sorting, pagination fixes, UI text changes, cleanup button.
**Triggered by**: Pre-release audit following ITaskManager refactor, UI improvements, and new RunCleanup endpoint.

---

### Confirmed Issues

#### 1. `RemoveAllLinks` performs N individual `Upsert` calls (LOW — efficiency)
- **File**: `Api/SpecialToMovieController.cs` lines 74-89
- **Issue**: Each pair in the RemoveAllLinks loop called `_pairStore.Upsert(pair)` individually, triggering a full JSON serialize + atomic file write per pair. With 200 pairs, that's 200 file writes. Same class of bug as the ClearDatabase issue fixed in the 2026-05-21 audit.
- **Status**: Fixed — added `UpsertMany(IEnumerable<LinkedPair>)` to `PairStore` that batches all mutations under a single lock + `Save()`. `RemoveAllLinks` collects modified pairs and calls `UpsertMany` once.

#### 2. `FindExistingMovie` matches movies across all libraries (LOW — correctness)
- **File**: `Services/SpecialDetectionService.cs` lines 361-377
- **Issue**: When `cachedMovies` is provided (full scan path), the list contains ALL movies across all libraries. `FindExistingMovie` matched by provider ID against this full list. If two libraries contained the same movie (different Jellyfin items, same TMDB/IMDB ID), it could match a movie in the wrong library.
- **Status**: Fixed — `FindExistingMovie` now accepts `virtualFolders` and filters the cached movie list to only movies whose path starts with a destination library location. The event handler path (uncached) still uses `ParentId` filtering via Jellyfin's query API.

---

### Previous Audit Items — Status Check

| Previous Finding | Current Status |
|---|---|
| Stale confirm dialog text (2026-05-21 #1) | Still fixed |
| Concurrent scan guard on RunFullScan (2026-05-21 #2) | **Superseded** — `ITaskManager.QueueScheduledTask<FullScanTask>()` now delegates concurrency to Jellyfin's task scheduler. The `static int _scanRunning` + `Interlocked` guard was removed. |
| Content-Length chunked bypass (2026-05-21 #3) | Still fixed |
| ClearDatabase N writes (2026-05-21 #4) | Still fixed |
| RemoveAllLinks pre-existing pair bug (2026-05-21 #5) | Still fixed |
| Info disclosure in test endpoints (2025-05-20 #1) | Still fixed |
| FindExistingMovie N+1 (2025-05-20 #2) | Still fixed |
| Search box debounce (2025-05-20 #3) | Still fixed |

All 7 false positives from the 2025-05-20 audit remain valid. No changes needed.

---

### Areas Reviewed (No New Issues)

- **ITaskManager refactor**: `RunFullScan` and `RunCleanup` both use `QueueScheduledTask<T>()`. Delegates concurrency, progress, and error handling to Jellyfin's scheduler.
- **RunCleanup endpoint**: New endpoint follows the same pattern as RunFullScan. Gated by `Policies.RequiresElevation`. No input parameters.
- **Column sorting (configPage.html)**: Client-side only. Uses `.slice()` to avoid mutating source array. No injection risk.
- **Pagination fix**: Removed `pairsPage = 0` from `loadPairs()`. Page clamped in `renderPairsPage()`.
- **Library load ordering fix**: `loadPairs()` moved inside `getVirtualFolders().then()` callback.
- **Display text changes**: "DryRun" → "Dry Run", "Pre-existing" → "Pre Existing". Display-only; filter logic uses integer values and unchanged option values.
- **PairStore**: Atomic writes, lock-guarded mutations, backup/restore — all intact. New `UpsertMany` follows same lock + Save pattern.
- **WatchSyncService**: Reentrancy guard, event lifecycle — intact.
- **LibraryEventHandler**: CancellationTokenSource lifecycle, cascade prevention — intact.
- **HardLinkService**: SanitizeFileName, path traversal guard, P/Invoke — intact.
- **Rate limiting**: SemaphoreSlim with try/finally on TMDB (30) and TVDB (5) — intact.
- **Response size limits**: `HasValue` + post-read check — intact.
- **All API endpoints**: `Policies.RequiresElevation` on controller class — covers all endpoints including new `RunCleanup`.

---

### Recommendations for Future Audits

- Verify `ITaskManager.QueueScheduledTask` behavior if Jellyfin SDK updates — confirm duplicate queue requests are handled gracefully.
- Monitor whether the destination library path filtering in `FindExistingMovie` correctly handles libraries with multiple content locations.

---
---

## Audit: 2026-05-21

**Scope**: Full codebase review of all .cs files, configPage.html, and fixes from this session (RemoveAllLinks pre-existing movie bug, cache default bump, UI text updates).
**Triggered by**: Pre-release audit following RemoveAllLinks bug fix and minor tweaks.

---

### Confirmed Issues

#### 1. RemoveAllLinks confirm dialog text was stale (LOW)
- **File**: `Configuration/configPage.html` line 977
- **Issue**: The `confirm()` message still said "transition pairs back to DryRun status" after the code was changed to skip pre-existing movie pairs entirely. Misleading to the user.
- **Status**: Fixed — dialog now says "plugin-created hard link folders" and explicitly states pre-existing movie links are not affected.

#### 2. No concurrent scan guard on RunFullScan API endpoint (MEDIUM)
- **File**: `Api/SpecialToMovieController.cs` line 102
- **Issue**: `RunFullScan` fired `Task.Run` with no concurrency guard. Multiple button clicks could launch parallel scans, causing duplicate pairs and error states. The scheduled `FullScanTask` goes through Jellyfin's task scheduler (which prevents concurrent runs), but the API endpoint bypassed that.
- **Status**: Fixed — added `static int _scanRunning` with `Interlocked.CompareExchange`. Concurrent requests return 409 Conflict. The flag is cleared in a `finally` block to handle exceptions.

#### 3. Content-Length check bypassed by chunked transfer encoding (LOW)
- **Files**: `Lookup/TmdbLookupService.cs` line 318, `Lookup/TvdbLookupService.cs` line 379
- **Issue**: `response.Content.Headers.ContentLength` is null for chunked responses. The comparison `null > 1_000_000` evaluates to false in C#, silently skipping the size check and allowing `ReadAsStringAsync` to read unbounded content into memory. Mitigated by TMDB/TVDB being well-known APIs with small JSON payloads.
- **Status**: Fixed — now checks `ContentLength.HasValue` before the header comparison, then validates `body.Length > 1_000_000` after reading as a safety net for chunked responses.

#### 4. ClearDatabase performed N individual saves (LOW)
- **File**: `Api/SpecialToMovieController.cs` line 193
- **Issue**: Each `_pairStore.Remove(pair.Id)` call serialized the full pair list to JSON and wrote to disk. With hundreds of pairs, this meant hundreds of file writes for a single button click.
- **Status**: Fixed — added `Clear()` method to `PairStore` that empties the list and calls `Save()` exactly once. `ClearDatabase` endpoint now uses it.

#### 5. RemoveAllLinks reset pre-existing movie pairs (MEDIUM — bug fix, not audit finding)
- **File**: `Api/SpecialToMovieController.cs` lines 73-88
- **Issue**: The `IsExistingMovie` check only gated `DeleteItemWithFiles`, but the status reset (DryRun) and `MovieItemId` wipe ran unconditionally for all pairs. This broke pre-existing movie pairs — they lost their `MovieItemId` and dropped to DryRun, killing watch sync.
- **Status**: Fixed — pre-existing movie pairs are now fully skipped with `continue`.

---

### Previous Audit Items — Status Check

| Previous Finding | Current Status |
|---|---|
| Information disclosure in test endpoints (v1.0.5) | Still fixed |
| FindExistingMovie N+1 queries (v1.0.5) | Still fixed |
| Search box debounce (v1.0.5) | Still fixed |
| `DeleteMovieFolder` safety net (flagged for future review) | No longer exists — fully removed in deletion refactor |

All 7 false positives from the 2025-05-20 audit remain valid false positives. No changes needed.

---

### Areas Reviewed (No New Issues)

- **PairStore atomic writes**: Temp file + rename pattern still intact. New `Clear()` method follows the same lock + Save pattern.
- **WatchSyncService reentrancy guard**: ConcurrentDictionary key `{userId}:{itemId}` — still correct.
- **Event handler lifecycle**: Subscribe/unsubscribe in Start/Stop/Dispose — no leak.
- **Rate limiting**: SemaphoreSlim with try/finally on both TMDB (30) and TVDB (5).
- **Response size limits**: Now correctly handles both Content-Length header and post-read body size.
- **IsExistingMovie guard**: Correctly applied in RemoveAllLinks, LibraryEventHandler, and CleanupTask.
- **TVDB auth**: Double-checked locking with SemaphoreSlim + volatile token + Interlocked expiry — correct.
- **All API endpoints**: Properly gated with `Policies.RequiresElevation`.
- **SanitizeFileName + path traversal guards**: Solid.
- **Hard link creation P/Invoke**: Correct cross-platform usage.
- **CleanupTask batch queries**: Pre-fetches all episodes and movies into dictionaries.

---

### Recommendations for Future Audits

- Monitor Jellyfin SDK updates for changes to `DeleteOptions` behavior or new plugin middleware capabilities (relevant to the Next Up duplicate issue #1).
- If the plugin grows to support user-facing input (e.g., search/filter via API), add input validation and escaping at that point.
- The `RunFullScan` API guard is per-process only (static field). If Jellyfin ever runs multiple plugin instances (unlikely), it would need a distributed lock.

---
---

## Audit: 2025-05-20

**Scope**: Full codebase review of all .cs files, configPage.html, and recent deletion refactor changes.
**Triggered by**: Pre-release audit for v1.0.5.0 (deletion refactor — delegate file deletion to Jellyfin via `DeleteItem(item, DeleteFileLocation = true)`)

---

### Confirmed Issues

#### 1. Information disclosure in API test endpoints (LOW)
- **Files**: `Api/SpecialToMovieController.cs` lines 230, 266
- **Issue**: `TestTmdb` and `TestTvdb` return raw `ex.Message` to the client (`$"Connection failed: {ex.Message}"`). Exception messages can leak internal infrastructure details (hostnames, file paths, stack traces).
- **Mitigated by**: Endpoint requires `Policies.RequiresElevation` (admin-only access).
- **Recommendation**: Return a generic "Connection failed" message instead of the raw exception.
- **Status**: Fixed — returns generic "Connection failed" message; full exception logged server-side at warning level.

#### 2. FindExistingMovie queries library without cache on single-episode processing (MEDIUM)
- **Files**: `Services/SpecialDetectionService.cs` lines 356-363
- **Issue**: When `ProcessEpisodeAsync` is called from the event handler (single episode added), `cachedMovies` is null, so `FindExistingMovie` makes a full `GetItemList` query against the destination library. During a library scan that adds many Season 0 episodes in sequence, this fires one query per episode.
- **Context**: The full scan path (`RunFullScanAsync`) correctly pre-fetches and passes cached movies. Only the event handler path is uncached.
- **Status**: Fixed — public `ProcessEpisodeAsync(Episode)` overload now pre-fetches the movie list before calling the internal method.

#### 3. Search box has no debounce (LOW)
- **Files**: `Configuration/configPage.html` lines 851-852
- **Issue**: Every keystroke in the pairs search box calls `renderPairsPage()`, which rebuilds the entire table DOM. With hundreds of pairs and rapid typing, this could cause UI jank.
- **Status**: Fixed — added 200ms debounce on the search input handler.

---

### False Positives

#### Language parameter injection in TMDB URLs
- **Files**: `Lookup/TmdbLookupService.cs` lines 88-90, 168-169
- **Flagged as**: Query parameter injection — `lang` not escaped in URL.
- **Why it's a false positive**: `lang` comes from `_configManager.Configuration.PreferredMetadataLanguage`, a Jellyfin server setting set by the admin. It is always a standard BCP-47 language code (e.g., "en", "fr", "de"). Not user-controlled input.

#### TVDB bearer token exposed in logs
- **Files**: `Lookup/TvdbLookupService.cs` line 358
- **Flagged as**: Token exposure via URL logging.
- **Why it's a false positive**: The bearer token is sent in HTTP headers (`Authorization: Bearer ...`), not in the URL. The logged URL (`{Url}`) contains only the API endpoint path with no secrets. TMDB service has a `StripApiKey()` utility because TMDB uses query-string API keys — TVDB does not have this problem.

#### Marshal.GetLastWin32Error() on POSIX
- **Files**: `HardLink/HardLinkService.cs` line 49
- **Flagged as**: Wrong error retrieval — `Marshal.GetLastWin32Error()` doesn't return errno on Unix.
- **Why it's a false positive**: The `LinkPosix` P/Invoke declaration uses `SetLastError = true` (line 238). In .NET, `SetLastError = true` on a `LibraryImport` causes the runtime to capture errno on Unix and expose it via `Marshal.GetLastWin32Error()`. This is documented .NET behavior and works correctly cross-platform.

#### Configuration race condition
- **Files**: `Services/SpecialDetectionService.cs` lines 47-54
- **Flagged as**: Config object could be replaced mid-read without locking.
- **Why it's a false positive**: The code already creates a `ConfigSnapshot.From(config)` at line 54, which copies all mutable collections (library mappings, force links, ignore list) into immutable snapshots. The initial null check at line 47 is a simple reference read that is atomic. The snapshot pattern is the correct mitigation.

#### API keys visible in config page DOM
- **Files**: `Configuration/configPage.html` lines 266-267
- **Flagged as**: Sensitive data exposure — API keys displayed in plain text inputs.
- **Why it's a false positive**: This is standard Jellyfin plugin behavior. Admin configuration pages display editable API keys so the admin can view/modify them. The entire config page is behind Jellyfin's admin authentication. Masking the keys would prevent admins from verifying they entered them correctly.

#### CSRF on POST requests
- **Files**: `Configuration/configPage.html` — all `ApiClient.ajax()` POST calls
- **Flagged as**: Missing CSRF tokens on destructive POST endpoints.
- **Why it's a false positive**: Jellyfin's `ApiClient` handles authentication via API tokens in request headers. All plugin POST endpoints are protected by `[Authorize(Policy = Policies.RequiresElevation)]`. Jellyfin's authentication framework provides CSRF-equivalent protection through its token-based auth model.

#### Path traversal on case-sensitive filesystems
- **Files**: `HardLink/HardLinkService.cs` line 119
- **Flagged as**: Case tricks could bypass path containment check on case-sensitive systems.
- **Why it's a false positive**: `BuildHardLinkPath` uses `StringComparison.OrdinalIgnoreCase` for the containment check (line 140). Additionally, path components are built entirely from sanitized movie titles via `SanitizeFileName()` — not from arbitrary user input. The destination library path comes from Jellyfin's own library configuration.

#### TOCTOU in hard link file operations
- **Files**: `HardLink/HardLinkService.cs` lines 27-31
- **Flagged as**: Directory created before full path validation; race window between validation and file creation.
- **Why it's a false positive**: The paths are constructed entirely by the plugin from Jellyfin library paths + sanitized movie titles. An attacker would need write access to the library directory to plant symlinks, at which point they already have more access than this vulnerability would grant. The threat model doesn't include attackers with filesystem access to the media library.

#### URL injection in config page links
- **Files**: `Configuration/configPage.html` lines 653, 670, 690, 699, 708
- **Flagged as**: Unvalidated item IDs injected into href attributes.
- **Why it's a false positive**: Item IDs come from Jellyfin's internal database (GUIDs). External IDs (IMDB, TMDB, TVDB) come from the plugin's own lookup services, not user input. The URLs are constructed with known-safe base URLs (imdb.com, themoviedb.org, thetvdb.com). Links use `target="_blank" rel="noopener noreferrer"` for external URLs.

#### Unescaped error messages in title attribute
- **Files**: `Configuration/configPage.html` line 748
- **Flagged as**: XSS via `pair.ErrorMessage` in `tdStatus.title`.
- **Why it's a false positive**: The `title` attribute is set via `tdStatus.title = pair.ErrorMessage` (DOM property assignment), not via `innerHTML` or `setAttribute`. DOM property assignment for `title` is inherently safe — the browser renders it as plain text in the tooltip, not as HTML. Error messages also come from the plugin's own code, not external input.

---

### Areas Reviewed (No Issues Found)

- **PairStore JSON persistence**: Uses atomic write (temp file + rename) with proper cleanup. File paths are deterministic from plugin data directory.
- **WatchSyncService reentrancy guard**: ConcurrentDictionary-based guard prevents infinite sync loops between paired items. Key format `{userId}:{itemId}` is unique. Guard is properly removed in finally block.
- **Event handler lifecycle**: `LibraryEventHandler` subscribes in `StartAsync`, unsubscribes in both `StopAsync` and `Dispose`. No leak risk.
- **Rate limiting on external APIs**: Both TMDB and TVDB services use `SemaphoreSlim` rate limiters with proper `WaitAsync`/`Release` in try/finally.
- **Response size limits**: Both lookup services check `ContentLength > 1_000_000` before reading response bodies. Prevents memory exhaustion from oversized responses.
- **Hard link creation P/Invoke**: Correct use of `LibraryImport` with `SetLastError = true` on both Windows (`CreateHardLinkW`) and Unix (`link`). Extended-length path prefix (`\\?\`) used on Windows to handle long paths.
- **CleanupTask batch queries**: Pre-fetches all episodes and movies into dictionaries before iterating pairs, avoiding N+1 queries.
- **SanitizeFileName**: Strips invalid filename characters, collapses `..` sequences, guards against Windows reserved device names (CON, NUL, etc.).
- **Deletion refactor (this session)**: All item deletion now delegates to `_libraryManager.DeleteItem(item, DeleteFileLocation = true)`. Pairs are removed from the store before calling `DeleteItem` to prevent cascading `ItemRemoved` events. The only remaining filesystem deletion is `DeleteMovieFolder` (called only when the movie item is already gone from Jellyfin and leftover files may remain).

---

### Recommendations for Future Audits

- Re-check `DeleteMovieFolder` usage — it remains as a safety net for orphaned folders when a movie item is removed from Jellyfin without file deletion. If Jellyfin changes its deletion behavior, this could become unnecessary or incorrect.
- Monitor Jellyfin SDK updates for changes to `DeleteOptions` behavior or new plugin middleware capabilities (relevant to the Next Up duplicate issue #1).
- If the plugin grows to support user-facing input (e.g., search/filter via API), add input validation and escaping at that point.
