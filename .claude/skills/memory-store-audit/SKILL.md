---
name: memory-store-audit
description: "Audit, prune and re-index a knowledge store (the per-project auto-memory under ~/.claude/projects/<slug>/memory/) with parallel read-only auditors, a verify-before-delete gate, and a scripted index rebuild that asserts its own budgets. Use when the memory index is over its load budget, when MEMORY.md links files that no longer exist, when the store has grown past a few dozen entries, or when the user says 'audit the memory', 'prune memory', 'compact MEMORY.md', 'the index is too big', 'rebuild the memory index'."
---

# Memory Store Audit

The store is a directory of small markdown facts plus one index (`MEMORY.md`)
that is injected into **every** session. The index has a hard load budget —
past it, entries are silently dropped, and a truncated index looks exactly like
a shorter one. So this workflow is built around two ideas: an audit produces
*proposals*, never instructions; and the thing that writes the index measures it.

Default store for this project:
`C:\Users\Leftos\.claude\projects\X--dev-yaat\memory\`

## Step 0: Snapshot — the store is not under version control

There is no `git checkout` to undo a bad prune. Copy the whole directory before
the first deletion, and keep the snapshot until the rebuilt index has been used
in a real session:

```bash
cp -r "C:/Users/Leftos/.claude/projects/X--dev-yaat/memory" "X:/dev/yaat/.tmp/memory-snapshot-$(date +%Y%m%d-%H%M)"
```

## Step 1: Partition into disjoint slices

List the store, split it into slices of roughly 30-50 files, and give **one
read-only auditor per slice**. Disjoint ownership is what makes parallel
auditing safe: no two agents propose edits to the same file.

```bash
ls "C:/Users/Leftos/.claude/projects/X--dev-yaat/memory"/*.md | wc -l
```

Each auditor gets: its explicit file list, the admission test (Step 2), the
verdict vocabulary (Step 3), and a standing instruction that it **writes
nothing** — it returns verdicts as text.

Partitioning prevents write conflicts and *guarantees* blind spots exactly where
two slices describe the same thing. Step 4 is where those are resolved; do not
expect an auditor to find them.

## Step 2: The admission test — a half-life question

Applied to every entry, and by whatever instruction later tells an agent to
"distill findings into memory":

> **Will this still be true in a month?**

| Answer | Verdict |
|--------|---------|
| A durable rule, invariant, footgun, or a fact about how the system works | **KEEP** |
| Current status, progress, task lists, "who owns what now", a decision-of-the-day, a gate-number snapshot, a milestone's remaining items | **PRUNE** — it belongs in `docs/plans/*.md` or the session task list |
| Durable, but buried under status text | **TRIM** to the durable half |

Status rots within days and bloats an index that is already at its limit. The
plan file and the task list are the right homes for it, and they are visible to
the user in a way memory is not.

## Step 3: Verdict vocabulary — every DELETE cites its replacement

Each auditor returns one row per file:

```
<filename> | KEEP | <one-line description> | <section>
<filename> | TRIM | <what survives> | <section>
<filename> | DELETE | covered by: <exact file or doc path + heading/line> | -
<filename> | MERGE  | into <target file> | <section>
```

**A DELETE verdict with no citation is not a verdict.** The citation must name a
specific location that already carries the fact — another memory file, a
`docs/*.md` section, a CLAUDE.md rule — not "this is generally known" and not
"covered elsewhere".

## Step 4: Execute — re-verify each citation at the moment of deletion

One executor holds the whole plan. For every DELETE and MERGE:

1. **Open the cited location and confirm the fact is actually there.**
2. If it is not, **downgrade to TRIM** — keep the durable sentence, drop the
   rest — and record that the citation failed.
3. Only then delete.

The audit and the execution see different states, and a proposal produced by an
analysis pass is not an instruction. This gate is the whole reason the audit is
split from the execution.

**Reconcile cross-slice contradictions explicitly.** A per-slice auditor cannot
see them. The observed shape: two slices issued opposite verdicts on the same
pair of files — each proposed merging into the file the other proposed deleting.
Only the executor holds both. Sweep for it deliberately:

Save each auditor's rows as `.tmp/memaudit/verdicts-<slice>.md`, then list every
destructive verdict in one place and check each MERGE target against the DELETE
list:

```bash
grep -hE "\| (DELETE|MERGE) \|" X:/dev/yaat/.tmp/memaudit/verdicts-*.md | sort
```

Report each contradiction and its resolution rather than silently picking one.

## Step 5: Rebuild the index with the script, never by hand

Rewriting the index freehand after a prune risks three defects that produce no
error: a link to a deleted file, a surviving file that nothing points at, and a
size quietly past the budget. All three have happened here — after one
229 → 135 prune, the hand-written index still linked twelve files the prune had
removed.

Concatenate the auditors' rows into one inventory file
(`<filename> | <type> | <description> | <section>`; sections appear in the index
in the order they first appear in the inventory), then:

```bash
python .claude/skills/memory-store-audit/scripts/rebuild_index.py \
  --memory-dir "C:/Users/Leftos/.claude/projects/X--dev-yaat/memory" \
  --inventory X:/dev/yaat/.tmp/memaudit/inventory.md \
  --dry-run
```

The script prints and **asserts** five properties on every run, and exits 1 if
any of them is non-zero — a failure, not a warning:

- inventory rows naming a file that is not on disk
- dangling links (an index link with no file behind it)
- unindexed files (a file no index line points at)
- lines over the per-line budget (`--max-line`, default 160)
- the whole file over its budget (`--max-lines` 200, `--max-chars` 25000)

Drop `--dry-run` to write. The index preamble is carried over from the existing
index (everything above the first `## ` heading) unless `--header FILE` is given.

If it fails on size, the fix is more pruning or shorter descriptions — never a
larger budget. The budget is the consuming platform's, not a style preference.

## Step 6: Verify before you discard the snapshot

```bash
python .claude/skills/memory-store-audit/scripts/rebuild_index.py \
  --memory-dir "C:/Users/Leftos/.claude/projects/X--dev-yaat/memory" \
  --inventory X:/dev/yaat/.tmp/memaudit/inventory.md --dry-run
```

A clean re-run against the written state is the proof: zero dangling, zero
unindexed, within budget. Report the before/after entry counts, the
contradictions you reconciled, and every DELETE that was downgraded to a TRIM
because its citation did not hold.

## Anti-patterns

- **Deleting on an auditor's say-so.** The verdict is a proposal. Re-verify.
- **Letting two auditors share a file.** Disjoint slices or nothing.
- **Editing `MEMORY.md` by hand "just to fix one link".** Fix the inventory row
  and re-run the script; a hand edit is how the dangling links got there.
- **Raising the budget instead of pruning.** The truncation is silent and the
  budget is not yours.
- **Storing status.** If it names a milestone, an owner, a remaining count or a
  current gate number, it belongs in `docs/plans/`.
- **Skipping the snapshot** because the prune "is only a few files".
