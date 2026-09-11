# Security & Efficiency Audit Log

## Standing Check: PII & Documentation Sweep

Every audit includes a sweep of **all tracked files** for personally identifying information — source
comments, everything under `agentic/` (including `agentic/memory/`), `README.md`, `manifest.json`,
`build.yaml`, the `.csproj`, config UI text, and log/exception strings — not only the prose docs. The
repository is public and its history is permanent, so anything personal that reaches `master` cannot
be withdrawn.

A finding is anything identifying a **person** or a **machine**: absolute or drive-rooted paths,
usernames, home directories, cloud-drive folder names, network share or UNC paths, hostnames, IP
addresses, email addresses, credentials or API keys, session identifiers, personal media-library
names, developer-machine inventories, or quotes of or characterisations of the user. Patterns, the
runnable commands, and the full scope list are in
[`CLAUDE.md` -> Pre-Release Audit -> PII & Documentation Sweep](CLAUDE.md#pii--documentation-sweep).

Every audit entry below carries a **PII Sweep** line recording that the sweep ran, what it covered,
and each finding with its resolution. A clean sweep is still recorded.

### Known-Acceptable Matches (do not re-flag)

| Match | Where | Why it is not a finding |
| --- | --- | --- |
| The project's GitHub owner handle and repository URL | `manifest.json`, `build.yaml`, `README.md`, `agentic/CLAUDE.md` | The plugin is published from that account, so the handle is the project's public identity rather than a leak. |
| Commit author name and email in git metadata | published history | Inherent to any published repository; outside the scope of a file sweep and not withdrawable after push. |
| Jellyfin's own install paths (`C:\ProgramData\Jellyfin\...`, `/var/lib/jellyfin/...`, `/config/...`) | `README.md` install steps | Jellyfin's install locations on any server, not paths on a developer machine. |
| The .NET SDK version used for a release build | `HANDOFF.md` | Documents the toolchain the build requires, not an inventory of a specific machine. |
| Four-part dotted versions (`1.0.16.0`, `12.0.0.0`, `10.11.11.0`) | throughout | Assembly and ABI versions, not IP addresses. These dominate the dotted-quad check. |
| The sweep patterns matching themselves | `agentic/CLAUDE.md` | The documented regexes match their own documentation. |
| Default Edge and Chrome install locations | `agentic/tools/webclient-harness/browser.js` | The vendors' own default paths, identical on every machine of that OS and not read from this one. They are the **last** resort in the lookup, after `STM_BROWSER` and `PATH`, and are confined to that one file so no other tool repeats them. |

**Baseline — 2026-08-20**: first full sweep, all 43 tracked files, run in both Git Bash and
PowerShell. **Clean.** No absolute developer paths, machine names, share names, email addresses, or
credentials in any tracked file. All 115 source comment lines were read individually — every one is
technical, none personal. The only matches returned were the known-acceptable ones above.

---
---

## Fix: 2026-09-10 (Session 21 — Cross-link icon measured once, post-v2.0.0)

**Scope**: one field defect and its fix, a caption rename, and the nine-step audit over the result.
Steps 4-7 were read over the changed surface — `Web/specialtomovie.js`, the two providers,
`configPage.html` and the new tooling — rather than the whole tree, which Session 20 covered.

| Step | Outcome |
| --- | --- |
| 1 Build | `dotnet build -c Release` — 0 errors, 0 warnings |
| 2 Harnesses | `audit-harness` 21/21, `test.js` 46/46, `test-layout.js` 66/66 |
| 3 Comment lint | clean, 70 files |
| 4 Security | nothing new reaches a URL, HTML attribute or the filesystem. `layoutKey` writes rounded integers through `setAttribute`, never markup |
| 5 Efficiency | one finding, accepted. See costs below |
| 6 Concurrency | one finding, fixed — unbounded timer array in `align-fix.js` |
| 7 Filesystem/API | untouched by this change; providers changed one string literal each |
| 8 Sweeps | both clean. See the PII and scratchpad lines below |
| 9 Write-up | this entry |

**Reported**: on one special the icon sat a few pixels low on desktop and after a refresh on mobile,
and sat high on the first in-app load. Movie pages were always right. No other item reproduced it.

### Finding (fixed): the row was measured once, and the row is not finished when it is first drawn

`matchRow` set `data-stm-matched` after its first successful measurement and read it as "done"
forever. The row it measures is assembled in stages: jellyfin-web renders plain anchors and adds its
own `emby-button` class when it upgrades the element — that class carries
`display: inline-flex; vertical-align: middle`, so a link's box changes shape when it lands — a
user's logo CSS can apply later still, and Jellyfin-Enhanced appends links after an API call. A
correction measured inside that window is wrong for the row that follows it, in either direction,
and nothing re-measured.

Why it looked item-specific and Seerr-related: Jellyfin-Enhanced adds its Seerr link only for
`Movie` and `Series` items (`seerr-detail-link.js`), and Letterboxd links only for movies and
people. On a movie page that late insertion moves our icon back to the end of the row, which clears
the flag and forces a fresh measurement on a settled row — so movie pages self-corrected. A special
is an episode: nothing arrives late, so whichever moment the first measurement landed in is the one
it keeps. The item was never the variable; the timing was.

**Fixed** in `Web/specialtomovie.js`:

- `data-stm-matched` holds a **key** rather than a flag — container width, the icon's own box, and
  the identity and box of the link it was measured against. A pass computing a different key
  measures again. The key is stored after the corrections are applied, so a stable row settles.
- The reference is the nearest preceding element the row actually **draws**. A hidden neighbour, or
  one an installed logo pack has no rule for, has no box: measuring against it yielded a correction
  the size of the distance to the viewport top, which the sanity limit discarded, leaving the icon
  uncorrected in a row that needed a correction.
- A correction is taken only from a link on the **same line** — the icon follows it along the line,
  or their boxes overlap vertically. Each test alone fails a row type. A wrapped icon on a narrow
  phone was being dragged up into the line above by up to `MAX_SHIFT`.
- `resize` and a short settle series (400ms, 1.2s, 3s after arriving on a page) trigger a pass. A
  stylesheet arriving late restyles the row with nothing for the observer to see.
- The gap guard compared the top edges of two differently shaped boxes, so on a row whose links are
  `inline-flex` the spacing correction never ran at all. It now uses the same-line test.

### Verification

| Check | Result |
| --- | --- |
| `webclient-harness/test.js` (jsdom) | 46/46 |
| `webclient-harness/test-layout.js` (Edge) | 66/66 — four new scenarios, 14 new checks |
| Same harness against the **v2.0.0** script | 56/66 — the 10 failures are exactly the new checks |
| Same harness, v2.0.0 + `align-fix.js` pasted late | 63/63, from rows 5.5px, 32px, 3.5px and 7.7px out |
| `audit-harness` | 21/21 |
| `dotnet build -c Release` | succeeded, 0 warnings |
| `check-comments.mjs .` | clean, 70 files |

The new scenarios build the row as the web client really does (`is="emby-linkbutton"` anchors joined
with `", "`, under jellyfin-web's own `emby-button` rules) and then change it after the icon has been
placed: the element upgrade plus logo CSS, a hidden link directly before the icon, a wrap on a narrow
viewport followed by a widen, and a stylesheet arriving with no DOM change at all. Each asserts
**drift** — the correction carried, minus the correction the row asks for now — which is the number
of pixels the user sees the icon out by.

**Cost** (`measure.js`, and the new `measure-idle.js`): computing the key adds about five box reads
to a pass on a visible detail page. Ten passes over a settled row produce **zero** style writes, so a
stable row is still measured exactly once and the key cannot oscillate. On the measure.js scenario,
which appends to the row every pass and so re-measures every pass by design, a pass costs 35 box
reads against 24 before.

### Finding (fixed): `align-fix.js` grew its timer array without bound

Step 6. Each `hashchange` armed three more settle checks and pushed them onto an array that was only
ever read by `stop()`. A console tool outlives many navigations, so the array grew for as long as it
ran, and a previous page's checks stayed queued behind the current page's. `settle()` now clears the
outstanding batch before arming the next, which is also the correct behaviour: the page that
superseded them no longer wants them. The shipped script arms the same checks without tracking them,
so it never had the array and needed no change.

### Finding (accepted): `rowReference` walks siblings without a cap

Step 5. `isHidden` bounds its walk at `MAX_HIDDEN_HOPS` because it climbs ancestors, and the comment
there warns that an unbounded walk runs per link per pass. `rowReference` walks **siblings inside the
links row**, so it is already bounded by that row's child count, and it stops at the first drawn one —
normally the first hop. A cap would buy nothing and would return `null` on a legitimately long row,
dropping the correction entirely. Left uncapped deliberately.

**Not fixed, by design**: where a hidden link leaves its `", "` separators behind, the gap before the
icon is two separators wide. The row's text is the web client's, not ours to edit, so the correction
can only push the icon out, never pull it in. Pinned by the harness as "not spaced tighter than the
settled row".

### Caption rename

`Movie Version` → **`Linked Movie`** and `TV Special` → **`Linked Special`**, in both
`IExternalUrlProvider`s. The captions are also the icon's tooltip and accessible name, which the
client script copies from the anchor. Updated in `Configuration/configPage.html` (whose description
also still claimed the link "Displays as a badge when appropriate" — badge detection was removed in
v2.0.0), `HANDOFF.md`, `IDEAS.md` and the harness fixtures. `README.md` does not name the captions,
so it needs no change.

### New tools

[`agentic/tools/align-report.js`](tools/align-report.js) — a browser-console report for the same
question on a **live** server, where the row carries the install's own theme, logo CSS and plugins.
Written for this diagnosis and promoted out of the scratchpad rather than thrown away.

[`agentic/tools/align-fix.js`](tools/align-fix.js) — places the icon from the console on a server
running a build whose own placement is wrong, so the fix can be confirmed on the reporter's real row
before a build ships. It carries the same rules as `Web/specialtomovie.js` and must change with it.

[`webclient-harness/measure-idle.js`](tools/webclient-harness/measure-idle.js) — the steady-state
half of the cost question `measure.js` cannot ask, and the source of the zero-writes figure above.

`test-layout.js` takes an optional second path, a script pasted after the row has settled. Against
the v2.0.0 script — which fails 10 of its own checks — `align-fix.js` passes **63/63**, correcting a
row found 5.5px, 32px and 7.7px out in the three settling scenarios.

**PII Sweep**: all five documented checks, over 75 files — every tracked file plus the three
untracked new tools. **Clean.** Checks 1, 3 and 4 returned only known-acceptable matches: Jellyfin's
own install paths in `README.md`, the vendors' default browser paths in `browser.js`, the project's
GitHub owner handle, the sweep patterns in `CLAUDE.md` matching themselves, and assembly versions for
the dotted-quad check. Check 2 (e-mail) returned nothing. Check 5 read every comment line in the
changed published source: all technical, none personal. The only `:8096` in the tree is the harness's
literal `host:8096` fixture. Neither console tool takes a path; `align-report.js` prints
`(this server)` in place of the address it runs on and strips the query, and so the item id, from the
page it names. One absolute workspace path was found and removed from the steady-state probe as it
was promoted — it had been written against the scratchpad copy.

**Scratchpad Sweep**: three reusable things were found parked outside the repository and
**promoted** — the console placement fix (`tools/align-fix.js`), the steady-state cost probe
(`webclient-harness/measure-idle.js`), and a forked copy of the layout harness, which became the
optional second argument on `test-layout.js` rather than a second copy of it. Deleted: two superseded
one-off diagnostic probes and the concatenated script built to test the pair together. What remains
is third-party reference material only (jellyfin-web and Jellyfin-Enhanced sources, and the released
script, recoverable with `git show`); no server binaries, library data, configuration or logs were
present at any point.

---
---

## Audit: 2026-09-09 (Session 20 — Cross-link icon rebuild, comment lint adopted, post-v1.0.19)

**Scope**: the full nine-step audit over the whole tree. `Web/specialtomovie.js` was rewritten
(639 -> 460 lines) around the row's own icon idiom, the `webclient-harness` was rewritten to match,
and a comment lint was adopted as a standing release gate — which rewrote the comments in 19 source
files and moved their rationale into the new [`ARCHITECTURE.md`](ARCHITECTURE.md).

**Releases 1.0.18.0 and 1.0.19.0 were withdrawn** from GitHub and from `manifest.json` before this
audit; both shipped a visibly broken external-links row. The newest manifest entry is 1.0.17.0, so
the next release is **1.0.20.0** — not a re-cut 1.0.19.0, which anyone who already installed it would
never be offered as an update. `.csproj` and `build.yaml` still read `1.0.19.0` and need the bump.

### Step 1 — Build

`dotnet build -c Release`: **succeeded, 0 warnings, 0 errors.**

### Step 2 — Harnesses

| Harness | Result |
| --- | --- |
| `audit-harness` | 21/21 passed |
| `webclient-harness/test.js` (jsdom) | 46/46 passed |
| `webclient-harness/test-layout.js` (Edge) | 52/52 passed |
| `webclient-harness/measure.js` | 18 style resolutions + 24 layouts on first pass, flat across 10 passes on both row types — unchanged from the v1.0.19 measurement |

**Finding (harness, fixed): the layout harness measured the wrong box.** It asserted vertical
centring from `getBoundingClientRect`, which is wrong twice over here — a logo link is an inline
anchor with `font-size: 0`, so its rect collapses to zero height on the baseline, and the icon's
vertical correction is a `position: relative` offset on the `::before`, which moves paint without
moving layout. It reported the icon 4.5px low on a text row when hit-testing showed it 0.5px out.
Resolved by measuring the **painted** box with `document.elementFromPoint`, which is independent of
the arithmetic the script uses; a painted-size check was added alongside it. The script itself was
correct and was not changed for this.

### Step 3 — Comment lint (new standing gate)

`node agentic/tools/check-comments.mjs .` — **404 violations in 19 files at the start of this audit,
`clean (67 files)` at the end.** Everything outside `agentic/` is in scope.

| Rule | Count | Resolution |
| --- | --- | --- |
| `doc-comment` | 251 | Every `///` XML doc block removed. Nothing consumes them — no `GenerateDocumentationFile`, no StyleCop, no `.editorconfig` — so this is not an API-surface change. |
| `rationale-word` | 65 | Reworded, with the reasoning moved to `ARCHITECTURE.md`. |
| `comment-run` / `comment-block-length` / `comment-prose` | 86 | Long explanatory blocks cut to two-line notes pointing at the matching `ARCHITECTURE.md` section. |
| `comment-length`, `commented-code` | 2 | Shortened / removed. |

`agentic/ARCHITECTURE.md` was created to receive the rationale: cross-link buttons (all four
components plus the client script), detection and pairing, hard links and subtitles, persistence, the
`ItemRemoved` cascade, metadata lookup, watch sync, and configuration. **No reasoning was deleted** —
every removed explanation is in that file.

**Verified comment-only**: `git diff -U0 -- '*.cs'` shows no added and no removed non-comment line in
any C# file. The C# behaviour is byte-identical to `ed30cca`.

### Step 4 — Security review

No new findings. The three recurring shapes were re-checked and all still hold:

- **Library metadata reaching a URL path segment** — `seriesTmdbId`, `imdbId`, `episodeTvdbId` are
  still `Uri.EscapeDataString`-wrapped before joining a URL that also carries the API key. Covered by
  four `audit-harness` checks, all passing.
- **Request-derived string reaching an HTML attribute** — `GetBasePrefix` still rejects anything that
  is not a plain path, and `CrossLinkUrlResolver` still validates `GetSmartApiUrl`'s result as an
  absolute http/https URI. `CrossLinkUrlBuilder.Details` still admits only a GUID, the system ID and
  a fixed marker char.
- **Composed path escaping its library root** — `BuildHardLinkPath` still resolves and re-checks
  against the root with a trailing separator. Covered by `audit-harness`, passing.

`ClientScriptController` remains the only `[AllowAnonymous]` route and still reflects nothing from
the request; the client script it serves was reviewed in full as part of the rewrite.

### Step 5 — Efficiency review

No new findings. The two indexes that keep a full scan from growing as episodes × movies
(`MovieIndex`, the force-link episode index) and the batched fetches in `CleanupTask` are unchanged.
The client script's per-render cost is unchanged from v1.0.19 (step 2).

### Step 6 — Concurrency review

No new findings. `ConfigSnapshot` still takes the mutable config collections once at scan start;
`WatchSyncService`'s reentrancy guard against its own save event is intact; `PairStore` still rebuilds
its indexes under `_lock` and both `Load` and `Save` still refuse to throw into Jellyfin's event
dispatch.

### Step 7 — Filesystem and API review

No new findings. Sanitisation (invalid chars, `..` collapsed to a fixed point, Windows reserved
names), containment, and the copy-backup-write-temp-rename write sequence in `PairStore` are
unchanged. The delete endpoint's two guards — a pre-existing movie is never deleted, and the *saved*
configuration decides rather than the request — are intact.

### Step 8 — Sweeps

**PII Sweep**: all 5 checks run over all 69 tracked files, plus the uncommitted working tree.
**Clean.** Check 1 returned only Jellyfin's own install paths, the sweep patterns matching
themselves, a test string in `audit-harness`, and the default Edge/Chrome install locations in
`browser.js` — all four already on the known-acceptable list. Check 2 (email) returned nothing. Check
3 returned only the project's public GitHub owner handle. Check 4's single non-version-prefixed match
is `9.0.0.0`, a .NET assembly version in an existing `AUDIT.md` table. Check 5 was read in full: all
comment lines across the 29 published source files are technical, none personal — and step 3 has now
capped their length, which makes this check materially cheaper to do properly.

**Scratchpad Sweep**: the session scratchpad held one-off console builds and browser probes written
while diagnosing the icon in a live browser (`bundle.js`, `console-build.js`, `tempfix.js`,
`clonetest.js`, `jeidiom*.js`, `probe.js`, `probe2.js`, `probe3.js`, `public-icon.css`) and local
copies of two third-party scripts read as reference. Nothing promoted: each answered one question,
and the durable versions of those questions are now checks in `test-layout.js`. No server binaries,
no `library.db`, no plugin configuration, no `PairStore` JSON, no API keys, no logs. `probe3.js`
earned the harness's painted-box measurement and its finding is recorded above.

**Open item for the user — `agentic/tools/webclient-harness/diagnose.js` is untracked and stale.**
It is a console probe that reports "whether the external-links row is rendered as brand badges or as
text" — a decision the script no longer makes, since removing it is what fixed this feature. It
predates this session and is not mine to delete. Recommendation: **delete it**. Promoting it would
mean rewriting it against a design that no longer exists.

### Documentation

- `agentic/ARCHITECTURE.md` — **new**, described under step 3.
- `agentic/CLAUDE.md` — Pre-Release Audit rewritten from a 7-item list into nine numbered steps, each
  with its command and its pass condition. Comment lint added as step 3; the PII and scratchpad
  sweeps are now step 8 and are explicitly mandatory-and-recorded-even-when-clean. `ARCHITECTURE.md`
  added to the structure diagram. The PII sweep snippet now filters `git ls-files` through
  `Test-Path`, which stops `Select-String` erroring on a file staged for deletion.
- `agentic/HANDOFF.md` — `Web/specialtomovie.js` row and the "How the link is styled" section
  rewritten for the icon-idiom design; two gotchas added (do not detect the row type or copy a
  neighbour's geometry; comments are lint-enforced); `ARCHITECTURE.md` added to the sibling-doc list.
- `agentic/tools/README.md` — `check-comments.mjs` row added; `webclient-harness` row corrected
  (46 and 52 checks, and what they now actually cover).
- `agentic/tools/webclient-harness/README.md` — rewritten; it still described badge detection,
  contrast correction and 40/18 checks, none of which exist.
- `agentic/memory/feedback_comment_lint.md` — **new**, indexed in `MEMORY.md`.
- **`README.md` checked, no change needed.** No new feature, default, task name or config option:
  `ShowCrossLinks` and `InjectClientScript` are unchanged, and the icon rebuild is not user-visible
  as a setting.

---

## Audit: 2026-09-09 (Session 19 — Badge row joining and alignment, post-v1.0.18)

**Scope**: `Web/specialtomovie.js` — the badge-row search and metric matching added after v1.0.18 —
and the `webclient-harness` tooling around it. No `.cs` file changed since Session 18, so the C#
codebase was not re-reviewed; the build and the C# harness were re-run to confirm no regression.
**Triggered by**: Two further defects reported from a live install after v1.0.18 shipped, followed by
an explicit request to audit, fix and verify.

### The measurement gap, closed

Session 18 recorded that two paths could not be verified because jsdom does not lay out:
`matchBadgeMetrics`, and the aspect-ratio fallback in `captionSuppressed`. That was accepted at the
time and should not have been. Both are now covered by
[`test-layout.js`](tools/webclient-harness/test-layout.js), which drives an already-installed Edge or
Chrome through `playwright-core` — no browser is downloaded.

It is a real regression test, not a restatement of the fix. Run against the script exactly as shipped
in v1.0.18 it fails **16 of its 18 checks**, and reproduces the reported symptom precisely: the
cross-link measures **88x16** — the proportions of a text link — instead of a square tile matching
the row. Against the fix, all 18 pass, at logo heights of both 32px and 24px so that a hard-coded
size cannot satisfy it.

A false negative was caught while establishing that control. The first control run compared against
`HEAD:Web/specialtomovie.js` and reported 18/18 passing, which would have meant the test proved
nothing. `HEAD` had already advanced to include the fix; the pre-fix script was at the release commit.
**A control run that passes is a result to distrust, not to accept.**

### Findings

All three are efficiency, all measured rather than reasoned about, using
[`measure.js`](tools/webclient-harness/measure.js) — written for this audit and promoted with it.
Counts are for a row of ten links plus the cross-link, over ten subsequent render passes.

| # | Severity | Finding | Resolution |
| --- | --- | --- | --- |
| 1 | Medium | The upward search for the badge row widens at every hop, and re-tested every link the previous hop had already tested. Each test costs a style resolution, and the search runs on every mutation. A ten-link text row cost **3195 style resolutions across ten passes** (~320 per pass), scaling with links x hops | Anchors are marked with a per-walk token and tested once. A `MAX_ANCHORS_SCANNED` budget of 60 bounds the pathological case of a page with hundreds of links |
| 2 | Medium | `matchBadgeMetrics` called `getBoundingClientRect` on every pass, forcing layout each time — **140 forced layouts across ten passes** of a badge row | Skipped when the tile is already sized against the same neighbour. A rebuilt anchor arrives with no inline height, so a genuine size change is still picked up |
| 3 | Low | `applyContrastColour` re-walked the ancestor chain for a colour that cannot have changed | Skipped when the anchor already carries an inline colour, on the same reasoning |

Measured after the fixes:

| Row | Style resolutions (10 passes) | Forced layouts (10 passes) |
| --- | --- | --- |
| text | 3195 -> **700** (-78%) | 0 -> 0 |
| badges | 140 -> **71** (-49%) | 140 -> **71** (-49%) |

The trade-off in findings 2 and 3 is deliberate and worth stating: a size or theme change that
happens *without* the web client rebuilding the row will not be picked up until it next does. Both
memos key on inline state that a rebuilt anchor does not have, and the client rebuilds this row on
every render, so the window is small. Paying a forced layout on every mutation to close it is not a
good trade.

### Security

No finding. The new code reads computed style from neighbouring elements and writes the values back
as inline style on the plugin's own anchor; the values come from the browser's own computed-style
serialisation, never from a URL, a DTO, or anything user-supplied. `findBadgeRow` only reads the DOM.
The two expando markers it sets (`stmScan`, `stmRef`) are plain properties on elements, not
attributes, so they neither serialise into the document nor trigger the mutation observer.

`playwright-core` was added as a **devDependency** — harness-only, never referenced by the plugin and
never shipped in the DLL. It launches a browser already on the machine and downloads nothing.

### PII & Documentation Sweep

Ran over all tracked files plus the new untracked ones, per `CLAUDE.md`.

| Check | Result |
| --- | --- |
| 1. Drive-rooted / UNC / home paths | One item to settle, now known-acceptable: `browser.js` lists the vendors' default install locations for Edge and Chrome. Every other match is a regex escape, a newline escape, or a relative MSBuild path |
| 2. Email addresses | Clean |
| 3. This machine's identity | Clean |
| 4. Dotted quads | Clean — all 24 distinct matches are assembly or ABI versions |
| 5. Comment read-through | Clean — no personal reference in any new comment |

**No PII finding.** The browser paths were deliberately confined to one file, `browser.js`, rather
than duplicated across the two harnesses that need them, and are consulted **last**: `STM_BROWSER`
is checked first, then whatever is on `PATH`. Nothing is read from this machine to produce them.

### Scratchpad & Temporary File Sweep

**Promoted**: [`measure.js`](tools/webclient-harness/measure.js), which produced every count quoted
above — a finding backed by numbers whose tool has been deleted cannot be re-checked at the next
change. [`browser.js`](tools/webclient-harness/browser.js) was factored out at the same time so the
two browser-driven harnesses share one lookup.

**Deleted**: a scratch copy of the measurement script that had been dropped into the tool directory
under a leading-underscore name, and an extracted copy of the v1.0.18 script used as the control.
Both are reproducible — the second with `git show <ref>:Web/specialtomovie.js`, which is now
documented in `measure.js` rather than left as session knowledge. No server binaries, library
database, plugin configuration, `PairStore` JSON, API keys or logs were present.

### Verification

- `dotnet build -c Release` — succeeded, 0 warnings, 0 errors
- `agentic/tools/audit-harness` — **21 checks, all passing** (unchanged, no C# regression)
- `agentic/tools/webclient-harness` — `npm test` runs both halves: **40 behaviour checks** (jsdom) and
  **18 layout checks** (real browser), all passing
- Control: the layout harness against the v1.0.18 script — **16 of 18 fail**, as it must

---
---

## Audit: 2026-09-09 (Session 18 — Detail-page cross-link defects reported against v1.0.17.0)

**Scope**: `Web/specialtomovie.js` and `Services/ScriptInjectionStartupFilter.cs`, the two files
behind the detail-page cross-link enhancement, plus the full mechanical PII and scratchpad sweeps
over the whole tree. The `.cs` codebase was audited end to end in Session 17 and only the injector
changed since, so the C# review was scoped to that change rather than repeated in full.
**Triggered by**: Four defects reported from a live install running v1.0.17.0, followed by an
explicit request to audit, fix and verify.

### Reported defects, and what actually caused them

All four were one enhancement failing in four visible ways. The fourth report — that the *first*
click opens a new tab and a click in the resulting tab does not — is what made the rest diagnosable:
it proved the script was loading and running, which ruled out the injector, the embedded resource,
the anonymous route and the configuration defaults, all of which were checked and found correct.

| # | Symptom | Root cause | Fix |
| --- | --- | --- | --- |
| 1 | No badge icon; the link rendered as bare text among brand logos | `rowIsBadges` skipped every anchor whose `textContent` was non-empty before testing it for an image. The usual way to render a logo row keeps the label for screen readers and hides it in CSS, so on exactly the rows the check existed to detect, every anchor was skipped and the row was judged to be text | Detection now asks whether a caption is *painted*, not whether it exists: `isBadgeAnchor` combines an image test with `text-indent`, `font-size` and an aspect-ratio fallback |
| 2 | Dark text on a dark background | Nothing ever set a colour; the link inherited whatever the theme gave it, which on a dark theme was unreadable | `backdropIsDark` walks ancestors for the first opaque background, computes relative luminance and sets an `!important` inline colour. Only the text form is touched; the badge keeps its own palette |
| 3 | Not last in the row | Position was applied once, at upgrade time, and only on the badge path. Other plugins append after us, and the row is rebuilt on every render | `moveToEnd` runs on every pass, for both forms, bounded by a move budget |
| 4 | First click opened a new tab; the second did not | The fix relied on rewriting `href` and dropping `target`, which cannot win the race against a re-render — the web client rebuilds the row from the server DTO, restoring the absolute URL and `target="_blank"` on a fresh anchor. A click landing before the next upgrade pass got the untouched anchor | A capture-phase document click handler, which does not depend on the anchor having been upgraded: `parseLink` reads the item out of the absolute URL exactly as well as out of the rewritten hash |

Two fragile assumptions were removed while fixing these. The selector no longer requires
`#itemDetailPage:not(.hide) .itemExternalLinks`; it matches the plugin's own `stm=` marker, which
cannot drift with a web-client class rename. Had either class name changed, the failure mode would
have been silent and would have looked exactly like this report.

### Findings from auditing the new code

| # | Severity | Finding | Resolution |
| --- | --- | --- | --- |
| 1 | Medium | The capture-phase click handler sees every click in the document and cancelled the default on any link whose fragment merely carried `stm` and `id`. Another plugin's link of that shape would have been swallowed | `parseLink` now validates the exact shape `CrossLinkUrlBuilder` emits: route must be `/details`, marker must be `m` or `s`, id must be a GUID. Four negative cases are asserted in the harness |
| 2 | Low | The move budget that stops a fight with another last-forcing plugin was consumed by ordinary re-appends and never replenished, so after eight displacements the link would stop being placed at all | The counter is cleared whenever a pass finds the link already last. A genuine fight never reaches that line, so it still terminates |
| 3 | Low | Nothing made the script idempotent. A second copy — a stale service worker, a manual install alongside the injected tag — would register a second click handler and a second `MutationObserver` | A `window.__specialToMovieLoaded` guard returns early on a second load |

### Efficiency

`rowIsBadges` runs for every link in the row on every mutation pass, and its checks force style and
layout resolution. The tests were reordered cheapest-first: a plain text row — the common case, and
the one whose answer is "no" — now costs a single `getComputedStyle` per link, with no
pseudo-element read and no `getBoundingClientRect`, both of which are reached only when an anchor
already presents both a picture and text.

### Caching

`ScriptInjectionStartupFilter` correctly strips `ETag` and `Last-Modified` after rewriting the
document, but set no `Cache-Control`, leaving the browser free to keep serving a heuristically
cached `index.html` — including one fetched before the plugin was installed, which carries no script
tag and disables the enhancement entirely until a hard reload. It now sets
`no-cache, must-revalidate`. This was not one of the four reports and no evidence says it was in
play, but it is a silent-failure path of exactly the reported kind.

### Verification

- `dotnet build -c Release` — succeeded, 0 warnings, 0 errors
- `agentic/tools/audit-harness` — **21 checks, all passing** (no regression from Session 17)
- `agentic/tools/webclient-harness` — **31 checks, all passing**, newly written this session

Every one of the four reported defects has a check that fails against the previous script. Check 7
in particular exercises twelve consecutive displacements, which the old eight-move budget could not
survive.

**Not verified here**: jsdom computes style but does not lay out, so `getBoundingClientRect` returns
zeroes and the aspect-ratio fallback in `captionSuppressed` — the path for a badge whose caption is
hidden on a child element rather than on the anchor — is not exercised. The CSS-based paths around
it are. That fallback needs a real browser to confirm.

### PII & Documentation Sweep

Ran over all tracked files plus the four new untracked files, per `CLAUDE.md`.

| Check | Result |
| --- | --- |
| 1. Drive-rooted / UNC / home paths | Clean — matches are regex escapes and newline escapes in source, relative MSBuild paths in the `.csproj`, the sweep patterns matching their own documentation, and the known-acceptable Jellyfin install path in `README.md` |
| 2. Email addresses | Clean — no matches in any tracked or new file |
| 3. This machine's identity (username, domain, hostname, git user name/email) | Clean — no matches. Also run against `node_modules/`: clean, and the lockfile carries no local path |
| 4. Dotted quads | Clean — all 30 distinct matches are assembly, ABI or package version numbers |
| 5. Comment read-through | Clean — the only first-person matches are the editorial "we"/"our" meaning *the plugin*, already settled as not-a-finding in the Session 17 entry |

**No PII finding.**

### Scratchpad & Temporary File Sweep

**Promoted**: the jsdom harness written to verify these fixes became
[`agentic/tools/webclient-harness/`](tools/webclient-harness/) — 31 checks, its own `README.md`, and
a row in the tools inventory. `tools/README.md` already named "DOM-stub harnesses for
`Web/specialtomovie.js`" as belonging there; until now none existed.

**Deleted**: a duplicate copy of the harness and its `node_modules` in the system temp directory; the
release-verification artifacts from the v1.0.17 release earlier in the session (the downloaded
release zip, its extracted plugin DLL, the fetched live manifest, the release-notes draft); and two
sweep working files, one of which held this machine's identity strings and was deleted first. No
Jellyfin server binaries, `library.db`, plugin configuration, `PairStore` JSON, API keys or exported
logs were present anywhere. The scratchpad is empty.

### Follow-up after v1.0.18 — two further defects from the field

Shipping v1.0.18 fixed the four reported faults but surfaced two more, both in the badge path and
both reported against a live install:

| Symptom | Root cause | Fix |
| --- | --- | --- |
| The tile did not line up with the row's other logos | The badge was a hard-coded 28px box. The row's logos are whatever height the install's theme and plugins make them, so a fixed size lines up only by coincidence | `matchBadgeMetrics` copies height, display, vertical-align and margins from a neighbouring badge, keeping the tile square |
| One side of the pair rendered as a badge, the other stayed as text | Badge detection only looked at the link's **own parent**. A plugin that converts text links into tiles can place those tiles in a container of its own, leaving the cross-link behind in the original one - and a parent holding nothing but our link looks exactly like a text row | `findBadgeRow` walks up to four ancestors to find the badge row wherever it is, and the link is moved **into** that container beside the badges rather than styled to imitate one from outside it |

The second is the more instructive: the enhancement was still assuming a DOM shape, just a subtler
one than the class names removed earlier in this session. Joining the row it finds, rather than
decorating whatever container it happens to be in, removes the assumption rather than replacing it.
Both are covered by checks 10 and 11 of the jsdom harness, which now runs **40 checks**. The size
and alignment fix is measurement, which jsdom cannot see at all, so a second harness was added:
[`test-layout.js`](tools/webclient-harness/test-layout.js) drives an already-installed Edge or Chrome
through `playwright-core` and asserts the tile matches its neighbours in height, stays square and
sits centred on the row, against logo heights of both 32px and 24px so a hard-coded size cannot pass.

It is a true regression test rather than a restatement of the fix: run against the script as shipped
in v1.0.18 it fails **16 of 18 checks**, and reproduces the reported symptom exactly - the cross-link
measures 88x16, the proportions of a text link, rather than a square tile matching the row. This also
closes the gap recorded in the Session 18 verification note, which stated that the aspect-ratio
fallback in `captionSuppressed` could not be exercised without a real browser; the layout harness
reaches it, because the badges it builds hide their captions on a child span exactly as a real
converting plugin does.

### Resolved — `node_modules` is gitignored, not committed

Vendoring `jsdom` into the repository was considered and rejected: it would have added **23 MB across
1663 files**, and **85 distinct third-party package author emails**, to permanent history. Those are
public npm package metadata rather than anyone's private detail, so this was a size and hygiene call
rather than a PII finding. `node_modules/` is in `.gitignore`; `package.json` and `package-lock.json`
pin the exact tree, so `npm install` in `agentic/tools/webclient-harness` reproduces it.

The PII sweep above therefore covers the harness sources only, which is the correct scope. Should
the dependency ever be vendored after all, re-run check 2 over `node_modules/` and expect those 85
addresses to need a known-acceptable entry of their own.

Worth knowing for next time: **npm added a `node_modules/` line to `.gitignore` by itself** during
the install. Re-read `.gitignore` after any `npm install` in this repository rather than assuming it
is unchanged - in this instance it happened to match the decision taken, but it was not a deliberate
edit.

---
---

## Audit: 2026-09-09 (Session 17 — Pre-release v1.0.17.0, Jellyfin 12.0.0 GA re-pin + full codebase audit)

**Scope**: Two parts. (a) The Jellyfin 12 GA re-pin and the release mechanics around it — `Jellyfin.Controller`/`Jellyfin.Model` `12.0.0-rc4` -> `12.0.0`; `AssemblyVersion`/`FileVersion` -> `1.0.17.0`; `build.yaml` and `manifest.json` resynced; `agentic/**` excluded from the plugin's compile globs; `agentic/tools/abi-probe/` added. (b) A full security and efficiency audit of every `.cs` file, with fixes applied and verified.
**Triggered by**: User-requested release now that Jellyfin 12 has shipped, with a check that nothing remains outstanding for Jellyfin 12; followed by an explicit request to audit, fix and verify.

**Scope note**: this began as a GA re-pin with no application-code change, then a full security and
efficiency review of the whole codebase was requested and run. Seven issues were found and fixed
across `Data/PairStore.cs`, both lookup services, `Services/SpecialDetectionService.cs`,
`HardLink/HardLinkService.cs` and the four copies of the deletion helper.
`Configuration/configPage.html` and `Web/specialtomovie.js` are unchanged from what Sessions 15 and
16 reviewed and were re-checked rather than re-audited from scratch.

A behavioural harness was written to verify the fixes and promoted to
[`agentic/tools/audit-harness`](tools/audit-harness/): **21 checks, all passing**.

---

### The rc4 -> GA assumption, discharged

The v1.0.16.0 audit shipped against `12.0.0-rc4` and recorded an **accepted risk**: that 12.0.0 GA
would carry no API change from rc4, with no re-verification planned. That assumption is now
**verified rather than carried forward**, and it needed verifying — GA is not identical to rc4.

Method: [`agentic/tools/abi-probe`](tools/abi-probe/) dumps the public and protected API surface of
each version's Jellyfin assemblies (16,096 members for rc4, 16,138 for GA) and diffs them.

**Result: 21 members removed, 63 added.** Every removal is in an area this plugin does not touch:

| Removed | Nature |
| --- | --- |
| `ISessionManager.AddAdditionalUser` / `RemoveAdditionalUser` / `ReportCapabilities` / `ReportNowViewingItem` | Signature change — each gained a leading `controllingSessionId` parameter |
| `JellyfinQueryHelperExtensions.WhereOneOrMany` / `OneOrManyExpressionBuilder` / `WhereReferencedItem*` | Signature change — `IList<T>` -> `IReadOnlyList<T>` |
| `Permission.UserId`, `Preference.UserId` | Signature change — `Guid?` -> `Guid` |
| `ActivityLogQuery.Severity` | Not a real change — a rebind of `Microsoft.Extensions.Logging.Abstractions` from 9.0.0.0 to 10.0.0.0 |
| `ILibraryManager.ValidatePeopleAsync`, `ILibraryManager`/`IItemCountService.GetChildCountBatch` | Genuine removal / return-type change |

The plugin references **none** of them, confirmed by grep over all `.cs` files as well as by the
build. Conversely, every Jellyfin type the plugin does bind to is **byte-identical** between rc4 and
GA — `IExternalUrlProvider`, `GetSmartApiUrl`, `IPluginServiceRegistrator`, `ISubtitleManager`,
`IUserDataManager`, `ITaskManager`, `IMediaSourceManager`, and `Policies.RequiresElevation` were each
diffed individually and are unchanged.

Build against `12.0.0`: **0 warnings, 0 errors** on SDK 10.0.302.

**Runtime-bound surface** (the part a clean compile cannot vouch for) was checked separately: the
plugin uses no reflection against Jellyfin types — the only `System.Reflection` use is
`ClientScriptController` reading an embedded resource out of the plugin's *own* assembly, and
`Plugin.cs` reading its own namespace — so there is no member resolved by name at run time that the
compiler would not have caught.

**Conclusion: the Jellyfin 12 migration is complete.** `targetAbi 12.0.0.0` matches the GA
assemblies' own `assemblyVersion`/`fileVersion`, the README requirements already state Jellyfin
12.0.0 / .NET 10, and no rc-era reference remains anywhere but in historical documents.

---

### Confirmed Issues

#### 1. `PairStore.Save()` could throw into Jellyfin's event dispatch (MEDIUM — robustness/data integrity)
- **File**: `Data/PairStore.cs`
- **Issue**: `Save()` performed `File.Copy`, `File.WriteAllText` and `File.Move` with no error handling, and every mutation (`Upsert`, `UpsertMany`, `Remove`, `RemoveMany`, `Clear`) calls it while holding the store lock. A locked file, a full disk or a briefly unavailable network share therefore threw *out of the mutation*. Two consequences: the in-memory list had already been mutated, so the caller saw a failure for a change that had taken effect; and the call sites include `LibraryEventHandler.OnItemRemoved`, which is **not** wrapped in a try/catch, so the exception would unwind into Jellyfin's own `ItemRemoved` dispatch and could disrupt unrelated subscribers.
- **Status**: **Fixed** — `Save()` now catches `IOException`/`UnauthorizedAccessException`, logs an error naming the path and the number of pairs held in memory, and returns. The store stays newer in memory than on disk until the next successful save, which repairs itself because every save rewrites the whole file. Verified by harness checks *"a failing save does not throw out of Upsert"* and *"...out of Remove or Clear"*.

#### 2. An unreadable pair store took the whole plugin down (MEDIUM — availability)
- **File**: `Data/PairStore.cs`
- **Issue**: `Load()` and `LoadBackup()` caught only `JsonException`. A file that exists but cannot be *read* — locked by another process, a permissions problem, an I/O error — threw `IOException`/`UnauthorizedAccessException` from the `PairStore` constructor. Because `IPairStore` is a DI singleton every other plugin service depends on, that fails the registration and the entire plugin dies with an error that points at DI rather than at the real cause.
- **Status**: **Fixed** — both now catch `JsonException`, `IOException` and `UnauthorizedAccessException`, degrading primary → backup → empty. Verified by harness checks *"a corrupt primary file is recovered from the backup"*, *"a corrupt primary and a corrupt backup degrade to an empty store"*, and *"an unreadable primary file does not throw out of the constructor"*.

#### 3. Provider IDs interpolated unescaped into API URL paths (LOW — input validation)
- **Files**: `Lookup/TmdbLookupService.cs`, `Lookup/TvdbLookupService.cs`
- **Issue**: `imdbId`, `seriesTmdbId` (TMDB) and `episodeTvdbId`, `episodeId` (TVDB) were interpolated straight into request paths. Unlike the force-link values — which `ParseForcedMovie` validates as `tt`+digits or all-digits — these come from **library metadata**: an NFO file beside the media, or whatever a metadata provider wrote. That is not plugin-controlled input. A value containing `/`, `..`, `?` or `#` re-points the request at a different endpoint on the API host or reshapes its query string, and the TMDB URLs carry `api_key=`.
- **Not** a host-level SSRF: the scheme and host are constants, so this is confined to the metadata API being called.
- **Status**: **Fixed** — every string-typed ID is wrapped in `Uri.EscapeDataString`, with a comment at the first site explaining the provenance. TVDB's `movieId` is a `long` and TMDB's `movieId`/`seasonNumber`/`episodeNumber` are numeric, so those were left alone. Verified by five harness checks covering well-formed IDs (unchanged), traversal, query injection and fragment truncation.
- **Note**: this does not contradict the standing false positive *"Language parameter injection in TMDB URLs"*, which concerns `lang` — an admin-set server setting — not provider IDs.

#### 4. Force-link matching scaled as force links × episodes (MEDIUM — efficiency)
- **File**: `Services/SpecialDetectionService.cs`, `ProcessForceLinksAsync`
- **Issue**: For each force link the method scanned the whole Season 0 episode list with `FirstOrDefault`, calling `FormatEpisodeKey(e)` — a string interpolation — on every candidate. 50 force links against 1,000 Season 0 episodes meant up to 50,000 key formats per run, on a task that runs every 6 hours by default.
- **Status**: **Fixed** — the episodes are indexed once into a `Guid`→episode map and a case-insensitive key→episode map, then each force link is two dictionary probes. `TryAdd` preserves the original "first match wins" semantics of `FirstOrDefault`.

#### 5. Destination-library movie filtering repeated per episode (MEDIUM — efficiency)
- **File**: `Services/SpecialDetectionService.cs`, `FindExistingMovie` / `FindExistingMovieByTitle`
- **Issue**: A full scan fetches every movie on the server once, correctly — but narrowing that list to the destination library happened *inside* the per-episode lookup: a `virtualFolders.FirstOrDefault` plus a `Where` over all movies with a nested `Any` of case-insensitive `StartsWith` per library location. The answer is identical for every episode mapped to the same library, so the work grew as episodes × movies × locations. 500 episodes against 5,000 movies is on the order of millions of redundant path comparisons per scan.
- **Status**: **Fixed** — a new private `MovieIndex` holds the movie list and the virtual folders and memoises the filtered list per destination library. It is built once per scan and passed through in place of the raw list, so it is thread-confined to the scan and needs no locking. The uncached fallback (`GetItemList` scoped by `ParentId`) is unchanged for the null-index path.

#### 6. Path containment guard used a bare prefix match (LOW — defence in depth)
- **File**: `HardLink/HardLinkService.cs`, `BuildHardLinkPath`
- **Issue**: The guard tested `resolvedPath.StartsWith(resolvedRoot)` without a trailing separator, so a sibling directory whose name merely begins with the root's — `/media/movies-old` against a root of `/media/movies` — satisfied it. Not reachable today: the path components are `SanitizeFileName` output and the root comes from Jellyfin's library configuration, so the constructed path is always a genuine child. But this is the backstop that is supposed to hold when that stops being true.
- **Status**: **Fixed** — the root gets a trailing `Path.DirectorySeparatorChar` before the comparison. Verified by harness checks *"a sibling directory sharing the root's prefix is not treated as inside it"* (asserting the old predicate accepted it and the new one does not) and *"a genuine child is still accepted"*.

#### 7. `DeleteItemWithFiles` existed in four near-identical copies (LOW — reuse)
- **Files**: `Api/SpecialToMovieController.cs`, `Services/SpecialDetectionService.cs` (as `DeleteLinkedMovieItem`), `EventHandlers/LibraryEventHandler.cs` (two overloads), `Tasks/CleanupTask.cs`
- **Issue**: The same null guard, `GetItemById` miss guard, `DeleteItem(… DeleteFileLocation = true)` call and try/catch, written out five times across four files. One of the `LibraryEventHandler` overloads took a non-nullable `Guid` and omitted the `Guid.Empty` check — harmless, since `GetItemById(Guid.Empty)` returns null, but exactly the kind of drift that duplication invites.
- **Status**: **Fixed** — replaced by `Services/LinkedItemDeleter.DeleteWithFiles(ILibraryManager, ILogger, Guid?)`, a single internal static definition that logs through the caller's own logger so log lines keep their originating category.

---

### Observations (no action taken)

- **`ValidateSameFilesystem` is likely wrong on macOS.** `StatBuf` declares `st_dev` as a `ulong` and relies on it being the first field of `struct stat`. That holds on Linux/glibc, but on macOS `dev_t` is a 32-bit `int32_t` followed by `mode_t` (16-bit) and `nlink_t` (16-bit) — so the 8 bytes read as `st_dev` there actually splice in the file's mode and link count. Two files on the same filesystem with different permissions or link counts would then compare unequal, `ValidateSameFilesystem` returns false, and the episode is stored as an Error pair with "Source and destination are on different filesystems" — silently disabling the plugin's core function. **Not fixed**: the correct change is a platform-specific struct layout, and there is no macOS host here to verify against, so shipping an unverified P/Invoke layout change would be the riskier move. Flagged for a decision.
- **`ApiResponseCache` has no size bound.** Entries expire only by age (`MetadataCacheDays`), so the in-memory dictionary and its on-disk JSON grow with the number of distinct lookups. Not a leak — a very large library with a long cache window is simply the intended cost — but there is no cap if one is ever wanted.
- **`WatchSyncService` reentrancy guard keys on the paired item, not the saved one.** The echo of a sync therefore does not hit the key the guard holds, and one extra round-trip write occurs before the second echo is suppressed. It terminates and converges (the values written are the ones just copied), so this is a redundant write rather than a loop, and it is left alone.
- **`SanitizeFileName` can return an empty string** for a title composed entirely of stripped characters, producing a folder like `" (2019) [JellyfinPlugin-SpecialToMovie]"`. Cosmetic, and not reachable from any real TMDB/TVDB title.
- **README vs. code.** The README documents the cross-link buttons and the ignore list generally, but does not spell out that an ignore entry can now name a **whole series**. Flagged only — the README is not edited without being asked.
- **`plans/cross-link-buttons(DONE).md`** still refers to `12.0.0-rc4`. Left as-is: a completed historical plan, accurate when written.

---

### Re-confirmed, not re-flagged

Checked against the standing false-positive list and found unchanged: the TMDB `lang` query parameter (admin-set server setting), the TVDB bearer token (sent as a header, never in the logged URL), `Marshal.GetLastWin32Error()` under `SetLastError = true` on POSIX, the `ConfigSnapshot` copy that defuses the config-mutation race, API keys shown in the admin-only config page, CSRF (Jellyfin's token auth plus `RequiresElevation` on every mutating endpoint), and case sensitivity in the containment check.

Newly reviewed and clean:

- **`Api/ClientScriptController.cs`** — the plugin's only `[AllowAnonymous]` route. Returns a fixed embedded asset, reflects nothing from the request, and the ETag is the assembly version. Correct.
- **`Web/specialtomovie.js`** — no `innerHTML`, no `eval`, no `Function`. The only `href` written is `parseLink`'s return value, which is always `'#' + hash`, so a `javascript:` URL cannot be constructed.
- **`Providers/CrossLinkUrlBuilder` / `CrossLinkUrlResolver`** — only a GUID, the system ID and a fixed marker char reach the route; `GetSmartApiUrl` output is validated as an absolute http/https URI before being emitted.
- **`Services/ScriptInjectionStartupFilter.cs`** — fails open on every path, and the base-URL prefix is whitelist-checked and discarded rather than escaped.
- **API surface** — every mutating endpoint carries `[Authorize(Policy = Policies.RequiresElevation)]` at the controller level; `RemovePair` gates deletion on the *saved* `AutoDeleteOnRemoval` rather than the caller's word, and refuses to touch pre-existing movies.

---

### PII Sweep

**Ran**: 2026-09-09, over all 55 tracked files, including the working tree's uncommitted release edits.

| Check | Result |
| --- | --- |
| 1. Drive-rooted / UNC / home paths | Clean — matches are regex escapes in source, repo-relative build paths in `CLAUDE.md`, Jellyfin's own install paths in `README.md`, and the sweep patterns matching their own documentation. All known-acceptable. |
| 2. Email addresses | Clean — no matches in any tracked file. |
| 3. This machine's identity (username, hostname, git user name/email) | Clean — the only matches are the project's GitHub owner handle, which is known-acceptable. |
| 4. Dotted quads | Clean — all 20 distinct matches are assembly/ABI version numbers. |
| 5. Comment read-through (538 comment lines) | Clean — every match for personal-sounding language is a generic reference to "the user" meaning the plugin's end user, or README FAQ phrasing addressed to the reader. Nothing identifies a person or machine. |

The newly added `agentic/tools/abi-probe/` files were included and are clean: the probe takes all
paths as arguments, resolves the rest relative to the script's own directory or the system temp
path, and contains no absolute path, credential or machine detail.

---

### Scratchpad & Temporary File Sweep

**Promoted to `tools/`**, two things:

1. The Jellyfin ABI probe, as `agentic/tools/abi-probe/` — `AbiProbe.csproj`, `Program.cs` (a
   `MetadataLoadContext` surface dumper), and `Compare-JellyfinAbi.ps1` (a driver that materialises
   each version's dependency closure, dumps both surfaces and diffs them). It backs the rc4 -> GA
   verification above, and will be needed again at the next Jellyfin version bump. Verified working
   end-to-end from its committed location, reproducing the 21-removed/63-added result.
2. The audit harness, as `agentic/tools/audit-harness/` — 21 behavioural checks over
   `BuildHardLinkPath`, `PairStore` and the provider-ID escaping, referencing the plugin as a
   project so it always runs against the working tree. It is the evidence behind the "Fixed" status
   on issues 1, 2, 3 and 6 above, and is exactly the case the promotion rule exists for: a harness
   cited by an audit but living only in a scratchpad cannot be re-run at the next release.

**Deleted**: two extracted Jellyfin assembly sets (rc4 and GA, 7 assemblies each), two full published
dependency closures (20 and 30 assemblies) and the throwaway reference projects that produced them,
both API surface dumps, the diff output, the probe's scratch build tree, a temporary comment listing
used for the PII sweep, and the scratch copies of the prose spliced into this entry. No Jellyfin
server binary, server data or user data was left in the scratchpad or anywhere else; the ABI driver
script deletes its own working directory on exit unless `-KeepWork` is passed, and the audit harness
deletes its temporary store directory in a `finally`. Both were confirmed after their runs.

Neither promoted tool's `bin/`/`obj/` is tracked — the repository's `.gitignore` matches those at any
depth — and `agentic/**` is excluded from the plugin's own compile globs (issue below), so a tooling
project under `agentic/` cannot leak into the shipped assembly.

---

## Audit: 2026-08-24 (Session 16 — Cross-link settings removal, badge artwork, script delivery)

**Scope**: Everything changed after the Session 15 audit, all of it inside the cross-link feature and none of it released:

1. **Settings removed.** `CrossLinkUrlStyle`, `MovieLinkLabel` and `SpecialLinkLabel` are gone. `CrossLinkUrlResolver` now always emits the full URL when one can be derived and the client script reduces it to a hash in the browser; the captions are constants on the two providers. The "Detail Page Links" section collapsed into a single checkbox in **General**.
2. **The link became a badge.** `Web/specialtomovie.js` gained an inline SVG tile, `rowIsBadges`, `dropSeparator` and the end-of-row placement, applied only when the row it joins is already a row of brand badges.
3. **Script delivery** — the machinery the above two lean on: `ScriptInjectionStartupFilter` and `ClientScriptController`.

**Triggered by**: User request, "audit + fix + verify".

---

### Confirmed Issues

#### 1. The client script never loaded on a base-URL install — MEDIUM — fixed

**Where**: `Services/ScriptInjectionStartupFilter.cs`.

The injected tag was `<script src="/SpecialToMovie/ClientScript">` — root-relative, with no base-URL prefix. `Startup.Configure` wraps the whole pipeline in `app.Map(config.BaseUrl, ...)`, and `MapControllers` lives inside that branch, so on a server configured with a base URL the browser asked for `/SpecialToMovie/ClientScript`, outside the branch, and got a 404. The links kept working as plain text, so nothing looked broken — the badges, the in-app navigation and the row matching simply never appeared, with no error anywhere to explain it.

The filter already knew about base URLs in one direction: an `IStartupFilter` is invoked *before* `app.Map`, so it sees the un-stripped path and an empty `PathBase`, which is why `IsIndexRequest` matches on a suffix. Only the outbound half was missing.

**Resolution**: `GetBasePrefix` derives the prefix from the matched request path and the tag is written as `{prefix}/SpecialToMovie/ClientScript`. The prefix comes off the request line and lands in an HTML attribute, so it is checked against a plain-path whitelist (ASCII alphanumerics, `/-._~`, no `..`) and **discarded rather than escaped** if it fails. There is no SPA fallback in the pipeline, so a crafted path cannot reach the injection branch today; the whitelist is what keeps that true if one is ever added.

#### 2. The script was re-read per request and its ETag did nothing — LOW — fixed

**Where**: `Api/ClientScriptController.cs`.

Every load of the web app called `GetManifestResourceStream` and read the asset into a fresh string. The response carried a version-stamped `ETag`, but MVC does not act on one by itself: the conditional request came back, was ignored, and the full body was sent again. There was also no `Cache-Control`, leaving the asset to heuristic caching — which for a versioned file with no `Last-Modified` is both unpredictable and the wrong failure mode, since a stale copy survives a plugin upgrade.

**Resolution**: the asset is read once into a `static Lazy<string?>` (it is embedded in the assembly and cannot change while the process lives), `If-None-Match` is compared against the ETag and answered with a 304, and `Cache-Control: no-cache` is set so the browser revalidates instead of guessing. The common case is now a conditional request rather than the whole script.

#### 3. Two XML doc comments described settings that no longer exist — LOW — fixed

`CrossLinkUrlBuilder.Details` still spoke of "the configurable button captions" and `CrossLinkUrlResolver`'s constructor parameter of "absolute mode". Both were accurate when written and survived the settings removal. The security-relevant half of the first comment — that only a GUID, the system ID and a marker may be interpolated, because the web client does not escape `Url` — is intact and still correct. The plan document keeps the old names deliberately: it is a historical record, and its as-built table reconciles them.

---

### Design Points Verified (no change needed)

- **`IHttpContextAccessor` is registered by the host.** `CrossLinkUrlResolver` cannot be constructed without it, and the URL providers are built by part discovery where a constructor throw calls `FailPlugin` and disables the entire plugin — so this is worth being sure about rather than assuming. It appears nowhere in `MediaBrowser.Controller` or `MediaBrowser.Common`, but Jellyfin's `Startup.ConfigureServices` calls `services.AddHttpContextAccessor()` directly. A defensive `TryAdd` was added during this audit and then reverted: redundant code carrying a comment about a risk that does not exist is worse than no code.
- **The web-client selectors match what jellyfin-web actually renders.** `renderLinks` sets `.itemExternalLinks` innerHTML to `links.join(', ')`, so the anchors are direct children separated by literal `", "` text nodes — the model `dropSeparator` and the harness are built on. The view root is `<div id="itemDetailPage" ... class="page libraryPage itemDetailPage ...">`, so `#itemDetailPage:not(.hide)` resolves. `.itemExternalLinks` only carries `hide` when it is empty, so it can never be the element that suppresses the plugin's own link.
- **Stripping `Accept-Encoding` really does prevent compression.** `UseResponseCompression` is registered *inside* the base-URL branch, i.e. inside this filter, so the header is already gone when it runs. Removing the response's `ETag`/`Last-Modified` after rewriting is likewise necessary and sufficient — the host sets `Cache-Control: no-cache` on `index.html` itself.
- **The `MutationObserver` cannot loop.** The upgrade pass mutates class, attributes and child order, all of which the observer watches; the pass those mutations schedule matches nothing, because the selector excludes `[data-stm-upgraded]`, and so mutates nothing. It settles after one no-op run.
- **Removing settings did not orphan their storage.** `ShowCrossLinks` is the only cross-link setting in the UI; `InjectClientScript` remains stored but unlisted, and survives a save because the config page mutates the fetched configuration object rather than rebuilding it.
- **The deletion-ordering invariant from Session 15 still holds.** Re-checked mechanically across all six methods that delete media.

---

### Verification

| Check | Result |
| --- | --- |
| `dotnet build -c Release --no-incremental` | 0 warnings, 0 errors |
| Reflection harness against the built DLL — 34 assertions covering `GetBasePrefix`, `IsIndexRequest`, the emitted tag, the embedded resource name, the cached script and the ETag | 34/34 pass |
| Injected tag stays a single element for hostile paths (markup, quotes, angle brackets, spaces, newlines, non-ASCII, `%`, `..`) | Pass — prefix dropped, tag has exactly two quotes and one element |
| Client-script harness against a DOM stub, 31 assertions (badge row vs text row, separator handling, reordering, stale reset, foreign links) | 31/31 pass |
| `node --check` on `Web/specialtomovie.js` and the `configPage.html` script block | Pass |
| Static ordering check: store mutation precedes media deletion in all six deleting methods | Pass |
| `renderLinks`, the detail view root element, and the React tree, read from jellyfin-web `master` | Pass — external links are still rendered only by the legacy controller |
| Line endings across all tracked and new files | 50 LF-only; `README.md` and `.gitignore` hold CRLF in the working tree after editor saves. `core.autocrlf=true` normalises both on commit — confirmed by `README.md` showing 3 changed lines rather than 190 |

**Not verified** — needs a running server, and carried forward from Session 15: a base-URL install end to end; two `IStartupFilter`s (this one and Jellyfin Enhanced) buffering the same `index.html` in both install orders; behaviour behind a reverse proxy; the emitted full URL in non-web clients.

**Future risk, not a finding**: jellyfin-web is migrating to React, and the detail view now lives under `src/apps/legacy/`. External links are still rendered only there, so both the selector and the `", "`-separated structure hold today; a React detail page would change the whole client-side half of this feature, not just the selector.

---

### PII Sweep

**Ran** over all 53 files — 44 tracked ones that still exist plus the 9 new untracked ones — using the patterns in [`CLAUDE.md`](CLAUDE.md#pii--documentation-sweep). **Clean.**

- Checks 1–2 (drive-rooted and UNC paths, home directories, email addresses): no findings. The matches returned were regex escape sequences in source, relative MSBuild paths in the `.csproj`, the sweep patterns matching their own documentation, and the known-acceptable Jellyfin install path in `README.md`.
- Check 3 (this machine's username, hostname, git identity): matched only the project's own GitHub owner handle in `manifest.json`, `build.yaml`, `README.md` and `agentic/CLAUDE.md` — already on the known-acceptable list as the project's public identity.
- Check 4 (dotted quads): 20 distinct matches, all assembly or ABI versions.
- Check 5 (comment read-through): all 533 comment lines were counted and every one in the files touched this session was read individually — all technical, none personal. The badge comment block was checked specifically for first-person narration and design-session chatter.
- **No PII finding.** One stylistic correction was made rather than a finding: a first-person "our own link" in this entry, rewritten impersonally per the docs convention. The editorial "we"/"our" throughout the plan document and the original design doc means *the plugin*, identifies nobody, and is not a finding — do not re-flag it.
- The sweep was re-run over the **final** state of every file, after the audit entry and the `HANDOFF.md` updates were written. An earlier pass had run before those existed, which would have left the two largest pieces of new prose in this session unswept — the sweep has to be the last thing done, not the last thing done to the code.

---

### Previous Audit Items — Status Check

No open item from Sessions 1–15 was reopened. Finding 1 above is newly identified: Session 15 audited the filter's fail-open behaviour and its response handling, but never asked what the injected `src` resolves to on a server that is not hosted at the root.

---
---

## Audit: 2026-08-24 (Session 15 — Cross-link buttons, series ignore, deletion prompt)

**Scope**: Three features implemented in one pass and audited together. Unreleased — no version bump yet.

1. **Cross-link buttons** — all three phases of [`plans/cross-link-buttons(DONE).md`](plans/cross-link-buttons%28DONE%29.md) plus the `PairStore` lookup indexes. New: `Providers/` (4 files), `Api/ClientScriptController.cs`, `Services/ScriptInjectionStartupFilter.cs`, `Web/specialtomovie.js`. Modified: `PairStore`, `PluginConfiguration` (two new properties, `ShowCrossLinks` and `InjectClientScript`), `PluginServiceRegistrator`, `configPage.html`, `.csproj`, `README.md`.
2. **Ignore an entire series** — `SpecialDetectionService.IsIgnored`, one matcher shared by the detection path and ignore-list enforcement; the list now accepts a series name or series item ID alongside the two episode forms.
3. **Remove-selected honours the auto-delete setting** — `RemovePair` takes a `DeleteMedia` flag. With `AutoDeleteOnRemoval` on, the confirmation states that the pairs and their plugin managed movie items will be removed together and confirming does both; with it off, only the pairs are removed. Cancelling is a no-op in both cases.

**Triggered by**: User request to implement all three, then "audit + fix + verify". Issues 3 and 4 come from a second audit pass the same day, after the removal dialogue was reworked so that Cancel is always a no-op and after the plan document was renamed.

---

### Confirmed Issues

#### 1. ItemRemoved cascade deletes the original episode file — HIGH — fixed

**Where**: `Api/SpecialToMovieController.cs` (`RemovePair`, `RemoveAllLinks`, `RemoveForceLinkedPairs`), `Services/SpecialDetectionService.cs` (`EnforceIgnoreList`), `Tasks/CleanupTask.cs` (`ValidatePair`).

Each of these deleted a movie item through `LibraryManager.DeleteItem` **while the pair was still in the store**. Jellyfin raises `ItemRemoved` for that deletion; `LibraryEventHandler.OnItemRemoved` matches the still-present pair by movie ID, reads it as a user-initiated removal, and with `AutoDeleteOnRemoval` **and** `TwoWayDeletion` both enabled calls `DeleteItemWithFiles(pair.EpisodeItemId)` — deleting the user's original episode file in response to an action that never asked for it.

`LibraryEventHandler` already documents the correct ordering in a comment ("Remove pair first to prevent cascading events"); these five call sites did not follow it.

Only `RemovePair` is new code — the new `DeleteMedia` flag put a deletion on that path for the first time. The other four are pre-existing and were never flagged in Sessions 1–14, because no earlier audit traced a deletion call back into the plugin's own event handler. `ValidatePair` is not actually reachable: its branch only runs once the episode is already gone. It was reordered for consistency.

Severity is raised by feature 2 above: an ignore-list entry now covers a whole series, so a single edit that previously affected one special can drive this path across every special in a series at once.

**Resolution**: all five sites now remove the pair — or persist it with `MovieItemId` cleared — **before** any media is deleted. `RemoveAllLinks` and `RemoveForceLinkedPairs` collect the item IDs, commit the store change, then delete in a second loop. Verified by a static check that the first `_pairStore` mutation precedes the first delete call in every one of the six methods that delete media.

**Rule for future work**: a pair must leave the store, or stop pointing at the item, before that item is handed to `LibraryManager.DeleteItem`.

#### 2. Remove-selected prompt overcounted deletable items — LOW — fixed

**Where**: `Configuration/configPage.html`, `removeSelectedPairs`.

The count offered in the new prompt ("Also delete the N plugin managed movie item(s)…") included pairs with no `MovieItemId` — dry-run and pending pairs, which have no item to delete. A selection containing only such pairs would offer a deletion that silently does nothing. **Resolution**: the filter now also requires `p.MovieItemId`, so an all-dry-run selection falls through to the standard confirmation instead.

#### 3. Unsaved auto-delete checkbox could drive a real deletion — MEDIUM — fixed

**Where**: `Configuration/configPage.html` (`updateTwoWayState`, `removeSelectedPairs`), `Api/SpecialToMovieController.cs` (`RemovePair`).

The config page tracked "Remove plugin managed items automatically" from the **live checkbox**, updated on every `change` event, while `RemovePair` trusted the client's `DeleteMedia` flag outright and never consulted the stored configuration. Ticking the checkbox and removing pairs *without saving* therefore deleted movie items and their files even though the saved configuration said to keep them — an irreversible action driven by a setting the user had not committed to. The opposite order (unticking without saving) failed safe.

**Resolution**, both sides:

- `RemovePair` now honours `DeleteMedia` only when the **stored** `AutoDeleteOnRemoval` is true, alongside the existing `IsExistingMovie` guard. The API can no longer be talked into a deletion the saved configuration forbids, whatever the caller sends.
- The page's `autoDeleteOnRemoval` is now set from the saved configuration only — once on load and again after a save succeeds — never from the checkbox. Otherwise the prompt would promise a deletion the server would then refuse.

#### 4. Renaming the plan document broke three links — LOW — fixed

**Where**: `agentic/AUDIT.md` (2), `agentic/IDEAS.md` (1).

`plans/cross-link-buttons.md` was renamed to `plans/cross-link-buttons(DONE).md`. Beyond the stale targets, the new name contains parentheses, which terminate a markdown link early and cannot simply be pasted in. **Resolution**: all three now point at `plans/cross-link-buttons%28DONE%29.md`, percent-encoded. A link checker over every doc — internal anchors and relative file targets, with percent-decoding — passes across all eight.


#### 5. Button icon rendered black on every theme — LOW — fixed, then superseded

**Where**: `Web/specialtomovie.js`, `injectStyles`.

The original chain-link icon was drawn with `stroke="currentColor"` inside an SVG data URI used as
`background-image`. An SVG loaded as an image is an independent document that cannot see the host
page's CSS, so `currentColor` resolved to its own initial value — black — rather than inheriting the
button's text colour. On Jellyfin's default dark theme that is a black icon on a dark background.

Fixed first by painting the glyph through a CSS mask with `background-color: currentColor`. The link
was then redesigned as a self-coloured badge (below), which removes the dependency on the host's
colour altogether. Harness assertions keep `currentColor` out of the data URI either way.

#### 6. The link did not match the row it sits in — LOW — fixed

**Where**: `Web/specialtomovie.js`, `upgradeLinks`.

The external-links row is rendered one of two ways depending on what else is installed: Jellyfin's
own stock rendering is text links joined by `", "` ([`renderLinks`](https://github.com/jellyfin/jellyfin-web) builds `links.join(', ')`), while plugins such as Jellyfin
Enhanced replace them with brand logo tiles. The script styled its link the same way regardless, so
it was guaranteed to be the odd one out in one of the two — a lone text link among badges, or a lone
badge among text.

**Resolution**: the script now reads the row and matches it. A row counts as badges when a sibling
link renders a picture and no words; a link showing an icon *and* text — Jellyfin Enhanced's
Letterboxd links, for instance — is still a text row. In a badge row the link becomes a circular
tile in Jellyfin's blue-to-purple carrying two interlocking rings, keeps its caption as `title` and
`aria-label`, drops the one `", "` separator that would otherwise dangle beside it, and moves to the
end of the row. In a text row it stays plain text and is left where it is.

Detection is deliberately behavioural rather than a check for a named plugin or CSS class, so it
neither depends on Jellyfin Enhanced being installed nor breaks when it renames something. Both
paths are covered by the client-script harness, which models a badge row and a text row.

---

### Design Points Verified (no change needed)

- **Provider construction cannot fail the plugin.** `LinkedMovieUrlProvider` and `LinkedSpecialUrlProvider` are discovered by Jellyfin's own part scan and deliberately not registered in `PluginServiceRegistrator`. Verified by reflection against the built assembly: both are public and concrete, and every constructor parameter (`IPairStore`, `ILibraryManager`, `CrossLinkUrlResolver`, `ILogger<T>`) resolves from the container. A throwing constructor would trigger `FailPlugin` and disable the whole plugin; `Name` is read at startup during the provider sort, so it degrades rather than throws too.
- **No unescaped user input reaches an href.** The web client escapes `ExternalUrl.Name` but **not** `.Url`. Only a GUID, the server's system ID and a fixed marker character are ever interpolated into the URL, and no part of it is user-supplied; the captions are compile-time constants returned through `Name`, which the client escapes in any case. The resolver validates the `GetSmartApiUrl` result with `Uri.TryCreate` and an http/https scheme check before emitting it, and falls back to the bare hash route on every failure path.
- **The startup filter fails open.** Non-GET, non-200 and non-HTML responses are never buffered; a downstream exception restores the original body stream and rethrows; a missing `</body>` serves the buffered HTML unchanged. It is on by default and, after the settings were pared back, no longer switchable from the config UI — `InjectClientScript` survives only in the stored configuration. That removes the volunteer-only escape hatch the default-on decision leaned on, which makes the fail-open discipline non-negotiable rather than merely tidy.
- **The anonymous route is justified.** `SpecialToMovie/ClientScript` is the plugin's only `[AllowAnonymous]` endpoint. The browser requests it while loading the web app, before sign-in, so it cannot require auth. It returns a fixed embedded asset and reflects nothing from the request.
- **`PairStore` indexes are correct under in-place mutation.** Callers mutate the live `LinkedPair` reference and then call `Upsert`, which defeats incremental key eviction. The wholesale `RebuildIndexes()` that shipped instead makes stale keys structurally impossible. See [`plans/cross-link-buttons(DONE).md` §5.4](plans/cross-link-buttons%28DONE%29.md#54-pairstore-indexes-in-scope-for-this-change).
- **Accepted limitation, documented**: the buttons are not permission-aware. A user who cannot see the item on the other side of the link still sees the button and lands on an error page. Recorded in `README.md` and in the plan; it mirrors how Jellyfin's own external links behave.

---

### Verification

| Check | Result |
| --- | --- |
| `dotnet build -c Release --no-incremental` | 0 warnings, 0 errors |
| Reflection probe against the built DLL — provider/filter/controller shape, constructor resolvability, embedded resource name | Pass |
| `PairStore` harness, 19 assertions (index eviction after in-place mutation, null/empty movie IDs, batch upsert, load, clear) | 19/19 pass |
| Client-script harness against a DOM stub, 14 assertions (both URL forms, marker parsing, target handling, stale-link reset, foreign links untouched) | 14/14 pass |
| `node --check` on `Web/specialtomovie.js` and the `configPage.html` script block | Pass |
| Static ordering check: store mutation precedes media deletion in all six deleting methods | Pass |
| Markdown anchors and relative links across the changed docs | Pass |
| Line endings across all tracked files | Uniformly LF, unchanged by this work. `README.md` was later saved as CRLF by an editor; `core.autocrlf=true` normalises it, and its diff stays at 3 added lines with no whitespace churn |

**Not verified** — needs a running server, and listed in the plan's testing checklist: behaviour of each URL form in non-web clients; two `IStartupFilter`s (this one and Jellyfin Enhanced) buffering the same `index.html` in both install orders; absolute mode behind a reverse proxy and under a configured base URL.

---

### PII Sweep

**Ran** over all 52 files — 46 tracked plus the 6 new untracked ones — using the patterns in [`CLAUDE.md`](CLAUDE.md#pii--documentation-sweep). **Clean.**

- Checks 1–2 (drive-rooted and UNC paths, home directories, email addresses): no findings. The matches returned were regex escape sequences in source, relative MSBuild paths in the `.csproj`, and the known-acceptable Jellyfin install path in `README.md`.
- Check 3 (this machine's username, hostname, git identity): no findings.
- Check 4 (dotted quads): 20 distinct matches, all assembly or ABI versions.
- Check 5 (comment read-through): every comment line in the six new files read individually — all technical, none personal. The new plan document was additionally checked for scratchpad paths, temp directories, hostnames, ports and first-person narration: none.

---

### Previous Audit Items — Status Check

No open item from Sessions 1–14 was reopened by this change. The `ItemRemoved` cascade above is newly identified, not a regression of a closed finding.

---
---

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
