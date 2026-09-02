---
name: test-fix
description: "YAAT TDD bug-fix workflow: write failing test, confirm failure, apply fix, confirm pass"
---

# TDD Bug-Fix Workflow

Follow these steps exactly. Do not skip or reorder.

## Reference files — load on demand

- `references/diagnosing-a-wrong-red.md` — **load at Step 3 whenever the test
  fails for a reason other than its assertion**, and at Step 5 when a GREEN
  will not come. Live test output, the `Diag_` probe recipe, the blame setter,
  replay-reachability, the stacked-replay crash signature.
- `references/post-fix-triage.md` — **load at Step 6 the moment more than one
  test is red.** The five failure categories, what each one licenses, and the
  prohibition on greening by relaxing assertions.

## Step 1: Understand the bug

**1a. Check for work that already exists.** An issue number is a pointer into a
tracker, not a specification — the nightly-review bot files a fix PR alongside
the issue it opens.

```bash
gh issue view <N> --repo leftos/yaat --json title,body,closedByPullRequestsReferences
gh pr list --repo leftos/yaat --search "<N>" --state all
```

If a PR is linked, review, verify and land it (see the `land-bot-pr` skill)
rather than reimplementing. Implement from scratch only when nothing is linked.

**1b. Decide the scope before the code area.** A single-subject report is often
an instance of a class. Check whether the subsystem under the symptom changed
recently — a recent change there is the prior suspect:

```bash
git log --since=3.weeks -- src/Yaat.Sim/<subsystem>/
```

Then ask the user (AskUserQuestion) whether they read the report as a one-off or
as a symptom of something broader; they usually already suspect. Reproduce the
instance, but prefer an assertion that catches the class ("no aircraft exceeds
the ground turn rate during a fillet arc" over "this aircraft exits at K4").

**1c.** Identify the affected code area and read the source. Open the subsystem
doc from `docs/architecture.md`'s task index before exploring.

## Step 2: Write the failing test FIRST

- Create a test that reproduces the bug. Use real data via
  `TestVnasData.EnsureInitialized()` — no synthetic stubs.
- The test must assert the **correct** (expected) behavior, so it **fails**
  against the current code.
- Place the test in the appropriate test project mirroring the source structure.

## Step 3: Confirm the test fails (RED)

```bash
tools/gate.sh .tmp/test-red.log dotnet test <test-project> -- --filter-method "*<TestName>*"
```

- If the test **passes**, it doesn't reproduce the bug. Revise the test.
- If the test **fails for the wrong reason** — compile error, unrelated
  exception, a crash inside the harness, or an assertion that can't be reached —
  **load `references/diagnosing-a-wrong-red.md` and work through it.** Do not
  guess at the cause from the crash site.
- Only proceed when the test fails because the assertion catches the bug.

## Step 4: Apply the fix

- Make the minimal code change that fixes the bug.
- Do not refactor surrounding code. Do not add unrelated improvements.

## Step 5: Confirm the test passes (GREEN)

```bash
tools/gate.sh .tmp/test-green.log dotnet test <test-project> -- --filter-method "*<TestName>*"
```

If the test still fails, the fix is incomplete — iterate on Step 4. If it fails
for a reason that is not the assertion, load
`references/diagnosing-a-wrong-red.md`.

## Step 5a: Mutation check — prove the test catches the bug

A test that passes after the fix has not yet been shown to fail without it.

1. Invert the fix using the **same Edit/replace mechanism** that applied it.
2. Re-run the filtered test; confirm RED.
3. Invert back; confirm GREEN.

**Restoring a temporary edit.** Undo by applying the inverse replacement, never
by `git checkout -- <file>` / `git restore <file>`: a whole-file restore reverts
the file to HEAD and silently wipes every other uncommitted change in it. This
binds for every temporary edit — mutation, `Diag_` probe, blame setter. A
checkout is permitted only after `git diff <file>` shows nothing worth keeping.

## Step 6: Run broader tests for regressions

```bash
tools/gate.sh .tmp/test-suite.log dotnet test <test-project>
```

Use `timeout 120` if you wrap this yourself — this is a full-project run, not a
targeted one, and CLAUDE.md's `timeout 30` applies only to filtered runs. A kill
at 120 s means a genuine hang (broken graph topology, an infinite pathfinder
loop), not a slow suite.

**If more than one test is red, load `references/post-fix-triage.md` and
classify every failure before editing anything.** Present the breakdown to the
user first. Never relax an assertion or add a case-specific shortcut to reach
green — a suite made green that way is a worse state than a red one.

## Step 7: Build with warnings as errors

```bash
tools/gate.sh .tmp/build.log dotnet build -p:TreatWarningsAsErrors=true
```

## Step 8: Cross-repo gate (when the diff touches Yaat.Sim's public surface)

If the change touched a type, signature or DTO in `src/Yaat.Sim`, a bare
`dotnet test` in yaat cannot see that it broke the sibling yaat-server repo:

```bash
tools/gate.sh .tmp/test-all.log pwsh tools/test-all.ps1
```

## Step 9: Aviation review (when behaviour changed)

Any change to pilot/ATC behaviour, physics, phraseology, phase transitions or
ground ops requires the `aviation-sim-expert` review — see the
`aviation-review-gate` skill for the invoke/skip decision, the citation rules
and the standard local-references preamble.

**Skip it** when the user prescribed the exact behaviour ("make X also do Y",
"stop allowing Z"). **Invoke it** when the design is open, or when physics or
phraseology is being invented.

## Reminders

- **No guessing at root causes** — the test must reproduce the bug before any
  fix attempt.
- **No synthetic test data** — use `TestVnasData.EnsureInitialized()` with real
  NavData/CIFP.
- **Gate commands go through `tools/gate.sh`.** A teed pipeline reports the
  status of its last stage, so `dotnet build | tee | tail` exits 0 on a failed
  build. Never end a gate command with `tail` or `grep`.
- Enable SimLog in tests when you need debug output:
  `SimLogBuilder.CreateForTest(output).EnableCategory("ClassName", LogLevel.Debug).InitializeSimLog()`.
