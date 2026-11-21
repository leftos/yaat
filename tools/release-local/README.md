# release-local

Builds a client release on the local builders and creates its draft. It's the default release path: a `v*` tag push starts no hosted build, and `release.yml` and `release-macos.yml` stay as a manual fallback (`gh workflow run release.yml --ref v<version>`, then `gh workflow run release-macos.yml --ref v<version>`) for when the builders are unavailable.

Every asset the hosted release has is built under the same name, and the draft gets the same body. Publishing stays where it is for a hosted release: `prepare-release` 7.3, or the deploy.

```powershell
pwsh -NoProfile -File tools/release-local/release.ps1 -Tag v<version>            # build all, create the draft
pwsh -NoProfile -File tools/release-local/release.ps1 -Tag v<version> -BuildOnly # build and gather, no release
```

The tag must be pushed. The script refuses a run when a GitHub release of that name already exists, so it never races a hosted run's draft or overwrites it. To switch to the hosted fallback after a partial local run, delete the draft first; to switch back after a hosted run started, cancel it and delete its draft.

| File | What it does |
|---|---|
| `release.ps1` | Runs on this PC. It checks the tag out into `X:\dev\yaat.wt\release-local-<tag>`, copies this folder into it when the tag predates it, runs both legs at once through the gate, gathers and renames the assets into `.tmp/release-local/<tag>/release`, writes the body, and creates the draft (`-ReleaseName` gives it another name for a test). |
| `pack-win-linux.ps1` | The Windows and Linux legs, routed to the Windows builder: `release.yml`'s `build` publish for `win-x64` and `linux-x64`, then `vpk pack` and `vpk [linux] pack` (vpk cross-packs the AppImage on Windows). |
| `pack-macos.ps1` | The macOS leg, pinned to `builder@mac-mini`: publish `osx-arm64` and `osx-x64`, the x86_64 slice check, then `vpk pack` signed with the Developer ID identities and notarized. |
| `macos-keychain.ps1` | The Mac user keychain search list helpers `pack-macos.ps1` uses: read the list, add a keychain (pruning entries whose file is gone), remove only that keychain. |
| `test-macos-keychain.ps1` | A probe of those helpers, run on the Mac inside the `mac-signing` lock; it sets the starting list back whatever happens. |
| `release-local-common.ps1` | The vpk pin (`VPK_VERSION` of the workflows), the publish command, and the exit-status check. |
| `release-notes.sh` | The release notes and body, shared with `release.yml` (`notes` and `body`). |

## Secrets

`release.ps1` reads them into its own process and hands each leg the names it needs as bare `--env` gate flags. They never appear on a command line or in a log.

- `LMKIT_LICENSE_KEY`: from this checkout's `.env`.
- The Developer ID Application and Installer `.p12` files, their password (`p12-password.txt`), the App Store Connect notary key (`AuthKey_*.p8`), and the key and issuer IDs plus both signing identities (`ids.env`): all from `G:\My Drive\Private Config\yaat\secrets\macos-signing\`, whose `INVENTORY.md` describes them. They reach the Mac under `release-macos.yml`'s secret names.

These are the local set, separate from the GitHub `MACOS_*` secrets. They are new certificates and a separate notary key, because GitHub secrets can't be read back.

## Footguns

- **The Intel smoke test is half done.** The mac-mini is Apple Silicon, so only `smoke-intel`'s static check runs: every shipped `osx-x64` `.dylib` carries an x86_64 slice, and `Yaat.Client` is an x86_64 executable. The LM-Kit transcribe on an Intel CPU is skipped.
- **The Mac's keychain search list.** `release-macos.yml` replaces the user search list with its temporary keychain, which is harmless only on a throwaway runner. `pack-macos.ps1` adds the keychain to the current list (pruning entries whose file is gone), then in a `finally` deletes the keychain, removes only its entry from the list as it then stands, and deletes the decoded `.p12`/`.p8` files.

  A failure of the list call there is a warning, so it never skips the delete or hides the error that ended the run.

  A hard-killed remote pwsh runs no `finally`, so a leftover signing keychain stays on disk and in the list until the next yaat run deletes it or prunes it.

  The macOS leg runs under gate's `mac-signing` lock (`--lock mac-signing`), shared with rustling-tulip, so the two repos never sign on the mac-mini at once; the list helpers live in `macos-keychain.ps1`.

  To check them on the Mac, run the probe inside the same lock: `pwsh -NoProfile -File tools/gate.ps1 run --log .tmp/kc-probe.log --timeout 1800 --slot heavy --builder builder@mac-mini --lock mac-signing -- pwsh -NoProfile -File tools/release-local/test-macos-keychain.ps1`. Its log ends with `keychain probe: PASS` only when every check held and the starting list was set back.

  The probe does not cover `Remove-UserKeychain` when the login keychain is missing (it would need the builder's login keychain deleted).
- **Native calls are checked one by one.** PowerShell rebinds a wrapped command's switches (`-c` to a declared `-Command`) and drops `--version` from `$args`, so every native command is called directly and followed by `Assert-LastExit`.
- **Gate routing** lives in this PC's `%LOCALAPPDATA%\gate\remote.json`:
  - `pwsh -NoProfile -File tools/release-local/pack-win-linux.ps1 *` under yaat's `repos`
  - `.../pack-macos.ps1 *` under the macOS builder's
  - `pwsh -NoProfile -File tools/release-local/test-macos-keychain.ps1` under the macOS builder's too, for the probe
  - `.tmp/release-local/out/**` in both `outputs`, so the packages come back

  A machine without those rules runs the Windows leg locally, and the macOS leg can't run.
