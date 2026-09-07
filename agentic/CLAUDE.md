# JellyfinPlugin-SpecialToMovie

## Repository Structure

`agentic/` holds the agent-facing docs and lives inside the repository, one level below the root:
```
<repo root>/
├── agentic/                        ← agent-facing docs
│   ├── CLAUDE.md                   ← this file
│   ├── AUDIT.md                    ← security/efficiency audit history
│   ├── IDEAS.md                    ← prioritised feature backlog
│   ├── HANDOFF.md                  ← codebase map, read first in a new session
│   ├── JellyfinPlugin-SpecialToMovie plan.md   ← original design doc (historical)
│   ├── plans/                       ← per-feature implementation plans, one file per feature
│   ├── tools/                       ← reusable tooling — harnesses, probes, build helpers
│   └── memory/                      ← standing conventions, one rule per file
├── README.md                       ← user-facing docs
├── manifest.json                   ← Jellyfin plugin manifest (serves as the plugin repository index)
├── build.yaml                      ← JPRM build manifest (no consumer; kept in sync by hand)
├── Jellyfin.Plugin.SpecialToMovie.csproj
├── Plugin.cs
├── PluginServiceRegistrator.cs
├── Lookup/                         ← TMDB + TVDB + Aggregated lookup services
├── Services/                       ← Detection + watch sync
├── HardLink/                       ← Hard link creation + NFO writing
├── Tasks/                          ← FullScanTask + CleanupTask
├── EventHandlers/                  ← Library event hooks
├── Api/                            ← REST controller
├── Configuration/                  ← Plugin config model + configPage.html
├── Models/                         ← MovieMatch, LinkedPair, PairStatus
└── Data/                           ← PairStore (JSON persistence)
```

Run git commands from the repository root — the parent of `agentic/`.

## Release Process

Jellyfin uses 4-part version numbers (e.g., `1.0.4.0`). **Only perform these steps when the user explicitly requests a new release.**

0. **Check `README.md`** for any needed updates (new features, changed defaults, renamed tasks, new config options).

1. **Update `AssemblyVersion` and `FileVersion`** in `Jellyfin.Plugin.SpecialToMovie.csproj`:
   ```xml
   <AssemblyVersion>1.0.4.0</AssemblyVersion>
   <FileVersion>1.0.4.0</FileVersion>
   ```
   Jellyfin reads the version from the DLL assembly metadata — if you skip this, the plugin will still report the old version in the dashboard.

   **Also update `build.yaml`** in the same commit — its `version`, `changelog`, `targetAbi`, and `framework` fields. No tooling currently reads this file (there is no CI and no JPRM setup), which is exactly why it silently drifted from `1.0.0.0` through fifteen releases until it was caught in the v1.0.16.0 audit. Keep it in sync so it never becomes misleading again.

2. **Build Release mode**:
   ```
   dotnet build -c Release
   ```

3. **Create the zip** containing only the DLL:
   ```powershell
   Compress-Archive -Path "bin\Release\net10.0\Jellyfin.Plugin.SpecialToMovie.dll" -DestinationPath "bin\Release\specialtomovie-v{VERSION}.zip"
   ```

4. **Compute the MD5 checksum** of the zip (not the DLL):
   ```powershell
   (Get-FileHash "bin\Release\specialtomovie-v{VERSION}.zip" -Algorithm MD5).Hash.ToLower()
   ```

5. **Update `manifest.json`**: Add a new entry at the top of the `versions` array with:
   - `version`: 4-part (e.g., `1.0.4.0`)
   - `changelog`: Short description of changes
   - `targetAbi`: Jellyfin server version (use latest supported, e.g., `10.11.10.0`)
   - `sourceUrl`: `https://github.com/AdamHarrison99/jellyfin-plugin-specialtomovie/releases/download/v{VERSION}/specialtomovie-v{VERSION}.zip`
   - `checksum`: MD5 hash from step 3
   - `timestamp`: ISO 8601 date **with time** (e.g., `2026-05-22T21:27:24Z`), not just midnight. Use the actual current UTC time when creating the entry.

