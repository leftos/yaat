---
name: nextup
description: "Use when the user says \"next up\", \"what's next\", \"next up on main.md\", \"push through the plan\", \"clear the bug list\", or invokes /nextup — a work session that starts from docs/plans/MAIN.md with no item named, especially when several items are queued and the goal is to land as many as possible today."
---

# Next up

Turn the top of `docs/plans/MAIN.md` into a pipeline: every queued item is being explored, built, or shipped at all times, and each lands as its own hotfix the moment it is green. The orchestrator never idles on one item while others sit unexplored, and never holds finished work back to batch it.

**REQUIRED SUB-SKILLS:** `parallel-worktree-agents` (before the first worktree dispatch), `ship` (per item), `aviation-review-gate` (per aviation item). `test-fix` is the shape of every implementer brief.

## 1. Orient (5 minutes, read-only)

1. Read the first list in MAIN.md (**Bug reports and feature requests**; **Backlog** when it is empty).
2. `gh issue list --repo leftos/yaat --state open --json number,title,createdAt` — an issue the plan lacks outranks the plan; fold it in (`triage-open-issues`) before choosing.
3. Build the candidate table, one row per item:

| Column | What goes in it |
|---|---|
| Size | one-site fix · multi-file · design (a list of sub-ideas, an open question for the user) |
| Files | the source files the fix touches; mark any 3,000+ line hotspot MAIN.md names |
| Needs before a brief | nothing · `yaat-explore` map · `aviation-sim-expert` design consult · photos/videos via `multimodal-looker` |
| Cluster | items whose "Files" or subsystem doc overlap — they share one exploration prompt |

A *design* item is not started here: it gets one `AskUserQuestion` when its turn comes, and the others keep moving.

## 2. Fan out every exploration now

Dispatch, in one message, one read-only agent per cluster that is not yet understood — `yaat-explore` for code maps, `aviation-sim-expert` for a ruling with citations, `multimodal-looker` for attachments (a Discord "*[empty message]*" is a forward; its files sit under `message_snapshots`). Do this before the first implementer, not when each item's turn arrives: exploration is cheap, runs in the background, and the report is waiting when the item is promoted.

## 3. Schedule

- The first bounded item starts immediately; the rest start as their exploration lands.
- Items with disjoint file sets run concurrently. Items sharing a hotspot file, or a file another running implementer owns, wait.
- Each concurrent implementer gets its own tree: `git worktree add X:/dev/yaat.wt/<slug> -b <slug> main`. Not `Agent({ isolation: "worktree" })` — its cwd is unreliable. The main checkout hosts at most one implementer, and none while a gate runs there.
- Three concurrent implementers is the practical ceiling: every `Yaat.Sim` ship runs the cross-repo gate on `main`, and those serialize there anyway.
- While one item is in its build/test loop, the next explored item gets its brief written. An orchestrator waiting on a notification with an unbriefed, explored item in the table is behind.

## 4. Per item

1. Brief → `implementer` (red test first, files, proving command).
2. Parent-side gate: `git -C <wt> status --short` and `git -C X:/dev/yaat status --short` (no strays).
3. Review the diff; `csharp-reviewer` for anything beyond a one-file change, `aviation-sim-expert` for aviation behaviour. Corrections go back to the same implementer with `SendMessage`; the orchestrator does not fix source inline.
4. Orchestrator writes the docs, `CHANGELOG.md` bullet, `COMMANDS.md`/`USER_GUIDE.md` and the MAIN.md line removal **in the worktree** (fast-forward the worktree onto `main` first when `main` moved).
5. Commit in the worktree, then `ship` it: land, gate on `main` when it was a real cherry-pick, push, close the issue with an audit comment, `git worktree remove` + `git branch -d`.
6. Findings the reviews raised but the item does not fix become Backlog lines in MAIN.md in the same commit.

Ship each item as it lands. Holding three green items to "ship in one go" is the failure this skill exists to prevent: a hotfix release can only carry what is on `origin/main`.

## Stop conditions

- An item that needs a user decision: ask once, at the moment it blocks, with the options ranked; keep every other item moving.
- A queued item the code shows already fixed: delete its line, say so, move on.
- The user asks for the release: finish the item in flight, then `/prepare-release` is theirs to invoke.

## Red flags

| Thought | What it costs |
|---|---|
| "I'll serialize these, it's simpler" | Two hours of one implementer's wall clock while another tree sits idle |
| "I'll explore #2 when I get to it" | The explorer's ten minutes land on the critical path instead of in the background |
| "Ship all three at the end" | Nothing is on `origin/main` when the release is cut |
| "I'll patch the reviewer's finding myself" | Two writers in one worktree; the implementer's next report edits over yours |
| "`isolation: worktree` is fine" | Edits land in the main checkout while a gate runs there |
