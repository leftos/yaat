# Triaging a batch of failures after a correctness fix

Load this at Step 6 the moment more than one test is red, and for a single red
whenever the failing check is a diff-based baseline guard (category f).

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
| f | **Baseline guard whose diff moved** | A diff-based guard — accepted-divergence baseline, golden file, accepted-failure list — reports entries **gone** as well as, or instead of, added | Classify the direction before any fix (below); never take the offered re-baseline command on reflex |

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

## Classifying category (f): which direction did the diff move?

A guard that asserts a diff *exactly* fails in two directions that render almost
identically — a new divergence and a silently fixed one both just say "the set
does not match" — and the "gone" branch usually ends with the one-line
re-baseline command. During work that is supposed to be behaviour-preserving,
the likeliest reason an expected entry disappeared is that the production path
lost the step that produced it. The guard caught a regression, then framed it as
progress and handed you the command that banks it as the new expected state.

1. State the direction first: entries added, entries gone, or both. Do not read
   a shrunk set as progress because the message says so.
2. For every gone entry, name the change that removed it and show the removal
   was intended. The mutation check (Step 5a) is the cheap proof — re-disable
   that change and confirm exactly that entry comes back, and that the entries
   attributed to other causes do not move.
3. Re-bless the baseline only after a stated cause, and only in the commit where
   the movement is the intent. In a behaviour-preserving commit the invariant is
   bidirectional: the baseline must not move in either direction, and "accept
   the diff" is not an available response.

When you own the guard, make its shrink branch demand a stated cause instead of
offering the accept command.

## Emergent multi-body systems

`GroundConflictDetector` re-times every pair when one pair changes, so a
one-pair unit test cannot show the effect of a change. Run the dense-scenario
replay regressions the subsystem doc names — see
[`docs/conflict-and-visual-detection.md`](../../../../docs/conflict-and-visual-detection.md).
