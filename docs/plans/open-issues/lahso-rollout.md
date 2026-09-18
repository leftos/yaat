# LAHSO rollout: the lander stops past the hold-short point and can never exit before it

Found 2026-09-18 while clearing unused-value findings: `LandingPhase.TickRollout` computed the LAHSO-required decel into a
local nothing read. `aviation-sim-expert` confirmed the defect and found three more in the same arm. No test has ever ticked
the LAHSO rollout: every test in `tests/Yaat.Sim.Tests/LahsoTests.cs` stops at command dispatch or calls
`RunwayIntersectionCalculator` directly, and `LandingPhaseRestoreTests` round-trips the fields with `HasLahso = false`.

## Defects (`src/Yaat.Sim/Phases/Tower/LandingPhase.cs`, line numbers as of `6c5d81a4`)

1. **Stops past the point.** The LAHSO block (~L733-757) computed `RolloutBraking.RequiredDecelKtsPerSec(gs, 0, distToHoldShort)`
   into a local nothing read (the no-op branch was deleted in the strict-formatting pass; the behaviour is unchanged). The published rate is `decelRateOverride` (~L798/853) and the published target is coast speed (~L791),
   so the aircraft coasts at 40 kt until `distToHoldShort <= 0`, then brakes at 5 kt/s: a B738 stops ~225 ft past the point.
2. **Can never exit before the point.** `!_hasLahso` (~L875) blocks the exit handoff entirely.
3. **Exit planner ignores the point.** `ResolveNextCandidate` (~L763-766) is not LAHSO-filtered; it can commit to, and brake
   for, an exit beyond the hold-short point.
4. **Wrong distance.** ~L735 uses great-circle `GeoMath.DistanceNm` from the threshold; off-centerline it under-reads and biases
   late. Use the along-track form the phase already uses at ~L770.

## The rule

- AIM 4-3-11.b.6: a pilot who accepts a LAHSO clearance "should land and exit the runway at the first convenient taxiway (unless
  directed otherwise) before reaching the hold short point. Otherwise, the pilot must stop and hold at the hold short point."
  Exiting early is the preferred branch; stopping short is the fallback.
- 7110.65 §3-10-4.a.2: the arrival has "completed landing roll and will hold short of the intersection/flight path";
  §3-10-4.b.1 NOTE: the point may be a runway, a taxiway or a predetermined point.
- AIM 4-3-11.b.2: stop within the ALD, which runs landing threshold → hold-short point, i.e. `_lahsoHoldShortDistNm`.
- Stopped means all parts short of the marking (AIM 2-3-5.a.1 / 4-3-21.b), the datum the phase already uses for "clear".

Local references only: `.claude/reference/faa/aim/`, `.claude/reference/faa/7110.65/`.

## Ruling (while `distToHoldShort > 0`, after the exit planner has set `targetSpeed` / `decelRateOverride`)

1. `stopDist = distToHoldShort − LahsoStopMarginNm`, margin **50 ft**. A judgement call: `ComputeHoldShortDistanceNm` already
   carries the 200 ft RSA plus half the crossing width, so the margin covers only discrete-tick overshoot (one 0.25 s sub-tick
   at 40 kt ≈ 17 ft), in the spirit of `TurnOffSpeedToleranceKts`.
2. New `RolloutBraking.MaxEntrySpeedKts(distNm, rate)` = `sqrt(2·a·d)`;
   `targetSpeed = Math.Min(targetSpeed, MaxEntrySpeedKts(stopDist, FirmBrakingRateKtsPerSec))`. The ceiling decays to 0 at the
   point: no early stop, no coast to the line.
3. `decelRateOverride = Math.Max(decelRateOverride, RequiredDecelKtsPerSec(gs, 0, stopDist))` — LAHSO never lowers the exit
   rate — then clamp to `CategoryPerformance.ExpediteExitDecelRate(cat)`.
4. Over capability: clamp and overrun, keep `TargetSpeed = 0`, `Log.LogWarning` the overrun in feet. No post-touchdown
   go-around (AIM 4-3-11.b.5 permits a rejected landing; that is a separate feature).
5. Exit before the point wins: filter `ResolveNextCandidate` to branch points at or before `stopDist`, drop `!_hasLahso` from
   the handoff gate so a qualifying candidate hands off, and null `Phases.LahsoHoldShort` at handoff so `PhaseRunner`
   (~L64-79) does not append the post-LAHSO chain.

## Tests, failing first (real layout, production tick loop)

`sfo.geojson`, landing 28R with `LAHSO 1L`, B738 from short final:

- Every tick: along-track distance from the landing threshold ≤ `LahsoTarget.DistFromThresholdNm`, 0 ft tolerance.
- Within 100 ft of the point: GS ≤ 25 kt. Within 55 ft: GS ≤ 1 kt.
- Final stop inside `[DistFromThresholdNm − 400 ft, DistFromThresholdNm]`.
- Expected today: 40 kt at 100 ft out and a stop ~225 ft past — both assertions red.
- Second test for ruling 5: an exit is committed whose branch point is before `DistFromThresholdNm`, the aircraft hands off to
  the exit, and `LahsoHoldShort` is cleared.
- A snapshot round-trip with `HasLahso = true` mid-rollout (the restore tests never cover it).

## Steps

- [ ] Write the two E2E tests and the restore test; confirm the first two are red for the stated reason
- [ ] Rulings 1-4 (stop short); first test green
- [ ] Ruling 5 (exit before the point); second test green
- [ ] `aviation-sim-expert` review of the diff; `docs/landing-and-runway-exit.md` gains a LAHSO section; `USER_GUIDE.md` /
      `COMMANDS.md` if the LAHSO command text changes meaning; CHANGELOG bullets
- [ ] `pwsh tools/test-all.ps1`; a landing replay that desyncs is handled per `feedback_ship_fix_delete_desynced_recordings`

## Follow-up, not this fix

An ALD acceptance gate at clearance time (AIM 4-3-11.b.2/b.3): refuse or warn on `LAHSO` when the available landing distance
is below what the type needs.
