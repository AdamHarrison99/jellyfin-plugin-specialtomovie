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
│   └── JellyfinPlugin-SpecialToMovie plan.md   ← original design doc (historical)
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

**Do not commit code or cut a release until all findings have been presented to the user for review.** Present each issue with its location, severity, and proposed resolution. Only proceed after the user approves.

Previous audit results are recorded in [`AUDIT.md`](AUDIT.md) alongside this file. Reference it to avoid re-flagging known false positives and to track the history of accepted risks.

**Always update `AUDIT.md` with results immediately after completing an audit — do not ask for confirmation first.**

After completing an audit, also check whether `README.md` needs updating to reflect any new features, changed defaults, renamed tasks, or new configuration options added since the last release.

## API Keys

TMDB and TVDB API keys are configured by the user in the plugin settings UI, not hardcoded.

Keys used for local testing live outside this repository and are deliberately not referenced from
any file in `agentic/`. Never commit an API key, token, or local machine configuration — not in
`agentic/`, not in source, not in a test fixture.

Everything under `agentic/` is public. Before writing to any of these docs, check that it contains
no credentials, absolute local paths, usernames, hostnames, IP addresses, share names, or other
machine-specific detail. Describe the situation generically instead ("if the working copy is on a
network share…") rather than naming a real environment.
