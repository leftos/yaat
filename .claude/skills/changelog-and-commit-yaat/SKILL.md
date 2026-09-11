---
name: changelog-and-commit-yaat
description: "Use in the yaat / yaat-server repos whenever the user says \"changelog and commit\", \"log it and commit\", \"update the changelog and commit\", \"changelog + commit\", or invokes /changelog-and-commit — this yaat-specific variant replaces the user-level `changelog-and-commit` skill here (one CHANGELOG in yaat, two repos, cross-repo commits). Derives bullets from the currently uncommitted (staged + unstaged) work only. Writes the changelog and commits without further prompts — invoking the skill IS the approval."
---

# Changelog and Commit

Two-stage skill: produce or update the CHANGELOG entry for the **work the user is about to commit**, then commit it. Invoking the skill IS the approval — this skill drafts bullets, writes the file, stages, and commits without further prompts. This deliberately overrides the global "never auto-commit" rule because the user's invocation phrase ("changelog and commit") is the explicit go-ahead.

**Scope is narrow on purpose.** This skill describes what's *in the working tree right now* (staged + unstaged), not everything that's happened since the last release. If older committed work is also missing from the changelog, the user can run `/update-changelog` separately to backfill — that skill handles full-cycle synthesis. Don't conflate the two.

## YAAT-specific: one CHANGELOG, two repos

This is the yaat-specific variant of the user-level `changelog-and-commit` skill. It carries a distinct name because a personal skill shadows a project skill of the same name, so a same-named copy here would never load; in this repo, invoke `changelog-and-commit-yaat` (the `ship` skill does). Keep the two in step when the generic flow changes.

In the YAAT setup, there is **one** `CHANGELOG.md` and it lives in the main yaat repo (`CHANGELOG.md` at the yaat checkout root). The sibling repo `..\yaat-server\` has **no** CHANGELOG of its own — its user-visible changes are logged in yaat's CHANGELOG too. (Confirm by looking for existing yaat CHANGELOG entries that describe server-only fixes — there are several.)

Practical consequences for this skill when working on YAAT:

- **Detect cross-repo work in Step 0.** Run `git status --porcelain` in *both* trees and combine the results. The yaat-server tree is the paired sibling of the current yaat checkout (`$(dirname "$(pwd)")/yaat-server`) when that directory is a checkout on the same branch — a `wt`-paired worktree session — and the sibling of the main checkout otherwise: `"$(git rev-parse --path-format=absolute --git-common-dir)/../../yaat-server"` (never a hardcoded drive letter; the repos live on several machines). Naming the main checkout in a paired session reports the server side clean and commits nothing there. The diff scope can span both trees.
- **Two commits, not one.** `git commit` is per-repo, so a single logical change that touches yaat-server code becomes:
  1. A commit in **yaat-server** with the code/test files. Prefix matches the work (`fix:`, `feat:`, `ref:`, …) — same rule as Step 7.
  2. A commit in **yaat** with `CHANGELOG.md` only. Prefix is `docs:`. Subject and body mirror the bullet(s) added.
- **Order matters.** Commit yaat-server first (the substantive change), then yaat (the log of it). If yaat-server's branch is behind `origin/main` due to a submodule bump, fast-forward (`git pull --ff-only`) before committing — don't create divergent history.
- **Pre-commit hooks run per-repo.** Both repos have csharpier/format hooks. If a hook reformats a file, re-stage and create a new commit in that repo (do not `--amend`). The other repo's commit is unaffected.
- **Step 5 announcement** should make the cross-repo nature visible: state both repos by name and which files go in which commit.
- **Step 8 verification** should print HEAD for both repos:
  ```
  Committed yaat-server@<sha> — <subject>
  Committed yaat@<sha> — <subject>
  Working trees: clean / clean
  ```

If the working-tree diff is fully contained in yaat (CHANGELOG plus client/sim code), this is a single commit in yaat as usual.

## Step 0: Snapshot the index state (do this first, before anything else)

The commit scope depends on what was already staged when the user invoked this skill. Capture it now, before anything else can touch the index:

```bash
git status -sb > .tmp/changelog-commit-presnapshot.txt
```

**Read the first line before the file list — it names the ref you are about to
commit to.** `git status --porcelain` alone shows what will be committed but
never where it lands:

- `## HEAD (no branch)` → **halt.** A detached checkout accepts a commit and
  prints `[detached HEAD ...]`; a later `git push origin main` does not carry
  it and nothing notices. The yaat-server checkout sits detached at
  `origin/main` after a submodule-style update. Recover with
  `git checkout -B main <sha>` when `git merge-base --is-ancestor main HEAD`
  holds, then require `## main...origin/main` before committing.
