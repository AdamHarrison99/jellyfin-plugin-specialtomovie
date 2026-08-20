---
name: always-update-audit
description: Update AUDIT.md immediately after completing an audit, without asking first
metadata:
  type: feedback
---

Write audit results into `AUDIT.md` as soon as the audit pass finishes. Do not pause to ask whether
the file should be updated — the record is part of the audit, not a follow-up step.

**Why:** An audit with no written record is incomplete, and the next session re-flags the same
accepted risks and known false positives.

**How to apply:** Treat "run the audit" as including "write the findings to AUDIT.md" in the same
workflow. Related: [[readme-after-audit]], [[agentic-docs-in-repo]].
