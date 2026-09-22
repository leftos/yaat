---
name: yaat-changelog-and-commit
description: "Use in the yaat / yaat-server repos whenever the user says \"changelog and commit\", \"log it and commit\", \"update the changelog and commit\", \"changelog + commit\", or invokes /changelog-and-commit — this yaat-specific variant replaces the user-level `changelog-and-commit` skill here (one CHANGELOG in yaat, two repos, cross-repo commits). Derives bullets from the currently uncommitted (staged + unstaged) work only. Writes the changelog and commits without further prompts — invoking the skill IS the approval."
---

# Changelog and Commit (yaat variant)

Invoking this skill is the approval: it drafts the bullets for the work in the working tree right now, writes the file, stages by name and commits, announcing each step and asking nothing. That overrides the global "never auto-commit" rule; the invocation phrase is the go-ahead. Older committed work missing from the changelog is `/update-changelog`'s job, never this skill's, so `git log <baseline>..HEAD` is never a source of bullets.

This is the yaat-specific variant of the user-level `changelog-and-commit` skill, under its own name because a personal skill shadows a same-named project skill; `ship` invokes this one. It carries the generic flow plus the two-repo rules below, and is kept in step with the generic skill's decisions. Worked shapes for bullets, announcements and commit messages are in the generic skill's `reference.md` under `~/.claude/skills/changelog-and-commit/`.

## YAAT-specific: one CHANGELOG, two repos

There is **one** `CHANGELOG.md`, at the yaat checkout root; `..\yaat-server\` has none, and its user-visible changes are logged in yaat's file (several existing entries describe server-only fixes).

- **Detect cross-repo work in Step 0.** Run `git status --porcelain` in *both* trees. The yaat-server tree is the paired sibling of the current yaat checkout (`$(dirname "$(pwd)")/yaat-server`) when that directory is a checkout on the same branch, which is a `wt`-paired worktree session, and the sibling of the main checkout otherwise: `"$(git rev-parse --path-format=absolute --git-common-dir)/../../yaat-server"`, never a hardcoded drive letter, since the repos live on several machines. Naming the main checkout in a paired session reports the server side clean and commits nothing there.
- **Two commits, not one**, since `git commit` is per-repo: first the **yaat-server** commit with the code and test files, prefix from the work (`fix:`, `feat:`, `ref:`); then the **yaat** commit with `CHANGELOG.md` only, prefix `docs:`, subject and body mirroring the bullets. Server first, then the log of it. A yaat-server branch behind `origin/main` after a submodule bump fast-forwards (`git pull --ff-only`) before the commit; no divergent history.
- **Hooks run per repo**: a csharpier or format hook that reformats a file means re-stage and a new commit in that repo, never `--amend`; the other repo's commit is unaffected.
- The Step 5 announcement names both repos and which files go in which commit; the Step 8 report prints both HEADs (`Committed yaat-server@<sha> — <subject>`, `Committed yaat@<sha> — <subject>`, `Working trees: clean / clean`).

A diff fully contained in yaat is a single commit in yaat as usual.

## Step 0: Snapshot the index before anything touches it

```bash
git status -sb > .tmp/changelog-commit-presnapshot.txt
```

The first line names the ref the commit lands on. `## HEAD (no branch)` halts: a detached checkout accepts the commit and a later push does not carry it, and the yaat-server checkout sits detached at `origin/main` after a submodule-style update. Recover with `git checkout -B main <sha>` when `git merge-base --is-ancestor main HEAD` holds, and require `## main...origin/main` before committing. Any branch other than the expected one halts and is named.

Bucket the file list: pre-staged (column 1 is not a space or `?`), unstaged modifications (column 2 set), untracked (`??`). A pre-staged `CHANGELOG.md` counts as not pre-staged. All three empty, in both trees, halts with "nothing to commit".

## Step 1: Scope

Anything pre-staged makes the scope exactly the pre-staged files; the user assembled that index on purpose, and unstaged and untracked work stays out. Nothing pre-staged makes the scope every modified tracked file plus every untracked file, announced in Step 5 rather than asked about. A path that looks like a secrets file (`.env`, `*credentials*`, `*.pem`, `*.key`, `id_rsa*`) in the scope halts the skill; this is the one place it stops and asks.

The bullets describe only this scope, and the commit stages only these files plus `CHANGELOG.md`.

## Step 2: Read the diff and decide what it is

