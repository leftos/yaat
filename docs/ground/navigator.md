# Ground Navigator — Route-Following Design & Implementation

> Read this before touching `src/Yaat.Sim/Phases/Ground/GroundNavigator.cs`, `PathPrimitive.cs`, `PathPrimitiveBuilder.cs`, or the route-following parts of `TaxiingPhase.cs` / `RunwayExitPhase.cs` / `CrossingRunwayPhase.cs`. The navigator is the per-tick controller that physically steers an aircraft along an already-resolved taxi route. It does not build routes (that is the [pathfinder](./pathfinder.md)) and it does not build arc geometry (that is the [fillet generator](./fillet-generator.md)).

## Where it sits

### In the phase / tick system

The navigator is **not** a phase. It is a plain `sealed class GroundNavigator` owned by a ground phase as a field. The phases that own one:

| Phase | File | What the navigator follows |
|---|---|---|
| `TaxiingPhase` | `Phases/Ground/TaxiingPhase.cs:26` (`_nav`) | the full `AssignedTaxiRoute` from the pathfinder |
| `RunwayExitPhase` | `Phases/Ground/RunwayExitPhase.cs:53` (`_navigator`) | a short `_exitRoute` (virtual segment → branch → hold-short) |
| `CrossingRunwayPhase` | `Phases/Ground/CrossingRunwayPhase.cs:237` | a `_crossingRoute` across the runway to the exit node |

Phases run **before** physics each sub-tick (see [`../tick-loop.md`](../tick-loop.md) and [`../phases.md`](../phases.md)). The owning phase's `OnTick` calls `_nav.Tick(...)`; the navigator writes `ctx.Targets.TargetSpeed` / `TargetTrueHeading` (and, on arcs, writes `ctx.Aircraft.Position` / `TrueHeading` **directly** — see invariant I2 below) before `FlightPhysics.Update` reads them. Sub-tick delta is `ctx.DeltaSeconds` (≈ 0.25 s, four sub-ticks per sim-second).

`GroundConflictDetector.ApplySpeedLimits` runs **between** the phase tick and physics, writing `ctx.Aircraft.Ground.SpeedLimit`. The navigator honors that cap via `ClampBySpeedLimit` (`GroundNavigator.cs:1113`) and `AdjustSpeed` (`GroundNavigator.cs:1124`) so it never overruns a conflict-imposed limit.

### Inputs and outputs

**Consumes:**
- A `TaxiRoute` (`Data/Airport/TaxiRoute.cs`) — an ordered `List<TaxiRouteSegment>` plus `HoldShortPoints`, with a mutable `CurrentSegmentIndex`. Each segment wraps a `DirectionalEdge` over either a straight `GroundEdge` or a `GroundArc` fillet.
- Per-segment geometry, compiled into a `PathPrimitive` by `PathPrimitiveBuilder.FromSegment` (`PathPrimitiveBuilder.cs:43`): `PathPrimitiveStraight` for straight edges, `PathPrimitiveBezier` for `GroundArc` fillets (played as their true cubic Bézier).
- `PhaseContext` — aircraft state, `DeltaSeconds`, `Category`, ground-layout-derived data.
- An `isHoldShortCleared(nodeId)` delegate supplied by the owning phase, so the speed profile knows which hold-shorts are stops vs cleared transits.

**Writes:**
- `ctx.Targets.TargetSpeed` (always) and `ctx.Targets.TargetTrueHeading` (on curves).
- On straights: turns `ctx.Aircraft.TrueHeading` toward the steer bearing, bounded by turn rate, and lets physics advance position.
- On Bézier arcs and slow-turns: writes `ctx.Aircraft.Position` and `ctx.Aircraft.TrueHeading` **directly** from closed-form curve state.
- `ctx.Aircraft.Ground.LastNavDiag` — a `NavTickDiag` per-tick record (`GroundNavigator.cs:26`) consumed by `TickRecorder` CSV traces and `Yaat.LayoutInspector --tick-table`.

