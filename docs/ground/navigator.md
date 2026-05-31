# Ground Navigator — Route-Following Design & Implementation

> Read this before touching `src/Yaat.Sim/Phases/Ground/GroundNavigator.cs`, `PathPrimitive.cs`, `PathPrimitiveBuilder.cs`, or the route-following parts of `TaxiingPhase.cs` / `RunwayExitPhase.cs` / `CrossingRunwayPhase.cs`. The navigator is the per-tick controller that physically steers an aircraft along an already-resolved taxi route. It does not build routes (that is the [pathfinder](./pathfinder.md)) and it does not build arc geometry (that is the [fillet generator](./fillet-generator.md)).

## Status banner — V2 is the ground navigator

The ground stack is three layers (see `docs/plans/ground-graph-v2.md`). As of the Phase-7 joint flip all three layers are **V2-only**; the V1/Legacy implementations and their selector seams are deleted:

| Layer | Role | Status |
|---|---|---|
| Fillet generator | builds arc geometry (nodes, edges, arcs, radii) | **V2 only** — Legacy generator + `FilletArcGeneratorRouter` deleted |
| Pathfinder | resolves a `TaxiRoute` over the graph | **V2 only** — V1 `TaxiPathfinder` + `ITaxiPathfinder` / router deleted |
| **Navigator (this doc)** | **follows** the route + geometry per tick | **V2 only** — V1 `GroundNavigator` deleted |

The navigator is the clean-room `GroundNavigator` (`src/Yaat.Sim/Phases/Ground/GroundNavigator.cs`). The old V1 `GroundNavigator`, the `IGroundNavigator` / `GroundNavigatorRouter` selector seam, and the Legacy-fillet compensations they carried — slow-turn synthesis planner, short-chord cluster detection, chord-chain aggregate-turn, the orbit-stall tick counter — are **deleted**. V2 was built for clean V2 fillet geometry (tighter single arcs, fewer tangent nodes) and plays fillet arcs as their actual cubic Bézier rather than an inscribed circle. Its replacements for the dropped compensations are documented in their own `(V2)` sections below (advance-on-pass + the hard orbit invariant; the per-corner turn-rate-feasibility cap; entry-alignment rounding at any corner past the threshold).

> **Doc body-prose refresh pending.** The cross-layer rename has landed (the three layers are now `GroundNavigator` / `TaxiPathfinder` / `FilletArcGenerator`, no `V2` suffix). The detailed sections below still carry `GroundNavigator.cs:NNNN` line references and `[Legacy-compensation — re-evaluate on V2]` markers from the V1 era; the mechanisms they describe as "V1" are now deleted and those re-evaluation questions are resolved (the `(V2)` addenda are the current behavior). A prose cleanup of those stale line-refs/markers remains.

---

## Where it sits

### In the phase / tick system

The navigator is **not** a phase. It is a plain `sealed class GroundNavigator` owned by a ground phase as a field. The phases that own one:

| Phase | File | What the navigator follows |
|---|---|---|
| `TaxiingPhase` | `Phases/Ground/TaxiingPhase.cs:26` (`_nav`) | the full `AssignedTaxiRoute` from the pathfinder |
| `RunwayExitPhase` | `Phases/Ground/RunwayExitPhase.cs:53` (`_navigator`) | a short `_exitRoute` (virtual segment → branch → hold-short) |
| `CrossingRunwayPhase` | `Phases/Ground/CrossingRunwayPhase.cs:237` | a `_crossingRoute` across the runway to the exit node |

Phases run **before** physics each sub-tick (see [`../tick-loop.md`](../tick-loop.md) and [`../phases.md`](../phases.md)). The owning phase's `OnTick` calls `_nav.Tick(...)`; the navigator writes `ctx.Targets.TargetSpeed` / `TargetTrueHeading` (and, on arcs, writes `ctx.Aircraft.Position` / `TrueHeading` **directly** — see invariant I2 below) before `FlightPhysics.Update` reads them. Sub-tick delta is `ctx.DeltaSeconds` (≈ 0.25 s, four sub-ticks per sim-second).

`GroundConflictDetector.ApplySpeedLimits` runs **between** the phase tick and physics, writing `ctx.Aircraft.Ground.SpeedLimit`. The navigator honors that cap via `ClampBySpeedLimit` (`GroundNavigator.cs:1639`) and `AdjustSpeed` (`GroundNavigator.cs:1647`) so it never overruns a conflict-imposed limit.

### Inputs and outputs

**Consumes:**
- A `TaxiRoute` (`Data/Airport/TaxiRoute.cs`) — an ordered `List<TaxiRouteSegment>` plus `HoldShortPoints`, with a mutable `CurrentSegmentIndex`. Each segment wraps a `DirectionalEdge` over either a straight `GroundEdge` or a `GroundArc` fillet.
- Per-segment geometry, compiled into a `PathPrimitive` by `PathPrimitiveBuilder.FromSegment` (`PathPrimitiveBuilder.cs:44`): `PathPrimitiveStraight` for straight edges, `PathPrimitiveArc` for `GroundArc` fillets (reinterpreted as a true circle, see below).
- `PhaseContext` — aircraft state, `DeltaSeconds`, `Category`, ground-layout-derived data.
- An `isHoldShortCleared(nodeId)` delegate supplied by the owning phase, so the speed profile knows which hold-shorts are stops vs cleared transits.

