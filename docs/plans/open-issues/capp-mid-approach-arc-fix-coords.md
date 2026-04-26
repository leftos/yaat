# CAPP I17R fired mid-approach produces invalid arc-fix coordinates

## Summary

When `CAPP I17R` fires after the aircraft has already sequenced past the IAF
(initial approach fix) and is mid-approach, one of the SCOLA1 RW17R arc fixes
ends up with an out-of-range longitude that crashes
`MagneticDeclination.GetDeclination` on the next physics tick.

This is a **separate, latent bug** that was masked by another defect. PR #143's
Fix A (compound `DCT;ERD` queueing in `ReplayCommand`) unblocked the recording's
`AT KLOCK CAPP I17R` action so it now fires during replay — which surfaces this
bug.

## Reproduction

`tests/Yaat.Sim.Tests/Simulation/Issue97SpeedConstraintTests.cs::SWA11_MeetsSpeedConstraintsAtFixes`
is currently `[Fact(Skip = "...")]` referencing this writeup.

Recording: `tests/Yaat.Sim.Tests/TestData/issue95-capp-ambiguity-recording.json`
(SWA11, B738, KPHX→KRNO on SCOLA1 STAR with `AT KLOCK CAPP I17R` queued at t=71).

To reproduce, remove the `Skip` and run that test.

Observed sequence:

```
t=71:   replay positions SWA11 ~150 nm out, AT KLOCK CAPP I17R queued
t=74:   sequenced past CHIME (IAS=210, alt=11698)
t=76:   sequenced past KLOCK → AT condition fires CAPP I17R
t=78:   sequenced past BELBE (IAS=250, alt=8748)
t=79:   sequenced past ARC05 (IAS=250, alt=7752)
t=80:   sequenced past ARC29 (IAS=250, alt=6580)
crash:  Geo.Coordinate ctor: ArgumentOutOfRangeException (longitude)
        at MagneticDeclination.GetDeclination
        at FlightPhysics.Update
```

## Hypothesis

`CAPP I17R` at t=76 rebuilds the approach navigation route. The SCOLA1 RW17R
transition includes ARC fixes (constant-radius DME arcs) whose lat/lon are
computed from a center fix + radius + radial. When the approach is rebuilt
mid-flight (aircraft already past one or more arc start/end fixes), the arc
parameter math may be using stale or incorrectly-sequenced state, producing
NaN or out-of-range coordinates.

Crash hits longitude validation in `Geo.Coordinate`, which clamps to
`[-180, 180]`. The actual produced longitude is presumably NaN, infinite, or
catastrophically large.

## Likely files to investigate

- `src/Yaat.Sim/Commands/ApproachCommandHandler.cs` — `TryClearedApproach` and
  the path that builds approach navigation when the aircraft is already past
  the IAF.
- ARC-leg construction in CIFP parsing or approach assembly. Search for
  `LegType.ArcToFix`, `LegType.ConstantRadiusArc`, `FixRadius`, `Theta`,
  `Rho`, or `RF` legs.
- `src/Yaat.Sim/Data/Vnas/CifpParser.cs` ARC-fix coordinate computation —
  there's a recent off-by-one fix referenced in `CLAUDE.md` for procedure
  leg field offsets that touched arc fields.
- `src/Yaat.Sim/Data/Vnas/CifpProcedureBuilder.cs` (or equivalent) — look for
  where ARC legs are projected to lat/lon points and whether the "you're
  already past this" branch handles missing prior-fix context.

## Suggested investigation steps

1. **Localize the bad fix.** Insert a guard in
   `ApproachNavigationPhase` (or wherever the route is materialized after
   CAPP) that logs each new nav target's lat/lon. Replay the recording with
   logging on; identify which fix has the bad coordinate. Likely ARC05 or
   ARC29 or the next fix after them.
2. **Check the leg's source data.** Use `tools/Yaat.CifpInspector` against
   `FAACIFP18.gz` for KRNO I17R approach. Verify the ARC legs' radius, theta,
   rho, and center fix decoded sanely (cifparse upstream is the canonical
   reference per `CLAUDE.md`).
3. **Test the rebuild path in isolation.** Write a unit test that:
   - Spawns a B738 already past KLOCK on the SCOLA1 STAR
   - Issues `CAPP I17R`
   - Asserts every fix in `aircraft.Targets.NavigationRoute` has a valid
     `Position` (lat in [-90, 90], lon in [-180, 180])
4. **Compare against CAPP-from-IAF.** Build the same approach for an aircraft
   *before* KLOCK and compare the resulting nav-route fix coordinates against
   the post-KLOCK case. The diff should localize where stale state leaks in.

## Why this wasn't caught before

`SimulationEngine.ReplayCommand` (pre-fix) silently dropped any command
string that failed `CommandParser.Parse` — which includes every compound
command using `;` or `,`. So `AT KLOCK CAPP I17R` never fired during
replay-based tests. The crash path was unreachable.

PR #143 Fix A in `src/Yaat.Sim/Simulation/SimulationEngine.cs` lets parse
failures fall through to `ParseCompound`, which is correct behavior — and
exposes this bug.

## Scope guard

This is **out of scope for PR #143**. That PR is fixing the user-reported
ERD/go-around defects from the S2-OAK-4 bundle. The skipped test documents
the regression and is a clear handoff for the next person.

## Acceptance

When fixing this, the acceptance is:

- [ ] Remove `Skip` from
      `Issue97SpeedConstraintTests.SWA11_MeetsSpeedConstraintsAtFixes`
- [ ] That test runs to completion without a coordinate exception
- [ ] Either keep its existing speed-tracking output or replace with a real
      assertion about deceleration on speed-constrained fixes (the originally
      intended scope)
- [ ] Add a focused unit test for the "CAPP rebuilt past an arc fix" case so
      this can't regress silently again
