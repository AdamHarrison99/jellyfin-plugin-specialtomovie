# tools/

Reusable tooling for this project. **Anything worth running a second time lives here** — not in a
scratchpad, not in a system temp folder, not quoted only inside an `AUDIT.md` entry.

Scratch directories are wiped between sessions. A harness written there is gone by the next release,
and the audit that cited it can no longer be reproduced. Moving it here makes it version controlled,
published with the code, and reviewable.

## What belongs here

- Test and verification harnesses — reflection probes against the built DLL, DOM-stub harnesses for
  `Web/specialtomovie.js`, assertion scripts backing an audit entry
- ABI and compatibility probes against a target Jellyfin server version
- Build, packaging, and release helpers beyond the plain `dotnet build` documented in `CLAUDE.md`
- Any one-off script that took real effort to get right and would be rewritten from scratch otherwise

## What does not belong here

- **Jellyfin server binaries or any server/user data.** Never commit extracted server assembly sets,
  a `library.db`, plugin configuration XML, `PairStore` JSON from a live install, API keys, device
  identifiers, user or media-library listings, or exported logs. A tool that needs a server assembly
  resolves or downloads it at run time into a temporary directory and deletes it afterwards.
- Build output — `bin/`, `obj/`, and the shells probe projects leave behind
- Cloned third-party repositories, which are re-creatable from their source
- Backups of `agentic/` or of the source tree
- Throwaway experiments that answered one question and will not be asked again — delete those

## Rules for anything added here

1. `agentic/` is public. A tool must contain no absolute or drive-rooted path, username, home
   directory, hostname, IP address, network share, email address, credential, or personal
   media-library name. Take paths as arguments or resolve them relative to the repository root.
2. Never reference a file outside the repository. Configuration a tool needs — API keys in
   particular — is passed in at run time, never committed and never pointed at by path.
3. Write no data into the repository at run time. A tool that produces output writes it to a
   temporary directory the caller supplies, and the caller cleans it up.
4. Add a row to the table below when adding a tool, and remove the row when removing one.

## Inventory

| Tool | What it does | How to run it |
| --- | --- | --- |
| [`abi-probe/`](abi-probe/) | Diffs the public + protected API surface of two Jellyfin package versions, so a server upgrade can be checked against what the plugin actually binds to instead of assumed safe. Materialises each version's dependency closure in a temporary directory and deletes it afterwards. | `pwsh agentic/tools/abi-probe/Compare-JellyfinAbi.ps1 -From 12.0.0-rc4 -To 12.0.0` — exit 0 = identical, 1 = differences listed, 2 = probe failed. Add `-KeepWork` to keep the surface dumps. |
| [`audit-harness/`](audit-harness/) | Behavioural checks for the parts of the plugin that run without a Jellyfin server: `BuildHardLinkPath` containment and filename sanitisation, `PairStore` persistence/corruption recovery/write-failure tolerance, and the URL escaping applied to provider IDs. References the plugin as a project, so it always tests the working tree. | `dotnet run -c Release --project agentic/tools/audit-harness` — prints PASS/FAIL per check; exit 0 = all passed, 1 = at least one failed. |
| [`webclient-harness/`](webclient-harness/) | Two harnesses for `Web/specialtomovie.js`. `test.js` (jsdom, 46 checks) covers behaviour: rendering as an icon, the accessible name surviving, copies on hidden detail pages being left alone, staying last in the row, clicks staying in the app, and no geometry being written to the anchor. `test-layout.js` (real Edge/Chrome via `playwright-core`, 66 checks) covers what jsdom cannot lay out: icon size taken from the row, spacing matched to the row's own median gap, and vertical centring — measured by hit-testing the painted pixels, since the anchor's own rect is zero-height and the correction is a paint-only offset. Four of its scenarios change the row *after* the icon has been placed (the element upgrade, a late stylesheet, a hidden neighbour, a wrap and a resize) and assert the correction still fits the row it is in. Needs `npm install` in that directory once; `node_modules/` is gitignored and the lockfile pins the tree. | `npm test` from `agentic/tools/webclient-harness` runs both; `npm run test:layout` for the browser half; `node measure.js` for the cost of re-measuring and `node measure-idle.js` for the cost of a pass over a row that has not moved. Exit 0 = all passed, 1 = at least one failed, 2 = no browser found. |
| [`align-fix.js`](align-fix.js) | Places the cross-link icon from the browser console on a server running a build whose own placement is wrong, without installing anything. Carries the same rules as `Web/specialtomovie.js`, so pasting it is how a placement fix is confirmed on a real row — with whatever logo CSS, theme and other plugins the install has — before the build that contains it ships. It duplicates the algorithm rather than sharing it, because it has to run beside a copy of the script it is correcting: change the two together or it stops being evidence. | Paste the file's contents into the DevTools console on a detail page; `__stmAlignFix.stop()` ends it, a reload undoes it. To check it against a released build: `node test-layout.js path/to/that/specialtomovie.js ../align-fix.js` from `webclient-harness`. |
| [`align-report.js`](align-report.js) | Browser-console report on the cross-link icon's placement on a **real** server, where the row carries whatever logo CSS, theme and other plugins the install has. Prints the drift (the correction the icon carries against the one the row asks for now), every element in the row including the ones nothing draws, and the same figures after forcing a re-measure — which separates a stale measurement from one that is wrong on today's row. Names providers and link captions only: no server address, item id or user detail. | Open the detail page in a desktop browser, paste the file's contents into the DevTools console, and press Enter. The report is copied to the clipboard. |
| [`check-comments.mjs`](check-comments.mjs) | Enforces the comment rule over everything outside `agentic/`: at most two adjacent comment lines, 100 characters a line, 180 across a run, two sentences, no rationale connectives, no history, no `///` XML docs, no `/* */` blocks, no commented-out code, no comment that restates the line below it. Also rejects references to documents that do not exist in the published repository. Rationale that fails it belongs in [`../ARCHITECTURE.md`](../ARCHITECTURE.md). | `node agentic/tools/check-comments.mjs .` for the whole tree (the release gate); no argument lints only what the branch changed; `COMMENT_LINT_BASE=HEAD` lints uncommitted work. Exit 0 = clean, 1 = violations, 2 = usage error. |

The pre-release audit sweeps the scratchpad for tooling that should have been promoted here — see
`CLAUDE.md` → Pre-Release Audit → Scratchpad & Temporary File Sweep.
