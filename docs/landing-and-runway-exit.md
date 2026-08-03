# Landing Rollout & Runway Exit — Design & Implementation

> This is the **ground** half of an arrival. For the **airborne** half upstream of touchdown — approach intercept, pattern legs, holding, and glideslope geometry — see [approach-and-pattern-geometry.md](approach-and-pattern-geometry.md).

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

Measured end-to-end through the production tick loop (`TouchdownPointTests`), touchdown lands at **B738 1,367 ft / DH8D 798 ft / C172 401 ft** past the threshold — all inside the AIM 2-1-5.b touchdown zone (100–3,000 ft), with the transports on or just past the AIM 2-3-3.b.4 aiming point markings and the light single near the numbers.

Each figure is `aimPoint + flareFloat`, and **the two are coupled**: raising a crossing height moves the whole path back, so the float has to shrink to match (AIM 1-1-9.d.7 NOTE: "a higher than optimum TCH … may cause the aircraft to touch down further from the threshold"). That coupling is why the jet's `FlareDescentRate` is 370 fpm rather than the 200 it carried while the glidepath had no crossing height at all — 200 fpm bought a 2,300 ft float that faked an aiming point the geometry wasn't providing. `FlareDescentRate` is the sink commanded *at flare initiation*, decaying with height; a transport enters the flare from ~700 fpm and arrests to ~150 by touchdown.

## Displaced thresholds: which datum

`RunwayInfo.ThresholdLatitude`/`Longitude` are **pavement** ends — the nav database builds them from the vNAS `start_location`/`end_location`, which ignore any displacement. The displacement lives on the airport map, so an end's landing threshold only exists once a `AirportGroundLayout` is in hand: `LandingThreshold.Resolve(runway, layout)` (or the `GroundRunway` overload, for callers that already looked the ground runway up). Both fall back to the pavement threshold when there is no layout, which is what every pre-existing replay depends on.

AIM 2-3-3.b.8.2: pavement behind a displaced threshold is available for **takeoff in either direction** and for rollout from the opposite end, but not for landing in that direction. So the two datums split by *who is being measured*:

| Landing threshold (arrival datum) | Pavement threshold (departure / surface datum) |
|---|---|
| `FinalApproachPhase` glidepath + completion | `TakeoffPhase`, `LineUpGeometry`, `LineUpGraphRoute` |
| `LandingPhase` (`LandingPlan.ThresholdLat/Lon`) — flare, centerline steering, LAHSO progress | Runway rendering, `RunwayCrossingDetector` runway rectangles |
| `LowApproachPhase`, `ApproachNavigationPhase` continuous descent | `ConflictAlertDetector` corridors (must cover the whole surface) |
| `InterceptCoursePhase.ThresholdLat/Lon` + `ApproachGateDatabase` (P/CG measures the gate from the landing threshold) | `RunwayIntersectionCalculator` reported distances (its consumers are departures rolling from the pavement) |
| `PatternGeometry` downwind-abeam, base turn, `ThresholdLat/Lon` | `PatternGeometry` departure end / crosswind turn (AIM 4-3-2 anchors these beyond the *departure* end) |
| `SoloTrainingEvaluator.AlongLandingThresholdFt` — §3-10-3 "n feet down the runway", `IsLandingAfterThreshold` | `SoloTrainingEvaluator.AlongThresholdFt` — §3-9-6 departure spacing, intersection passage, "crossed the runway end" |

LAHSO is the one place both datums appear in a single calculation: `RunwayIntersectionCalculator.ComputeHoldShortDistanceNm` walks the pavement centerline to the intersection, then subtracts the displacement so the reported distance is the available landing distance a LAHSO clearance actually offers. `PatternCommandHandler` projects the hold-short point from the landing threshold to match — so authoring a displacement shortens the reported distance without moving the physical hold-short point.

## LandingPhase Braking Strategy

LandingPhase's job is to decelerate the aircraft to the speed needed for the committed exit. For high-speed exits this is coast speed; for standard exits whose turnoff is below coast it is the exit's turnoff speed. RunwayExitPhase and GroundNavigator handle turn geometry and precision braking through the turn.

