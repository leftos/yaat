---
name: consolidate-recordings
description: "Dedupe recording .zip files in tests/Yaat.Sim.Tests/TestData via tools/Yaat.RecordingConsolidator and commit the cleanup. Internal maintenance — do not update CHANGELOG.md."
---

# Consolidate Recordings

Runs `tools/Yaat.RecordingConsolidator` to hash all `.zip` files under
`tests/Yaat.Sim.Tests/TestData/`, collapse duplicates to a single hash-named
file, rewrite `.cs` and `.md` references to keep the codebase pointing at the
new names, and commit the result.

This is **internal maintenance**. Do **not** add a CHANGELOG.md entry — the
change is invisible to users.

## Step 1: Dry-run preview

```bash
bash tools/gate.sh .tmp/consolidate-dry.log dotnet run --project tools/Yaat.RecordingConsolidator -- tests/Yaat.Sim.Tests/TestData --dry-run
```

If the summary reports `0 duplicate group(s)`, stop here — there is nothing to
do. Tell the user no duplicates were found and exit.

## Step 2: Live run

```bash
bash tools/gate.sh .tmp/consolidate-live.log dotnet run --project tools/Yaat.RecordingConsolidator -- tests/Yaat.Sim.Tests/TestData
```

The tool deletes duplicate `.zip`s, renames the keeper to `<hash12>.zip`, and
rewrites every matching `TestData/<name>` reference in `*.cs` and `*.md`
files across the repo (excluding `bin/`, `obj/`, and `.git/`).

## Step 3: Verify the rewrite compiled and tests still pass

```bash
bash tools/gate.sh .tmp/consolidate-build.log dotnet build -p:TreatWarningsAsErrors=true
bash tools/gate.sh .tmp/consolidate-test.log dotnet test tests/Yaat.Sim.Tests
```

This is a full-project run of roughly 300 replay E2E tests. It legitimately
takes minutes — if you wrap it in a `timeout`, use `timeout 120`, not the
`timeout 30` CLAUDE.md specifies for *filtered* runs. A kill at 120 s means a
genuine hang, not a slow suite.

If a test fails because it references a renamed file the tool missed, fix the
reference by hand before committing. Do not revert the consolidation.

## Step 4: Commit

Stage only what the tool touched — the renamed `.zip`s under `TestData/` and
the updated `.cs` / `.md` files. Per workspace rules, never use `git add -A`.

```bash
git status --short                                    # confirm scope
git add tests/Yaat.Sim.Tests/TestData/                # renamed/deleted zips
git add $(git diff --name-only HEAD -- '*.cs' '*.md') # updated source/doc files
git commit -m "chore: consolidate duplicated recordings"
```

If `git status` shows unrelated pending changes, leave them alone — stage by
explicit path only.

## Reminders

- **No CHANGELOG.md edit.** Internal cleanup is not user-visible.
- **No branches/PRs** — commit directly to `main` per workspace rules.
- **Do not stash unrelated work** — the consolidator only touches `TestData/`
  zips and `.cs` / `.md` references; an unrelated dirty tree is fine to leave alone.
- If duplicate-rename collisions occur (rare — when an existing file already
  has the hash name), the tool deletes the duplicate keeper rather than
  overwriting. Re-run after manual cleanup if you see anomalies.
