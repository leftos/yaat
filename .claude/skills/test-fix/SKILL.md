---
name: test-fix
description: "TDD bug-fix workflow: write failing test, confirm failure, apply fix, confirm pass"
---

# TDD Bug-Fix Workflow

Follow these steps exactly. Do not skip or reorder.

## Step 1: Understand the bug

- Read the issue or user description carefully.
- Identify the affected code area (file, class, method).
- Read the relevant source code to understand current behavior.

## Step 2: Write the failing test FIRST

- Create a test that reproduces the bug. Use real data via `TestVnasData.EnsureInitialized()` — no synthetic stubs.
- The test must assert the **correct** (expected) behavior, so it **fails** against the current code.
- Place the test in the appropriate test project mirroring the source structure.

## Step 3: Confirm the test fails (RED)

Run:
```bash
timeout 30 dotnet test <test-project> --filter "FullyQualifiedName~<TestName>" 2>&1 | tee .tmp/test-red.log
```

- If the test **passes**, the test doesn't reproduce the bug. Revise the test.
- If the test **fails for the wrong reason** (compile error, unrelated exception), fix the test setup.
- Only proceed when the test fails because the assertion catches the buggy behavior.

## Step 4: Apply the fix

- Make the minimal code change that fixes the bug.
- Do not refactor surrounding code. Do not add unrelated improvements.

## Step 5: Confirm the test passes (GREEN)

Run:
```bash
timeout 30 dotnet test <test-project> --filter "FullyQualifiedName~<TestName>" 2>&1 | tee .tmp/test-green.log
```

- If the test still fails, the fix is incomplete. Iterate on Step 4.

## Step 6: Run broader tests for regressions

Run the full test suite for the affected project:
```bash
timeout 30 dotnet test <test-project> 2>&1 | tee .tmp/test-suite.log
```

- If other tests break, determine whether the fix exposed a pre-existing issue or introduced a regression.
- Fix regressions. For pre-existing issues exposed by the fix, fix forward — do not revert correct fixes.

## Step 7: Build with warnings as errors

```bash
dotnet build -p:TreatWarningsAsErrors=true 2>&1 | tee .tmp/build.log
```

## Reminders

- **No guessing at root causes** — the test must reproduce the bug before any fix attempt.
- **No synthetic test data** — use `TestVnasData.EnsureInitialized()` with real NavData/CIFP.
- **timeout 30** on all `dotnet test` invocations to catch soft hangs.
- **tee to .tmp/** on all dotnet commands.
- Enable SimLog in tests if you need debug output: `SimLogBuilder.CreateForTest(output).EnableCategory("ClassName", LogLevel.Debug).InitializeSimLog()`.
