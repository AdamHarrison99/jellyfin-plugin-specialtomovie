---
name: pii-sweep-in-audit
description: Every audit sweeps all tracked files — source comments included — for personal or machine-identifying detail
metadata:
  type: feedback
---

Every audit includes a sweep of all tracked files for personally identifying information: source
comments, everything under `agentic/` including `agentic/memory/`, `README.md`, `manifest.json`,
`build.yaml`, the `.csproj`, config UI text, and log or exception strings. Prose documentation is not
the whole scope — an example path in a code comment or a machine name in a log message leaks just as
permanently.

A finding is anything identifying a person or a machine: absolute or drive-rooted paths, usernames,
home directories, cloud-drive folder names, network share or UNC paths, hostnames, IP addresses,
email addresses, credentials, session identifiers, personal media-library names, or developer-machine
inventories.

**Why:** The repository is public and its history is permanent, so a personal detail that reaches
`master` cannot be withdrawn. The categories that actually leak are incidental rather than
deliberate, which is why the check has to be mechanical and cover comments, not just docs.

**How to apply:** Run it as step 6 of the pre-release audit using the commands in `CLAUDE.md` ->
Pre-Release Audit -> PII & Documentation Sweep. Consult the known-acceptable list in `AUDIT.md`
before flagging a match, and record the result under the audit entry even when the sweep is clean.
Related: [[agentic-docs-in-repo]], [[always-update-audit]], [[readme-after-audit]].