### Default exits (no explicit preference)

The pilot picks the first comfortable forward exit (AIM 4-3-21.1 "exit at the first available taxiway"). "Comfortable" means achievable at 1.5x the default rollout decel rate — not the first exit that requires maximum effort. Back-exits (>100°) are deferred during the centerline walk: `FindExitFromCenterline` keeps looking for a forward exit and only returns a back-exit if nothing forward is found within the walk range.

- Target the smaller of `coastSpeed` and the exit's `turnOffSpeed` — a 12-kt standard exit needs the aircraft at 12 kt at the branch, not at 25 kt coast
- Subtract a braking buffer: the distance RunwayExitPhase needs to brake from coast speed to the exit's turn-off speed (using the default decel rate)
- Plan decel to reach the target speed at that buffer point — not at the exit itself
- If the exit is far enough that normal braking would reach target too early, use a gentler rate (floored at 0.5 kts/s) to avoid a long pointless coast

### Side preference and the "later on-side beats earlier off-side" rule

When a side preference is in play (explicit from `EL`/`ER`, or inferred from runway/parking layout via `InferPreferredExitSide`), the planner walks past **off-side** candidates while looking for an **on-side** option further down the runway. Crossing the runway centerline to exit increases controller workload (the aircraft now has to taxi back across to reach parking), so a same-side exit a bit further down beats an opposite-side exit at the closest taxiway.

- The off-side candidate is remembered as a fallback. It is only committed if the walk exhausts without finding an on-side option (e.g. one-sided exits like C3 at SFO, or every later option requires more than firm braking).
- For default selection (no explicit taxiway), the planner also passes `OccupiedHoldShortNodes` to the BFS so a known-occupied on-side hold-short doesn't appear as the on-side answer at that branch — the search naturally moves to the next exit. Explicit-taxiway commands (`EXIT G`) still ignore occupancy at planning time; RunwayExitPhase relaxes reactively at handoff if the named exit becomes blocked.
- The shared lookahead lives in `AirportGroundLayout.FindOnSidePreferredExit`. LandingPhase uses it with a comfort-braking filter; RunwayExitPhase uses it with only a back-exit filter.

### Explicit exits (EXIT T, EL, ER, etc.)

The pilot is committed to a specific exit. LandingPhase uses firm braking (up to 5 kts/s) if needed, but still targets coast speed for the handoff.

A **taxiway-only** exit (`EXIT D`) issued after an explicit side (`EL`/`ER`) inherits the standing side, so a `ER ; EXIT D` sequence (two separate commands, common in scenario presets) exits *right at D* rather than dropping the side and falling back to the inferred side. The taxiway name is a hard constraint and the side a soft preference: if the named taxiway exists only on the other side, the on-/off-side fallback in `FindAdjacentHoldShort`/`FindOnSidePreferredExit` still takes it (never fails to exit). A later command carrying its own explicit side (`EL D`) still overrides. The merge lives in `GroundCommandHandler.TryExitCommand`.

- Compute distance to the exit
- Subtract a braking buffer: the distance RunwayExitPhase needs to brake from coast speed to the exit's turn-off speed (using the default decel rate)
- Plan decel to reach coast speed at that buffer point — not at the exit itself
- If the exit is far enough that normal braking would reach coast too early, use a gentler rate (floored at 0.5 kts/s) to avoid a long pointless coast
- If the exit requires more than firm braking (5 kts/s), broadcast "unable" and replan

### Expedited exits (EXP — "without delay")

`EXP` (the standalone command on a just-landed aircraft, or the `ER`/`EL`/`EXIT` modifier) clears the runway as fast as possible. It sets `AircraftGroundOps.IsExpeditingExit`, which raises the braking limit from the firm 5 kts/s to a category-specific max-effort rate (`CategoryPerformance.ExpediteExitDecelRate` — jet 7.5, turboprop 6.0, piston 5.0, helicopter 4.0 kts/s). The higher limit feeds both `LandingPhase.BrakingLimit` (the exit-reachability filter *and* the braking rate) and `RunwayExitPhase` (via `GroundNavigator.DecelRateKts` for the hold-short stop), so the aircraft:

