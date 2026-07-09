# Installer and release pipeline

YAAT ships as a Velopack installer with auto-update, built and published by a tag-driven GitHub Actions workflow. This doc covers the packaging stack, the auto-update path, the CRC install-time configuration prompt, and the `release.yml` flow end-to-end.

## Versioning

The version lives in `Directory.Build.props` at the yaat repo root (`<Version>`). A push of a `v*` tag whose version matches that property triggers a release; the `/prepare-release` skill bumps the property, promotes the changelog heading, and pushes the tag.

## Packaging (Velopack)

[Velopack](https://github.com/velopack/velopack) produces the installers via the `vpk` CLI. Each platform job downloads the self-contained `dotnet publish` output, runs `vpk pack` with `--packId YaatClient`, and emits a per-platform installer plus auto-update metadata:

- **Windows** — `YaatClient-win-Setup.exe` and a `*-win-Portable.zip`.
- **Linux** — `YaatClient.AppImage` (self-contained; serves as both installer and portable).
- **macOS** — one `.pkg` + `*-Portable.zip` pair per architecture (`osx-arm64`, `osx-x64`), all signed and notarized (see below). A `.icns` is generated from `icon.png` via `sips` + `iconutil`, and a custom `Info.plist` (from `build/macos/Info.plist.template`) carries `NSMicrophoneUsageDescription` for push-to-talk capture and `LSMinimumSystemVersion` for the macOS 14 floor that .NET 10 imposes.

Portable archives bundle the single-file exe plus sibling native DLLs (libSkiaSharp, HarfBuzz, LM-Kit) and run without install or auto-update.

## macOS architectures and update channels

Apple Silicon and Intel ship as two separate packages rather than one universal bundle: `PublishSingleFile` embeds the managed payload in the apphost, so the two publishes cannot be `lipo`'d together without giving up single-file.

Velopack resolves updates **by channel**, and its docs require one channel per os/arch — otherwise the updater will eventually hand an Intel package to an Apple Silicon client. So `vpk pack` is passed `--channel osx-arm64` / `--channel osx-x64`, which also puts the channel into every filename it emits (`YaatClient-osx-arm64-Setup.pkg`, `releases.osx-arm64.json`, …). It is additionally passed `--runtime <rid>`, which records the architecture as `.pkg` metadata so the macOS Installer refuses the wrong package — without it Rosetta would silently run the Intel build on Apple Silicon.

`UpdateService` needs no channel argument: constructed with `channel: null`, Velopack looks for updates in whatever channel the *installed* release was packed with.

**The `osx` → `osx-arm64` supersede.** Before the split, arm64 shipped on Velopack's default `osx` channel, and those installs fetch `releases.osx.json` by exact filename. `release-macos.yml` therefore publishes copies of the arm64 index under the old names (`releases.osx.json`, `assets.osx.json`, `RELEASES-osx`) alongside the new ones. An old client finds the copy, updates once, and thereafter carries the `osx-arm64` channel and follows it. Intel never had an `osx` install, so it needs no equivalent. These three copies can be deleted once the arm64 install base has moved off the old channel.

## macOS Intel validation

`release-macos.yml`'s `smoke-intel` job gates the release on a real x86_64 machine (`macos-15-intel` — the last Intel image GitHub offers, available until August 2027). It verifies that every native the x64 publish ships carries an `x86_64` slice, then runs a Whisper transcription through `Yaat.SpeechSandbox --lmkit-stt` to prove LM-Kit's native backend actually loads and runs on an Intel CPU.

Two LM-Kit dylibs are deliberately exempt from the slice check. LM-Kit publishes its macOS natives from a RID-agnostic `runtimes/osx/` folder, so `LM-Kit.ggml.backend.metal.dylib` (arm64-only) and `LM-Kit.onnxruntime.dylib` (arm64-only) are copied into the x64 output too. Neither is loaded on Intel: Metal is skipped in favour of the CPU backend, and LM-Kit's ONNX runtime only serves ONNX models while YAAT loads GGUF exclusively. Any *other* dylib missing an `x86_64` slice fails the release rather than shipping a build that aborts at its first `dlopen`.

## Code signing and notarization (macOS)

The macOS `.app` and `.pkg` are signed with Apple Developer ID certificates and
notarized by Apple, so they launch without a Gatekeeper warning and auto-update
silently. `vpk pack` drives the whole flow: it codesigns the bundle under the
hardened runtime with `build/macos/Yaat.Client.entitlements`, submits it to
`notarytool`, staples the ticket, then repeats for the `.pkg`.

Signing is **conditional on secrets being present** — exactly like the
`LMKIT_LICENSE_KEY` fallback. When the `MACOS_*` secrets are not configured (a
fork, or before setup), the `package-macos` job logs a warning and produces an
unsigned package that still installs but trips Gatekeeper. Windows and Linux are
unaffected; Windows installers remain unsigned (SmartScreen still warns).

The entitlements file grants the four hardened-runtime keys Microsoft documents
for self-contained .NET apps (JIT, unsigned executable memory, dyld env vars,
disabled library validation) plus `com.apple.security.device.audio-input` for
`AudioCaptureService`. Setting up the certificates and the eight required GitHub
secrets is a one-time task documented in [`macos-code-signing.md`](macos-code-signing.md).

## Auto-update

`UpdateService` checks GitHub Releases via Velopack's `GithubSource` and surfaces an update notification bar in `MainWindow`. The auto-updater fetches the `RELEASES*`, `*.json`, and `*-full.nupkg` assets by exact filename, so `release.yml` copies those metadata files into the release **without renaming** — only the user-facing installer/portable filenames get the `-{version}-` suffix.

## CRC install-time configuration

`CrcConfigService` (a C# port of `Setup-CrcEnvironment.ps1`) registers the YAAT server in CRC's `DevEnvironments.json`. It runs during the Velopack install callback via `CrcInstallPrompt`, and skips when CRC is not installed or the entries already exist. It is also reachable later from the Tools > Configure CRC menu.

## Release workflow (`release.yml`)

Triggered on `push` of a `v*` tag. Jobs run in dependency order:

1. **version** — reads `<Version>` from `Directory.Build.props` and the short SHA.
2. **changelog** — extracts the `CHANGELOG.md` section matching the tag, splitting out a `### Highlights` subsection (authored by `/prepare-release`) from the changelog body.
3. **build** — `dotnet publish` of `src/Yaat.Client` for `win-x64` and `linux-x64` (`release-macos.yml` publishes `osx-arm64` and `osx-x64` itself).
4. **package-win / package-linux / package-macos** — `vpk pack` per platform. `package-macos` (in `release-macos.yml`, once per architecture) additionally imports the Developer ID certificates and an App Store Connect API key into a temporary keychain, then signs + notarizes (skipped when the `MACOS_*` secrets are absent).
5. **release** — assembles `release/`, builds the release body from highlights + changelog + a download table, and publishes via `softprops/action-gh-release` with the default `GITHUB_TOKEN`.

### Discord announcement quirk

`softprops/action-gh-release` with the default `GITHUB_TOKEN` publishes the release, but GitHub's recursion guard suppresses the resulting `release: published` event, so `discord-release.yml` never fires from it. The release job works around this with a final step that dispatches the Discord workflow explicitly: `gh workflow run discord-release.yml --repo "$REPO" --ref main -f tag="v${TAG}"`. `workflow_dispatch` events fired by `GITHUB_TOKEN` are exempt from the recursion guard, so this triggers (the job declares `permissions: actions: write`). The `--repo` flag is required because the release job never runs `actions/checkout`, so `gh` has no `.git/` to infer the repository from.

## Shipping a release

Use the `/prepare-release` skill: it bumps the version, promotes the changelog, drafts highlights, tags, and pushes after approval.

The flow can optionally wait for `deploy-to-droplet.ps1 -WaitForEmptyRooms` before deploying, so an in-progress training session
isn't disrupted. That flag polls the live server's `/admin/status`; it **can't gate the very release that first ships that endpoint**
(or any future release-gating endpoint) — the live droplet 404s until the new build is deployed, and the poll treats a 404 as
"retry, not empty," so it never terminates. Expect this once per new gating endpoint: stop the wait (Ctrl-C) and deploy normally.
