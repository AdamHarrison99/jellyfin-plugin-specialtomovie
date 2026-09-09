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
| `node test-layout.js` | real browser | measurement and alignment - 52 checks |

`node measure.js` is a diagnostic rather than a test: it counts the style resolutions and forced
layouts one upgrade pass causes, for a text row and a logo row. There is no pass or fail - compare
two revisions by pointing it at each in turn. It found the two efficiency faults fixed after v1.0.18,
neither of which was apparent from reading the code.

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

Four scenarios: logos at 25px, logos at 36px (a fixed 25px box passes the first and fails this one,
which is the whole reason the size is measured), a link starting mid-row so the move path runs, and a
plain text row with no logo CSS at all.

**Vertical position is measured by hit-testing the painted pixels**, not by reading a rect. Neither
kind of link in this row can be measured from its own box: a link showing a logo is an inline anchor
with `font-size: 0`, so its rect collapses to zero height on the baseline, and the icon's vertical
correction is a relative offset on the `::before`, which moves paint without moving layout. Reading
the rect reported the icon 4.5px out on a text row when it was in fact half a pixel out.

What remains unverified anywhere is visual appearance: that the artwork reads clearly at the row's
size, and that its colour sits well beside real brand logos. No harness answers that.
