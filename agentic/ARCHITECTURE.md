# Architecture — why the code is shaped the way it is

[`HANDOFF.md`](HANDOFF.md) is the map: which file does what, and where to start reading. This file is
the **rationale** — the reasoning that used to live in source comments and no longer may.

The comment rule (enforced by [`tools/check-comments.mjs`](tools/check-comments.mjs)) is that a
comment is a short note making the next line readable: at most two adjacent lines, no rationale
connectives, no history. Everything longer belongs here. When you remove an explanation from a
comment, put it in this file under the matching section rather than deleting it — the reasoning is
the expensive part and most of it was paid for with a bug.

Sections are ordered by feature, not by folder.

---

## Cross-link buttons

A linked pair shows a button on each side's detail page: the special links to the movie, the movie
links back to the special. Four pieces cooperate.

### `Providers/CrossLinkUrlBuilder.cs`

The route shape has exactly one definition. `Details()` emits
`#/details?id={guid:N}&serverId={systemId}&stm={marker}`, where the marker is `m` for a link
pointing at the movie and `s` for one pointing at the special.

**Only a GUID, the server's system ID and a fixed marker character may ever reach this string.** The
web client interpolates an external URL into an `href` attribute without escaping it, so
user-supplied text added here would be an injection point. Button captions travel through the
provider's `Name` instead, which the web client *does* escape.

### `Providers/CrossLinkUrlResolver.cs`

The only part of the feature registered in the DI container. The two `IExternalUrlProvider`
implementations are found and constructed by Jellyfin's own part discovery, so registering them
would construct them twice; the resolver they depend on has to be registered because constructor
arguments must be resolvable from the container.

A **full** URL is emitted whenever one can be derived from the in-flight request, since that is the
only form a phone, tablet or TV app can follow. The web client neither needs it nor wants it — a
full URL opens a new tab and reloads the whole app — so the client script reduces these links back
to their hash before the user can click one. That division of labour is what removes any setting for
the user to get wrong.

Three guards, in order:

1. **No ambient request** means the DTO is not being built for a client (a scheduled task, a session
   message). The bare hash route is returned instead of a guessed hostname.
2. **`GetSmartApiUrl` can derive the host from the request itself**, so its result is validated as an
   absolute http/https URI before being emitted — the web client writes it straight into an `href`.
3. **The returned base already carries any configured base-URL path**, so the prefix is never added
   a second time.

### `Providers/LinkedMovieUrlProvider.cs` and `LinkedSpecialUrlProvider.cs`

Mirror images; the same construction and failure rules apply to both.

Jellyfin discovers these classes itself and constructs them from the *root* service provider, so
they must not appear in `PluginServiceRegistrator`. A throw from the constructor fails the whole
plugin, and a throw from `Name` or `GetExternalUrls` breaks the item detail response for that item —
so both are written to degrade rather than throw.

`Name` is a constant. Jellyfin reads it at startup to sort providers as well as per request, and it
is the one part of the link the web client escapes before rendering.

`GetExternalUrls` returns a **materialised** result rather than an iterator: the caller enumerates
the sequence after the method has returned, which would put any `try`/`catch` inside the iterator out
of reach of the failure it is there to catch. The pair's movie is re-checked against the library so a
movie deleted outside the plugin does not render a dead button.

### `Api/ClientScriptController.cs`

The plugin's **only anonymous route**. The browser requests it while loading the web app, before
anyone has signed in, so it cannot require authentication. It returns a fixed embedded asset and
reflects nothing whatsoever from the request.

The asset is embedded in the assembly and cannot change while the process is running, so it is read
once rather than on every page load. The ETag is version-stamped, so upgrading the plugin invalidates
any cached copy; it is paired with `no-cache`, which asks the browser to revalidate rather than to
stop caching, making the common case a conditional request instead of the whole body every time. MVC
does not act on an ETag by itself, so the 304 is returned explicitly — without that the header is
decorative and every request still carries the full script.

### `Services/ScriptInjectionStartupFilter.cs`

Jellyfin offers plugins no hook for adding a script to the web client, and writing into the web
folder on disk needs a writable install and is undone by every jellyfin-web update. Rewriting the
response as it is served keeps the change self-contained.

The filter is **additive and fails open**: every unexpected condition results in the original
response being served untouched. It is enabled by default, so that property is what makes it safe —
each early return in the file is a pass-through.

- **Registered ahead of the rest of the pipeline** so it runs outermost. Stripping `Accept-Encoding`
  then reliably yields a response body that can be read.
