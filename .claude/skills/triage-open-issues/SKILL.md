---
name: triage-open-issues
description: "Use when the user asks to triage the open GitHub issues, asks which untriaged issues are already fixed by unreleased work or where a given #N belongs on the board, or when issues are sitting in team YAAT's Triage state before a planning or release session."
---

# Triage open issues onto the board

The plan is Linear, team YAAT (`yaat-nextup`; `~/.claude/docs/plan-operations.md`). GitHub issues on `leftos/yaat` reach the team's Triage state through Linear's sync, so nothing is copied. This skill is the reading and the verdicts for the **triage** operation there: each untriaged issue ends the run with one verdict and, when it is still open work, one project. Triage stops at placement: the fix is designed when the item is picked up. `docs/plans/MAIN.md` is a generated snapshot and is never edited here. Nothing on GitHub is mutated by hand: no close, no comment, no label.

## Step 1: Sync and list

1. `linear sync-priority yaat`, so the owner's `urgent`/`high` labels and hand-set priorities agree before anything is read; it also puts every open Urgent or High issue not yet in an open placeholder into `vNext`.
2. `linear publish yaat --labelled` publishes every open issue the owner marked `public`.
3. The untriaged issues are those in `linear list yaat` whose state is Triage, plus any top-level issue with `project: null`. `linear list` also gives the board to place them on: the projects, their content and milestones.

## Step 2: Read each issue

**Read item** every untriaged issue with `linear show <ID>`, description and whole comment thread: the reporter or owner often states the current state in a comment ("fixed in abc123, leaving open until confirmed"). Read it far enough to name the files it touches. An attachment (a screenshot, a recording, a Discord forward) is read with `multimodal-looker`; a `*-recording.zip` or bug bundle goes through `bug-bundle`. A bot issue (`nightly-review` label) states its confidence and carries a red-first repro; that is the evidence, and re-deriving the root cause from source is the implementer's work.

The `suggestions` in `linear show <ID>` (Triage Intelligence, set only while the issue is in Triage) are a hint and the files decide: a suggested project that is one of the board's projects is taken when the issue's files belong there, and an open issue suggested as related is named beside the placement in the report.

## Step 3: One verdict per issue

Apply the first row that matches:

| Verdict | Predicate | Action |
|---|---|---|
| `pr-linked` | an open PR (`gh pr list --repo leftos/yaat --state open --json number,title,headRefName`) names the issue in its title, body or branch | the PR is planned by that mention; otherwise **add** `Review and land PR #N` with its files, checks and mergeability in the description, routed to the `land-bot-pr` skill for a bot's PR |
| `probably-fixed` | a commit since the last tag cites the issue, or a bullet in `CHANGELOG.md`'s Unreleased section satisfies the body's ask (the matching rules are `prepare-release` step 2.9: read the body, a partial fix is not a fix) | probe it: when the code shows the ask met, **land** it with the fixing commit's sha (`linear land <ID> --sha <sha>`, `~/.claude/docs/plan-operations.md`); a partial or unproven fix is placed as `open` with the evidence in a comment |
| `open` | everything else | place it (Step 4) |

A file the issue names with no commits since the last tag is untouched, which settles `open` against `probably-fixed` cheaply; a file with commits gets `git log <tag>..HEAD --oneline -- <file>` (in yaat-server, `git log --since=<tag date>`) and the subjects say whether the change was the issue's or unrelated churn.

**A vague title is renamed.** A user submission titled broadly is renamed to say what goes wrong, where, and the command or view involved, per `yaat-nextup`'s triage paragraph: `linear edit <ID> --title "<title>"`. The reporter's own words stay in the description.

## Step 4: Place each `open` issue

Each goes in the project that shares its files: the project whose issues, or whose content, name the same `docs/*.md` subsystem doc or the same fix site. Two reports of one gap are one item: the second gets a comment on the first, or a `--related` or `--duplicate-of` edit, not a second placement. Different subsystem docs are distinct items, even when the labels match. An issue no project covers goes in `Backlog`. A defect that belongs to a feature marker's outcome project goes there, under the feature parent when it is worked on the branch.

Then one **arrange** places them all: `linear arrange yaat <layout.json>`, the layout being the current one from `linear list` with each untriaged issue added to its project's list. `arrange` sets no state, so each issue placed out of Triage then gets Backlog (or Todo, in an outcome being worked) with a bare `issueUpdate(stateId)`, which posts no comment to its GitHub ticket.

## Step 5: Report

Post one table: `#` or ID, verdict, evidence (one cell: the commit, changelog bullet, PR or comment), placement (project and state), and any related open issue Triage Intelligence or the read found. Placement is applied in Step 4 without asking, since it only moves a card; a rename is reported with its old and new title.

## Completion

Every untriaged issue in the Step 1 list appears in the table with a verdict; every `open` issue has a project and left Triage; every `pr-linked` row names its PR; every `probably-fixed` row names its evidence; a vague title was renamed.