**Writes:**
- `ctx.Targets.TargetSpeed` (always) and `ctx.Targets.TargetTrueHeading` (on arcs).
- On straights: turns `ctx.Aircraft.TrueHeading` toward the steer bearing, bounded by turn rate, and lets physics advance position.
- On arcs/slow-turns: writes `ctx.Aircraft.Position` and `ctx.Aircraft.TrueHeading` **directly** from closed-form arc state.
- `ctx.Aircraft.Ground.LastNavDiag` — a `NavTickDiag` per-tick record (`GroundNavigator.cs:26`) consumed by `TickRecorder` CSV traces and `Yaat.LayoutInspector --tick-table`.

**Returns** a `NavigatorResult` (`GroundNavigator.cs:12`): `Navigating` (still moving toward the target) or `ArrivedAtNode` (the owning phase should advance `CurrentSegmentIndex`).

### Relationship to sibling ground phases

The navigator is one stage in a chain owned by `TaxiingPhase`. It is **not** responsible for hold-short insertion, runway crossing, departure clearance, parking, or phase handoff — `TaxiingPhase` does all of that around the navigator (see `ArriveAtNode` / `BuildResumePhases` in `TaxiingPhase.cs:221`). On arrival at a node:

- An uncleared hold-short → `TaxiingPhase` inserts `HoldingShortPhase` + resume phases; resuming a runway-crossing hold-short builds a `CrossingRunwayPhase` (`BuildResumePhases`).
- A **pre-cleared** runway crossing (the hold-short was cleared by an early `CROSS` / auto-cross before arrival) → `CrossingRunwayPhase` straight from the moving `TaxiingPhase`, no stop (`BuildPreClearedCrossingPhases`). Gated to a genuine forward crossing (near-side hold-short with a matching far-side hold-short of the same runway ahead, via `FindRunwayCrossingExitNode(requireSameRunwayExit: true)`); the far-side hold-short of a runway already vacated (landing-rollout exit) stays in `TaxiingPhase`. `TaxiingPhase.OnEnd` skips its stop-braking when handing off to a moving crossing.
- `CrossingRunwayPhase` owns its own navigator over a crossing-route slice. It crosses at **taxi speed** with `RunwayCrossingSpeed` as a no-stop floor (`MinSpeedKts`) — a crossing is just taxiing across (7110.65 §3-7-2 "cross without delay"); any curve in the painted line is still slowed by the navigator's arc-speed cap.
- Route end with a parking destination → `AtParkingPhase`; otherwise `HoldingInPositionPhase`.
- A pending departure clearance at route end → `LineUpPhase` / `LinedUpAndWaitingPhase` / `TakeoffPhase` chain.

The landing/exit side is documented separately — see [`../landing-and-runway-exit.md`](../landing-and-runway-exit.md). `LandingPhase` and `RunwayExitPhase` deliberately do **not** node-walk the runway; `RunwayExitPhase` builds a virtual inbound segment and hands a short route to a fresh `GroundNavigator` for the turn off the runway. `LineUpPhase` is a special case: it consumes `PathPrimitiveSlowTurn` **geometry** but plays it back with its own `LineUpArcPlayback` integrator (`Phases/Tower/LineUpPhase.cs`), not via `GroundNavigator` — see the gotcha on `SetupPrimitive` below.

---

## Key design decisions

### Analog playback over compiled primitives ("Design B")

The class summary (`GroundNavigator.cs:41`) calls this "Design B closed-form playback over `PathPrimitive`s". The aircraft does not snap to nodes. Each route segment compiles into exactly one immutable `PathPrimitive` (`PathPrimitive.cs`):

