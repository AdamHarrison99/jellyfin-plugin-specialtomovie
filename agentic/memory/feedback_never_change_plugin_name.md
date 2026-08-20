---
name: never-change-plugin-name
description: Plugin.Name and the manifest name field are internal identifiers — changing them wipes saved configuration
metadata:
  type: feedback
---

Never change `Plugin.Name`, the manifest `name` field, or any other identifier Jellyfin uses to
locate plugin configuration on disk. These are internal identifiers, not display names.

**Why:** Jellyfin derives the configuration file path from `Plugin.Name`. Renaming it points the
plugin at a different path, so previously saved configuration — library mappings, API keys, force
links, ignore list — is replaced with defaults. That loss is not recoverable from within the plugin.

**How to apply:** For a UI-visible rename, change only `DisplayName` and the HTML headings. The `Name`
property in `Plugin.cs`, the `name` field in `manifest.json`, and API route identifiers stay as they
are. Verify no internal identifier is caught up in a rename before making it.
