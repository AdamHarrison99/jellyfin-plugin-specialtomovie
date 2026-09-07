---
name: scratchpad-hygiene
description: Reusable tooling is promoted to agentic/tools/; server, user and temporary data is deleted from scratch directories when finished with
metadata:
  type: feedback
---

Nothing of value is left in a scratchpad, and nothing that should not exist is left there either.

**Reusable tooling always ends up in [`../tools/`](../tools/)** — test and verification harnesses,
ABI or reflection probes, build and packaging helpers, any script that took real effort to get right.
It moves there as soon as it works, with a row added to that folder's `README.md` inventory.

**Jellyfin server data, user data and temporary data is deleted as soon as it has served its
purpose** — extracted server assembly sets, a `library.db`, plugin configuration XML, `PairStore`
JSON from a live install, API keys, device identifiers, user or media-library listings, exported
logs, backups of `agentic/` or of the source tree, probe-project build output, and cloned
third-party repositories. This applies to the scratchpad and to any other temporary working
directory used during a session.

**Why:** Scratch directories are wiped between sessions and are never swept by the PII check, which
only looks at tracked files. So the two failure modes run in opposite directions: work worth keeping
is silently lost — an audit entry can cite a harness that exists nowhere in the repository — while
copies of server and user data quietly persist on disk long after the session that needed them,
outside every safeguard that covers the repository.

**How to apply:** Promote or delete at the moment the work is finished rather than deferring it.
Step 7 of the pre-release audit is the backstop: sweep the scratchpad and every other temporary
directory, promote what is reusable, delete the rest, and record what was swept in `AUDIT.md` even
when the result is clean. See `CLAUDE.md` -> Pre-Release Audit -> Scratchpad & Temporary File Sweep.
Related: [[pii-sweep-in-audit]], [[agentic-docs-in-repo]], [[always-update-audit]].