- **`PathPrimitiveStraight`** — a line from `From` to `To` at `BearingDeg`. Followed by pure-pursuit steering (below); physics advances position.
- **`PathPrimitiveArc`** *(V1 only)* — a `GroundArc` fillet reinterpreted as a *true circle* (centre, radius = `MinRadiusOfCurvatureFt`, start bearing-from-centre, sweep, turn direction). The Bezier is **not** sampled at runtime; `PathPrimitiveBuilder.BuildArc` (`PathPrimitiveBuilder.cs:78`) recovers circle parameters once at setup. **This is the playback the V2 navigator dropped** — see `PathPrimitiveBezier` below for why.
- **`PathPrimitiveBezier`** *(V2)* — the same `GroundArc` fillet played back as its **actual cubic Bézier** (the curve the renderer paints as the centerline), built by `PathPrimitiveBuilder.FromSegmentV2`/`BuildBezier`. `GroundNavigator.TickBezier` advances the curve parameter by arc-length each tick (`Δt = v·dt / |B'(t)|`, via `CubicBezier.DerivativeMagnitudeFt`) and writes position from `Evaluate(t)` and heading from `TangentBearing(t)` — still satisfying I2 (both are pure functions of the one scalar `_bezierT`). **Why V2 abandoned the circle:** `MinRadiusOfCurvatureFt` is the Bézier's *tightest* (apex) curvature, not the radius that connects its endpoints. For a wide sweeping fillet the apex is far tighter than the endpoint-connecting radius, so the inscribed circle ended well short of the corner's exit node (the OAK 28R→G corner: a 72 ft circle for endpoints 153 ft apart finished 56 ft short; a systemic scan found ~30–40 % of all OAK/SFO/FLL fillet traversals would undershoot >5 ft). Playing the real Bézier ends *exactly* on the to-node (its `P3`), so the next segment starts on-centerline instead of tripping the re-acquire speed gate into a crawl. The traversal-orientation (forward vs reversed) is baked into the stored curve at build time. Guarded by `GroundArcBezierPlaybackGuardTests` (every V2 arc on OAK/SFO/FLL ends within 2 ft of its node).
- **`PathPrimitiveSlowTurn`** — geometrically identical to an arc circle, but the radius is the aircraft's nose-wheel minimum and the speed cap is `SlowTurnSpeedKts` (≈ 3 kt). Used for synthesised tight turns (entry alignment, slow-turn synthesis) and tight programmatic pivots. Kept as a true-circle primitive in both V1 and V2 — it is synthesised, not derived from a painted `GroundArc`.

**Invariant I2 (the reason arcs are closed-form):** during an arc primitive, *both* position and heading are pure functions of a single scalar — the aircraft's compass bearing from the arc centre (`_arcBearingFromCenterDeg`). They advance together by `v·dt/r` each tick and therefore **cannot drift apart**. The class summary states this directly (`GroundNavigator.cs:51`): the feedback-saturation "knife-edge" that dogged the older Bezier-waypoint approach cannot occur here by construction. This is why `TickArc` / `TickSlowTurn` write `ctx.Aircraft.Position` and `TrueHeading` *directly* rather than steering toward a moving waypoint.

**Invariant I7 (no pivot-in-place):** arc and slow-turn ticks refuse to advance the integrator below `ArcSpeedFloorKts = 0.1` kt (`GroundNavigator.cs:82`, `1343`, `1429`). A stationary aircraft can't rotate on the spot; it must first roll forward. The navigator sets the speed target and bails, letting physics re-accelerate, then resumes the sweep.

### Pure-pursuit steering on straights

`TickStraight` (`GroundNavigator.cs:1081`) does **not** steer at the target node directly. It steers toward a look-ahead point projected forward along the *segment line* from the aircraft's foot-of-perpendicular (`GroundNavigator.cs:1242`). This makes convergence onto the line first-class: an aircraft that spawned slightly off a taxiway, or got nudged by a prior corner, re-acquires the line rather than cutting diagonally across terrain. Look-ahead distance is `2 × speed × dt` clamped to `[LookAheadFloorFt = 10, LookAheadCapFt = 50]` (`GroundNavigator.cs:90`, `97`, `1250`). The floor stops the look-ahead point collapsing onto the aircraft when nearly stationary; the cap stops it anticipating the next turn too aggressively on long straights.

A **pre-turn blend** (`GroundNavigator.cs:1271`) blends the steer bearing toward the next segment's departure bearing over the last ≈ 50 ft of a straight, scaled by turn angle — full blend at ≤ 30°, ramping linearly to zero by 90° (`1 - (turnAngle-30)/60`), so sharp turns get little or no blend (those are handled by synthesis or entry alignment instead).

### Entry-alignment threshold

When a new segment begins with the aircraft heading far off the segment's first tangent, `SetupSegment` (`GroundNavigator.cs:403`) builds a `PathPrimitiveSlowTurn` from the aircraft's current pose to the segment's start direction, stashes the real primitive in `_pendingSegmentPrimitive`, and plays the alignment arc first. The aircraft rolls forward at `SlowTurnSpeedKts` while rotating through real arc geometry — no in-place pivot, no heading snap (a first `TickArc` on a misaligned segment would otherwise write the arc tangent straight into `TrueHeading`).

Two gates, both required (`GroundNavigator.cs:436`):
1. **Heading delta > `EntryAlignmentThresholdDeg` = 45°** (`GroundNavigator.cs:311`). Normal fillet-smoothed corners stay below this by construction; only wrong-way starts, post-pushback U-turns, and mid-route corners where synthesis failed produce deltas this large.
2. **Segment long enough** to absorb the alignment chord plus a 1.2× pure-pursuit recovery margin (`GroundNavigator.cs:434`). Short segments can't fit the displacement; alignment is deferred and the (rare, brief) snap is accepted.

