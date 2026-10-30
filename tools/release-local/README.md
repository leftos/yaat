# release-local

Builds a client release without GitHub Actions and creates its draft. It's the fallback when `release.yml` and `release-macos.yml` can't run. Every asset the hosted release has is built under the same name, and the draft gets the same body. Publishing stays where it is for a hosted release: `prepare-release` 7.3, or the deploy.

```powershell
pwsh -NoProfile -File tools/release-local/release.ps1 -Tag v<version>            # build all, create the draft
pwsh -NoProfile -File tools/release-local/release.ps1 -Tag v<version> -BuildOnly # build and gather, no release
```

The tag must be pushed. The script refuses a run when a GitHub release of that name already exists, so it never races CI's draft or overwrites it. When the hosted workflows ran part of the way, cancel them, delete their draft, then run this.

| File | What it does |
|---|---|
| `release.ps1` | Runs on this PC. It checks the tag out into `X:\dev\yaat.wt\release-local-<tag>`, copies this folder into it when the tag predates it, runs both legs at once through the gate, gathers and renames the assets into `.tmp/release-local/<tag>/release`, writes the body, and creates the draft (`-ReleaseName` gives it another name for a test). |
| `pack-win-linux.ps1` | The Windows and Linux legs, routed to the Windows builder: `release.yml`'s `build` publish for `win-x64` and `linux-x64`, then `vpk pack` and `vpk [linux] pack` (vpk cross-packs the AppImage on Windows). |
| `pack-macos.ps1` | The macOS leg, pinned to `builder@mac-mini`: publish `osx-arm64` and `osx-x64`, the x86_64 slice check, then `vpk pack` signed with the Developer ID identities and notarized. |
| `release-local-common.ps1` | The vpk pin (`VPK_VERSION` of the workflows), the publish command, and the exit-status check. |
| `release-notes.sh` | The release notes and body, shared with `release.yml` (`notes` and `body`). |

## Secrets

`release.ps1` reads them into its own process and hands each leg the names it needs as bare `--env` gate flags. They never appear on a command line or in a log.

- `LMKIT_LICENSE_KEY`: from this checkout's `.env`.
- The Developer ID Application and Installer `.p12` files, their password (`p12-password.txt`), the App Store Connect notary key (`AuthKey_*.p8`), and the key and issuer IDs plus both signing identities (`ids.env`): all from `G:\My Drive\Private Config\yaat\secrets\macos-signing\`, whose `INVENTORY.md` describes them. They reach the Mac under `release-macos.yml`'s secret names.

These are the local set, separate from the GitHub `MACOS_*` secrets. They are new certificates and a separate notary key, because GitHub secrets can't be read back.

## Footguns

- **The Intel smoke test is half done.** The mac-mini is Apple Silicon, so only `smoke-intel`'s static check runs: every shipped `osx-x64` `.dylib` carries an x86_64 slice, and `Yaat.Client` is an x86_64 executable. The LM-Kit transcribe on an Intel CPU is skipped.
- **The Mac's keychain search list.** `release-macos.yml` replaces the user search list with its temporary keychain, which is harmless only on a throwaway runner. `pack-macos.ps1` adds the keychain beside the existing ones, then puts the list back and deletes the keychain and the decoded `.p12`/`.p8` files in a `finally`.
- **Native calls are checked one by one.** PowerShell rebinds a wrapped command's switches (`-c` to a declared `-Command`) and drops `--version` from `$args`, so every native command is called directly and followed by `Assert-LastExit`.
- **Gate routing** lives in this PC's `%LOCALAPPDATA%\gate\remote.json`:
  - `pwsh -NoProfile -File tools/release-local/pack-win-linux.ps1 *` under yaat's `repos`
  - `.../pack-macos.ps1 *` under the macOS builder's
  - `.tmp/release-local/out/**` in both `outputs`, so the packages come back

  A machine without those rules runs the Windows leg locally, and the macOS leg can't run.