**Returns** a `NavigatorResult` (`GroundNavigator.cs:12`): `Navigating` (still moving toward the target) or `ArrivedAtNode` (the owning phase should advance `CurrentSegmentIndex`).

### Relationship to sibling ground phases

The navigator is one stage in a chain owned by `TaxiingPhase`. It is **not** responsible for hold-short insertion, runway crossing, departure clearance, parking, or phase handoff — `TaxiingPhase` does all of that around the navigator (see `ArriveAtNode` / `BuildResumePhases` in `TaxiingPhase.cs:221`). On arrival at a node:

- An uncleared hold-short → `TaxiingPhase` inserts `HoldingShortPhase` + resume phases; resuming a runway-crossing hold-short builds a `CrossingRunwayPhase` (`BuildResumePhases`).
- A **pre-cleared** runway crossing (the hold-short was cleared by an early `CROSS` / auto-cross before arrival) → `CrossingRunwayPhase` straight from the moving `TaxiingPhase`, no stop (`BuildPreClearedCrossingPhases`). Gated to a genuine forward crossing (near-side hold-short with a matching far-side hold-short of the same runway ahead, via `FindRunwayCrossingExitNode(requireSameRunwayExit: true)`); the far-side hold-short of a runway already vacated (landing-rollout exit) stays in `TaxiingPhase`. `TaxiingPhase.OnEnd` skips its stop-braking when handing off to a moving crossing.
- `CrossingRunwayPhase` owns its own navigator over a crossing-route slice. It crosses at **taxi speed** with `RunwayCrossingSpeed` as a no-stop floor (`MinSpeedKts`) — a crossing is just taxiing across (7110.65 §3-7-2 "cross without delay"); any curve in the painted line is still slowed by the navigator's arc-speed cap.
- Route end with a parking destination → `AtParkingPhase`; otherwise `HoldingInPositionPhase`.
- A pending departure clearance at route end → `LineUpPhase` / `LinedUpAndWaitingPhase` / `TakeoffPhase` chain.

The landing/exit side is documented separately — see [`../landing-and-runway-exit.md`](../landing-and-runway-exit.md). `LandingPhase` and `RunwayExitPhase` deliberately do **not** node-walk the runway; `RunwayExitPhase` builds a virtual inbound segment and hands a short route to a fresh `GroundNavigator` for the turn off the runway. `LineUpPhase` is a special case: it consumes `PathPrimitiveSlowTurn` **geometry** but plays it back with its own `LineUpArcPlayback` integrator (`Phases/Tower/LineUpPhase.cs`), not via `GroundNavigator`.

---

## Key design decisions

### Analog playback over compiled primitives ("Design B")

The class summary (`GroundNavigator.cs:39`) calls this "Design B closed-form playback over `PathPrimitive`s". The aircraft does not snap to nodes. Each route segment compiles into exactly one immutable `PathPrimitive` (`PathPrimitive.cs`):

- **`PathPrimitiveStraight`** — a line from `From` to `To` at `BearingDeg`. Followed by pure-pursuit steering (below); physics advances position.
- **`PathPrimitiveBezier`** — the `GroundArc` fillet played back as its **actual cubic Bézier** (the curve the renderer paints as the centerline), built by `PathPrimitiveBuilder.FromSegment`/`BuildBezier` (`PathPrimitiveBuilder.cs:43`/`:80`). `GroundNavigator.TickBezier` (`GroundNavigator.cs:823`) advances the curve parameter by arc-length each tick (`Δt = v·dt / |B'(t)|`, via `CubicBezier.DerivativeMagnitudeFt`) and writes position from `Evaluate(t)` and heading from `TangentBearing(t)` — satisfying I2 (both are pure functions of the one scalar `_bezierT`). Why the Bézier: `MinRadiusOfCurvatureFt` is the Bézier's *tightest* (apex) curvature, not the radius that connects its endpoints. For a wide sweeping fillet the apex is far tighter than the endpoint-connecting radius, so reinterpreting it as a single circle undershoots the corner's exit node (the OAK 28R→G corner: a 72 ft circle for endpoints 153 ft apart finished 56 ft short; a systemic scan found ~30–40 % of all OAK/SFO/FLL fillet traversals would undershoot >5 ft). Playing the real Bézier ends *exactly* on the to-node (its `P3`), so the next segment starts on-centerline instead of tripping the re-acquire speed gate into a crawl. The traversal-orientation (forward vs reversed) is baked into the stored curve at build time. Guarded by `GroundArcBezierPlaybackGuardTests` (every arc on OAK/SFO/FLL ends within 2 ft of its node).
- **`PathPrimitiveSlowTurn`** — geometrically identical to an arc circle, but the radius is the aircraft's nose-wheel minimum and the speed cap is `SlowTurnSpeedKts` (≈ 3 kt). Used for entry-alignment tight turns and tight programmatic pivots. Kept as a true-circle primitive — it is synthesised programmatically, not derived from a painted `GroundArc`.

