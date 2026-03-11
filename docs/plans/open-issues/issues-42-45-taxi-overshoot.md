# Issues #42-45: Taxi Overshoot and Hold-Short Encroachment

## Background

All four issues appeared after commit `2fe0dff` ("feat: increase taxi speeds — jets 30kts straight,
proportional for others"), which raised jet `TaxiSpeed` from 18 → 30 kts, turboprop from 15 → 25 kts,
piston from 12 → 20 kts, and `TaxiAccelRate` from 2 → 3 kts/s. `TaxiDecelRate` was not changed and
remains at 3 kts/s.

- **#42**: Aircraft take wide turns and go off taxiways (S→T, T→U, U→W at OAK).
- **#43**: Aircraft taxied past hold-short node for 28R @ B, encroached on runway.
- **#44**: Aircraft held short east of B (slightly past hold bar), still encroached on 28R.
- **#45**: Aircraft encroached on runway 30 after hold-short command on W1.

---

## Root Cause Analysis

### Failure Mode A — Wide Turns (#42)

`TaxiingPhase.OnTick` lines 109-113 scale speed with heading error relative to the *current*
bearing-to-target node:

```csharp
double angleDiff = Math.Abs(FlightPhysics.NormalizeAngle(bearing - ctx.Aircraft.Heading));
double speedFraction = Math.Clamp(1.0 - (angleDiff / 120.0), 0.15, 1.0);
double targetSpeed = maxSpeed * speedFraction;
```

The problem: when an aircraft is on a straight leg approaching a 90° turn node, `angleDiff` is nearly
zero (the aircraft is pointed straight at the node) right up until it arrives. Speed does not slow for
the upcoming corner until it is already at the node. At 30 kts the aircraft physically cannot execute
a tight turn without departing the taxiway centerline.

Additionally, `maxSpeedForDist = (dist * 0.8) / dt * 3600` (line 119) prevents a single-tick
overshoot but does not slow the aircraft far enough in advance of a turn to prevent the geometric
wide-turn excursion.

### Failure Mode B — Hold-Short Node Overshoot (#43, #45)

Stopping distance from 30 kts at 3 kts/s deceleration:

```
d = v² / (2a)  [converting to nm/s: v = 30/3600 nm/s, a = 3/3600 nm/s²]
d = (30/3600)² / (2 * 3/3600) = 0.00833² / 0.001667 ≈ 0.0417 nm
```

`NodeArrivalThresholdNm` is 0.015 nm. The deceleration ramp therefore needs to begin at ~0.042 nm
from the hold-short node, which is nearly 3× beyond the arrival threshold. The existing
`maxSpeedForDist` cap only prevents a single-tick slip; it does not start braking early enough to
stop within 0.015 nm.

The phase and the physics system also interact in a way that delays deceleration by one tick:
`TaxiingPhase.AdjustSpeed` writes `aircraft.GroundSpeed`, but `FlightPhysics.UpdateSpeed` (called
later in the same tick) re-derives `GroundSpeed` from `IndicatedAirspeed`. Unless `TargetSpeed` is
also set, the intent to decelerate is not visible to `FlightPhysics` until the next tick.

### Failure Mode C — Stopped East of Hold Bar (#44)

When the overshoot detection branch fires (`dist > _prevDistToTarget && _prevDistToTarget <
OvershootDetectionNm`), the aircraft's position is already past the hold-short node. `ArriveAtNode`
inserts `HoldingShortPhase` and sets speed to 0, but does not snap the aircraft back to the node
coordinates. The aircraft ends up stopped 0.01-0.02 nm past the hold bar — visually inside the
runway environment.

---

## Required Code Changes

### 1. Raise `TaxiDecelRate` — `src/Yaat.Sim/AircraftCategory.cs` line 439

Raise from 3 kts/s to 5 kts/s. This reduces stopping distance from 30 kts to ~0.025 nm, making
look-ahead braking viable with a reasonable margin. Consider separating `TaxiBrakeRate` (commanded
full-stop) from `TaxiDecelRate` (gradual slowdown) if further tuning is needed.

This is the lowest-risk change; do it first.

### 2. Look-ahead braking in `TaxiingPhase.OnTick` — lines 109-123

Replace the current single-tick `maxSpeedForDist` cap with a multi-tick braking ramp that begins
decelerating when the aircraft is within `stoppingDistNm + safetyMargin` of the target node.

Key additions to `OnTick`:

- Compute `stoppingDistNm = gs² / (2 * decelRate * 3600)` each tick.
- Determine whether the current target node is a hold-short (`route.GetHoldShortAt(_targetNodeId) is not null`). If yes, `requiredArrivalSpeed = 0`. If no (plain intersection), `requiredArrivalSpeed = cornerSpeed` derived from upcoming turn angle.
- When `dist <= stoppingDistNm * 1.5` (1.5× safety factor), override `targetSpeed` with the minimum of the current computed value and a linear ramp down to `requiredArrivalSpeed`.
- Set `ctx.Targets.TargetSpeed = targetSpeed` in addition to calling `AdjustSpeed`, so `FlightPhysics.UpdateSpeed` sees the intent in the same tick.
- Keep the existing `maxSpeedForDist` cap as a safety backstop.

Corner speed for turn nodes: use the upcoming turn angle (bearing of next segment minus bearing of
current segment) to compute an approach speed that keeps the angular error manageable at
`GroundTurnRate`. Simple approximation: `cornerSpeedKts = GroundTurnRate * turnRadiusNm * 2π /
360 * 3600` where `turnRadiusNm ≈ 0.003 nm` (tight 90° taxiway intersection).

### 3. Position snap to hold-short node — `TaxiingPhase.ArriveAtNode` lines 233-253

After confirming a hold-short exists and is not cleared, snap aircraft coordinates to the node's
exact position before inserting `HoldingShortPhase`:

```csharp
if (ctx.GroundLayout is not null && ctx.GroundLayout.Nodes.TryGetValue(_targetNodeId, out var hsNode))
{
    ctx.Aircraft.Latitude = hsNode.Latitude;
    ctx.Aircraft.Longitude = hsNode.Longitude;
}
```

This fixes both the "stopped east of B" case (#44) and any residual overshoot in #43/#45 where
braking wasn't quite sufficient.

Also apply the same snap in the overshoot detection branch of `OnTick` (before `ArriveAtNode` is
called) when `overshot == true` and the node is a hold-short.

### 4. Upcoming turn angle for speed reduction — `TaxiingPhase.OnTick`

Replace the current heading-error-based `speedFraction` with a look-ahead that reads the angle
between the current segment's bearing and the next segment's bearing. Store the next segment's
bearing in `SetupCurrentSegment` and use it in `OnTick`:

```
// Current leg bearing (from/to node coordinates)
// Next leg bearing (if next segment exists)
double upcomingTurnAngle = Math.Abs(NormalizeAngle(nextBearing - currentBearing));
double cornerFraction = Math.Clamp(1.0 - upcomingTurnAngle / 120.0, 0.15, 1.0);
double cornerSpeed = maxSpeed * cornerFraction;
// Start decelerating stoppingDistNm before the node
if (dist <= stoppingDistNm * 1.5)
    targetSpeed = Math.Min(targetSpeed, cornerSpeed);
```

For the last segment (no next segment), this degenerates gracefully to full speed (which is correct:
the aircraft will stop when the route completes).

---

## New Tests

All tests go in `tests/Yaat.Sim.Tests/GroundPhaseTests.cs`.

### `TaxiingPhase_BrakingLookAhead_SlowsBeforeHoldShort`
- Two-node route, node 1 is a hold-short, placed 0.05 nm north of node 0.
- Aircraft starts at node 0 at 0 speed. Tick for ~40 s.
- Assert: when dist to node 1 drops below 0.03 nm, `aircraft.GroundSpeed < 5 kts`.
- Assert: aircraft does not pass node 1's latitude.
- Assert: `HoldingShortPhase` is inserted.

### `TaxiingPhase_StopsExactlyAtHoldShortNode`
- Same setup. After hold-short phase is inserted, assert
  `|aircraft.Latitude - node1.Latitude| < 0.00005` (≈ 0.003 nm tolerance).

### `TaxiingPhase_NoBrakingOvershootAt30Kts`
- Place aircraft 0.02 nm before a hold-short node at 25 kts.
- Run 5 ticks (1 s each).
- Assert: aircraft never passes the node's latitude (using `GeoMath.DistanceNm` sign logic).
- Assert: speed reaches 0 at or before node.

### `TaxiingPhase_PositionSnappedToHoldShortOnArrival`
- Aircraft placed at exactly `NodeArrivalThresholdNm - 0.001 nm` from hold-short node.
- Run 1 tick.
- Assert `aircraft.Latitude == node.Latitude` within 0.00002 deg.

### `TaxiingPhase_OvershotHoldShort_PositionCorrected`
- Manually set `_prevDistToTarget` (via reflection or by constructing a scenario where two ticks
  result in the overshoot condition: dist tick N-1 < OvershootDetectionNm, dist tick N > dist tick N-1).
- Assert: after overshoot detection fires, aircraft position is at node coordinates, not past them.
- Assert: `HoldingShortPhase` inserted.

### `TaxiingPhase_TurnSlowdown_ReducesSpeedBeforeCorner`
- Three-node route: node 0 → node 1 → node 2. Node 1 is at 0.05 nm north, node 2 is at 0.05 nm
  east of node 1 (90° turn).
- Aircraft starts at node 0. Tick for ~60 s.
- Assert: when `dist_to_node1 < 0.035 nm`, `aircraft.GroundSpeed < maxSpeed * 0.6` (speed is
  already ramping down for the corner).
- Assert: aircraft does not depart laterally more than 0.005 nm from the direct node-to-node path.

### `TaxiDecelRate_ProducesAcceptableStoppingDistance`
- Unit assertion: `stoppingDist = TaxiSpeed(Jet)² / (2 * TaxiDecelRate(Jet) * 3600) < 0.030 nm`.
- Documents the contract that stopping distance at full speed must be less than `NodeArrivalThresholdNm * 2` (0.030 nm) — a level where look-ahead braking at 1.5× margin comfortably covers the stop.

---

## Extending `SimulationEngineReplayTests`

The existing OAK replay tests load `oak-taxi-recording.json`, which issues `TAXI S T U W W1 HS 30`
for NKS2904 — exactly the S→T→U→W path from #42 and the runway 30 hold-short from #45. These tests
should be extended with position assertions.

Add to `tests/Yaat.Sim.Tests/Simulation/SimulationEngineReplayTests.cs`:

### `Replay_OakTaxi_NKS2904_DoesNotOvershootAnyNode`
- Replay recording.
- Assert: for each segment that was traversed, the aircraft's final position is within 0.020 nm of
  the segment's `ToNodeId` coordinates. (Use the engine's post-replay state and walk the completed
  segments.)

### `Replay_OakTaxi_NKS2904_HoldsShortOf28R`
- Replay recording.
- Locate the hold-short node for 28R/B in the OAK layout (query via `groundData.GetLayout("OAK")`
  and find nodes with `RunwayId` matching 28R near taxiway B).
- Assert: `nks.Latitude` does not cross the hold-short node's latitude toward runway 28R.

### `Replay_OakTaxi_NKS2904_HoldsShortOf30`
- Same, for runway 30's hold-short node on W1.
- Assert: aircraft position is not past the hold-short node into the runway 30 environment.

Gate all three with the existing `if (recording is null || engine is null) return;` pattern.

---

## Steering Angle / GroundTurnRate

Aviation-sim-expert review confirmed `GroundTurnRate` should also increase. At the old 15 kts speed,
15 deg/s implied a 29 m turn radius — already wider than a real B737 outer main gear sweep (21.7 m).
At 30 kts straight / 15 kts corner, the correct values are:

| Category | Old | New | Physics basis (at 15 kts corner) |
|---|---|---|---|
| Jet | 15 deg/s | **20 deg/s** | 22 m radius = B737/A320 outer gear (Boeing D6-58325: 71.2 ft) |
| Turboprop | 20 deg/s | **25 deg/s** | 18 m radius = ATR-72/CRJ-700 |
| Piston | 20 deg/s | **35 deg/s** | 13 m radius = C172 |
| Helicopter | 30 deg/s | 30 deg/s | Unchanged |

Expert also recommended keeping the **fixed deg/s model** (not switching to fixed minimum radius),
because look-ahead braking already pins corner speed to ~15 kts, and a fixed-radius arc model
would require substantially more complex path-following code.

A new `TaxiCornerSpeed` method (jet/turboprop 15 kts, piston 20 kts, helicopter 10 kts) provides
the target arrival speed for turn nodes.

## Implementation Status

All changes implemented. Build: 0W 0E. Tests: 1161 Sim + 209 Client, all passing.

### Files changed
- `src/Yaat.Sim/AircraftCategory.cs` — raised `TaxiDecelRate` 3→5 kts/s; raised `GroundTurnRate`
  per table above; added `TaxiCornerSpeed()`
- `src/Yaat.Sim/Phases/Ground/TaxiingPhase.cs` — added `_nextSegmentBearing` field; updated
  `SetupCurrentSegment` to look one segment ahead; replaced `maxSpeedForDist`-only cap with
  physics-correct braking ramp in `OnTick`; added hold-short position snap in `ArriveAtNode`

### Remaining
- Unit tests (GroundPhaseTests) and replay position assertions (SimulationEngineReplayTests) from
  the plan above have not been added yet — the behavior fixes land first.

## Anticipated Challenges

- **Phase/physics decel conflict**: `TaxiingPhase.AdjustSpeed` writes `aircraft.GroundSpeed`
  directly, but `FlightPhysics.UpdateSpeed` re-derives it from `IndicatedAirspeed` later in the same
  tick. Always set `ctx.Targets.TargetSpeed` alongside direct `GroundSpeed` writes so the intent
  survives the full tick.

- **`maxSpeedForDist` retention**: Keep the existing single-tick cap as a safety backstop. The new
  look-ahead braking supersedes it at longer range but does not replace it.

- **Segment bearing for look-ahead turns**: Computing next-segment bearing requires resolving both
  the current and next nodes from `ctx.GroundLayout`. Both should be available since `SetupCurrentSegment`
  already resolves the target node. Store `_nextSegmentBearing` as a field and update it in
  `SetupCurrentSegment` by looking at `route.Segments[CurrentSegmentIndex + 1]` if it exists.
