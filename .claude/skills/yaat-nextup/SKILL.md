---
name: yaat-nextup
description: Profile for the user-level `nextup` skill in the yaat / yaat-server repos — loaded by `nextup` at its step 0 for this project's plan convention, agents, gates, docs map and landing path. Not a loop of its own; invoke `/nextup`.
---

# yaat profile for `nextup`

The generic loop is the user-level `nextup` skill; this file supplies only what is yaat-specific.

## Plan and tracker

- Index: `docs/plans/MAIN.md`. Sections in priority order: **Bug reports and feature requests**, then the **Current programme**'s next slice, then **Backlog — waves** top to bottom (a wave is one release-sized bundle sharing files and a review gate); the other programmes run in the background and release with a non-hotfix.
- siblings: ../yaat-server
- `../yaat-server` has no plan index or changelog of its own: this index plans it, and its `docs/plans/live-traffic-swim/` is linked from here.
- Pre-loop hooks: none.
- Finished-item convention: **delete the line**, never tick it; finished subplans are deleted (git history is the archive). Review findings the item does not fix become Backlog lines in the same commit.
- Tracker: `gh issue list --repo leftos/yaat --state open --json number,title,createdAt`; fold unplanned issues in with `triage-open-issues`. Cross-repo: yaat-server commits cite `Closes https://github.com/leftos/yaat/issues/N`.
- Pull requests: `gh pr list --repo leftos/yaat --state open --json number,title,author`, and the same with `--repo leftos/yaat-server`. An unplanned PR gets one line, its review and landing: a person's PR under **Bug reports and feature requests**, a bot's dependency bump at the foot of the Backlog, each naming the files, whether the checks pass and whether it merges cleanly.
- Hotspots (3,000–4,000 lines each; two items touching one wait on each other): `PatternCommandHandler.cs`, `MainViewModel.cs`, `CommandParser.cs`, `CommandDispatcher.cs`, `MainWindow.axaml.cs`, `GroundCommandHandler.cs`.

## Agents and gates

- Explore: the user-level `Explore` (docs-first). Domain ruling: `aviation-sim-expert` with the local-FAA-references preamble from CLAUDE.md — `aviation-review-gate` decides when it is owed. Attachments: `multimodal-looker` (a Discord "*[empty message]*" is a forward; files sit under `message_snapshots`).
- Reviewers: `csharp-reviewer` for anything beyond a one-file change; `aviation-sim-expert` for aviation behaviour.
- Brief shape: `test-fix` (red test first, real navdata, never synthetic). Every `dotnet` command wrapped as `pwsh tools/gate.ps1 -Log .tmp/<name>.log -TimeoutSeconds <seconds> -Slot heavy -- <command…>` (heavy for every yaat call, a filtered test run included since it builds first), 30 on filtered test runs (a ceiling on the gate's load-adjusted clock; `CLAUDE.md` "Build after edits" says how to read a `STALLED`, `TIMED OUT` or `BACKSTOP` kill), `-p:TreatWarningsAsErrors=true` on builds. A `Yaat.Sim` change is proved by `pwsh tools/test-all.ps1` (both repos) before it lands.
- Parent-side gate: `git -C <wt> status --short` in both halves of the pair, and the same in both main checkouts: yaat's is `$(cd "$(git rev-parse --path-format=absolute --git-common-dir)/.." && pwd)` also from a worktree, and yaat-server's is its sibling.

## Traps

- `git -C ../yaat-server` from a lone yaat worktree (no pair beside it) walks up to the nearest `.git` and silently operates on yaat; resolve `$SERVER` from the main checkout first (the recipe is in `prepare-release`).
- A partial commit stashes the rest of yaat; if yaat-server's on-disk code depends on a stashed `Yaat.Sim` change, prek's build hook fails with `CS0246` from the sibling — stash yaat-server first (CLAUDE.md, "prek's build hook is cross-repo").
- `dotnet format`: never bare, never `-v q`; CSharpier formats nothing under a dot-directory worktree, so format in the main checkout after the patch is applied.

## Concurrency

- Worktrees: every item gets a pair, `../yaat.wt/<slug>/yaat` and `../yaat.wt/<slug>/yaat-server`, both on branch `<slug>` from `<base>` (from the yaat main checkout: `git worktree add ../yaat.wt/<slug>/yaat -b <slug> <base> && git -C ../yaat-server worktree add "$(cd .. && pwd)/yaat.wt/<slug>/yaat-server" -b <slug> <base>`, where yaat-server uses its `main` when it has no `<base>`), then `branch.<slug>.base` / `branch.<slug>.landOn` recorded in each half as the user-level `nextup` §3 **Base and target** says. The pair mirrors the main layout, so yaat-server's `../yaat/src/Yaat.Sim` reference, `tools/Yaat.GuideCapture`'s reference to the server and `tools/test-all.ps1`'s default `-ServerDir` all resolve inside it, and `/ship` (with `## Ship` below) lands it as a paired session. A yaat-only item still gets both halves, since its `Yaat.Sim` gate builds the server.
- Ceiling: three implementers. Every `Yaat.Sim` ship runs the cross-repo gate on `main`, and those serialize there anyway.
- Context: the figure the status bar shows is read at every landing from the session's `<scratchpad>/statusline.json` (the user-level CLAUDE.md's "Context usage is measured" rule), and past 40% the user-level `nextup` stops refilling: the slot a landing frees stays empty, an exploration already running is written into the plan, and the checkpoint follows the in-flight items.
- Depends on, where yaat's file lists hide it: a `Yaat.Sim` signature, `AircraftState` field or `CanonicalCommandType` one item adds and another item's client or yaat-server work consumes; a hub method or `AircraftUpdated` field (`docs/training-hub-contract.md`) one item adds and another renders; two items that each change the snapshot schema (`SnapshotSchemaMigrator` orders the migrations).

