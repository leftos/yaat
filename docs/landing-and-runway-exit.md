# Landing Rollout & Runway Exit — Design & Implementation

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

LandingPhase's job is to decelerate the aircraft to coast speed. That's it. It does **not** try to deliver the aircraft to a specific speed at a specific point. RunwayExitPhase and GroundNavigator handle precision braking for the turn.

### Default exits (no explicit preference)

The pilot picks the first comfortable exit. "Comfortable" means achievable at 1.5x the default rollout decel rate — not the first exit that requires maximum effort.

- Decelerate at the default rollout rate
- Only brake harder when kinematically required (the exit is close enough that default braking won't suffice)
- Target speed is coast speed unless braking below coast is kinematically needed

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
| Default rollout decel (kts/s) | 2.5 | 2.0 | 1.5 | 0 |
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

## Key Files

- `src/Yaat.Sim/Phases/Tower/LandingPhase.cs` — Rollout braking, exit candidate resolution, unable-replan
- `src/Yaat.Sim/Phases/Ground/RunwayExitPhase.cs` — Analog rolling, virtual segments, exit search
- `src/Yaat.Sim/Phases/Ground/GroundNavigator.cs` — Steering, turn anticipation, backward-propagated braking
- `src/Yaat.Sim/Data/Airport/AirportGroundLayout.cs` — FindExitFromCenterline, FindAdjacentHoldShort, InferPreferredExitSide, exit scoring
- `src/Yaat.Sim/AircraftCategory.cs` — All category-specific performance constants

## Anti-Patterns to Avoid

**Do not try to deliver the aircraft to a specific speed at a specific node.** This is the single most important rule. LandingPhase delivers to coast speed, RunwayExitPhase finds the exit and builds the virtual segment, GroundNavigator handles precision braking. Each phase has one job.

**Do not walk centerline nodes during rollout.** The runway is a continuous surface. Use `GeoMath.AlongTrackDistanceNm` to measure distances, not node-to-node traversal.

**Do not shorten the virtual segment.** A longer virtual segment is always better — it gives GroundNavigator more context for turn anticipation. Handing off the aircraft far from the exit (at coast speed with room to brake) is correct behavior.

**Do not loosen the unable/degenerate guards.** Standard exits at the branch point are always rejected. This prevents degenerate near-zero virtual segments that cause heading reversals. Fix the braking planning instead.

**Do not brake below coast speed in LandingPhase for explicit exits.** That's RunwayExitPhase's job. LandingPhase plans the decel to coast, accounts for the braking buffer, and hands off.
