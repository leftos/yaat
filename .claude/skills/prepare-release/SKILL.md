---
name: prepare-release
description: Prepare a new YAAT release — version bump, changelog cut, tag, cross-repo push (invoke by hand as /prepare-release).
disable-model-invocation: true
---

Prepare a new YAAT release. Walk through these phases interactively. Any two steps that do not depend on each other run in parallel; the gate's slot pools, not the order of steps, limit the machine load.

Everything inside a phase is independent and is launched together: long commands in the background first, then the reading audits as subagents (each briefed with its step's text plus `$YAAT`, `$SERVER`, `PREV_TAG` and `PREV_DATE` from Phase 1, each returning findings and editing nothing), then the inline checks and the questions to the user. A phase starts when what it needs from the phase before is done; each phase heading says what that is.

**Ground rules for every phase:**

- **Every long command runs through the gate wrapper, never piped into `tail`, `grep` or `tee`.** A pipeline reports its last stage's status, so a failed build or deploy reads as green — that is exactly how a release once went out over a `main` that did not compile, with four "Passed!" lines above it. `tools/gate.ps1` propagates the command's own status and also fails when the log contains `Build FAILED` or `error CS`.

  If the wrapper is unavailable, read the log and confirm the command's own success line before continuing; never treat a reported exit status as the verdict.
- **Address yaat-server only through `$SERVER`** (step 1.1), and never `git -C` a path you have not verified exists.
- **Commits from parallel work go in one at a time.** Two commits racing in one checkout collide on git's index lock, and a partial commit's stash sweeps up the other's files.

## Phase 1: Ground the run

Sequential and short, and finished before any work starts: the secret check can stop the release, and every later phase reads what the other steps produce.

### 1.1 Resolve the sibling repo once, and assert it exists

Every later step addresses yaat-server. **`git -C <path>` does not fail when `<path>` is not a repository — it walks up from the current working directory to the nearest `.git` and operates there.**

From a worktree session (`../yaat.wt/<branch>/` beside the main checkout) `../yaat-server` does not exist, so a relative `git -C ../yaat-server diff` silently computes its verdict from *yaat*, and a relative `git -C ../yaat-server push origin main` pushes yaat a second time while reporting yaat-server pushed.

The Bash tool's cwd also persists across calls, so an earlier stray `cd` changes what `..` means with no visible error.

Resolve it once and assert, then use `$SERVER` in every later command:

```bash
YAAT="$(cd "$(git rev-parse --path-format=absolute --git-common-dir)/.." && pwd)"   # main checkout, also from a worktree
SERVER="$YAAT/../yaat-server"      # sibling of the main checkout, or the paired worktree sibling when one exists
[ -d "$SERVER/.git" ] || [ -f "$SERVER/.git" ] || { echo "no repo at $SERVER"; exit 1; }
git -C "$SERVER" status -sb | head -1
```

If yaat-server's release work sits in a worktree, point `$SERVER` at that worktree's real path.

### 1.2 Verify the release secret

Confirm that the `LMKIT_LICENSE_KEY` secret is configured on the repo (`gh secret list --repo leftos/yaat`). If absent, warn the user that the released installer will run as LM-Kit Community Edition and ask whether to proceed anyway or stop and configure the secret first (`gh secret set LMKIT_LICENSE_KEY --repo leftos/yaat`). This is a soft gate — the build succeeds either way, but users expecting a licensed build should be told upfront.

### 1.3 Read the current version and the previous release

Read `Directory.Build.props` at the repo root to get the current `<Version>` value. Run `git tag --sort=-v:refname | head -5` to find existing release tags; the latest is `PREV_TAG`, the lower bound of this cycle's work in both repos. yaat-server isn't release-tagged, so its cycle is anchored on the tag's commit date:

```bash
PREV_DATE=$(git log -1 --format=%cI "$PREV_TAG")
```

### 1.4 Ask for the new version

Suggest the next version based on the current one. Ask the user what the new version should be, unless the invocation already names the bump.

**The user's bump vocabulary.** The words map to the three numbers of `major.minor.revision`:

| The user says | Bump | Example from `0.12.29-beta` |
|---|---|---|
| "minor rev bump", "minor revision bump", "rev bump" | third number | `0.12.30-beta` |
| "minor version bump" | second number, third resets to 0 | `0.13.0-beta` |
| "major version bump" | first number, the rest reset to 0 | `1.0.0-beta` |

"Minor rev" is *not* semver's "minor": a release of ordinary fixes is a revision bump. A 0.13 or 1.0 is only ever asked for by name.

### 1.5 Locate the unreleased CHANGELOG section

Read `CHANGELOG.md`. Find the topmost version heading (a line starting with `## `, ignoring the file title `# Changelog`). Cross-check against `git tag --sort=-creatordate | head -10`:

- **Topmost heading matches a released tag** → there is no unreleased section. The CHANGELOG is stale relative to HEAD. **Offer to write it inline now by `/changelog-and-commit`'s rules** (Step 3 opens `## Unreleased` and consolidates it as the difference from the last release; Step 4 words the bullets), then re-read the file. Do not proceed past this step until the topmost heading is an unreleased section. Do not fall back to scraping git log.
- **Topmost heading is `[Unreleased]` or an untagged version** (e.g. `## 0.2.0-alpha` with no matching `v0.2.0-alpha` tag) → that's the unreleased section. Capture its full body (everything from the heading up to but not including the next `## ` heading). This is the source of truth for the release notes.

## Phase 2: Launch every independent check

Needs Phase 1. Launch 2.1 to 2.4 in the background, then 2.5 to 2.11 as subagents, then work 2.12 inline while they run. Phase 3 acts on what they return.

### 2.1 Run the full cross-repo test suite

The suite builds and tests yaat + yaat-server in Release configuration; failures here would ship to users. Run it in the background:

```bash
pwsh tools/gate.ps1 -Log .tmp/test-all-prerelease.log -TimeoutSeconds 360 -Slot heavy -- pwsh tools/test-all.ps1
```

If anything fails, stop and surface it. Do not proceed to the version-bump commit (Phase 5) on a red suite — fix forward (or abort the release), then re-run.

The verdict must cover the tree that ships: a commit that changes code or tests after this run started (2.3's consolidation, a 3.2 landing) means a re-run after the last such commit.

### 2.2 Check the precompute cache, and recompute it when stale

Run `pwsh tools/gate.ps1 -Log .tmp/precompute-check.log -TimeoutSeconds 300 -Slot heavy -- dotnet run -c Release --project tools/Yaat.PrecomputeCache -- --check --online`. Every `::warning::` line names an airport whose push-target entry is missing or stale (a new AIRAC NavData serial, a changed ground map, sidecar or planner source); CI only warns about them, so this is where they are fixed.

When any are reported, recompute them in the background, beside 2.1's suite rather than after it: `pwsh tools/gate.ps1 -Log .tmp/precompute.log -TimeoutSeconds 3600 -Slot heavy -- dotnet run -c Release --project tools/Yaat.PrecomputeCache`. Step 3.4 commits the result.

An `::error::` line (an unreadable entry) or a non-zero exit stops the release until it is fixed. The tool and its flags are in `src/Yaat.Sim/Data/PrecomputeCache/README.md`.

A **hashed source** is any `PrecomputeLayoutSource` / `PrecomputePushTargetSource` in `src/Yaat.Sim/Yaat.Sim.csproj`, such as `GeoJsonParser.cs`. A change to one stales every entry again, so a recompute runs after the last such change lands (step 3.2).

### 2.3 Consolidate recordings

Invoke the `consolidate-recordings` skill (it runs in a forked subagent). It hashes all `.zip` files under `tests/Yaat.Sim.Tests/TestData/`, collapses duplicates, rewrites `.cs` references, and commits the result. If duplicates exist they shouldn't ride along inside a release commit unnoticed — handle them now.

If the dry-run reports zero duplicates, the skill exits without committing and we continue. If it produces a cleanup commit, note the SHA — it lands before the release commit.

### 2.4 Refresh the screenshots

Captures render the client as the code now stands, so a change this cycle that moved a picture shows up here. Run in the background, one gate call after another (the runs share one build output):

```bash
pwsh tools/gate.ps1 -Log .tmp/guide-capture-all.log -TimeoutSeconds 1800 -Slot heavy -- dotnet run -c Release --project tools/Yaat.GuideCapture
for s in $(rg -o --no-filename -r '$1' 'img/(whats-new-[a-z0-9-]+)\.png' docs/releases/whats-new-next.md | sort -u); do
  pwsh tools/gate.ps1 -Log ".tmp/guide-capture-$s.log" -TimeoutSeconds 600 -Slot heavy -- dotnet run -c Release --project tools/Yaat.GuideCapture -- --scene "$s" || echo "capture failed: $s"
done
```

The plain run rewrites every user-guide PNG under `docs/user-guide/img/`, byte-identical where the picture has not moved, and skips every `whats-new-*` scene. The loop captures the showcase scenes `whats-new-next.md` uses, by name, into `docs/releases/img/`. Scene mechanics: `docs/guide-capture.md` and the `guide-capture` skill. Step 3.3 reviews what changed.

### 2.5 Find open user-reported bugs (subagent)

A release never ships with a known user-reported bug: an open issue reporting a defect that a person other than the owner hit — a Discord thread, a named reporter, an attached bug bundle or recording — whoever filed it, an agent included. A defect only an agent, a nightly review or an audit found does not count.

The Discord bot's `/create-issue` labels its issues `bug` (`/create-feature-request` uses `enhancement`), so start from `gh issue list --repo leftos/yaat --state open --label bug --limit 200`; issues filed any other way (agent-filed reports from Discord, Linear-synced ones) carry no label, so also read the unlabelled open issues' bodies (`gh issue list --repo leftos/yaat --state open --limit 400 --json number,title,labels,body`) to judge them.

For each user-reported bug, its Linear issue must be Landed in the open release, or the user must have agreed in this session or a comment on the issue to defer it. Any other one stops the release: list them for step 3.1, which asks the user, one question per bug, whether to fix it first or defer it; a deferral is written as a comment on the Linear issue.

### 2.6 Decide every version-bearing file (subagent)

More than one file carries a version number, and they do **not** all move together. Walk this table every release and state the verdict for each before continuing — a missed bump here ships a server that mis-gates clients, which is worse than a stale changelog because it locks users out.

| File | Value | When to bump |
|---|---|---|
| `Directory.Build.props` (**yaat**) | `<Version>` | **Every release.** This is the client installer version and the tag. Never skip. |
| `src/Yaat.Server/appsettings.json` (**yaat-server**) | `Yaat:ClientVersions:Recommended` | **Every release.** It's the "please update" pointer, so leaving it behind means nobody is ever nudged. Set it to the version being released. |
| `src/Yaat.Server/appsettings.json` (**yaat-server**) | `Yaat:ClientVersions:Minimum` | **Only when this release breaks older clients.** See the test below. Otherwise leave it exactly as-is. |
| `src/Yaat.Server/YaatOptions.cs` (**yaat-server**) | `ClientVersionOptions` defaults | Keep in sync with the two appsettings values above — the defaults apply to any deployment that doesn't override them. |

#### Does this release need a `Minimum` bump?

Bump `Minimum` to the released version **only if an older client talking to the new server would misbehave**, not merely miss a feature. Concretely, yes if this cycle did any of:

- removed, renamed, or retyped a field on a DTO the client deserializes (`RoomStateDto`, `AircraftDto`, `TrainingRoomInfoDto`, the `*ChangedDto` broadcast payloads, …)
- changed a hub method's name, parameter list, or return type
- changed the CRC wire format or the `/hubs/training` protocol

Adding a **new optional** field is not breaking — an old client ignores it. Server-only changes, client-only changes, sim/physics changes, and doc changes are never breaking.

State the verdict explicitly, e.g. *"`Minimum` stays at 0.9.18-beta — this cycle only added fields"* or *"`Minimum` → 0.9.21-beta: `TrainingRoomInfoDto.MemberInitials` was replaced by `Members`"*.

**Caveat to tell the user when you do bump `Minimum`:** the gate is enforced by the *client*, so it only protects users running a build that already has the check. Raising `Minimum` cannot rescue anyone on a version older than the release that introduced the gate — those clients still fail the old way. The bump is still correct; it just starts helping one release later.

### 2.7 Determine deployment scope: client-only vs server-affecting (subagent)

The droplet deploy (step 7.5) rebuilds and restarts `yaat-server`, costing ~10 minutes of downtime for anyone in a live training session. That downtime is only *necessary* when this release actually changes something the server runs. If every change since the previous release is confined to the desktop client, the running server already matches the release and the deploy can be skipped.

This step decides which case we're in; the verdict is restated in the draft (4.1) and sets the default push option (6.1).

#### What forces a server redeploy

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

#### Compute the changed-file set across both repos

The version-bump and release commits don't exist yet, so `$PREV_TAG..HEAD` is exactly this cycle's work.

yaat — list every changed file, then just the server-trigger subset:
```
git diff --name-only $PREV_TAG..HEAD
git diff --name-only $PREV_TAG..HEAD -- src/Yaat.Sim src/Yaat.Client.Strips src/Yaat.Client.Tdls tools/Yaat.VStrips.Web tools/Yaat.VTdls.Web
```

yaat-server — anchor on the commit that was HEAD at the prev-tag's timestamp and ignore the `extern/yaat` submodule pointer (nothing keeps it current, and the droplet re-resolves yaat via `--remote` at deploy time, so a pointer change ships nothing new):
```
SERVER_BASE=$(git -C "$SERVER" rev-list -1 --before="$PREV_DATE" HEAD)
git -C "$SERVER" diff --name-only "$SERVER_BASE" HEAD -- . ':(exclude)extern/yaat'
```

Redirect the combined output to `.tmp/deploy-scope-since-$PREV_TAG.log`.

#### Verdict

- **CLIENT_ONLY** — both server-trigger queries are empty, every file in the full yaat list maps to a client-only path above, and the yaat-server query is empty (only ever the excluded `extern/yaat` bump). The running server already matches this release.
- **SERVER_AFFECTING** — anything else, **including any changed path you don't positively recognize as client-only**. Bias toward SERVER_AFFECTING: a wrong "skip" leaves a stale server live, while a wrong "deploy" only costs downtime.

Report the verdict with its evidence — the server-relevant paths that triggered it, or "none — all changes are client-only". Note *why* skipping is safe for CLIENT_ONLY: with `Yaat.Sim` untouched the SignalR/CRC wire contract is unchanged, so the already-running server stays compatible with the newly-released client, and the client installer itself is built by `release.yml`, not by the droplet.

### 2.8 Audit user-facing documentation against the release commits (subagent)

Before the release notes are locked, walk the commits going into this release and confirm user-facing documentation actually covers what changed. Stale or missing docs hurt users more than missing changelog bullets — they steer instructors and RPOs wrong on real workflows.

**Build the commit list.** Capture both repos — user-visible changes can land on either side:

- yaat: `git log $PREV_TAG..HEAD --oneline`
- yaat-server: `git -C "$SERVER" log --since "$PREV_DATE" --oneline`

Redirect both lists to `.tmp/release-commits-since-$PREV_TAG.log` so you can scan without re-running.

Skip commits that are pure refactor / test / CI / build / internal plumbing — they don't drive user-facing doc updates. The signal is "would an instructor reading the docs need to know this?", not "did anything change?".

**Map changes to docs.** For each user-visible commit, identify which doc owns the topic and check whether its current text reflects the new behavior. Open the file and read the relevant section — don't trust filenames or memory.

| Topic | Doc(s) — all must stay synced when listed together |
|-------|-----|
| Install, update, first-run | `INSTALL.md`, `GETTING_STARTED.md`, `README.md` |
| Commands (added / renamed / aliased / behavior change / removed) | `COMMANDS.md` **and** `docs/command-cheatsheet.json` **and** `docs/command-cheatsheet.html` |
| Client feature usage (windows, panels, settings, workflows) | `USER_GUIDE.md` and screenshots under `docs/user-guide/` |
| Solo training mode behavior | `SOLO_TRAINING.md` |
| New / renamed / removed projects or top-level files | `docs/architecture.md` |
| Discord integration | `docs/discord-integration.md` |
| Scenario format / validation | `docs/scenario-validation.md` |

For each match, log a finding: file + section + what's stale, missing, or wrong. A correct-but-incomplete doc (e.g. command added to `COMMANDS.md` but cheatsheet JSON/HTML not updated) is still a gap.

**Findings go to step 3.1** as a focused diff (per file: what's wrong, proposed update). For each:

- Default: update before shipping so the release is self-contained.
- Allowed: defer with a tracking note (file an issue or add a TODO bullet to the next cycle's changelog draft) if the doc change is large enough to warrant its own focused commit.
- Do not auto-edit docs without confirmation.

### 2.9 Scan for open issues this release fixes (subagent)

Step 8.1's `linear release complete` moves every Landed issue in the release to Done, which closes its GitHub issue through the sync. That only catches issues someone **land**ed in Linear. Feature work driven by a Discord thread routinely ships without ever naming the issue, leaving a fixed request open — the reporter never learns it landed.

Match open issues against what actually shipped, not against commit metadata:

1. `gh issue list --repo leftos/yaat --state open --limit 60 --json number,title,createdAt`
2. For each open issue, ask whether any bullet in this release's changelog section satisfies it. Recently-created issues are the likeliest hits — a request filed days before the release is often exactly what the cycle built.
3. **Read the issue body before proposing closure.** Titles mislead. An issue titled "show scratchpad in radar view" may already be half-satisfied, with the real ask buried in a follow-up comment ("manual primaries already show; automatic ones don't"). Confirm the shipped behavior covers the *actual* ask, including any narrowing in the thread.
4. Partial fixes are not fixes. If a cycle pinned a dependency but the issue asked to pin *and* later drop the pin, it stays open — say so explicitly.

Return the matches as a table (issue → ask → implementing commit) for step 3.1, which asks before closing. These are public issues with a watching reporter, so closing posts outward; never close without confirmation. Step 8.3 closes the approved ones.

### 2.10 Scope-check the automatic-behavior bullets (subagent)

This check runs before the draft (4.1) locks the notes — a correction here updates the changelog bullet and any highlight drawn from it.

For every `### Added` / `### Changed` bullet describing something the sim does on its own, without the controller asking, resolve the feature's gating condition and its call sites and state which sessions it reaches: all rooms, solo only, headless/soak only, replay only. Read that from the code, never from the plan note or commit message the bullet came from.

A milestone heading records what the change was motivated by, and a milestone-driven fix that lands on shared sim state reaches every session touching that state. Where the reach is broader than the originating plan context implies, say so in the bullet — that is the reach a reader gets wrong.

Skipping the check leaves both errors available: a bullet that under-states its reach, and a needless narrowing of correct behavior because the reviewer trusted a heading instead of a call site.

### 2.11 Check the feature showcase and the reels (subagent)

Every release ships a **feature showcase** (`CONTEXT.md`): `docs/releases/whats-new-next.md`, one section per major feature this release introduces or reworks (or a set of UI and UX changes worth showing together), each a short paragraph and a screenshot under `docs/releases/img/`.

Read the open release's issues (`linear list yaat`, release `vNext`) beside the unreleased section and check every candidate has its section; a missing one is written before the cut (step 3.2), its screenshot a `whats-new-<topic>` scene.

**Sizzle reels never block a cut.** Reel issues live in the Linear release `vNext reels` in the yaat pipeline, not in `vNext`, and are recorded and edited after the cut in a session with no builds running. A feature that warrants a reel and has none is returned for step 3.1, which puts it to the user, one question per candidate: plan a reel (an **add** joined to `vNext reels` with the MCP `save_issue` `setReleases`), or none. Step 8.2 renames the reel release after the cut.

### 2.12 Audit `### Fixed` for same-release follow-ups

Scan the captured unreleased section for **`### Fixed` bullets that describe polish on features added in the same cycle**. These are `internal-fix` smell that `/changelog-and-commit`'s fold rule (Step 3: a fix to something added in the same unreleased section folds into that bullet) should have folded; they slip through when the changelog was drafted commit-by-commit.

For each Fixed bullet, ask: *was the underlying feature itself added in this release?* (Check `### Added` / `### Changed` in the same section, plus the substance of the bullet.) If yes:

- The user-observable behavior that the fix bullet describes belongs in the corresponding Added bullet, if it's not already implicit.
- The Fixed bullet itself should be dropped — readers haven't seen the broken version, so "now works" is not news.

The candidates go to step 3.1 as a focused diff (drop / fold / keep). Do not auto-edit the unreleased section without confirmation.

## Phase 3: Settle the findings and land the pre-cut work

Needs Phase 2's audits back; the background commands may still be running. Phase 4 starts when every exit condition at the end of this phase holds.

### 3.1 Put the findings to the user

In one message, report each audit's result and ask for each decision its step defines. Write each deferral where its step says. The version-file verdicts use the version from 1.4.

- the open user-reported bugs (2.5, one question per bug: fix first or defer)
- the version-file verdicts, with the `Minimum` caveat when it applies (2.6)
- the deployment-scope verdict with its evidence (2.7)
- the doc gaps (2.8)
- the issue matches to close (2.9)
- the reach corrections (2.10)
- the missing showcase sections and the reel candidates (2.11, one question per candidate)
- the Fixed-bullet candidates (2.12)

### 3.2 Land the agreed work

Land what 3.1 agreed. The bug fixes and the scenes for missing showcase sections are code: each lands in its own commit through its own gate. The `CHANGELOG.md` cleanup and reach corrections and the doc updates (the showcase sections included) are applied in the yaat repo and ride along in the release commit; the staging list in step 5.4 picks them up.

A landing invalidates what Phase 2 measured before it, so follow up each one by what it touched:

- **A hashed source** (2.2): the recompute runs after the last such landing, in parallel with that landing's gate.
- **What a capture shows, or a new showcase scene**: re-run that scene (2.4) after the landing.
- **Any path outside the docs**: re-run 2.7's diff queries and re-check 2.6's breaking-change list against the new HEAD before the draft.
- **Code or tests**: re-run 2.1 after the last such landing.

### 3.3 Review the refreshed screenshots

Once 2.4's runs (and any re-runs from 3.2) finish, copy the user-guide images the showcase uses into its folder, so both pages show the same picture:

```bash
for img in $(rg -o --no-filename -r '$1' 'img/([a-z0-9-]+\.png)' docs/releases/whats-new-next.md | rg -v '^whats-new-' | sort -u); do
  cp "docs/user-guide/img/$img" "docs/releases/img/$img"
done
```

Then `git status --short -- docs/user-guide/img docs/releases/img` lists every PNG whose output changed. Read each one beside its committed version (`git show HEAD:<path> > .tmp/prev-<name>.png`) and confirm it shows what its doc text says. A capture that came out wrong is fixed in its scene (the `guide-capture` skill) and re-captured, or restored with `git checkout -- <path>`. The reviewed PNGs ride along in the release commit with the doc updates.

### 3.4 Commit the recomputed precompute cache

When a recompute ran (2.2's, or the one after the last hashed-source landing in 3.2), commit the changed entries under `src/Yaat.Sim/Data/PrecomputeCache/` as `chore: recompute the precompute cache` before the release commit.

### 3.5 Select highlights

Read the unreleased section as 3.2 left it. Count the user-visible bullets first: **if Added/Changed/Fixed total fewer than 3-4 user-visible items, omit the `### Highlights` block entirely** — those bullets already are the highlights, and a Highlights header would just duplicate them in the GitHub release notes (the workflow falls back to showing the full changelog). Otherwise select **3-4 user-impactful items** to surface as highlights:

- Prefer items from `### Added` and `### Changed`. `### Fixed` items only if a fix is something users were waiting on (i.e. the broken behavior shipped in a prior release).
- Skip purely internal items even if they made it into the changelog (refactors, test infra, build plumbing).
- Tighten each chosen bullet to a short, scannable one-liner — drop sub-clauses about how it works internally. The full detail stays in the Changelog section below the Highlights.
- No marketing language (no "significantly", "robust", "comprehensive", etc.). State the change.
- **Write for users, not developers.** The audience is instructors and RPOs running YAAT, not contributors. Drop implementation jargon: framework/library names (Velopack, Avalonia, SignalR), class/method names, exception types, thread/dispatcher terminology, internal subsystem names. Lead with what the user sees and does. Keep user-vocabulary names for actual UI elements (e.g. the "Update Now" button, the command bar).
  - Bad: *"Velopack download-progress callback now marshalled to UI thread, fixing InvalidOperationException."*
  - Good: *"'Update Now' no longer crashes — auto-updates download and apply correctly."*

### Exit conditions

- 2.1's suite is green over the tree that ships.
- The precompute cache is current for the tree that ships and committed (3.4): 2.2's check reported nothing stale, or a recompute ran after the last hashed-source landing.
- 2.3's consolidation has committed or reported zero duplicates.
- Every changed screenshot has been reviewed (3.3).
- Every open user-reported bug is Landed in the open release or deferred by the user (2.5).
- Every agreed landing from 3.2 is in.

## Phase 4: Draft the release notes and get approval

Needs Phase 3's exit conditions. The user's approval of this draft gates Phase 5.

### 4.1 Present the draft

Show the user the draft — highlights from 3.5 **plus the full unreleased CHANGELOG section verbatim**:

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

Also restate the **deployment-scope verdict** from 2.7 (CLIENT_ONLY or SERVER_AFFECTING, with the triggering paths) so the user knows upfront whether a server deploy is coming.

### 4.2 Apply the requested edits

Ask the user to review. Apply any requested edits to the highlights or the unreleased section in CHANGELOG.md, and re-present until the user approves.

## Phase 5: Cut the release

Needs the user's approval from Phase 4. Steps 5.1 to 5.3 are one edit set; 5.4 commits it and 5.5 tags it.

### 5.1 Apply every version bump

Apply **every** version bump decided in 2.6 — `<Version>` in `Directory.Build.props`, and in yaat-server both `Yaat:ClientVersions:Recommended` (always) and `Yaat:ClientVersions:Minimum` (only on the breaking-change verdict), keeping `ClientVersionOptions`' defaults in sync. The yaat-server edits are committed with that repo's own commit and pushed in step 6.3, not with yaat's release commit.

### 5.2 Promote the heading, insert the highlights, rename the showcase

1. **Promote the CHANGELOG.md heading** in place — `Edit` the heading line to the chosen format (version + optional date, matching sibling sections). Do not touch the body of the section yet.
2. **Insert the approved highlights** into CHANGELOG.md as a `### Highlights` subsection at the top of the version's section, immediately after the heading and before the first existing subsection (typically `### Added` or `### Fixed`). Use the bullets verbatim as approved in Phase 4 — these are what the GitHub release will surface.
3. **Rename the showcase** `docs/releases/whats-new-next.md` to `whats-new-<version>.md` (and its image folder references). `release.yml` links that file from the GitHub release below the highlights, and the Discord announcement carries the link, so the name must match the version exactly (`whats-new-0.16.0-beta.md` for `v0.16.0-beta`).

### 5.3 Update the architecture doc

Update `docs/architecture.md` if any new files were added (and it wasn't already covered by 3.2's doc updates).

### 5.4 Stage and commit

Stage these explicit files only (no `git add -A`): `Directory.Build.props`, `CHANGELOG.md`, the renamed showcase, `docs/architecture.md` if changed, **every user-facing doc updated in step 3.2** (e.g. `COMMANDS.md`, `docs/command-cheatsheet.json`, `docs/command-cheatsheet.html`, `USER_GUIDE.md`, `INSTALL.md`, etc.), and the screenshots reviewed in 3.3. List them explicitly so the user can audit before commit.

If the release commit is a partial commit of a dirty tree, follow the partial-commit protocol in `changelog-and-commit`'s `reference.md` ("Partial commits under prek") — stash the remainder including untracked files, commit with explicit paths, pop, then `git show --stat HEAD` to confirm nothing extra landed.

Commit: `release: v{version}`.

Commit yaat-server's version edits from 5.1 in that repo, staging `src/Yaat.Server/appsettings.json` and `src/Yaat.Server/YaatOptions.cs` by name, as `chore: require client {version}` when `Minimum` moved (the body names the breaking change) or `chore: recommend client {version}` otherwise. It goes through the gate like any commit and is pushed in 6.3.

### 5.5 Tag

Create the tag: `git tag v{version}`.

## Phase 6: Push both repos

Needs the release commit and tag from Phase 5.

### 6.1 Ask how to push

Pushing causes no downtime — the room-occupancy check and the deploy decision come *after* the ~20-minute CI image build (step 7.4), when they're actually current. Use `AskUserQuestion` (single-select), ordered by the 2.7 verdict. Every release is created as a hidden draft that something in this flow must publish (Phase 7, "Drafts and the announcement").

**If 2.7 was CLIENT_ONLY** — lead with skipping the deploy:
- **Push only — skip server deploy (Recommended)** — no shared-library, server, or web-UI (vStrips/vTDLS) changes this cycle, so the running server already matches the release. Pushing ships the new client via `release.yml`; step 7.1 watches the release runs and step 7.3 publishes the draft when they are green.
- **Push, then build image + decide deploy after** — force a redeploy anyway (e.g. to pull a new AIRAC cycle); continues through steps 7.2, 7.4 and 7.5.
- **Abort** — stop here; the user resumes manually.

**If 2.7 was SERVER_AFFECTING** — the release changes what the server runs:
- **Push and start the server image build (Recommended)** — push both repos, then front-load the ~20-minute CI image build (no downtime; the live server keeps serving). The deploy decision and room check happen when the image is ready.
- **Push only — no image build, no deploy** — leaves the live server on the previous build. **Warn explicitly:** the draft release stays invisible to users and to auto-update until a deploy publishes it — or until it's published manually with `gh release edit v{version} --repo leftos/yaat --draft=false`.
- **Abort** — stop here; the user resumes manually.

If "abort" is picked, stop.

### 6.2 Check both repos before either push

Push both repos so any cross-repo work made during this cycle ships together. Run these checks before pushing either repo.

**Confirm where each HEAD is:**

```bash
git        status -sb | head -1      # expect '## main...origin/main'
git -C "$SERVER" status -sb | head -1
```

`## HEAD (no branch)` means the checkout is detached — a commit made there is real but off-branch, and this push will not carry it. Halt and recover with `git checkout -B main <sha>` (safe when `git merge-base --is-ancestor main HEAD` holds) before continuing.

**Check what the push range holds.** The range is every local commit `origin/main` lacks, not just this cycle's release commit, and a commit minted by a codegen tool, an IDE action, or a hook that commits carries none of the repo's `Co-Authored-By:` / `Claude-Session:` trailers and never passed the ask-before-commit gate. A range with one in it pushes exactly like a clean one:

```bash
git              log --format='%h %s' --invert-grep --grep='Co-Authored-By:' origin/main..main
git -C "$SERVER" log --format='%h %s' --invert-grep --grep='Co-Authored-By:' origin/main..main
```

Anything printed is a commit this workflow did not author. Halt and report it before pushing — a release push is the point after which the message can no longer be amended.

**Confirm no unexpected local tags are pending** (yaat only; yaat-server isn't release-tagged):

```bash
comm -23 <(git tag | sort) <(git ls-remote --tags origin | sed 's/.*refs.tags.//;s/\^{}//' | sort -u)
```

This should print nothing but the release tag you just created. If it lists others, delete or investigate them before pushing — do not push them along (step 6.3, "Never use `--tags`").

### 6.3 Push yaat-server, then yaat, then the tag

- First push yaat-server's pending commits, its version-bump commit from 5.1 included (no tag — yaat-server isn't release-tagged): `git -C "$SERVER" push origin main`. Run even if you think there's nothing pending — it's idempotent. If the push is rejected, rebase (`git -C "$SERVER" pull --rebase origin main`) and re-push.
- Then push yaat's release commit, and **only after that returns**, push the release tag **by name in a separate command**:

  ```bash
  git push origin main
  git push origin v{version}
  ```

**Two pushes, not one.** GitHub sometimes coalesces a simultaneous branch push and tag push into a single delivered `push` webhook event; when that happens the `release.yml` workflow (`push: tags: ['v*']`) does not fire and only the `main`-triggered CI workflow runs. A tag pushed on its own always produces its own event.

If the release workflow still shows no run for the tag (`gh api "repos/leftos/yaat/actions/runs?head_sha=<sha>"` → `total_count: 0`), re-push the tag in isolation: `git push --delete origin v{version}` then `git push origin v{version}`.

**Never use `--tags` here.** Besides re-sending every existing tag in one batch (which raises the coalescing risk above), this repo has two tag namespaces with separate release workflows — `v*` (the client, `release.yml`) and `crc-config-v*` (the standalone tool, `yaat-crc-config.yml`).

`--tags` pushes every local tag, so any stale unpushed tag fires its workflow and publishes a release built from whatever old commit it points at. Because GitHub ranks "Latest" by publish time, that stale release also steals the Latest badge and the `/releases/latest` API from the real client release.

Order matters: yaat-server first means yaat-server's own work is live before yaat's release CI fires. yaat's CI builds and tests yaat-server's `main` against the release commit, so pushing yaat second tests the pair as it ships.

## Phase 7: Watch, build and publish

Needs the tag push. Steps 7.1 and 7.2 launch together, in the background, right after it; each later step names what it waits on.

**Drafts and the announcement.** `release.yml` **always** creates the GitHub Release as a *draft* — invisible to users and to auto-update. CI cannot judge deployment scope (a release can be server-affecting purely via yaat-server, which the workflow can't see), so publishing is never decided there: step 7.3 publishes a client-only release, and the deploy script publishes on the deploy path.

The Discord announcement fires on its own, once, on publish: publishing with a user token (manually or via the deploy script) raises the `release: published` event that triggers `discord-release.yml` — do **not** also dispatch that workflow, or the announcement posts twice. A release published without an installer is announced without it.

### 7.1 Watch every client release run — both paths, always

The tag push starts three workflows on the tagged commit: `Release` (Windows installer + the draft GitHub Release), `Release (macOS)` (signed/notarized `.pkg` + `.app`, appended to that draft with `gh release upload`), and `CI`.

Confirming they *started* is not the step; a run that fails after that goes unnoticed until a user reports it, and publishing the draft (step 7.3 or the deploy) before `Release (macOS)` has uploaded ships a release with no macOS installer. This step is complete only when every run on the tagged SHA reports `success`, or when the one failure is `CI`'s formatting or style step (item 3 below).

Right after the tag push, list the runs and watch each one **in the background** (they take 15-30 minutes; the image build in step 7.2 runs alongside):

```bash
SHA=$(git rev-parse v{version}^{commit})
gh api "repos/leftos/yaat/actions/runs?head_sha=$SHA" --jq '.workflow_runs[] | "\(.id)\t\(.name)\t\(.status)\t\(.conclusion)"'
gh run watch <run-id> --repo leftos/yaat --exit-status   # one background call per run
```

If the list is missing `Release` or `Release (macOS)`, the tag push was coalesced — re-push the tag in isolation as described in step 6.3. When a watch exits non-zero:

1. `gh run view <run-id> --repo leftos/yaat --json conclusion,jobs` to find the failed job and step, then `gh run view <run-id> --repo leftos/yaat --log-failed > .tmp/<name>-failed.log` and read the error lines.
2. **Transient infrastructure** — Apple's timestamp service (`CMS signature encoding failed: The timestamp service is not available. (-67885)`), a notarization upload/poll timeout, `smoke-intel`'s model download from Hugging Face, a runner lost mid-job — re-run the failed jobs: `gh run rerun <run-id> --repo leftos/yaat --failed`, then watch the re-run the same way. The `build` jobs stay green and are not repeated.
3. **A `CI` failure confined to `Formatting (CSharpier)` or `Style diagnostics`** (an info-level IDE rule in a test file, say) while `Release` and `Release (macOS)` are green is not a release defect: the installers are fine, so carry on to the publish (7.3) or the deploy (7.5).

   Commit and push the fix only after the draft is published or the deploy prints `Deployment complete!` — the droplet re-resolves yaat main, and a commit past the tag fails the deploy's tag-equals-deployed-commit publish check.
4. **Anything else** (compile error, packaging error, a missing secret) is a release defect: report it with the error lines and stop. Do not publish the draft and do not deploy until the user decides how to fix forward; a hidden draft is recoverable, a published release with a broken or missing installer is not.

Report each run's final conclusion to the user in the message that moves on to the next step.

### 7.2 Build the server image (when 6.1 chose an image build)

Run it **in the background**, through the gate wrapper (a green-looking failed build would feed a stale image straight into step 7.5):

```bash
pwsh tools/gate.ps1 -Log .tmp/deploy-image-build.log -TimeoutSeconds 1800 -Slot heavy -- pwsh deploy-to-droplet.ps1 -BuildImageOnly
```

It dispatches yaat-server's `docker-image.yml`, watches the ~20-minute CI build, and exits without touching the droplet — the live server keeps serving throughout, so no room check is needed yet. (`docker-image.yml` advances `extern/yaat` to origin/main at build time, so dispatching immediately after the push is safe.)

**Do not land further commits on yaat main until the deploy finishes** — the image bakes in whatever main holds, and the deploy only auto-publishes the draft release when the deployed client commit *is* the tagged commit. If the build fails, surface it and stop — do not deploy a stale image.

### 7.3 Publish a CLIENT_ONLY release (waits on 7.1)

Skip on SERVER_AFFECTING — the deploy publishes there. On the client-only path no deploy will publish the draft. Once step 7.1 is complete, confirm the draft holds all three installers — `YaatClient-{version}-win-Setup.exe`, `YaatClient-{version}-osx-arm64-Setup.pkg` and `YaatClient-{version}-osx-x64-Setup.pkg`:

```bash
gh release view v{version} --repo leftos/yaat --json assets --jq '.assets[].name | select(endswith("-Setup.exe") or endswith("-Setup.pkg"))'
```

A missing one means its run has not uploaded yet: back to 7.1. With all three listed, publish:
`gh release edit v{version} --repo leftos/yaat --draft=false`

Steps 7.4 to 7.6 are the image-build/deploy path: they run only when 6.1 forced a redeploy (7.2 is then already building). Otherwise go on to Phase 8.

### 7.4 Check rooms, then ask whether to deploy (waits on 7.2's success)

Only now is the occupancy check meaningful — the deploy that follows takes just the container-recreate downtime (~2 minutes). Run `pwsh deploy-to-droplet.ps1 -StatusOnly` and report the result: the active room count, and for each room its id, members, scenario, and aircraft count — or "no active rooms".

The script reads `ADMIN_PASSWORD` from yaat `.env` itself — **do not read the secret yourself**. If the query fails (server unreachable, password unset — exit code 2), say so and continue to the prompt, treating occupancy as unknown/possibly-occupied.

Surface the room status in the same message as the deploy question, then use `AskUserQuestion` (single-select), leading with "deploy now" when the server is empty and "wait for rooms to clear" when it isn't:
- **Deploy now** — ~2 minutes of downtime; active sessions are checkpointed and restored.
- **Wait for rooms to clear, then deploy** — run `pwsh deploy-to-droplet.ps1 -WaitForEmptyRooms` **in the background** (it polls `GET /admin/status` every 60s and blocks until the server reports zero rooms, printing the active rooms each check; backgrounding keeps the agent responsive — you're re-invoked when it exits). When it exits cleanly, continue to step 7.5. *(The deploy still calls `prepare-restart` as a safety net for any room that appears between "cleared" and "deployed".)*
- **Skip the deploy** — leave the live server on the previous build. **Warn explicitly** (SERVER_AFFECTING): the GitHub Release stays a hidden draft until a deploy publishes it or it's published manually.

### 7.5 Deploy (waits on 7.4 and on 7.1 being complete)

The deploy publishes the draft release, so a still-running or failed `Release (macOS)` at this point ships a release without a macOS installer; if a watch is still pending, wait for it (rooms cleared in step 7.4 stay cleared for the extra minutes, and the deploy's `prepare-restart` covers any that appear). Run it through the gate wrapper:

```bash
pwsh tools/gate.ps1 -Log .tmp/deploy-droplet.log -TimeoutSeconds 1800 -Slot heavy -- pwsh deploy-to-droplet.ps1 -SkipCiBuild -NoLogs
```

`-SkipCiBuild` deploys the image step 7.2 already built instead of building again; always pass `-NoLogs` — without it the script tails server logs indefinitely and blocks the agent until timeout. The script calls `POST /admin/prepare-restart` first (needs `ADMIN_PASSWORD` in yaat `.env`, matching the droplet) so active training sessions survive the deploy. Use `-SkipSessionSave` only for emergency deploys. Wait for the `Deployment complete!` banner before declaring the release done.

### 7.6 Verify the release is public

On the deploy path `deploy-to-droplet.ps1` publishes the draft after verifying the deployed client commit matches the tag — watch for its "Published GitHub release" line.

Confirm with `gh release view v{version} --repo leftos/yaat --json isDraft,assets`: `isDraft` false **and** the three installers of step 7.3 listed — a public release missing one means its run failed or never uploaded, so go back to step 7.1 for it.

If it's still a draft (deploy skipped, failed, the commit check refused, or `release.yml` hadn't created the draft yet when the deploy finished), the release is invisible to users and auto-update; publish manually with `gh release edit v{version} --repo leftos/yaat --draft=false` once the matching server is live.

## Phase 8: Close out

Needs the release public (step 7.6, or 7.3 on the client-only path). Launch 8.1 and 8.3 together; 8.2 follows 8.1's `release complete`.

### 8.1 Complete the Linear release — no confirmation

Cutting the release is the owner's ack for every issue that shipped in it, so this step runs without asking. First **land** the issue that tracks this cut, if the queue has one (a "Cut the release…" gate item), on the tag commit (`linear land <ID> --sha <tag commit>`): only Landed issues ship, so an unlanded cut item stays in `vNext`. Then:

```bash
uv run --project ~/.claude/tools/linear python -m linear release complete yaat --version v{version} --sha "$(git rev-parse v{version}^{commit})" --repo-root "$YAAT"
```

It creates a release `v{version}` and moves into it each Landed issue of `vNext` whose landing commit is in the tag's history, set to Done with the comment `Released v{version}` (which closes its GitHub issue through the sync), plus any issue already Done; Canceled issues leave `vNext`, every other issue stays in it, and every `vNext+1` issue moves into `vNext`.

Then it completes `v{version}` at the tag commit. `vNext` and `vNext+1` keep their ids and names. Report its `<ID>: Done` / `<ID>: stays in vNext` / `<ID>: moved to vNext` lines.

If it exits 2 with `LINEAR_OAUTH_CLIENT_ID is not set`, the shell predates the user variables: run it from PowerShell after `$env:LINEAR_OAUTH_CLIENT_ID = [Environment]::GetEnvironmentVariable('LINEAR_OAUTH_CLIENT_ID','User')` and the same for `LINEAR_OAUTH_CLIENT_SECRET`. After a failure, re-run the same command: it reuses the open `v{version}` release the failed run left.

Then sweep the project statuses with the Linear MCP `save_project`: a project whose every issue is Done or Canceled → `completed` (the snapshot also does this), one with an In Progress issue → `started`. Then regenerate the plan snapshot (`linear snapshot yaat --repo-root "$YAAT"`) and commit `docs/plans/MAIN.md` as `docs: plan snapshot` and push it, if it changed; the deploy is over, so a commit past the tag is safe now.

### 8.2 Roll the reel release

After 8.1's `linear release complete`, rename `vNext reels` to `<version> reels` (the MCP `save_release`) and create a fresh `vNext reels` (stage Planned) for the next release's reels.

### 8.3 Close the open issues this release fixed

Close each issue the user approved in 3.1 from 2.9's table. When closing, comment with the version, the implementing commit SHA, and a short description of the shipped behavior in user terms — the reporter should be able to tell whether their case is handled without reading the diff.

## What the tag push ships

The tag push triggers the `release.yml` GitHub Actions workflow. The workflow extracts the matching section from `CHANGELOG.md` (using the tag name), splits out the `### Highlights` subsection for the GitHub Release's "Highlights" block, and uses the rest of the section as the "Changelog" block.

The highlights you and the user agreed on in Phase 4 are exactly what ships — no AI rewriting at release time. Every release is created as a draft and published by this flow: step 7.3 for client-only releases, the deploy script for server-affecting ones (full mechanics: `docs/installer-release.md`).