- **`IsIndexRequest` matches on a suffix, not on equality**, so a server hosted under a base-URL
  prefix still matches.
- **The base-URL prefix is recovered from the request path.** Because the middleware is outermost —
  ahead of the `Map` the server wraps its whole pipeline in — a base-URL install still carries its
  prefix on `HttpRequest.Path` while `PathBase` is empty. The script tag has to carry the same
  prefix: a root-relative `src` would point the browser at a path the server does not serve, and the
  enhancement would silently never load.
- **That prefix is written into an HTML attribute**, so anything that is not a plain path is
  discarded rather than escaped. Without the check, a request whose path merely *ends* in
  `/web/index.html` could reflect markup into the served page.
- **Only a GET has a body worth rewriting.** Buffering a HEAD would produce a `Content-Length` that
  does not match what the host intends to send.
- **The request is normalised** so the static file handler returns a complete, uncompressed 200: a
  compressed or partial response cannot be rewritten correctly.
- **An exception during buffering is rethrown, not swallowed.** The buffered bytes never reached the
  client, so restoring the real stream and rethrowing lets the host render its own error response.
- **Anything not HTML — a 304, a redirect — passes through byte for byte**, and a failure after that
  point serves whatever has been buffered rather than breaking the web app.
- **Cache validators are removed** once the body no longer matches the static file, and range
  requests are not supported on the rewritten document. Removing the validators leaves the browser
  nothing to revalidate against, so `no-cache` is set too: otherwise the browser is free to keep
  serving a heuristically cached copy — including one fetched *before* the plugin was installed,
  which has no script tag in it and so disables the enhancement until a hard reload. Revalidating a
  document this small costs little; serving a stale one costs the whole feature.

### `Web/specialtomovie.js`

Progressive enhancement over links the server already renders. It turns them into icon buttons,
keeps them at the end of the row, and navigates in-app instead of opening a tab. If any of it fails,
the links still work as plain text links.

**The icon idiom is borrowed, not invented.** Two projects already put icons in this row and were
read rather than guessed at:

- **Druidblack/jellyfin-icon-metadata** — the CSS that turns IMDb/AniDB/TMDB links into logos. Every
  rule it ships has the same shape: neutralise the anchor (`background: none`, `color: transparent`,
  `padding: 0`, `font-size: 0`), draw the logo in a `::before` with an explicit box, and cancel the
  theme's hover chrome. Matching that shape is what makes this link a *peer* of those icons instead
  of something parked beside them.
- **n00bcodr/Jellyfin-Enhanced** — adds its own links to the same row. Its icon size comes from
  measuring a native link's content height rather than from a constant, because a hard-coded box fits
  exactly one theme; and it separates its link from the previous one with a space text node.

Three releases were spent styling this link to *look* like a row member — measuring a neighbour and
copying its geometry onto the anchor with `!important`. That produced a tile that sat low, jumped
sideways, and rendered a second icon inside itself. The anchor is already a `button-link
emby-button` like every other link in that row; giving it the row's own icon idiom and otherwise
leaving its layout alone is what makes it sit correctly on any theme.

Points that each cost a bug:

- **Double-load guard.** The injector adds the tag once per document, but a second copy — a stale
  service worker, a manual install into the web root alongside the injected tag — would register a
  second click handler and a second `MutationObserver` on the same page.
- **The selector matches only our own marker.** An earlier version also required the link to sit
  inside `#itemDetailPage:not(.hide) .itemExternalLinks`, which tied the enhancement to two
  web-client class names; if either changed, the script silently did nothing and every link fell back
  to unstyled text that opened a new tab. `parseLink` rejects anything that merely happens to contain
  the marker.
- **`parseLink` validates every field against the exact shape the plugin emits**, not merely for
  presence. The click handler cancels the browser's default on whatever `parseLink` accepts and it
  sees every click in the document, so a loose match would let it swallow another plugin's link.
- **The href is reduced to its hash unconditionally**, not only when the URL is same-origin. These
  links always point at an item on the server that served the page, so the hash is always the right
  way to reach it — and that stays true when a reverse proxy hands the server a hostname the browser
  cannot resolve, which is exactly the case a same-origin test gets wrong.
- **Href, target, title and `aria-label` are re-applied every pass.** The web client rebuilds the row
  from the server DTO, and the fresh anchor arrives with the absolute href and `target="_blank"`
  restored.
