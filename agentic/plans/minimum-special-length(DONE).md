# Minimum Special Length — Implementation Plan

| | |
| --- | --- |
| **Status** | **DONE — implemented 2026-09-30**, audited and verified. Unreleased — no version bump yet. Decisions: [§8](#8-decisions). Plan audit: [§6](#6-plan-audit). Where the build diverged: [§9](#9-as-built-notes) |
| **Source** | New request; not previously in [`IDEAS.md`](../IDEAS.md) |
| **Effort** | ~half day including the `ItemUpdated` hook, docs and harness checks |
| **Release shape** | Minor bump (new config option) — `2.1.0.0` per the version convention |

---

## 1. Goal

A setting, **Minimum special length (minutes)**, below which a Season 0 special is never linked to a
movie automatically. Short specials — featurettes, trailers, recaps, behind-the-scenes clips — are the
main source of false matches, because TVDB and TMDB sometimes associate them with the movie they are
about.

Success looks like: a 12-minute "Making of El Camino" special is never turned into a linked movie,
while the 2-hour *El Camino* special still is.

The setting affects **new links only**. Every pair that already exists, of any status, is left alone.

---

## 2. Verified facts the design rests on

- **All automatic detection flows through one method**:
  `SpecialDetectionService.ProcessEpisodeAsync` (private overload). It is reached from
  `LibraryEventHandler.OnItemAdded` (real time), `RunFullScanAsync` (main loop),
  `PromoteDryRunPairsAsync`, and `ProcessForceLinksAsync` (full scan and `CleanupTask`). One gate
  there covers every path.
- **The episode's length is `BaseItem.RunTimeTicks` (`long?`)**, populated by Jellyfin's media probe.
  Nothing in the plugin reads it today.
- **On the real-time `ItemAdded` path the length is not known yet.** Verified against the Jellyfin
  `v12.0` tag: `Folder.ValidateChildrenInternal2` calls `LibraryManager.CreateItems` (which raises
  `ItemAdded`) *before* `RefreshMetadataRecursive`, the refresh that runs the probe.
- **Jellyfin raises `ILibraryManager.ItemUpdated` when that refresh saves the item.** Verified at
  `v12.0`: `MetadataService.RefreshMetadata` always saves on an item's first refresh
  (`isFirstRefresh` in the save condition) through `UpdateToRepositoryAsync`, which reaches
  `LibraryManager.UpdateItemsAsync`, which raises `ItemUpdated` for each library item. The event is
  present on the pinned `Jellyfin.Controller` 12.0.0 `ILibraryManager`. That is the "length is now
  known" signal §3.3 waits on.
- **`ItemUpdated` handlers run inline**, inside Jellyfin's save loop, once per item per save, and
  fire for every kind of update (images, user edits, refreshes). A handler must be cheap and must
  hand real work to a background task. Jellyfin catches a throw, but logs it as an error.
- **The plugin has no existing "file still arriving" handling.** The closest precedent is the
  `Pending` pair, which waits for Jellyfin to index the hard link and is promoted when it does. The
  wait here follows the same idea: hold, and act when Jellyfin reports the missing fact.
- **Pairs are only created inside `ProcessEpisodeAsync`**, and a skipped episode leaves no record,
  so the next full scan re-evaluates it from scratch. That makes both "too short" and "not known yet"
  non-permanent outcomes.

---

## 3. Changes by file

### 3.1 `Configuration/PluginConfiguration.cs`

```csharp
public int MinimumSpecialLengthMinutes { get; set; } = 40;
```

**Default 40 minutes** ([D5](#8-decisions)). It is the feature-length line used by the Academy, the
AFI and the BFI (a film "over 40 minutes"), so it is conservative in the direction that matters: it
does not block real films, including short anime features and made-for-TV films in the 45–60 minute
range, while it does catch featurettes, trailers, recaps, deleted scenes, shorts and most 20–30 minute
bonus episodes. A special that is a real film but runs under 40 minutes can still be force-linked.

Any value `<= 0` disables the check. No upper clamp server side; the UI caps entry at 600.

An upgraded install has no stored value for the property and so picks up 40. That only changes
**new** detection (§4), which is the intent; it goes in the release changelog.

### 3.2 `Services/SpecialDetectionService.cs`

1. **`ConfigSnapshot` gains `MinimumSpecialLengthMinutes`**, copied in `From()`, so a single scan
   uses one threshold throughout even if the setting is saved mid-scan.
2. **A pure public static helper**, testable from the audit harness without a server:

   ```csharp
   public enum LengthCheck { Allowed, TooShort, Unknown }

   public static LengthCheck CheckMinimumLength(long? runTimeTicks, int minimumMinutes)
   ```

   - `minimumMinutes <= 0` → `Allowed`
   - `runTimeTicks` null or `<= 0` → `Unknown`
   - `runTimeTicks < (long)minimumMinutes * TimeSpan.TicksPerMinute` → `TooShort`
   - otherwise `Allowed` — a special exactly at the minimum passes.
3. **The gate in `ProcessEpisodeAsync`** guards only the creation of a new hard-linked movie
   ([D2](#8-decisions)). It sits *after* the lookup, the dual-confirmation check and the
   existing-movie match, and *before* the dry-run branch:

   ```text
   lookup → dual confirmation → movie already in library? → pair it (no gate)
                                                          → GATE → dry run / hard link
   ```

   - A force link for the episode exists → **gate skipped** ([D1](#8-decisions)).
   - A match to a movie already in the destination library → paired as today; the gate is never
     reached and the length is never consulted.
   - **The length is read fresh**: when the `Episode` instance the caller passed in has no
     `RunTimeTicks`, the gate re-reads it with `_libraryManager.GetItemById(episode.Id)` before
     deciding. A full scan's episode list is fetched once at scan start and an `ItemAdded` instance
     predates the probe, so either can be stale by the time the gate is reached.
   - `TooShort` → return, no pair stored. Logged at `Information` on the real-time path (one line
     per new special, so a user can see why it did not link) and at `Debug` during a full scan,
     where the summary line (item 4) covers it.
   - `Unknown` → `LogDebug("length of {Key} not known yet, waiting")`, record the episode as
     **awaiting length** (§3.3), return. No pair stored.

   It sits before the dry-run branch as well, since a `DryRun` pair means "would create a hard
   link" and must obey the same rule. `ProcessEpisodeAsync` returns the gate outcome to its caller
   (a small result enum on the private overload) so the event handler knows whether to keep waiting.

   Placing it after the lookup means a short special still costs the lookup, exactly as it does
   today; `ApiResponseCache` absorbs repeats. It saves nothing, but costs nothing extra either.
4. **Full-scan summary**: `RunFullScanAsync` counts gate rejections and logs one `Information`
   line — `"Full scan: skipped {Short} specials below the minimum length, {Unknown} still waiting for
   a length"` — only when either count is non-zero. Per-episode lines stay at `Debug`.
5. **Existing pairs are never touched** ([D3](#8-decisions)). One path needs a deliberate exemption:
   `PromoteDryRunPairsAsync` turns an existing `DryRun` pair into a real link by **removing it and
   re-running `ProcessEpisodeAsync`**. Left alone, the gate would silently drop every existing
   dry-run pair below the minimum the first time dry run is switched off. So the private overload
   gains a `bool applyMinimumLength` parameter, **`false` from `PromoteDryRunPairsAsync`** and `true`
   everywhere else. No other path re-creates an existing pair: `CleanupTask.ValidatePair` repairs
   hard links and promotes `Pending`/`Error` pairs without calling `ProcessEpisodeAsync`.
6. **One detection per episode at a time** ([D7](#8-decisions)). A
   `ConcurrentDictionary<Guid, byte> _inFlight` on the service (a singleton, so one set covers the
   real-time handlers and the scheduled tasks). The private `ProcessEpisodeAsync` overload takes the
   claim with `TryAdd(episode.Id)` at the top and releases it in a `finally`. A caller that loses
   the claim returns immediately without doing anything; the winner either creates the pair or
   leaves the episode for the next trigger.
   - **`PromoteDryRunPairsAsync` takes the claim *before* it removes the `DryRun` pair**, and holds
     it through the re-run (the private overload gains a `claimHeld` flag so it does not try to
     claim twice). If the claim were only taken inside the re-run, a real-time run could slip into
     the gap after the removal, find no pair, apply the minimum, and drop an existing short pair —
     breaking D3. If promotion cannot take the claim, it leaves that pair alone until the next scan.
   - The claim is released on every exit path, cancellation included, so an episode is never
     stranded as "in flight".
   - A caller that loses the claim puts nothing back into the awaiting set; the winner reads a fresh
     length and its outcome decides that.
7. **The awaiting set lives here, not in the event handler.** The gate is what discovers an
   unknown length, and the service cannot reach `LibraryEventHandler` (the dependency runs the other
   way). So `SpecialDetectionService` owns `ConcurrentDictionary<Guid, byte> _awaitingLength` and
   exposes two small methods: `TryTakeAwaiting(Guid)` for the handler, and `ForgetAwaiting(Guid)` for
   removal. A consequence worth having: an episode a full scan finds with no length waits for
   `ItemUpdated` as well, not only one found on `ItemAdded`. Any pair created for an episode also
   removes it from the set.

### 3.3 `EventHandlers/LibraryEventHandler.cs` — waiting for the length

[D4](#8-decisions): when the length isn't known, wait until it is, then check it.

- **The awaiting set** is owned by the service (§3.2 item 7). It is in memory only — after a
  restart the nightly full scan picks those episodes up, the same as any other unpaired special.
- **Subscribe to `ItemUpdated`** alongside `ItemAdded`/`ItemRemoved` (and unsubscribe in
  `StopAsync`/`Dispose`, matching the existing pair).
- **`OnItemUpdated`**: acts only when **all** hold, and otherwise returns immediately:
  `AutoDetectEnabled` is on; the item is a Season 0 `Episode`; `RunTimeTicks` is now > 0; and
  `_detectionService.TryTakeAwaiting(item.Id)` succeeds. Taking the ID out of the set is what stops
  repeat runs — `ItemUpdated` fires several times during one refresh, and only the first call gets
  past it. It then runs `ProcessEpisodeAsync` on a background task exactly as `OnItemAdded` does
  (same cancellation token, same exception handling). The synchronous part is wrapped in a
  `try`/`catch` that logs, since it runs inside Jellyfin's save loop (§2).
- **Detection re-runs in full**, not just the gate: the special may have been paired meanwhile
  (`ExistsForEpisode` catches that), ignored, or mapped differently.
- **Still unknown after that run** → re-added to the set, keeps waiting.
- **Too short → not added.** A genuine featurette is not re-checked on every metadata edit. A file
  that was still downloading and probed short is caught by the next full scan, which re-evaluates
  every unpaired special against its then-current length.
- **`OnItemRemoved`** calls `ForgetAwaiting` for a removed episode, so the set cannot hold IDs of
  deleted items.

Only items in the set trigger any work, so an ordinary `ItemUpdated` storm — a library-wide refresh,
image downloads, user edits — costs one type check and one dictionary lookup per event.

### 3.4 `Configuration/configPage.html`

In **Metadata Provider Settings**, directly after "Allow OVAs to link as movies" (it sits with the
other match filters):

```html
<div class="inputContainer" style="margin-top:16px;">
    <label class="inputLabel inputLabelUnfocused" for="txtMinSpecialLength">Minimum special length (minutes)</label>
    <input id="txtMinSpecialLength" type="number" min="0" max="600" step="1" is="emby-input" />
    <div class="fieldDescription">
        Specials shorter than this are not linked to a movie. Existing links and force links are not
        affected. New specials are checked once Jellyfin has read their length. Set to 0 to link
        specials of any length.
    </div>
</div>
```

- `loadConfig`: `value = config.MinimumSpecialLengthMinutes != null ? ... : 40`, matching the
  `txtMetadataCacheDays` pattern (a stored `0` must load as `0`, not fall back to the default).
- `saveConfig`: `parseInt(..., 10)`; `NaN` → `40`; negative → `0`; above 600 → `600`.

### 3.5 `agentic/tools/audit-harness/Program.cs`

New section "CheckMinimumLength":

- disabled (`0`, and a negative value) allows any length, including `null`;
- `null` and `0` ticks are `Unknown` when enabled;
- one tick under the minimum is `TooShort`; exactly the minimum and above are `Allowed`;
- `int.MaxValue` minutes does not overflow.

The `ItemUpdated` wait needs a live server and goes on the manual checklist (§7).

### 3.6 Docs

| File | Change |
| --- | --- |
| `agentic/HANDOFF.md` | Config table row; gate step in the detection pipeline diagram; `ItemUpdated` added to the architecture sketch and the `LibraryEventHandler` row; `RunFullScanAsync` paragraph |
| `agentic/ARCHITECTURE.md` | Detection section: why the gate guards only hard-link creation and sits after the existing-movie match, why unknown length waits, why force links and dry-run promotion bypass it, why too-short episodes are not watched. Configuration section: why 40 |
| `agentic/IDEAS.md` | Entry marked DONE (unreleased), pointing here |
| `agentic/tools/README.md` | Note the new harness section, if the README lists sections |
| `README.md` | One line under Configuration describing the setting and its 40-minute default (approved) |

---

## 4. Behaviour

### 4.1 Matrix (minimum 40)

| Episode | Force link? | Result |
| --- | --- | --- |
| 12 min, match needs a new hard link | no | skipped, no pair |
| 12 min, match is a movie already in the library | no | paired as today |
| 12 min | yes | linked as today |
| 90 min | no | detected as today |
| 40 min exactly | no | detected as today |
| just added, length not read yet | no | waits; detected as soon as Jellyfin reports the length |
| length never readable | no | waits indefinitely; a force link covers it |
| 12 min, already `Active`/`Pending`/`Error` | – | untouched; hard-link repair and watch sync continue |
| 12 min, existing `DryRun` pair | – | untouched; still promoted to a real link when dry run is turned off |
| 12 min, pair removed by the user | no | not re-detected — it is now a new link |
| any, minimum set to 0 | – | unchanged from today |

### 4.2 Existing-movie matches are not gated ([D2](#8-decisions))

When a lookup matches a special to a movie **already in the movie library**, the plugin makes no hard
link: it pairs the two existing items, so watch status syncs between them and each gets a
cross-link button. The minimum does not apply to that. It governs only whether the plugin **creates**
a movie.

Known consequence, accepted: a short featurette that a provider links to a movie the user already owns
is still paired with it, so watching the featurette marks the movie watched. The ignore list is the
remedy for such a case, as it is today.

A special waiting for its length (§3.3) is only ever one headed for a new hard link; an existing-movie
match pairs immediately, whether or not the length is known.

---

## 5. Out of scope

- A per-library-mapping minimum. Easy later as a nullable field on `LibraryMapping` overriding the
  global value, mirroring the per-library provider idea in `IDEAS.md`.
- Falling back to TMDB/TVDB episode runtimes when Jellyfin has no probed length. The `ItemUpdated`
  wait makes it unnecessary.
- Persisting the awaiting set across restarts. The full scan already covers that window.
- A maximum length.

---

## 6. Plan audit

Run against the pre-release audit's review steps, before any code exists.

| Step | Finding |
| --- | --- |
| **Security** | New input is one integer, deserialised by Jellyfin's config loader into an `int`. It reaches no URL, path, HTML attribute or log format string. The config page writes it with `.value`, never `innerHTML`. No new endpoint. **Clean.** |
| **Efficiency** | Gate is O(1). It runs after the lookup, so short specials cost the same lookups they cost today (cached by `ApiResponseCache`) — no regression. An awaited special re-runs its lookup once when the length arrives, and that is a cache hit. `OnItemUpdated` does a type check and one dictionary lookup per event and nothing else unless the item is awaited. The awaiting set holds at most the unpaired, unprobed specials. **Clean.** |
| **Concurrency — duplicate detection** | `ItemUpdated` fires several times per refresh; `TryTakeAwaiting` makes only one handler proceed. Separately, a real-time run and a full scan can both reach `ProcessEpisodeAsync` for the same unpaired episode and both pass `ExistsForEpisode` — a check-then-act race that **already exists today** between `OnItemAdded` and the full scan. This feature makes it slightly more likely by adding a second real-time trigger. **Finding (low, pre-existing):** fixed in this change by a per-episode claim (§3.2 item 6, D7). |
| **Concurrency — config** | Threshold read once into `ConfigSnapshot`; the real-time path builds its own snapshot. **Clean.** |
| **Reentrancy** | `ProcessEpisodeAsync` can itself cause `ItemUpdated` (none of its writes touch the episode item, but a hard link triggers a library change). The handler ignores anything not in the awaiting set, and an episode leaves the set before detection runs, so there is no loop. **Clean.** |
| **Lifecycle** | `ItemUpdated` subscribed and unsubscribed with the existing two events; background work uses the existing `_cts` token, so shutdown cancels it. **Clean.** |
| **Filesystem / API** | No filesystem operation added. No pair, file or media item is ever removed by this setting, so the ItemRemoved-cascade ordering rule does not apply. **Clean.** |
| **Existing pairs (requirement)** | Traced every caller of `ProcessEpisodeAsync`. The only one that re-creates an existing pair is `PromoteDryRunPairsAsync`, exempted in §3.2 item 5. The full-scan loop, `ProcessForceLinksAsync` and the new `OnItemUpdated` path all return early on `ExistsForEpisode`. **Addressed in design; on the manual checklist.** |
| **Overflow** | `(long)minimumMinutes * TimeSpan.TicksPerMinute` — the cast precedes the multiply, so even `int.MaxValue` from a hand-edited config fits in a `long`. Harness covers it. **Addressed in design.** |
| **Upgrade** | Upgraded installs pick up 40. Only new detection changes; existing pairs untouched (D3). Changelog must say so. **Accepted.** |
| **Plugin name / config path** | Not touched. **Clean.** |
| **Comment lint** | Comments stay within the two-line rule; the rationale in §3–4 goes to `ARCHITECTURE.md`. |
| **PII** | This plan and the planned doc edits contain no paths, names or machine detail. **Clean.** |
| **Risk: removed pair does not come back** | A short pair the user removes counts as new afterwards and is blocked. Consistent with "new links only"; a force link restores it. **Accepted.** |
| **Risk: file still downloading probes short** | Rejected as too short and not watched, then caught by the next full scan once the full file is probed. Delay up to a day in that case. **Accepted**; watching too-short items would re-check every real featurette on every edit. |
| **`ItemUpdated` timing** | Verified against the Jellyfin `v12.0` source (§2): `ItemAdded` precedes the probe, and the first refresh's save raises `ItemUpdated`. If a particular item's refresh does not produce a length, waiting degrades to "picked up by the next full scan", which is still correct. **Verified; manual check retained.** |

### Second pass (plan audit, 2026-09-30)

A dedicated review of the settled plan, tracing each design point back into the code and into the
Jellyfin `v12.0` source. Four findings changed the design; all are now folded into §3.

| # | Severity | Finding | Resolution |
| --- | --- | --- | --- |
| A1 | Medium | **Awaiting set had no owner that works.** §3.2 had the gate record an episode as awaiting, but §3.3 put the set in `LibraryEventHandler`, which the service cannot reach — the handler depends on the service, not the reverse. As written, only the handler could ever add to it, and a full scan's unknown-length episodes would never wait. | Set moved to `SpecialDetectionService` with `TryTakeAwaiting` / `ForgetAwaiting` (§3.2 item 7). |
| A2 | Medium | **D7 claim left a gap that could break D3.** `PromoteDryRunPairsAsync` removes the `DryRun` pair and *then* calls `ProcessEpisodeAsync`, which took the claim. A real-time run landing in that gap finds no pair, applies the minimum, and drops an existing short pair; promotion then loses the claim and returns. | Promotion takes the claim before removing the pair and holds it through the re-run (§3.2 item 6). |
| A3 | Low | **Stale length.** The full scan fetches its episode list once at scan start, and an `ItemAdded` instance predates the probe, so the gate could read "unknown" for an episode Jellyfin has since measured. Combined with the claim, that could lose the `ItemUpdated` wake-up and leave the special until the next nightly scan. | Gate re-reads the length with `GetItemById` whenever the passed instance has none (§3.2 item 3). |
| A4 | Low | **`ItemUpdated` runs inside Jellyfin's save loop**, once per item per save, for every kind of update. A slow or throwing handler slows or pollutes every library save on the server. | Handler does only a type check and a set lookup inline, wraps that in `try`/`catch`, and hands detection to a background task (§3.3). |
| A5 | Low | **A short special that was not linked left no trace** outside `Debug` logs on the real-time path, so "why didn't my special link?" had no answer. | Real-time `TooShort` logs at `Information` (§3.2 item 3). |
| A6 | Info | **"Remove All Hard Links" then turning dry run off recreates short links.** That button resets pairs to `DryRun`, and promotion bypasses the minimum. | Consistent with D3 — they are existing pairs. No change; noted so it is not later mistaken for a bug. |
| A7 | Info | **Items Jellyfin never measures** — `.strm` files, and files whose probe fails — wait indefinitely while a minimum is set. | Accepted; a force link covers them. Say so in the `ARCHITECTURE.md` note and the README line. |
| A8 | Low, accepted | **Narrow lost wake-up remains**: if the probe's save raises `ItemUpdated` while another run for the same episode is still in flight and has not yet re-added it to the awaiting set, that event is missed. | Needs a sub-second overlap on a single episode; the next full scan catches it. Closing it fully would need a per-episode "re-run requested" flag — not worth the complexity. |

Re-checked and still clean after these changes: security (no new input surface), filesystem (no new
file operation or deletion), lifecycle (the new event follows the existing subscribe/unsubscribe
pair), overflow, upgrade behaviour, and PII in this document.

---

## 7. Testing checklist

1. `dotnet build -c Release` — zero errors, zero warnings.
2. `dotnet run -c Release --project agentic/tools/audit-harness` — new checks pass, existing checks
   unchanged.
3. `node agentic/tools/check-comments.mjs .` — `clean`.
4. `cd agentic/tools/webclient-harness ; npm test` — unaffected, should still pass.
5. Manual, on a test server in dry run:
   - minimum 0 → full scan result identical to before;
   - minimum 40 → short specials needing a hard link absent from the pairs table, summary line in the log;
   - a short special matching a movie already in the library is still paired;
   - a force-linked short special still links;
   - add a new long special: confirm `RunTimeTicks` is null at `ItemAdded`, the episode waits, and
     it is detected when `ItemUpdated` reports the length — without a full scan;
   - add a new short special: waits, then is skipped once the length arrives, and is not re-checked
     on a later metadata edit;
   - with short `DryRun` pairs already stored, turn dry run off and run a full scan: those pairs are
     promoted, not dropped;
   - with a short `Active` pair, run full scan + cleanup: pair unchanged;
   - start a full scan and, while it runs, add a new special: exactly one pair results;
   - a special whose length is unknown when a full scan reaches it is detected once `ItemUpdated`
     reports the length, without waiting for the next scan;
   - save the page with an empty / negative / huge value: round-trips as 40 / 0 / 600; a stored 0
     reloads as 0.

---

## 8. Decisions

| # | Question | Decision |
| --- | --- | --- |
| **D1** | Do force links bypass the minimum? | **Yes.** |
| **D2** | Does the minimum also block pairing with a movie already in the library? | **No.** It only blocks creating a new hard-linked movie (§4.2). |
| **D3** | What happens to pairs that already exist below the minimum? | **Nothing.** New links only; every existing pair is left alone, including through dry-run promotion. |
| **D4** | Unknown length? | **Wait until it is known**, via `ItemUpdated` (§3.3). |
| **D5** | Unit and default. | **Minutes, default 40** (§3.1). |
| **D6** | Update `README.md`? | **Yes**, one line under Configuration. |
| **D7** | Include the per-episode detection claim that closes the pre-existing duplicate-pair race (§6)? | **Yes** (§3.2 item 6). |

---

## 9. As-built notes

Implemented as planned, with these differences in detail:

- **The private detection methods return `LengthCheck?`**, where `null` means the run never reached
  the length check (already paired, ignored, no match, existing movie, force link, lost the claim).
  Returning `Allowed` from those paths read as "passed the check", which was wrong for a dozen exits.
  Only `RunFullScanAsync` reads the value, to count skipped and waiting specials.
- **The claim wrapper clears the episode from `_awaitingLength` when it starts a run**, rather than
  each pair-creating path removing it. The run re-adds it if the length is still unknown, so a
  paired, ignored or too-short episode can never be left in the set.
- **Named arguments at every call site** (`applyMinimumLength:`, `claimHeld:`), since two adjacent
  `bool` parameters are otherwise unreadable.
- **`README.md`** carries a single feature-list bullet. A longer paragraph under Configuration was
  drafted and then trimmed out of the working copy by hand; the config-page hint carries the detail.

Verification: Release build with zero warnings, audit harness 28/28 (7 new checks), both web-client
harnesses passing (layout 66/66), config-page select check 4/4, comment lint clean, and the config
page's inline script parses. The server-side items in §7.5 still need a live Jellyfin server.
