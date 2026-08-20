---
name: version-bumping
description: Minor bumps the third position (1.0.x.0); major bumps the second (1.x.0.0)
metadata:
  type: feedback
---

This project's version convention, against the 4-part number `1.0.0.0`:

- **minor** — third position: `1.0.6.0` → `1.0.7.0`
- **major** — second position: `1.x.0.0`
- **first digit** — reserved for a full rewrite of the plugin

**Why:** This differs from the common reading, in which "minor" would be the second position.
Applying the usual convention lands several releases ahead of where the version should be.

**How to apply:** "Minor version bump" increments the third number; "major" increments the second.
Never touch the first digit unless a full rewrite is explicitly stated. Related:
[[release-zip-naming]].