- **The class is re-checked every pass rather than memoised.** An earlier version recorded that it had
  styled an anchor and skipped the work afterwards, which read correctly until the web client
  re-rendered the row: it resets the anchor's `className` while keeping its attributes, so the memo
  still said "done" over a link that had gone back to plain black text.
- **Hidden page copies are stripped, not merely skipped.** The web client keeps several detail pages
  in the document and hides all but one, so the same server-rendered link exists two or three times
  over. Styling every copy put an icon on a page nobody was looking at and left the visible one wrong
  — reported as two icons on one item and none on the other. A page hidden while styled is shown
  again later, so the styling has to come back off.
- **`isHidden` asks the computed style, not the layout.** An element has no client rects both when it
  is hidden *and* when the page has simply not been laid out yet; treating the second as the first
  meant the upgrade skipped links it should have styled, silently, on any client that renders before
  it lays out. It also asks about computed `display`/`visibility` rather than the class the web
  client happens to hide pages with, so it survives that class being renamed.
- **`iconSize` reads the pseudo-element's height first.** On a row of logos the anchor is left
  `display: inline` with `font-size: 0`, and an inline box wrapping an inline-block child reports a
  zero-height rect — so reading the anchor returned the fallback on every logo row. That went
  unnoticed because the fallback is the same 25px jellyfin-icon-metadata happens to draw at; a row
  whose logos were any other size got a 25px icon beside them. Links other plugins added are skipped
  when measuring, because sizing our icon from another plugin's icon compounds whatever that one got
  wrong; the reference has to be a link the web client itself rendered.
- **`visualCentre` treats a zero-height rect as a baseline.** A link showing a logo has no height of
  its own: it is an inline box with `font-size: 0` whose whole appearance comes from a
  pseudo-element, so its rect collapses onto the baseline — and that baseline is exactly where a
  `vertical-align: middle` pseudo-element with no x-height to work from is centred. The empty rect's
  own top is therefore the icon's visual centre.
- **`matchRow` measures both corrections rather than assuming either.** Spacing lives wherever a
  given setup keeps it: jellyfin-icon-metadata puts a margin on each logo's pseudo-element, inside
  the anchor's own box; the web client separates anchors with a text node; a theme may use the
  container's `gap`. Any of them can be in play at once. Reading a neighbour's margin was tried and
  produced no gap at all, because the value found sat on the side facing *away* from a link at the
  end of the row. Vertical placement is the same problem: no single `vertical-align` serves both
  kinds of row — the default is exact against logos and four pixels low against text, and every value
  that fixes the text row throws the logo row out by more. Both were measured across row types before
  the function was written. Our own contributions are zeroed before measuring so the correction
  cannot read itself back and walk the icon across the row.
- **The shift is applied as a relative offset, not as `vertical-align`**, so correcting it cannot
  change the height of the line the row sits on.
- **The corrections are re-derived whenever the row has moved, not taken once.** This row does not
  arrive finished. The web client renders it as plain anchors and adds its own `emby-button` class
  when it upgrades the element — that class carries `display: inline-flex; vertical-align: middle`,
  so a link's box changes shape the moment it lands. A user's logo CSS can apply later still, and
  Jellyfin-Enhanced appends its own links after an API call. A correction measured inside that
  window describes a row that no longer exists, and the icon used to keep it forever:
  `data-stm-matched` was a flag, set once and read as "done". It now carries a **key** — the
  container's width, the icon's own box, and the identity and box of the link it was measured
  against — and any pass that computes a different key measures again. This was reported as an icon
  a few pixels low on one item, high on another, and different again after a refresh. It was never
  the item: it was which moment the single measurement landed in. It is also why a *movie* page
  always looked right — Jellyfin-Enhanced appends its Seerr link late, which moves our icon back to
  the end of the row and forces a fresh measurement on a settled row. Seerr links are only added for
  movies and series, so on a special nothing ever disturbed the first measurement.
- **A resize and a short settle series trigger a pass, on top of the DOM observer.** The observer
  sees DOM changes, and the row can change without one: a resize rewraps it, and a stylesheet
  arriving late restyles every link in it. Re-checking a few times after arriving on a page covers
  the second, which nothing else can see.
- **The icon aligns to the nearest link the row actually draws**, not to whatever element happens to
  sit before it. A neighbour hidden by a theme, or one an installed logo pack has no rule for, has
  no box and therefore no position; measuring against it produced a correction the size of the
  distance to the top of the viewport, which the sanity limit then threw away — leaving the icon
  unaligned in a row that needed a correction.
