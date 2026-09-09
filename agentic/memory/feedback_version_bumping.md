---
name: version-bumping
description: The first digit tracks the Jellyfin major version; minor bumps the third position, major the second
metadata:
  type: feedback
---

This project's version convention, against the 4-part number `2.0.0.0`:

- **first digit** — the **Jellyfin major version** this release builds against. `1.x` was the
  Jellyfin 10.x line; `2.x` is the Jellyfin 12 line.
- **major** — second position: `2.x.0.0`
- **minor** — third position: `2.0.6.0` → `2.0.7.0`

Moving to a new major Jellyfin version is itself a first-digit bump, and the second and third
positions reset. `1.0.16.0` and `1.0.17.0` moved the plugin to Jellyfin 12 and should have been
`2.0.0.0`; that was corrected at the `2.0.0.0` release.

**Why:** The first digit is what tells a user at a glance which Jellyfin line a release is for, which
matters because a Jellyfin major bump is exactly when an old server must stay on an old plugin build.
Positions two and three differ from the common reading, in which "minor" would be the second
position; applying the usual convention lands several releases ahead of where the version should be.

**How to apply:** Check `targetAbi` against the previous release before choosing a number — a change
in its first component is a first-digit bump. Otherwise "minor version bump" increments the third
number and "major" increments the second. Related: [[release-zip-naming]].
