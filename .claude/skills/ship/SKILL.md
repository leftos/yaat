---
name: ship
description: "End-to-end release of the current session's work: changelog + commit, land onto `main` in both repos, push, then close the related GitHub issue(s). Trigger when the user says \"ship it\", \"ship this\", \"ship the session\", \"land and push\", \"push and close the issue\", or invokes /ship. Composes /changelog-and-commit → /merge-session-to-main → push → `gh issue close`. Invoking the skill IS the approval — it pushes to origin/main and closes issues without further prompts."
---

# Ship

One command to take finished session work all the way out the door:

1. **Changelog + commit** — `/changelog-and-commit`
2. **Land onto `main`** — `/merge-session-to-main`
3. **Verify `main`** — build gate when the landing was a real cherry-pick
4. **Push** both repos to `origin`
5. **Close** the related GitHub issue(s), including the ones a push can't auto-close

Invoking `/ship` **is** the approval for every step, including `git push` to `origin/main` and `gh issue close`. Announce each phase as you enter it so the user can interrupt, but never stop and ask. This deliberately overrides the global "never auto-commit" rule and `/merge-session-to-main`'s "does NOT push" rule — `/ship` is the authorization those two skills are missing.

## Composition, not duplication

Phases 1 and 2 are the existing skills, invoked with the **Skill** tool (`changelog-and-commit`, then `merge-session-to-main`). Follow their instructions as written; do not re-derive or paraphrase their logic here. This file only covers what `/ship` adds on top: the sequencing, the tolerance rules for no-op phases, the push, and the issue close.

The one place `/ship` **overrides** a sub-skill: `/merge-session-to-main` ends with "Do not push as part of this skill, even if the user said 'merge and push' — confirm separately." `/ship` is that separate confirmation. When that skill reports its final "not pushed" state, continue to Phase 3 rather than stopping.

## Phase 0: Orient

Establish, before invoking anything:

```bash
cwd=$(pwd)
git -C "$cwd" rev-parse --show-toplevel
git -C "$cwd" branch --show-current
git -C "$cwd" status --porcelain
git -C "X:/dev/yaat-server" branch --show-current
git -C "X:/dev/yaat-server" status --porcelain
```

Record three facts that drive which phases are no-ops:

- **In a worktree or in `X:/dev/yaat` itself?** If the toplevel is `X:/dev/yaat` and the branch is `main`, Phase 2 has nothing to land — the commits are already on `main`.
- **Cross-repo?** Does `X:/dev/yaat-server` have uncommitted work or local-only commits vs its `main`?
- **Anything uncommitted?** If both trees are clean, Phase 1 is a no-op.

Then collect issue candidates *now*, while the branch name and pre-landing commit list are still easy to read — see Phase 5. Doing it after the cherry-pick means digging through rewritten SHAs.

**Halt in Phase 0 only if** the working tree contains a secrets file (`.env`, `*.pem`, `*.key`, `id_rsa*`, `*credentials*`), or a repo has an in-progress cherry-pick/merge/rebase. Everything else is handled downstream.

## Phase 1: Changelog and commit

Invoke the `changelog-and-commit` skill. It snapshots the index, drafts bullets from the working-tree diff, writes `CHANGELOG.md`, and commits — per-repo, yaat-server first when the work is cross-repo.

