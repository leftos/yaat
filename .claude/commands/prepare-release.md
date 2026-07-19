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

Doc updates ride along in the release commit; the staging list in Step 9 picks them up.

## Step 6d: Scan for open issues this release fixes

GitHub auto-closes issues whose commits carry `Closes #N` (yaat) or
`Closes https://github.com/leftos/yaat/issues/N` (yaat-server). That only
catches issues someone remembered to cite. Feature work driven by a Discord
thread routinely ships without ever naming the issue, leaving a fixed request
open — the reporter never learns it landed.

Match open issues against what actually shipped, not against commit metadata:

1. `gh issue list --repo leftos/yaat --state open --limit 60 --json number,title,createdAt`
2. For each open issue, ask whether any bullet in this release's changelog
   section satisfies it. Recently-created issues are the likeliest hits — a
   request filed days before the release is often exactly what the cycle built.
3. **Read the issue body before proposing closure.** Titles mislead. An issue
   titled "show scratchpad in radar view" may already be half-satisfied, with
   the real ask buried in a follow-up comment ("manual primaries already show;
   automatic ones don't"). Confirm the shipped behavior covers the *actual*
   ask, including any narrowing in the thread.
4. Partial fixes are not fixes. If a cycle pinned a dependency but the issue
   asked to pin *and* later drop the pin, it stays open — say so explicitly.

Present matches as a table (issue → ask → implementing commit) and ask before
closing. These are public issues with a watching reporter, so closing posts
outward; never close without confirmation. When closing, comment with the
version, the implementing commit SHA, and a short description of the shipped
behavior in user terms — the reporter should be able to tell whether their
case is handled without reading the diff.

## Step 7: Determine deployment scope (client-only vs server-affecting)

The droplet deploy in Step 9 rebuilds and restarts `yaat-server`, costing ~10 minutes of downtime for anyone in a live training session. That downtime is only *necessary* when this release actually changes something the server runs. If every change since the previous release is confined to the desktop client, the running server already matches the release and the deploy can be skipped. This step decides which case we're in and feeds the recommendation into Step 9's push/deploy prompt.

### 7a. What forces a server redeploy

The droplet builds its image from the yaat-server repo **plus a WASM + shared-library closure pulled from the yaat repo** (see `src/Yaat.Server/Dockerfile` in yaat-server — its `COPY` lines are the source of truth for that closure; re-derive from them if the closure has grown). A change since the previous release requires a redeploy if it touches any of:

**yaat repo — shared with / deployed by the server:**
- `src/Yaat.Sim/**` — shared simulation library; the server links it and its DTOs define the SignalR/CRC wire contract
- `src/Yaat.Client.Strips/**` — in the WASM closure (vStrips **and** vTDLS reference it)
- `src/Yaat.Client.Tdls/**` — in the WASM closure (vTDLS references it)
- `tools/Yaat.VStrips.Web/**` — the vStrips browser app the server hosts at `/vstrips/`
- `tools/Yaat.VTdls.Web/**` — the vTDLS browser app the server hosts at `/vtdls/`

**yaat-server repo — the server itself:**
- `src/Yaat.Server/**`, `Directory.Build.props`, `src/Yaat.Server/Dockerfile`, `docker-compose.yml` — anything that changes the built or served artifact

**Client-only — does NOT force a redeploy:**
- `src/Yaat.Client/**`, `src/Yaat.Client.Core/**` (the desktop app; `Yaat.Client.Core` is deliberately excluded from the WASM closure)
- client-only tools (`tools/Yaat.LayoutInspector/`, `tools/Yaat.SpeechSandbox/`, `tools/Yaat.GuideCapture/`, `tools/Yaat.CifpInspector/`, …), docs, `*.md`, `CHANGELOG.md`, `.github/`, tests
- this release's own `<Version>` bump in the **yaat** `Directory.Build.props` (that's the client installer version; the server image never copies it)

### 7b. Compute the changed-file set across both repos

Reuse the previous release tag from Step 2 as the lower bound (the version-bump and release commits don't exist yet, so `{prev-tag}..HEAD` is exactly this cycle's work).

yaat — list every changed file, then just the server-trigger subset:
```
git diff --name-only {prev-tag}..HEAD
git diff --name-only {prev-tag}..HEAD -- src/Yaat.Sim src/Yaat.Client.Strips src/Yaat.Client.Tdls tools/Yaat.VStrips.Web tools/Yaat.VTdls.Web
```

