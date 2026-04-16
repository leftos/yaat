# VFR aircraft on short final should be able to switch runways

## Bug (user's report)

> N42416 kept telling me it was unable to switch runways because it was on short final, but they should have been able to. They're VFR, going very slow, there was plenty of time to reconfigure for a different runway.

## Reproduction

- Recording: `tests/Yaat.Sim.Tests/TestData/s2-oak3-vfr-sequencing-recording.yaat-bug-report-bundle.zip`
- Scenario: S2-OAK-3 (1) | VFR Sequencing
- Aircraft: **N42416** (VFR, slow)
- Approximate time: TBD. Diagnostic test: replay actions, find where `RWY` is sent and the rejection is returned.

## Suspected code

- `src/Yaat.Sim/Commands/PatternCommandHandler.cs:92-147` — `TryEnterPattern()`; the relevant guard around line 143:
  ```csharp
  if (!aircraft.IsOnGround && entryLeg == PatternEntryLeg.Final)
  {
      // compute total turn degrees and required arc length
      if ((totalTurnDeg > 180) && (arcNm > distToEntry))
          return new CommandResult(false, "Unable, short final");
  }
  ```
  This applies to **all** aircraft on final, IFR and VFR alike. It checks only turn geometry.
- `src/Yaat.Sim/Phases/Tower/FinalApproachPhase.cs:367` — precedent for VFR bypass: `if (ctx.Aircraft.IsVfr) { _interceptChecked = true; return; }`. Use the same IsVfr distinction here.

## Proposed fix direction

- **VFR branch**: when `aircraft.IsVfr`, relax the geometry threshold. VFR pilots can accept tighter turns at slower speeds (pattern-style maneuvering) — the IFR "180° + arc exceeds distance" guard is over-conservative. Either skip the guard or scale thresholds with groundspeed (slow = more permissive).
- **IFR branch**: keep current behavior.
- Implementer should check 7110.65 §3-9-6 (circling) and AIM 4-3-2 (traffic patterns) for the realistic VFR envelope; the `aviation-sim-expert` agent has access to `.claude/reference/faa/` — use it.

## Acceptance criteria

- VFR aircraft on final at low speed can accept `RWY {other}` when the turn is feasible at VFR-relaxed thresholds.
- IFR aircraft on short final still get "Unable, short final" with the existing guard — **regression test required**.
- Test must cover: (a) VFR accept case matching the recording, (b) VFR reject case when truly impossibly close, (c) IFR reject case preserved.

## TDD note

Follow `docs/e2e-tdd-issue-debugging.md`.

- Create `tests/Yaat.Sim.Tests/Simulation/VfrShortFinalRunwayChangeTests.cs`.
- For cases (b) and (c), fabricate aircraft states directly rather than trying to contrive them from the recording.
- Aviation review: request `aviation-sim-expert`; include the FAA-local-reference reminder from CLAUDE.md.