**Tolerance rule:** that skill halts with "nothing to commit" when both trees are clean. Under `/ship` that is a **no-op, not a failure** — say `Phase 1: skipped (working trees clean)` and continue to Phase 2. Only a real failure (hook failure, secrets file, dirty state it can't resolve) stops `/ship`.

If a pre-commit hook fails: surface the output, fix forward, new commit. Never `--amend`, never `--no-verify`. If the fix isn't obvious, stop `/ship` here — nothing has been pushed yet, so stopping is cheap.

## Phase 2: Land onto main

Invoke the `merge-session-to-main` skill. It cherry-picks the session's commits from the worktree branch onto `main` in both checkouts and auto-resolves the usual `CHANGELOG.md` `## Unreleased` bullet conflicts.

**Tolerance rule:** if Phase 0 established the session is already on `main` in `X:/dev/yaat`, that skill halts by design. Say `Phase 2: skipped (session is on main already)` and continue to Phase 3.

**Landing-order footgun (cross-repo signature changes).** When the session changed a `Yaat.Sim` signature that yaat-server calls, landing yaat first deadlocks: yaat's prek build hook compiles `yaat.slnx`, which includes the sibling yaat-server project *from disk*, and yaat-server's `main` still has the old call site. Land **yaat-server first** — `git merge --ff-only <branch>` creates no commit, so it runs no hooks at all — then resume the paused yaat cherry-pick, whose hook build now sees the updated call site. `/merge-session-to-main` documents yaat-first; that ordering is wrong for this case.

Stop `/ship` if the cherry-pick pauses on a conflict outside the auto-resolvable CHANGELOG pattern. Report the conflicted files and leave the cherry-pick paused for the user.

## Phase 3: Verify main before pushing

A cherry-pick that merges textually clean can still fail to compile on `main`: `main` may have gained files since the branch diverged that call the old signature, and the worktree's green suite cannot see them.

Decide the gate from what Phase 2 actually did, in `X:/dev/yaat`:

- **Fast-forward only, both repos** → skip. `main` didn't advance past the base, so the worktree's build already covered this tree.
- **Any real cherry-pick** → run in the target checkout:
  ```bash
  cd X:/dev/yaat && dotnet build -p:TreatWarningsAsErrors=true 2>&1 | tee .tmp/ship-build.log
  ```
- **Cherry-pick touching a `Yaat.Sim` type or method signature** → also run the cross-repo suite:
  ```bash
  cd X:/dev/yaat && pwsh tools/test-all.ps1 2>&1 | tee .tmp/ship-test-all.log
  ```

If the gate fails, **do not push**. Fix forward with a new commit on `main` (never `--amend`), re-run the gate, then continue. If the fix isn't obvious, stop and surface the log — `main` is local-only at this point, so it's recoverable.

## Phase 4: Push

Announce, then push in the same turn:

```
Phase 4: pushing
  X:/dev/yaat        main  <N> commits → origin/main
  X:/dev/yaat-server main  <M> commits → origin/main
```

**Push yaat first, then yaat-server.** yaat-server's CI bumps its `extern/yaat` submodule pointer to a yaat commit; if yaat isn't on `origin` yet, that bump references an object GitHub doesn't have. (Never bump the submodule pointer by hand — CI owns it.)

```bash
git -C X:/dev/yaat push origin main
git -C X:/dev/yaat-server push origin main
```

Plain `git push` only. **Never** `--force`, `--force-with-lease`, or `--tags`. Skip a repo with nothing ahead of `origin/main` and say so.

`origin/main` normally trails local `main` by a lot — the user commits locally across parallel worktrees and pushes occasionally. A push that carries dozens of unrelated commits is **expected**, not a red flag; report the count and move on. Tag pushes belong to `/prepare-release`, not here.

**If a push is rejected** (non-fast-forward — something advanced `origin/main` mid-session), classify the incoming commits before doing anything else:

```bash
# Every file touched by commits we don't have yet, minus the CI submodule pointer.
git -C <repo> fetch origin
git -C <repo> rev-list main..origin/main | while read -r c; do
    git -C <repo> show --pretty="" --name-only "$c"
done | sort -u | grep -v '^extern/yaat$'
```

- **Empty output — the incoming commits touch nothing but `extern/yaat`.** This is yaat-server's CI bumping its submodule pointer, which it does on every yaat push; it is not a human editing the same code. Rebase and continue without asking:
  ```bash
  git -C <repo> rebase origin/main && git -C <repo> push origin main
  ```
  Our commits never touch `extern/yaat` (CI owns that pointer), so the replay cannot conflict. Report it as a one-line note, not a blocker. If the rebase stops anyway, abort it (`git rebase --abort`) and fall through to the halt case.
- **Anything else in the output** — a real commit landed on `origin/main`. Halt. Do not force, do not rebase. Report the rejection and let the user decide.

Either way: anything already pushed stays pushed; say which repos landed.

## Phase 5: Close the issue(s)

All GitHub issues live on **`leftos/yaat`**, regardless of which repo the fix landed in. This phase exists because two common cases leave an issue open after a successful push:

- The fix is in **yaat-server**, so GitHub's auto-close never sees a `Closes` reference in the `leftos/yaat` repo.
- The commit message **forgot** the `Closes` trailer.

### Gathering candidates

Union of three sources, collected in Phase 0 before SHAs were rewritten:

1. **Commit trailers** — scan the landed commits in both repos for `Closes #N`, `Closes https://github.com/leftos/yaat/issues/N`, `Fixes #N`, or a bare `#N`:
   ```bash
   git -C X:/dev/yaat log --format=%B "$base..main"
   git -C X:/dev/yaat-server log --format=%B "$base_s..main"
   ```
2. **Branch name** — parse the session branch for an issue number: `issue-291-cross-runway`, `fix/291-...`, `291-taxi-bug`. Ignore `nightly-review/<date>-<slug>` branches, which encode a date, not an issue.
3. **Session context** — any issue number or `github.com/leftos/yaat/issues/N` URL that came up in this conversation. This is the source that covers the yaat-server case, where nothing in git references the issue.

### Verify before closing

For each candidate:

```bash
gh issue view <N> --repo leftos/yaat --json number,title,state,labels
```

- **Already `CLOSED`** → the push auto-closed it. Report `#N already closed by push`, do nothing.
- **`OPEN` and the shipped work plainly resolves it** → comment, then close.
- **`OPEN` but the issue is broader than what shipped** (a tracking issue, a multi-part request where this was one part, or the title doesn't match the changelog bullet) → **do not close**. Post the comment linking the commits, then report it as left open with the reason.

A candidate number that doesn't resolve to a real issue (a `#123` that was a PR reference, a stray number in a branch name) is dropped silently.

### Comment, then close

```bash
gh issue comment <N> --repo leftos/yaat --body "$(cat <<'EOF'
Fixed in:
- leftos/yaat@<sha> — <subject>
- leftos/yaat-server@<sha> — <subject>

<the CHANGELOG bullet(s) written in Phase 1>
EOF
)"

gh issue close <N> --repo leftos/yaat --reason completed
```

The comment carries the audit trail GitHub can't build itself for a cross-repo fix. Use full `owner/repo@sha` form so the yaat-server SHAs render as links from the yaat issue.

**If no candidates were found**, that's a normal outcome — say `Phase 5: no linked issue found` and stop. Never guess at an issue number, and never close an issue you didn't verify with `gh issue view`.

## Phase 6: Report

One block, at the end:

```
Shipped.

  yaat        <sha> — <subject>          pushed (N commits)
  yaat-server <sha> — <subject>          pushed (M commits)

  Changelog:  <bullet>
  Issues:     #291 closed · #285 already closed by push · #300 left open (broader than this fix)

  Source worktree X:/dev/yaat.wt/<branch> untouched — `wt rm <branch>` when ready.
```

Name every phase that was skipped and why, so a skipped phase never reads as a forgotten one.

## Anti-patterns

- **Do not ask for approval at any phase.** Invoking `/ship` is the go-ahead, push and issue-close included. Announcing ≠ gating.
- **Do not push before Phase 3's gate passes.** A cherry-pick onto a diverged `main` can break the build in ways the worktree's green suite never saw.
- **Do not `--force` a rejected push.** Rebase is allowed in exactly one case — the incoming commits touch nothing but `extern/yaat` (CI's submodule bump). Classify first (Phase 4); if anything else landed on `origin/main`, halt and surface it.
- **Do not push tags.** `--tags` can suppress the Release workflow; tagging is `/prepare-release`'s job.
- **Do not push yaat-server before yaat.** Its CI submodule bump would point at an unpushed yaat commit.
- **Do not bump `extern/yaat` by hand** to "help" the submodule along. CI owns that pointer.
- **Do not close an issue you haven't read.** `gh issue view` first, every time. Broader-than-the-fix issues get a comment, not a close.
- **Do not invent issue numbers** from a vague branch name or an unrelated `#N` in a diff.
- **Do not treat a no-op phase as a failure.** Clean tree → Phase 1 skipped. Already on `main` → Phase 2 skipped. Keep going.
- **Do not re-implement Phases 1–2 inline.** Invoke the skills; they own that logic and get updated independently.
- **Do not `--amend` or `--no-verify`** anywhere in the flow, including hook-failure recovery.

## Quick reference

```bash
# Phase 0 — orient (both repos)
git -C X:/dev/yaat            status --porcelain && git -C X:/dev/yaat            branch --show-current
git -C X:/dev/yaat-server     status --porcelain && git -C X:/dev/yaat-server     branch --show-current

# Phase 1 — Skill: changelog-and-commit   (clean tree → skip, not fail)
# Phase 2 — Skill: merge-session-to-main  (on main already → skip; cross-repo sig change → land yaat-server FIRST)

# Phase 3 — gate, only if a real cherry-pick happened
cd X:/dev/yaat && dotnet build -p:TreatWarningsAsErrors=true 2>&1 | tee .tmp/ship-build.log
cd X:/dev/yaat && pwsh tools/test-all.ps1 2>&1 | tee .tmp/ship-test-all.log   # if a Yaat.Sim signature changed

# Phase 4 — push, yaat first, no --force / no --tags
git -C X:/dev/yaat        push origin main
git -C X:/dev/yaat-server push origin main

# ...rejected? classify the incoming commits. Empty output = CI submodule bump only:
git -C <repo> fetch origin
git -C <repo> rev-list main..origin/main | while read -r c; do
    git -C <repo> show --pretty="" --name-only "$c"
done | sort -u | grep -v '^extern/yaat$'
# empty  -> git -C <repo> rebase origin/main && git -C <repo> push origin main   (continue, don't ask)
# else   -> halt, report, let the user decide

# Phase 5 — issues always live on leftos/yaat
gh issue view <N>    --repo leftos/yaat --json number,title,state,labels
gh issue comment <N> --repo leftos/yaat --body "..."
gh issue close <N>   --repo leftos/yaat --reason completed
```