## Docs map

| What changed | Owning docs |
|---|---|
| Any command added, renamed, aliased or changed | `COMMANDS.md`, `docs/command-cheatsheet.json` → `node tools/build-cheatsheet.mjs` |
| Anything an instructor or RPO sees or does | `USER_GUIDE.md` (screenshots via `tools/Yaat.GuideCapture`, run separately); `SOLO_TRAINING.md` for solo mode |
| A file added, moved or removed; a subsystem's shape | `docs/architecture.md` (the `architecture-updater` agent), the subsystem doc CLAUDE.md lists for it |
| A hub method or `AircraftUpdated` field | `docs/training-hub-contract.md` |
| A yaat-server file added, moved or removed; a server subsystem's shape | yaat-server `docs/architecture.md` |
| A CRC wire DTO or MessagePack layout | yaat-server `docs/crc-wire/` (regenerated per `docs/crc-update.md`), yaat `docs/crc-display-state.md` |
| Live traffic / SWIM behaviour | yaat-server `docs/plans/live-traffic-swim/` (the subplan the item cites), yaat `docs/live-traffic.md` |
| Everything user-visible | `CHANGELOG.md` under the unreleased heading, one bullet per change |

## Landing

- Orchestrator writes docs, the changelog bullet and the MAIN.md line removal **in the worktree**, commits there, then `/ship` (with `## Ship` below): land `base..<slug>` onto the recorded `landOn` (`ship` Phase 2), gate there when it was a real cherry-pick, push both repos, close the issue with an audit comment, then in each repo remove its half of the pair and its branch once landed, by the user-level `nextup` §4 step 6 check.
- An item under a feature marker (`branch: feat/<name>`, user-level `nextup` §3 "Feature branches") lands the same way onto the feature pair (`../yaat.wt/feat-<name>/yaat` and `/yaat-server`), whose `landOn` is `feat/<name>`; `/ship` then pushes both feature branches and watches their feature PRs without merging them. Its MAIN.md line stays on `main` with the note `landed on feat/<name>, ships with #N` in a separate `docs:` commit there, and leaves MAIN.md with the marker line when `/ship` Phase 2F merges the feature PRs.
- The main checkout hosts at most one implementer, and none while a gate runs there.

## Changelog

Read by the user-level `changelog-and-commit`; each rule names the step it adds to or overrides.

- Home: one `CHANGELOG.md`, at the yaat checkout root. yaat-server has none; its user-visible changes are bulleted in yaat's file. Plan lines close by `## Plan and tracker`.
- Sibling (Step 0, Step 1): yaat-server, snapshotted and scoped like yaat. It is the `yaat-server` beside the current yaat checkout when that is a checkout on the same branch (a paired worktree), else the main checkout's sibling, `"$(git rev-parse --path-format=absolute --git-common-dir)/../../yaat-server"`; never a drive letter. Naming the main checkout's sibling from a paired session reports the server side clean and commits nothing there.
- Commit order (Step 8): yaat-server first, its code and tests, prefix from the work; then yaat, `CHANGELOG.md` and the plan index plus any yaat scope, `docs:` when the changelog and plan are all it carries, subject and body mirroring the bullets. A yaat-server branch behind `origin/main` runs `git pull --ff-only` before its commit. A diff wholly in yaat is one commit.
- Hooks (Step 8): per repo. A hook that reformats a file means re-stage and a new commit in that repo; the other repo's commit is unaffected.
- Announcement and report (Step 5, Step 8): name both repos and which files go in which commit; the report prints both HEADs, `Committed yaat-server@<sha> — <subject>`, `Committed yaat@<sha> — <subject>`, `Working trees: clean / clean`.
- Reference (Step 2): `### Fixed` against `### Added` is judged against the emulated upstream (vNAS TDLS, CRC/STARS, ATCTrainer). A YAAT-original convenience the upstream lacks (a Tools-menu shortcut, an instructor-only view) is Added.
- Reach vocabulary (Step 2): all rooms, solo sessions only, headless/soak only, replay only.
- Audience (Step 4): instructors and students. No framework names (Velopack, Avalonia, SignalR, MessagePack); command names and UI vocabulary stay (`CTOC`, the "Update Now" button, Help → About).
- Issue references (Step 7): a yaat-server commit that closes a yaat issue writes `Closes https://github.com/leftos/yaat/issues/N`.
- Partial commits (Step 8): the build hook is cross-repo (`CLAUDE.md`, "prek's build hook is cross-repo"), so a partial commit in yaat stashes yaat-server too: `git -C <yaat-server> stash push -u`, commit, `git -C <yaat-server> stash pop`.
- Commit call (Step 8): never chain `git commit` after a `dotnet` command in one shell call; a guard reads the whole command text and rejects it. Implicated tests run through the gate as `## Agents and gates` says.

