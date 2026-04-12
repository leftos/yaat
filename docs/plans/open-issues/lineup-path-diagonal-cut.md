# LineUp phase cuts diagonally across grass instead of following taxiway path

## Summary

When an aircraft is cleared for takeoff from a taxiway hold-short position and
enters `LineUpPhase`, it often adopts a straight-line heading toward the runway
centerline target that takes it **off the painted taxiway surface and across
the grass** before ever entering the runway. It should instead follow the edge
and the fillet arc between the taxiway and the runway surface.

Diagonal cutting is legitimate in narrow circumstances — if the fillet arc
would make the aircraft span the runway diagonally and lose meaningful takeoff
distance — but at nearly-aligned intersections (e.g. SFO 28R/E where the angle
is small), the aircraft should simply follow the edge → fillet → centerline
path like any other taxi move.

## Observation

Captured via `SfoLineupDiagonalTests.Diagnostic_RecordLineupTicks` → `.tmp/sfo-lineup28r-ticks.csv`
→ rendered with LayoutInspector. N346G at SFO RWY 28R from taxiway E:

- CTO issued at t=250
- Aircraft enters `LineUpPhase` shortly after
- LineUp trajectory is a single diagonal straight line from the hold-short
  position to a point on the runway centerline
- The line visibly crosses off-surface terrain between E and 28R instead of
  staying on the painted taxiway/fillet
- Aircraft enters `TakeoffPhase` at t=271 — correct outcome, wrong path

## Expected behavior

- LineUp should compute a taxi path (edge + fillet arc + on-runway target)
  that stays on painted surface, same as any other taxi move.
- Only when the intersection angle is steep enough that the fillet arc would
  eat significant runway length should the phase allow a straight diagonal
  cut. This should be a measured decision (remaining runway ft after the
  fillet vs. total runway length), not the default.
- Even in the steep-angle diagonal case, the aircraft should taxi to the
  runway edge first and then begin the diagonal — not start the diagonal from
  the hold-short line.

## Related tests

- `SfoLineupDiagonalTests.N346G_LineUp28R_TickByTickTrace` — E2E replay test
  that currently passes against the buggy path but had its completion budget
  bumped from 20 s to 35 s when the stop-snap fix (pre-arrival brake clamp)
  made the diagonal traversal noticeably slower. Once this bug is fixed, the
  budget should be restored to ~15-20 s (taxi-path lineup is faster than
  diagonal cutting because the turn is absorbed by the fillet arc).
- `SfoLineupDiagonalTests.Diagnostic_RecordLineupTicks` — writes
  `.tmp/sfo-lineup28r-ticks.csv` for LayoutInspector animation.

## Files to read

- `src/Yaat.Sim/Phases/Tower/LineUpPhase.cs` — LineUp state machine and target
  computation. Stage 1 (NavigateToTarget) is the likely culprit: it appears to
  set an on-runway target and walk to it without consulting the taxi graph.
- `src/Yaat.Sim/Phases/Tower/LinedUpAndWaitingPhase.cs` — downstream phase.
- `src/Yaat.Sim/Phases/Ground/GroundNavigator.cs` — nav that could be reused
  if LineUp switched to a taxi-graph-based path.

## Where this came from

Discovered during the stop-snap fix (commit that added pre-arrival brake clamp
to `GroundNavigator.Tick()`). The kinematic brake clamp applies every physics
sub-tick, so the aircraft decelerates more aggressively for the sharp "corner"
at the hold-short → runway transition, revealing that the transition path
crosses non-surface terrain.
