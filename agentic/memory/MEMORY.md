# Memory

Standing conventions and constraints for this project, one per file. Each records a rule, why it
exists, and how to apply it.

- [Never commit](feedback_never_commit.md) — never run git commit or git push; finish the files, verify the build, and stop
- [No release without permission](feedback_no_release_without_permission.md) — each release action needs its own explicit request
- [Never change plugin Name](feedback_never_change_plugin_name.md) — Plugin.Name and the manifest name control config file paths; changing them wipes saved settings
- [agentic/ docs are public](feedback_agentic_docs_in_repo.md) — keep agentic/ free of PII and of references to anything outside the repo
- [Version bumping convention](feedback_version_bumping.md) — minor is 1.0.x.0, major is 1.x.0.0, first digit only for a full rewrite
- [Commit message style](feedback_commit_message_style.md) — subject line plus bullets, one per change; no prose paragraphs
- [Release zip naming](feedback_release_zip_naming.md) — zip and tag use the short version (v1.0.10); manifest sourceUrl must match exactly
- [Build before handing off](feedback_build_before_push.md) — run dotnet build -c Release and verify success
- [Always update AUDIT.md](feedback_always_update_audit.md) — write audit results immediately, without asking first
- [README after audit](feedback_readme_after_audit.md) — check README against the code at the end of every audit
