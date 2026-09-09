# webclient-harness

DOM harness for [`Web/specialtomovie.js`](../../../Web/specialtomovie.js), the detail-page client
script that turns the plugin's cross-links into badges and keeps clicks inside the app.

## Why this exists

The script is progressive enhancement layered over a row of links the server renders, so nearly
everything that can go wrong with it is a DOM question rather than a logic one:

- does it recognise that the row is rendered as brand badges rather than text?
- does it stay legible against whatever theme is actually in use?
- does it end up last in the row, and stay there when another plugin appends after it?
- does a click stay inside the app, including the first click after a re-render?

None of that is answerable by reading the file, and every one of those has been wrong at least once
in a shipped release. The bugs behind checks 1, 2 and 4 all reached users.

## Running it

```
cd agentic/tools/webclient-harness
node test.js
```

It defaults to the repository's own `Web/specialtomovie.js`; pass a path to check a different copy.
Exit `0` = every check passed, `1` = at least one failed, `2` = the script could not be read.

`jsdom` is **not** committed - `node_modules/` is gitignored. Run `npm install` in this directory
once before the first run; `package.json` and `package-lock.json` pin the exact tree, so the install
is reproducible.

jsdom does not implement `getComputedStyle` for pseudo-elements and prints a `Not implemented`
notice when the script probes `::before` / `::after` for a background image. That path is guarded and
falls through correctly; the notices are noise, not failures. Filter them with
`node test.js 2>&1 | grep -v "Not implemented"`.

## What it covers

| # | Check |
| --- | --- |
| 1 | A row of brand badges is recognised, and the link renders as a badge with its caption kept as the accessible name |
| 2, 3 | The text form picks its colour from the real backdrop — light on dark, dark on light |
| 4 | A click on a freshly re-rendered, not-yet-upgraded anchor still stays in the app |
| 5 | Ctrl-click still opens a new tab, because that is the user's choice to make |
| 6 | The link returns to the end of the row after another plugin appends to it |
| 7 | The move budget is released once the row settles, so ordinary re-appends cannot exhaust it |
| 8 | Links belonging to other plugins are neither upgraded nor swallowed by the click handler |
| 9 | Loading the script a second time is inert |
| 10 | The badge row is found and joined even when it is a **different container** from the one the cross-link was rendered into |
| 11 | Size and alignment are copied from a neighbouring badge rather than hard-coded |

## Limits

jsdom computes style but does not lay out, so `getBoundingClientRect` returns zeroes. Two things
therefore go unexercised here and need a real browser:

- the shape-based fallback in `captionSuppressed`, which catches a badge whose caption is hidden on a
  child element rather than on the anchor. Checks 1 and 8 cover the CSS-based paths around it.
- the height and width copy in `matchBadgeMetrics`. Check 11 pins the properties that come from
  computed style — display, vertical-align, margins — and asserts the size copy is *skipped* rather
  than applied as a zero-sized box, but the measurement itself cannot be verified without layout.