yaat-server — it isn't release-tagged, so anchor on the commit that was HEAD at the prev-tag's timestamp and ignore the `extern/yaat` submodule pointer (CI bumps it on every client release, and the droplet re-resolves yaat via `--remote` at deploy time, so a bare submodule bump ships nothing new):
```
PREV_DATE=$(git log -1 --format=%cI {prev-tag})
SERVER_BASE=$(git -C ../yaat-server rev-list -1 --before="$PREV_DATE" HEAD)
git -C ../yaat-server diff --name-only "$SERVER_BASE" HEAD -- . ':(exclude)extern/yaat'
```
(If yaat-server is in a worktree, use its real path.) Tee the combined output to `.tmp/deploy-scope-{version}.log`.

### 7c. Verdict

- **CLIENT_ONLY** — both server-trigger queries are empty, every file in the full yaat list maps to a client-only path above, and the yaat-server query is empty (only ever the excluded `extern/yaat` bump). The running server already matches this release.
- **SERVER_AFFECTING** — anything else, **including any changed path you don't positively recognize as client-only**. Bias toward SERVER_AFFECTING: a wrong "skip" leaves a stale server live, while a wrong "deploy" only costs downtime.

Report the verdict with its evidence — the server-relevant paths that triggered it, or "none — all changes are client-only". Note *why* skipping is safe for CLIENT_ONLY: with `Yaat.Sim` untouched the SignalR/CRC wire contract is unchanged, so the already-running server stays compatible with the newly-released client, and the client installer itself is built by `release.yml`, not by the droplet.

Carry the verdict into Step 9 — it sets the default push/deploy option.

## Step 8: Present draft release notes

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

Also restate the **Step 7 deployment-scope verdict** (CLIENT_ONLY or SERVER_AFFECTING, with the triggering paths) so the user knows upfront whether a server deploy is coming.

Ask the user to review. Apply any requested edits to the highlights or the unreleased section in CHANGELOG.md before continuing.

## Step 9: Ship it (after user approval)

Once the user approves:

