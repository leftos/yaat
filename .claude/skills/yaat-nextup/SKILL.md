---
name: yaat-nextup
description: Profile for the user-level `nextup` skill in the yaat / yaat-server repos — loaded by `nextup` at its step 0 for this project's plan convention, agents, gates, docs map and landing path. Not a loop of its own; invoke `/nextup`.
---

# yaat profile for `nextup`

The generic loop is the user-level `nextup` skill; this file supplies only what is yaat-specific.

## Plan and tracker

- Index: `docs/plans/MAIN.md`. Sections in priority order: **Bug reports and feature requests**, then the **Current programme**'s next slice, then **Backlog — waves** top to bottom (a wave is one release-sized bundle sharing files and a review gate); the other programmes run in the background and release with a non-hotfix.
- Pre-loop hooks: none.
- Finished-item convention: **delete the line**, never tick it; finished subplans are deleted (git history is the archive). Review findings the item does not fix become Backlog lines in the same commit.
- Tracker: `gh issue list --repo leftos/yaat --state open --json number,title,createdAt`; fold unplanned issues in with `triage-open-issues`. Cross-repo: yaat-server commits cite `Closes https://github.com/leftos/yaat/issues/N`.
- Hotspots (3,000–4,000 lines each; two items touching one wait on each other): `PatternCommandHandler.cs`, `MainViewModel.cs`, `CommandParser.cs`, `CommandDispatcher.cs`, `MainWindow.axaml.cs`, `GroundCommandHandler.cs`.

## Agents and gates

- Explore: `yaat-explore` (docs-first). Domain ruling: `aviation-sim-expert` with the local-FAA-references preamble from CLAUDE.md — `aviation-review-gate` decides when it is owed. Attachments: `multimodal-looker` (a Discord "*[empty message]*" is a forward; files sit under `message_snapshots`).
- Reviewers: `csharp-reviewer` for anything beyond a one-file change; `aviation-sim-expert` for aviation behaviour.
- Brief shape: `test-fix` (red test first, real navdata, never synthetic). Every `dotnet` command wrapped as `bash tools/gate.sh .tmp/<name>.log <command…>`, `timeout 30` on filtered test runs, `-p:TreatWarningsAsErrors=true` on builds. A `Yaat.Sim` change is proved by `pwsh tools/test-all.ps1` (both repos) before it lands.
- Parent-side gate: `git -C <wt> status --short` and the same in the main checkout, `$(cd "$(git rev-parse --path-format=absolute --git-common-dir)/.." && pwd)` also from a worktree.

## Traps

- `git -C ../yaat-server` from a worktree walks up to the nearest `.git` and silently operates on yaat; resolve `$SERVER` from the main checkout first (the recipe is in `prepare-release`).
- A partial commit stashes the rest of yaat; if yaat-server's on-disk code depends on a stashed `Yaat.Sim` change, prek's build hook fails with `CS0246` from the sibling — stash yaat-server first (CLAUDE.md, "prek's build hook is cross-repo").
- `dotnet format`: never bare, never `-v q`; CSharpier formats nothing under a dot-directory worktree, so format in the main checkout after the patch is applied.

## Concurrency

- Worktrees: `git worktree add ../yaat.wt/<slug> -b <slug> main` from the main checkout; `../yaat-server` is the sibling of the main checkout, so a worktree session resolves it from there (see `prepare-release`'s `$SERVER` resolution).
- Ceiling: three implementers. Every `Yaat.Sim` ship runs the cross-repo gate on `main`, and those serialize there anyway.
- Context: the figure the status bar shows is read at every landing from the session's `<scratchpad>/statusline.json` (the user-level CLAUDE.md's "Context usage is measured" rule; user, 2026-09-21), and past 50% the user-level `nextup` stops refilling: the slot a landing frees stays empty, an exploration already running is written into the plan, and the checkpoint follows the in-flight items.
- Depends on, where yaat's file lists hide it: a `Yaat.Sim` signature, `AircraftState` field or `CanonicalCommandType` one item adds and another item's client or yaat-server work consumes; a hub method or `AircraftUpdated` field (`docs/training-hub-contract.md`) one item adds and another renders; two items that each change the snapshot schema (`SnapshotSchemaMigrator` orders the migrations).

## Docs map

| What changed | Owning docs |
|---|---|
| Any command added, renamed, aliased or changed | `COMMANDS.md`, `docs/command-cheatsheet.json` → `node tools/build-cheatsheet.mjs` |
| Anything an instructor or RPO sees or does | `USER_GUIDE.md` (screenshots via `tools/Yaat.GuideCapture`, run separately); `SOLO_TRAINING.md` for solo mode |
| A file added, moved or removed; a subsystem's shape | `docs/architecture.md` (the `architecture-updater` agent), the subsystem doc CLAUDE.md lists for it |
| A hub method or `AircraftUpdated` field | `docs/training-hub-contract.md` |
| Everything user-visible | `CHANGELOG.md` under the unreleased heading, one bullet per change |

## Landing

- Orchestrator writes docs, the changelog bullet and the MAIN.md line removal **in the worktree** (fast-forward it onto `main` first when `main` moved), commits there, then `ship`: land, gate on `main` when it was a real cherry-pick, push both repos, close the issue with an audit comment, `git worktree remove` + `git branch -d` after `git merge-base --is-ancestor`.
- The main checkout hosts at most one implementer, and none while a gate runs there.