**Invariant I2 (the reason arcs are closed-form):** during an arc primitive, *both* position and heading are pure functions of a single scalar — the aircraft's compass bearing from the arc centre (for Bézier: curve parameter `_bezierT`; for slow-turn: bearing `_arcBearingFromCenterDeg`). They advance together each tick and therefore **cannot drift apart**. The class summary states this directly (`GroundNavigator.cs:43`): the feedback-saturation "knife-edge" that dogged the older Bezier-waypoint approach cannot occur here by construction. This is why `TickBezier` / `TickSlowTurn` write `ctx.Aircraft.Position` and `TrueHeading` *directly* rather than steering toward a moving waypoint.

**Invariant I7 (no pivot-in-place):** arc and slow-turn ticks refuse to advance the integrator below `ArcSpeedFloorKts = 0.1` kt (`GroundNavigator.cs:92`). A stationary aircraft can't rotate on the spot; it must first roll forward. The navigator sets the speed target and bails, letting physics re-accelerate, then resumes the sweep.

### Pure-pursuit steering on straights

`TickStraight` (`GroundNavigator.cs:586`) does **not** steer at the target node directly. It steers toward a look-ahead point projected forward along the *segment line* from the aircraft's foot-of-perpendicular. This makes convergence onto the line first-class: an aircraft that spawned slightly off a taxiway, or got nudged by a prior corner, re-acquires the line rather than cutting diagonally across terrain. Look-ahead distance is `2 × speed × dt` clamped to `[LookAheadFloorFt = 10, LookAheadCapFt = 50]` (`GroundNavigator.cs:101`, `108`). The floor stops the look-ahead point collapsing onto the aircraft when nearly stationary; the cap stops it anticipating the next turn too aggressively on long straights.

A **pre-turn blend** blends the steer bearing toward the next segment's departure bearing over the last ≈ 50 ft of a straight, scaled by turn angle — full blend at ≤ 30°, ramping linearly to zero by 90° (`1 - (turnAngle-30)/60`), so sharp turns get little or no blend (those are handled by entry alignment instead).

### Entry-alignment threshold

When a new segment begins with the aircraft heading far off the segment's first tangent, `SetupSegment` (`GroundNavigator.cs:325`) builds a `PathPrimitiveSlowTurn` from the aircraft's current pose to the segment's start direction, stashes the real primitive in `_pendingSegmentPrimitive`, and plays the alignment arc first. The aircraft rolls forward at `SlowTurnSpeedKts` while rotating through real arc geometry — no in-place pivot, no heading snap.

Two gates, both required (`GroundNavigator.cs:360–369`):
1. **Heading delta > `EntryAlignmentThresholdDeg` = 45°** (`GroundNavigator.cs:310`). Normal fillet-smoothed corners stay below this by construction; only wrong-way starts, post-pushback U-turns, and mid-route corners where pure-pursuit diverges produce deltas this large.
2. **Segment long enough** to absorb the alignment chord plus a 1.2× pure-pursuit recovery margin. Short segments can't fit the displacement; alignment is deferred and the (rare, brief) snap is accepted.

