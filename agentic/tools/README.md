# tools/

Reusable tooling for this project. **Anything worth running a second time lives here** — not in a
scratchpad, not in a system temp folder, not quoted only inside an `AUDIT.md` entry.

Scratch directories are wiped between sessions. A harness written there is gone by the next release,
and the audit that cited it can no longer be reproduced. Moving it here makes it version controlled,
published with the code, and reviewable.

## What belongs here

- Test and verification harnesses — reflection probes against the built DLL, DOM-stub harnesses for
  `Web/specialtomovie.js`, assertion scripts backing an audit entry
- ABI and compatibility probes against a target Jellyfin server version
- Build, packaging, and release helpers beyond the plain `dotnet build` documented in `CLAUDE.md`
- Any one-off script that took real effort to get right and would be rewritten from scratch otherwise

## What does not belong here

- **Jellyfin server binaries or any server/user data.** Never commit extracted server assembly sets,
  a `library.db`, plugin configuration XML, `PairStore` JSON from a live install, API keys, device
  identifiers, user or media-library listings, or exported logs. A tool that needs a server assembly
  resolves or downloads it at run time into a temporary directory and deletes it afterwards.
- Build output — `bin/`, `obj/`, and the shells probe projects leave behind
- Cloned third-party repositories, which are re-creatable from their source
- Backups of `agentic/` or of the source tree
- Throwaway experiments that answered one question and will not be asked again — delete those

## Rules for anything added here

1. `agentic/` is public. A tool must contain no absolute or drive-rooted path, username, home
   directory, hostname, IP address, network share, email address, credential, or personal
   media-library name. Take paths as arguments or resolve them relative to the repository root.
2. Never reference a file outside the repository. Configuration a tool needs — API keys in
   particular — is passed in at run time, never committed and never pointed at by path.
3. Write no data into the repository at run time. A tool that produces output writes it to a
   temporary directory the caller supplies, and the caller cleans it up.
4. Add a row to the table below when adding a tool, and remove the row when removing one.

## Inventory

| Tool | What it does | How to run it |
| --- | --- | --- |
| _(none yet)_ | | |

The pre-release audit sweeps the scratchpad for tooling that should have been promoted here — see
`CLAUDE.md` → Pre-Release Audit → Scratchpad & Temporary File Sweep.