- **A correction is only taken from a link on the same line**, tested by whether the icon follows
  that link along the line *or* their boxes overlap vertically. Neither test alone survives every
  row: a top-aligned box in a tall line never overlaps the icon's baseline box, and the overlap test
  is the only one left when a theme uses negative margins. A wrapped icon on a narrow phone was
  being dragged up a line by a correction measured against the line above.
- **The spacing correction only ever adds.** The separators between these links are the web client's
  own text, not ours to edit, so where a hidden link leaves two separators behind, the icon can be
  pushed out but not pulled in.
- **What it costs.** A pass on a visible detail page reads about five more boxes than before to
  compute the key, and writes nothing when the key matches: ten passes over a settled row produce
  zero style writes, so a stable row is measured exactly once.
- **No hover or focus styling of our own**, and the theme's button chrome is cancelled the way the
  logo rules cancel it. A row of logos that do not react to the pointer, with one tile that lifts and
  brightens, looks broken — and on themes with no hover treatment at all it looks worse.
- **The move budget stops a fight.** Another plugin that also forces its links last would trade moves
  with us forever. Reaching the end resets the counter, so ordinary re-appends over a long session do
  not exhaust it; a genuine fight never reaches that line, so it still stops.
- **Moving takes the old separator and puts a new one back.** The gap between links in this row is
  the whitespace *between* the anchors — every logo's margin sits inside its own box — so appending
  the link on its own butted it against the previous logo with no space at all. Jellyfin-Enhanced
  appends the same space before its own link.
- **Clicks are handled in the capture phase**, not left to the rewritten href. The row is rebuilt on
  every render, so there is always a window in which a freshly rendered anchor still carries the
  absolute URL and `target="_blank"`; a click landing in that window opened a new tab, and only the
  second click stayed in the app. Capture means the click is ours before the anchor's default runs,
  whatever state the anchor is in, and `parseLink` reads the item out of the absolute URL just as
  well as out of the rewritten hash. A deliberate modified click is still the user's to make.
- **The observer watches `class` attributes and `childList`**, not `style`. The script's own inline
  writes would retrigger a `style` filter.

### The icon itself

One inline SVG, so it renders on a server with no internet access. It is a complete self-coloured
mark rather than a tinted glyph: the row it joins is a row of brand logos, and an icon that
recoloured itself with the theme would not belong there. The gradient stops are deliberately far
apart — at 25px on a phone a timid gradient is indistinguishable from flat colour — and the range is
spent on the dark end, because a lighter light stop starts washing out the white chain. The mark is a
chain link drawn along the bottom-left to top-right diagonal. Both directions of the pairing share
it, exactly as each service in that row has one logo; which way it goes is obvious from the page you
are on, and the tooltip and accessible name say so outright.

---

## Detection and pairing — `Services/SpecialDetectionService.cs`

Force links are checked before any lookup. An entry whose right-hand side is a Jellyfin item ID links
directly to that movie rather than going out to a metadata provider.

**Config collections are snapshotted once at scan start.** They are mutable and a config save during
a scan would otherwise race the enumeration.

**Two indexes exist purely to stop the scan cost multiplying:**

- Episodes are indexed once by both forms a force link may name them by. Matching by scanning the
  list per force link re-formatted every episode's key for every entry, so the work grew as force
  links × Season 0 episodes. First match wins, mirroring the `FirstOrDefault` it replaced.
- Movies are indexed by destination library. Narrowing every movie on the server down to one
  destination library is a path-prefix test per movie per library location; that answer is identical
  for every episode mapped to that library, so computing it inside the per-episode lookup made a full
  scan grow as episodes × movies. Each destination is filtered once and reused. The index is built
  per scan and used only by that scan, so it needs no synchronisation.

**Virtual folders are cached and all movies batch-fetched once** for the same reason.

**A small delay sits between metadata lookups** to respect provider rate limits.

**Dry run stores the pair as `DryRun` and makes no filesystem change.** Disabling dry run promotes
those pairs on the next scan: the `DryRun` pair is removed so the normal path can recreate it as
`Pending`.

**Watch state is synced for all Active pairs on every full scan**, which catches pairs that existed
before watch sync was added and pairs whose state drifted while the plugin was stopped.

**The ignore list is one list carrying four forms of entry** — an episode key (`Series Name S00E01`),
an episode's Jellyfin item ID, a series name, or a series' item ID. That is unambiguous because an
episode key always carries the `S00E` suffix and IDs are GUIDs.

