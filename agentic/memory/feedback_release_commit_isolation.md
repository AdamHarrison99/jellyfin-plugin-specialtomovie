---
name: release-commit-isolation
description: Version bump, build.yaml and manifest.json go in their own commit, never mixed with code
metadata:
  type: feedback
---

The release mechanics are committed on their own: the `.csproj` `AssemblyVersion`/`FileVersion`
bump, `build.yaml`, and `manifest.json` — and nothing else. Everything else in the same release —
code fixes, `AUDIT.md`, `HANDOFF.md`, `README.md`, anything under `tools/` — is committed first, in
its own commit or commits.

When one file carries both kinds of change, split it across the two commits rather than letting the
release commit absorb the code change. The `.csproj` is the usual case: stage the build change with
the version number still at its old value, then bump the version as part of the release commit.
Every commit must build on its own, so a commit that adds a tooling project must also carry whatever
build change that project requires.

**Why:** The release commit is the one that gets tagged, and `manifest.json` is what Jellyfin servers
poll — so it needs to be readable and revertible on its own. A release bundling code changes cannot
be reverted to unpublish a bad build without also reverting the work, and it makes the diff that
matters impossible to review. Releases v1.0.13.0 through v1.0.15.0 bundled `.cs` changes with the
csproj and manifest, which is what this rule exists to stop.

**How to apply:** Group the working tree into the code/docs commit(s) first, then the release commit
last, so the tag lands on a commit that touches only version, `build.yaml` and `manifest.json`.
Related: [[never-commit]], [[no-release-without-permission]], [[release-zip-naming]],
[[commit-message-style]].