1. Update `<Version>` in `Directory.Build.props` to the new version.
2. **Promote the CHANGELOG.md heading** in place — `Edit` the heading line to the chosen format (version + optional date, matching sibling sections). Do not touch the body of the section yet.
3. **Insert the approved highlights** into CHANGELOG.md as a `### Highlights` subsection at the top of the version's section, immediately after the heading and before the first existing subsection (typically `### Added` or `### Fixed`). Use the bullets verbatim as approved in Step 8 — these are what the GitHub release will surface.
4. Update `docs/architecture.md` if any new files were added (and it wasn't already covered in Step 6).
5. Stage these explicit files only (no `git add -A`): `Directory.Build.props`, `CHANGELOG.md`, `docs/architecture.md` if changed, **and every user-facing doc updated in Step 6** (e.g. `COMMANDS.md`, `docs/command-cheatsheet.json`, `docs/command-cheatsheet.html`, `USER_GUIDE.md`, `INSTALL.md`, etc.). List them explicitly so the user can audit before commit.
6. Commit: `release: v{version}`.
7. Create tag: `git tag v{version}`.
8. **Check the target server for active rooms, then ask how to push and deploy.**

   First — always, regardless of the Step 7 verdict — query the live server for active training rooms so the decision is never made blind over an in-progress session. Run `pwsh deploy-to-droplet.ps1 -StatusOnly` and report the result: the active room count, and for each room its id, members, scenario, and aircraft count — or "no active rooms". The script reads `ADMIN_PASSWORD` from yaat `.env` itself — **do not read the secret yourself**. If the query fails (server unreachable, password unset — exit code 2), say so and continue to the prompt, treating occupancy as unknown/possibly-occupied. Surface the room status in the same message as the deploy question so the user weighs it when choosing.

   Then use `AskUserQuestion` (single-select). Order the options by the Step 7 verdict so the recommended choice is first.

   **If Step 7 was CLIENT_ONLY** — lead with skipping the deploy:
   - **Push only — skip server deploy (Recommended)** — no shared-library, server, or web-UI (vStrips/vTDLS) changes this cycle, so the running server already matches the release. Pushing still ships the new client via `release.yml`; skipping the droplet deploy avoids ~10 minutes of downtime for anyone mid-session.
   - **Deploy now alongside push** — force a redeploy anyway (e.g. to pull a new AIRAC cycle or restart the server); active sessions are checkpointed and restored.
   - **Wait for rooms to clear, then push + deploy** — as above, but hold *both* the push and the deploy until the live server reports zero rooms.
   - **Abort** — stop here; the user resumes manually.

   **If Step 7 was SERVER_AFFECTING** — lead with deploying (this release changes what the server runs):
   - **Deploy now alongside push** — push immediately, then deploy to the droplet (active sessions are checkpointed and restored across the deploy).
   - **Wait for rooms to clear, then push + deploy** — hold *both* the push and the deploy until the live server reports zero rooms, so no in-progress training session is disrupted at all.
   - **Push only** — push the release, no droplet deploy (leaves the live server on the previous build — only pick this if you'll deploy separately).
   - **Abort** — stop here; the user resumes manually.

   The droplet runs yaat-server in production; when the release changes server-side code, deploying alongside the tag push keeps client and server cycles aligned. Capture the answer before proceeding. If "abort" is picked, stop.
9. **If the user chose "wait for rooms to clear":** before pushing, run `pwsh deploy-to-droplet.ps1 -WaitForEmptyRooms` **in the background** (it polls `GET /admin/status` every 60s and blocks until the server has zero rooms, printing the active rooms each check — it can run for many minutes, so backgrounding keeps the agent responsive; you're re-invoked when it exits). The script reads `ADMIN_PASSWORD` from yaat `.env` itself — do not read the secret yourself. When it exits cleanly (rooms cleared), continue to step 10. For the other choices, skip this step. *(The real deploy in step 11 still calls `prepare-restart` as a safety net for any room that appears between "cleared" and "deployed".)*
10. **Unless "abort" was chosen:** push both repos so any cross-repo work made during this cycle ships together:
    - First push yaat-server's pending commits (no tag — yaat-server isn't release-tagged):
      `git -C ../yaat-server push origin main`
      Run even if you think there's nothing pending — it's idempotent. If a worktree, use the real yaat-server path. If the push is rejected because the CI submodule-bump landed on remote, rebase (`git -C ../yaat-server pull --rebase origin main`) and re-push.
    - Then push yaat's release commit and the release tag **by name**:
      `git push origin main v{version}`

      **Never use `--tags` here.** This repo has two tag namespaces with separate
      release workflows — `v*` (the client, `release.yml`) and `crc-config-v*`
      (the standalone tool, `yaat-crc-config.yml`). `--tags` pushes every local
      tag, so any stale unpushed tag fires its workflow and publishes a release
      built from whatever old commit it points at. Because GitHub ranks "Latest"
      by publish time, that stale release also steals the Latest badge and the
      `/releases/latest` API from the real client release.

      Before pushing, confirm no unexpected local tags are pending:
      `comm -23 <(git tag | sort) <(git ls-remote --tags origin | sed 's/.*refs.tags.//;s/\^{}//' | sort -u)`
      This should print nothing but the release tag you just created. If it lists
      others, delete or investigate them before pushing — do not push them along.

    Order matters: yaat-server first means yaat-server's own work is live before yaat's release CI fires. Pushing yaat second triggers yaat-server's `submodule-updated` CI dispatch (which bumps `extern/yaat` on yaat-server), so yaat-server's main already has the cycle's work when the bump arrives.
11. **If the user chose a deploy option ("deploy now" or "wait for rooms to clear"):** run `pwsh deploy-to-droplet.ps1 -NoLogs` and tee output to `.tmp/deploy-droplet.log`. Always pass `-NoLogs` — without it the script tails server logs indefinitely and blocks the agent until timeout. The script calls `POST /admin/prepare-restart` first (needs `ADMIN_PASSWORD` in yaat `.env`, matching the droplet) so active training sessions survive the deploy. Use `-SkipSessionSave` only for emergency deploys. Wait for the `Deployment complete!` banner before declaring the release done.

The tag push triggers the `release.yml` GitHub Actions workflow. The workflow extracts the matching section from `CHANGELOG.md` (using the tag name), splits out the `### Highlights` subsection for the GitHub Release's "Highlights" block, and uses the rest of the section as the "Changelog" block. The highlights you and the user agreed on in Step 8 are exactly what ships — no AI rewriting at release time.
