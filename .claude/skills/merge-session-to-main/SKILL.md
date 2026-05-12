---
name: merge-session-to-main
description: "Land the current Claude session's worktree commits onto `main` in both X:/dev/yaat and X:/dev/yaat-server. Cherry-picks divergent commits, auto-resolves the usual CHANGELOG.md `## Unreleased` bullet conflicts, runs each repo's pre-commit hooks, and stops (never pushes). Use when the user says 'merge to main', 'land this session', 'merge session', or invokes /merge-session-to-main after finishing work in a yaat worktree (typically `X:\\dev\\yaat.wt\\<branch>\\`)."
---

# Merge Session to Main

Land the commits produced by the current Claude session into the main checkouts of yaat and yaat-server. The session typically runs in a yaat worktree (`X:\dev\yaat.wt\<branch>\`) — possibly with sibling commits in `X:\dev\yaat-server\` if the work was cross-repo. This skill brings them home as linear-history cherry-picks.

## When to use

Trigger phrases: "merge to main", "merge session to main", "land this session", `/merge-session-to-main`.

Typical timing: after `/changelog-and-commit` has produced one or more session commits and the user wants them in the main checkout — usually so they can review, push later, or move on to the next worktree.

Per project convention (auto-memory `feedback_commit_to_main`), the user lands work directly on `main` with no PR. Linear history is preferred — this skill cherry-picks rather than producing merge commits.

## What it does NOT do

- **Does NOT push.** Pushing to `origin/main` requires the user's explicit go-ahead. Stop after the local cherry-pick.
- **Does NOT prune the source worktree or branch.** The bug branch / worktree stays intact. The user can `git worktree remove` or `wt rm` separately when they're done.
- **Does NOT auto-stash.** If the source has uncommitted changes, halt — that's lost work waiting to happen.
- **Does NOT amend or use `--no-verify`.** Pre-commit hooks must pass on their own merit.
- **Does NOT rebase the source branch onto main.** That rewrites the source's history. The source is left alone.

## Step 0: Discover the source(s)

The current Claude session's working directory is the yaat-side source. Confirm:

```bash
source_yaat=$(pwd)
git -C "$source_yaat" rev-parse --show-toplevel  # must succeed
source_yaat_branch=$(git -C "$source_yaat" branch --show-current)
```

Halt if:
- `source_yaat` is NOT a git repo
- `source_yaat` resolves to `X:\dev\yaat` itself (no separate worktree — nothing to land)
- `source_yaat_branch` is `main` (already on the target branch)

The yaat-server side has no separate worktree convention — cross-repo sessions edit `X:\dev\yaat-server` in place on its own branch. Check whether it has divergent commits:

```bash
server_dir="X:/dev/yaat-server"
server_branch=$(git -C "$server_dir" branch --show-current)
```

If `server_branch` is `main` and there are no local-only commits relative to a target main (see Step 1), the yaat-server side is a no-op for this skill.

## Step 1: Pre-flight checks

For each potentially involved repo, in this order:

1. **Source yaat working tree must be clean** (`git -C "$source_yaat" status --porcelain` empty). Halt and list dirty paths if not.
2. **Target yaat (`X:/dev/yaat`) must exist, be on `main`, and not have an in-progress operation** (no `.git/CHERRY_PICK_HEAD`, `.git/MERGE_HEAD`, `.git/rebase-merge`, `.git/rebase-apply`). Halt with what's in progress if so.
3. **yaat-server side** (`X:/dev/yaat-server`): if any local-only commits exist relative to its `main`, apply the same checks to its working tree and target. If working tree is dirty but the dirty files are the same files Claude already edited in this session (i.e. uncommitted session work), surface them — they need to be committed first via `/changelog-and-commit` or similar. Don't proceed past dirty trees.

Untracked files in the target (like the existing `.rustling-tulip/` in `X:\dev\yaat`) are fine — they're not in the working tree's modification set.

## Step 2: Compute the cherry-pick range per repo

For each source/target pair (yaat side, then yaat-server side if applicable):

```bash
target_main=$(git -C "$target" rev-parse main)
source_head=$(git -C "$source" rev-parse "$source_branch")
base=$(git -C "$target" merge-base "$target_main" "$source_head")
```

- If `source_head == target_main` → already landed, skip this repo.
- If `base == target_main` → fast-forward possible. Use `merge --ff-only`.
- If `base == source_head` → target is ahead of source; nothing to land, skip.
- Otherwise → diverged. Cherry-pick range `base..source_head`.

List the commits that will land:

```bash
git -C "$source" log --oneline "$base..$source_head"
```

## Step 3: Plan and announce

Print to the user (single message, no question):

```
Landing N commit(s) onto X:/dev/yaat from <source-branch>:
  <sha> <subject>
  ...
Landing M commit(s) onto X:/dev/yaat-server from <server-branch>:
  ...
(Pushing not included — local cherry-pick only.)
```

If a repo has 0 commits to land, state that explicitly so the user knows it wasn't overlooked.

## Step 4: Execute, repo by repo

For each repo with commits to land:

**Fast-forward case:**

```bash
git -C "$target" merge --ff-only "$source_branch"
```

(Works because both source and target are local refs in the same `.git` for the yaat worktree case; for yaat-server the source IS its own working branch, also local.)

**Cherry-pick case (single commit):**

```bash
git -C "$target" cherry-pick <source_head>
```

**Cherry-pick case (multiple commits):**

```bash
git -C "$target" cherry-pick "$base..$source_head"
```

### Conflict handling

The cherry-pick stops with a conflict. The overwhelmingly common case in YAAT is `CHANGELOG.md` — both branches added bullets under `## Unreleased`. Auto-resolve **only** this exact pattern:

1. Read the conflicted `CHANGELOG.md`.
2. Find each `<<<<<<<` / `=======` / `>>>>>>>` block.
3. If both sides of every block consist solely of additions of bullet lines under the same `## Unreleased` (no removals, no heading changes) → produce a merged version that keeps every bullet from both sides, in this order:
   - First the bullets already on the target (top of conflict)
   - Then the bullets from the source (bottom of conflict)
4. Save, `git add CHANGELOG.md`, `git cherry-pick --continue --no-edit`.

For any other conflicted file, or for a CHANGELOG.md conflict that doesn't fit the pattern above, **halt**:
- Print the conflicted files.
- Tell the user the cherry-pick is paused.
- Suggest they resolve manually then re-invoke the skill (which will detect the in-progress state and pick up where it left off — see Step 1 in-progress check), or run `git -C "$target" cherry-pick --continue --no-edit` themselves.

### Pre-commit hooks

Both yaat and yaat-server have `prek` hooks. They run on each cherry-picked commit. If a hook modifies files (csharpier/format), re-stage and `git cherry-pick --continue --no-edit`. If a hook reports a hard failure (build with `-p:TreatWarningsAsErrors=true`, large-file check, private-key check), halt and surface the output — fix forward, do not `--no-verify`.

If the large-file check fails on a recording bundle (`tests/Yaat.Sim.Tests/TestData/*.zip > 8192 KB`), surface the prek comment in `prek.toml` and suggest trimming the bundle (`/changelog-and-commit` has a precedent for this — trim the manifest's `Snapshots` to the time range the test actually uses).

## Step 5: Report

For each repo touched, print one block:

```
X:/dev/yaat: <new-HEAD-sha> — <commit-subject>
  N commits ahead of origin/main (not pushed)
X:/dev/yaat-server: <new-HEAD-sha> — <commit-subject>
  M commits ahead of origin/main (not pushed)
```

If a repo was skipped (no commits to land, or already at the target), say so explicitly:

```
X:/dev/yaat-server: skipped (no session commits on <branch>)
```

Final line: a reminder that the source worktree/branch is untouched and pruning is the user's call:

```
Source worktree at <source_yaat> still on <branch>. Use `wt rm <branch>` or
`git worktree remove <path>` to clean up when you're ready.
```

## Step 6: What the user does next

The user typically:
- Reviews the new main HEAD with `git log -3` in each repo.
- Pushes when ready (`git -C X:/dev/yaat push`, etc.) — separate decision.
- Prunes the worktree.

Do **not** push as part of this skill, even if the user said "merge and push" in the same breath. Confirm separately — pushing to main is shared state.

## Anti-patterns

- **Don't push.** Even if it feels natural to "finish" the merge by pushing, that's a different decision.
- **Don't squash.** The session may have multiple meaningful commits (`fix:`, `test:`, `docs:`); preserve them.
- **Don't rebase the source.** Rewriting the bug branch's history hides what happened.
- **Don't fall back to `git merge --no-ff`.** That produces a merge commit, which the project doesn't use.
- **Don't auto-resolve non-CHANGELOG conflicts.** The CHANGELOG bullet pattern is predictable; everything else needs human judgment.
- **Don't delete the worktree.** Even after a successful merge, the user may want to inspect or amend.
- **Don't run `git -C` against a path you haven't verified exists.** (`feedback_git_C_walks_up_on_missing_path` — git silently walks up to the nearest `.git`.)
- **Don't add `--no-verify`** to bypass hooks. If a hook fails, fix forward.

## Quick reference

```bash
# Discover source (current Claude cwd)
source_yaat=$(pwd)
source_branch=$(git -C "$source_yaat" branch --show-current)

# Targets
target_yaat="X:/dev/yaat"
target_server="X:/dev/yaat-server"
server_branch=$(git -C "$target_server" branch --show-current)

# Compute & execute for yaat
base=$(git -C "$target_yaat" merge-base main "$source_branch")
git -C "$source_yaat" log --oneline "$base..$source_branch"           # plan
git -C "$target_yaat" cherry-pick "$base..$source_branch"             # execute

# Compute & execute for yaat-server (only if its current branch has divergent commits)
if [ -n "$(git -C "$target_server" log --oneline "main..$server_branch" 2>/dev/null)" ]; then
    base_s=$(git -C "$target_server" merge-base main "$server_branch")
    git -C "$target_server" cherry-pick "$base_s..$server_branch"
fi

# Confirm — DO NOT push
git -C "$target_yaat" log -1 --oneline
git -C "$target_server" log -1 --oneline
git -C "$target_yaat" status -sb
git -C "$target_server" status -sb
```