Entry alignment is the **safety net** for the pure-pursuit divergence at low speed: when the look-ahead point shifts faster than the aircraft can turn, it orbits. Any segment whose start delta exceeds the threshold gets a slow-turn regardless of route position.

**Adaptive rounding radius (V2).** The entry-alignment slow-turn and the incoming tangent-rounding both use an *adaptive* radius (`GroundNavigator.AdaptiveCornerRadiusFt`), not a fixed nose-wheel radius. When the approach or departure leg is shorter than the comfortable tangent length `T = r·tan(δ/2)` — two junctions closer than `T` apart, e.g. SFO M2 between the B and A crossings (~22 ft for a 118° turn that wants 41.6 ft) — the radius tightens toward a category **tight-turn floor** (`CategoryPerformance.TightTurnFloorRadiusFt`, 15 ft jet ≈ inner-main-gear radius) so the arc still **exits on the outgoing centerline**. The incoming arrival threshold (`StraightArrivalThresholdNm`) relaxes its `0.45·leg` cap to the whole leg only on such a tight leg, so the rounding can begin at the leg start. Without this, a fixed 25 ft arc off a 22 ft leg finishes ~26 ft wide, and pure-pursuit limit-cycles (orbits) the corner on the short outgoing segment for ~45 s. This is judgmental oversteer (Boeing FCTM / AC 150/5300-13B): the nose may bulge wide of centerline mid-arc but rolls out aligned. Aviation-reviewed.

### Speed: corner-speed limits and backward-propagated braking

The navigator never overspeeds into a future turn. `BuildSpeedConstraints` (`GroundNavigator.cs:1532`) runs at every `SetupSegment`:

1. Sets `_currentNodeRequiredSpeed` from `CornerSpeedForAngle(category, EffectiveTurnAngleAt(...))` (0 for stops — uncleared hold-shorts and the last segment).
2. Forward-walks remaining segments collecting `(pathDist, requiredSpeed, nodeId)` constraints: future-arc max-safe speeds (`GroundArc.MaxSafeSpeedKts(category)` — a lateral-acceleration cap `v = √(a_lat·r)`, `a_lat ≈ 0.13 g`, additionally capped by `CornerSpeedForAngle` and floored at `SlowTurnSpeedKts`), corner speeds at each future node, and 0 at the first uncleared hold-short (then stops).
3. Backward-propagates a kinematic decel curve (`v = sqrt(v_next² + 2·a·d)`) between adjacent constraints and into the current node's required speed.

`ComputeTargetSpeed` (`GroundNavigator.cs:1482`) per-tick takes the min of: the brake curve from the current node's required speed, every future constraint (skipping cleared hold-shorts), a pre-trigger constraint for a planned synthesis (so the aircraft reaches the chosen entry speed by the tangent point), and a quadratic heading-error scaling (`speedFraction`, full speed at 0° error down to 3 % at ≥ 90°). A safety backstop in `TickStraight` (`GroundNavigator.cs:1295`) caps target speed so the aircraft can't cover more than 80 % of remaining distance in one tick (overshoot prevention).

Corner speeds, turn rate, accel/decel, nose-wheel radius, and slow-turn speed are all category-specific in `AircraftCategory.cs` (`CornerSpeedForAngle`, `GroundTurnRate`, `TaxiAccelRate`, `TaxiDecelRate`, `NoseWheelTurnRadiusFt`, `SlowTurnSpeedKts = 3.0`). Taxi max speed is `TaxiSpeed(category)`, multiplied by `TaxiExpediteMultiplier = 1.3` when the aircraft is expediting (`TaxiingPhase.cs:78`). These are the realism constraints: turn rate bounds heading change per tick (`GroundNavigator.cs:1287`), and synthesis radius floors at the nose-wheel minimum so the navigator never asks for a physically impossible hairpin.

### Slow-turn synthesis — why it exists **[Legacy-compensation — re-evaluate on V2]**

When the pathfinder emits two consecutive straights at a sharp corner and the **natural** turn radius at corner speed produces an arc longer than half the post-corner segment (`SlowTurnSynthesisSegmentFraction = 0.5`, `GroundNavigator.cs:227`), the aircraft cannot complete the turn before running off the outgoing segment — it orbits the corner node. Synthesis pre-builds a tight `PathPrimitiveSlowTurn` (tangent-entry geometry: `r·tan(θ/2)` before the corner) that engages part-way through the incoming straight, tracing a proper fillet-style turn at a slow speed. This compensates for **corners the Legacy fillet generator did not round** (or rounded with chord chains). V2 fillets produce cleaner single arcs, so some synthesis cases should disappear — but the AMX669 freeze (below) shows V2's *tighter* arcs created a new failure mode, so this needs re-tuning, not just removal.

---

## Per-tick walkthrough

### `SetupSegment` (called on each segment transition)

`GroundNavigator.cs:382`. Runs once when the route advances to a new segment:

1. Compile the current segment to a primitive (`PathPrimitiveBuilder.FromSegment`).
2. Set target node / lat / lon from the segment's to-node; reset `PrevDistToTarget`, `TicksNearTarget`, `ExtraSegmentsToAdvance`.
3. **Entry-alignment check** (above). If the start heading delta exceeds the threshold and the segment is long enough, swap in an alignment slow-turn and stash the real primitive. Plan synthesis even here (the segment-end corner is independent of the segment-start realignment — `GroundNavigator.cs:451`).
4. Otherwise install the real primitive and call `PlanSynthesisLookahead`.
5. `BuildSpeedConstraints` (speed profile for this and all downstream segments).

`TaxiingPhase.SetupCurrentSegment` (`TaxiingPhase.cs:190`) then overrides the target lat/lon with a hold-short offset position when the to-node carries an uncleared hold-short (so the aircraft stops at the painted bar, not the intersection node).

### `Tick` — dispatch on primitive kind

`GroundNavigator.cs:1040` switches on the active primitive:

| Primitive | Handler | Behavior |
|---|---|---|
| `PathPrimitiveStraight` | `TickStraight` (`1081`) | synthesis-trigger check → arrival check → pure-pursuit steering → speed |
| `PathPrimitiveArc` | `TickArc` (`1338`) | closed-form circle advance; writes pos+hdg directly (I2) |
| `PathPrimitiveSlowTurn` | `TickSlowTurn` (`1423`) | same closed-form advance, capped at the primitive's `MaxSpeedKts` |
| (null) | — | returns `ArrivedAtNode` |

After the switch, if an alignment slow-turn just completed (`result == ArrivedAtNode && _pendingSegmentPrimitive != null`), the navigator swaps in the deferred real primitive and returns `Navigating` in the same tick — the route counter has not advanced (`GroundNavigator.cs:1053`).

### `TickStraight` step order

1. **Synthesis trigger** (`GroundNavigator.cs:1093`): if a `_plannedSynthesis` is active and distance-to-target drops below the tangent inset, do the **strict-geometry check** — the aircraft must be within `max(TangentEntryStrictToleranceFt = 5, 0.15 × radius)` of the planned tangent entry point. If yes, swap in the slow-turn, set `ExtraSegmentsToAdvance` for cluster synth, and retarget on cluster. If no, **skip synthesis** (log a warning) and let the straight finish via pure-pursuit.
2. **Arrival check** (`GroundNavigator.cs:1172`): arrives when `distNm ≤ arrivalThreshold`, or overshoot, or stalled-at-threshold, or orbit-stalled. Threshold is tight (`FinalNodeArrivalThresholdNm ≈ 1.8 ft`) on the last segment, stop targets, short edges, active synthesis, or when the next segment is an arc; otherwise loose (`NodeArrivalThresholdNm ≈ 91 ft`). On arrival, nudge heading toward the next segment bearing (bounded by turn rate) and return `ArrivedAtNode`. **(V2) Advance-on-pass:** `GroundNavigator.TickStraight` also arrives when the aircraft's *along-track projection* (foot-of-perpendicular distance from the segment start) reaches the to-node (`alongNm ≥ edgeLengthNm`), independent of cross-track. On the centerline this coincides with normal arrival; off it (a pure-pursuit overshoot of a short chord) it advances to the next segment instead of circling the node — the V2 replacement for the dropped Legacy orbit-stall tick counter. Excluded for stop targets and the last segment (must arrive precisely, not pass).
3. **Pure-pursuit steering** (`GroundNavigator.cs:1223`) toward the look-ahead point, with the pre-turn blend.
4. **Speed**: `ComputeTargetSpeed` → 80 %-distance backstop → `ClampBySpeedLimit` → `AdjustSpeed`.

### `TickArc` / `TickSlowTurn` step order

