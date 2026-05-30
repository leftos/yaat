# Installer and release pipeline

YAAT Client ships as a Velopack installer with auto-update, built and published by a tag-driven GitHub Actions workflow. This doc covers the packaging stack, the auto-update path, the CRC install-time configuration prompt, and the `release.yml` flow end-to-end.

## Versioning

The version lives in `Directory.Build.props` at the yaat repo root (`<Version>`). A push of a `v*` tag whose version matches that property triggers a release; the `/prepare-release` skill bumps the property, promotes the changelog heading, and pushes the tag.

## Packaging (Velopack)

[Velopack](https://github.com/velopack/velopack) produces the installers via the `vpk` CLI. Each platform job downloads the self-contained `dotnet publish` output, runs `vpk pack` with `--packId YaatClient`, and emits a per-platform installer plus auto-update metadata:

- **Windows** — `YaatClient-win-Setup.exe` and a `*-win-Portable.zip`.
- **Linux** — `YaatClient.AppImage` (self-contained; serves as both installer and portable).
- **macOS** — `YaatClient-osx-Setup.pkg` and a `*-osx-Portable.zip` (a `.icns` is generated from `icon.png` via `sips` + `iconutil`).

Portable archives bundle the single-file exe plus sibling native DLLs (libSkiaSharp, HarfBuzz, LM-Kit) and run without install or auto-update.

## Auto-update

`UpdateService` checks GitHub Releases via Velopack's `GithubSource` and surfaces an update notification bar in `MainWindow`. The auto-updater fetches the `RELEASES*`, `*.json`, and `*-full.nupkg` assets by exact filename, so `release.yml` copies those metadata files into the release **without renaming** — only the user-facing installer/portable filenames get the `-{version}-` suffix.

## CRC install-time configuration

`CrcConfigService` (a C# port of `Setup-CrcEnvironment.ps1`) registers the YAAT server in CRC's `DevEnvironments.json`. It runs during the Velopack install callback via `CrcInstallPrompt`, and skips when CRC is not installed or the entries already exist. It is also reachable later from the Tools > Configure CRC menu.

## Release workflow (`release.yml`)

Triggered on `push` of a `v*` tag. Jobs run in dependency order:

1. **version** — reads `<Version>` from `Directory.Build.props` and the short SHA.
2. **changelog** — extracts the `CHANGELOG.md` section matching the tag, splitting out a `### Highlights` subsection (authored by `/prepare-release`) from the changelog body.
3. **build** — `dotnet publish` of `src/Yaat.Client` for `win-x64`, `linux-x64`, `osx-arm64`.
4. **package-win / package-linux / package-macos** — `vpk pack` per platform.
5. **release** — assembles `release/`, builds the release body from highlights + changelog + a download table, and publishes via `softprops/action-gh-release` with the default `GITHUB_TOKEN`.

### Discord announcement quirk

`softprops/action-gh-release` with the default `GITHUB_TOKEN` publishes the release, but GitHub's recursion guard suppresses the resulting `release: published` event, so `discord-release.yml` never fires from it. The release job works around this with a final step that dispatches the Discord workflow explicitly: `gh workflow run discord-release.yml --repo "$REPO" --ref main -f tag="v${TAG}"`. `workflow_dispatch` events fired by `GITHUB_TOKEN` are exempt from the recursion guard, so this triggers (the job declares `permissions: actions: write`). The `--repo` flag is required because the release job never runs `actions/checkout`, so `gh` has no `.git/` to infer the repository from.

## Shipping a release

Use the `/prepare-release` skill: it bumps the version, promotes the changelog, drafts highlights, tags, and pushes after approval.
