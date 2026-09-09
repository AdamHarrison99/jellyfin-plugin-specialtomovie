---
name: comment-lint-in-audit
description: Every published source file must pass check-comments.mjs; rationale lives in ARCHITECTURE.md
metadata:
  type: feedback
---

`node agentic/tools/check-comments.mjs .` must print `clean` as step 3 of every pre-release audit.
Everything outside `agentic/` is in scope — `agentic/` is the only exempt directory.

A comment is a short note that makes the next line readable: at most two adjacent lines, 100
characters a line, 180 across a run, two sentences, no rationale connectives ("because", "rather
than", "so that"), no narration of history, no `///` XML doc comments, no `/* */` blocks, no
commented-out code, and nothing that restates the line below it. A leading `!` marks a trap without
exempting the line from the limits.

**Why:** Long comments drift from the code they sit above and are invisible to review, while the
design rationale they carried is the expensive part — most of it was paid for with a shipped bug.
Holding source to short notes and keeping the reasoning in one reviewed document keeps both usable.

**How to apply:** Anything longer than a note goes to [`agentic/ARCHITECTURE.md`](../ARCHITECTURE.md)
under the matching section, with the comment reduced to a pointer naming it. When removing an
explanation from a comment, **move it there — never delete it**. Run the linter with no arguments to
check only what the branch changed; the whole-tree form is the release gate. Related:
[[always-update-audit]], [[agentic-docs-in-repo]].