**Movie identifiers are parsed from a force link's right-hand side** by prefix: `tt` followed by
digits is an IMDB ID, `tmdb:` and `tvdb:` name their providers, and anything else is read as
`Movie Title (Year)`.

---

## Hard links and subtitles — `HardLink/HardLinkService.cs`

**Same-filesystem validation is platform-specific.** On Linux and macOS it compares `st_dev` from
`stat()`. The struct layout varies by platform, so only `st_dev` — the first field — is read, and the
buffer is padded generously so the kernel cannot write past it.

**The resolved link path is re-checked against the destination library root**, with a trailing
separator appended to the root first: a bare prefix test also accepts a sibling directory whose name
merely starts with the root's (`/media/movies-old` against a root of `/media/movies`). Nothing
reaching there is attacker-controlled today — the components are sanitized titles and the root comes
from Jellyfin's library config — but this is the backstop that has to hold if that stops being true.

**Folder name sanitisation** strips invalid filename and control characters, collapses `..` sequences
in a loop until the result is stable, and rejects Windows reserved device names.

**Subtitle sync is bidirectional.** Tracked subtitles are checked for user-side removals: when one
side is gone, the surviving file's content hash is compared against the hash captured when the link
was created, confirming it is still the plugin-linked file before Jellyfin is asked to delete it.
New subtitles are linked only when one side has them and the other does not.

`SubtitleSyncResult.Records` are the tracked links that still exist plus any newly created;
`Deletions` are the confirmed one-sided removals, which the caller deletes through Jellyfin's
subtitle API rather than with raw file I/O.

---

## Persistence — `Data/PairStore.cs`

**Lookup indexes are rebuilt wholesale after every mutation** rather than maintained incrementally.
Callers mutate the `LinkedPair` they were handed and then call `Upsert`, so by then the pair's
previous `EpisodeItemId`/`MovieItemId` are already gone and cannot be evicted by key. Every mutation
already pays an O(n) serialise plus disk I/O in `Save()`, so an O(n) rebuild costs nothing measurable
and removes a whole class of stale-key bugs. First entry wins on a duplicate key, matching the
`List.Find` semantics the indexes replaced.

**A batch upsert keeps its position map current**, so a pair appearing twice in one call updates in
place instead of being appended twice.

**Load never throws.** The file can be malformed, locked, truncated or unreadable, and load runs from
the constructor — letting any of those escape fails the DI registration and takes the whole plugin
down instead of degrading to the backup. A stale temp file from a previous crash is cleaned up on
start, and the primary file is restored from backup when it cannot be read.

**Save never throws either.** Every mutation calls it, and the mutation has already been applied to
the in-memory list by the time it runs. Letting an I/O error escape would be the worst of both
worlds: the caller sees a failure for a change that *did* take effect in memory, and the exception
unwinds into whatever invoked the mutation — including Jellyfin's own `ItemRemoved` dispatch, where
it would disrupt unrelated subscribers. Swallowing it means the store can be newer in memory than on
disk until the next successful save, which is the lesser evil for a transient failure (a locked file,
a full disk, a momentarily unavailable network share) because the next mutation rewrites the whole
file and repairs the divergence on its own.

**Writes are atomic**: copy the current file to a backup, write the new content to a temp file, then
rename over the primary, so a crash mid-write cannot leave a partial file.

---

## Deletion and the `ItemRemoved` cascade

This is the single most repeated invariant in the codebase, and it is why several call sites look
back-to-front.

**Drop the pair from the store *before* deleting the media.** Jellyfin raises `ItemRemoved` for
whatever is deleted. If the pair is still in the store when that event arrives,
`LibraryEventHandler` reads it as a *user-initiated* removal and cascades into the other side —
deleting the original episode whenever two-way deletion is enabled. Every deletion path obeys this:
`SpecialToMovieController.RemovePair` and its bulk sibling, `SpecialDetectionService`'s cleanup,
and `CleanupTask`.

`Api/SpecialToMovieController.cs` additionally persists the *cleared* `MovieItemIds` before deleting,
so the handler cannot match those pairs by movie ID either.

**`Services/LinkedItemDeleter.cs`** is the one definition of "delete an item and its files".
Deletion goes through `ILibraryManager.DeleteItem` rather than raw file I/O so Jellyfin removes the
database row, the images and the user data along with the file, and so its own event pipeline runs.
This lived as four near-identical private copies across the controller, the detection service and the
library event handler; one definition keeps the null and missing-item guards from drifting apart. It
never throws — its callers are event handlers, scheduled tasks and API endpoints for which a failed
delete must not abort the surrounding work.

