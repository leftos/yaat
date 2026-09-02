---
name: land-bot-pr
description: "Land a nightly-review bot fix PR end to end: find the PR linked to an issue, take it out of draft, rebase its stale pinned base onto current main, add the changelog bullet, gate it, and merge it with `--merge` (never `--rebase`). Use when the user says 'land the bot PR', 'merge #N', 'review and land that PR', when a `nightly-review/<date>-<slug>` branch name appears, or when `gh issue view` shows a PR already linked to the issue you were about to implement from scratch."
---

# Land a Bot-Filed Fix PR

The nightly-review bot files an issue **and** a fix PR together, on a
`nightly-review/<date>-<slug>` branch (e.g. #333 ↔ #334, #271 ↔ #272). Landing
one hits the same six traps every time. Each step below exists because one of
them cost a session.

**Before implementing anything from an issue, check whether a PR is already
attached.** An issue number is a pointer into a tracker, not a specification.

## Step 1: Find the linked work

```bash
gh issue view <N> --repo leftos/yaat --json number,title,closedByPullRequestsReferences --jq '{number,title,linked:[.closedByPullRequestsReferences[].number]}'
gh pr list --repo leftos/yaat --search "<N>" --state all --json number,title,headRefName --jq '.[] | "\(.number) \(.headRefName) \(.title)"'
```

If nothing is linked, this skill does not apply — implement with `test-fix`.
If a PR is linked, review, verify and land **it**; re-implementing duplicates
work and collides with the PR branch.

## Step 2: Read the PR's real shape

```bash
gh pr view <PR> --repo leftos/yaat --json number,isDraft,state,baseRefName,headRefName --jq '{number,isDraft,state,base:.baseRefName,head:.headRefName}'
gh api repos/leftos/yaat/pulls/<PR> --jq '.base.sha'
```

**Trap 3 — the commit list lies.** GitHub pins `base.sha` at PR-creation time
and never refreshes it when `main` moves. `gh pr view --json commits` therefore
lists every commit between the *old* base and head, including commits already on
`main`. Measure the staleness rather than reading the list:

```bash
git -C X:/dev/yaat rev-list --count <base.sha>..main      # how far main has moved since
git -C X:/dev/yaat merge-base --is-ancestor <base.sha> main && echo "base is on main"
```

(On #334 that count was 273. The alarming diff is cosmetic.)

## Step 3: Take it out of draft

**Trap 1 — bot PRs open as drafts,** and `gh pr merge` fails on one with
`GraphQL: Pull Request is still a draft`.

```bash
gh pr ready <PR> --repo leftos/yaat
```

## Step 4: Inspect the changes without checking anything out

**Trap 4 — never `git checkout <pr-branch> -- <file>`.** It takes the file
*wholesale* and silently reverts every newer `main` commit to it. On #282 that
would have wiped `3b771ca8`'s `FindPhaseGateDriverIndex`/`ApplyParallelSibling`.
Diff and 3-way apply instead — check first, then apply:

```bash
git -C X:/dev/yaat fetch origin pull/<PR>/head:pr-<PR>
git -C X:/dev/yaat diff <base.sha> pr-<PR> -- <file> | git apply --3way --check -
git -C X:/dev/yaat diff <base.sha> pr-<PR> -- <file> | git apply --3way -
```

The `--check` run is a dry run: it reports whether the patch lands cleanly and
touches nothing. Read the whole diff before landing it — a bot PR is a proposal,
and CLAUDE.md's aviation review still applies to anything that changes
behaviour (see the `aviation-review-gate` skill).

## Step 5: Fast-forward-push `main` BEFORE rebasing

**Trap 5 — `origin/main` trails local `main`,** sometimes by dozens of commits
(work lands on local `main` across worktrees; pushes happen around releases).
Rebasing the PR onto a local `main` that is ahead of origin drags your unpushed
commits into the PR, and GitHub will show — and merge — them.

```bash
git -C X:/dev/yaat rev-list --left-right --count origin/main...main   # left=origin-only, right=local-only
```

If the right-hand number is non-zero, push before rebasing:

```bash
git -C X:/dev/yaat push origin main
```

**Trap 6 — inside a worktree, `git checkout main` fails**
(`already used by worktree at X:/dev/yaat`), and a following
`git merge --ff-only origin/main` then silently fast-forwards *the current
branch* instead. Never rely on `checkout main` from a worktree; address the
primary checkout explicitly with `git -C X:/dev/yaat …`, as every command in
this skill does.

## Step 6: Rebase the PR onto main, THEN add the changelog

**Trap 2 — the base can be releases stale.** #282 was branched at the
`v0.9.4-beta` tag, 14 commits and two releases behind `main`, and its
`CHANGELOG.md` had no `## Unreleased` section at all. Adding a bullet on that
branch guarantees a conflict — both sides insert different content directly
under `# Changelog`.

Order matters: **rebase first, add the bullet second.**

```bash
git -C X:/dev/yaat fetch origin
git -C X:/dev/yaat rebase main pr-<PR>
```

Code hunks usually rebase clean; verify rather than assume (Step 4's
`git apply --3way --check` on any hunk you are unsure of). Then add the
changelog commit with the `changelog-and-commit` skill, and push the rebased
branch:

```bash
git -C X:/dev/yaat push --force-with-lease origin pr-<PR>:<head-branch>
```

## Step 7: Gate it

The bot's tests are its own claim, not your verification. Run the repo gate in
the primary checkout — and use `tools/gate.sh`, because a teed pipeline reports
the status of its last stage and would read a failed build as green:

```bash
cd X:/dev/yaat
tools/gate.sh .tmp/build.log dotnet build -p:TreatWarningsAsErrors=true
tools/gate.sh .tmp/test-all.log pwsh tools/test-all.ps1
```

`test-all.ps1` is the right gate here rather than a bare `dotnet test`: a bot fix
in `Yaat.Sim` can break the sibling yaat-server repo, which yaat's own suite
cannot see.

## Step 8: Merge with `--merge`, never `--rebase`

**Consequence of trap 3:** `gh pr merge --rebase` makes GitHub replay the
commits it *thinks* belong to the PR — creating duplicate SHAs of commits
already on `main`.

```bash
gh pr merge <PR> --repo leftos/yaat --merge --delete-branch
```

`--merge` is safe: the parents are `main`'s tip and the PR head, the tree is
correct, nothing is duplicated. `main` already carries merge commits, so one
more is not out of place despite the mostly-linear history.

## Step 9: Close the loop

```bash
git -C X:/dev/yaat fetch origin && git -C X:/dev/yaat merge --ff-only origin/main
gh issue view <N> --repo leftos/yaat --json state --jq .state
```

If the PR body carried `Closes #N`, the merge closed the issue; otherwise close
it with a comment naming the merged PR.

**Superseded PRs.** If a cloud-agent or bot draft PR duplicates work that has
already landed on `main` under different SHAs, do not merge it — close it as
superseded, referencing the commit that landed:

```bash
gh pr close <PR> --repo leftos/yaat --comment "Superseded by <sha> on main."
```

## What has been exercised

The read-only commands here (`gh issue view`, `gh pr list`, `gh pr view`,
`gh api … .base.sha`, `git rev-list`, `git merge-base --is-ancestor`,
`git diff … | git apply --3way --check -`) were run against real data on this
repo — #333 ↔ #334, whose pinned `base.sha` sits 273 commits behind `main`.

The mutating ones (`gh pr ready`, `git fetch origin pull/N/head:…`,
`git rebase`, `git push --force-with-lease`, `gh pr merge --merge`,
`gh pr close`) have not been run from this file, because doing so needs a live
draft bot PR. Their *shapes* come from sessions that landed #272 and #282. Read
the flags before running one, and prefer the `--check`/`--dry-run` form first
where the command has one.

## Checklist before merging

- [ ] The PR was found from the issue, not assumed absent.
- [ ] `gh pr ready` ran (drafts cannot merge).
- [ ] `base.sha` staleness measured, not read off the commit list.
- [ ] Every file inspected by `git diff … | git apply --3way`, never by
      `git checkout <branch> -- <file>`.
- [ ] `origin/main` was fast-forwarded from local `main` **before** the rebase.
- [ ] Rebase happened before the changelog bullet was added.
- [ ] `tools/gate.sh` build + `test-all.ps1` both green.
- [ ] Merged with `--merge`. Not `--rebase`, not `--squash`.
