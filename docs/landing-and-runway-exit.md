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

- Compute distance to the exit
- Subtract a braking buffer: the distance RunwayExitPhase needs to brake from coast speed to the exit's turn-off speed (using the default decel rate)
- Plan decel to reach coast speed at that buffer point — not at the exit itself
- If the exit is far enough that normal braking would reach coast too early, use a gentler rate (floored at 0.5 kts/s) to avoid a long pointless coast
- If the exit requires more than firm braking (5 kts/s), broadcast "unable" and replan

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

### Why the virtual segment matters

GroundNavigator computes turn arcs based on the angle between consecutive segments. Without the virtual segment, the navigator has no inbound context — it doesn't know the aircraft was approaching from the runway. The virtual segment provides this context naturally, and a longer segment (more distance before the branch) produces better turn anticipation. This is why LandingPhase should hand off early, not at the branch point.

## GroundNavigator — Turn Execution

GroundNavigator handles the actual turn through the exit. It uses:

- **Backward-propagated braking**: walks future segments, collects speed constraints at each turn, and back-propagates braking limits. The aircraft never overspeeds into a future turn.
- **Turn anticipation**: for turns ≥20°, the arrival threshold expands so the aircraft begins turning before reaching the node, creating a smooth arc.
- **Heading-based speed scaling**: speed reduces proportionally to heading error (full speed at 0° error, 15% at ≥120°), modeling realistic ground steering constraints.

## Constants

| Constant | Jet | Turboprop | Piston | Helicopter |
|----------|-----|-----------|--------|------------|
| Coast speed (kts) | 40 | 35 | 25 | 15 |
| Default rollout decel (kts/s) | 2.5 | 2.0 | 2.5 | 0 |
| High-speed exit turn-off (kts) | 30 | 25 | 18 | 15 |
| Standard exit turn-off (kts) | 15 | 15 | 12 | 10 |
| Ground turn rate (deg/s) | 20 | 25 | 35 | 30 |
| Taxi corner speed (kts) | 15 | 15 | 20 | 10 |

| Constant | Value | Purpose |
|----------|-------|---------|
| Firm braking limit | 5.0 kts/s | Max decel for explicit exit commands |
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
