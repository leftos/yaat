# Landing Rollout & Runway Exit — Design & Implementation

> This is the **ground** half of an arrival. For the **airborne** half upstream of touchdown — approach intercept, pattern legs, holding, and glideslope geometry — see [approach-and-pattern-geometry.md](approach-and-pattern-geometry.md).

> **Everything below the touchdown flip is GROUND-FRAME.** On the ground `AircraftState.IndicatedAirspeed` carries wheel speed;
> coast/exit speeds, `DesiredDecelRate`, and braking-distance math are all ground-frame quantities. Wind enters exactly once,
> at the touchdown conversion (`GroundFrame.EnterGround`: wheel speed = touchdown TAS − headwind component), so a headwind
> shortens the rollout by the v² law and makes earlier exits reachable. See [flight-physics.md](flight-physics.md).

## Core Principle: Analog, Not Node-Based

The runway exit system treats the runway as a continuous surface, not a graph of nodes. The aircraft rolls along the runway heading, measures distances to exits, and plans braking kinematically. There is no node-walking on the runway — nodes are only used once the aircraft commits to a taxiway.

**Why this matters**: Node-based approaches (walk to node X, arrive at speed Y, turn) create brittleness. Discrete-tick racing between guards, degenerate virtual segments, and instant phase transitions all stem from trying to deliver the aircraft to a precise node at a precise speed. The analog approach avoids all of this by treating the runway as a driving surface with measurable distances.

## Phase Flow

```
LandingPhase (rollout)
  - Steers along runway heading (centerline correction via XTE)
  - Decelerates toward coast speed
  - Searches for reachable exits ahead
  - Plans braking based on distance to exit
  - Hands off at coast speed with enough room for the turn
  ↓
RunwayExitPhase
  - Rolls along runway heading at coast speed (no node-walking)
  - Continuously searches for exits ahead (analog, distance-based)
  - When an exit is found: builds a virtual segment from aircraft → branch,
    appended to the real exit path (branch → hold-short)
  - Hands the full route to GroundNavigator
  ↓
GroundNavigator
  - Steers through the route with turn anticipation
  - Brakes using backward-propagated speed constraints
  - Handles the turn and arrival at hold-short
```

## Touchdown Aiming Point

A real 3° approach crosses the threshold at a crossing height and reaches the surface at the aiming point (~1,000 ft in), not on the threshold. The aiming point is **not a tunable** — it falls out of the crossing height: `aimPoint = crossingHeight / tan(angle)`.

`CategoryPerformance.WheelCrossingHeightFt` is the input (jet 30 ft, turboprop 25, piston 20, helicopter 0), and `GlideSlopeGeometry.AltitudeAtDistance(distNm, thresholdElev, category)` builds the path from it. That gives aiming points of 572 / 477 / 382 ft.

These are **wheel** crossing heights, per AIM 1-1-9.d.7 ("a comfortable wheel crossing height is approximately 20 to 30 feet, depending on the type of aircraft") — deliberately *not* the 30–50 ft published TCH of AIM 5-4-5.b.3, which AIM 1-1-9.d.6 defines as the height of the glide slope *antenna*. YAAT models a single point that becomes the wheels at touchdown, so the wheel band is the one that applies.

`FinalApproachPhase` completes (hands to `LandingPhase`) at `agl < 30`, near the threshold, **or once the aircraft passes the threshold**. That last condition is a safety net: a high/rushed approach that crosses still above the 50 ft AGL completion band hands off to the flare (or a stabilization-gate go-around) instead of flying past the threshold and climbing away, tracking the glideslope that rises again beyond it (distance-to-threshold is unsigned).

## Occupied-runway go-around

`OccupiedRunwayGoAround.TryTrigger` (`Phases/Tower/OccupiedRunwayGoAround.cs`) runs from `FinalApproachPhase.OnTick` right after the
landing-clearance check, before the no-clearance branches. Gate: session setting `AutoGoAroundOnOccupiedRunway` (default on for live
sessions, `false` in `SimScenarioState` so old recordings replay unchanged), fixed-wing, not CLANDF, `RunwayOccupancy.SecondsToLandingThreshold`
≤ 30 s, AGL ≥ 50 ft above the runway end. §3-10-3 is a "does not cross the landing threshold until" rule, so every occupant on the
pavement is judged where it will be when the arrival reaches the threshold (present ground speed held), by what it did on this runway:
**landed here** (`LandingPhase`/`RunwayExitPhase`, a `StopAndGoPhase` still braking toward its stop, or a phase-less `Landing`) → a.1 landmark from the **landing**
threshold (`SameRunwaySeparation`: Cat I 3,000 ft / Cat II 4,500 ft / no exception when either is Cat III — no motion requirement); **departed here** (`TakeoffPhase`/`InitialClimbPhase`, a touch-and-go, a stop-and-go once stopped, a low approach past the landing threshold, or a phase-less `Departing`) →
a.2: crossed the runway end, or airborne/rolling and projected past 3,000 / 4,500 / 6,000 ft; **neither** (lined up, holding in
position, crossing, parked) blocks at any distance. An aircraft past the runway's hold line off the pavement (`RunwayUseKind.Fouling`) blocks only with a part inside `OccupiedRunwayGoAround.GoAroundEdgeBandFt` of the centreline; a crossing projects clear only if it decelerates to its commanded speed and a commanded stop never clears. A helicopter arrival never triggers it (§3-10-3.a.3). The pilot line is spoken with its reason
(`PilotResponder.BuildGoingAroundTrafficOnRunway`, AIM 5-2-5.9 / 5-5-5.a.2) and the go-around itself goes through `GoAroundHelper.Trigger`.
A separate terminal entry names the blocking aircraft and the branch that blocked, so an instructor can tell a sequencing
go-around from one their own crossing caused without replaying the session.

**An option on the runway is an arrival until touchdown and a departure after (7110.65 3-8-2).** A touch-and-go counts as departing from touchdown and a stop-and-go from its complete stop (`SameRunwaySeparation.IsOptionCountedAsDeparting`); the classifier flips both to `Departing` at liftoff, and the ground interval before it is judged by §3-10-3.a.2, never by the a.1 landed credit (`SameRunwaySeparation.IsOptionOnTheGround`).

A touch-and-go on the ground is projected airborne through the rest of its rollout dwell and its re-acceleration (`SameRunwaySeparation.WillBeFlying`), so fewer arrivals go around behind one.

A stop-and-go stopped before its second roll blocks (a.2 is not met), and one still braking is a landed aircraft: past the landmark no go-around, inside it the reason reads "landing rollout, N ft down, not clear in S s".