6. **Commit and push** to `master`.

7. **Create the GitHub release** with the zip attached:
   ```
   gh release create v{VERSION} "bin\Release\specialtomovie-v{VERSION}.zip" --title "v{VERSION}" --notes "changelog"
   ```

The manifest.json in the repo is what Jellyfin servers poll for updates — pushing it to master is what triggers new installs/updates.

## Version Bumping Convention

- **Patch** (1.0.x.0): Bug fixes, detection improvements
- **Minor** (1.x.0.0): New features, new config options
- **Major** (x.0.0.0): Breaking changes

## Pre-Release Audit

Before every new version release, perform a full security and efficiency audit of the entire codebase:

1. Review all files for security vulnerabilities (injection, path traversal, unsafe deserialization, OWASP top 10)
2. Review for efficiency issues (N+1 queries, unnecessary allocations, redundant I/O, blocking async calls)
3. Check for race conditions in event handlers and concurrent operations
4. Verify all file system operations have proper safety checks
5. Review API endpoints for authorization and input validation gaps
6. Sweep every tracked file for personally identifying information — see [PII & Documentation Sweep](#pii--documentation-sweep) below
7. Sweep the scratchpad and every other temporary working directory — see [Scratchpad & Temporary File Sweep](#scratchpad--temporary-file-sweep) below

**Do not commit code or cut a release until all findings have been presented to the user for review.** Present each issue with its location, severity, and proposed resolution. Only proceed after the user approves.

Previous audit results are recorded in [`AUDIT.md`](AUDIT.md) alongside this file. Reference it to avoid re-flagging known false positives and to track the history of accepted risks.

**Always update `AUDIT.md` with results immediately after completing an audit — do not ask for confirmation first.**

After completing an audit, also check whether `README.md` needs updating to reflect any new features, changed defaults, renamed tasks, or new configuration options added since the last release.

### PII & Documentation Sweep

Step 6 covers **every tracked file**, not only the prose docs. The repository is public and its
history is permanent, so a personal detail that reaches `master` cannot be withdrawn. In scope:

- source **comments** — `///` XML docs, `//` notes, and commented-out code
- everything under `agentic/`, including `agentic/memory/`
- `README.md`, `manifest.json`, `build.yaml`, the `.csproj`, and the config UI text in `configPage.html`
- log messages and exception strings, which end up pasted into user-submitted logs
- the commit message written for the release itself

A finding is anything identifying a **person** or a **machine**: absolute or drive-rooted paths,
Windows usernames or home directories, cloud-drive folder names, network share or UNC paths,
hostnames, IP addresses, email addresses, credentials or API keys, session identifiers, personal
media-library or directory names, developer-machine inventories, and direct quotes of or
characterisations of the user. Replace each with a repo-relative path (`../Data/PairStore.cs`) or a
generic description ("if the working copy is on a network share...").

Scope the sweep to tracked files so build output and ignored local files are excluded:

```powershell
$files = git ls-files

# 1. Drive-rooted paths, UNC paths, home directories
Select-String -Path $files -Pattern '(^|[^A-Za-z0-9_])[A-Za-z]:[\\/]', '\\\\[A-Za-z0-9]', '/home/', '/Users/', 'USERPROFILE', '/Volumes/', '/mnt/'

# 2. Email addresses
Select-String -Path $files -Pattern '[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}'

# 3. This machine's identity, read from the environment so no real value is ever written into these docs
$me = @($env:USERNAME, $env:COMPUTERNAME, (git config user.name), (git config user.email)) | Where-Object { $_ }
Select-String -Path $files -SimpleMatch -Pattern $me

# 4. Dotted quads — expect version-number noise; anything that is not a version is a finding
Select-String -Path $files -Pattern '\b([0-9]{1,3}\.){3}[0-9]{1,3}\b'

# 5. Every comment line, to be read rather than pattern-matched
Select-String -Path $files -Pattern '^\s*(//|///|\*|<!--)'
```

Checks 1-4 are mechanical. Check 5 is a read-through: a personal detail phrased in prose ("the drive
I keep recordings on") matches no pattern. Sweep the working tree's uncommitted edits too, since this
runs before the release commit exists.

Known-acceptable matches are listed in [`AUDIT.md`](AUDIT.md#known-acceptable-matches-do-not-re-flag)
— check them before flagging anything, and add to that list when a new one is settled.

Record the result in `AUDIT.md` under the audit entry **even when the sweep is clean**: that it ran,
what it covered, and every finding with its resolution.

### Scratchpad & Temporary File Sweep

Step 7 covers the agent's scratchpad directory and anywhere else working files were parked during
the release cycle — a system temp folder, a throwaway probe project, a cloned reference repository,
a copy of `agentic/` taken "just in case". None of it is tracked, so the PII sweep above never sees
it, and it survives the session that created it.

Two rules decide what happens to each thing found:

- **Reusable tooling is promoted, not deleted.** Anything that would be worth running again — a test
  harness, an ABI or reflection probe, a build or packaging helper, a verification script — moves
  into [`tools/`](tools/) with a note in that folder's `README.md` saying what it does and how to run
  it. See [`tools/README.md`](tools/README.md) for what qualifies and the PII rules it must meet
  first. A harness that is described in `AUDIT.md` but exists nowhere in the repository is the
  failure this rule prevents.
- **Everything else is deleted.** In particular, **Jellyfin server binaries or server/user data must
  never be left behind**: extracted server assembly sets, a real or sample `library.db`, plugin
  configuration XML, `PairStore` JSON from a live install, API keys, device identifiers, user or
  media-library listings, and exported logs. The same goes for temporary backups of `agentic/` or of
  the source tree, stale `bin/`/`obj/` output from probe projects, and cloned third-party
  repositories, all of which are re-creatable and none of which should outlive their session.

Delete the copy as soon as it has served its purpose rather than waiting for the next audit — the
sweep is the backstop, not the plan.

Record the result in `AUDIT.md` under the audit entry **even when nothing was found**: what was
swept, what was promoted into `tools/`, and what was deleted. Name deleted items by kind, never by
absolute path.

## Memory

Project conventions — any standing rule or constraint about how this codebase is worked on — are
written as files in [`memory/`](memory/). **Read [`memory/MEMORY.md`](memory/MEMORY.md) at the start
of every session**; it indexes every rule currently in force, and several of them constrain what you
are allowed to do. Never leave a convention only in a session transcript.

This folder is public, so it holds **project conventions only**. Anything personal or
machine-specific — environment details, local paths, user preferences — goes to the agent's own
local memory store outside the repository instead, and is never written here. Decide which of the
two a memory belongs to before writing it.

One fact per file, named `feedback_<slug>.md`, with this shape:

```markdown
---
name: <short-kebab-case-slug>
description: <one-line summary>
metadata:
  type: feedback
---

<the rule>

**Why:** <the reason it exists>

**How to apply:** <what to do differently> Related: [[other-slug]].
```

After adding a file, add one line for it to [`memory/MEMORY.md`](memory/MEMORY.md), which is the
index. Before writing a new memory, check whether an existing file already covers the ground and
update that one instead of creating a near-duplicate; delete any memory that turns out to be wrong.

Write these impersonally — the rule, its rationale, and how to apply it. Do not quote the user,
characterise them, log that a correction happened, or include session identifiers, absolute paths, or
any other machine-specific detail.

## API Keys

TMDB and TVDB API keys are configured by the user in the plugin settings UI, not hardcoded.

Keys used for local testing live outside this repository and are deliberately not referenced from
any file in `agentic/`. Never commit an API key, token, or local machine configuration — not in
`agentic/`, not in source, not in a test fixture.

Everything under `agentic/` is public. Before writing to any of these docs, check that it contains
no credentials, absolute local paths, usernames, hostnames, IP addresses, share names, or other
machine-specific detail. Describe the situation generically instead ("if the working copy is on a
network share…") rather than naming a real environment.
