# webclient-harness

DOM harness for [`Web/specialtomovie.js`](../../../Web/specialtomovie.js), the detail-page client
script that renders the plugin's cross-links as icon buttons and keeps clicks inside the app.

## Why this exists

The script is progressive enhancement layered over a row of links the server renders, so nearly
everything that can go wrong with it is a DOM question rather than a logic one:

- does it render as an icon at all, and does the caption survive as the accessible name?
- is the copy on a hidden detail page left exactly as the server rendered it?
- does it end up last in the row, and stay there when another plugin appends after it?
- does a click stay inside the app, including the first click after a re-render?
- is the icon the size, spacing and vertical position the rest of the row uses?

None of that is answerable by reading the file, and every one of those has been wrong at least once
in a shipped release.

## Running it

```
cd agentic/tools/webclient-harness
npm test
```

That runs both halves:

| Script | Engine | Covers |
| --- | --- | --- |
| `node test.js` | jsdom | behaviour - 46 checks |
| `node test-layout.js` | real browser | measurement and alignment - 66 checks |

Two diagnostics sit beside them rather than tests - neither has a pass or fail, and both are read by
comparing two revisions:

| Script | Question it answers |
| --- | --- |
| `node measure.js` | What does re-measuring cost? Counts style resolutions and forced layouts in one upgrade pass, for a text row and a logo row. It found the two efficiency faults fixed after v1.0.18, neither of which was apparent from reading the code. |
| `node measure-idle.js` | Is a settled row measured again at all? Ten passes driven by a change elsewhere on the page. Style-attribute writes must be zero, or a correction that re-derives itself can oscillate on an idle page. |

Both browser-driven scripts share [`browser.js`](browser.js), which prefers `STM_BROWSER`, then
`PATH`, then the vendors' default install locations.

Each defaults to the repository's own `Web/specialtomovie.js`; pass a path to check a different copy,
which is how a shipped build or a previous release can be tested. Exit `0` = every check passed,
`1` = at least one failed, `2` = the script could not be read, or no browser was found.

`test-layout.js` drives an **already-installed** Edge or Chrome through `playwright-core`, so no
browser is downloaded. Set `STM_BROWSER` to point at a specific executable if neither is found on one
of the usual paths.

`jsdom` is **not** committed - `node_modules/` is gitignored. Run `npm install` in this directory
once before the first run; `package.json` and `package-lock.json` pin the exact tree, so the install
is reproducible.

## What `test.js` covers

| # | Check |
| --- | --- |
| 1, 2 | The outcome is identical on a text row and on a row of logos — the script no longer tries to work out which kind of row it is in, which is what removed a whole class of bug |
| 3 | Copies on hidden detail pages are left unstyled, and styling is stripped from one that becomes hidden |
| 4 | A click on a freshly re-rendered, not-yet-upgraded anchor still stays in the app |
| 5 | Ctrl-click still opens a new tab, because that is the user's choice to make |
| 6 | The link returns to the end of the row after another plugin appends to it |
| 7 | The move budget is released once the row settles, so ordinary re-appends cannot exhaust it |
| 8 | Links belonging to other plugins are neither upgraded nor swallowed by the click handler |
| 9 | Loading the script a second time is inert |
| 10 | A separator is put back when the link is moved to the end |
| 11 | **No geometry is written to the anchor** — the regression guard for three releases of bugs caused by doing exactly that |

## What `test-layout.js` covers

jsdom computes style but does not lay out, so `getBoundingClientRect` returns zeroes there and the
two measuring parts of the script are invisible to `test.js`: `iconSize`, which takes the icon's box
from a link the web client rendered, and `matchRow`, which matches the row's own spacing and vertical
centre. This half drives a real engine for those.

Four scenarios cover a row that is already finished: logos at 25px, logos at 36px (a fixed 25px box
passes the first and fails this one, which is the whole reason the size is measured), a link starting
mid-row so the move path runs, and a plain text row with no logo CSS at all.

Four more cover a row that is **still settling**, which is where the field defect lived. They build
the row the way the web client really does — `is="emby-linkbutton"` anchors joined with `", "`, with
jellyfin-web's own `emby-button` rules (`display: inline-flex; vertical-align: middle`) — and then
change it after the icon has been placed:

| Scenario | What it reproduces |
| --- | --- |
| Row upgraded after the icon was placed | The `emby-button` class and the logo CSS land after the first measurement, changing every link's shape |
| Hidden link directly before the icon | The sibling the icon would align to has no box, so it cannot say where the row sits |
| Narrow row, then widened | The icon wraps to a line of its own, and must not be dragged up into the line above; widening re-measures |
| Logo CSS arriving late, with no DOM change | The hardest case: a stylesheet restyles the row with nothing for a `MutationObserver` to see |

Each of those asserts **drift**: the correction the icon is carrying, minus the correction this row
asks for now. Zero means the icon is placed for the row as it stands; anything else is the number of
pixels it is out by, and is exactly what a user sees. Against the v2.0.0 script all four fail.

## Checking a console script

A second path models a script pasted into a page that is already running: it is injected after the
row has settled, and the checkpoints before that are printed rather than asserted, since they
describe the script being corrected rather than the correction.

```
node test-layout.js path/to/v2.0.0/specialtomovie.js ../align-fix.js
```

That is how [`../align-fix.js`](../align-fix.js) is kept honest — 63 checks pass against a v2.0.0
script that fails 10 of them on its own.

For a report from a real server rather than a built row, paste
[`../align-report.js`](../align-report.js) into the browser console on the detail page.

**Vertical position is measured by hit-testing the painted pixels**, not by reading a rect. Neither
kind of link in this row can be measured from its own box: a link showing a logo is an inline anchor
with `font-size: 0`, so its rect collapses to zero height on the baseline, and the icon's vertical
correction is a relative offset on the `::before`, which moves paint without moving layout. Reading
the rect reported the icon 4.5px out on a text row when it was in fact half a pixel out.

What remains unverified anywhere is visual appearance: that the artwork reads clearly at the row's
size, and that its colour sits well beside real brand logos. No harness answers that.
