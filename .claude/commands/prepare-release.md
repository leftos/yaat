Prepare a new YAAT release. Walk through these steps interactively:

## Step 0: Verify release secrets

Confirm that the `LMKIT_LICENSE_KEY` secret is configured on the repo
(`gh secret list --repo leftos/yaat`). If absent, warn the user that the
released installer will run as LM-Kit Community Edition and ask whether
to proceed anyway or stop and configure the secret first
(`gh secret set LMKIT_LICENSE_KEY --repo leftos/yaat`). This is a soft
gate — the build succeeds either way, but users expecting a licensed
build should be told upfront.

## Step 0a: Consolidate recordings

Invoke the `consolidate-recordings` skill before any release work. It hashes
all `.zip` files under `tests/Yaat.Sim.Tests/TestData/`, collapses duplicates,
rewrites `.cs` references, and commits the result. If duplicates exist they
shouldn't ride along inside a release commit unnoticed — handle them now.

If the dry-run reports zero duplicates, the skill exits without committing
and we continue. If it produces a cleanup commit, note the SHA — it'll be
the parent of the release commit.

## Step 0b: Run the full cross-repo test suite

Run `pwsh tools/test-all.ps1` before the release commit goes out. It builds
and tests yaat + yaat-server in Release configuration; failures here would
ship to users. Tee to `.tmp/test-all-prerelease.log` so the user can review
without re-running.

If anything fails, stop and surface it. Do not proceed to the version-bump
commit on a red suite — fix forward (or abort the release), then re-run.

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

## Step 5: Audit the unreleased section, then pick highlights

### 5a. Audit `### Fixed` for same-release follow-ups

Before drafting highlights, scan the captured unreleased section for **`### Fixed` bullets that describe polish on features added in the same cycle**. These are `internal-fix` smell that `update-changelog` should have folded; they slip through when the changelog was drafted commit-by-commit.

For each Fixed bullet, ask: *was the underlying feature itself added in this release?* (Check `### Added` / `### Changed` in the same section, plus the substance of the bullet.) If yes:

- The user-observable behavior that the fix bullet describes belongs in the corresponding Added bullet, if it's not already implicit.
- The Fixed bullet itself should be dropped — readers haven't seen the broken version, so "now works" is not news.

Surface the candidates to the user as a focused diff (drop / fold / keep), apply the agreed cleanup to `CHANGELOG.md` before continuing. Do not auto-edit the unreleased section without confirmation.

### 5b. Select highlights

Read the (possibly-cleaned) unreleased section. Select **3-4 user-impactful items** to surface as highlights:

- Prefer items from `### Added` and `### Changed`. `### Fixed` items only if a fix is something users were waiting on (i.e. the broken behavior shipped in a prior release).
- Skip purely internal items even if they made it into the changelog (refactors, test infra, build plumbing).
- Tighten each chosen bullet to a short, scannable one-liner — drop sub-clauses about how it works internally. The full detail stays in the Changelog section below the Highlights.
- No marketing language (no "significantly", "robust", "comprehensive", etc.). State the change.
- **Write for users, not developers.** The audience is instructors and RPOs running YAAT, not contributors. Drop implementation jargon: framework/library names (Velopack, Avalonia, SignalR), class/method names, exception types, thread/dispatcher terminology, internal subsystem names. Lead with what the user sees and does. Keep user-vocabulary names for actual UI elements (e.g. the "Update Now" button, the command bar).
  - Bad: *"Velopack download-progress callback now marshalled to UI thread, fixing InvalidOperationException."*
  - Good: *"'Update Now' no longer crashes — auto-updates download and apply correctly."*

## Step 6: Audit user-facing documentation against the release commits

Before locking the release notes, walk the commits going into this release and confirm user-facing documentation actually covers what changed. Stale or missing docs hurt users more than missing changelog bullets — they steer instructors and RPOs wrong on real workflows.

### 6a. Build the commit list

Use the previous release tag from Step 2 as the lower bound. Capture both repos — user-visible changes can land on either side:

- yaat: `git log {prev-tag}..HEAD --oneline`
- yaat-server: yaat-server isn't release-tagged, so use the prev-tag's commit date as the cutoff:
  - `PREV_DATE=$(git log -1 --format=%cI {prev-tag})`
  - `git -C ../yaat-server log --since "$PREV_DATE" --oneline`
  - (If yaat-server is in a worktree, use the real path.)

Tee both lists to `.tmp/release-commits-{version}.log` so you can scan without re-running.

Skip commits that are pure refactor / test / CI / build / internal plumbing — they don't drive user-facing doc updates. The signal is "would an instructor reading the docs need to know this?", not "did anything change?".

### 6b. Map changes to docs

For each user-visible commit, identify which doc owns the topic and check whether its current text reflects the new behavior. Open the file and read the relevant section — don't trust filenames or memory.

