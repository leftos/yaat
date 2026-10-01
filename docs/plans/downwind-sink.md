# A held or extended downwind sinks to the rollout altitude

Index line: [MAIN.md](./MAIN.md), under **Bug reports and feature requests**. Found by montage clip A1 (`tools/montage/follow/A1`); the montage capture waits on this fix (user).

## Root cause (debugger, measured on the A1 run)

FOLLOW is not the cause. Every downwind, held or not, commits near abeam to descend to the base-to-final rollout altitude (KOAK 28R, C172: 415 ft MSL, ~406 ft AGL), and a held leg never cancels it.

- `DownwindPhase.OnTick`'s abeam trigger (`src/Yaat.Sim/Phases/Pattern/DownwindPhase.cs` ~:284-290) calls `ApplyPastAbeamDescentTargets` (~:539-545) once: `TargetAltitude` = the 3° path altitude at the rollout point (base extension 1.001 NM + turn radius 0.222 NM = 1.223 NM from the threshold), at the piston rate (700 fpm). It arms 0.28 NM before abeam (`AlongTrackToleranceNm` = 0.3).
- The hold branches (~:326-334 `IsExtended`/proximity, ~:374-382 the follow sequencing hold) only write `_altitudeFloor = ExtendedDownwindFloor(...)` (~:562-571) = min(TPA, 3° altitude at the aircraft's own along-track + r, current altitude) = 97–415 ft over the first NM past abeam — always below the aircraft, so the `Altitude <= _altitudeFloor` guard never arrests the descent. The comment at ~:547-552 ("a long extension levels back at pattern altitude") is false.
- Measured (A1, TPA 1009 MSL): descent starts t=138 at −0.277 NM from abeam, reaches 415 ft at t=188 (0.698 NM, exactly the nominal base trigger), then holds 415 ft for the rest of the extension (24 s in A1; 1.1 NM with an `EXT`, same profile with or without FOLLOW). The late base turn then leaves the aircraft below the 3° path, and `FinalApproachPhase` climbs it 415 → 460 ft.
- Origin: 3f1303f4 ("level off at the glideslope intercept altitude … while waiting for the TB command") — design intent, not a regression.

## Proposed fix (subject to the aviation ruling below)

In `DownwindPhase.OnTick`:
1. Evaluate the hold (`IsExtended || holdForProximity || wantsSequenceHold`, today ~:353-356) **before** the abeam block.
2. At the abeam trigger set `_pastAbeam` and start the base-speed deceleration, but skip `ApplyPastAbeamDescentTargets` while held.
3. While held: on the first held tick latch `_altitudeFloor = Math.Min(ctx.Aircraft.Altitude, Waypoints.PatternAltitude)` once (a new snapshotted bool on `DownwindPhaseDto` so the latch does not ratchet), then each held tick write `TargetAltitude = _altitudeFloor`, `DesiredVerticalRate = null`.
4. On release while the leg continues past abeam: call `ApplyPastAbeamDescentTargets(ctx, alongTrack)` once and clear the latch; a release at the base turn is covered by `BasePhase.PlanDescent` (`BasePhase.cs` ~:156-204), which plans from the held altitude.
5. Delete `ExtendedDownwindFloor` (dead), fix the comment at ~:547-552 and `docs/approach-and-pattern-geometry.md` "Past-abeam descent target" (~:109-118).

Leave untouched: the normal and SA branches of `ApplyPastAbeamDescentTargets`, `BasePhase.PlanDescent`, the hold decisions in `AirborneFollowHelper`.

Risk to measure: a base turned from TPA at the nominal point needs ~921 fpm against the piston base ceiling of min(1500, 69.8 kt × tan 7.5° × 101.27) = 930 fpm — feasible at the edge; check it does not arrive high and go around (`FollowPairTrajectoryTests` landing-order checks would catch it).

## Open for `aviation-sim-expert` (before the brief)

1. A held or extended downwind: hold TPA until the base turn (or `TB`), or arrest at whatever altitude the hold catches it (A1's hold caught it ~70 ft into the descent)?
2. A normal downwind: may it lose the whole TPA-to-rollout drop (~594 ft for a C172 at KOAK) before the base turn and fly the base level, as today, or should the downwind descent be partial with the rest on base? AIM FIG 4-3-2 key 2 says only "maintain pattern altitude until abeam".
3. Should the descent arm 0.3 NM before abeam (the shared `AlongTrackToleranceNm`) or at abeam?

## Tests

Keep green: `PatternTurbineTpaTests.DownwindPastAbeam_TargetsGlideslopeInterceptAtRollout_NotFractionOfTpa`, `ExtendedDownwindNoClimbTests.ExtendedDownwind_PastBaseTurn_HoldsAltitude_DoesNotClimb`, `FollowPairTrajectoryTests` (11), `FollowPatternSequencingAuditTests`, `VfrPatternFollowSequencingTests`, `FollowRunwaylessLeadFromPatternTests.ExtensionBreakOff_*`, `BaseFollowSpacingTests` (glidepath ±100), `PatternCircuitE2ETests`, `N342TFollowAfterExtendTests`, `VfrFollowSequencesToFinalTests`.

New, red today (debugger wrote and ran them, then removed them from its tree): `tests/Yaat.Sim.Tests/Simulation/ExtendedDownwindAltitudeTests.cs` with
- `ExtendedDownwind_HoldsPatternAltitude_UntilBaseTurn`: KOAK 28R right traffic, C172 placed on the downwind 0.5 NM before abeam at TPA via `PatternBuilder.BuildCircuit(... PatternEntryLeg.Downwind ...)`, cleared to land, `EXT`, 120 one-second ticks of `FlightPhysics.Update` + `PhaseRunner.Tick`; asserts the leg was held > 0.2 NM past the base trigger (`BaseTurnAlongTrack(wp) - DownwindPhase.AlongTrackToleranceNm`) and the lowest downwind altitude ≥ TPA − 100. Red: "Held downwind sank to 415 ft MSL at t=61 (along-track 0.72 nm from abeam, target 415)".
- `Follow_HeldDownwindBehindStraightInLead_HoldsPatternAltitude`: follower 0.2 NM before abeam, C172 lead on a 2 NM straight-in (`PatternEntryLeg.Final`, 318 ft/NM above field elevation, 62 kt), both cleared to land, `ReportTrafficInSightForcedCommand(lead)` then `FOLLOW N52417`, 300 ticks; same assertions. Red: "sank to 415 ft MSL at t=52 (along-track 0.82 nm)".
Both measure flown altitude against TPA, not the phase's own target.

## Tooling note

`tools/bug_bundle.py track` shows no per-phase internals; the decisive measurement read `DownwindPhaseDto` (`PastAbeam`, `AltitudeFloor`, `AbeamAlongTrack`, `BaseTurnAlongTrack`) out of the archive's `snapshots/NNN.json.br` with a throwaway probe. A `phase-state --callsign X` subcommand would save the next investigation that script (CLAUDE.md "Extend the tools").