- **Takes the earliest reachable exit** — the comfort/firm filter that normally skips close exits is relaxed to the max-effort rate, so an exit one or two turnoffs earlier now qualifies.
- **Brakes harder during rollout** to make that earlier exit.
- **Keeps the high-speed turn-off speed** — the target is still `min(coastSpeed, turnOffSpeed)`, so a high-speed exit (≈30 kts jet) is taken at speed, not crawled.
- **Brakes firmly to the hold-short stop** after the turn-off (the navigator uses the same max-effort decel instead of the gentle taxi rate).

The standalone-`EXP` path is keyed on an *active* Landing/RunwayExit phase and resets the cached `LandingPhase` candidate (`ResetExitCandidate`) so the next tick re-resolves at the higher limit; the `ER`/`EL`/`EXIT EXP` modifier form re-resolves via the normal preference-change path. The flag is cleared when the exit completes (`RunwayExitPhase.CompleteExit`) and by `NORM`. Phraseology is **"without delay"** (7110.65 §3-7-2.b.10), not "expedite" — `EXP` is only the keyboard token.

### Unable and replan

When an exit is missed or unreachable:
1. The branch point is added to an exclusion set (never re-found)
2. If there was an explicit preference, broadcast "unable"
3. Relax the preference: drop the taxiway name, keep the side (from EL/ER)
4. Replan immediately — find the next comfortable exit, same as default behavior

## RunwayExitPhase — Analog Rolling

RunwayExitPhase does not walk centerline nodes. It:

1. **Steers** along the runway heading at the category's ground turn rate
2. **Adjusts speed** toward coast speed (accel/decel at taxi rates)
3. **Searches** for exits ahead using `TryFindExitAhead` — a continuous, distance-based search that respects preferences and applies soft tiebreakers (inferred side for taxiway-only commands)

When an exit is found, it builds a **virtual segment**: a synthetic route segment from the aircraft's current position to the branch node. This segment exists only to give GroundNavigator an inbound bearing for turn anticipation. The full route becomes `[virtual → branch → ... → hold-short]`.

Two contracts of the handoff to the navigator (`StartExitNavigation`):

- **The exit route's speed ceiling is `min(coastSpeed, TaxiSpeed × expedite multiplier)`**, not coast speed. The turn itself is governed by the junction arc's `MaxSafeSpeedKts` and back-propagated braking; the taxi-speed ceiling is what prevents the old slow-turn-then-surge profile (accelerate back toward 40 kt coast on the exit straight, then brake hard for the hold-short). Once off the runway the aircraft is taxiing — it only ever decelerates from the turn-off speed to the hold-short.
- **The analog-rolling heading hold is cleared** (`Targets.TargetTrueHeading` / `TurnRateOverride` → null). `TickRolling` steers via the persistent `ControlTargets`; the navigator steers by writing `TrueHeading` directly. Leaving the hold in place makes FlightPhysics turn the aircraft back toward the runway heading every substep, fighting the navigator's exit turn to a standstill (a false pure-pursuit "orbit").

### Changing the exit after the route is committed

Handing a route to the navigator is **not** the same thing as turning off. Segment 0 of that route is the virtual approach leg — a straight down the runway centerline to the branch node — so a committed aircraft can still have most of the runway to run. A late `EL`/`ER`/`EXIT <twy>` is honored throughout that window and refused after it.

`RunwayExitPhase.EvaluateRetarget` is the single verdict, asked from two places: `GroundCommandHandler.TryExitCommand` at command time (so the controller gets immediate feedback instead of a success echo for an instruction that will be dropped) and `TryRetargetCommittedExit` at tick time, from the aircraft's updated position. Three gates close the window:

