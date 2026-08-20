---
name: release-zip-naming
description: Release zips and tags use the short version (v1.0.10), not the 4-part version
metadata:
  type: feedback
---

Release asset filenames and GitHub release tags drop the trailing `.0`:

- zip: `specialtomovie-v1.0.10.zip`, not `specialtomovie-v1.0.10.0.zip`
- tag: `v1.0.10`, not `v1.0.10.0`
- the manifest `sourceUrl` must match the uploaded asset name exactly

**Why:** Jellyfin fetches the asset at `sourceUrl`. A mismatch returns 404 and the install fails —
this shipped broken once, in v1.0.10.

**How to apply:** The 4-part version is used only in the `.csproj` `AssemblyVersion`/`FileVersion` and
the manifest `version` field. Everywhere else use the short form. Related:
[[no-release-without-permission]], [[version-bumping]].
