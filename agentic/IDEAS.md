# Ideas

## High Priority

- **Cross-link buttons between paired special and movie** — DONE (unreleased)
  - A button in the external-links row on an item's detail page (next to IMDb / TMDB / TVDB) that jumps to the linked counterpart: a Season 0 special gets a **Movie Version** button, the paired movie gets a **TV Special** button.
  - Shipped all three phases plus the `PairStore` lookup indexes. Full research, design, decisions, and the audit trail: [`plans/cross-link-buttons(DONE).md`](plans/cross-link-buttons%28DONE%29.md).

- **Per-library primary metadata provider** — Effort: Medium (half day)
  - Allow each library mapping to override the global primary metadata provider. Best use case: anime libraries should use anime-focused metadata sources (e.g. TVDB, which has better anime coverage), while live-action TV libraries use TMDB. Currently only a single global primary provider is supported.
  - Implementation: add an optional `PrimaryProvider` field to `LibraryMapping` (null = inherit global default). Pass it through to `AggregatedLookupService` or resolve it in `SpecialDetectionService` before calling lookup. The `MetadataProviderType` enum and conflict resolution logic already exist — this just needs per-mapping plumbing.
  - Touches: `LibraryMapping` model (new nullable field), `SpecialDetectionService` (pass provider override), `AggregatedLookupService` (accept optional override parameter), configPage.html (per-mapping dropdown in library mappings section).

- **Manual pair creation via search UI** — Effort: Large (1-2 days)
  - Replace or supplement the current force link text inputs with a searchable UI. Let users browse/search for a Season 0 episode and a movie, then pair them directly with a button click. Much more accessible than needing to know the exact `SeriesName S00E##` format. Could use Jellyfin's existing item search API to power the dropdowns.
  - Touches: new API endpoints for item search, significant configPage.html UI work, new pairing logic that bypasses the normal detection flow.

- **Sync only watched/unwatched status (not playback position)** — DONE (v1.0.12)~~
  - ~~Added "Only sync watched/unwatched status" checkbox. When enabled, only Played/PlayCount/IsFavorite sync — PlaybackPositionTicks and LastPlayedDate are skipped, preventing Continue Watching duplicates.~~

- **Add the ability to add an entire series to the ignore list** — DONE (unreleased)
  - The ignore list now accepts a series name or a series Jellyfin item ID alongside the existing episode key and episode item ID forms, and ignores every special in that series. Both the detection path and ignore-list enforcement share one matcher (`SpecialDetectionService.IsIgnored`).

- **Change remove selected confirmation dialogue to ask to remove media** — DONE (unreleased)
  - With "Remove plugin managed items automatically" enabled, the remove-selected confirmation now says the plugin managed movie items and their files will be deleted along with the pairs, and confirming does both. With it disabled the pairs are removed on their own, as before. Cancelling is always a no-op. `RemovePair` takes a `DeleteMedia` flag and refuses it for pre-existing movies.

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
