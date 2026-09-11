---
name: triage-open-issues
description: "Use when the user asks to fold, sync or incorporate the open GitHub issues into docs/plans, asks which open issues are already fixed by unreleased work or where a given #N fits in the plan, or when the tracker and docs/plans/MAIN.md have drifted before a planning or release session."
---

# Triage open issues into the plans

Every open issue on `leftos/yaat` ends the run with one verdict and, for the ones still open, one
place in `docs/plans/`. Triage stops at placement: the fix is designed when the item is picked up,
not here. Nothing on GitHub is mutated — no close, no comment, no label.

## Step 1: Gather

```bash
bash .claude/skills/triage-open-issues/scripts/gather.sh
```

Writes `.tmp/issue-triage/`: `issues.tsv` (number, date, labels, linked PR, title), `open-prs.tsv`,
`bodies/<N>.md` (body + comments), `refs.txt` (issue numbers cited by commits since the last tag and
by the changelog's Unreleased section), `changelog-unreleased.md`, `plan-refs.txt` (plan lines in
both repos citing an open issue, then open checkboxes naming a class the body names), `touched-files.txt` (files each body names, with the count of
commits since the tag that touched each). yaat-server has no tags, so its window is
`--since=<yaat tag date>`. Read `issues.tsv`, `refs.txt`, `plan-refs.txt`, `touched-files.txt`, then
every `bodies/<N>.md`. Read a comment thread to the end: the reporter or owner often states the
current state there ("fixed in abc123, leaving open until confirmed").

## Step 2: One verdict per issue

Apply the first row that matches:

| Verdict | Predicate | Plan action |
|---|---|---|
| `planned` | `plan-refs.txt` cites `#N` from a live plan line (not a folder-map row) | none; the pointer already exists |
| `pr-linked` | `issues.tsv` shows a linked PR, or an open PR title carries `(#N)` | one Backlog line naming every PR↔issue pair, routed to the `land-bot-pr` skill; the issue's own scope notes are not plan items |
| `probably-fixed` | `refs.txt` cites `#N`, or a bullet in `changelog-unreleased.md` satisfies the body's ask (the matching rules are `prepare-release` step 6d: read the body, a partial fix is not a fix) | none; report the evidence. Closing belongs to `prepare-release` step 6d after the release ships |
| `open` | everything else | place it (Step 3) |

`touched-files.txt` settles `open` vs `probably-fixed` cheaply: a file with zero commits since the tag
is untouched; a file with commits gets `git log <tag>..HEAD --oneline -- <file>` and the subjects say
whether the change was the issue's or unrelated churn. A bot issue (`nightly-review` label) states
its confidence and carries a red-first repro; that is the evidence, and re-deriving the root cause
from source is the implementer's work. Rule 1 in Step 3 needs the plan files that name the
issue's classes; `plan-refs.txt` lists those too, under its second heading, so the subplan check is
a read. Any `rg` you run yourself takes an absolute path (`rg -n pat "$(git rev-parse --show-toplevel)/docs/plans"`): a
`cd … && rg` is rejected by the permission guard and costs a retry.

## Step 3: Place each `open` issue

Placement rules, first match wins:

1. **A subplan already covers the subsystem** (its file names the same `docs/*.md` subsystem doc or
   the same fix site) → one checkbox in that subplan; append `#N` to the subplan's MAIN.md pointer line.
2. **A MAIN.md Backlog line already describes the same defect family** → merge: add `#N` and one
   clause to that line. Two reports of one gap are one item.
3. **Two or more `open` issues share a subsystem doc and a contract or invariant** (the same
   `docs/<subsystem>.md` section, or the same class) → one new subplan `docs/plans/<slug>.md` listing
   them as checkboxes, plus one MAIN.md line under "Subplans without a schedule". Different subsystem
   docs → distinct items, even when the labels match.
4. **Otherwise** → one Backlog line. An `open-issues/<N>-<slug>.md` file exists only for an issue
   that needs a design document of its own (a feature, a cross-repo programme); a bug gets a line.

The MAIN.md Backlog line is one line, in the index's own voice, and carries exactly two things:
the symptom and the fix site. The mechanism, the repro test and the fix model stay in the issue,
which the reader opens by number:

```markdown
- [ ] **Free-text `RDTXT`/`SP1`/`ANNOTATE` over-split on `,`/`;`** (#421, reported 2026-09-06): the message is truncated at the separator and the tail is dispatched as a command when it happens to parse — `CompoundPolicy.TrySplitSpecialCompound`
```

Bare `#N`, not a markdown link; no longer than the neighbouring lines (under about sixty words). A
merge (rule 2) adds `#N` and one clause to the existing line. A subplan checkbox may quote the
issue's root-cause paragraph and its repro test name. Keep the folder-map row for `open-issues/` in
sync when a file is added there.

## Step 4: Propose, then apply on approval

Post one table — `#`, verdict, evidence (one cell: the commit, changelog bullet, PR or plan line),
placement (file + section), group — followed by the exact lines to add or change, then stop for the
user's go. On approval edit `MAIN.md` and the subplans, show `git diff --stat docs/plans`, and offer
a `docs:` commit; the global rule against auto-commit still holds.

## Completion

Every number in `issues.tsv` appears in the table with a verdict from Step 2; every `open` issue has
a placement; every `pr-linked` pair names its PR; every `probably-fixed` verdict names its evidence.
About ten tool calls for a tracker of this size (eight open issues): one gather, the reads, the
table.
