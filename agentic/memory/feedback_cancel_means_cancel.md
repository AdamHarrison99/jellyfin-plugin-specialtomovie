---
name: cancel-means-cancel
description: Cancel on a confirmation is always a full no-op — never overload it with a secondary action
metadata:
  type: feedback
---

In any confirmation the plugin shows, Cancel means **nothing happens**. It must never be repurposed
as a second answer — "Cancel = keep the files but remove the entries anyway" is not acceptable, even
when the prompt spells that out. A confirmation has exactly two outcomes: proceed with the described
action, or do nothing at all.

Where an action's scope depends on a setting, the *setting* decides the scope and the prompt states
what will happen. It does not become a question with two affirmative answers. The remove-selected
button is the reference case: with "Remove plugin managed items automatically" enabled, confirming
removes the pairs and deletes their plugin managed movie items together; with it disabled, confirming
removes the pairs only; cancelling does nothing in both cases.

**Why:** Cancel is the universal escape from a destructive action. A user who realises they picked the
wrong rows reaches for it expecting to abort, and a Cancel that still deletes database entries
destroys data the user was actively trying to protect. Reading the prompt carefully is not something
a destructive dialogue may require.

**How to apply:** Before shipping a confirmation, check that the Cancel path returns without touching
the store, the filesystem, or the API. If an action genuinely needs three outcomes, that is a real
dialog with three buttons — not `confirm()` with its two answers relabelled. Related:
[[always-update-audit]].