1. I7 speed floor: if below `ArcSpeedFloorKts`, set target heading to the current tangent, set speed, bail.
2. Advance the arc by `dAngle = (v·dt)/r` (clamped to remaining sweep), signed by turn direction.
3. Write `Position` and `TrueHeading` directly from the new bearing-from-centre (I2).
4. Mirror heading into `Targets` (so physics doesn't fight the closed-form state) and set speed — `ComputeTargetSpeed` for arcs (participates in the constraint system), the primitive's `MaxSpeedKts` cap for slow-turns (they do not).
5. When remaining sweep ≤ 0.01°, nudge heading toward the next bearing and return `ArrivedAtNode`.

### Route advance in the owning phase

On `ArrivedAtNode`, `TaxiingPhase.ArriveAtNode` (`TaxiingPhase.cs:221`) fires AT-ground/AT-taxiway triggers, checks for a hold-short (insert `HoldingShortPhase`) or runway crossing, then advances `CurrentSegmentIndex += 1 + _nav.ExtraSegmentsToAdvance`. The `ExtraSegmentsToAdvance` term skips the intermediate chord-chain nodes that a cluster slow-turn geometrically passed (below). If the route is now complete it inserts the terminal phase; otherwise it calls `SetupCurrentSegment` for the next segment.

### Synthesis planning detail

`PlanSynthesisLookahead` (`GroundNavigator.cs:612`) forward-scans up to `SynthesisLookaheadCapFt = 500` ft for the next corner needing synthesis:

- Skips corners below `SlowTurnSynthesisMinAngleDeg = 45.0°` **unless** `TryDetectCluster` confirms a chord-chain cluster.
- Skips corners whose natural arc fits the post-corner segment (single corners only; clusters are mandatory by construction).
- Walks backward through collinear straights (within `CollinearBearingToleranceDeg = 5°`) to find available incoming room, picks the largest radius that fits both tangent insets with a 20 % safety margin (floored at the nose-wheel minimum, with a warning when geometry wants tighter), derives entry speed from `v = r·ω` clamped to `[SlowTurnSpeedKts, CornerSpeedForAngle(θ)]`, and returns a `PlannedSynthesis` **only if the trigger point lands in the current segment** (otherwise defers to a later `SetupSegment`).

`TryDetectCluster` (`GroundNavigator.cs:904`) **[Legacy-compensation — re-evaluate on V2]** extends a run of consecutive short segments until it hits a "long" post-cluster anchor segment, treats the whole run as one virtual corner (net inbound-to-outbound deflection), and refuses the cluster if: it hits an arc, hits a ≥ 45° individual corner, the net deflection is less than half the cumulative absolute turn (zigzag guard), the natural arc actually fits (relevance guard), or any bypassed node — including the exit corner — carries a hold-short (braking-safety guard, `GroundNavigator.cs:998`).

---

## Caveats & gotchas

### Fillet arcs have a natural-forward bezier direction; reverse traversal flips tangents 180° **[Legacy-compensation — re-evaluate on V2]**

A `GroundArc` stores its Bezier with `Nodes[0]` = P0 and `Nodes[1]` = P3, with **no implied direction** (`AirportGroundLayout.cs:173`). When the route traverses the arc backward (from-node = `Nodes[1]`), `DirectionalEdge.DepartureBearing` / `ArrivalBearing` flip the bezier tangent by 180° (`AirportGroundLayout.cs:477`). `PathPrimitiveBuilder.BuildArc` derives the circle from these directional bearings, so it handles forward/backward correctly — **but** the historical "270° spiral" bug came from a reverse-traversed arc landing the aircraft 180° from the next walk step. The detection heuristic is whether `arc.Nodes[0].Id == fromNodeId`. V2 fillets collapse junctions into fewer tangent nodes, changing which arcs get reverse-traversed; re-validate this against V2 geometry.

### Chord-chain aggregate turn over a forward window **[Legacy-compensation — re-evaluate on V2]**

`EffectiveTurnAngleAt` (`GroundNavigator.cs:1691`) sums per-corner bearing changes over a forward window of `CornerLookaheadFt = 120 ft` rather than reading a single corner. Without it, a corner that the Legacy fillet generator sliced into many short chord edges (`FilletEdgeKind.ArcSplit` chains) shows only a small per-chord bend, `CornerSpeedForAngle` returns full taxi speed, and the aircraft enters the chain too fast to turn per chord — cross-track grows until pure-pursuit settles into a stable orbit. This was the OAK J chord-chain spin (N70CS orbiting node 383). V2's cleaner arcs may not produce chord chains, so this aggregation may be unnecessary on V2 (or the 120 ft window may need re-tuning).

**(V2) Resolved — no aggregate window; the per-corner feasibility cap is tessellation-invariant.** `GroundNavigator` reads a single corner (`SingleCornerTurnAngle`) and prices it through `CornerSpeed`, which takes the lower of the angle comfort cap and a turn-rate-feasibility cap `v ≤ ω·½L/θ` keyed on the shorter adjacent leg. On a chord chain the short legs drive that cap down even though each per-bend angle is gentle, and because `L ≈ R·θ` the cap reduces to `v = ω·r` — independent of how finely the curve is chorded — so it converges on the curve's true geometric speed without summing a window (and matches the lateral-accel cap a genuine arc carries). Curved ramp taxiways (SFO CG/SIG) **do** arrive as chord chains, so this is load-bearing, not dead code. The earlier `θ ≤ 30°` short-circuit defeated it — it returned full taxi speed for every shallow chord-chain bend, letting a jet take a 50 ft-radius / 90° apron curve at 30 kt vs the ~9 kt 0.13 g lateral-accel limit — so the gate was lowered to a 1° near-collinear epsilon. (Validated by aviation-sim-expert; guarded by `GroundNavigatorCornerSpeedTests`.)

### Cluster synth planner across short consecutive segments **[Legacy-compensation — re-evaluate on V2]**

`TryDetectCluster` + the cluster branch of `PlanSynthesisLookahead` build **one** composite slow-turn spanning a sequence of short chord segments and exiting onto the post-cluster long segment, advancing the route by `1 + ExtraSegmentsToAdvance` (`TaxiingPhase.cs:295`). The retarget at synth-engage time (`GroundNavigator.cs:1144`) is critical: without it, the hold-short check on arrival fires against the cluster's *already-passed* start node instead of its exit node. This addresses the same Legacy chord-chain anti-pattern as the aggregate-turn handling and is a prime candidate for removal on V2.

### Entry alignment fires at any segment start (threshold 45°)

`EntryAlignmentThresholdDeg = 45.0` and `SlowTurnSynthesisMinAngleDeg = 45.0` (both in `GroundNavigator.cs`; the lineage is 90° → 60° → 45°, so older notes and git history mention 60°). The entry-alignment slow-turn is the catch-all for any misaligned segment start (post-pushback U-turns, mid-route corners where synthesis didn't engage). It fires at any segment start with a heading delta over the threshold, independent of route position — there is no route-entry gate.

### Orbit detector as a backstop **[Legacy-compensation — re-evaluate on V2]**

When pure-pursuit can't close the last few feet (bearing-to-target rotates faster than the aircraft can turn), the aircraft enters a limit cycle around the node while distance decreases too slowly for the overshoot check to fire. `TicksNearTarget` (`GroundNavigator.cs:131`) counts consecutive ticks within `OrbitDetectionNm ≈ 30 ft` without arriving; after `OrbitStallTicks = 15` it force-advances (`GroundNavigator.cs:1208`). Suppressed at stop targets and the last segment (the aircraft must stop *at* those, not within 30 ft). Calibrated against orbits observed at SFO 42-4/D11/E10U/CG3 that sat 12–33 ft from target for 30–90 s. With synthesis and cluster handling now in place, the orbit detector is described as the **backstop**; on V2 it may be redundant or need recalibration.

**(V2) The orbit guard is two-layer, not the tick-count detector.** `GroundNavigator` dropped the Legacy `TicksNearTarget` counter on the theory that clean V2 fillets never produce chord chains — but curved ramp taxiways (SFO CG/SIG) still arrive as ~15 ft straight chords, and an aircraft carrying taxi speed into one overshoots and orbits (CG3→10L sat 73 s at 2–3 kt circling node 805). Replaced by: (1) **advance-on-pass** (above) — geometric, not a timeout, so it breaks the limit cycle the moment the aircraft passes the node; and (2) a **hard orbit invariant** in `Tick` — net signed turn within a single segment (reset on every segment/primitive start) may never reach `OrbitTurnLimitDeg = 360°` (no legitimate single-segment maneuver does: arcs sweep <180°, slow-turns <180°, straights ≈0°). On breach it **throws in tests** (`ThrowOnOrbit`, set by the test module initializer — every test that orbits fails hard with callsign/segment/degrees) and **logs + force-advances in the shipping app** (never crashes a live session). After this, all 717 SFO parking→runway pairs taxi within budget under full V2 (avg ramp speed ~10 kt → ~19 kt as the crawl/orbit is eliminated).

### Synth tolerance vs tight V2 arcs — the AMX669 freeze **[V2 regression — confirmed]**

This is the concrete latent issue the V2 sweep surfaced. On a tight **r = 40 ft V2 arc**, the strict-geometry synthesis tolerance was only ~6 ft (`max(5, 0.15 × 40) = 6`). AMX669 arrived **30.8 ft** off the planned tangent entry, so synthesis was **skipped** (`[NavV2] Synth trigger … skipping synthesis for corner node 770`), and pure-pursuit produced **no forward motion** — the aircraft **froze at taxi segment 0 from t=1, ias = 0, never advanced** (`IssueAmxTaxiOvershootTests.AMX669_HoldsShortOf1L_WithReasonableHeading`). The tolerance does not scale up enough for tighter V2 radii. The fix (per the review plan): scale tolerance with arc radius and/or guarantee a non-freezing pure-pursuit fallback so a skipped synthesis can never leave the aircraft stationary. The same benign `… skipping` lines appear in *passing* V2 taxi-smoke runs, showing the navigator operating right at the tolerance edge on V2 arcs. See `docs/plans/filletv2/v2-sim-validation.md` § "Navigator review".

### Short-approach base + landing geometry

The runway-exit/landing side has its own tuning that interacts with the navigator at the turn off the runway. Do not bump piston `PatternTurnRate` or shorten the virtual inbound segment without re-reading [`../landing-and-runway-exit.md`](../landing-and-runway-exit.md) — a longer virtual segment gives the navigator better turn anticipation, and shortening it produces heading reversals. The navigator's `SetupPrimitive` exists to inject a primitive directly (bypassing the route machinery), but see the next gotcha.

### `SetupPrimitive` has no production callers — its doc comment is stale

`GroundNavigator.SetupPrimitive` (`GroundNavigator.cs:545`) claims it is "used by phases that synthesise primitives programmatically (e.g. `LineUpPhase`)". **This is not currently true.** `LineUpPhase` does **not** call it — it carries its own `LineUpArcPlayback` integrator (`Phases/Tower/LineUpPhase.cs:134`) and consumes `PathPrimitiveSlowTurn` only as a geometry carrier. A grep for `SetupPrimitive` across `src/` finds the definition and no callers. Treat `SetupPrimitive` as effectively dead/test-only and the comment as aspirational; do not assume LineUpPhase shares the navigator's playback loop.

### Ground conflict detection / mutual proximity-stop deadlock

`GroundConflictDetector` (`src/Yaat.Sim/GroundConflictDetector.cs`) runs between the phase tick and physics and caps `Ground.SpeedLimit`, which the navigator honors. It classifies each aircraft pair into exactly one `PairKind` and resolves it. Two deadlock-avoidance mechanisms matter when reading navigator behavior:

- **SameEdgeHeadOn** picks a deterministic holder (more remaining route, tie-break by callsign) so the pair doesn't pin both aircraft — the earlier "both stop" rule deadlocked once two routes resolved onto one single-lane segment (`GroundConflictDetector.cs:437`).
- **Self-pin recovery** (`GroundConflictDetector.cs:341`): a routed aircraft clamped to gs ≈ 0 / SpeedLimit ≈ 0 with no explicit hold reclassifies as `Stationary`, opening the wingspan-lateral-clearance bypass so nearby movers can pass beside it the next tick.

A mutual proximity-stop deadlock is still possible in pathological geometry; `docs/plans/ground-graph-v2.md` notes it should be re-evaluated after routing (AMX669 was routed the wrong way *before* it deadlocked, so the routing layer may be the real cause). When the navigator appears "stuck", check `Ground.SpeedLimit` and the `[Classify]` / `[Pair]` diagnostic log lines before assuming a navigator bug.

### Snapshot round-trip is intentionally lossy

`GroundNavigator.ToSnapshot` / `FromSnapshot` (`GroundNavigator.cs:1749`) do **not** persist the active `PathPrimitive` or arc-progress state. `TaxiingPhase.FromSnapshot` (`TaxiingPhase.cs:154`) forces `_initialized = false` so the next `OnTick` rebuilds the primitive and speed constraints from `route.CurrentSegmentIndex`. A mid-arc save resumes from where the plan re-projects the aircraft geometrically, not from an exact arc-progress point — acceptable because arc segments are 2–3 seconds and mid-arc saves are rare. Adding new navigator runtime state means deciding whether it must round-trip; most arc state is deliberately reconstructed, not serialized.

---

## Key files

| File | Role |
|---|---|
| `src/Yaat.Sim/Phases/Ground/GroundNavigator.cs` | The navigator itself — setup, tick dispatch, steering, speed, synthesis, orbit detection |
| `src/Yaat.Sim/Phases/Ground/PathPrimitive.cs` | Immutable straight / arc / slow-turn primitives |
| `src/Yaat.Sim/Phases/Ground/PathPrimitiveBuilder.cs` | Segment → primitive compilation (Bezier-arc → true-circle) |
| `src/Yaat.Sim/Phases/Ground/TaxiingPhase.cs` | Owns the navigator; route management, hold-short / crossing / clearance / parking |
| `src/Yaat.Sim/Phases/Ground/RunwayExitPhase.cs` | Owns a navigator over the virtual exit route |
| `src/Yaat.Sim/Phases/Ground/CrossingRunwayPhase.cs` | Owns a navigator over the crossing route |
| `src/Yaat.Sim/Data/Airport/TaxiRoute.cs` | The route the navigator follows (segments, hold-shorts, index) |
| `src/Yaat.Sim/Data/Airport/AirportGroundLayout.cs` | `GroundArc` bezier fields, `DirectionalEdge` bearings, `MaxSafeSpeedKts` |
| `src/Yaat.Sim/AircraftCategory.cs` | All category performance constants (taxi/turn/decel/nose-wheel/corner speeds) |
| `src/Yaat.Sim/GroundConflictDetector.cs` | Per-tick speed-limit capping the navigator honors |

## Related docs

- [`./fillet-generator.md`](./fillet-generator.md) — the arc geometry the navigator follows (V1/V2).
- [`./pathfinder.md`](./pathfinder.md) — how the `TaxiRoute` is resolved (V1/V2).
- [`../landing-and-runway-exit.md`](../landing-and-runway-exit.md) — landing rollout, runway exit, virtual segments, anti-patterns (durable; cross-reference, do not duplicate).
- [`../phases.md`](../phases.md) — phase base contract, lifecycle, auto-append, command acceptance.
- [`../tick-loop.md`](../tick-loop.md) — per-tick order (phases before physics; conflict detection between).
- `docs/plans/filletv2/v2-sim-validation.md` § "Navigator review" — the open V2 navigator task list and AMX669 root cause.
- `docs/plans/ground-graph-v2.md` Workstream 3 — the three-layer flip plan; navigator v1.1 scope.