| Topic | Doc(s) — all must stay synced when listed together |
|-------|-----|
| Install, update, first-run | `INSTALL.md`, `GETTING_STARTED.md`, `README.md` |
| Commands (added / renamed / aliased / behavior change / removed) | `COMMANDS.md` **and** `docs/command-cheatsheet.json` **and** `docs/command-cheatsheet.html` |
| Client feature usage (windows, panels, settings, workflows) | `USER_GUIDE.md` and screenshots under `docs/user-guide/` |
| Solo training mode behavior | `SOLO_TRAINING.md` |
| YAAT vs ATCTrainer parity / divergence | `docs/yaat-vs-atctrainer.md` |
| New / renamed / removed projects or top-level files | `docs/architecture.md` |
| Discord integration | `docs/discord-integration.md` |
| Scenario format / validation | `docs/scenario-validation.md` |

For each match, log a finding: file + section + what's stale, missing, or wrong. A correct-but-incomplete doc (e.g. command added to `COMMANDS.md` but cheatsheet JSON/HTML not updated) is still a gap.

### 6c. Surface findings and fix

Present the gap list to the user as a focused diff (per file: what's wrong, proposed update). For each:

- Default: update before shipping so the release is self-contained.
- Allowed: defer with a tracking note (file an issue or add a TODO bullet to the next cycle's changelog draft) if the doc change is large enough to warrant its own focused commit.
- Do not auto-edit docs without confirmation.

Apply the agreed updates in the yaat repo. If `USER_GUIDE.md` screenshots need regeneration, mention `tools/Yaat.GuideCapture` so the user can run it separately — do **not** invoke it from inside this flow.

Doc updates ride along in the release commit; the staging list in Step 8 picks them up.

## Step 7: Present draft release notes

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

## Step 8: Ship it (after user approval)

Once the user approves:

1. Update `<Version>` in `Directory.Build.props` to the new version.
2. **Promote the CHANGELOG.md heading** in place — `Edit` the heading line to the chosen format (version + optional date, matching sibling sections). Do not touch the body of the section yet.
3. **Insert the approved highlights** into CHANGELOG.md as a `### Highlights` subsection at the top of the version's section, immediately after the heading and before the first existing subsection (typically `### Added` or `### Fixed`). Use the bullets verbatim as approved in Step 7 — these are what the GitHub release will surface.
4. Update `docs/architecture.md` if any new files were added (and it wasn't already covered in Step 6).
5. Stage these explicit files only (no `git add -A`): `Directory.Build.props`, `CHANGELOG.md`, `docs/architecture.md` if changed, **and every user-facing doc updated in Step 6** (e.g. `COMMANDS.md`, `docs/command-cheatsheet.json`, `docs/command-cheatsheet.html`, `USER_GUIDE.md`, `INSTALL.md`, etc.). List them explicitly so the user can audit before commit.
6. Commit: `release: v{version}`.
7. Create tag: `git tag v{version}`.
8. **Ask the user whether to also deploy to the droplet** at the same time as pushing the tag. Use `AskUserQuestion` (single-select: deploy alongside push / push only / abort). The droplet runs yaat-server in production; deploying alongside the tag push keeps client and server cycles aligned. Capture the answer before proceeding to step 9 — if "abort" is picked, stop here and let the user resume manually.
9. Push both repos so any cross-repo work made during this cycle ships together:
   - First push yaat-server's pending commits (no tag — yaat-server isn't release-tagged):
     `git -C ../yaat-server push origin main`
     Run even if you think there's nothing pending — it's idempotent. If a worktree, use the real yaat-server path. If the push is rejected because the CI submodule-bump landed on remote, rebase (`git -C ../yaat-server pull --rebase origin main`) and re-push.
   - Then push yaat's release commit and tag:
     `git push origin main --tags`

   Order matters: yaat-server first means yaat-server's own work is live before yaat's release CI fires. Pushing yaat second triggers yaat-server's `submodule-updated` CI dispatch (which bumps `extern/yaat` on yaat-server), so yaat-server's main already has the cycle's work when the bump arrives.
10. **If the user approved a droplet deploy in step 8**: run `pwsh deploy-to-droplet.ps1 -NoLogs` and tee output to `.tmp/deploy-droplet.log`. Always pass `-NoLogs` — without it the script tails server logs indefinitely and blocks the agent until timeout. Wait for the script to finish (the `Deployment complete!` banner) before declaring the release done.

The tag push triggers the `release.yml` GitHub Actions workflow. The workflow extracts the matching section from `CHANGELOG.md` (using the tag name), splits out the `### Highlights` subsection for the GitHub Release's "Highlights" block, and uses the rest of the section as the "Changelog" block. The highlights you and the user agreed on in Step 7 are exactly what ships — no AI rewriting at release time.