**Two guards on the delete endpoint that the request cannot talk its way past.** A pre-existing movie
belongs to the user's library, not to the plugin, and is never deleted. And deletion is gated on the
*saved* configuration rather than on the caller's word for it: the config page tracks the checkbox
live, so an unsaved tick would otherwise delete files the stored configuration says to keep.

---

## Metadata lookup — `Lookup/`

**Every value that originates in library metadata is URL-escaped before becoming a path segment.**
`seriesTmdbId`, `imdbId` and `episodeTvdbId` come from an NFO or a metadata provider, not from this
plugin, so none is guaranteed to be a bare number. They land in the path of a URL that also carries
the API key: an unescaped `/`, `?` or `#` would re-point the request at a different endpoint on the
API host.

Tag-based TVDB matches lack provider IDs, so they are enriched via a TMDB search.
`AggregatedLookupService` is what orders that fallback chain.

TVDB responses are read for an IMDB ID in `remoteIds` where present, prefer the English title over
the original-language one, and fall back to English when the preferred language is unavailable —
which needs the configured ISO 639-1 code converted to ISO 639-2/B. `ApiResponseCache` double-checks
for an existing entry after acquiring its lock.

---

## Watch sync — `Services/WatchSyncService.cs`

When a pair first becomes Active, watch state is synced once for all users, copying the "most
watched" state: if either item is played, both become played. Otherwise the item with more progress
wins — with one asymmetry, that **the movie wins only when the episode has no progress at all**; the
special has priority in every other case.

A reentrancy guard stops the save event from re-triggering the sync that caused it. Cancellation
during shutdown is expected and is not logged as a failure — the same is true of the equivalent path
in `LibraryEventHandler`.

---

## Configuration — `Configuration/PluginConfiguration.cs`

**Dry run is enabled by default** so a new user reviews matches before anything touches the
filesystem.

**`EnableCrossLinkButtons` is read on every call**, so toggling it takes effect without a server
restart. **`EnableClientScript` controls only the injection** of the client script: with it off the
cross-links still work, but render as plain text and open a new tab.

`LibraryMapping` maps one source TV library to one destination movie library for hard link placement.

## The config page — `Configuration/configPage.html`

The page carries no comments of its own: the comment lint bans `/* */` blocks, and the rationale
belongs here anyway. Two traps in it look like clutter and are not.

**Every `<select is="emby-select">` must sit in a positioned parent.** Jellyfin's `emby-select`
upgrade draws the chevron in a `.selectArrowContainer` of its own, `position: absolute`, and an
absolutely positioned box resolves against its nearest positioned ancestor. A select in a plain flex
row has none, so its arrow escapes to the page's top-right corner, where it reads as a stray control
that does nothing -- three of them stacked there in v2.0.0. Jellyfin's own `.selectContainer` is
`position: relative`, which is why the selects the page inherits from Jellyfin never showed this.
The three Linked Pairs filters each sit in a `.pairs-select-wrapper` for that reason. Do not
unwrap them; [`tools/configpage-selects.js`](tools/configpage-selects.js) fails if one is unwrapped,
and [`tools/configpage-arrow-fix.js`](tools/configpage-arrow-fix.js) repairs a running server's page
from the console.

**A column legend's hidden spacer carries the same class as the control it stands in for.** Each
list section (library mappings, force links, ignore list) heads its rows with a flex legend whose
last child is a `visibility: hidden` Remove button, there only to reserve the column the real button
occupies. Unclassed, it takes the host theme's default button metrics instead of the row's
(`.mapping-remove` / `.forcelink-remove` / `.ignore-remove`: `0.85em`, `2px 6px`, a 1px border), and
the legend drifts out of line with the rows by however much the two disagree -- invisibly on the
bare page, by more under a server's stylesheet. The spacer matching the row's class makes the widths
equal by construction rather than by coincidence.

**The format hints are real examples, not templates** (`Firefly S00E01` -> `Serenity (2005)`). A
placeholder like `SeriesName S00E##` invites typing the `##` literally, which fails as silently as
every other malformed entry, since nothing in `IsIgnored` or `ParseForcedMovie` reports an entry it
did not match. An example naming a real series has to be checked against the provider before it
ships: the first pair used here was `Breaking Bad S00E01` -> `El Camino (2019)`, and TheTVDB lists
no El Camino among Breaking Bad's specials at all. Provider IDs stay templated (`tt[ID]`,
`tmdb:[Movie ID]`) because a real ID there would be copied verbatim into a mapping it does not fit.
