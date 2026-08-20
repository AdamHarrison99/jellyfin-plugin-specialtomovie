---
name: agentic-docs-in-repo
description: Everything under agentic/ is public — keep it free of PII and of links to anything outside the repo
metadata:
  type: feedback
---

The `agentic/` folder is version controlled and published with the code, so every word in it is
public. Two rules follow:

1. No personally identifying or machine-specific information — absolute local paths, usernames,
   hostnames, IP addresses, network share names, credentials, or development machine inventories.
   Describe such situations generically instead.
2. No reference to any file that lives outside the repository. This applies to commit messages too.

**Why:** These docs ship with the plugin source and land in public history permanently, where an
incidental detail cannot be taken back.

**How to apply:** Before writing to any file under `agentic/`, scan the new text for the categories
above. Keep source links repo-relative from `agentic/` — `../Data/PairStore.cs`, `../README.md`.
Related: [[never-commit]], [[always-update-audit]].