- any branch other than the expected one → halt and name it.

Parse the file list into three buckets and remember them for Step 6:

- **Pre-staged files** — lines starting with a non-space, non-`?` in column 1 (e.g. `M `, `A `, `D `, `R `). These were in the index before the user invoked the skill.
- **Unstaged modifications** — lines with a non-space character in column 2 (e.g. ` M`, `MM`).
- **Untracked files** — lines starting with `??`.

If `CHANGELOG.md` itself appears as pre-staged, treat it as not-pre-staged for the purposes of "was anything staged" — the user almost certainly didn't pre-stage it on purpose. Note it and continue.

If **all three buckets are empty**, halt — there's nothing to commit. Tell the user there are no changes and stop.

## Step 1: Determine the diff scope

Define the set of files this commit will cover:

- **Anything pre-staged** → commit scope = pre-staged files. Ignore unstaged and untracked. (The user assembled this index deliberately.)
- **Nothing pre-staged** → default to **all modified tracked files + untracked files**. Don't prompt. The diff scope is announced in Step 5; if the user is surprised by what's being included, they'll interrupt.

The changelog bullets describe **only** the chosen scope; the commit stages **only** these files (plus `CHANGELOG.md`).

Skip any path that looks like a secrets file (`.env`, `*credentials*`, `*.pem`, `*.key`, `id_rsa*`). If one shows up in the proposed scope, halt and surface it to the user — this is one of the few cases where the skill stops and asks.

## Step 2: Read the diff and understand what changed

**Classify against the emulated upstream, not against this codebase's history.**
YAAT's specification is "behave like the real tools" (vNAS TDLS, CRC/STARS,
ATCTrainer). So the question is not "did this code path exist before?" but
**"does the emulated upstream already behave this way?"**:

- Upstream already behaves this way → the gap was a defect → `### Fixed`, even
  when the code path is brand new.
- A YAAT-original convenience the upstream lacks (a Tools-menu shortcut, an
  instructor-only view) → `### Added`.

Filing a parity gap under Added misrepresents it to users who know the real tool.

**Read the scope from the code, not from the plan note.** When a bullet is
derived from a plan heading, a commit message, or a steer that names a
milestone, that names what the change was *motivated by*, never what it
*reaches* — the two diverge whenever a milestone-driven fix lands in shared
state, which is the normal way such fixes land. Resolve the feature's gating
condition and its call sites, and word the bullet from the reach they show: all
rooms, solo sessions only, headless/soak only, replay only. Say so when the
reach is broader than the originating context implies — that is the case the
reader gets wrong.

**Detect the mixed-shape diff and split it.** When the diff contains many call
sites changed identically *plus* a small logic change — a signature change and
the feature that motivated it — land two commits, refactor first:

1. Stage and commit the mechanical part alone as `ref:`, with **no changelog
   bullet** (zero behaviour change is not user-visible), verified by build and
   tests.
2. Then run the normal flow for the behaviour change.

Bundled, the feature hides inside a sea of mechanical edits and neither half can
be reverted alone. When the two genuinely share hunks and cannot be separated
from the working tree, say so and commit once.

Get the actual content changes for the scope you settled on in Step 1:

```bash
# For pre-staged scope:
git diff --cached -- <pre-staged-paths>

# For all modified tracked files:
git diff HEAD -- <paths>

# For untracked files: read them with the Read tool — they have no diff.
```

Read the diff with the **Read** tool against `.tmp/`-piped output if it's large, or run the command directly with Bash if small. Don't skim — the bullets will describe the actual user-visible behavior change, so you need to know what the code does, not just which files moved.