## Ship

Read by the user-level `ship`; each rule names the phase it adds to or overrides.

- Sibling (Phase 0): yaat-server, shipped by the same phases. Source: the `yaat-server` beside the yaat worktree when it is on the same branch (a paired session), else the main checkout's sibling. Target: always the main checkout's sibling, `"$(cd "$(git rev-parse --path-format=absolute --git-common-dir)/.." && pwd)/../yaat-server"`; never a drive letter. Its first `status -sb` line halts on `## HEAD (no branch)` like yaat's (yaat-server sits detached at `origin/main` after a submodule-style update). A dirty yaat-server source holding the session's edits goes through Phase 1 first; a yaat-server with no local-only commits against `main` is `Phase 2: yaat-server nothing to land`.
- Orders (Phase 2, Phase 2F, Phase 4):

  | | First | Then | Why |
  |---|---|---|---|
  | Landing | yaat | yaat-server | Default. A `Yaat.Sim` signature change yaat-server calls lands yaat-server first (the deadlock rule below): a fast-forward runs no hooks. |
  | Push | yaat-server | yaat | yaat's CI dispatches `submodule-updated` only after its build and tests pass on a push (`.github/workflows/ci.yml`, "Trigger yaat-server CI"), and yaat-server's `update-submodule.yml` checks out yaat's `origin/main` and retries its push with a rebase, so the bump never names a missing commit. |

- Feature PR (Phase 0, Phase 2F): one per repo that carries the branch, `gh pr view feat/<name> --repo leftos/yaat --json number,state,baseRefName` and the same with `--repo leftos/yaat-server`. Phase 2F runs per repo in the landing order; the marker line closes once, on yaat's `main`, after both PRs merge.
- Additive conflict files (Phase 2, Phase 2F): `CHANGELOG.md`, `docs/plans/*.md`, `docs/architecture.md`.
- Hook deadlock (Phase 2): a `Yaat.Sim` signature change yaat-server calls deadlocks the two prek build hooks. yaat's compiles `yaat.slnx`, which builds the sibling yaat-server from disk (old call site, `CS7036`); yaat-server's builds against the sibling yaat (no new API). Land yaat-server first:
  - yaat-server fast-forwards: `git merge --ff-only` makes no commit and runs no hooks; then run or resume the yaat pick, whose hook now sees the new call site.
  - yaat-server needs a real cherry-pick: start the yaat cherry-pick first and let it pause (a conflict or a hook failure; yaat's tree then holds the new `Yaat.Sim` on disk), cherry-pick yaat-server (its hook builds against that tree), then `git cherry-pick --continue --no-edit` in yaat.
  - Standing hooks-bypass exception (`CLAUDE.md`, "Git & Issues"): an intermediate cherry-pick that cannot pass its hooks until the sibling lands, never pushed on its own, is finished with `git -c core.hooksPath=<empty dir> cherry-pick --continue --no-edit`. The end gate must then pass before anything is pushed.
- Gate (Phase 3), in yaat's main checkout after a real cherry-pick: `pwsh tools/gate.ps1 -Log .tmp/build.log -TimeoutSeconds 300 -Slot heavy -- dotnet build -p:TreatWarningsAsErrors=true`; a `Yaat.Sim` signature change also `pwsh tools/gate.ps1 -Log .tmp/test-all.log -TimeoutSeconds 900 -Slot heavy -- pwsh tools/test-all.ps1`.
- End gate (Phase 3), after the deadlock path or after any signature-changing commit lands on a diverged `main`, before anything is pushed, in both main checkouts (yaat-server has no gate of its own; call `../yaat/tools/gate.ps1` from it): `pwsh tools/gate.ps1 -Log .tmp/prek.log -TimeoutSeconds 900 -Slot heavy -- prek run` and the gated build in each, then the gated `test-all.ps1` in yaat's, which builds and tests both. Report those results, not "hooks passed". A break it finds is fixed forward on `main`; a DTO or wire mapping fix goes into one shared helper both call sites use, never a duplicated switch.
- Large-file hook (Phase 2): a recording bundle (`tests/Yaat.Sim.Tests/TestData/*.zip`) over the `check-added-large-files` limit in `prek.toml` is trimmed, never exempted: cut the manifest's `Snapshots` to the time range the test uses.
- Rejected push (Phase 4): the path set is yaat-server's `extern/yaat`. Never bump `extern/yaat` by hand; yaat-server's CI owns the pointer.
- Issues (Phase 5): the issue repository is always `leftos/yaat`, also for a yaat-server-only fix, whose `Closes` GitHub never auto-closes. Comment lines name `leftos/yaat@<sha>` and `leftos/yaat-server@<sha>`.
- Branch names (Phase 5): `nightly-review/<date>-<slug>` encodes a date, not an issue.