Entry alignment is the **safety net** for the pure-pursuit divergence at low speed: when the look-ahead point shifts faster than the aircraft can turn, it orbits. Any segment whose start delta exceeds the threshold gets a slow-turn regardless of route position.

**Adaptive rounding radius.** The entry-alignment slow-turn and the incoming tangent-rounding both use an *adaptive* radius (`GroundNavigator.AdaptiveCornerRadiusFt`, defined at `531`), not a fixed nose-wheel radius. When the approach or departure leg is shorter than the comfortable tangent length `T = r·tan(δ/2)` — two junctions closer than `T` apart, e.g. SFO M2 between the B and A crossings (~22 ft for a 118° turn that wants 41.6 ft) — the radius tightens toward a category **tight-turn floor** (`CategoryPerformance.TightTurnFloorRadiusFt`, 15 ft jet ≈ inner-main-gear radius) so the arc still **exits on the outgoing centerline**. The incoming arrival threshold (`StraightArrivalThresholdNm`, defined at `556`) relaxes its `0.45·leg` cap to the whole leg only on such a tight leg, so the rounding can begin at the leg start. Without this, a fixed 25 ft arc off a 22 ft leg finishes ~26 ft wide, and pure-pursuit limit-cycles the corner on the short outgoing segment for ~45 s. This is judgmental oversteer (Boeing FCTM / AC 150/5300-13B): the nose may bulge wide of centerline mid-arc but rolls out aligned. Aviation-reviewed.

### Speed: corner-speed limits and backward-propagated braking

The navigator never overspeeds into a future turn. `BuildSpeedConstraints` (`GroundNavigator.cs:988`) runs at every `SetupSegment`:

1. Sets `_currentNodeRequiredSpeed` from `CornerSpeed(category, SingleCornerTurnAngle(...), legIn, legOut)` (0 for stops — uncleared hold-shorts and the last segment).
2. Forward-walks remaining segments collecting `(pathDist, requiredSpeed, nodeId)` constraints: future-arc max-safe speeds (`GroundArc.MaxSafeSpeedKts(category)` — a lateral-acceleration cap `v = √(a_lat·r)`, `a_lat ≈ 0.13 g`, additionally capped by `CornerSpeed` and floored at `SlowTurnSpeedKts`), corner speeds at each future node, and 0 at the first uncleared hold-short (then stops).
3. Backward-propagates a kinematic decel curve (`v = sqrt(v_next² + 2·a·d)`) between adjacent constraints and into the current node's required speed.

`ComputeTargetSpeed` (`GroundNavigator.cs:956`) per-tick takes the min of: the brake curve from the current node's required speed, every future constraint (skipping cleared hold-shorts), and a quadratic heading-error scaling (`speedFraction`, full speed at 0° error down to 3 % at ≥ 90°). A safety backstop in `TickStraight` caps target speed so the aircraft can't cover more than 80 % of remaining distance in one tick (overshoot prevention).

Corner speeds, turn rate, accel/decel, nose-wheel radius, and slow-turn speed are all category-specific in `AircraftCategory.cs` (`CornerSpeedForAngle`, `GroundTurnRate`, `TaxiAccelRate`, `TaxiDecelRate`, `NoseWheelTurnRadiusFt`, `SlowTurnSpeedKts = 3.0`). Taxi max speed is `TaxiSpeed(category)`, multiplied by `TaxiExpediteMultiplier = 1.3` when the aircraft is expediting (`TaxiingPhase.cs:78`). These are the realism constraints: turn rate bounds heading change per tick, and entry-alignment radius floors at the nose-wheel minimum so the navigator never asks for a physically impossible turn.


## Per-tick walkthrough

### `SetupSegment` (called on each segment transition)

`GroundNavigator.cs:325`. Runs once when the route advances to a new segment:

