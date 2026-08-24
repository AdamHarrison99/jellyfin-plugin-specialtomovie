---
name: readme-after-audit
description: Check README.md against the code after an audit, but never edit it without explicit permission
metadata:
  type: feedback
---

As the last step of every pre-release audit, check whether `README.md` still reflects the codebase —
new features, changed defaults, renamed tasks, new configuration options, updated descriptions.

**Never edit `README.md` without being asked for that edit specifically.** Being told to implement a
feature, to audit, or to "check the README" is not permission to change it. Report the discrepancies
and wait.

**Why:** The README is the project's public face and its wording is the maintainer's own. It has also
drifted before — a scheduled task was renamed and its default interval changed, and several features
shipped without the docs following — so the *check* still matters; only the editing is reserved.

**How to apply:** Diff the README against current behaviour at the end of the audit. List each
discrepancy with the exact wording you would use, and let the maintainer decide. If a change was made
without being asked, revert it with `git checkout -- README.md` and offer the diff instead. Related:
[[always-update-audit]], [[never-commit]].
