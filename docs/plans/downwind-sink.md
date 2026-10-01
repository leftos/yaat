# A held or extended downwind sinks to the rollout altitude

Index line: [MAIN.md](./MAIN.md), under **Bug reports and feature requests**. Found by montage clip A1 (`tools/montage/follow/A1`); the montage capture waits on this fix (user).

## Root cause (debugger, measured on the A1 run)

FOLLOW is not the cause. Every downwind, held or not, commits near abeam to descend to the base-to-final rollout altitude (KOAK 28R, C172: 415 ft MSL, ~406 ft AGL), and a held leg never cancels it.

- `DownwindPhase.OnTick`'s abeam trigger (`src/Yaat.Sim/Phases/Pattern/DownwindPhase.cs` ~:284-290) calls `ApplyPastAbeamDescentTargets` (~:539-545) once: `TargetAltitude` = the 3° path altitude at the rollout point (base extension 1.001 NM + turn radius 0.222 NM = 1.223 NM from the threshold), at the piston rate (700 fpm). It arms 0.28 NM before abeam (`AlongTrackToleranceNm` = 0.3).
- The hold branches (~:326-334 `IsExtended`/proximity, ~:374-382 the follow sequencing hold) only write `_altitudeFloor = ExtendedDownwindFloor(...)` (~:562-571) = min(TPA, 3° altitude at the aircraft's own along-track + r, current altitude) = 97–415 ft over the first NM past abeam — always below the aircraft, so the `Altitude <= _altitudeFloor` guard never arrests the descent. The comment at ~:547-552 ("a long extension levels back at pattern altitude") is false.
- Measured (A1, TPA 1009 MSL): descent starts t=138 at −0.277 NM from abeam, reaches 415 ft at t=188 (0.698 NM, exactly the nominal base trigger), then holds 415 ft for the rest of the extension (24 s in A1; 1.1 NM with an `EXT`, same profile with or without FOLLOW). The late base turn then leaves the aircraft below the 3° path, and `FinalApproachPhase` climbs it 415 → 460 ft.
- Origin: 3f1303f4 ("level off at the glideslope intercept altitude … while waiting for the TB command") — design intent, not a regression.

## Rulings (aviation-sim-expert; user picks on the open choices)

Grounding: AIM FIG 4-3-2/4-3-3 key 2; AC 90-66B §11.5 ("maintained until the aircraft is at least abeam the approach end"), Appendix A key 2 ("begin descent and turn base at approximately 45 degrees"), §11.3 (a common pattern altitude for traffic acquisition) — `.claude/reference/faa/ac-90-66b/ac-90-66b.md`.

1. **Held or extended downwind** (`EXT`, the proximity hold, the FOLLOW sequencing hold): a hold in force at abeam never starts the descent and keeps TPA until the base turn or `TB`; a hold that arrives mid-descent levels off where it is. **Never climbs back** (user) [J].
2. **Normal downwind: the descent is split between downwind and base** (user: same item). One constant ground gradient from abeam to the base-to-final rollout: the downwind targets, at the base trigger, `rollout + f × (TPA − rollout)` with `f = baseLen / (dwDescentLen + baseLen)` (`baseLen` the pattern width `PlanDescent` already uses, `dwDescentLen` abeam to the base trigger). Piston f ≈ 0.52 (C172 at KOAK turns base ~720 ft MSL, hand-computed), turboprop ≈ 0.45, **jet ≈ 0.41, the same gradient rule** (user) [J].
3. **The descent arms at abeam**, not 0.3 NM before: the abeam test alone drops `AlongTrackToleranceNm`; the base-turn trigger keeps it.
4. A base turned from TPA after a hold released at the nominal base point needs ~921 fpm against the piston ceiling of ~930 fpm: acceptable as an edge case, not pre-descended. Measure go-arounds and "arrives high" in `FollowPairTrajectoryTests` and `N342TFollowAfterExtendTests`; if held releases go around, the fallback (a held-leg floor past the nominal base point) comes back to the user.

## Fix

In `DownwindPhase.OnTick`:
1. Evaluate the hold (`IsExtended || holdForProximity || wantsSequenceHold`) **before** the abeam block; the abeam test is `aircraftAlongTrack >= _abeamAlongTrack`.
2. At abeam set `_pastAbeam` and start the base-speed deceleration; start the descent only when not held.
3. While held: on the first held tick latch `_altitudeFloor = Math.Min(Altitude, PatternAltitude)` once (a snapshotted bool on `DownwindPhaseDto`, so the latch never ratchets or rises), then each held tick `TargetAltitude = _altitudeFloor`, `DesiredVerticalRate = null`.
4. The descent (at abeam unheld, or on release past abeam before the base trigger) aims along one straight line from the aircraft's current altitude and position to the rollout altitude at the rollout point: at the base trigger, `rollout + (current − rollout) × baseLen / (remainingDw + baseLen)`, at the rate that reaches it over the remaining downwind at ground speed. At abeam from TPA that is ruling 2. A release at or past the base trigger is `BasePhase.PlanDescent`'s (unchanged; it plans from wherever the base starts).
5. Delete `ExtendedDownwindFloor`, fix the comment at ~:547-552 and `docs/approach-and-pattern-geometry.md` "Past-abeam descent target", citing AC 90-66B §11.5 and App. A key 2 beside AIM FIG 4-3-2.

Leave untouched: the short-approach branch of `ApplyPastAbeamDescentTargets`, `BasePhase.PlanDescent`, the hold decisions in `AirborneFollowHelper`.

## Tests

Keep green: `PatternTurbineTpaTests.DownwindPastAbeam_TargetsGlideslopeInterceptAtRollout_NotFractionOfTpa`, `ExtendedDownwindNoClimbTests.ExtendedDownwind_PastBaseTurn_HoldsAltitude_DoesNotClimb`, `FollowPairTrajectoryTests` (11), `FollowPatternSequencingAuditTests`, `VfrPatternFollowSequencingTests`, `FollowRunwaylessLeadFromPatternTests.ExtensionBreakOff_*`, `BaseFollowSpacingTests` (glidepath ±100), `PatternCircuitE2ETests`, `N342TFollowAfterExtendTests`, `VfrFollowSequencesToFinalTests`.

New, red today (debugger wrote and ran them, then removed them from its tree): `tests/Yaat.Sim.Tests/Simulation/ExtendedDownwindAltitudeTests.cs` with
- `ExtendedDownwind_HoldsPatternAltitude_UntilBaseTurn`: KOAK 28R right traffic, C172 placed on the downwind 0.5 NM before abeam at TPA via `PatternBuilder.BuildCircuit(... PatternEntryLeg.Downwind ...)`, cleared to land, `EXT`, 120 one-second ticks of `FlightPhysics.Update` + `PhaseRunner.Tick`; asserts the leg was held > 0.2 NM past the base trigger (`BaseTurnAlongTrack(wp) - DownwindPhase.AlongTrackToleranceNm`) and the lowest downwind altitude ≥ TPA − 100. Red: "Held downwind sank to 415 ft MSL at t=61 (along-track 0.72 nm from abeam, target 415)".
- `Follow_HeldDownwindBehindStraightInLead_HoldsPatternAltitude`: follower 0.2 NM before abeam, C172 lead on a 2 NM straight-in (`PatternEntryLeg.Final`, 318 ft/NM above field elevation, 62 kt), both cleared to land, `ReportTrafficInSightForcedCommand(lead)` then `FOLLOW N52417`, 300 ticks; same assertions. Red: "sank to 415 ft MSL at t=52 (along-track 0.82 nm)".
Both measure flown altitude against TPA, not the phase's own target.

## Tooling note

`tools/bug_bundle.py track` shows no per-phase internals; the decisive measurement read `DownwindPhaseDto` (`PastAbeam`, `AltitudeFloor`, `AbeamAlongTrack`, `BaseTurnAlongTrack`) out of the archive's `snapshots/NNN.json.br` with a throwaway probe. A `phase-state --callsign X` subcommand would save the next investigation that script (CLAUDE.md "Extend the tools").
