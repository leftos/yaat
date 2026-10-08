---
name: yaat-nextup
description: Profile for the user-level `nextup` skill in the yaat / yaat-server repos — loaded by `nextup` at its step 0 for this project's plan convention, agents, gates, docs map and landing path. Not a loop of its own; invoke `/nextup`.
---

# yaat profile for `nextup`

The generic loop is the user-level `nextup` skill; this file supplies only what is yaat-specific.

## Plan and tracker

- siblings: ../yaat-server
- linear: yaat
- The plan lives in Linear: every task is a Linear issue in team YAAT, per `~/.claude/docs/plan-operations.md`; `docs/plans/MAIN.md` is its generated snapshot, never edited by hand, and `docs/plans/README.md` says what each design folder holds.

  The open release fences the slice as the user-level CLAUDE.md's "An open release fences the work" says (`vNext+1` never counts); within the fence, the project order below decides. The only items pulled into the release without the owner asking are user submissions (next paragraph) and their blockers.

  A **user submission** is a bug report or feature request from a person other than the owner: a defect they hit or a feature they asked for (a Discord thread, a named reporter, an attached bug bundle or recording), whoever filed it, an agent included. A defect or idea only an agent, a nightly review or an audit found does not count.

  The Discord bot labels `/create-issue` reports `bug`; an unlabelled issue (agent-filed, Linear-synced) is judged from its body. **Triage** places each user submission in `Bug reports and feature requests` and adds it to the open release, and no release is cut while a user-reported bug is open unless the user explicitly agreed to defer it (`prepare-release` Step 0d).

  **An item that blocks one in the open release joins it too**: whenever an **add**, a **split** or a ruling makes an open issue wait on another (a sub-issue of a release item, a tool or fix the release item needs first), add the blocker to the open release and record the relation (`save_issue` with `blocks: ["<the release item>"]`), so the release's issue list shows everything it waits on.

  Adding to the open release is `linear release add yaat <ID>...` (`--next` for `vNext+1`, the release after, which never fences work); never the MCP's `addReleases: ["vNext"]`, since every pipeline names its open release `vNext` and the name is ambiguous. `vNext` and `vNext+1` are standing placeholders that keep their ids across cuts: `linear release complete` moves what shipped into a new release named after the version, and moves `vNext+1`'s issues into `vNext`.

  Project order, which is the order the queue is worked: `ERAM release gate` (ERAM is the #1 priority, ahead of every other bug report), `Bug reports and feature requests`, the feature projects (`Client driver in the background (#474)`, `Context-menu quick commands (#471)`, `Client surfaces redesign`, `Follow on the taxi graph (feat/follow-on-graph)`).

  After them come `Tick-path unification` (the current programme), the background programmes (`Say again`, `Taxi append`, `Programmes`; they release with a non-hotfix), `STT tuning`, the waves `Wave 1 — Ground realism and braking` through `Wave 9 — Docs and repo hygiene` (a wave is one release-sized bundle sharing files and a review gate, its record in the project's content), `Singles`, then `Backlog`.
- **Sizzle reels** (`CONTEXT.md`, "Releases"): when a major user-visible feature is planned into the open release or starts there, **add** a reel item for it in the same release (a sub-issue of an existing reel when one covers the area, as the ground FOLLOW clips are of YAAT-7), so the reel is scripted while the feature is fresh, not at the cut. `prepare-release` Step 5d reviews the release for any feature still without one.
- `../yaat-server` has no plan or changelog of its own: team YAAT plans it, and its `docs/plans/live-traffic-swim/` is linked from the issues that use it.
- Pre-loop hook: **trade client-driver lessons with the other projects.** Before the queue is read, run its status check from the main checkout: `uv run --no-project ~/.claude/skills/conventions-sync/scripts/conventions_sync.py status --stack driving`. Exit 0 (`in-sync`) is the whole hook: say so in one line and do not load the skill. Otherwise load the user-level `conventions-sync` skill and run it with `--stack driving`. When it changed anything, `docs/DRIVING_CONVENTIONS.md` and `docs/.conventions-sync-driving.json` land on main as their own `docs:` commit, staged by name, before the first worktree is cut; the skill commits its side of `~/.claude` itself.
- An item **land**s after its commit; finished subplans are deleted (git history is the archive). Review findings the item does not fix get an **add**, in the project that shares their files, else in `Backlog`.
- Tracker: **triage** as plan-operations says (GitHub issues reach the team through Linear's sync and arrive in Triage), each placed in the project that shares its files, using `triage-open-issues` for the reading. Commits in either repo cite a GitHub issue as `Refs https://github.com/leftos/yaat/issues/N`, never with a closing keyword (`Closes`, `Fixes`, `Resolves`): GitHub closes a yaat issue on push when a yaat commit says `Closes #N`, ahead of the **land**, and the sync then reopens it.
- Pull requests: `gh pr list --repo leftos/yaat --state open --json number,title,author`, and the same with `--repo leftos/yaat-server`. A PR is planned when its title, body or branch names an issue id; an unplanned one gets an **add**, its review and landing: a person's PR in `Bug reports and feature requests`, a bot's dependency bump in `Backlog`, each naming the files, whether the checks pass and whether it merges cleanly.
- Hotspots (3,000–4,000 lines each; two items touching one wait on each other): `PatternCommandHandler.cs`, `MainViewModel.cs`, `CommandParser.cs`, `CommandDispatcher.cs`, `MainWindow.axaml.cs`, `GroundCommandHandler.cs`.

## Agents and gates

- Explore: the user-level `Explore` (docs-first). Domain ruling: `aviation-sim-expert` with the local-FAA-references preamble from CLAUDE.md — `aviation-review-gate` decides when it is owed. Attachments: `multimodal-looker` (a Discord "*[empty message]*" is a forward; files sit under `message_snapshots`).
- Reviewers: `csharp-reviewer` for anything beyond a one-file change; `aviation-sim-expert` for aviation behaviour.
- Brief shape: `test-fix` (red test first, real navdata, never synthetic). Every `dotnet` command wrapped as `pwsh tools/gate.ps1 -Log .tmp/<name>.log -TimeoutSeconds <seconds> -Slot heavy -- <command…>` (heavy for every yaat call, a filtered test run included since it builds first), 30 on filtered test runs (a ceiling on the gate's load-adjusted clock; `CLAUDE.md` "Build after edits" says how to read a `STALLED`, `TIMED OUT` or `BACKSTOP` kill), `-p:TreatWarningsAsErrors=true` on builds.

  A `Yaat.Sim` change is proved by `pwsh tools/test-all.ps1` (both repos) before it lands. `dotnet format style … --include` takes paths relative to the working directory: absolute paths match nothing and the check passes on an empty set.
- Parent-side gate: `git -C <wt> status --short` in both halves of the pair, and the same in both main checkouts: yaat's is `$(cd "$(git rev-parse --path-format=absolute --git-common-dir)/.." && pwd)` also from a worktree, and yaat-server's is its sibling.

## Traps

- `git -C ../yaat-server` from a lone yaat worktree (no pair beside it) walks up to the nearest `.git` and silently operates on yaat; resolve `$SERVER` from the main checkout first (the recipe is in `prepare-release`).
- A partial commit stashes the rest of yaat; if yaat-server's on-disk code depends on a stashed `Yaat.Sim` change, prek's build hook fails with `CS0246` from the sibling — stash yaat-server first (CLAUDE.md, "prek's build hook is cross-repo").
- `dotnet format`: never bare, never `-v q`; CSharpier formats nothing under a dot-directory worktree, so format in the main checkout after the patch is applied.

## Concurrency

- Worktrees: every item gets a pair, `../yaat.wt/<slug>/yaat` and `../yaat.wt/<slug>/yaat-server`, both on branch `<slug>` from `<base>` (from the yaat main checkout: `git worktree add ../yaat.wt/<slug>/yaat -b <slug> <base> && git -C ../yaat-server worktree add "$(cd .. && pwd)/yaat.wt/<slug>/yaat-server" -b <slug> <base>`, where yaat-server uses its `main` when it has no `<base>`).

  Then `branch.<slug>.base` / `branch.<slug>.landOn` are recorded in each half as the user-level `nextup` §3 **Base and target** says.

  The pair mirrors the main layout, so yaat-server's `../yaat/src/Yaat.Sim` reference, `tools/Yaat.GuideCapture`'s reference to the server and `tools/test-all.ps1`'s default `-ServerDir` all resolve inside it, and `/ship` (with `## Ship` below) lands it as a paired session. A yaat-only item still gets both halves, since its `Yaat.Sim` gate builds the server.
- Ceiling: three implementers. Every `Yaat.Sim` ship runs the cross-repo gate on `main`, and those serialize there anyway.
- Context: the figure the status bar shows is read at every landing with `bash ~/.claude/tools/context-usage/context-usage.sh` (the user-level CLAUDE.md's "Context usage is measured" rule; never a scratchpad path taken from a task's output, which after `/clear` names an earlier session), and past 40% the user-level `nextup` stops refilling: the slot a landing frees stays empty, an exploration already running is written into the plan, and the checkpoint follows the in-flight items.
- Depends on, where yaat's file lists hide it: a `Yaat.Sim` signature, `AircraftState` field or `CanonicalCommandType` one item adds and another item's client or yaat-server work consumes; a hub method or `AircraftUpdated` field (`docs/training-hub-contract.md`) one item adds and another renders; two items that each change the snapshot schema (`SnapshotSchemaMigrator` orders the migrations).

## Docs map

| What changed | Owning docs |
|---|---|
| Any command added, renamed, aliased or changed | `COMMANDS.md`, `docs/command-cheatsheet.json` → `node tools/build-cheatsheet.mjs` |
| Anything an instructor or RPO sees or does | `USER_GUIDE.md` (screenshots via `tools/Yaat.GuideCapture`, run separately); `SOLO_TRAINING.md` for solo mode |
| A file added, moved or removed; a subsystem's shape | `docs/architecture.md` (the `architecture-updater` agent), the subsystem doc CLAUDE.md lists for it |
| A hub method or `AircraftUpdated` field | `docs/training-hub-contract.md` |
| A client-driver lesson (the MCP server, the automation pipe, how agents drive the client) | `docs/client-driver-mcp.md`, and the rule in `docs/DRIVING_CONVENTIONS.md` |
| A yaat-server file added, moved or removed; a server subsystem's shape | yaat-server `docs/architecture.md` |
| A CRC wire DTO or MessagePack layout | yaat-server `docs/crc-wire/` (regenerated per `docs/crc-update.md`), yaat `docs/crc-display-state.md` |
| A sidecar field or `Data/ARTCCs` category, a GeoJSON property YAAT reads, or a vNAS ARTCC-config/scenario field YAAT reads | `ARTCC_CUSTOMIZATION.md` (synced to Discord), `src/Yaat.Sim/Data/ARTCCs/README.md` for sidecar schemas |
| Live traffic / SWIM behaviour | yaat-server `docs/plans/live-traffic-swim/` (the subplan the item cites), yaat `docs/live-traffic.md` |
| Everything user-visible | `CHANGELOG.md` under the unreleased heading, one bullet per change |

## Landing

- Orchestrator writes docs and the changelog bullet **in the worktree**, commits there with a `Refs: YAAT-<n>` trailer per issue, then `/ship` (with `## Ship` below).

  `/ship` does this: land `base..<slug>` onto the recorded `landOn` (`ship` Phase 2), gate there when it was a real cherry-pick, push yaat (public) at once and leave yaat-server (private with workflows, so metered: user-level `CLAUDE.md`, "A metered repo is pushed only at checkpoints") for the `/nextup` checkpoint push, yaat first then yaat-server as `ship.md`'s push order says, post the audit comment on the GitHub issue and leave it open (it stays Landed until `linear release complete` moves it to Done, which closes it through the sync). Then in each repo remove its half of the pair and its branch once landed, by the user-level `nextup` §4 step 6 check.
- An item under a feature marker (`branch: feat/<name>`, user-level `nextup` §3 "Feature branches") lands the same way onto the feature pair (`../yaat.wt/feat-<name>/yaat` and `/yaat-server`), whose `landOn` is `feat/<name>`; `/ship` then pushes yaat's feature branch and watches its feature PR without merging it; yaat-server's `feat/<name>` is pushed at its opening push, at the merge and at the `/nextup` checkpoint, never per landing (metered). The item is **land**ed with the note `on feat/<name>, ships with #N`; the project's `Merge feat/<name> (…)` tracking issue is landed when `/ship` Phase 2F merges the feature PRs.
- The main checkout hosts at most one implementer, and none while a gate runs there.

## Changelog

Read by the user-level `changelog-and-commit`: the rules live in [`changelog.md`](changelog.md) beside this file. Read that file whole before Step 0; `nextup` never needs it.

## Ship

Read by the user-level `ship`: the rules live in [`ship.md`](ship.md) beside this file. Read that file whole before Phase 0; `nextup` never needs it.
