# Triaging a batch of failures after a correctness fix

Load this at Step 6 the moment more than one test is red.

**Classify every red test before editing any of them, and present the breakdown
to the user before touching anything.** A correctness fix legitimately breaks
tests, and the categories license opposite actions — so acting test-by-test in
discovery order produces a pile of unrelated edits and hides the pattern.

## The prohibition

**Never relax an assertion, widen a tolerance, or add a case-specific shortcut
to reach green.** A suite made green that way is a worse state than a red one:
the signal is gone and the cause remains. The V1 taxi pathfinder died exactly
this way — a cluster-synth planner, then an orbit detector, then a chord-chain
aggregate turn, each a bandaid on the last.

## The categories

| # | Category | How to recognise it | What it licenses |
|---|---|---|---|
| a | **Genuine regression** | The fix changed behaviour the test correctly describes | Fix the code |
| b | **Test over-fit to the old implementation** | The assertion pins an exact output the fix legitimately changed (a literal, an ordering, a rounded value) | Update the assertion, stating in the commit why the new value is correct |
| c | **Replay-fidelity casualty** | The recording was produced by the old code, so the replay diverges from the moment the fix takes effect | Prove it, then delete only the failing methods (below) |
| d | **Capability deliberately not built** | The test asserts something the project has decided not to implement | Report it; never fake a pass |
| e | **Static-singleton race** | Passes alone, fails in the suite; mismatched values where both sides should come from one lookup (`Expected 98 / Actual 96.5`) | Pin with `TestVnasData.EnsureInitialized()` in the class constructor |

## Proving category (c) before deleting anything

A replay casualty must be demonstrated, not assumed:

1. Synthetic tests of the same behaviour pass.
2. The fix does not touch the phase logic the recording exercises.
3. Reverting half the fix isolates the divergence to the expected cause.

Then **delete only the failing test methods**, not the recording: one recording
usually feeds several test files, and a passing test elsewhere may still use it.
Ship the fix — a correct fix is not held back because a recording desynced.

Fillet geometry is a common trigger: snapshot DTOs persist fillet-minted
tangent-cut node IDs, so any fillet-geometry change time-shifts every replay
that crosses a corner.

## Emergent multi-body systems

`GroundConflictDetector` re-times every pair when one pair changes, so a
one-pair unit test cannot show the effect of a change. Run the dense-scenario
replay regressions the subsystem doc names — see
[`docs/conflict-and-visual-detection.md`](../../../../docs/conflict-and-visual-detection.md).