Read the scope's actual content (`git diff --cached -- <paths>` for pre-staged, `git diff HEAD -- <paths>` otherwise, the Read tool for untracked files), never the file list alone. Three classifications the diff does not make for you:

- **Against the emulated upstream, not this codebase's history.** YAAT's specification is "behave like the real tools" (vNAS TDLS, CRC/STARS, ATCTrainer), so the question is whether the upstream already behaves this way: yes makes the gap a defect under `### Fixed` even when the code path is brand new; a YAAT-original convenience the upstream lacks (a Tools-menu shortcut, an instructor-only view) goes under `### Added`. Filing a parity gap under Added misrepresents it to users who know the real tool.
- **Reach from the code, not the plan note.** A plan heading, commit message or steer names what motivated the change, never what it reaches; a milestone-driven fix that lands in shared state reaches every room. Resolve the gating condition and its call sites and word the bullet from that reach (all rooms, solo sessions only, headless/soak only, replay only), saying so when it is broader than the originating context implies.
- **Mixed-shape diff splits into two commits.** Many call sites changed identically plus a small logic change lands as a `ref:` commit of the mechanical part first, with no bullet, verified by build and tests, then the normal flow for the behaviour change. Only when the two share hunks that cannot be separated from the working tree does it commit once, saying so.

**Gate: does the diff warrant a bullet at all?** Planning docs for work not yet built, internal refactors with no behaviour change and test/CI-only diffs take none. Two checks make that a finding: `rg -F "<topic>" CHANGELOG.md` for precedent, and `git log --oneline -- <paths>` for how the same files were treated before. On a no-bullet verdict, leave `CHANGELOG.md` untouched, stage the scope paths only, use the prefix the work carries (`docs:`, `ref:`, `test:`, `ci:`), and state the decision with its two findings in the announcement.

## Step 2b: Reconcile the main plan

List the unchecked items in `docs/plans/MAIN.md` (`rg -n "^\s*- \[ \]" docs/plans/MAIN.md`, plus the subplans it links for the work in scope). Every item this diff resolves, or that a prior commit of this session resolved and that outlived its fix, is **deleted**, never ticked: MAIN.md keeps no `[x]` items (steer 2026-09-14), git history is the record. A multi-part item has the shipped part trimmed from its text and is deleted when the last part lands; a tracking item broader than the diff stays open with the landed part recorded. The plan file joins the commit scope, and the announcement names the outcome (`Plan: 2 items closed — <item>, <item>` or `Plan: nothing to reconcile`); a silent pass reads as a skipped step.

## Step 3: Mode, fresh or iterate

Read `CHANGELOG.md` and match its topmost version heading against `git tag --sort=-creatordate | head -10` in both `vX.Y.Z` and `X.Y.Z` forms. A matching tag means **fresh mode**: a new `## Unreleased` heading goes above it, with no version number and no date, since the bump happens at release time (`prepare-release`). An untagged top heading means **iterate mode**: edit that section in place, never a second heading above it.

In iterate mode the new bullets coexist with the existing ones under four rules:

- A genuinely new topic gets its own bullet.
- Work that extends or supersedes a bullet already in the section modifies that bullet to describe the current end state; no second bullet.
- Work that reverts something already bulleted drops that bullet.
- A fix to something first added or changed in this same unreleased section is never a `### Fixed` bullet: nothing is fixed for a reader who never saw it broken (user, 2026-09-20). Fold what the fix makes true into the bullet that introduced the thing, if that bullet does not already imply it, and write nothing under Fixed. `### Fixed` is for behaviour that shipped broken in a tagged release; the test for each would-be Fixed bullet is `git tag --contains <the commit that introduced the behaviour>`, and no tag means fold or drop.

**Consolidate the section, not just your own bullets.** A section drafted a commit at a time drifts into two bullets about one feature's successive states, or a Fixed bullet under a feature the same section adds. Touching the section means folding those in the same edit and naming the fold in the announcement, so the section always reads as the difference between the last release and now, never as the history of getting there.

## Step 4: Write the bullets

One sentence per bullet, at most 25 words, stating what works now (`CTOC` after a mid-taxi `CTO` revokes the stored clearance and reinstates the destination runway hold-short). A reader scans bullets, so:

- No "Previously..." framing and no mechanism: describe the new state, never the thread bug or null deref behind it.
- The audience is instructors and students: no framework names (Velopack, Avalonia, SignalR, MessagePack), no class, method, property or file names, no exception types or subsystem names. Command names and UI vocabulary stay (`CTOC`, the "Update Now" button, the command bar, Help → About).
- Match the file's existing voice (imperative or noun phrase) and its sub-headings (`### Added`, `### Fixed`, `### Changed`), inventing neither.
- No SHAs, author names, issue numbers or superlatives unless the file already carries them.
- One bullet per distinct user-visible change, never one per bug or per batch (user, 2026-09-10: "don't bundle disparate changes in the same bullet, even if they came from the same bug"): a change a reader could miss inside another bullet, or revert on its own, is its own bullet.

## Step 5: Announce, then continue in the same turn

State the mode and target section, the scope (file count and notable names, both repos when cross-repo), the bullets, and the plan reconciliation. This is transparency, not a gate: the user can interrupt, and the skill goes straight on to Step 6.

## Step 6: Write the file and stage by name

`Edit` `CHANGELOG.md` per the mode, then `git add CHANGELOG.md <scope-paths>` in yaat and `git add <scope-paths>` in yaat-server. Never `git add -A` or `git add .`, which can sweep in `.env` files and build artifacts.

## Step 7: Derive the commit message from the bullets

The prefix comes from the dominant work in the non-changelog files, chosen from the project's own tag set (`git log --oneline -10`), never `docs:` because `CHANGELOG.md` is in the commit: `feat:`/`add:` for a new capability, `fix:`, `ref:`, `chore:`/`dep:`/`ci:`/`test:` as the project uses them, and `docs:` only when the diff is genuinely doc-only (a release roll-up, a pure README or COMMANDS edit, the yaat half of a cross-repo commit). The subject names the change itself (`fix: hold-short markers survive strip bay collapse`), never the documentation event (`update CHANGELOG for X`), in at most 72 characters; five or more unrelated bullets fall back to `<prefix>: update for <section heading>`.

The body has one line per delta bullet in changelog order, the bullet's leading phrase lowercased and trimmed, prefixed `(new)` or `(modified)` when an iterate-mode commit did both. YAAT's commits carry `Co-Authored-By:` and `Claude-Session:` trailers, so every commit this skill writes carries the ones the session supplies: the landing skills read a missing attribution trailer as the mark of a commit this workflow did not author (a codegen tool, an IDE action, a committing hook all mint commits without it) and verify their whole range rather than land it. No issue references the bullets do not already carry; yaat-server commits that close a yaat issue use the full URL.

## Step 8: Show the message, run the implicated tests, commit

Show the assembled message as a status ("Committing as: ..."), not a question. Then run the tests the diff implicates, found from the diff rather than from memory: a changed or removed user-visible string is a search key, `rg -F "<literal>" tests/`, and every test class that matches runs, since a citation reworded inside an advisory string once shipped green and left `main` red on CI from the class that also asserted it.

**Partial commits: isolate the remainder yourself.** When the scope is a subset of the tree, prek stashes unstaged *tracked* changes, and that model has three edges: untracked files are never stashed, so a new test file referencing an unstaged change breaks the build hook when the other slice is committed (stash the remainder including untracked files first: `git stash push -u -m other -- <its paths>`, commit, then `git stash pop`); the whitespace/EOF hook `git add`s whole files, sweeping unrelated dirty hunks into the commit, so commit with explicit paths and re-read `git status` immediately before; a re-apply conflict leaves the stash as a patch under `~/.cache/prek/patches/` to be `git apply`-ed by hand, so check `git diff --cached` for an empty-blob commit before re-running prek. The build hook is cross-repo: yaat-server code that depends on a just-stashed `Yaat.Sim` change fails it, so stash the sibling too (`CLAUDE.md`, "prek's build hook is cross-repo").

Write the message to `.tmp/commit-msg.txt` with the Write tool and commit by path, never by heredoc (the Windows Bash tool halves doubled backslashes and dies on triple quotes), never with `--no-verify`, `--amend` or `--no-gpg-sign`, and never chained after a dotnet command in one Bash call (a guard reads the whole command text and rejects the `-q`):

```bash
git commit -F .tmp/commit-msg.txt -- <exact paths>
git show --stat HEAD
```

Report each HEAD sha with its subject and the working-tree state of both repos. A failed hook is fixed forward and re-staged into a **new** commit; the hook output is surfaced, never bypassed.
