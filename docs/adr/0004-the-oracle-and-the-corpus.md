---
status: accepted
date: 2026-09-02
---

# How state-equivalence is verified, and what happens to the recordings

The contract in [0001](0001-state-equivalence-is-the-tick-contract.md) is only worth as much as its
test. Today's defence is six per-feature parity tests: of 331 server test methods that drive the real
tick, twelve assert that a specific engine step fires on the server path. That is a sampling
strategy, and it guards feature seven only if someone remembers to write test seven.

**The oracle** runs one scenario under several run kinds and compares the resulting state. During the
refactor it is a full per-second snapshot diff, which names the tick and the field. Once the paths
agree it becomes a per-second snapshot hash plus a step trace, cheap enough to run over the whole
corpus continuously, with the full diff kept as the drill-down when a hash goes red.

Nearly every legitimate difference is a named member of the host or the run profile, so the interface
is itself the enumerated list. A small typed exemption registry exists anyway, as the reviewable home
for what implementation turns up — additions arrive as diffs, and it follows the curated-exemption
pattern `TrackDispatchParityTests` already proves out in this repo.

**Recordings.** Every desync is triaged to a named cause before anything happens to it. If the
divergence turns out to be an over-broad assertion, the test is corrected and the recording stands;
if it genuinely desynced, it is deleted. Recordings are never re-baselined: rewriting a recording's
snapshot stream against the new engine would freeze any bug in the refactor in as ground truth and
turn an independent check into a tautology. One desync from an unexpected cause stops the work until
it is understood — an unexplained cause is a finding about the refactor, not about the corpus.

**Schema growth.** New snapshot fields migrate old recordings forward through
`SnapshotSchemaMigrator` with an explicit unknown sentinel rather than a plausible default, so the
oracle can distinguish "no evidence here" from "the paths agree here".