- **`TurnStarted`** — latched in `TickFollowingExitPath` once the navigator leaves segment 0, or once the heading diverges more than `RetargetMaxHeadingDeviationDeg` from the runway heading (the navigator's pre-turn blend starts rotating the nose inside the last ~50 ft while still nominally on segment 0). Latched, not recomputed: a momentary re-alignment must not reopen the window. It round-trips through the snapshot DTO because the restore path rebuilds the route from segment 0 and would otherwise forget the aircraft was turning.
- **The turn lead** — the branch point must still be `max(RetargetLeadSeconds × groundspeed, MinRetargetLeadFt)` plus the nose-wheel radius ahead. The radius term covers `GroundNavigator.StraightArrivalThresholdNm`'s tangent corner-rounding, which begins the arc `r·tan(δ/2)` *before* the branch vertex. `RetargetLeadSeconds` is **4.0** and means one thing only (aviation-reviewed): a receive/readback/re-plan budget, well under the 5-10 s used for airborne clearances because an exit instruction during rollout is high-expectancy.
- **Reachability** — `RunRetargetSearch` resolves the new preference **exactly, with no relaxation**, and accepts a candidate only if it is past that lead **and** the aircraft can still brake to its turn-off speed (`RolloutBraking.RequiredDecelKtsPerSec` against the firm rate, or the expedite rate under `EXP` — the same limit `LandingPhase.BrakingLimit` uses for an explicit exit). The two gates are deliberately separate: at a 4 s lead the lead is the tighter of the two for any normally-rolling aircraft, so the braking check is currently insurance rather than the binding constraint — but it is what lets the lead be tuned as a pure reaction number without silently admitting exits the aircraft would arrive at hot. Tearing down a perfectly good committed exit for a taxiway that is not ahead is worse than refusing, so a named taxiway that does not resolve gets *"Unable, no D ahead"* rather than the silent taxiway → side → any relaxation the pre-commit path uses. ("no X ahead", not "X is behind" — the search also returns nothing when the taxiway is not on this runway at all.)

The kinematics behind that check live in `Phases/RolloutBraking.cs`, shared with `LandingPhase` — both rollout phases answer the same question ("can this aircraft be at that exit's turn-off speed by its branch point?"), so `RequiredDecelKtsPerSec` / `BrakingDistanceNm` and the `FirmBrakingRateKtsPerSec` / `TurnOffSpeedToleranceKts` limits have one definition rather than one per phase.

**Refusals are spoken, not just printed.** `EL`/`ER`/`EXIT` are `Ground`-category verbs, so `CommandRegistry.DefaultProducesPilotUnable` routes a rejection into `PilotResponder.BuildUnable` and a solo pilot says it on frequency. Two consequences for anyone authoring a rejection string on a command path: it is stripped of its leading "unable" token and spoken as *"unable, {rest}"*, so **"Unable to X, …" comes out as "unable, to X, …"** — write the reason as a standalone clause. `CleanUnableReason` handles en/em dashes alongside the ASCII hyphen (it did not, which is how *"unable, — already turning off at G"* was possible); `RefusalText_SurvivesThePilotUnablePipeline` and `BuildUnable_StripsTheLeadingTokenAndAnyDashAfterIt` pin both halves.

On success the phase drops its navigator and route, loads the new exit, and falls back through `OnTick`'s normal commit block, which re-runs `StartExitNavigation` from the current position. That reuse is what keeps it safe: the aircraft is still on the centerline at runway heading, exactly the state a first commit assumes. **The occupancy set must have this aircraft's own claim subtracted first** (`RetargetOccupancyExcludingSelf`) — `SimulationEngine.BuildOccupiedHoldShortNodes` adds every `TargetHoldShortNodeId`, so without that an `EL` on an aircraft already exiting left could never resolve back to the exit it currently holds and would needlessly skip to the next taxiway.

The re-targeted aircraft adds power back, but its ceiling is the exit route's `min(coastSpeed, TaxiSpeed)` (30 kt jet, 20 kt piston), not the full rollout coast speed — `StartExitNavigation` caps the whole route including the virtual runway segment.

### Restoring a committed exit from a snapshot

`_exitRoute` is runtime-only — it is built from the live ground layout, so a restored phase comes back with the state, the path node ids and the navigator but no route. `OnTick` rebuilds it on the first tick, and that rebuild must resume on the segment the live route was on rather than restarting at segment 0.

Segment 0 is the virtual approach leg [aircraft position → branch node]. Rebuilt from segment 0 for an aircraft that has *already passed* the branch, it points backward: `GroundNavigator` reads its ~180° heading delta as a corner to round, commits to the entry-alignment slow-turn, and taxis the reconstruction back onto the runway it just vacated — a ~180° heading divergence from what happened live, in every path that restores from a snapshot (rewind, bug-bundle reconstruction, client playback). That was issue #309.

Three pieces hold the resume together:

- **`ExitWaypointIndex` round-trips** and is threaded into the rebuild. Segment indexing is stable across the rebuild because the route shape is a pure function of `_exitPath`: `[virtual approach, path[0]→path[1], …, hold-short → virtual-past]`, so segment *k* ≥ 1 is always `path[k-1] → path[k]`. `ToSnapshot` falls back to the restored index rather than 0 when no route exists yet, so a round-trip before the first tick doesn't lose the cursor again.
- **`ResumeSegmentIndexAfterRestore` floors the index at 1 when the aircraft is past the branch** (along-track, on the runway heading). A snapshot can land on the tick before the navigator signals arrival, so the stored index alone still reads 0 while the aircraft is physically on the exit taxiway.
- **Segment 0 is re-anchored when resuming past it** — on the centerline `RestoredApproachSegmentNm` *behind* the branch instead of at the aircraft. The navigator reads that leg's arrival bearing as the corner's incoming tangent (and its length feeds the adaptive rounding radius and the short-connector check), so it has to still be the runway heading.

The live first-commit and re-target paths are untouched: both pass a resume index of 0 and get exactly the route they always did. `GroundNavigator` is separately non-round-tripping — it stores no arc progress, so a restore mid-fillet replays that arc from its start and the reconstruction trails the live track by a second or two on the same path.

### Why the virtual segment matters

GroundNavigator computes turn arcs based on the angle between consecutive segments. Without the virtual segment, the navigator has no inbound context — it doesn't know the aircraft was approaching from the runway. The virtual segment provides this context naturally, and a longer segment (more distance before the branch) produces better turn anticipation. This is why LandingPhase should hand off early, not at the branch point.

## GroundNavigator — Turn Execution

GroundNavigator handles the actual turn through the exit. It uses:

- **Backward-propagated braking**: walks future segments, collects speed constraints at each turn, and back-propagates braking limits. The aircraft never overspeeds into a future turn.
- **Turn anticipation**: for turns ≥20°, the arrival threshold expands so the aircraft begins turning before reaching the node, creating a smooth arc.
- **Heading-based speed scaling**: speed reduces proportionally to heading error (full speed at 0° error, 15% at ≥120°), modeling realistic ground steering constraints.

## Parallel-Runway Auto-Pull-Up (issue #175)

When an aircraft vacates **between two parallel runways** — e.g. lands OAK 28L, exits right onto G, and G crosses on to 28R ~500 ft away — `RunwayExitPhase.CompleteExit` can auto-advance it to hold short of the *parallel* runway instead of stopping at the landing runway's exit hold-short.

- **Trigger** (`AirportGroundLayout.FindParallelRunwayCrossing`): walk the same exit taxiway forward from the landing-runway hold-short. If the next runway hold-short reached belongs to a *different, (anti-)parallel* runway with **no intervening taxiway intersection** in between, return its near-side and far-side hold-shorts plus the node paths. An intervening intersection (a node carrying a different taxiway) aborts the search — the controller may want to route the aircraft down that taxiway, so the aircraft stops at the landing exit (today's behavior). Forward direction comes from the exit path's tail node, so an outer-side exit (pointing back across the landing runway) finds nothing.
- **Reuse, not new geometry**: `CompleteExit` synthesizes a normal `AssignedTaxiRoute` of **real nodes** `[landing-HS → parallel-near-HS → … → parallel-far-HS]`, annotates it with `HoldShortAnnotator.AddImplicitRunwayHoldShorts` + `ComputeHoldShortPositions` (which stops the nose *at* the parallel hold-short line, not past it), and hands off to a `TaxiingPhase`. The existing taxi machinery then drives the pull-up, stops short of the parallel, inserts the `HoldingShortPhase`, and — once a `CROSS` clears it — runs the `CrossingRunwayPhase` across and holds clear on the far side. No virtual nodes are added to the route (a negative `VirtualNode` id would drop the whole route on snapshot restore).
- **Gating**: the `AutoPullUpToParallel` scenario setting (opt-out, on by default for live sessions via the client preference; off in `SimScenarioState` so pre-feature recordings replay faithfully). Independent of `AutoCrossRunway` — the pull-up **always** requires an explicit `CROSS`/`CROSS <rwy>`; it never auto-crosses the parallel.
- **Solo phraseology**: the synthesized pull-up hold-short carries `HoldShortReason.RunwayCrossing`, so it never triggers the solo-mode "ready for departure" report — that report fires only for `HoldShortReason.DestinationRunway` (the aircraft's assigned departure runway). A landed aircraft crossing a parallel is not a departure; it still surfaces a controller-facing "holding short runway <parallel>" reminder.

## Constants

| Constant | Jet | Turboprop | Piston | Helicopter |
|----------|-----|-----------|--------|------------|
| Coast speed (kts) | 40 | 35 | 25 | 15 |
| Default rollout decel (kts/s) | 2.5 | 2.0 | 2.5 | 0 |
| High-speed exit turn-off (kts) | 30 | 25 | 18 | 15 |
| Standard exit turn-off (kts) | 15 | 15 | 12 | 10 |
| Ground turn rate ceiling (deg/s) | 12 | 16 | 20 | 30 |
| Taxi corner speed (kts) | 15 | 15 | 10 | 10 |

`Ground turn rate` is now a **ceiling**, not a flat rate: achievable ground yaw is `ω = v/R` at the tight nose-wheel radius (`CategoryPerformance.GroundYawRateAtSpeed`), capped at the ceiling above the ~3 kt crossover. A taxiing aircraft can only slew its nose as fast as it rolls forward, so a near-stationary aircraft no longer pivots at the full rate (a 120° turn takes ~6 s piston / ~10 s jet, not ~2 s). Helicopters are exempt (a wheeled pedal-turn holds the hover rate). Tight fillets are additionally held at their curvature speed via `GroundArc.MaxSafeSpeedKts` (which folds in the same `ω·r` yaw-rate cap), so a jet no longer accelerates through a sharp ramp fillet at 20 kt / 0.84 g.

| Constant | Value | Purpose |
|----------|-------|---------|
| Firm braking limit | 5.0 kts/s | Max decel for explicit exit commands |
| Expedite braking rate | jet 7.5 / TP 6.0 / piston 5.0 / helo 4.0 kts/s | Max-effort decel for `EXP` ("without delay") exits |
| Comfortable multiplier | 1.5x | Default exit: 1.5x rollout decel |
| Min soft braking | 0.5 kts/s | Floor for gentle decel on far exits |
| Turn-off tolerance | 3.0 kts | Discrete-tick overshoot margin |
| High-speed exit threshold | 45° | Exits ≤45° use high-speed turn-off |
| High-speed exit bonus | 0.15 nm | Scoring bonus for ≤45° exits |
| Standard exit min distance | 0.02 nm | Min distance to branch for standard exit handoff |

## Exit Angle Classification

- **High-speed exits** (≤45°): shallow turns, higher turn-off speed (30 kts for jets). Tolerate shorter virtual segments since the turn angle is small.
- **Standard exits** (>45°): steep turns, lower turn-off speed (15 kts for jets). Need more distance for the turn arc. Rejected if the aircraft is at/past the branch point — the virtual segment would be too short.

## Final Approach Course (FAC) Alignment

The aircraft flies the published final approach course down to minimums, then transitions visually onto the runway centerline — heading and lateral position together, with no snap at the threshold.

- **FAC derivation.** `FinalApproachCourseExtractor` derives the published FAC from the CIFP missed-approach (MAP) leg rather than hardcoding runway heading. CF/FA legs use `OutboundCourse`; TF/DF legs use the great-circle bearing to the MAP fix; RF and anything else fall back to runway heading. This is what makes offset approaches (LDA, RNAV-with-offset CF leg, VOR offset) track their published FAC instead of the centerline.
- **Magnetic-to-true conversion uses the CIFP variation of record, not live WMM.** CF/FA leg `OutboundCourse` is magnetic and converts to true via the airport's CIFP-published variation (`NavigationDatabase.GetAirportMagneticVariation`, parsed from the CIFP airport PA record cols 51-55), not `MagneticDeclination.GetDeclination` (current WMM) — the two drift apart across AIRAC epochs (~1-2°), which pushes an aligned ILS localizer off centerline. `FinalApproachCourseExtractor.Extract` uses `navDb.GetAirportMagneticVariation(runway.AirportId) ?? MagneticDeclination.GetDeclination(...)`, falling to WMM only when CIFP has no record (issue #187: KIAH I26R 267° mag — WMM +1.7° gives 268.7° true vs runway 269.95°; CIFP variation +3.0° gives the correct 270.0°). Only the CF/FA path is affected; TF/DF is geometric. Do not "snap" the FAC to runway heading on a tolerance to paper over this — a real offset ILS would be wrongly flattened; fix the conversion instead.
- **Visual alignment ramp.** `FinalApproachPhase.OnTick` lerps both the lateral cross-track course and the lateral anchor from FAC/anchor toward runway-heading/threshold via a smoothstep, so the aim-point bearing rotates onto the centerline over the last ~200 ft AGL. The ramp is a no-op below `FacRampMinOffsetDeg` (0.5°); small CIFP/mag-var divergences (0.5°–5°) ramp over ~300→100 ft AGL, genuine offsets (≥5°) over ~1000→500 ft AGL. There is no separate heading snap — heading and position converge together.

## Short-Approach Base & Landing Geometry

The `SA` (Make Short Approach) compressed pattern has two coupled geometry invariants that must hold together:

- **Base-leg descent targets the rollout point.** `BasePhase`'s SA branch targets glideslope altitude at `(finalDist + turnRadius)`, not at `finalDist` — the 90° base→final turn translates the aircraft one turn radius further along the final, so targeting at `finalDist` would put it at GS-intercept altitude before the turn fires and trip the landing stabilization gate.
- **LandingPhase floats over the runway while rolling out.** When heading-error from runway exceeds 5° (`_floatingForRollout`), LandingPhase holds level (target altitude = current, vertical rate = 0) until wings level, then resumes descent → flare → touchdown. This lets a tight turn complete before the bank-stabilization gate engages. The descent target is restored on the first non-rolling-out tick.

## Key Files

- `src/Yaat.Sim/Phases/Tower/LandingPhase.cs` — Rollout braking, exit candidate resolution, unable-replan
- `src/Yaat.Sim/Phases/Ground/RunwayExitPhase.cs` — Analog rolling, virtual segments, exit search
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

**Do not loosen the unable/degenerate guards.** Standard exits at the branch point are always rejected. This prevents degenerate near-zero virtual segments that cause heading reversals. Fix the braking planning instead.

**Brake toward the exit's turn-off speed when it is below coast.** LandingPhase targets `min(coastSpeed, candidateExit.TurnOffSpeed)` so a slow piston can actually take a 12-kt standard exit — the missed-exit check at `distToBranch≤0` fires unconditionally for standard exits, so the aircraft has to be at or below turn-off speed *before* the branch, not at coast. RunwayExitPhase still owns braking through the turn; LandingPhase just stops stranding slow aircraft above a reachable exit's turn-off.
