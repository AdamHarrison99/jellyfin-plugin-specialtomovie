---
name: commit-message-style
description: Commit messages are a subject line plus bullets — no prose paragraphs
metadata:
  type: feedback
---

Write commit messages as a short imperative subject line, a blank line, then bullets. One bullet per
change. No prose paragraphs, no narrative rationale, no restating the diff at length.

```
Short imperative subject

- File or area: what changed
- File or area: what changed

Co-Authored-By: <trailer>
```

Keep each bullet to a line or two. If a change genuinely needs explaining, the explanation belongs in
the code, the docs, or a memory file — not in the commit body.

**Why:** Short bulleted messages stay scannable in `git log --oneline` and in the history view, and
they do not accumulate justification that goes stale as soon as the code moves on.

**How to apply:** Name the file or area, then the change. Do not name any file that lives outside the
repository, and do not include paths, personal detail, or an account of how the work went. Related:
[[agentic-docs-in-repo]], [[never-commit]].