1. Compile the current segment to a primitive (`PathPrimitiveBuilder.FromSegment` — returns `PathPrimitiveStraight` or `PathPrimitiveBezier`).
2. Set target node / lat / lon from the segment's to-node; reset `PrevDistToTarget`.
3. **Entry-alignment check** (above). If the start heading delta exceeds the threshold and the segment is long enough, swap in an alignment slow-turn and stash the real primitive.
4. Otherwise install the real primitive.
5. `BuildSpeedConstraints` (`GroundNavigator.cs:424` calls it; defined at `988`) — speed profile for this and all downstream segments.

`TaxiingPhase.SetupCurrentSegment` (`TaxiingPhase.cs:190`) then overrides the target lat/lon with a hold-short offset position when the to-node carries an uncleared hold-short (so the aircraft stops at the painted bar, not the intersection node).

### `Tick` — dispatch on primitive kind

`GroundNavigator.cs:454` switches on the active primitive:

| Primitive | Handler | Behavior |
|---|---|---|
| `PathPrimitiveStraight` | `TickStraight` (`586`) | arrival check → pure-pursuit steering → speed |
| `PathPrimitiveBezier` | `TickBezier` (`823`) | closed-form Bézier advance by arc-length; writes pos+hdg directly (I2) |
| `PathPrimitiveSlowTurn` | `TickSlowTurn` (`899`) | closed-form circle advance, capped at the primitive's `MaxSpeedKts`; writes pos+hdg directly (I2) |
| (null) | — | returns `ArrivedAtNode` |

After the switch, if an alignment slow-turn just completed (`result == ArrivedAtNode && _pendingSegmentPrimitive != null`), the navigator swaps in the deferred real primitive and returns `Navigating` in the same tick — the route counter has not advanced.

### `TickStraight` step order

1. **Arrival check** (`GroundNavigator.cs:634`): arrives when `distNm ≤ arrivalThreshold`, or overshoot, or by the **advance-on-pass rule**. Threshold is tight (`FinalNodeArrivalThresholdNm ≈ 1.8 ft`) on the last segment, stop targets, short edges, or when the next segment is a Bézier arc; otherwise loose (`NodeArrivalThresholdNm ≈ 91 ft`). The advance-on-pass rule: the aircraft also arrives when its *along-track projection* (foot-of-perpendicular distance from the segment start) reaches or passes the to-node (`alongNm ≥ edgeLengthNm`), independent of cross-track. On the centerline this coincides with normal arrival; off it (a pure-pursuit overshoot of a short chord) it advances to the next segment instead of circling the node. Excluded for stop targets and the last segment (must arrive precisely, not pass). On arrival, nudge heading toward the next segment bearing (bounded by turn rate) and return `ArrivedAtNode`.
2. **Pure-pursuit steering** (`GroundNavigator.cs:680`) toward the look-ahead point, with the pre-turn blend.
3. **Speed**: `ComputeTargetSpeed` (`GroundNavigator.cs:753` calls it; defined at `956`) → 80 %-distance backstop → `ClampBySpeedLimit` → `AdjustSpeed`.

### `TickBezier` / `TickSlowTurn` step order

