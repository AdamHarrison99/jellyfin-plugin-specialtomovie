# Ideas

## High Priority

- **Cross-link buttons between paired special and movie** — Effort: Medium (Phase 1 ~2h, Phase 2 ~half day)
  - A button in the external-links row on an item's detail page (next to IMDb / TMDB / TVDB) that jumps to the linked counterpart: a Season 0 special gets a **Movie Version** button, the paired movie gets a **TV Special** button. Also wants to look good alongside [Jellyfin Enhanced](https://github.com/n00bcodr/Jellyfin-Enhanced) buttons — though JE is **not** required for any part of this (see [Does this require Jellyfin Enhanced?](#does-this-require-jellyfin-enhanced)).
  - Full research + design below in [Cross-Link Buttons — Design](#cross-link-buttons--design).

- **Per-library primary metadata provider** — Effort: Medium (half day)
  - Allow each library mapping to override the global primary metadata provider. Best use case: anime libraries should use anime-focused metadata sources (e.g. TVDB, which has better anime coverage), while live-action TV libraries use TMDB. Currently only a single global primary provider is supported.
  - Implementation: add an optional `PrimaryProvider` field to `LibraryMapping` (null = inherit global default). Pass it through to `AggregatedLookupService` or resolve it in `SpecialDetectionService` before calling lookup. The `MetadataProviderType` enum and conflict resolution logic already exist — this just needs per-mapping plumbing.
  - Touches: `LibraryMapping` model (new nullable field), `SpecialDetectionService` (pass provider override), `AggregatedLookupService` (accept optional override parameter), configPage.html (per-mapping dropdown in library mappings section).

- **Manual pair creation via search UI** — Effort: Large (1-2 days)
  - Replace or supplement the current force link text inputs with a searchable UI. Let users browse/search for a Season 0 episode and a movie, then pair them directly with a button click. Much more accessible than needing to know the exact `SeriesName S00E##` format. Could use Jellyfin's existing item search API to power the dropdowns.
  - Touches: new API endpoints for item search, significant configPage.html UI work, new pairing logic that bypasses the normal detection flow.

- **Sync only watched/unwatched status (not playback position)** — DONE (v1.0.12)~~
  - ~~Added "Only sync watched/unwatched status" checkbox. When enabled, only Played/PlayCount/IsFavorite sync — PlaybackPositionTicks and LastPlayedDate are skipped, preventing Continue Watching duplicates.~~

- **Add the ability to add an entire series to the ignore list**
  - Allow a series jellyfin item id or series name to be put into the ignore list feild to ignore that entire tv series.

- **Change remove selected confirmation dialogue to ask to remove media**
  - If the user has "Remove plugin managed items automatically" enabled, then clicking the button to remove a pair from the database should prompt a dialogue asking the user if they would like the plugin managed hard link item to be removed as well, with a yes or no confirmation.

## Medium Priority

- **Per-pair "Ignore" button in the pairs table** — Effort: Small (1-2 hours)
  - Add an "Ignore" button to each row in the pairs table that automatically adds the episode to the ignore list. Should work for both DryRun and already-linked pairs. Saves users from manually typing the episode key into the ignore list section.
  - Touches: new API endpoint to add to ignore list + remove pair, button in configPage.html table rows.

## Low Priority

- **Activity log in the config page** — Effort: Medium (half day)
  - Show recent plugin activity (new pairs created, errors, retries, deletions) directly on the config page so users don't have to dig through Jellyfin's server log. Could be a collapsible section or a separate tab.
  - Touches: new in-memory or persisted log store, new API endpoint, new UI section in configPage.html. Need to decide on retention (last N entries vs. time-based).

- **Unmatched episodes report** — Effort: Small (2-3 hours)
  - Show Season 0 episodes that the plugin scanned but couldn't find a movie match for, so users know what to force-link manually. Could be a tab or filter in the pairs table, or a separate section on the config page.
  - Touches: track "no match" results during scan (currently just logged and discarded), new API endpoint or extend existing Pairs endpoint, UI filter/section.

- **Webhook/notification support** — Effort: Small-Medium (3-4 hours)
  - Ping a URL when new pairs are created, for users who run monitoring setups (e.g. Discord, Slack, Ntfy).
  - Touches: new config fields for webhook URL + events to notify on, HTTP POST calls from detection service and event handlers.

- **Convert pre-existing links to plugin-managed links** — Effort: Medium (half day)
  - Add a button (per-pair or bulk) that converts a pre-existing movie pair into a plugin-managed pair. This would rename the movie's folder to include the `[JellyfinPlugin-SpecialToMovie]` tag, set `IsExistingMovie = false` on the pair (enabling one-way and two-way deletion), and update `HardLinkPath` to reflect the renamed folder.
  - Useful when the user originally had the movie in their library independently but now wants the plugin to manage its lifecycle alongside the linked episode.
  - Touches: folder rename logic (need to handle Jellyfin's internal path tracking), pair update, new API endpoint, per-row button in configPage.html. Risk: Jellyfin may not pick up the rename gracefully — may require a library scan.

---

## Cross-Link Buttons — Design

Research done 2026-08-14 against `Jellyfin.Controller`/`Jellyfin.Model` **12.0.0-rc4** (the versions this plugin currently targets), `jellyfin-web` master, and `n00bcodr/Jellyfin-Enhanced` main.

### Research findings (all verified, not assumed)

1. **The right extension point is `IExternalUrlProvider`.** `MediaBrowser.Controller.Providers.IExternalUrlProvider` exists in 12.0.0-rc4 (confirmed in the package's XML docs) with exactly two members:
   - `string Name { get; }` — "the external service name"
   - `IEnumerable<string> GetExternalUrls(BaseItem item)`

   This is what the TVDB plugin uses (`Jellyfin.Plugin.Tvdb/Providers/TvdbExternalUrlProvider.cs`) to put its links on detail pages. The older `IExternalId` + `UrlFormatString` route is the wrong tool — it is keyed to a provider ID and also shows up in the metadata editor.

2. **Discovery is automatic — no DI registration required.** `ApplicationHost.FindParts()` calls:

   ```csharp
   Resolve<IProviderManager>().AddParts(
       GetExports<IImageProvider>(), GetExports<IMetadataService>(),
       GetExports<IMetadataProvider>(), GetExports<IMetadataSaver>(),
       GetExports<IExternalId>(), GetExports<IExternalUrlProvider>());
   ```

   `GetExports<T>()` scans every concrete type across the composable assemblies (which includes plugin assemblies loaded by the plugin manager) and instantiates each via `CreateInstanceSafe`, which resolves constructor arguments from the root service provider. So a public class implementing the interface is picked up automatically, and it can constructor-inject `IPairStore` / `ILibraryManager` / `IServerApplicationHost` because those are all in the root container (`IPairStore` is registered in [PluginServiceRegistrator.cs](../PluginServiceRegistrator.cs#L20)). **Do not also register it in `PluginServiceRegistrator`** — that would risk a duplicate instance.

3. **One label per provider class.** `ProviderManager` stores `_externalUrlProviders = externalUrlProviders.OrderBy(i => i.Name).ToArray()` and flattens them as:

   ```csharp
   _externalUrlProviders.SelectMany(p => p.GetExternalUrls(item)
       .Select(url => new ExternalUrl { Name = p.Name, Url = url }));
   ```

   The button caption is the **provider's** `Name`, not a per-URL value. Two different captions ("Movie Version" / "TV Special") therefore require **two provider classes**. `Name` is a property read at call time, so it can be sourced from plugin config (with a non-empty fallback).

4. **DTO gating.** `DtoService` only fills the field when asked: `if (options.ContainsField(ItemFields.ExternalUrls)) { dto.ExternalUrls = _providerManager.GetExternalUrls(item).ToArray(); }`. The item detail page requests the full field set, so this is satisfied there; list/grid queries generally don't request it, which keeps the cost off the hot paths.

5. **How the web client renders it** — `jellyfin-web/src/apps/legacy/controllers/itemDetails/index.js`, `renderLinks()`:

   ```js
   if (item.ExternalUrls) {
       for (const url of item.ExternalUrls) {
           links.push(`<a is="emby-linkbutton" class="button-link" href="${url.Url}" target="_blank">${escapeHtml(url.Name)}</a>`);
       }
   }
   externalLinksElem.innerHTML = html.join(', ');
   ```

   Notes that matter:
   - The container is `.itemExternalLinks` inside `#itemDetailPage`. This is still the live code path on master (i.e. Jellyfin 12); there is no React replacement for the item detail page yet.
   - `ExternalUrls` are **not** gated by `layoutManager.tv` — only `HomePageUrl` is. So the button appears in the TV layout too.
   - `url.Name` is HTML-escaped; **`url.Url` is not** — it is interpolated raw into the `href` attribute. Our URLs are built from GUIDs and digits only, so there is no injection risk, but this is worth recording in `AUDIT.md` as a deliberate constraint: never put user-controlled text into the emitted URL.
   - `target="_blank"` is hardcoded ⇒ **a plain server-side link opens a new browser tab.** Fixing that requires client-side code (Phase 2).

6. **The in-app route** is `#/details?id={itemId}&serverId={serverId}` (`appRouter.getRouteUrl`, same shape for Movie and Episode). `serverId` is `IApplicationHost.SystemId`, reachable through the injected `IServerApplicationHost`.

7. **Emit a hash-only relative URL.** `href="#/details?id=…&serverId=…"` resolves against the current document, which on a detail page is already `…/web/#/details?id=…`. That means it works unchanged behind a reverse proxy, a custom base URL, or remote access, with no need to know the external hostname. The tradeoff is non-web clients (Android / iOS / TV) that hand `ExternalUrls` to a browser intent — a relative URL is meaningless to them and the button will be dead. Mitigation: a config option for URL style (see below).

8. **Jellyfin Enhanced does not restyle third-party links.** It injects *its own* buttons into the same `.itemExternalLinks` container. Verbatim pattern from `js/others/letterboxd-links.js`:
   - anchor built with `is="emby-linkbutton"`, `target="_blank"`, `rel="noopener noreferrer"`
   - `class="button-link emby-button letterboxd-link"` for text mode, `+ " letterboxd-link-icon"` for icon mode (config-driven)
   - icon supplied by an injected `<style id="letterboxd-links-styles">` defining `.letterboxd-link-icon::before` with `content:""`, `25px` square, `background-image: url(...)`, `background-size: contain`, `vertical-align: middle; margin-right: 5px`
   - a `MutationObserver` on `document.body` (`childList`, `subtree`, `attributeFilter:['class']`) throttled through `requestIdleCallback`, plus an `isAdding` lock, a `processedItemIds` set, and cleanup of stale links on `#itemDetailPage.hide`
   - item identified from the hash: `new URLSearchParams(window.location.hash.split('?')[1]).get('id')`
   - `arr-tag-links.js` additionally hangs `data-id` / `data-tag` attributes on the anchor, and JE's `docs/advanced/css-customization.md` documents `.itemExternalLinks a.arr-tag-link[data-id="…"]` recipes for users.

   **So "make it work with Jellyfin Enhanced" = adopt JE's conventions ourselves**: same class shape (`button-link emby-button specialtomovie-link [specialtomovie-link-icon]`), same `::before` icon technique, and `data-*` attributes so JE users' existing CSS-customization habits apply to our button too. Nothing needs to be contributed to JE.

9. **Script injection has no official API.** `MediaBrowser.Controller.Plugins` in 12.0.0-rc4 exposes only `IHasEmbeddedImage` and `IPluginServiceRegistrator`; `IHasWebPages` is config-page-only. JE gets its script in via `Services/ScriptInjectionStartupFilter.cs` — an `IStartupFilter` whose middleware runs outermost, strips `Accept-Encoding`/`Range`/`If-Range` so the downstream response is uncompressed, buffers the body for `GET /web/index.html` (also matching `/web/` and `/web`, which handles base-URL prefixes), checks for `200` + `text/html`, then inserts its `<script src="/JellyfinEnhanced/script">` before `</body>`, updates `Content-Length`, drops `ETag`/`Last-Modified`, and on any failure passes the original HTML through untouched. That is the pattern to copy.

### Plan

#### Phase 1 — server-side links (small, self-contained, ships value on its own)

New folder `Providers/`:

- `Providers/LinkedMovieUrlProvider.cs`
  - `Name` → `Plugin.Instance?.Configuration.MovieLinkLabel` falling back to `"Movie Version"`.
  - `GetExternalUrls(item)`: bail unless `item is Episode`; `_pairStore.GetByEpisodeId(item.Id)`; require `pair.Status == PairStatus.Active` and `MovieItemId` non-null/non-empty; yield the details URL for `MovieItemId`.
- `Providers/LinkedSpecialUrlProvider.cs`
  - `Name` → `SpecialLinkLabel` falling back to `"TV Special"`.
  - Mirror: bail unless `item is Movie`; `_pairStore.GetByMovieId(item.Id)`; yield the details URL for `EpisodeItemId`.
- Shared helper for URL construction (`#/details?id={id:N}&serverId={SystemId}&stm=1`). The `stm=1` marker is what the Phase 2 script uses to recognise our anchors without an extra API round-trip.
- Both classes: type check **first** (cheapest possible rejection), store lookup second, and a `null`-safe `Plugin.Instance` guard so a partially-initialised plugin can't throw inside the DTO pipeline. A throw here would break the whole item detail response, so wrap the body defensively.
- Optionally verify the target still exists with `ILibraryManager.GetItemById` before yielding, so a stale `MovieItemId` (movie deleted outside the plugin) doesn't render a dead button. Cheap — the library manager caches — but it is one extra call per DTO; decide with a measurement, default to including it.

Config additions in [PluginConfiguration.cs](../Configuration/PluginConfiguration.cs):

| Field | Default | Purpose |
| --- | --- | --- |
| `ShowCrossLinks` | `true` | Master on/off for the whole feature |
| `MovieLinkLabel` | `"Movie Version"` | Caption on the episode's page |
| `SpecialLinkLabel` | `"TV Special"` | Caption on the movie's page |
| `CrossLinkUrlStyle` | `Relative` | `Relative` (in-app, proxy-safe, web-only) vs `Absolute` (built from the published server URL, so non-web clients can follow it) |

`PairStore` performance: `GetByEpisodeId` / `GetByMovieId` are `List.Find` linear scans under a lock ([PairStore.cs:90-104](../Data/PairStore.cs#L90-L104)). Called once per detail-page DTO with a few hundred pairs this is nothing, but since this newly puts the store on a user-facing request path it is worth adding `Dictionary<Guid, LinkedPair>` indexes for episode ID and movie ID, rebuilt on `Load`/`Clear` and maintained in `Upsert`/`UpsertMany`/`Remove`/`RemoveMany`. Flag it in the pre-release audit either way.

#### Phase 2 — client-side polish (in-app navigation + the pretty button)

Pure progressive enhancement: if any of this fails or is disabled, Phase 1 still works, just as a plain text link that opens a new tab.

- `Web/specialtomovie.js` (embedded resource):
  - Inject `<style id="specialtomovie-links-styles">` defining `.specialtomovie-link-icon::before` exactly as JE does, with the icon as an **inline `data:` URI SVG** — no CDN dependency, unlike JE's `JE.cdn.selfhst(...)`.
  - `MutationObserver` on `document.body` (`childList`, `subtree`, `attributeFilter:['class']`), throttled via `requestIdleCallback` with an in-flight lock, mirroring JE's structure so the two coexist predictably.
  - On each pass: find `#itemDetailPage:not(.hide) .itemExternalLinks a[href*="stm=1"]` that aren't yet upgraded. For each — add `class="button-link emby-button specialtomovie-link specialtomovie-link-icon"`, set `data-specialtomovie="movie|special"` and `data-linked-id`, remove `target="_blank"`, and attach a click handler doing `e.preventDefault(); window.location.hash = <href>` so the SPA router navigates in place instead of opening a tab.
  - Idempotency via a `data-stm-upgraded` attribute; clean up on hidden `#itemDetailPage` elements the way JE does.
  - Optional per-user hardening: `ApiClient.getItem(...)` the target and hide the button on 404, which closes the visibility gap in "Risks" below. Costs one request per detail page — make it the non-default.
- `Api/ClientScriptController.cs`: `[AllowAnonymous] GET /SpecialToMovie/ClientScript` returning the embedded resource as `application/javascript` with a version-stamped `ETag`. Static content only, no request input, no reflection of user data.
- `Services/ScriptInjectionStartupFilter.cs`: the JE pattern from finding 9. Register with `serviceCollection.AddSingleton<IStartupFilter, ScriptInjectionStartupFilter>()`. Idempotency marker: the `/SpecialToMovie/ClientScript` substring. Gate the whole filter on `ShowCrossLinks` **and** a separate `InjectClientScript` config flag so a user who distrusts index.html rewriting can keep Phase 1 only.

#### Phase 3 — surface and docs

- configPage.html: a "Detail page links" section with the master toggle, the two label inputs, the URL-style dropdown, and the script-injection toggle (with an explicit note that it rewrites the served `index.html` in memory).
- README: new feature section, new config options.
- `AUDIT.md`: record the new attack surface before cutting a release (see below).

### Does this require Jellyfin Enhanced?

**No.** Jellyfin Enhanced is not a dependency at any layer — it is a *pattern source*, not a runtime requirement. Recording this explicitly because the phrase "look good alongside Jellyfin Enhanced buttons" in the summary above reads like a dependency and is not one.

- **Phase 1** is a pure server-side `IExternalUrlProvider`. It renders as a plain text link in `.itemExternalLinks`, styled exactly like the stock IMDb / TMDB / TVDB links. No JS, no JE, works on a vanilla server.
- **Phase 2** is where "the pretty button" comes from, and it is entirely self-hosted:
  - we inject **our own** `<style id="specialtomovie-links-styles">` with the `.specialtomovie-link-icon::before` rule,
  - the icon is an inline `data:` URI SVG — deliberately chosen over JE's CDN approach,
  - the script is served by **our own** `ClientScriptController` and injected by **our own** `ScriptInjectionStartupFilter`.

The JE connection is research finding 8: JE does not restyle third-party links, it injects its own buttons into the same container. So "make it work with Jellyfin Enhanced" means *adopting JE's conventions* — the same `button-link emby-button` class shape, the same `::before` icon technique, the same `data-*` attribute habit — so that when both are installed the buttons read as siblings rather than as two different design languages, and JE users' existing CSS-customization snippets transfer to our button too. Nothing is contributed to JE and nothing is consumed from it.

| Installed | Result |
| --- | --- |
| Phase 1 only (no client script) | Plain text link, opens in a new tab |
| Phase 2, no JE | Full icon button, in-app SPA navigation |
| Phase 2 + JE | Same icon button, visually consistent with JE's Letterboxd / arr buttons |

The one genuinely JE-dependent item is the startup-filter nesting risk in [Risks and open questions](#risks-and-open-questions) below — two `IStartupFilter`s buffering the same `index.html` response. That is a *compatibility* question when JE happens to be present, not a prerequisite for our feature.

### Risks and open questions

- **Non-web clients.** A relative URL is unusable to Android/iOS/TV clients that open `ExternalUrls` externally; expect a dead button there. `CrossLinkUrlStyle = Absolute` is the escape hatch, but it needs a correct published server URL and will break for users whose external hostname differs from it. Verify what the Android client actually does with an unparseable URL before promising anything.
- **Two `IStartupFilter`s buffering the same response** (ours + Jellyfin Enhanced). Each is idempotent by its own marker so both scripts should land, but the nesting is untested — **must be verified on a server with JE installed**, in both install orders.
- **New unauthenticated endpoint.** `/SpecialToMovie/ClientScript` is the plugin's first `[AllowAnonymous]` route; every other route is `RequiresElevation` ([SpecialToMovieController.cs:20](../Api/SpecialToMovieController.cs#L20)). Keep it to a fixed embedded asset.
- **Visibility leak.** `IExternalUrlProvider` has no user context, so the button renders even for a user whose library permissions exclude the target item; clicking it lands on an error. It exposes an item ID and a caption, nothing more. Either accept and document, or use the Phase 2 per-user check.
- **Existing-movie pairs.** `IsExistingMovie` pairs should link too — the button is about navigation, not lifecycle ownership. `DryRun`/`Pending`/`Error` pairs should not.

### Testing checklist

- Episode page shows the button; movie page shows the reverse button; both navigate to the right item.
- Nothing renders for unpaired items, `DryRun` pairs, or pairs whose counterpart was deleted.
- Works behind a reverse proxy and with a configured base URL.
- Renders correctly in the TV layout.
- Coexists with Jellyfin Enhanced (buttons from both appear, styling is consistent, no duplicate injection, no observer thrash).
- Detail-page DTO latency unchanged in a library with a large `pairs.json`.
- Disabling `ShowCrossLinks` removes the buttons without a restart (config is read per call).