**A landed occupant is projected off the runway, not assumed to still be on it.** The authority is **§3-10-6.a** (anticipating
separation: a landing clearance "need not be withheld if you observe the positions of the aircraft and determine that prescribed
runway separation *will exist when the aircraft crosses the landing threshold*"); §3-10-3.a.1 states the condition, §3-10-6.a
authorises anticipating it. `WillBeClearOfRunway` asks whether the occupant will be clear **by the arrival's threshold crossing**,
using the exit the occupant is already committed to (`LandingPhase.CandidateExit` / `RunwayExitPhase`): for a rolling-out leader the
braking leg to the exit's branch point at the constant-deceleration mean of present ground speed and the exit's turn-off speed, then
the exit path at that speed; for a leader already on its exit, the remainder at the exit route's taxi ceiling and then v²/2a of
braking to the stop; then the tail-clearance term — AIM 2-3-5.a.1 / 4-3-21.b, an aircraft is not clear until **all parts** are past the holding position marking, so
the protection measures to the clear point (half the aircraft's length past the bar), and `RunwayExitPhase` stops `RunwayTailClearance.MarginFt` (10 ft) beyond it, unless another runway's bar lies closer: a crossover too short for that, so the nose stops short of the next runway's bar and the tail stays inside the vacated runway's line, never clears (see "Crossovers too short to clear"). A leader on its exit is modelled braking to rest at the clear point, though it brakes to rest the margin beyond it and passes the clear point sooner, so it is predicted clear later than it is: the safe side. `SameRunwayArrivalProtection` owns that arithmetic so
the go-around and the spacing pass cannot disagree about when an aircraft is clear. The spacing pass itself now works from 20 NM on
the expected approach and, inside 10 NM with the tower simulated, holds the follower at Vapp ("reduce to final approach speed") all the
way to touchdown — see `docs/scenario-loading-and-generation.md` § `ApplySameRunwayArrivalProtection`.

Fail-closed everywhere the answer is unknown: a stopped occupant, an unresolved exit, or a missing ground layout all read as "not
clear". **§3-10-6.b forbids anticipating separation for LUAW** — satisfied structurally rather than by a check, because a lined-up
occupant classifies `OnSurface`, which is neither the landed nor the departed branch and so never reaches the projection. Before this
existed the code passed `landerClearOfRunway: false` unconditionally, which for a Cat III pair (no a.1 landmark exception) demanded the
runway clear a full `DecisionWindowSeconds` — 30 s — *before* the threshold crossing, stricter than both the controller rule and the
pilot model.

Measured end-to-end through the production tick loop (`TouchdownPointTests`), touchdown lands at **B738 1,367 ft / DH8D 798 ft / C172 401 ft** past the threshold — all inside the AIM 2-1-5.b touchdown zone (100–3,000 ft), with the transports on or just past the AIM 2-3-3.b.4 aiming point markings and the light single near the numbers.

Each figure is `aimPoint + flareFloat`, and **the two are coupled**: raising a crossing height moves the whole path back, so the float has to shrink to match (AIM 1-1-9.d.7 NOTE: "a higher than optimum TCH … may cause the aircraft to touch down further from the threshold").

That coupling is why the jet's `FlareDescentRate` is 370 fpm rather than the 200 it carried while the glidepath had no crossing height at all — 200 fpm bought a 2,300 ft float that faked an aiming point the geometry wasn't providing. `FlareDescentRate` is the sink commanded *at flare initiation*, decaying with height; a transport enters the flare from ~700 fpm and arrests to ~150 by touchdown.

## Displaced thresholds: which datum

`RunwayInfo.ThresholdLatitude`/`Longitude` are **pavement** ends — the nav database builds them from the vNAS `start_location`/`end_location`, which ignore any displacement.

The displacement lives on the airport map, so an end's landing threshold only exists once a `AirportGroundLayout` is in hand: `LandingThreshold.Resolve(runway, layout)` (or the `GroundRunway` overload, for callers that already looked the ground runway up). Both fall back to the pavement threshold when there is no layout, which is what every pre-existing replay depends on.

AIM 2-3-3.h.2: the portion of runway behind a displaced threshold "is available for **takeoffs in either direction** and landings from the opposite direction" — but not for landing in that direction. So the two datums split by *who is being measured*:

| Landing threshold (arrival datum) | Pavement threshold (departure / surface datum) |
|---|---|
| `FinalApproachPhase` glidepath + completion | `TakeoffPhase`, `LineUpGeometry`, `LineUpGraphRoute` |
| `LandingPhase` (`LandingPlan.ThresholdLat/Lon`) — flare, centerline steering, LAHSO progress | Runway rendering, `RunwayCrossingDetector` runway rectangles |
| `LowApproachPhase`, `ApproachNavigationPhase` continuous descent | `ConflictAlertDetector` corridors (must cover the whole surface) |
| `InterceptCoursePhase.ThresholdLat/Lon` + `ApproachGateDatabase` (P/CG measures the gate from the landing threshold) | `RunwayIntersectionCalculator` reported distances (its consumers are departures rolling from the pavement) |
| `PatternGeometry` downwind-abeam, base turn, `ThresholdLat/Lon` | `PatternGeometry` departure end / crosswind turn (AIM 4-3-2 anchors these beyond the *departure* end) |
| The 7110.65 §5-7-1.b.4 final window and the "on final" test: `ApproachCommandHandler.IsOnFinal`, `FlightPhysics.AutoCancelSpeedAtFinal`, `SPD`'s 5 NM-final rejection (the last two pass `aircraft.Ground.Layout`), and the `ApplyArrivalSpacing` / `ApplySameRunwayArrivalProtection` passes (the runway's own field's layout, below) | — |
| `SoloTrainingEvaluator.AlongLandingThresholdFt` — §3-10-3 "n feet down the runway", `IsLandingAfterThreshold` | `SoloTrainingEvaluator.AlongThresholdFt` — §3-9-6 departure spacing, intersection passage, "crossed the runway end" |

**The 5-mile final window is a judgement call.** §5-7-1.b.4 says "a point 5 miles from the runway" and names no datum; the references are otherwise silent. The landing threshold is chosen because the P/CG puts the APPROACH GATE "no closer than 5 miles from the landing threshold", defines THRESHOLD as "the beginning of that portion of the runway usable for landing", and AIM 2-3-3.h.2 makes the pavement behind a displaced threshold unavailable to the arrival.

An aircraft crossing the pavement end of KSJC 30L still has 2,537 ft (0.42 NM) to fly, and one over the displaced stretch is on a short final, not past the runway (`FinalApproachLandingDatumTests`). `IsOnFinal` takes the layout as a required parameter; `null` (no ground map for the field) answers from the pavement threshold.

**A measurement about a runway resolves through the runway's own field.** An airborne arrival's `Ground.Layout` is null for a scripted arrival with no destination and for every live-traffic shadow, so the engine passes that compare several arrivals to one runway never read the displacement off the aircraft.

`SimulationEngine.ResolveAirportLayout(runway.AirportId)` returns `World.GroundLayout` for the primary airport and the ground data's layout for any other field, and the on-final test and the threshold distance are both taken from that one layout (see [scenario-loading-and-generation.md](scenario-loading-and-generation.md)). A displaced runway at a secondary airport is therefore measured from its landing threshold too; a field with no ground map falls back to the pavement threshold.

**Elevation splits the same way, on a different axis.** `RunwayInfo.ElevationFt` is the active end's landing threshold elevation (from the CIFP) and carries the glidepath, flare and touchdown altitude; `RunwayInfo.AirportElevationFt` is the field, and carries traffic pattern altitude.

Level runways make them identical — see [approach-and-pattern-geometry.md](approach-and-pattern-geometry.md#traffic-pattern-construction--patterngeometrycompute) and [navigation-database.md](navigation-database.md#runway-end-elevations-come-from-the-cifp).

LAHSO is the one place both datums appear in a single calculation: `RunwayIntersectionCalculator.ComputeHoldShortDistanceNm` walks the pavement centerline to the intersection, then subtracts the displacement so the reported distance is the available landing distance a LAHSO clearance actually offers. `PatternCommandHandler` projects the hold-short point from the landing threshold to match — so authoring a displacement shortens the reported distance without moving the physical hold-short point.

## LandingPhase Braking Strategy

LandingPhase's job is to decelerate the aircraft to the speed needed for the committed exit. For high-speed exits this is coast speed; for standard exits whose turnoff is below coast it is the exit's turnoff speed. RunwayExitPhase and GroundNavigator handle turn geometry and precision braking through the turn.

### Default exits (no explicit preference)

The pilot picks the first comfortable forward exit (AIM 4-3-21.1 "exit at the first available taxiway").

"Comfortable" depends on the exit's class (aviation ruling, every category): a **high-speed** exit (turn-off speed at or above `HighSpeedExitSpeed`, angle ≤ 46°) qualifies if it needs at most `CategoryPerformance.ComfortableExitDecelRate` (jet 4.5 / turboprop 3.75 / piston 3.75 kt/s, ≈ autobrake 3).

A **standard** 60–90° exit qualifies only if it needs at most the routine `RolloutDecelRate` (jet 3.6 / turboprop 3.0 / piston 2.5) — a crew rolls past a 90° exit it cannot make at routine braking for the next high-speed one (SFO 19L: H, not F1).

When no exit qualifies, the landing searches once more at the category's firm rate (`CategoryPerformance.FirmBrakingRate`: jet 5.0 / turboprop 5.0 / piston 4.0 / helicopter 3.0 kt/s) and takes the earliest exit it can make that way (a CRJ9 on OAK 28R exits at C1 instead of stopping on the runway); only when even that fails does `RunwayExitPhase`'s "no exit found… braking to stop" path run.

The committed exit carries the rate that selected it (`ResolvedExitInfo.SelectionDecelRate`, snapshotted); the rollout brakes for it at most that rate + `LandingPhase.CommittedExitDecelToleranceKtsPerSec` (0.25) and gives the exit up past that.

Fallback-chosen, instructed and restored-without-a-rate exits are capped at the category's firm rate, which sits below its expedite rate for every category (comfortable ≤ firm < expedite, pinned by `RolloutBrakingRatesTests`); `EXP` keeps max effort (pinned by `UninstructedExitSelectionTests`).

Instructed exits (`ER`/`EL`/`EXIT`) use the firm rate and `EXP` the expedite rate (`RolloutBraking.NamedExitBrakingLimit`); an instructed exit that would need more than that is refused or given up with the crew's "unable" (§ Unable and replan). Not the first exit that requires maximum effort. Back-exits (>100°) are deferred during the centerline walk: `FindExitFromCenterline` keeps looking for a forward exit and only returns a back-exit if nothing forward is found within the walk range.

- Target the smaller of `coastSpeed` and the exit's `turnOffSpeed` — a 12-kt standard exit needs the aircraft at 12 kt at the branch, not at 25 kt coast
- Subtract a braking buffer: the distance RunwayExitPhase needs to brake from coast speed to the exit's turn-off speed, reserved at the plan's routine rollout rate (the default decel rate)
- Plan decel to reach the target speed at that buffer point before the exit's turn-entry point (below) — not at the branch itself
- If the exit is far enough that normal braking would reach target too early, use a gentler rate (floored at 0.5 kts/s) to avoid a long pointless coast

**The turn-entry point.** A standard exit's turn begins short of its branch point, where the ground navigator starts rounding the first corner. That turn-entry point P = branch − R·tan(min(Δ, 120°)/2) is the one place the rollout judges, plans, hands off and declares a standard exit missed against (`RolloutBraking.TurnEntryLeadNm`; `RolloutBraking.IsStandardExit` is a turn-off speed below `HighSpeedExitSpeed`). R is `MainGearTurnRadiusFt` (piston 15, turboprop 18, jet 25 ft) and Δ the exit angle from `AirportGroundLayout.MeasureExitAngle` (the path, else the hold-short node, else 90°), so a 74° piston exit leads by 11.3 ft and a 90° jet exit by 25 ft. A high-speed exit has lead 0 and is judged at the branch.

- **Judged** — `JudgeExitReach` measures the braking from the aircraft to P at the braking limit. An aircraft already within turn-off speed + 3 kt has no braking left to do, so it is judged against the branch itself and can still turn between P and the branch. A standard exit the rollout accepts is therefore the one it takes: OAK 28R C172 `TAXIAUTO 30` leaves at E with its route kept, not an "unable E".
- **Handed off** — `CanHandOff` needs IAS at or below coast and the reach test to P passing at the committed turn-off rate (`LandingPhase.HandOffDecelRate`: `RolloutBraking.CaptureTurnOffRate` of the selection rate, or `ExpediteExitDecelRate` under `EXP`), the rate `RunwayExitPhase` brakes the approach leg at.
- **Missed** — past P with IAS above turn-off + 3 kt (`HasMissedCandidateExit`). A standard exit still in `LandingPhase` at its branch is only the backstop for a hand-off that was blocked (above coast, LAHSO).

**The turn-off keeps the rollout's rate.** The rate the exit was chosen with carries across the handoff (`PhaseList.ResolvedExit` → `RunwayExitPhase.TurnOffDecelRate`, snapshotted, capped at the expedite rate; a late re-target carries its own firm or expedite limit).

`RunwayExitPhase.ApplyExitDecelRate` sets it as the navigator's `SlowdownDecelRateKts` before every tick and segment set-up: the approach leg down the runway to the branch (segment 0) brakes at that rate, the first exit edge, where the turn arc is flown, at min(that rate, the category's taxi decel rate), and the rest of the route at the taxi rate.

The route-end stop — the tail-clear point past the bar (`RunwayTailClearance.PastBarFt`: half the aircraft's length plus `RunwayTailClearance.MarginFt`, 10 ft), not the bar itself — is always planned at the taxi rate, on its own braking curve, so the turn-off rate never moves it. The aircraft brakes to rest there: `GroundNavigator.PublishSegmentSpeed` / `StraightTargetSpeed` publish the stop segment's speed on the arrival sub-tick (`IsExitStopSegment`), so it does not reach the point at 10-13 kt and zero there. An exit this phase found on its own has no rate and uses the taxi rate throughout; under `EXP` the whole route, stop included, runs at the expedite rate (`DecelRateKts`).

### Side preference and the "later on-side beats earlier off-side" rule

When a side preference is in play (explicit from `EL`/`ER`, or inferred from runway/parking layout via `InferPreferredExitSide`), the planner walks past **off-side** candidates while looking for an **on-side** option further down the runway. Crossing the runway centerline to exit increases controller workload (the aircraft now has to taxi back across to reach parking), so a same-side exit a bit further down beats an opposite-side exit at the closest taxiway.

With no controller instruction naming a side, the default side is `ArrivalExitSide.Resolve`, which `FinalApproachExitForecast`, `LandingPhase` and `RunwayExitPhase` all call, so the menu's forecast, the rollout and the exit agree (`RunwayExitPhaseDto.InferredSideValue` keeps it across a snapshot). Order:

1. The side of the landing runway where the arrival parks, read from its flight-plan remarks (`FlightPlanParking`: `/V/ GATE F14, 28R`, `/V/ PARKING SIGNATURE, 28R`, `PARKING AT CUTTER`), measured from the threshold along the true heading. A stand (any airport) comes from the layout and an area is the centroid of the sidecar `parkingAreas` stands it covers; parking within 250 ft of the extended centreline gives no side, and an unknown stand falls to its area.
2. The sidecar `exitDirections` entry for the arrival's class on that runway end (`ga`: a registration callsign or an operator in the sidecar's `gaOperators`, e.g. EJA, XOJ; `cargo`: an operator in `cargoOperators`, judged first; any other operator, such as an airline, keeps the default).
3. `InferPreferredExitSide` below.

An `EL`/`ER`/`EXIT` command always wins. A side from (1) or (2) is a rule: the aircraft slows and takes a 90° exit on that side rather than switching for want of a high-speed one (SFO 28R heavy cargo takes D). The `ArrivalExitSide` log category traces the choice.

`InferPreferredExitSide`'s priority order: (1) a per-end `exitDirections` override from the airport sidecar (`Data/ARTCCs/{ARTCC}/Airports/*.json`, via `AirportSidecarCatalog.GetExitDirection`) — needed where the GeoJSON `turnoff` flip for the reciprocal end is wrong (issue #405, KMIA 26R); (2) the GeoJSON-authored `turnoff` for the end (`GroundRunway.TurnoffForEnd`); (3) high-speed-exit count validated by parking proximity; (4) parking proximity; (5) parallel-runway HS inheritance.

See the `exitDirections` section in `src/Yaat.Sim/Data/ARTCCs/README.md` for the authoring schema.

- The off-side candidate is remembered as a fallback. It is only committed if the walk exhausts without finding an on-side option (e.g. one-sided exits like C3 at SFO, or every later option requires more than firm braking).
- For default selection (no explicit taxiway), the planner also passes `OccupiedHoldShortNodes` to the BFS so a known-occupied on-side hold-short doesn't appear as the on-side answer at that branch — the search naturally moves to the next exit. Explicit-taxiway commands (`EXIT G`) still ignore occupancy at planning time; RunwayExitPhase relaxes reactively at handoff if the named exit becomes blocked.
- The shared lookahead lives in `AirportGroundLayout.FindOnSidePreferredExit`. LandingPhase uses it with a comfort-braking filter; RunwayExitPhase uses it with only a back-exit filter.

### Explicit exits (EXIT T, EL, ER, etc.)

The pilot is committed to a specific exit. LandingPhase brakes up to the category's firm rate (`CategoryPerformance.FirmBrakingRate`: jet 5.0 / turboprop 5.0 / piston 4.0 / helicopter 3.0 kt/s) if needed, but still targets coast speed for the handoff.

A **taxiway-only** exit (`EXIT D`) issued after an explicit side (`EL`/`ER`) inherits the standing side, so a `ER ; EXIT D` sequence (two separate commands, common in scenario presets) exits *right at D* rather than dropping the side and falling back to the inferred side.

The taxiway name is a hard constraint and the side a soft preference: if the named taxiway exists only on the other side, the on-/off-side fallback in `FindAdjacentHoldShort`/`FindOnSidePreferredExit` still takes it (never fails to exit). A later command carrying its own explicit side (`EL D`) still overrides. The merge lives in `GroundCommandHandler.TryExitCommand`.

An exit with no side on KOAK 28R (`EXIT J`) prefers the right (back) turn-off onto J, because 28R's preferred exit side is right: a left exit leaves the aircraft boxed in between the parallel runways, while the parking is on the right.

- Compute distance to the exit
- Subtract a braking buffer: the distance RunwayExitPhase needs to brake from coast speed to the exit's turn-off speed (using the default decel rate)
- Plan decel to reach coast speed at that buffer point — not at the exit itself
- If the exit is far enough that normal braking would reach coast too early, use a gentler rate (floored at 0.5 kts/s) to avoid a long pointless coast
- If the exit requires more than the firm rate (the max-effort rate under `EXP`), the crew says "unable {twy}." and gives the taxiway up for the landing (§ Unable and replan)

### Expedited exits (EXP — "without delay")

`EXP` (the standalone command on a just-landed aircraft, or the `ER`/`EL`/`EXIT` modifier) clears the runway as fast as possible. It sets `AircraftGroundOps.IsExpeditingExit`, which raises the braking limit from the category's firm rate to its max-effort rate (`CategoryPerformance.ExpediteExitDecelRate` — jet 7.5, turboprop 6.0, piston 4.5, helicopter 4.0 kts/s).

The higher limit feeds both `LandingPhase.BrakingLimit` (the exit-reachability filter *and* the braking rate) and `RunwayExitPhase` (via `GroundNavigator.DecelRateKts` for the whole exit route, applied every tick, so an `EXP` issued mid-exit takes effect at once), so the aircraft:

- **Takes the earliest reachable exit** — the comfort/firm filter that normally skips close exits is relaxed to the max-effort rate, so an exit one or two turnoffs earlier now qualifies.
- **Brakes harder during rollout** to make that earlier exit.
- **Keeps the high-speed turn-off speed** — the target is still `min(coastSpeed, turnOffSpeed)`, so a high-speed exit (≈30 kts jet) is taken at speed, not crawled.
- **Brakes at max effort to the hold-short stop** after the turn-off (the navigator uses the same max-effort decel for the whole exit route instead of the turn-off and taxi rates).

The standalone-`EXP` path is keyed on an *active* Landing/RunwayExit phase and resets the cached `LandingPhase` candidate (`ResetExitCandidate`) so the next tick re-resolves at the higher limit; the `ER`/`EL`/`EXIT EXP` modifier form re-resolves via the normal preference-change path. The flag is cleared when the exit completes (`RunwayExitPhase.CompleteExit`) and by `NORM`. Phraseology is **"without delay"** (7110.65 §3-7-2.b.10), not "expedite" — `EXP` is only the keyboard token.

A named-exit command (`ER`/`EL`/`EXIT <twy>`) writes `IsExpeditingExit` as the expedite the instruction runs under: set by `EXP` on it, kept when it repeats the taxiway already instructed while an `EXP` stands (`GroundCommandHandler.KeepsStandingExpedite` — `EXIT W3` after `EXIT W3 EXP` goes on braking at max effort), and cleared by any other named exit without `EXP`. A bare `EXP` never revives a taxiway the crew has given up (below).

### Unable and replan

An instructed exit the crew cannot make — it would need more than `RolloutBraking.NamedExitBrakingLimit` (the category's firm rate, max effort under `EXP`) — is never braked for. The crew says **"unable {twy}."**

(`PilotResponder.BuildUnableToExit`; spoken with the taxiway spelled, "unable whiskey three"), P/CG UNABLE: it cannot comply with the instruction, and the controller may already be acting on that. Exit instructions are a request the crew may decline (7110.65 3-10-9.a phraseology, "IF ABLE, TURN LEFT/RIGHT (taxiway)"). The call comes at the first point the crew knows:

- **Up front, at command time.** On the rollout, `GroundCommandHandler.TryExitCommand` asks `LandingPhase.EvaluateAndApplyNamedExitInstruction` before it reads anything back.

  It runs the rollout's own named-exit search (`FindWithInferredSide` over `TryFindCandidate`; the one reach test `JudgeExitReach`: connections at or behind the aircraft skipped, slow enough by indicated airspeed or makeable by ground speed at the limit, the inferred side first for a taxiway-only instruction), judged with the expedite the instruction will run under (above).

  When nothing qualifies the command is refused with an `ExitInstructionVerdict` and the pilot's own line travels on `CommandResult.PilotUnable`: "unable {twy}." when a connection of the taxiway is ahead but past the limit or past a LAHSO hold-short point; *"Unable, no {twy} ahead"* (pilot: "unable, no {twy} ahead.", `BuildUnableNoExitAhead`) only when no connection of it lies ahead on a runway with hold-short data.

  A runway without hold-short data, a forced (`CLANDF`) rollout, an aircraft still airborne and one with no layout accept the instruction. The standing preference is left as it was — unless the refused taxiway is the one the aircraft is already braking for, which the refusal gives up (the call counted as made, the candidate dropped), so the branch makes no second call.
- **At decision time.** An instruction accepted on final, or an exit that becomes unmakeable after a clean readback (a long float), is judged every time the rollout resolves its candidate: the tick the search first fails at the limit while a connection is still ahead, `GiveUpUnreachableNamedExit` makes the call, gives the taxiway up and resolves again, so the aircraft brakes for the next exit it can make from that tick on — before it reaches the branch.
- **At the branch.** A committed candidate passed too fast (`MarkExitUnable`, from the normal rollout and the forced rollout alike) excludes its branch node, makes the call while an instructed taxiway is in force, and gives the taxiway up when it is the instructed one; `TickUnable` then relaxes the preference and replans.

The call is made once per instruction: `_unableBroadcast` (snapshotted as `LandingPhaseDto.UnableBroadcast`) is set by the call and cleared whenever a new exit instruction is accepted. An exit the rollout chose on its own and then drops is given up silently: its branch node is excluded and the search goes on. Replanning relaxes the preference to the controller's side (`EL`/`ER`), taxiway dropped, and finds the next comfortable exit as default selection would.

#### The given-up set

A taxiway the crew has said "unable" to is given up for the rest of the landing: `PhaseList.GivenUpExitTaxiways` (compared without case, snapshotted as `PhaseListDto.GivenUpExitTaxiways`).

It is the taxiway, not a branch node, because one bar is reached from several centerline nodes (OAK 30's W3 from 87, 714, 715 and 716) and excluding one would let the next search re-find the exit at the next. The set lives on the phase list, not the phase, so `LandingPhase` and `RunwayExitPhase` share one source:

- **Skipped while rolling.** The rollout's searches (`JudgeCandidate`), the straight-line fallback (`FindExitAheadOnRunway(..., excludeTaxiways)`) and every `RunwayExitPhase` search after the hand-off — the analog search, the occupied-exit re-search, the backstop's `FindExitBehind` — skip a given-up taxiway while the aircraft is moving (`RunwayExitPhase.IsRolling`: above `BackstopStoppedSpeedKts`, 1 kt).
- **Eligible once stopped.** The "unable" was about rollout speed. An aircraft stopped on the runway, which may not reverse without ATC approval (AIM 4-3-21.a), may taxi forward to a given-up taxiway ahead like any other exit, so a stop with only a given-up exit ahead takes it rather than reporting "unable to exit". The stop does not clear the set; a search run once rolling again skips it again.
- **Revived only by a new instruction naming it.** An accepted `EXIT <twy>` takes `<twy>` out of the set whatever phase the aircraft is in (`GroundCommandHandler.TryExitCommand`, `LandingPhase.TakeNewExitInstruction`), judged afresh by the up-front check; a re-target probe (`RunRetargetSearch`) treats the taxiway the instruction names as eligible before that write. A refused instruction, a side-only `EL`/`ER` and a bare `EXP` leave the set alone.
- **Cleared** when a landing starts (`LandingPhase.OnStart`) and when the runway exit completes (`RunwayExitPhase.CompleteExit`), so a later runway exit of the same aircraft starts with none.

### Exits ahead (the listing)

The context menu's Exit left / Exit right flyouts offer the named exits the arrival can make. The server computes the list in the `Situation` step and ships it as `ExitsAhead` (`ExitAheadDto`: taxiway, side, distance rounded to 100 ft, planned, turn-off angle in whole degrees; [training-hub-contract.md](training-hub-contract.md)); the client never recomputes it. Both forms are read-only: they never refuse, give up or commit anything.

- **Rule.** `LandingPhase.ListExitsAhead` judges every taxiway of the runway (`AirportGroundLayout.ExitListTaxiways`, authored `noTurnoff` taxiways left out) on each side by the search an accepted `EL <twy>` / `ER <twy>` runs, without expedite, so a taxiway is listed exactly when that command would be accepted.

  A given-up taxiway is judged afresh and listed if it passes; exits past a LAHSO hold-short point are left out; a connection whose bar sits inside the holding distance (a short stub accepted only when named) is not an exit a crew would offer. One row per taxiway and side, at its first makeable connection, left side first and each side nearest first.
- **On the rollout** the list is live while `LandingPhase` rolls out: distances run from the aircraft to the branch point, and the exit the aircraft is braking for is planned. It is null under `CLANDF` and without a layout or hold-short data for the runway, and from the hand-off to `RunwayExitPhase` on (the menu then offers only the pilot's-choice rows).
- **On final** `FinalApproachExitForecast.ListExitsAhead` runs the same search from the projected touchdown (the glidepath aiming point plus the flare's float, with the touchdown wheel speed) at the firm limit, as the first rollout tick would judge an exit named before touchdown.

  Distances run from the landing threshold, and the planned row is the exit the controller's instruction would have the crew take. It is listed only while a full-stop landing follows and the aircraft, fixed-wing and on final for its assigned runway, is more than 1 and at most 5 nm from the threshold (`WindowInnerNm`, `WindowOuterNm`): inside 1 nm there is no list until the rollout's (7110.65 3-10-9.a NOTE). A pattern final usually turns inside that window, so it rarely has a list.
- **Cached.** The forecast is recomputed only when its inputs change (layout, runway, type, projected touchdown, the instructed exit, the LAHSO limit), not as the aircraft advances down final.
- **The runway's own exits.** `LandingPhase.ListRunwayExits(layout, runway, side)` lists every named exit of the runway end on one side, independent of any aircraft or forecast: each taxiway of `ExitListTaxiways` once, at its first connection on that side from the landing threshold when the connection's bar is at the runway's holding distance, distance from the threshold rounded to 100 ft, nearest first, never planned. A taxiway whose first connection on that side rounds to 0 ft or lies behind the threshold is left out whole, later connections included (KOAK 28R B). It feeds the final flyout's "Exit at" box through `IMenuHost.GetRunwayExits`; the client calls it, with its own ground layout, only when the aircraft's destination is that layout's airport.

### TAXI on the rollout

A `TAXI` or `TAXIAUTO` issued while `LandingPhase` rolls out is accepted (`LandingPhase.CanAcceptCommand` allows `Taxi` / `TaxiAuto` on the ground; airborne it keeps the phase's own refusal). `TryTaxiCore` hands it to `GroundCommandHandler.RolloutTaxi.cs`: the TAXI is an instructed exit plus a route stored behind that exit. The landing's phases keep running, nothing is cleared, and the aircraft gives no "clear of the runway" call.

- **Stored route.** The route is resolved from the exit's hold-short node (`ResolveTaxiRouteFromStart`) and stored on `PhaseList.TaxiRoute`, with the destination runway the clearance names in `PhaseList.StoredTaxiDestinationRunway`. It is not `Ground.AssignedTaxiRoute`, so no `TaxiingPhase` runs yet, but the strip and status "via" text, the route overlay and the next-crossing `CROSS` read it from the moment it is accepted (`AircraftState.ActiveOrStoredTaxiRoute`: `SituationFlagCalculator`, `AircraftStatusDescriber`, the CROSS handlers, `GIVEWAY`).
- **The landing runway's own bar** is cleared (leaving the runway just landed on is not a crossing), and the clearance's `CROSS` keywords and `NoDelete` apply at once. Scenario auto-cross clears every other runway crossing but never the landing runway's re-crossing: `ApplyAutoCrossExceptLandingRunway` marks it `HoldShortPoint.RecrossesLandingRunway`, which `TaxiRouteAutoCross.Apply` skips, so it waits for an explicit `CROSS` (7110.65 3-10-9.b.2). The mark holds when the auto-cross mode is changed mid-session (`SimulationWorld.ApplyAutoCrossToActiveTaxiRoutes` re-applies auto-cross to a stored route too).
- **Exit choice.** The implied exit is judged like `EXIT <twy>` at the firm braking limit (`LandingPhase.EvaluateImpliedExit`) and instructed as `TryExitCommand` instructs one, so it is the rollout's `RequestedExit`.

  A `TAXI … EXP` or `TAXIAUTO … EXP` judges at the expedite limit instead (`EvaluateImpliedExit` and `MakeableExitsAhead` take `expedite`) and picks from the exits that limit makes. Under a standing `EXIT x EXP`, a TAXI/TAXIAUTO without `EXP` lists at that limit too and flies its pick with the exit expedite on. A rollout route the exit cannot join clears the route and crossing expedites with it.

  A `TAXIAUTO` goes via the first exit the aircraft can make (`LandingPhase.MakeableExitsAhead`, nearest first, either side) whose route leaves the exit ahead. Re-crossing the landing runway is allowed: OAK 28R's left exit takes a back-taxi across 28L.

  A `TAXI` whose first taxiway touches the landing runway turns off onto it, on the side the route turns to. One whose first taxiway does not touch it is resolved from the exits ahead: the first makeable exit in `JoinSideRank` order (exits on the controller's standing `EL`/`ER` side first, then those on the side of the first taxiway, then the rest, nearest first within each) whose taxiway reaches that taxiway with no other taxiway between, and the rollout's preferred side is inferred toward it (`InferSideTowardTaxiRoute`; the side it replaced is kept and given back when the route is dropped).

  A bare `EL`/`ER` on the side of the exit the TAXI implied, given while the route still waits, confirms that same exit (`ConfirmedImpliedExit`), so the aircraft still turns off there into the stored route; any other exit instruction is an instruction of its own (one naming another taxiway drops the route, § Drops). A second TAXI replaces the first one's route and implied exit, and the side the first gave the rollout.

  The pilot reads the exit back ahead of the taxi: "exit right at J, taxi to runway 30 via C B W W1" (spoken "exit right at juliett, …"). The exit taxiway is left out of the taxi clause only at its leading position, in the pilot's readback and the controller-facing echo alike (`ImpliedExitReadback`, `WithoutTaxiway`); a later mention of it stays. A TAXI that joins from an exit the crew picked itself leads with the exit clause only when the controller had named that exit.
- **Refusals.** "unable {twy}." (a pilot-unable reply) for an exit too close to make or a TAXI no makeable exit joins; "Unable, no {twy} ahead" for an exit already passed; the airborne refusal; and a route that never leaves the landing runway ("Cannot taxi along the landing runway during the rollout; issue TAXI after the runway exit"). A refusal changes nothing. Without hold-short data or on a forced (`CLANDF`) rollout the exit is not judged and the route is stored behind the rollout's own pick.
- **Join rules** (`JoinStoredRoute`). The exit taxiway is followed away from the runway from its hold-short node until the stored route leaves it ahead (not back). The walk ends with no join at another taxiway met first, a dead end, a runway centerline, or a runway hold-short node other than the exit's own, so an exit whose taxiway crosses another runway's bar before reaching the route never joins it: `TAXIAUTO` tries the next exit, a `TAXI` is refused "unable {twy}.", and at exit completion an unreachable route is dropped with the crew's "unable {first taxiway}.".
- **Starting.** `RunwayExitPhase` checks the join when it builds the exit route; a joined route's end is rolled through, not stopped at. When the exit completes, `GroundCommandHandler.TryStartStoredTaxiRoute` makes the taxi start `TryTaxiCore` makes (landing clearance dropped, departure runway assigned, taxi speed reset) and a `TaxiingPhase` follows, with no stop and no "clear of the runway" call. Without a ground layout the route cannot start and the exit brakes to its end as usual.
- **Drops.** The stored route (and `StoredTaxiDestinationRunway`) is dropped when the exit is given up (§ Unable and replan), when a new exit instruction names a different taxiway, on a go-around (`GoAroundHelper`, which also clears the implied `RequestedExit`; a `CROSS` is then refused "No taxi route assigned"), and when `RunwayExitPhase` stops on the runway with no exit ahead (log "[Exit] {Callsign}: no exit ahead; the stored taxi route is dropped"). A restore with no ground layout, or whose route nodes are not in the layout, drops it and logs.
- **GIVEWAY.** `GIVEWAY` is accepted on the rollout and the exit once a TAXI stored a route (`TryGiveWay` reads `ActiveOrStoredTaxiRoute`). It stays pending while the aircraft is on its landing runway (`AircraftState.HasGiveWayPendingOnLandingRunway`: a give-way hold with `LandingPhase` or `RunwayExitPhase` current): it never slows or stops the aircraft there (`RunwayExitPhase.OnTick`, `RunwayCrossingFarSideGate`), is not aged or judged (`FlightPhysics.UpdateGiveWayResume`), and `TryStartStoredTaxiRoute` keeps it, so it applies once `TaxiingPhase` takes over. The arrival counts as landed traffic, not stopped traffic, for the takeoff-clearance advisory (`RunwaySafetyAdvisor`), and the pending give-way no longer hides a stuck arrival from `StuckAircraftRule`.
- **Snapshot.** `PhaseListDto.TaxiRoute` carries the route and the optional `PhaseListDto.StoredTaxiDestinationRunway` its destination runway, not written while null; `PhaseListDto.RequestedExitIsTaxiImplied`, `LandingPhaseDto.ActivePreferenceIsRequestedExit`, `SideTowardTaxiRoute` and `SideBeforeTaxiRouteValue` keep the implied-exit link across a restore ([snapshots-and-replay.md](snapshots-and-replay.md)).

A `TAXI` in `RunwayExitPhase` or `HoldingAfterExitPhase` is unchanged: it clears the phase and taxis from where the aircraft is.

## Land and hold short (LAHSO)

`LAHSO <rwy>` (`PatternCommandHandler.TryLandAndHoldShort`) stores a `LahsoTarget` on `Phases.LahsoHoldShort`: the hold-short point's distance from the **landing** threshold (`RunwayIntersectionCalculator.ComputeHoldShortDistanceNm` — half the crossing runway's width plus the 200 ft runway safety area back from the intersection). `LandingPhase.OnStart` captures it; everything below lives in `LandingPhase.TickRollout`.

The pilot's obligation is AIM 4-3-11.b.6 — exit at the first convenient taxiway before the hold-short point, *otherwise* stop and hold at it — and the stopping datum is AIM 2-3-5.a.2: all of the aircraft short of the runway holding position marking.

- **Distance** is along-track from the landing threshold on the runway heading, the same form the exit planner uses for branch points.
- **Stop target** = the point − (`LahsoStopMarginNm` 50 ft + half the airframe length). `Position` is the centroid, so the half-length term is what puts the *nose* short of the marking; the 50 ft covers discrete-tick overshoot only (the hold-short distance already carries the safety-area setback).
- **Speed ceiling** (`ApplyLahsoCeiling`, applied after the exit planner and the freeze-at-current block, before the publish): `targetSpeed = min(targetSpeed, RolloutBraking.MaxEntrySpeedKts(stopDist, planningRate))`, with `planningRate = min(plan.DefaultDecel, ExpediteExitDecelRate)` — the normal rollout rate, so braking is front-loaded and there is no 40 kt coast up to the line.

  The published decel is `min(max(exit planner's rate, RequiredDecelKtsPerSec(gs, 0, stopDist)), ExpediteExitDecelRate)`: LAHSO never lowers the exit planner's rate and never exceeds the category's expedite rate. A B738 on SFO 28R `LAHSO 1L` (point 5,601 ft from the landing threshold) slows continuously from ~1,100 ft out and stops with its nose 50 ft short.
- **Stop branch** (`TickLahsoStop`, once the stop target is reached): target 0 at the category's firm rate (`FirmBrakingRate`); past the point itself the rate is the category's expedite rate and the stop logs `LAHSO overrun — stopped N ft past the hold-short point`.

  At ≤ 0.5 kt `StoppedForLahso` is set and the phase completes; `PhaseRunner` then appends `RunwayHoldingPhase` → `RunwayExitPhase` → `HoldingAfterExitPhase` and clears the target; any phases still queued behind the landing are ended first (`PhaseList.Clear`, Skipped), so the runway hold always runs next. There is no self-initiated go-around after touchdown.
- **Exit before the point wins.** While LAHSO is active `ResolveNextCandidate` (both the planner path and the straight-line fallback) skips any exit whose branch point lies past the stop target (`BranchFitsInsideLahso`) — an instructor-preferred exit included. The handoff to `RunwayExitPhase` needs a committed candidate that phase will honour: a real route (`Path.Count >= 2`) whose hold-short node is not occupied (the same test `RunwayExitPhase.OnStart` applies).

  `TickHandoff` nulls `Phases.LahsoHoldShort` in the same branch that publishes `ResolvedExit`, so the landing stops being a hold exactly when the exit is committed. An occupied or route-less candidate never hands off: the normal missed-exit path runs, and with nothing left before the point the aircraft stops and holds. A restore into `State.Handoff` without a usable candidate is routed back through `TickRollout` for the same reason.
- **An amended clearance ends it** (AIM 4-3-11.b.5): every go-around (`GoAroundHelper`), a `TG`/`SG`/`LA`/`COPT` that replaces the landing terminator, and `CANCLAND` null the target. `CLAND` restated after `LAHSO` keeps it (same clearance), and so does `CLANDF`: a forced landing after `LAHSO` keeps the hold-short until it is cancelled explicitly.

Known gaps are open in YAAT-31 (LAHSO residuals): `RunwayExitPhase`'s own re-search and the instructor's late `EL`/`ER`/`EXIT` retarget are LAHSO-blind after the handoff, and the exit filter tests the branch point rather than the turn-off arc.

## Forced landing (CLANDF)

`CLANDF` sets `Phases.ForceLanding`: the aircraft lands and stops on the runway from any energy state, including when the command arrives after it has already passed the threshold.

The numbers live in `Phases/Tower/ForcedLandingProfile.cs` and are an instructor-override rule, not aircraft performance: no FAA source covers the override, so they are aviation-sim-expert judgement calls, and a normal landing never reads them. `FinalApproachPhase.ApplyForcedLandingGuidance` and `LandingPhase` (down to the flare, in place of `ApplyGlidepathFloor`) both call `ForcedLandingProfile.ApplyAirborneGuidance`.

- **Aim point** (`AimPointAlongFt`), measured signed along-track from the landing threshold, so an aircraft already past it aims further down the runway rather than back at the threshold: the furthest of the 6° point ahead of the aircraft (along-track + AGL / tan 6°), the threshold + 1,000 ft, and the aircraft + 500 ft.

  When the runway beyond that point is shorter than a 6 kt/s stop plus 500 ft, the aim point is pulled back toward the aircraft (`LatestTouchdownAlongFt`). The runway length is the landing distance (pavement less any displacement, AIM 2-3-3.h.2) less one sub-tick of travel, because the landing phase sees the wheels on the runway one sub-tick after physics puts them there.
- **Speed**: the target is Vref plus the wind additive, reached at 5 kt/s (`AirborneDecelKtsPerSec`, written to `DesiredDecelRate`). The forced landing owns the speed profile, so a `SPEEDF` to a forced aircraft is superseded. A go-around or `CLC` that lifts the override also clears that airborne deceleration (`PatternCommandHandler.EndForcedLanding`).
- **Descent** (`DescentRateFpm`): capped at 3,000 fpm, and at 1,000 fpm at or below 100 ft AGL. The cap gives way only when the capped path cannot reach the runway by the latest touchdown point; the rate is then whatever reaches it. In that case the flare may not float either (`FlareFloorFpm`, applied by `LandingPhase.ApplyForcedFlareFloor`), and a forced descent that crosses the flare band in one sub-tick runs the flare on the same tick.

**Forced rollout.** `ForceLanding` stays set through the rollout and is consumed in `LandingPhase.OnEnd` once the landing ends on the ground. While it is set (and no LAHSO hold-short point outranks it), `LandingPhase.TickForcedRollout` replaces the normal exit braking:

- It takes only exits off the runway's centerline graph (`TryResolveGraphCandidate`), never the straight-line fallback, whose one-node path the runway exit cannot drive. When the normal planner finds none, `ResolveForcedCandidate` takes the last exit ahead it can make at 6 kt/s or less, since the last one is the gentlest to brake for.
- It brakes to the exit's turn-off speed 300 ft before the branch point (`ExitBrakingMarginFt`), at a rate clamped to 3–6 kt/s. An exit that needs more than 6 kt/s is given up (`MarkExitUnable`) and the search runs again as the aircraft slows. A search that finds nothing is not repeated until the aircraft passes the next branch point it saw (`ForcedNoExitUntilAlongNm`, snapshotted).
- With no usable exit it stops 300 ft before the runway end (`TickForcedRunwayEndStop`), braking 3–10 kt/s, and harder only when even 10 kt/s would carry it off the end (`ForcedRunwayEndStopDecelKtsPerSec`). Once stopped the landing completes and `RunwayExitPhase` takes over.

A forced landing that completes on the ground sets `Phases.ForcedRollout`. It tells `RunwayExitPhase`'s backstop that this aircraft may taxi back to the nearest exit behind it on its own (below).

Any successful tower or ground command to the aircraft on the ground clears the flag (`CommandDispatcher.TryApplyTowerCommand`), and so does the end of the runway exit. From then on the controller directs the aircraft (AIM 4-3-21.a). `SfoClandfPastThresholdTests` and `ForcedRolloutStopAndBackstopTests` pin the profile and the rollout.

## RunwayExitPhase — Analog Rolling

RunwayExitPhase does not walk centerline nodes. It:

1. **Steers** along the runway heading at the category's ground turn rate
2. **Adjusts speed** toward coast speed (accel/decel at taxi rates)
3. **Searches** for exits ahead using `TryFindExitAhead` — a continuous, distance-based search that respects preferences and applies soft tiebreakers (inferred side for taxiway-only commands)

When an exit is found, it builds a **virtual segment**: a synthetic route segment from the aircraft's current position to the branch node. This segment exists only to give GroundNavigator an inbound bearing for turn anticipation. The full route becomes `[virtual → branch → ... → hold-short]`.

### Exits whose bar sits inside the holding distance

An exit is clear of the runway only when the whole aircraft is past the holding position marking, even if that means entering another taxiway (AIM 4-3-21.b, 2-3-5.a.1; 7110.65 §3-10-9.b NOTE 1). "Inside the holding distance" means short of the runway's standoff S (`GroundRunway.HoldShortStandoffFt`, else its authored `HoldShortDistanceFt`; an angled taxiway's bar sits at D, beyond S, so it is never inside) by more than `AirportGroundLayout.HoldingDistanceToleranceFt` (5 ft); `AirportGroundLayout.IsAtRunwayHoldingDistance` is the one test; it reads the bar's `GroundNode.IsShortBar` flag, set when the layout is built, and measures the cross-track only when the flag is null ([ground/hold-short-placement.md](ground/hold-short-placement.md)). `FindAdjacentHoldShort` handles three shapes:

- **A branch that dead-ends at a taxiway carrying the runway's bar** (OAK P at J, MIA T8 at S — their own fallback bars are dropped, see `docs/ground/hold-short-placement.md`): `HopToJoiningBar` walks outward onto the joining taxiway to that bar — a shortest-path search keyed on (length, node id), each step at least 1 ft farther from the centerline, capped at the holding distance, honouring one-way edges.

  The exit keeps its branch name ("clear of 28R at P") while the segments past the branch carry the joining taxiway, so `CurrentTaxiway` and the parallel-runway pull-up read J. If that bar is occupied the exit is unavailable and the search moves on.
- **Another runway's bar on the way out**: the search passes through it when the step moves away from that runway (farther from its centerline segment than the node before), so COS B1 off 17R/35L passes runway 13/31's bar #96 and holds at the 17R bar #88. No exit may end on another runway's centerline or pavement (AIM 4-3-21.a).
- **A short bar with nothing beyond** (named-only, found at ATL: SG, SG2, R3, R7, R11, N10, C, B13, A4): never chosen for an uninstructed exit, never listed by `FindAdjacentHoldShortForListing` (the `--exits` listing and the side inference).

  An instructed exit (`ExitSearch.Instructed`, a named taxiway) stops there; `HoldingAfterExitPhase.StoppedInsideHoldingDistance` (snapshotted) suppresses the "clear of runway" call, `RunwayOccupancy` counts the aircraft `OnSurface`, and the parallel-runway pull-up is skipped (AIM 4-3-21.b, 2-3-5.a.1).

### Crossovers too short to clear

A crossover between parallel runways (SFO 28R-28L) can be shorter than the aircraft: the nose reaches the other runway's holding position marking before the tail is `RunwayTailClearance.PastBarFt` (half the length plus 10 ft) past the vacated runway's bar. `RunwayTailClearance.StopPastBar` builds the stop, cut back by `VirtualNode.OffsetPastShortOfOtherRunways` so the nose stays at or short of that bar (`RunwayHoldBars.DepthPastMarkingFt` judges the paint). Its `Stop.TailInsideLine` is true when the tail is left inside the vacated runway's marking, or within `RunwayTailClearance.TailClearToleranceFt` (2.8 ft: the navigator's stop threshold plus 1 ft) of it. This is a tail-inside stop: the aircraft is not clear of the runway it left.

- **Automatic exit.** `RunwayTailClearance.IsTooShortForAutomaticExit` skips a crossover whose walk from the exit's bar meets another runway's bar closer than the aircraft's length plus `MarginFt`, unless the controller named its taxiway. When no exit long enough fits it still takes a too-short one. An exit that fits at firm braking wins over a too-short crossover reachable at the default rate. `ExitPreference.StopsTailInsideRunwayLine` carries the answer, computed once when the exit is resolved.
- **Explicit exit or `CLRWY`.** The aircraft stops with the nose short of the other runway's bar and stays a runway occupant. An exit ends in `HoldingAfterExitPhase` with `HeldShortOfRunway`; the pilot says "holding short of runway 28L at K, not clear of runway 28R" (`PilotResponder.BuildHoldingShortNotClearOfRunwayText`), makes no taxi-in call, and the status line reads `holding short of 28L on K, not clear of 28R`. `CLRWY` ends in `HoldingInPositionPhase` with `HeldShortOfRunway` and `TailInsideRunway`, keeping the runway it is still on as its assigned runway until the stop clears it. A `CLRWY` given to an aircraft already at its stop, with the nose more than `ClearRunwayPhase.NosePastBarSlackFt` (about 3.8 ft: the set-back margin plus the navigator's arrival threshold) past the other runway's bar, says "Holding position, not clear of runway 28R" (`BuildHoldingPositionNotClear`) and its status reads `On K, not clear of 28R`.
- **Consumers.** `RunwayOccupancy.IsTailInsideRunwayState` makes such an exit or hold `OnSurface` (moving or stopped); `SameRunwayArrivalProtection` never projects it clear; `RunwaySafetyAdvisor` warns a landing clearance with one on the runway; `GroundConflictDetector` ranks the `CLRWY` hold, and a `HOLD`-ended crossing's with its tail inside, as vacating ([runway-fouling.md](runway-fouling.md)).

A controller's side (`EL`/`ER`, `Phases.RequestedExit.Side`) is never overridden: when the named taxiway is available only on the other side, the aircraft takes the next exit ahead on the instructed side and the pilot says "unable P." (`RunwayExitPhase.ReportNamedExitOnlyOffSide` → `PilotResponder.BuildUnableToExit`).

A plain `EXIT P` may still turn off P on the other side; an uninstructed rollout uses the off side only as a last resort. `ComputePathExitAngle(path, exitTaxiway, runwayHeading)` follows the path from the runway centreline to the first bar or to where the path leaves the exit taxiway; a path that doubles back still reads as a back exit (over 90°).

### Exit angle and exit chain

An exit's angle (`AirportGroundLayout.TurnOffAngle`, used by `ComputeExitAngle` and `ComputePathExitAngle`) is the largest angle to the runway heading of the exit centreline's direction, measured continuously from the runway centreline outward until the centreline is past the runway edge (cross-track from the centreline node of half the runway's width) and has then run `TurnOffWindowPastEdgeFt` (50 ft) further, or until the bar if that comes first. A straight hop's direction is its bearing; an arc's end tangents bound it.

Any bend after that window is the navigator's turn, never the exit's class (P/CG "HIGH SPEED TAXIWAY", 7110.65 §3-4-15, AIM 2-1-5.f.3). It is never read at the bar, which moves with the standoff: OAK 28R E reads 74.2° (a standard exit) although its leg to the bar bends to 45°.

`ChainFromRunway` finds the exit's nodes from the runway centreline to the bar (a shortest-path search over the exit taxiway's non-centreline edges, within `ExitChainSearchFt`, 1,500 ft; a junction arc matches by any of its names). Of the centreline nodes it reaches, it picks by, in order:

1. The bar's own runway: a node on the centreline of the runway the bar protects, before any other runway's (every runway counts for a bar that protects none).
2. The leaving tangent: the first hop's direction off the centreline, an arc by its tangent at the centreline node, against the runway heading (`LeavingClass`). Under 20° (`ExitChainMinOffCenterlineDeg`) leaves forward, 20° to 160° across, over 160° (`ExitChainReversedDeg`) back up the runway; forward ranks before across before back.
3. The shorter path, then the lower node id (`ChainReach`), so the answer does not depend on the order a node lists its edges.

A back chain is ranked last, never dropped, so a bar reachable only by doubling back reads as the back exit it is.

Rank by the leaving tangent and the bar's own runway, never by hop count, length or chord: the corner built for the opposite landing direction is a short arc tangent to the centreline pointing against the landing, so it is the shortest and fewest-hop chain and a search that preferred either would take it, reading an exit as a reversed corner (OAK W5 once read a 35 ft fillet built for runway 12, tangent 130°, and some chains ended on another runway's centreline). The width in the angle is the bar's own runway's (`ExitChainRunway`), falling back to the runway owning the chain's first centreline node when the bar protects none or the chain ends on another runway.

Two contracts of the handoff to the navigator (`StartExitNavigation`):

- **The exit route's speed ceiling is `min(coastSpeed, TaxiSpeed × expedite multiplier)`**, not coast speed. The turn itself is governed by the junction arc's `MaxSafeSpeedKts` and back-propagated braking; the taxi-speed ceiling is what prevents the old slow-turn-then-surge profile (accelerate back toward 40 kt coast on the exit straight, then brake hard for the hold-short). Once off the runway the aircraft is taxiing — it only ever decelerates from the turn-off speed to the hold-short.
- **The analog-rolling heading hold is cleared** (`Targets.TargetTrueHeading` / `TurnRateOverride` → null). `TickRolling` steers via the persistent `ControlTargets`; the navigator steers by writing `TrueHeading` directly. Leaving the hold in place makes FlightPhysics turn the aircraft back toward the runway heading every substep, fighting the navigator's exit turn to a standstill (a false pure-pursuit "orbit").

### No ground layout: stop on the runway and complete

Airports without a vNAS ground map (KFAT in the #429 bundle) have nothing to search: `TryFindExitAhead` returns immediately. `OnTick` takes
a dedicated branch when `ctx.GroundLayout is null` — hold the runway heading, command zero speed at the taxi braking rate, and **return
`true` once the aircraft is stationary** so the `HoldingAfterExitPhase` that `PhaseRunner` queued behind the exit actually starts. That
handoff is what `SimulationEngine.TickAutoDelete` keys on: its `stuckAfterLanding` arm (any non-disabled mode) and its `OnLanding` arm both
require `HoldingAfterExitPhase`, so before this branch existed a layout-less arrival coasted to the runway-end backstop, braked to 0 kt and
sat in `RunwayExitPhase` for the rest of the session — never deleted under *On landing*. `HoldingAfterExitPhase.OnStart` skips its
"clear of runway … at …" transmission on this path (no exit taxiway, no layout): the aircraft is stopped *on* the runway, and under mode
`Never` it stays there awaiting an instruction rather than claiming to be clear.

### Stopped on the runway with no exit ahead

When `RunwayExitPhase` has no committed exit and the aircraft is at or below 1 kt (`BackstopStoppedSpeedKts`), for example after the runway-end safety stop or a forced landing that stopped short of the end, `TickStoppedWithoutExit` holds it at zero speed and searches again every tick:

- **A forced rollout** (`Phases.ForcedRollout`) backtracks on its own to the nearest free exit behind it (`TryCommitExitBehind`). `FindExitBehind` runs the same centerline walk on the reciprocal heading, under the reciprocal end's designator first so that end's no-turnoff list applies, and the end-of-runway turnoff counts.

  The rollout heading flips to the reciprocal only once the route to that exit is built (`BacktrackPending`, snapshotted), so a failed route build leaves the aircraft facing its rollout direction.
- **Any other aircraft** stays stopped, because reversing on the runway needs ATC approval (AIM 4-3-21.a). An exit ahead that is occupied or full is waited for.

  When there is no exit ahead at all, the pilot reports once (`ReportedNoExitAhead`, snapshotted) with `PilotResponder.BuildUnableToExitRequestBackTaxi`: "unable to exit, request back-taxi to {twy}." naming the nearest free exit behind, or "unable to exit the runway." when there is none. The instructor terminal shows "{CS} stopped on RWY {rwy} with no exit ahead, awaiting TAXI or EXIT".

A taxiway the crew gave up on the rollout counts as an exit ahead here: stopped, the aircraft may taxi forward to it (§ The given-up set), so it never asks for a back-taxi past one.

### Changing the exit after the route is committed

Handing a route to the navigator is **not** the same thing as turning off. Segment 0 of that route is the virtual approach leg — a straight down the runway centerline to the branch node — so a committed aircraft can still have most of the runway to run. A late `EL`/`ER`/`EXIT <twy>` is honored throughout that window and refused after it.

`RunwayExitPhase.EvaluateRetarget` is the single verdict, asked from two places: `GroundCommandHandler.TryExitCommand` at command time (so the controller gets immediate feedback instead of a success echo for an instruction that will be dropped) and `TryRetargetCommittedExit` at tick time, from the aircraft's updated position. Three gates close the window:

- **`TurnStarted`** — latched in `TickFollowingExitPath` once the navigator leaves segment 0, or once the heading diverges more than `RetargetMaxHeadingDeviationDeg` from the runway heading (the navigator's pre-turn blend starts rotating the nose inside the last ~50 ft while still nominally on segment 0).

  Latched, not recomputed: a momentary re-alignment must not reopen the window. It round-trips through the snapshot DTO because the restore path rebuilds the route from segment 0 and would otherwise forget the aircraft was turning.
- **The turn lead** — the branch point must still be `max(RetargetLeadSeconds × groundspeed, MinRetargetLeadFt)` plus the main-gear turn radius ahead. The radius term covers `GroundNavigator.StraightArrivalThresholdNm`'s tangent corner-rounding, which begins the arc `r·tan(δ/2)` *before* the branch vertex (there is no tangent before a node carrying a hold-short, which is arrived at exactly).

  `RetargetLeadSeconds` is **4.0** and means one thing only (aviation-reviewed): a receive/readback/re-plan budget, well under the 5-10 s used for airborne clearances because an exit instruction during rollout is high-expectancy.
- **Reachability** — `RunRetargetSearch` resolves the new preference **exactly, with no relaxation**, and accepts a candidate only if it is past that lead **and** the aircraft can still brake to its turn-off speed by the exit's turn-entry point (`RolloutBraking.RequiredDecelKtsPerSec` against `RolloutBraking.NamedExitBrakingLimit` — the firm rate, or the max-effort rate when the instruction runs without delay — the same limit `LandingPhase` judges a named exit by).

  `EvaluateRetarget` takes that expedite as an argument: the handler passes the one the instruction will run under (`EXP` on it, or standing for the same taxiway), not the flag as it stood, so a fresh `EXIT W5 EXP` is judged at max effort and a bare `EXIT W5` naming a new taxiway under a standing `EXP` at the firm rate; the tick re-asks with the flag the handler wrote. The search skips taxiways the crew has given up, except the one the instruction names (§ The given-up set).

  An accepted re-target carries its limit to the new turn-off (`TurnOffDecelRate`). The two gates are deliberately separate: at a 4 s lead the lead is the tighter of the two for any normally-rolling aircraft, so the braking check is currently insurance rather than the binding constraint — but it is what lets the lead be tuned as a pure reaction number without silently admitting exits the aircraft would arrive at hot.

  Tearing down a perfectly good committed exit for a taxiway that cannot be used is worse than refusing, so a named taxiway that does not resolve is refused rather than given the silent taxiway → side → any relaxation the pre-commit path uses: with the crew's *"unable D."* when a connection of D is ahead but past the braking limit or inside the turn lead (`IsAnyConnectionAhead`), and *"Unable, no D ahead"* only when no connection of D lies ahead.

  ("no X ahead", not "X is behind" — the search also returns nothing when the taxiway is not on this runway at all.)

The kinematics behind that check live in `Phases/RolloutBraking.cs`, shared with `LandingPhase` — both rollout phases answer the same question ("can this aircraft be at that exit's turn-off speed by its branch point?"), so `RequiredDecelKtsPerSec` / `BrakingDistanceNm`, the `TurnOffSpeedToleranceKts` limit and `NamedExitBrakingLimit` (the per-category `CategoryPerformance.FirmBrakingRate`, or `ExpediteExitDecelRate` without delay) have one definition rather than one per phase.

`RequiredDecelKtsPerSec` answers a non-positive distance with the category's firm rate ("at least firm braking"); `DecelOverDistanceKtsPerSec` is the category-free formula for callers that own that case themselves (the forced rollout's runway-end stop).

**Refusals are spoken, not just printed.** `EL`/`ER`/`EXIT` are `Ground`-category verbs, so `CommandRegistry.DefaultProducesPilotUnable` routes a rejection into `PilotResponder.BuildUnable` and a solo pilot says it on frequency — unless the handler supplies the pilot's own line on `CommandResult.PilotUnable` (from `ExitInstructionVerdict.PilotUnable`), which `SimulationEngine` speaks instead: the exit refusals "unable D." and "unable, no D ahead." do, so the spoken form spells the taxiway.

Two consequences for anyone authoring a rejection string on a command path: it is stripped of its leading "unable" token and spoken as *"unable, {rest}"*, so **"Unable to X, …" comes out as "unable, to X, …"** — write the reason as a standalone clause.

`CleanUnableReason` handles en/em dashes alongside the ASCII hyphen, so no refusal is spoken as *"unable, — already turning off at G"*; `RefusalText_SurvivesThePilotUnablePipeline` and `BuildUnable_StripsTheLeadingTokenAndAnyDashAfterIt` pin both halves.

On success the phase drops its navigator and route, loads the new exit, and falls back through `OnTick`'s normal commit block, which re-runs `StartExitNavigation` from the current position. That reuse is what keeps it safe: the aircraft is still on the centerline at runway heading, exactly the state a first commit assumes.

**The occupancy set must have this aircraft's own claim subtracted first** (`RetargetOccupancyExcludingSelf`) — `SimulationEngine.BuildOccupiedHoldShortNodes` adds every `TargetHoldShortNodeId`, so without that an `EL` on an aircraft already exiting left could never resolve back to the exit it currently holds and would needlessly skip to the next taxiway.

The re-targeted aircraft adds power back, but its ceiling is the exit route's `min(coastSpeed, TaxiSpeed)` (30 kt jet, 20 kt piston), not the full rollout coast speed — `StartExitNavigation` caps the whole route including the virtual runway segment.

### Restoring a committed exit from a snapshot

`_exitRoute` is runtime-only — it is built from the live ground layout, so a restored phase comes back with the state, the path node ids and the navigator but no route. `OnTick` rebuilds it on the first tick, and that rebuild must resume on the segment the live route was on rather than restarting at segment 0.

Segment 0 is the virtual approach leg [aircraft position → branch node].

Rebuilt from segment 0 for an aircraft that has *already passed* the branch, it points backward: `GroundNavigator` reads its ~180° heading delta as a corner to round, commits to the entry-alignment slow-turn, and taxis the reconstruction back onto the runway it just vacated — a ~180° heading divergence from what happened live, in every path that restores from a snapshot (rewind, bug-bundle reconstruction, client playback). That was issue #309.

Three pieces hold the resume together:

- **`ExitWaypointIndex` round-trips** and is threaded into the rebuild. Segment indexing is stable across the rebuild because the route shape is a pure function of `_exitPath`: `[virtual approach, path[0]→path[1], …, hold-short → virtual-past]`, so segment *k* ≥ 1 is always `path[k-1] → path[k]`. `ToSnapshot` falls back to the restored index rather than 0 when no route exists yet, so a round-trip before the first tick doesn't lose the cursor again.
- **`ResumeSegmentIndexAfterRestore` floors the index at 1 when the aircraft is past the branch** (along-track, on the runway heading). A snapshot can land on the tick before the navigator signals arrival, so the stored index alone still reads 0 while the aircraft is physically on the exit taxiway.
- **Segment 0 is re-anchored when resuming past it** — on the centerline `RestoredApproachSegmentNm` *behind* the branch instead of at the aircraft. The navigator reads that leg's arrival bearing as the corner's incoming tangent (and its length feeds the adaptive rounding radius and the short-connector check), so it has to still be the runway heading.

The live first-commit and re-target paths are untouched: both pass a resume index of 0 and get exactly the route they always did. `RunwayExitPhase.FromSnapshot` keeps the saved navigator (`_restoredNavigator`) and the first tick's rebuild makes the navigator from it, so a restore mid-fillet continues the same arc at the same progress as the live run ([ground/navigator.md](ground/navigator.md) § Snapshot round-trip of the active primitive).

### Why the virtual segment matters

GroundNavigator computes turn arcs based on the angle between consecutive segments. Without the virtual segment, the navigator has no inbound context — it doesn't know the aircraft was approaching from the runway. The virtual segment provides this context naturally, and a longer segment (more distance before the branch) produces better turn anticipation. This is why LandingPhase should hand off early, not at the branch point.

## GroundNavigator — Turn Execution

GroundNavigator handles the actual turn through the exit. It uses:

- **Backward-propagated braking**: walks future segments, collects speed constraints at each turn, and back-propagates braking limits. The aircraft never overspeeds into a future turn. On a turn-off with its own rate (`SlowdownDecelRateKts`) each constraint keeps its own curve instead — slowdowns at the turn-off rate, the route-end stop at the stop rate — see [ground/navigator.md](ground/navigator.md) § Speed.
- **Turn anticipation**: for turns ≥20°, the arrival threshold expands so the aircraft begins turning before reaching the node, creating a smooth arc.
- **Heading-based speed scaling**: speed reduces proportionally to heading error (full speed at 0° error, 15% at ≥120°), modeling realistic ground steering constraints.
- **The one-tick overshoot backstop applies only where the aircraft must arrive precisely** — a stop target, the route's last segment, or an arc entry (`TickBezier` writes position from curve state at engagement). A pass-through node on a straight polyline is retired by advance-on-pass, so it gets no backstop.

  Applied everywhere, on a chorded exit whose every chord arrives on the tight 1.8 ft threshold (SFO 28R's T: 16 chords of 38–145 ft) it would slash the target over the last ~16 ft of each chord; physics would brake at 5 kt/s and recover at the 1.0 kt/s taxi accel with the next node 1–2 s away, so SKW3398 would ratchet 37 → 17 kt and take 39 s to clear a 1,242 ft exit leg that it clears in 28 s at a 27.8 kt mean.

  `SameRunwayArrivalProtection`'s `RunwayExitPhase` regime models the same profile (steady at the route's taxi ceiling, then v²/2a of braking), so the vacate prediction the go-around and the spacing pass share tracks the phase (28.6 s predicted / 28 s flown).

Where SFO 28R's runway occupancy goes (S1-SFO-2 bundle, E75L): ~6 s threshold → touchdown, ~41 s rollout to the E/T branch at ~6,200 ft — vNAS marks L and P `noTurnoff` on both 28s, so E/T is the first permitted exit whatever the brake rate — then the exit leg. T's 28R hold bar is 279 ft off the centerline but ~1,010 ft along the 33° exit, so "clear" is a 1,050 ft taxi plus half a fuselage.

E off 28L (70°) is bound by its fillet's 12.1-kt arc cap through the turn and clears its 566 ft leg in ~26 s. The phase completes only when the aircraft has *stopped* half a fuselage past the bar — ~3 s after the tail is actually clear — even when `AutoPullUpToParallel` then rolls it on; that and the rollout-braking retune are backlog items in `docs/plans/MAIN.md`. `Sfo28rArrivalOccupancyTests` pins the two vacates.

## Parallel-Runway Auto-Pull-Up (issue #175)

When an aircraft vacates **between two parallel runways** — e.g. lands OAK 28L, exits right onto G, and G crosses on to 28R ~500 ft away — `RunwayExitPhase.CompleteExit` can auto-advance it to hold short of the *parallel* runway instead of stopping at the landing runway's exit hold-short.

- **Trigger** (`AirportGroundLayout.FindParallelRunwayCrossing`): walk the same exit taxiway forward from the landing-runway hold-short. If the next runway hold-short reached belongs to a *different, (anti-)parallel* runway with **no intervening taxiway intersection** in between, return its near-side and far-side hold-shorts plus the node paths.

  The walk across the parallel runway (`WalkAcrossToFarHoldShort`) is capped by distance walked (`MaxRunwayCrossingWalkFt`, 1500 ft), not by node count: a node count depends on how many points the GeoJSON line has (SFO Q across 28L is 11 edges for 617 ft). The near-side walk (`WalkToParallelNearHoldShort`) is still capped at 10 hops.

  An intervening intersection (a node carrying a different taxiway) aborts the search — the controller may want to route the aircraft down that taxiway, so the aircraft stops at the landing exit (today's behavior). Forward direction comes from the exit path's tail node, so an outer-side exit (pointing back across the landing runway) finds nothing.
- **Reuse, not new geometry**: `CompleteExit` synthesizes a normal `AssignedTaxiRoute` of **real nodes** `[landing-HS → parallel-near-HS → … → parallel-far-HS]`, annotates it with `HoldShortAnnotator.AddImplicitRunwayHoldShorts` + `ComputeHoldShortPositions` (which stops the nose *at* the parallel's near hold-short line, not past it; the route's far bar is not a stop), and hands off to a `TaxiingPhase`.

  The existing taxi machinery then drives the pull-up, stops short of the parallel, inserts the `HoldingShortPhase`, and — once a `CROSS` clears it — runs the `CrossingRunwayPhase` across and holds clear on the far side. No virtual nodes are added to the route (a negative `VirtualNode` id would drop the whole route on snapshot restore).
- **Gating**: the `AutoPullUpToParallel` scenario setting (opt-out, on by default for live sessions via the client preference; off in `SimScenarioState` so pre-feature recordings replay faithfully). Independent of `AutoCrossRunwayMode` — the pull-up **always** requires an explicit `CROSS`/`CROSS <rwy>`; it never auto-crosses the parallel.
- **Known limitation (YAAT-834)**: after the `CROSS`, the crossing currently stops with the main gear on the parallel runway's far bar and the tail still inside the hold line.
- **Exit capacity**: see the next section for how many aircraft a sidecar lets stand between the two runways.
- **Solo phraseology**: the synthesized pull-up hold-short carries `HoldShortReason.RunwayCrossing`, so it never triggers the solo-mode "ready for departure" report — that report fires only for `HoldShortReason.DestinationRunway` (the aircraft's assigned departure runway). A landed aircraft crossing a parallel is not a departure; it still surfaces a controller-facing "holding short runway <parallel>" reminder.

## Exit capacity between parallel runways

Some exits between parallel runways hold only a few aircraft before the last one's tail is back over the landing runway. An airport sidecar's `exitCapacity` entries (authoring schema in `src/Yaat.Sim/Data/ARTCCs/README.md`) cap each one. SFO's ten crossovers between 28R and 28L (Q, K, D, T, E, L, C, R, N, P) each hold two aircraft when every aircraft involved is CWT G, H or I, and one otherwise; an aircraft with no known CWT counts as above G. Each crossover is one shared segment, so a 28L arrival and a 28R arrival draw on the same capacity.

- **Segment.** `ExitCapacityResolver` resolves each rule against the live layout into an exit capacity segment (`ExitCapacitySegment`): the stretch of the rule's taxiway from the landing runway's exit hold-short to the parallel runway's hold-short, found with the same forward walk as the auto-pull-up (`FindParallelRunwayCrossing`). A crossovers rule walks from both runways' sides and must agree on the one segment between the two bars.

  Either bar is an exit bar, whichever runway the arrival is on: `ExitBarFor(runway)` gives that runway's bar. A rule that does not resolve to exactly one segment logs an Error and is dropped for that layout, so exits there behave as if it were absent; `ExitCapacitySidecarTests` fails on such an entry. Segments are cached per layout instance.
- **Occupants.** `SimulationEngine.BuildOccupiedHoldShortNodes` counts, in the same pass as the occupied hold-shorts, every ground aircraft inside a segment, hold line to hold line, in either direction and under either rule form: exiting toward or holding after exiting at either bar, taxiing on a pull-up route from one bar toward the other, holding short at either bar, or simply standing or moving on the segment's pavement.

  An occupant travelling toward the arrival's exit bar, or stopped (under 1 kt), fills the segment outright. Its direction comes from its phase, else from its track against the far-bar-to-exit-bar bearing.
- **Same-tick claims.** An arrival committing an exit (both commit sites in `RunwayExitPhase`, and the retarget path) joins the segment's occupants at once through `PhaseContext.JoinExitSegmentOccupants`, so of two arrivals choosing the same crossover in one tick the first in world order wins.
- **Capacity.** The sidecar's maximum count is a ceiling. On top of it the arrival needs runtime length: the free length is the smallest distance, by position, from the arrival's exit bar to the tail of a same-direction occupant, and the needed length is the arrival's length plus `GroundConflictDetector.StopGapFt` to the nearest occupant plus `RunwayTailClearance.MarginFt`, the margin past the exit bar its exit stops its tail at. The stop gap is a sim convention; the rule it serves is AIM 4-3-21.a/b, that the whole aircraft is past the hold line (`SimulationEngine.ExitSegmentRefusal`).
- **A full segment reads as an occupied exit.** For an arrival in `FinalApproachPhase`, `LandingPhase` or `RunwayExitPhase` on either of the rule's runways, `OccupancyForExitChoice` adds that runway's exit bar of every segment that is full for it to that aircraft's `OccupiedHoldShortNodes`, so it takes the next exit by the normal logic (AIM 4-3-21.a). An explicit `EXIT T` naming the segment's taxiway bypasses the check. A refusal is logged at Debug under the `ExitCapacity` category, once per distinct occupancy: `[ExitCapacity] … refused (N ft free from the exit bar to the nearest tail, M ft needed)`.
- **Simplifications.** An arrival still in `LandingPhase` that has picked a crossover is not yet counted as an occupant. `SfoExitCapacityTests` and `SfoCrossoverCapacityTests` pin the SFO rules.

## Constants

| Constant | Jet | Turboprop | Piston | Helicopter |
|----------|-----|-----------|--------|------------|
| Coast speed (kts) | 40 | 35 | 25 | 15 |
| Default rollout decel (kts/s) | 3.6 | 3.0 | 2.5 | 0 |
| Comfortable exit decel (kts/s) | 4.5 | 3.75 | 3.75 | 0 |
| Touch-and-go rollout decel (kts/s) | 1.0 | 1.5 | 1.5 | 0 |
| High-speed exit turn-off (kts) | 30 | 25 | 18 | 15 |
| Standard exit turn-off (kts) | 15 | 15 | 12 | 10 |
| Ground turn rate ceiling (deg/s) | 12 | 16 | 20 | 30 |
| Taxi corner speed (kts) | 15 | 15 | 10 | 10 |

`Ground turn rate` is now a **ceiling**, not a flat rate: achievable ground yaw is `ω = v/R` at the comfortable main-gear turn radius (`MainGearTurnRadiusFt`; `CategoryPerformance.GroundYawRateAtSpeed`), capped at the ceiling above the ~3 kt crossover. A taxiing aircraft can only slew its nose as fast as it rolls forward, so a near-stationary aircraft no longer pivots at the full rate (a 120° turn takes ~6 s piston / ~10 s jet, not ~2 s).

Helicopters are exempt (a wheeled pedal-turn holds the hover rate). Tight fillets are additionally held at their curvature speed via `GroundArc.MaxSafeSpeedKts` (which folds in the same `ω·r` yaw-rate cap), so a jet no longer accelerates through a sharp ramp fillet at 20 kt / 0.84 g.

| Constant | Value | Purpose |
|----------|-------|---------|
| Firm braking limit (`FirmBrakingRate`) | jet 5.0 / TP 5.0 / piston 4.0 / helo 3.0 kts/s | Max decel for explicit exit commands and the firm-rate fallback |
| Expedite braking rate | jet 7.5 / TP 6.0 / piston 4.5 / helo 4.0 kts/s | Max-effort decel for `EXP` ("without delay") exits |
| Expedite taxi acceleration | 1.5 kts/s jet, TP, piston; 1.0 Super/Heavy and helo | `ExpediteTaxiAccelRate`, the acceleration of a `TAXI`/`TAXIAUTO`/`CROSS … EXP` taxi or crossing (normal 1.0) |
| Uninstructed exit limit | by exit class | High-speed exit: `ComfortableExitDecelRate`; standard exit: `RolloutDecelRate`; none qualifies: firm-rate fallback |
| Min soft braking | 0.5 kts/s | Floor for gentle decel on far exits |
| Turn-off tolerance | 3.0 kts | Discrete-tick overshoot margin |
| High-speed exit threshold | 45° | Exits ≤45° use high-speed turn-off |
| High-speed exit bonus | 0.15 nm | Scoring bonus for ≤45° exits |
| Turn-entry lead | R·tan(min(Δ, 120°)/2) | Standard exits only: how far before the branch the turn begins (`RolloutBraking.TurnEntryLeadNm`); the point the hand-off, plan and missed-exit test use |

## Exit Angle Classification

The angle is the one measured in "Exit angle and exit chain" above, so the class of an exit follows its turn off the runway, not where its bar stands.

- **High-speed exits** (≤45°): shallow turns, higher turn-off speed (30 kts for jets). Tolerate shorter virtual segments since the turn angle is small.
- **Standard exits** (>45°): steep turns, lower turn-off speed (15 kts for jets). Need more distance for the turn arc. Judged against the turn-entry point short of the branch (§ The turn-entry point); rejected once the aircraft is past it and too fast to turn.

## Final Approach Course (FAC) Alignment

The aircraft flies the published final approach course down to minimums, then transitions visually onto the runway centerline — heading and lateral position together, with no snap at the threshold.

- **FAC derivation.** `FinalApproachCourseExtractor` derives the published FAC from the CIFP missed-approach (MAP) leg rather than hardcoding runway heading. CF/FA legs use `OutboundCourse`; TF/DF legs use the great-circle bearing to the MAP fix; RF and anything else fall back to runway heading. This is what makes offset approaches (LDA, RNAV-with-offset CF leg, VOR offset) track their published FAC instead of the centerline.
- **Magnetic-to-true conversion uses the CIFP variation of record, not live WMM.** CF/FA leg `OutboundCourse` is magnetic and converts to true via the airport's CIFP-published variation (`NavigationDatabase.GetAirportMagneticVariation`, parsed from the CIFP airport PA record cols 51-55), not `MagneticDeclination.GetDeclination` (current WMM) — the two drift apart across AIRAC epochs (~1-2°), which pushes an aligned ILS localizer off centerline.

  `FinalApproachCourseExtractor.Extract` uses `navDb.GetAirportMagneticVariation(runway.AirportId) ?? MagneticDeclination.GetDeclination(...)`, falling to WMM only when CIFP has no record (issue #187: KIAH I26R 267° mag — WMM +1.7° gives 268.7° true vs runway 269.95°; CIFP variation +3.0° gives the correct 270.0°).

  Only the CF/FA path is affected; TF/DF is geometric. Do not "snap" the FAC to runway heading on a tolerance to paper over this — a real offset ILS would be wrongly flattened; fix the conversion instead.
- **Visual alignment ramp.** `FinalApproachPhase.OnTick` lerps both the lateral cross-track course and the lateral anchor from FAC/anchor toward runway-heading/threshold via a smoothstep, so the aim-point bearing rotates onto the centerline over the last ~200 ft AGL.

  The ramp is a no-op below `FacRampMinOffsetDeg` (0.5°); small CIFP/mag-var divergences (0.5°–5°) ramp over ~300→100 ft AGL, genuine offsets (≥5°) over ~1000→500 ft AGL. There is no separate heading snap — heading and position converge together.

## Short-Approach Base & Landing Geometry

The `SA` (Make Short Approach) compressed pattern has two coupled geometry invariants that must hold together:

- **Base-leg descent targets the rollout point.** `BasePhase`'s SA branch targets glideslope altitude at `(finalDist + turnRadius)`, not at `finalDist` — the 90° base→final turn translates the aircraft one turn radius further along the final, so targeting at `finalDist` would put it at GS-intercept altitude before the turn fires and trip the landing stabilization gate.
- **LandingPhase floats over the runway while rolling out.** When heading-error from runway exceeds 5° (`_floatingForRollout`), LandingPhase holds level (target altitude = current, vertical rate = 0) until wings level, then resumes descent → flare → touchdown. This lets a tight turn complete before the bank-stabilization gate engages. The descent target is restored on the first non-rolling-out tick.

## Key Files

- `src/Yaat.Sim/Phases/Tower/LandingPhase.cs` — Rollout braking, exit candidate resolution, unable-replan, the forced (CLANDF) rollout
- `src/Yaat.Sim/Phases/Tower/ForcedLandingProfile.cs` — CLANDF aim point, descent cap and braking constants
- `src/Yaat.Sim/Phases/Tower/FinalApproachExitForecast.cs` — The exits-ahead list on the last miles of final: projected touchdown, window, cache
- `src/Yaat.Sim/ExitAheadDto.cs` — One exit of the list (taxiway, side, distance, planned, turn-off angle)
- `src/Yaat.Sim/Commands/GroundCommandHandler.RolloutTaxi.cs` — TAXI / TAXIAUTO on the rollout: implied exit, stored route, join rules, starting the route at exit completion
- `src/Yaat.Sim/Phases/Ground/RunwayExitPhase.cs` — Analog rolling, virtual segments, exit search, the stopped-without-exit backstop
- `src/Yaat.Sim/Data/Airport/ExitCapacityResolver.cs` — Resolves sidecar `exitCapacity` rules into exit capacity segments
- `src/Yaat.Sim/Phases/Ground/GroundNavigator.cs` — Steering, turn anticipation, backward-propagated braking
- `src/Yaat.Sim/Data/Airport/AirportGroundLayout.cs` — FindExitFromCenterline, FindAdjacentHoldShort, InferPreferredExitSide, exit scoring
- `src/Yaat.Sim/AircraftCategory.cs` — All category-specific performance constants
- `src/Yaat.Sim/Phases/Tower/FinalApproachPhase.cs` — FAC tracking and the visual-alignment ramp onto the centerline
- `src/Yaat.Sim/Data/Vnas/FinalApproachCourseExtractor.cs` — Derives the published FAC from the CIFP MAP leg
- `src/Yaat.Sim/Phases/Pattern/BasePhase.cs` — Short-approach base-leg descent geometry

## Anti-Patterns to Avoid

**Do not try to deliver the aircraft to a specific speed at a specific node.** This is the single most important rule. LandingPhase delivers to coast speed, RunwayExitPhase finds the exit and builds the virtual segment, GroundNavigator handles precision braking. Each phase has one job.

**Do not walk centerline nodes during rollout.** The runway is a continuous surface. Use `GeoMath.AlongTrackDistanceNm` to measure distances, not node-to-node traversal.

**Do not shorten the virtual segment.** A longer virtual segment is always better — it gives GroundNavigator more context for turn anticipation. Handing off the aircraft far from the exit (at coast speed with room to brake) is correct behavior.

**Do not loosen the unable/degenerate guards.** A standard exit is rejected once the aircraft is past its turn-entry point too fast to turn, or at its branch point still in the rollout. This prevents degenerate near-zero virtual segments that cause heading reversals. Fix the braking planning instead.

**Brake toward the exit's turn-off speed when it is below coast.** LandingPhase targets `min(coastSpeed, candidateExit.TurnOffSpeed)` so a slow piston can actually take a 12-kt standard exit — the missed-exit check fires past the turn-entry point while IAS is above turn-off + 3 kt, so the aircraft has to be at or below turn-off speed by that point, not at coast.

RunwayExitPhase still owns braking through the turn; LandingPhase just stops stranding slow aircraft above a reachable exit's turn-off.
