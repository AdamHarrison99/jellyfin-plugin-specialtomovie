---
name: no-release-without-permission
description: Each release action needs its own explicit request — never chain one off another task
metadata:
  type: feedback
---

None of the following happen unless that specific action is explicitly requested at that moment:

- `git push`, for any commit, release or not
- editing `manifest.json`
- creating or recreating release zips
- computing checksums
- `gh release create`, `gh release upload`, or `gh release delete`
- bumping version numbers in the `.csproj` for release purposes

Permission for one action is not permission for the next. A request to fix a changelog, or to build,
is not a request to publish.

**Why:** Anything that leaves the local machine is irreversible once it reaches the remote or the
plugin repository index, so each step is authorised separately.

**How to apply:** After a local change, stop and report it. Never auto-repair a broken release by
rebuilding and re-uploading. The full release sequence, when it is requested, is documented in
[CLAUDE.md](../CLAUDE.md). Related: [[never-commit]], [[release-zip-naming]].