1. I7 speed floor: if below `ArcSpeedFloorKts`, set target heading to the current tangent, set speed, bail.
2. Advance the curve/arc: for Bézier, by arc-length `dt = v·dt / |B'(t)|` updating the parameter; for slow-turn, by `dAngle = (v·dt)/r` (clamped to remaining sweep), signed by turn direction.
3. Write `Position` and `TrueHeading` directly from the evaluated curve/bearing-from-centre (I2).
4. Mirror heading into `Targets` (so physics doesn't fight the closed-form state) and set speed — `ComputeTargetSpeed` for Béziers (participates in the constraint system), the primitive's `MaxSpeedKts` cap for slow-turns (they do not).
5. When complete (Bézier `_bezierT ≥ 1.0`, slow-turn remaining sweep ≤ 0.01°), nudge heading toward the next bearing and return `ArrivedAtNode`.

### Route advance in the owning phase

On `ArrivedAtNode`, `TaxiingPhase.ArriveAtNode` (`TaxiingPhase.cs:221`) fires AT-ground/AT-taxiway triggers, checks for a hold-short (insert `HoldingShortPhase`) or runway crossing, then advances `CurrentSegmentIndex += 1`. If the route is now complete it inserts the terminal phase; otherwise it calls `SetupCurrentSegment` for the next segment.

---

## Caveats & gotchas

### Fillet Bézier orientation and reverse traversal

A `GroundArc` stores its Bézier with `Nodes[0]` = P0 and `Nodes[1]` = P3, with **no implied direction** (`AirportGroundLayout.cs:173`). When the route traverses the arc backward (from-node = `Nodes[1]`), `DirectionalEdge.DepartureBearing` / `ArrivalBearing` flip the tangent bearing by 180° (`AirportGroundLayout.cs:477`). `PathPrimitiveBuilder.BuildBezier` orients the curve for traversal at build time: the forward direction uses the stored Bézier as-is (`t=0` at the from-node), and reverse traversal reverses the control points so `t` still runs from-node → to-node (`PathPrimitiveBuilder.cs:103–106`). The historical "270° spiral" bug came from a reverse-traversed arc landing the aircraft 180° from the next walk step; the orientation fix ensures the tangent bearings are consistent with the actual curve direction.

### Chord-chain speed limiting via turn-rate feasibility

`SingleCornerTurnAngle` (`GroundNavigator.cs:1190`) reads a single corner's bearing change and prices it through `CornerSpeed` (`GroundNavigator.cs:1165`), which takes the lower of the angle comfort cap (`CategoryPerformance.CornerSpeedForAngle`) and a turn-rate-feasibility cap `v ≤ ω·½L/θ` (where `L` is the shorter adjacent leg, `ω` is the ground turn rate, and `θ` is the turn angle in radians). On a chord chain the short legs drive that cap down even though each per-bend angle is gentle; because `L ≈ R·θ`, the cap reduces to `v = ω·r` — independent of how finely the curve is chorded — so it converges on the curve's true geometric speed. Curved ramp taxiways (SFO CG/SIG, curved apron sections) **do** arrive as chord chains from the fillet generator, so this feasibility cap is load-bearing for realistic speeds through such sections (a 50 ft-radius / 90° apron curve should taxi at ~9 kt per 0.13 g lateral-accel, not 30 kt full speed). The gate applies across the whole angle range (lowered to a 1° near-collinear epsilon) so even shallow chord-chain bends get capped. Guarded by `GroundNavigatorCornerSpeedTests`.

### Short-connector transit (steady speed across a lane change)

`DetectShortConnector` (`GroundNavigator.cs`, called from `BuildSpeedConstraints`) recognizes a *short connector*: the current **straight** segment sits in a straight run bracketed on both ends by a turn (a `GroundArc` neighbor, or a `> ConnectorCornerThresholdDeg = 30°` heading change between straights) whose total length is `≤ ShortConnectorMaxLenFt = 250 ft`. This is a lane change across parallel taxiways via a short cross taxiway — the SFO **A→F1→B** case (issue #236): A and B run parallel ~236 ft apart, F1 is the ~perpendicular connector, and the ~228 ft straight F1 run between the two ~90° corners qualifies. While on such a run, `ComputeTargetSpeed` caps the target to `_connectorFlowSpeedKts` so the aircraft flows through at a steady low speed instead of accelerating on the connector straight (the braking curve alone permits ~20 kt) and braking back down for the second turn. A real crew flies a short connector as one continuous low-speed maneuver, not settle-wings-level-and-accelerate (AC 120-74B; aviation-reviewed).

The cap is **self-limiting** and only bites on genuinely sharp corners: `_connectorFlowSpeedKts` = the higher of the two bracketing corners' comfortable speeds, where a sharp (`> EntryAlignmentThresholdDeg`) bracketing corner — rounded by a nose-wheel-radius slow-turn — contributes `TurnRateLimitedSpeedKts(cat, NoseWheelTurnRadiusFt(cat))` (~5 kt for a jet) and a gentle (30–45°) one contributes the higher `CornerSpeedForAngle`. So a gentle bracketing turn yields a high (no-op) cap and the length window alone never slows a run; only sharp ~90° lane-change corners pull the speed down. It caps **speed only** — the ground track is unchanged (the aircraft still tracks the connector centerline, which is unavoidable for a connector this long without a fillet-layer S-curve; see the `#236` follow-up). Both ends must be a turn, so a single corner or a from-rest spot-exit pivot (one turn then a long straight) is unaffected. Recomputed each `SetupSegment`, so it round-trips through a snapshot for free. Guarded by `GroundNavigatorTests.ShortConnector_HoldsSteadyLowSpeed_NoSurge` (unit) and `Issue236SfoAF1BConnectorTests` (SFO replay).

### Entry alignment fires at any segment start (threshold 45°)

`EntryAlignmentThresholdDeg = 45.0` (`GroundNavigator.cs:310`). The entry-alignment slow-turn is the catch-all for any misaligned segment start (post-pushback U-turns, mid-route corners where convergence might diverge). It fires at any segment start with a heading delta over the threshold, independent of route position — there is no route-entry gate.

### Orbit guard (two-layer defense)

When pure-pursuit can't close the last few feet (bearing-to-target rotates faster than the aircraft can turn), the aircraft can enter a limit cycle around the node. Two mechanisms prevent indefinite circling:

1. **Advance-on-pass** (`TickStraight`, line 660): geometric, not a timeout — the aircraft advances to the next segment as soon as its along-track projection passes the to-node. This breaks the limit cycle the moment the aircraft passes the node, even off the centerline.
2. **Hard orbit invariant** (`Tick`, lines 469–486): net signed turn within a single segment (reset on every segment/primitive start) may never reach `OrbitTurnLimitDeg = 360°` (no legitimate single-segment maneuver does: arcs sweep <180°, slow-turns <180°, straights ≈0°). On breach it **throws in tests** (`ThrowOnOrbit`, set by the test module initializer) and **logs + force-advances in the shipping app** (never crashes a live session).


### Short-approach base + landing geometry

The runway-exit/landing side has its own tuning that interacts with the navigator at the turn off the runway. Do not bump piston `PatternTurnRate` or shorten the virtual inbound segment without re-reading [`../landing-and-runway-exit.md`](../landing-and-runway-exit.md) — a longer virtual segment gives the navigator better turn anticipation, and shortening it produces heading reversals.

### Ground conflict detection / mutual proximity-stop deadlock

`GroundConflictDetector` (`src/Yaat.Sim/GroundConflictDetector.cs`) runs between the phase tick and physics and caps `Ground.SpeedLimit`, which the navigator honors. It classifies each aircraft pair into exactly one `PairKind` and resolves it with a uniform **one-holds-one-goes** rule: exactly one aircraft of a close-range conflicting pair is held while the other proceeds — it never stops both. The deterministic holder per kind:

- **SameEdgeHeadOn** (opposite directions on one edge): holder = more remaining route, tie-break by callsign (`ResolveSameEdgeHeadOn`) — the earlier "both stop" rule deadlocked once two routes resolved onto one single-lane segment.
- **Converging merge** (routes share an upcoming node from different edges, then often continue on one shared lane): holder = the aircraft **farther** from the shared node; the nearer one (the merge-order leader) proceeds through the intersection first and the holder follows in trail. The closing-proximity safety net is merge-aware (`ApplyConvergenceClosing`) — without it the symmetric closing check pinned **both** aircraft at the merge (the OAK U/W node-17 JSX177-vs-SWA897 deadlock), because at a true merge there is no lateral room for the wingspan-bypass to open. Distance-to-the-shared-node is an acknowledged stand-in for controller merge precedence (real sequencing is closer to time-to-intersection); aviation-reviewed against 7110.65 §3-7-2/§3-7-3 (FOLLOW/BEHIND).
- **Crossing** (paths cross, no shared node): mutual stop → one aircraft holds while the other proceeds. The holder is chosen follower-aware (`ChooseMutualStopHolder`): when the geometry shows a clear follower — the other aircraft nearly dead-ahead of it (off-nose angles differ by ≥ `FollowerLeadOffNoseMarginDeg`) — the follower holds and the lead (which moves away as it proceeds) goes first, the auto equivalent of FOLLOW/BEHIND (7110.65 §3-7-2.a). Near-symmetric geometry (a true perpendicular crossing / head-on) falls back to a deterministic callsign tie-break. This stops a follower being released *through* the stopped aircraft it is trailing when both merge onto one lane (the OAK TE `949→1` drive-through, issue #224).

A residual mutual proximity-stop is still possible in pathological geometry, but is usually a **routing-layer** issue, not a detector one: two independently-resolved routes assigned opposing directions on a single-lane segment (AMX669 / SKW3404-vs-SWA2208 class) deadlock *correctly* — one holds until the routing or controller resolves it. That is a separate pathfinder concern. When the navigator appears "stuck", check `Ground.SpeedLimit` and the `[Classify]` / `[Pair]` diagnostic log lines before assuming a navigator bug.

### Snapshot round-trip is intentionally lossy

`GroundNavigator.ToSnapshot` / `FromSnapshot` (`GroundNavigator.cs:1232–1246`) do **not** persist the active `PathPrimitive` or curve-progress state. `TaxiingPhase.FromSnapshot` (`TaxiingPhase.cs:154`) forces `_initialized = false` so the next `OnTick` rebuilds the primitive and speed constraints from `route.CurrentSegmentIndex`. A mid-curve save resumes from where the plan re-projects the aircraft geometrically, not from an exact curve-progress point — acceptable because curve segments are 2–3 seconds and mid-curve saves are rare. Adding new navigator runtime state means deciding whether it must round-trip; most curve state is deliberately reconstructed, not serialized.

---

## Key files

| File | Role |
|---|---|
| `src/Yaat.Sim/Phases/Ground/GroundNavigator.cs` | The navigator itself — setup, tick dispatch, steering, speed, orbit detection |
| `src/Yaat.Sim/Phases/Ground/PathPrimitive.cs` | Immutable straight / Bézier / slow-turn primitives |
| `src/Yaat.Sim/Phases/Ground/PathPrimitiveBuilder.cs` | Segment → primitive compilation (GroundArc Bézier → PathPrimitiveBezier) |
| `src/Yaat.Sim/Phases/Ground/TaxiingPhase.cs` | Owns the navigator; route management, hold-short / crossing / clearance / parking |
| `src/Yaat.Sim/Phases/Ground/RunwayExitPhase.cs` | Owns a navigator over the virtual exit route |
| `src/Yaat.Sim/Phases/Ground/CrossingRunwayPhase.cs` | Owns a navigator over the crossing route |
| `src/Yaat.Sim/Data/Airport/TaxiRoute.cs` | The route the navigator follows (segments, hold-shorts, index) |
| `src/Yaat.Sim/Data/Airport/AirportGroundLayout.cs` | `GroundArc` bezier fields, `DirectionalEdge` bearings, `MaxSafeSpeedKts` |
| `src/Yaat.Sim/AircraftCategory.cs` | All category performance constants (taxi/turn/decel/nose-wheel/corner speeds) |
| `src/Yaat.Sim/GroundConflictDetector.cs` | Per-tick speed-limit capping the navigator honors |

## Related docs

- [`./fillet-generator.md`](./fillet-generator.md) — the Bézier arc geometry the navigator follows.
- [`./pathfinder.md`](./pathfinder.md) — how the `TaxiRoute` is resolved.
- [`../landing-and-runway-exit.md`](../landing-and-runway-exit.md) — landing rollout, runway exit, virtual segments, anti-patterns.
- [`../phases.md`](../phases.md) — phase base contract, lifecycle, auto-append, command acceptance.
- [`../tick-loop.md`](../tick-loop.md) — per-tick order (phases before physics; conflict detection between).
