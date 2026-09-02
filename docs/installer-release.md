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

The portable is always the `*-Portable.zip` that `vpk pack` emits next to the installer, never a renamed bare `dotnet publish` single-file exe. `PublishSingleFile` embeds only the managed assemblies; the natives stay as sibling files in the publish folder, so a lone exe crashes at startup with `DllNotFoundException: libSkiaSharp`. Velopack packs the whole publish folder, which is why its zip works. A true single-file portable would need `-p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true` (self-extracts to a temp folder on first run); without those flags, do not ship a bare exe.

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

The check runs automatically five seconds after startup and stays silent unless an update exists. **Help → Check for Updates…** runs the same check on demand and reports every outcome in a message box, because a user who asks the question expects an answer: it offers the download when an update is found, confirms the build is current when it isn't, points at the releases page when Velopack has no install to update (portable or run from source), and shows the failure reason otherwise. `UpdateService.CheckForUpdateAsync` returns an `UpdateCheckResult` carrying an `UpdateCheckOutcome` to keep those four cases distinguishable — they used to collapse into a single `null`.

## CRC install-time configuration

`CrcConfigService` (a C# port of `Setup-CrcEnvironment.ps1`) registers the YAAT server in CRC's `DevEnvironments.json`. It runs during the Velopack install callback via `CrcInstallPrompt`, and skips when CRC is not installed or the entries already exist. It is also reachable later from the Tools > Configure CRC menu.

## Release workflow (`release.yml`)

Triggered on `push` of a `v*` tag. Jobs run in dependency order:

1. **version** — reads `<Version>` from `Directory.Build.props` and the short SHA.
2. **changelog** — extracts the `CHANGELOG.md` section matching the tag, splitting out a `### Highlights` subsection (authored by `/prepare-release`) from the changelog body.
3. **build** — `dotnet publish` of `src/Yaat.Client` for `win-x64` and `linux-x64` (`release-macos.yml` publishes `osx-arm64` and `osx-x64` itself).
4. **package-win / package-linux / package-macos** — `vpk pack` per platform. `package-macos` (in `release-macos.yml`, once per architecture) additionally imports the Developer ID certificates and an App Store Connect API key into a temporary keychain, then signs + notarizes (skipped when the `MACOS_*` secrets are absent).
5. **release** — assembles `release/`, builds the release body from highlights + changelog + a download table, and creates the release via `softprops/action-gh-release` with the default `GITHUB_TOKEN`, always as a **draft**.

### Workflow authoring notes

- `windows-latest` `run:` steps default to **pwsh**, where `"$VPK_VERSION"` is an undefined PowerShell variable that expands to an empty string rather than the env var. Reference env vars as `$env:VPK_VERSION` (or `${{ env.VPK_VERSION }}`, or set `shell: bash`); the Linux/macOS steps use the bash `"$VPK_VERSION"` form. The failure mode is silent: `dotnet tool install -g vpk --version ""` installs the latest vpk and the only symptom is a `Velopack library version is lower than vpk version` warning in the pack log.
- The `vpk` pin (`VPK_VERSION`) lives in **two** workflow files, `release.yml` and `release-macos.yml`, and must stay equal to the `Velopack` package version in the client csproj. When bumping either, grep every workflow for the env name and check each consumer's shell.

### Draft-until-published

Every release is created as a draft — invisible to the releases page and to Velopack auto-update. CI cannot judge deployment scope: a release can be server-affecting purely via the yaat-server repo, which `release.yml` (running in the yaat repo) cannot see, so publishing is never decided in CI. The `/prepare-release` flow, which has cross-repo visibility, publishes the draft:

- **Server-affecting releases**: `deploy-to-droplet.ps1` publishes (`gh release edit --draft=false`) after verifying via `/api/version` that the deployed client commit **is** the tagged commit. The client release workflow finishes in ~5 minutes while the server image build + droplet deploy takes ~30; keeping the release a draft until then stops users picking up a client whose matching server isn't live yet.
- **Client-only releases**: the `/prepare-release` flow publishes once `release.yml` finishes building the installers — no deploy is involved.

`release-macos.yml` is unaffected: the authenticated `gh release view`/`upload` it uses sees drafts, so macOS assets land on the draft like any release.

**Missing macOS assets on a fresh draft are expected.** `release-macos.yml` is a separate workflow on the same `v*` tag: `release.yml` creates the release with the Windows and Linux assets, and the macOS workflow *appends* the notarized `osx-arm64`/`osx-x64` `.pkg`, Portable, and `RELEASES` assets 15–30 minutes later (Apple notarization dominates). Publishing before they land is the designed flow; do not hold the publish for them, and do not read their absence as a failed run.

A failed or skipped deploy leaves the release as a draft on purpose — publish manually with `gh release edit v{version} --repo leftos/yaat --draft=false` once a matching server is live.

### Discord announcement

`discord-release.yml` triggers on `release: published`. Both publish paths use the operator's *user* token (the deploy script or a manual `gh release edit --draft=false`), which is not subject to GitHub's workflow-recursion guard, so the event fires and the announcement posts. Do **not** also dispatch `discord-release.yml` by hand on top of a publish — that posts the announcement twice (which is exactly what happened on v0.11.6/v0.11.7).

## Shipping a release

Use the `/prepare-release` skill: it bumps the version, promotes the changelog, drafts highlights, tags, and pushes after approval.

### Moving an unpublished draft to a new commit

A change that must ride into an already-tagged but still-draft release can be retagged cleanly: fold its changelog bullets into the released section, commit, then

```bash
gh release delete v{version} --cleanup-tag
git tag -d v{version}
git tag v{version}
git push origin main
git push origin v{version}
```

`release.yml` re-runs on the new tag push and recreates the draft. For a server-affecting release the server image must also be **rebuilt**: it bakes `YAAT_CLIENT_COMMIT` at build time and the deploy only auto-publishes the draft when that commit is the tagged one.

### Troubleshooting: the tag push started no workflow

- **Signature:** `gh api "repos/leftos/yaat/actions/runs?head_sha=<sha>"` still reports `total_count: 0` well after the push. Two known causes: a GitHub Actions outage drops the push event outright (it is not queued, and recovery does not replay it), and GitHub occasionally coalesces a simultaneous branch push and tag push into one delivered `push` webhook, in which case only the `main`-triggered CI workflow fires. The second cause is why `/prepare-release` pushes `main` and the tag as two separate commands.
- **Recovery:** delete and re-push the tag to generate a fresh tag-push event; both `release.yml` and `release-macos.yml` then fire normally. There is no rerun path for a run that was never created.

  ```bash
  git push origin :refs/tags/v{version}
  git push origin v{version}
  ```

The flow can optionally wait for `deploy-to-droplet.ps1 -WaitForEmptyRooms` before deploying, so an in-progress training session
isn't disrupted. That flag polls the live server's `/admin/status`; it **can't gate the very release that first ships that endpoint**
(or any future release-gating endpoint) — the live droplet 404s until the new build is deployed, and the poll treats a 404 as
"retry, not empty," so it never terminates. Expect this once per new gating endpoint: stop the wait (Ctrl-C) and deploy normally.
