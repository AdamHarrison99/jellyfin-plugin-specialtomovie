---
name: build-before-push
description: Run dotnet build -c Release and verify it succeeds before handing off any change
metadata:
  type: feedback
---

Run `dotnet build -c Release` and confirm it succeeds before reporting a change as finished.

**Why:** Code that does not compile breaks the build for anyone who pulls it, and a green build is
the minimum evidence that an edit is safe to commit.

**How to apply:** Build after making code changes and before handing off. If the build fails, fix the
cause rather than reporting the change as done. Related: [[never-commit]].
