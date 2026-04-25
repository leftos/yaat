Prepare a new YAAT release. Walk through these steps interactively:

## Step 0: Verify release secrets

Confirm that the `LMKIT_LICENSE_KEY` secret is configured on the repo
(`gh secret list --repo leftos/yaat`). If absent, warn the user that the
released installer will run as LM-Kit Community Edition and ask whether
to proceed anyway or stop and configure the secret first
(`gh secret set LMKIT_LICENSE_KEY --repo leftos/yaat`). This is a soft
gate — the build succeeds either way, but users expecting a licensed
build should be told upfront.

## Step 1: Read current version
Read `Directory.Build.props` at the repo root to get the current `<Version>` value.

## Step 2: Find previous release
Run `git tag --sort=-v:refname | head -5` to find existing release tags.

## Step 3: Ask for new version
Suggest the next version based on the current one (e.g., `0.1.0-alpha` → `0.2.0-alpha`). Ask the user what the new version should be.

## Step 4: Locate the unreleased CHANGELOG section

Read `CHANGELOG.md`. Find the topmost version heading (a line starting with `## `, ignoring the file title `# Changelog`). Cross-check against `git tag --sort=-creatordate | head -10`:

- **Topmost heading matches a released tag** → there is no unreleased section. The CHANGELOG is stale relative to HEAD. **Offer to run the `update-changelog` skill inline now**, then re-read the file. Do not proceed past this step until the topmost heading is an unreleased section. Do not fall back to scraping git log.
- **Topmost heading is `[Unreleased]` or an untagged version** (e.g. `## 0.2.0-alpha` with no matching `v0.2.0-alpha` tag) → that's the unreleased section. Capture its full body (everything from the heading up to but not including the next `## ` heading). This is the source of truth for the release notes.

## Step 5: Pick highlights from the CHANGELOG section

Read the unreleased section captured in Step 4. Select **3-4 user-impactful items** to surface as highlights:

- Prefer items from `### Added` and `### Changed`. `### Fixed` items only if a fix is something users were waiting on.
- Skip purely internal items even if they made it into the changelog (refactors, test infra, build plumbing).
- Tighten each chosen bullet to a short, scannable one-liner — drop sub-clauses about how it works internally. The full detail stays in the Changelog section below the Highlights.
- No marketing language (no "significantly", "robust", "comprehensive", etc.). State the change.
- **Write for users, not developers.** The audience is instructors and RPOs running YAAT, not contributors. Drop implementation jargon: framework/library names (Velopack, Avalonia, SignalR), class/method names, exception types, thread/dispatcher terminology, internal subsystem names. Lead with what the user sees and does. Keep user-vocabulary names for actual UI elements (e.g. the "Update Now" button, the command bar).
  - Bad: *"Velopack download-progress callback now marshalled to UI thread, fixing InvalidOperationException."*
  - Good: *"'Update Now' no longer crashes — auto-updates download and apply correctly."*

## Step 6: Present draft release notes

Show the user the draft — highlights you derived **plus the full unreleased CHANGELOG section verbatim**:

```
## Highlights
- [3-4 derived bullets]

## Changelog
[full body of the unreleased section from CHANGELOG.md, sub-headings included]
```

Also show the **heading promotion** that will happen on commit:

```
CHANGELOG.md heading change:
  before: ## 0.1.1-alpha
  after:  ## 0.1.1-alpha - 2026-04-24
```

Match the existing file style by inspecting an already-released sibling section (e.g. `## 0.1.0-alpha`):
- If sibling sections have no `v` prefix, don't add one. If they do, keep it.
- If sibling sections include a date (`## 0.1.0-alpha - 2025-12-30`), include one. If not, just leave the version.
- If the current heading is `## [Unreleased]`, replace it entirely with the new version heading; do **not** keep an `[Unreleased]` placeholder in this commit (it'll come back next cycle when the user starts logging again).

Ask the user to review. Apply any requested edits to the highlights or the unreleased section in CHANGELOG.md before continuing.

## Step 7: Ship it (after user approval)

Once the user approves:

1. Update `<Version>` in `Directory.Build.props` to the new version.
2. **Promote the CHANGELOG.md heading** in place — `Edit` the heading line to the chosen format (version + optional date, matching sibling sections). Do not touch the body of the section; only the heading line.
3. Update `docs/architecture.md` if any new files were added.
4. Stage these explicit files only (no `git add -A`): `Directory.Build.props`, `CHANGELOG.md`, and `docs/architecture.md` if changed.
5. Commit: `release: v{version}`.
6. Create tag: `git tag v{version}`.
7. Push commit and tag: `git push origin main --tags`.

This triggers the `release.yml` GitHub Actions workflow. The workflow extracts the matching section from `CHANGELOG.md` (using the tag name) and AI-generates highlights from that curated text — so what you reviewed in Step 6 is what ships, modulo the AI's tighter highlight phrasing.
