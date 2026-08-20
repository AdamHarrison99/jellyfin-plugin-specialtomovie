---
name: never-commit
description: Never run git commit or git push — finish the files, verify the build, and stop
metadata:
  type: feedback
---

Never run `git commit` or `git push`. This holds even when a task appears to end in a commit, when
the working tree is clean and verified, and when the request is to add something to the repository.
Stop once the files are correct on disk.

**Why:** Commits and pushes are reviewed and performed manually. Publishing to a public remote is
irreversible, so that decision stays with a human.

**How to apply:** Complete the edits, verify with a build ([[build-before-push]]), report what changed
and where, and stop. Do not offer commit or push as an option, and do not ask for permission to
commit. Read-only git — `status`, `log`, `diff`, `show` — is fine; anything that writes to history or
a remote is not. Related: [[no-release-without-permission]].
