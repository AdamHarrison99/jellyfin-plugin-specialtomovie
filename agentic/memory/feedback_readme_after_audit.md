---
name: readme-after-audit
description: After a pre-release audit, check whether README.md still matches the code
metadata:
  type: feedback
---

As the last step of every pre-release audit, check whether `README.md` still reflects the codebase —
new features, changed defaults, renamed tasks, new configuration options, updated descriptions.

**Why:** The README has drifted before: a scheduled task was renamed and its default interval
changed, and several features shipped without the docs following.

**How to apply:** Diff the README against current behaviour at the end of the audit and report any
discrepancy. Related: [[always-update-audit]].