For each file group, identify:
- The user-visible behavior change (what's different in the running app, the CLI, the docs).
- Whether it's a **new capability**, a **changed behavior**, a **bug fix**, or **internal-only** (test/CI/refactor with no user effect).
- Whether multiple files form one cohesive change (typical) or several independent changes (rare for a single uncommitted batch).

**Gate: does this diff warrant a bullet at all?** Step 3 asks only *where* the
bullets go, so settle *whether* there are any before entering it. A diff that
changes nothing a user sees or can do takes **no bullet**: planning and design
docs for work that is designed but not built, internal refactors with no
behavior change, test/CI-only diffs. Two checks turn that call from a judgement
into a finding:

```bash
rg -F "<topic>" CHANGELOG.md      # precedent — has work like this ever been bulleted?
git log --oneline -- <paths>      # how the same files were treated before
```

On a no-bullet verdict, skip Steps 3 and 4 and leave `CHANGELOG.md` untouched:
go to Step 6 staging (scope paths only, no `CHANGELOG.md`) and Step 7 with the
prefix that fits the work (`docs:` for a planning-doc diff, `ref:` / `test:` /
`ci:` for the rest). State the no-entry decision and its justification — the
precedent and the prior commits to those paths — in the Step 5 announcement.

## Step 2b: Reconcile the main plan

If the repo has a main plan file — `docs/plans/MAIN.md`, or the file the project's CLAUDE.md names as its plan index — this step is REQUIRED. Skip it only when no such file exists.

1. List the unchecked items: `rg -n "^\s*- \[ \]" docs/plans/MAIN.md`, plus the subplan MAIN.md links for the work in the diff scope.
2. For every item the work resolves — the fix, feature or cleanup it describes is in this diff, **or a prior commit of this session already landed it and the item outlived its fix** — tick it with a one-clause `shipped <date>` note, or delete the line when the plan's convention is that finished items are removed.
3. Add the plan file(s) to the commit scope, and name the outcome in the Step 5 announcement: `Plan: 2 items closed — <item>, <item>` or `Plan: nothing to reconcile`. A silent pass reads as a skipped step.

An item broader than the diff (a tracking item with several parts) stays open; record the part that landed in its text instead.

## Step 3: Detect the changelog target section (mode detection)

Read `CHANGELOG.md` and decide where the bullets go:

1. **Find the topmost version heading.** Try to match it to a git tag — `git tag --sort=-creatordate | head -10` and check both `vX.Y.Z` and `X.Y.Z` forms.
2. **Classify:**
   - Top heading **has a matching tag** (already released) → **fresh mode**. Draft a new `## Unreleased` section above it.
   - Top heading is **untagged** (e.g. `## Unreleased`, `## [Unreleased]`, or any other un-tagged heading) → **iterate mode**. Append/edit within that section.

In iterate mode, also read the existing bullets — your new bullets need to coexist:

- If a new bullet is genuinely **new** (different topic from anything already there) → add it.
- If a new bullet **extends or supersedes** an existing one (you're tweaking a feature already bulleted in this section) → modify the existing bullet to describe the current end state, and don't add a separate one. This is the same Rule A/E logic as `update-changelog` but applied only to the current uncommitted work.
- If new work **reverts** something already bulleted → drop the existing bullet.

In fresh mode, the new heading is always `## Unreleased` — no version number, no date. Subsequent commits since the last release will iterate on the same section. The actual version bump (`Unreleased` → `v0.1.6-alpha [date]`) happens at release time, not here.

## Step 4: Write the bullets

**One sentence per bullet. ≤25 words. No "Previously..." narration. State what works now.**

A changelog reader scans bullets — they don't read prose paragraphs. Three- and four-sentence bullets get skipped. Keep each bullet to a single sentence describing the user-visible change. If you find yourself writing "Previously...", "Was...", or "Used to..." — stop. The reader doesn't care what was broken; they care what works now (or what's new).

Style rules:

- **One sentence. ≤25 words.** If you can't fit it, the bullet is doing too much — split it, or drop a sub-clause.
- **No "Previously..." framing.** Describe the new state directly. ("`CTOC` revokes a stored takeoff clearance and reinstates the destination hold-short" — not "Previously CTOC failed... Now it works.")
- **No mechanism explanations.** "Fixed X by switching to Y" → just describe what X does now. The reader doesn't need to know whether a crash was a thread bug, null deref, or deadlock — they need to know what they'll see now.
- **Audience is end users.** Drop implementation jargon — framework names (Velopack, Avalonia, SignalR, MessagePack), class/method/property identifiers, exception types, thread/dispatcher terminology, internal subsystem names, stack-trace fragments. Keep user-vocabulary names for actual UI elements (the "Update Now" button, the command bar, Help → About).
- **Imperative or noun-phrase, not past tense.** Match the file's existing style — if entries say "Added X", keep that; if noun phrases, keep that.
- **No commit SHAs, author names, or issue numbers** unless the repo already uses them.
- **No superlatives** ("significantly", "robust", "comprehensive"). State the change.
- **Group under sub-headings** if the file uses them (`### Added`, `### Fixed`, `### Changed`). Don't invent new ones.

Style contrast:

- Bad: *"A student joining a room and activating their position now appears in the instructor's CRC controller list. Previously the controller list snapshot the instructor first received stayed stale — connecting, activating, or deactivating a position didn't push a delta, so the student stayed invisible until the instructor disconnected and reconnected."*
- Good: *"Students joining a room or activating their position appear in the instructor's controller list immediately."*
- Bad: *"`CTOC` (cancel takeoff clearance) now works when issued after a `CTO` given mid-taxi. Previously, clearing an aircraft for takeoff while it was still taxiing to the runway stored the clearance for later, but a subsequent `CTOC` returned 'No takeoff clearance to cancel'..."*
- Good: *"`CTOC` after a mid-taxi `CTO` revokes the stored clearance and reinstates the destination runway hold-short."*
- Bad: *"Velopack download-progress callback now marshalled to UI thread, fixing InvalidOperationException in MainViewModel.UpdateNowAsync."*
- Good: *"'Update Now' no longer crashes — auto-updates download and apply."*

**One bullet per distinct user-visible change, never per bug or per batch.** A single bug report or session that produced several separable behaviour changes gets one bullet each — a fix, a supporting determinism change and an overlay tweak are three bullets even when all three exist only because of that one bug (the user said so explicitly on 2026-09-10: "don't bundle disparate changes in the same bullet, even if they came from the same bug"). The test: could a reader scanning for "did X change?" miss X inside the bullet, or could X be reverted on its own? If yes, X is its own bullet. Only keep one bullet when the change is genuinely one behaviour that happens to touch several files.

## Step 5: Announce the planned change

Print to the user (in your text response) what you're about to write. **This is a transparency announcement, not an approval gate** — proceed straight to Step 6 in the same turn. The user can interrupt mid-flow if they see something wrong, but the skill does not stop and wait.

State:

1. The mode (fresh or iterate) and the target section heading.
2. The diff scope (file count and a few notable filenames so the user can spot surprises).
3. The bullet(s) you're about to write.
4. The Step 2b plan reconciliation (`Plan: <n> items closed — …` or `Plan: nothing to reconcile`).

**Fresh mode example:**

```
Fresh mode — adding `## Unreleased` above released `## v0.1.5-alpha`. Scope: 4 working-tree files (3 modified + 1 new test file).
Bullet under `### Fixed`:
- RFIS no longer caps at 12 nm — acquisition range now uses METAR vis × geometric horizon × airport-conspicuity.
```

**Iterate mode example:**

```
Iterate mode — appending to `## Unreleased`. Scope: 4 pre-staged files.
New bullet under `### Fixed`:
- Hold-short markers no longer disappear when the strip bay collapses.
Existing bullets unchanged.
```

## Step 6: Write CHANGELOG and stage

Immediately after Step 5's announcement, `Edit` `CHANGELOG.md`:

- **Fresh mode**: insert the new section above the topmost (released) section.
- **Iterate mode**: edit the existing section in place. Do not duplicate the heading. Do not add a new section above it.

Then stage the commit scope **by explicit filename** — never `git add -A` or `git add .`:

```bash
git add CHANGELOG.md <scope-paths>
```

The global rule against blanket-staging applies because it can pull in `.env` files or build artifacts.

## Step 7: Derive the commit message from the bullets

Build the message from the bullets you just wrote (the delta). The commit usually carries **both** the changelog edit *and* the underlying code/asset change — the message must reflect the dominant work, not the changelog mechanics.

**Subject line:**

- **Prefix.** Pick from the project's existing tag set (check `git log --oneline -10`) based on the dominant work in the diff scope, **not** based on the fact that CHANGELOG.md is in the commit:
  - `feat:` — new user-visible capability
  - `add:` — added something (some projects use this instead of `feat:`)
  - `fix:` — bug fix
  - `ref:` — refactor, replacement of an existing form, or reshape that doesn't add new capability
  - `chore:` / `dep:` / `ci:` / `test:` — pick the project's conventional tag
  - `docs:` — **only** if the diff is genuinely doc-only (e.g. release-bump CHANGELOG roll-up with no code, or a pure README/COMMANDS edit). If the diff includes code that the bullets describe, the prefix should match the code change, not the docs.
- **Subject content.** Describe the change itself ("pushback cardinal-direction syntax", "follow-aircraft mode tracks leader speed", "hold-short markers survive strip bay collapse"). Don't frame it as a documentation event ("log X", "document X", "update CHANGELOG for X") unless it actually is doc-only.
- ≤72 chars, imperative or noun-phrase, matching the project's commit style.
- Example shapes:
  - `feat: pushback cardinal-direction syntax`
  - `fix: hold-short markers survive strip bay collapse`
  - `ref: replace numeric pushback headings with cardinals`
  - `docs: update CHANGELOG for v0.3.0` ← only when it really is doc-only

**Picking the prefix when the scope is mixed:** look at non-CHANGELOG files only. If they're code (`src/`, `tests/`) plus a few doc tweaks describing the same change, the doc tweaks are *part of* the feature/fix — use the code prefix. If they're purely under `docs/` / `*.md` / similar, use `docs:`.

If the delta is a single bullet, the subject is just `<prefix>: <bullet topic>`.

If the delta is too varied to summarize (5+ unrelated bullets), fall back to `<prefix>: update for <version-or-section-heading>` using the prefix that fits the largest bullet group.

**Body:**

- One blank line after the subject.
- One bullet per delta item, in the order they appear in the changelog.
- Use the bullet's leading phrase from the changelog, lowercased and trimmed. Don't restate full sentences.
- For iterate mode where you both added and modified bullets in this commit, prefix with `(new)` or `(modified)` to disambiguate.

**Example body (single-bullet commit):**

```
- pushback orientation now uses cardinal directions instead of numeric headings
```

**Example body (iterate mode with mixed delta):**

```
- (new) tickrecorder attach helper for replay testing
- (modified) follow-aircraft mode — leader-speed tracking on pattern legs
```

**Trailers.** Match the project's convention (`git log --pretty=format:%B -5`); never invent trailers a repo does not already use. Where the convention carries them — YAAT's commits carry `Co-Authored-By:` and `Claude-Session:` — put them on every commit this skill writes: the landing skills read a missing attribution trailer as the mark of a commit this workflow did not author, and halt rather than land it. This skill is also not the only writer of commits in the repo. A codegen tool, an IDE action, or a hook that commits mints commits with no trailers and without the ask-before-commit gate, which is why the landing skills verify their whole range instead of assuming everything in it came from here. Do **not** add issue references unless the bullets themselves reference issues.

## Step 8: Show the message and commit

Display the assembled subject + body to the user as a final transparency check. Phrase it as a status update, not a question — invocation was the approval, and Step 5 already announced the bullets:

> *"Committing as:*
> ```
> feat: pushback cardinal-direction syntax
>
> - replace numeric headings with FACE/TAIL keywords and </> shorthand
> - taxiway+cardinal aligns with whichever edge direction matches closest
> ```
> *"*

**First, run the tests the diff implicates — defined by the diff, not by
memory.** A changed or removed user-visible string is a search key for its own
regression tests, which usually live in a different test class from the one you
edited:

```bash
rg -F "<removed or changed literal>" tests/     # every class asserting it
```

Run every test class that matches. This is exactly the failure the "run relevant
tests, not the full suite" guidance lets through: a citation reworded inside an
advisory string once shipped green and left `main` red on CI.

**Partial commits: isolate the remainder yourself.** When the scope is a subset
of the tree, the hook runner (prek) stashes unstaged *tracked* changes — and
that model has three edges:

- **Untracked files are never stashed.** A new test file referencing an unstaged
  change breaks the build hook when the *other* slice is committed. Stash the
  remainder including untracked files first: `git stash push -u -m other -- <its paths>`, commit, then `git stash pop`.
- **The whitespace/EOF hook `git add`s whole files**, sweeping unrelated dirty
  hunks into your commit. Commit with explicit paths and re-read `git status`
  immediately before committing.
- **A re-apply conflict** leaves the stash as a patch under
  `~/.cache/prek/patches/` to be `git apply`-ed by hand; check
  `git diff --cached` for an empty-blob commit before re-running prek.

Then commit. **Write the message to a file and pass it by path — do not use a
heredoc.** In this Windows Git-Bash tool a heredoc body is not delivered
verbatim even with a quoted delimiter: doubled backslashes arrive halved, and a
body containing a triple-quoted string dies with "unexpected EOF while looking
for matching quote". Commit messages routinely quote Windows paths and regexes.
Per the project rule, no `--no-verify`, no `--amend`, no `--no-gpg-sign`:

```bash
# write .tmp/commit-msg.txt with the Write tool, then:
git commit -F .tmp/commit-msg.txt -- <exact paths>
```

Never chain a commit after a dotnet command in one Bash call: a guard hook
matches the whole command text, so `dotnet build ... && git commit -q ...` is
rejected for the `-q`.

Verify nothing extra landed:

```bash
git show --stat HEAD
```

After the commit, run `git status` and report the resulting HEAD sha + one-line summary:

```
Committed abc1234 — feat: pushback cardinal-direction syntax
Working tree: <clean | N files still modified>
```

If pre-commit hooks failed: do not `--amend`, do not `--no-verify`. Surface the hook output, fix the underlying issue (or ask the user to), re-stage the changed files, and create a **new** commit.

## Anti-patterns (do not do these)

- **Do not describe work the user isn't committing.** Bullets describe the diff scope from Step 1, not the full release-cycle history. If older committed work is missing from the changelog, that's a `/update-changelog` job, not this skill.
- **Do not run `git log <baseline>..HEAD`** to derive bullets. The diff scope is the working tree, full stop.
- **Do not default the commit prefix to `docs:`** just because CHANGELOG.md is in the diff. The prefix must reflect the dominant work (`feat:`, `fix:`, `ref:`, …); `docs:` is correct only when the commit is genuinely doc-only. Likewise, don't frame the subject as "log X" or "update CHANGELOG for X" when the commit contains the X-implementing code.
- **Do not ask for approval at any step.** Invoking the skill is the explicit go-ahead — write the file and commit without prompting. Asking is the failure mode this skill exists to eliminate. Step 5 *announces* the bullets; it does not gate on them.
- **Do not propose a version number in fresh mode.** The new heading is always `## Unreleased`. The version bump happens at release time, not here.
- **Do not stage with `git add -A` or `git add .`.** Always enumerate filenames.
- **Do not include unstaged work** when something was pre-staged. The user's index is the source of truth.
- **Do not commit secrets files.** Halt if `.env`, `*.pem`, `id_rsa*`, or `*credentials*` appears in the proposed scope.
- **Do not amend or `--no-verify`** if the pre-commit hook fails. Fix forward and create a new commit.
- **Do not skip Step 0.** If you find out about pre-staged files only after editing CHANGELOG.md, you've already lost the ability to distinguish "user staged this" from "I dirtied the tree."
- **Do not duplicate a section** in iterate mode. Edit the existing unreleased section in place. Two unreleased headings in the file is a hard failure.
- **Do not invent issue/PR references** in the commit body. Only mirror what's in the changelog bullets.

## Quick reference

```bash
# Step 0 — snapshot before doing anything; first line names the ref
git status -sb > .tmp/changelog-commit-presnapshot.txt   # '## HEAD (no branch)' -> halt

# Step 2 — read the actual diff for the scope you chose
git diff --cached -- <pre-staged-paths>     # if pre-staged
git diff HEAD -- <paths>                    # if unstaged

# Step 3 — find target section in CHANGELOG.md
head -30 CHANGELOG.md
git tag --sort=-creatordate | head -10

# Step 6 — stage by name only
git add CHANGELOG.md <scope-paths>

# Step 8 — tests the diff implicates, then commit by message FILE (not heredoc)
rg -F "<removed literal>" tests/            # run every class that matches
# write .tmp/commit-msg.txt with the Write tool; prefix matches the dominant
# work in the diff scope, NOT "docs:" by default.
git commit -F .tmp/commit-msg.txt -- <exact paths>

# Confirm
git show --stat HEAD
git log -1 --oneline
git status
```
