# Ground Navigator — Route-Following Design & Implementation

> Read this before touching `src/Yaat.Sim/Phases/Ground/GroundNavigator.cs`, `PathPrimitive.cs`, `PathPrimitiveBuilder.cs`, or the route-following parts of `TaxiingPhase.cs` / `RunwayExitPhase.cs` / `CrossingRunwayPhase.cs`. The navigator is the per-tick controller that physically steers an aircraft along an already-resolved taxi route. It does not build routes (that is the [pathfinder](./pathfinder.md)) and it does not build arc geometry (that is the [fillet generator](./fillet-generator.md)).
>
> Read [FOLLOWG: joining the lead's taxi path](#followg-joining-the-leads-taxi-path) before touching `Phases/Ground/FollowRoutePlanner.cs`, the `FOLLOWG` planner, and [FOLLOWG: driving the follow route](#followg-driving-the-follow-route) before touching `Phases/Ground/FollowingPhase.cs`.

## Where it sits

### In the phase / tick system

The navigator is **not** a phase. It is a plain `sealed class GroundNavigator` owned by a ground phase as a field. The phases that own one:

| Phase | File | What the navigator follows |
|---|---|---|
| `TaxiingPhase` | `Phases/Ground/TaxiingPhase.cs:64` (`_nav`) | the full `AssignedTaxiRoute` from the pathfinder |
| `RunwayExitPhase` | `Phases/Ground/RunwayExitPhase.cs:108` (`_navigator`) | a short `_exitRoute` (virtual segment → branch → hold-short) |
| `CrossingRunwayPhase` | `Phases/Ground/CrossingRunwayPhase.cs:44` (`_navigator`) | a `_crossingRoute` across the runway to the exit node |
| `FollowingPhase` | `Phases/Ground/FollowingPhase.cs:46` (`_nav`) | the `FOLLOWG` follow route (the follower's route to the merge node, then the lead's path), or a clearing route off a runway; never the aircraft's `AssignedTaxiRoute` |

Phases run **before** physics each sub-tick (see [`../tick-loop.md`](../tick-loop.md) and [`../phases.md`](../phases.md)). The owning phase's `OnTick` calls `_nav.Tick(...)`; the navigator writes `ctx.Targets.TargetSpeed` / `TargetTrueHeading` (and, on arcs, writes `ctx.Aircraft.Position` / `TrueHeading` **directly** — see invariant I2 below) before `FlightPhysics.Update` reads them. Sub-tick delta is `ctx.DeltaSeconds` (≈ 0.25 s, four sub-ticks per sim-second).

`GroundConflictDetector.ApplySpeedLimits` runs **between** the phase tick and physics, writing `ctx.Aircraft.Ground.SpeedLimit`. The navigator honors that cap via `ClampBySpeedLimit` so it never publishes a target above a conflict-imposed limit, and `FlightPhysics.UpdateSpeed`/`UpdatePosition` clamp to the same limit again.

**The navigator never integrates speed.**

It publishes `Targets.TargetSpeed` (and `Targets.DesiredDecelRate`: `SlowdownDecelRateKts` when the owning phase set one and no stop's braking curve sets the target, else `DecelRateKts` — the expedite rate, null otherwise) every tick and `FlightPhysics.UpdateSpeed` closes the gap at the ground rates — `CategoryPerformance.TaxiAccelRate` (1.0 kt/s for every category: the breakaway rate, then idle) and `DesiredDecelRate ?? TaxiDecelRate` — with a snap window of one sub-tick's change.

The braking curve planned at `TaxiDecelRate` executes at exactly that rate, with no hidden margin.

### Inputs and outputs

**Consumes:**
- A `TaxiRoute` (`Data/Airport/TaxiRoute.cs`) — an ordered `List<TaxiRouteSegment>` plus `HoldShortPoints`, with a mutable `CurrentSegmentIndex`. Each segment wraps a `DirectionalEdge` over either a straight `GroundEdge` or a `GroundArc` fillet.
- Per-segment geometry, compiled into a `PathPrimitive` by `PathPrimitiveBuilder.FromSegment` (`PathPrimitiveBuilder.cs:43`): `PathPrimitiveStraight` for straight edges, `PathPrimitiveBezier` for `GroundArc` fillets (played as their true cubic Bézier).
- `PhaseContext` — aircraft state, `DeltaSeconds`, `Category`, ground-layout-derived data.
- An `isHoldShortCleared(nodeId)` delegate supplied by the owning phase, so the speed profile knows which hold-shorts are stops vs cleared transits.

**Writes:**
- `ctx.Targets.TargetSpeed` (always) and `ctx.Targets.TargetTrueHeading` (on curves — and **released** (`null`) by `ReleaseHeadingHold` whenever a straight primitive takes over, in `SetupSegment` and at the entry-alignment swap).

  The target is persistent on the aircraft.

  Left in place after a fillet, physics turns the nose back toward the stale exit tangent every substep while pure pursuit nudges it out, the aircraft drifts off a straight that leaves the fillet a fraction of a degree off the tangent, and the orbit guard — which sees only the navigator's half of that tug-of-war — declares a full circle on an aircraft that never turned (OAK 30-departure scenario: a queued 5-kt crawl toward the W/W1 junction, `GroundNavigatorStraightHandoffTests`).

  Same family as the `RunwayExitPhase` handoff leak.
- On straights: turns `ctx.Aircraft.TrueHeading` toward the steer bearing, bounded by the **speed-coupled** ground yaw rate (`CategoryPerformance.GroundYawRateAtSpeed` — `ω = v/R` at the comfortable main-gear turn radius (`MainGearTurnRadiusFt`), capped at the `GroundTurnRate` ceiling; full authority above ~3 kt, falling to ~0 at a standstill), and lets physics advance position.

  This is why a near-stationary aircraft no longer pivots at the full ceiling. Arcs are already `v/R`-coupled by construction (the closed-form advance is `dAngle = v·dt/r`), and `GroundArc.MaxSafeSpeedKts` folds the same `ω·r` cap into the arc speed so a jet can't carry taxi speed through a tight fillet.
- On Bézier arcs and slow-turns: writes `ctx.Aircraft.Position` and `ctx.Aircraft.TrueHeading` **directly** from closed-form curve state.
- `ctx.Aircraft.Ground.LastNavDiag` — a `NavTickDiag` per-tick record (`GroundNavigator.cs:26`) consumed by `TickRecorder` JSON traces and `Yaat.LayoutInspector --tick-table`.

**The hold contract: a hold pins the published speed, it never skips the steering tick.** While `Ground.IsImmobile` (HOLDPOSITION / GIVEWAY) — and equally while `LineUpPhase.HoldPosition` is set by CTOC or a mid-line-up HOLD — the owning phase still calls `_nav.Tick`, and pins `ctx.Targets.TargetSpeed = 0` *after* it. Two independent reasons, and the order matters:

- **Pin the target, don't merely decrement `IndicatedAirspeed`**, or `FlightPhysics.UpdateSpeed` re-accelerates toward the speed the navigator just published, every sub-tick, and the "held" aircraft keeps rolling (issue #407: two held aircraft taxied through each other head-on). Behavioral pin: `GroundPhaseTests.GroundMotionPhase_WhenHeld_LeavesNoSpeedTarget`.
- **Keep ticking the navigator**, or closed-form curve playback goes stale. During a curve, position is a pure function of one progress scalar (invariant I2), so a tick skipped while physics is still braking the aircraft forward leaves that scalar behind the aircraft — and the first un-held tick writes the aircraft *backwards* onto the stale pose.

A held phase must also report **nothing terminal**: no segment advance, no hold-short insertion, no spot stop, no route completion, no phase completion. `TaxiingPhase` and `LineUpPhase` both snap `IndicatedAirspeed` to 0 on an `ArrivedAtNode` that lands during a hold and defer the arrival itself to the first un-held tick, where the primitive reports it again.

The one exception is a `TaxiingPhase` arriving still faster than 3 kt at an uncleared bar, which only an aircraft whose runway line was already lost does: it keeps braking at the firm rate instead of being stopped dead from that speed ([Stopping at an uncleared bar](#stopping-at-an-uncleared-bar)).

Advancing the segment index while held would set up the next primitive and let the next held tick re-tick the finished one at `t = 1` and advance again; completing while held would start a stored takeoff clearance's line-up, or hand a held line-up to `TakeoffPhase`, with the controller having said hold. `CrossingRunwayPhase` is exempt from the staleness half only because it snaps `IndicatedAirspeed` to 0 on the same tick the hold appears, so its playback and the aircraft never diverge.

A held `TaxiingPhase` near an uncleared bar does not stop where it is at the taxi rate alone: its pinned speed is capped by the braking the bar needs, after any node arrival, so a `HOLD` or `GIVEWAY` given inside the taxi-rate stopping distance still stops the nose at or behind the holding position marking ([Stopping at an uncleared bar](#stopping-at-an-uncleared-bar)).

**Returns** a `NavigatorResult` (`GroundNavigator.cs:12`): `Navigating` (still moving toward the target) or `ArrivedAtNode` (the owning phase should advance `CurrentSegmentIndex`).

### Relationship to sibling ground phases

The navigator is one stage in a chain owned by `TaxiingPhase`. It is **not** responsible for hold-short insertion, runway crossing, departure clearance, parking, or phase handoff — `TaxiingPhase` does all of that around the navigator (see `ArriveAtNode` / `BuildResumePhases` in `TaxiingPhase.cs:221`). On arrival at a node:

- An uncleared hold-short → `TaxiingPhase` inserts `HoldingShortPhase` + resume phases; resuming a runway-crossing hold-short builds a `CrossingRunwayPhase` (`BuildResumePhases`).
- A **pre-cleared** runway crossing (the hold-short was cleared by an early `CROSS` / auto-cross before arrival) → `CrossingRunwayPhase` straight from the moving `TaxiingPhase`, no stop (`BuildPreClearedCrossingPhases`).

  Gated to a genuine forward crossing (near-side hold-short with a matching far-side hold-short of the same runway ahead, via `FindRunwayCrossingExitNode(requireSameRunwayExit: true)`); the far-side hold-short of a runway already vacated (landing-rollout exit) stays in `TaxiingPhase`. `TaxiingPhase.OnEnd` skips its stop-braking when handing off to a moving crossing.
- `CrossingRunwayPhase` owns its own navigator over a crossing-route slice. It crosses at **taxi speed** with `RunwayCrossingSpeed` as a no-stop floor (`MinSpeedKts`) — a crossing is just taxiing across (7110.65 §3-7-2 "cross without delay"); any curve in the painted line is still slowed by the navigator's arc-speed cap.

  The ½-fuselage tail-clearance past the far-side node follows the **route's own next segments** (a straight is cut at the exact distance with a virtual node; an arc is taken whole), and on completion the crossing phase writes `route.CurrentSegmentIndex` to the segment the aircraft is actually on.

  Until then the route's current segment stays on the crossing's entry segment for the whole crossing, even once the aircraft is past that segment's end node. Anything that reads `AssignedTaxiRoute.CurrentSegment` during a crossing must allow for that; `GroundConflictDetector` orders an in-trail pair by along-edge progress for this reason.

  It used to extend straight along the entry taxiway's graph continuation and hand the route back one segment past the exit, so a route that turned right after the crossing resumed on a fillet 60 ft behind the aircraft (the SFO G → B case the no-teleport guard caught).
- Route end with a parking destination → `AtParkingPhase`; otherwise `HoldingInPositionPhase`.
- **Nose-at-spot terminal stop** (`TryStopNoseAtSpot`, run before the navigator each tick): a route to a parking **spot** (`DestinationSpot`) stops with the aircraft's nose at the spot marking — the centroid rests a half-fuselage-length **short** of the spot node — not centered on it.

  Otherwise a fuselage centered on a ramp spot juts a half-length toward the adjacent taxiway, and the ground conflict detector then slows traffic taxiing past (issue #234; SFO's spot 7 ramp sits ~175 ft off taxiway A).

  The final approach within one fuselage of the spot is capped to a slow parking speed so the stop lands cleanly; on a tight ramp whose lead-in lane is shorter than a fuselage the aircraft comes to rest mid-turn, at an angle — realistic for a taxi-in (aircraft are normally pushed onto spots).
- A pending departure clearance at route end → `LineUpPhase` / `LinedUpAndWaitingPhase` / `TakeoffPhase` chain. The route end is **not** a stop in that case: `TaxiingPhase` sets `GroundNavigator.RouteEndSpeedKts` to the category taxi-corner speed when a takeoff or LUAW clearance is already stored for the destination-runway bar the route terminates at, so the speed plan aims to arrive rolling and `TaxiingPhase.OnEnd` skips its stop-braking, the same exemption a moving crossing gets.

  A runway holding position marking means stop only "when a clearance has not been issued to proceed onto the runway" (AIM §2-3-5.a.1); braking there and re-accelerating also defeats the controller technique in 7110.65 §3-9-5, which clears a still-taxiing aircraft precisely to spend the taxi time against a closing separation interval.

  `RouteEndSpeedKts` defaults to 0 — every other taxi (gate, spot, an uncleared bar, the far side of a crossing) still ends at rest — and it is recomputed each tick, so a clearance arriving mid-segment re-plans the profile through `RefreshSpeedConstraints` rather than waiting for the next node. It replaces only the route-end stop; the zero at every **uncleared** hold-short on the way is untouched, which is what keeps it from being a runway-incursion path.

The landing/exit side is documented separately — see [`../landing-and-runway-exit.md`](../landing-and-runway-exit.md). `LandingPhase` and `RunwayExitPhase` deliberately do **not** node-walk the runway; `RunwayExitPhase` builds a virtual inbound segment and hands a short route to a fresh `GroundNavigator` for the turn off the runway.

`LineUpPhase` (line up onto the runway) mirrors that pattern. Its **preferred** path is graph-aware: `LineUpGraphRoute.TryPlan` (`Phases/Tower/LineUpGraphRoute.cs`) resolves a route that follows the real taxiway edges from the hold-short to the runway edge and curves onto the centerline via the baked junction fillet arc, ending tangent to the departure heading; `LineUpPhase` plays it back through a fresh `GroundNavigator` (`State.GraphTaxi`).

This makes departures follow the painted lead-on lines onto the runway instead of cutting a diagonal across the taxiway/runway junction (issue #239).

The walk toward the runway skips neighbours that are **on** the runway centerline, not merely edges flagged `IsRunwayCenterline` — a junction fillet is a taxiway-to-runway curve and carries no such flag, so stepping along one lands the walk on a centerline node whose every other edge is a centerline edge, and it dead-ends there.

That is not hypothetical at a runway-**crossing** taxiway: the approach side carries a tangent-cut node per prong, and the walk always meets the reciprocal-end one (whose arc `TangentAlignToleranceDeg` correctly rejects) before the departure-end one that holds the arc the route needs. SFO 28R off taxiway E is the case — the walk reached the 10L-facing prong first and refused the whole route, dropping every such departure onto the synthetic fallback at walking pace.

The plan carries **two** speeds. `MaxSpeedKts` is the straight-segment cap, the category taxi speed: the physics binds before the cap does on a connector this short (a piston entering at 10 kt reaches only ~15 kt over 230 ft), so the cap only ever binds on a genuinely long one.

The fillet is left to the navigator's own `GroundArc.SpeedProfile`, which is already `min(lateral accel, CornerSpeedForAngle, ω·r)` — the previous flat `TaxiCornerSpeed` override was documented as the speed for "a turn of 90° or more" and was being applied to a 70° fillet, holding it 20% below what the geometry allows. `FlowSpeedKts` is the rolling floor (`MinSpeedKts`), which keeps the corner speed so a takeoff clearance flows onto the centerline without braking.

A route always ends with a virtual straight rollout segment past the arc exit. Under **LUAW** the navigator brakes to a stop on it — braking to a stop on the fillet itself would deadlock the closed-form playback, which cannot advance below the arc speed floor.

Under a **rolling** clearance the phase completes at the fillet exit instead and never flies it: in a real rolling takeoff the throttle comes up as the aircraft straightens out of the turn, so that 80 ft of centerline alignment is the first 80 ft of the takeoff roll, not taxi preceding it.

The segment stays in the route regardless, so a `CTOC` reverting the aircraft out of rolling mode still has somewhere to brake.

When no departure-aligned onto-runway arc route resolves — a parallel-taxiway or shallow-angle hold-short with no clean junction arc (issues #142, #193) — it falls back to the synthetic geometric pivot: `LineUpGeometry` produces a `LineUpPathPlan` consuming `PathPrimitiveSlowTurn` **geometry**, played back with its own `LineUpArcPlayback` integrator, not via `GroundNavigator`.

### FOLLOWG: joining the lead's taxi path

`FollowRoutePlanner.Plan(layout, follower, lead)` (`Phases/Ground/FollowRoutePlanner.cs`) says whether and where the follower can get onto the lead's taxi path. It is read twice. Before `FOLLOWG` installs a `FollowingPhase`, `GroundCommandHandler.RejectUnjoinableFollow` runs it as the joinability probe: `TryFollow` runs it, and so does `ArmFollowBehindRunwayHold` when the follow is armed at a runway bar; it is skipped when there is no layout or no lead lookup.

`FollowingPhase` then plans again on its first tick (and after a restore with no follow route, or when its route runs out) and drives the `Joinable` plan as its follow route ([FOLLOWG: driving the follow route](#followg-driving-the-follow-route)). While it drives one, it plans again through `FollowRoutePlanner.Replan` at each node and when the lead is re-routed ([re-planning](#followg-driving-the-follow-route)).

**The lead's path** has three parts, oldest first:

- **Its trail**, walked back from the newest `TaxiEdgeTrail` edge. Each trail edge is pointed toward the edge after it. Where two do not meet (1 Hz samples skip short edges), the shortest graph path fills the gap, searched forward the way the lead taxied it — from each end of the older edge to the newer one, keeping the cheaper — so a one-way lane the lead drove the right way is not excluded. Each trail edge also records the end node the aircraft entered it from (`TaxiTrailEdge.EntryNodeId`), which the ground conflict detector reads to tell a lead coming back along its trail; the planner does not.

  The walk stops at the first edge no graph path joins, and at the first step that would revisit an edge the walk already holds. That is where the lead reversed, as after a push out along a taxiway it then taxied back down, so the path never runs out and back over an edge.

  The path is built from the edges the lead drove, except that a gap's straight stubs either side of a fillet arc the lead rounded are replaced by the arc, so the follower does not take the square corner. `FollowRoutePlanner.CutCorner` swaps the newest run of the gap step's edges (at most the step's edges plus the one edge held before it) for the junction's arc when the arc suits the follower's taxi class (`UsableArc`) and is shorter than the run. A trail that leaves a corner and returns to it keeps its loop, and a lead that drove the junction itself keeps the junction's edges. The held set swaps the run for the arc. A path arc is never matched by `LocateOnPath` (see [measuring along the lead's path](#followg-joining-the-leads-taxi-path)).
- **The edge it is on** (`TaxiEdgeLocator.EdgeUnder`, looking near the newest trail edge first), pointed the way it is going. On its route, the first remaining segment over that edge points it; off its route, the end nearer its heading does (`OrientByHeading`), which a turn in progress can swing past the edge's own direction.
- **Its remaining assigned route** after that edge. Segments the lead has passed are dropped even when its segment index lags and still names one — held on its first segment through an entry-alignment turn, or sampled onto the next edge before it reaches the node. Only the segments after the one over its current edge lie ahead.

  A segment that does not start where the path ends is reached by the shortest graph path.

  A lead that is itself a follower (a `FollowingPhase` current or still queued in its phases) contributes the route it drives instead (`FollowingPhase.DrivenRouteOf`: its follow route, or its clearing route) while its follow is its current phase, and no route while a hold or a crossing stands in front of the follow. Its assigned route is never read: `FOLLOWG` leaves it in place, so it predates the follow and is stale.

**The rules, in order:**

1. The lead is in `PushbackPhase` or `AtParkingPhase`, or on no taxi edge → `WaitForLead`. There is no path to join yet, and the follow is accepted.
2. The follower stands on the lead's edge nearer its far end than the lead is, or on an edge of the lead's route ahead of that edge → `FollowerAhead`. `FOLLOWG` answers "unable, ahead of {lead} on its route — issue HOLD, GIVEWAY or TAXI first".
3. The follower stands behind the lead on the edge the lead is on → `Joinable` at once, with no search. The merge node is that edge's start in the lead's direction (behind the follower), the route to it has no segments, and the merge is on the lead's trail side, not ahead of it.
4. A goal-set search from the follower's route start (`StartOf`, [below](#where-the-follow-route-starts)) to every node of the lead's path (`TaxiPathfinder.FindRouteToNearestGoal`; see [the pathfinder](./pathfinder.md#goal-set-search-autorouterruntogoals)) finds nothing, or there is no start node → `NoPath`. `FOLLOWG` answers "unable, no taxi route to {lead}'s route".
5. Otherwise → `Joinable`. The merge node is the goal the search reached, and the plan carries the follower's route to it (`PathToMerge`), the lead's path edge into it (`LeadEdgeIntoMerge`, null where the lead's path starts at the merge), the lead's path from it on, and whether the merge lies ahead of the lead (the far node of its current edge, or further along its route) rather than on its trail.

   A follower standing mid-way along an edge into the search's start node, facing that node, gets that edge as the first segment of `PathToMerge` (`LeadInSegment`, the lead-in segment; any straight edge within `TaxiEdgeLocator.OnTaxiwayMaxOffsetFt` that it projects between the ends of, a runway centreline too).

   It then drives on from where it stands, rather than reading the stretch back to the route's first node as a centreline it is off and crawling there at the navigator's re-acquire speed. There is no lead-in at the node itself, facing away from it, or on no edge into it.

**Where the follow route starts.** `FollowRoutePlanner.StartOf` picks the search's start node, and the lead-in segment into it, from the follower's own `TaxiEdgeTrail`, which records ramp connectors too (`TaxiEdgeLocator.DrivenEdgeUnder`). A follower on a fillet arc, or beside another taxiway's edge, so plans on along the edges it drove rather than from whichever straight edge it stands nearest. The first rule that applies wins:

1. **Partway round a fillet arc.** The follower stands on a fillet arc that meets its newest trail edge at the end it drives toward (`TaxiEdgeLocator.FilletArcRounding`), within `TaxiEdgeLocator.OnFilletArcMaxOffsetFt` (8 ft) of the curve and strictly between its ends. The route starts on the arc: the arc, pointed away from the trail edge, is the first segment and its far node the search's start.

   A follower already driving a route counts as on the arc only while that route's current segment is the arc into its far node: where a fillet leaves a straight edge tangent to it the two lie within a foot of each other, and only the route says which one it drives. The navigator enters the Bézier at the follower's closest point on it ([Invariant I8](#analog-playback-over-compiled-primitives-design-b)).
2. **The trail's end.** Within `TaxiEdgeLocator.OnTaxiwayMaxOffsetFt` (50 ft) of its newest trail edge, the start is the end of that edge the follower drives toward: the end it does not share with the edge recorded before it, or, with none that meets it, the end the edge runs toward within 90° of the heading. `LeadInSegment` leads it in as above.
3. **Otherwise** (farther off the newest edge, an empty trail, or a newest edge the layout does not hold), `AirportGroundLayout.FindTaxiStartNode`.

On a plan made with no route to drive (`Plan`: the first plan, or one after the route ran out) the arc's far node and the trail's end count only when they lie ahead of the follower, within 90° of its heading; one behind falls through to the next rule. A re-plan (`Replan`) takes them as they are, since a follower re-planning just past a node has that node a little behind it and still drives on from it. The clearing route starts the same way, without the ahead check.

**Measuring along the lead's path.** A `Joinable` plan gives the follower's gap to its lead along the taxi path rather than in a straight line:

- `LocateOnPath` finds the first path edge that is the straight taxi edge under an aircraft (never a fillet arc) and how far along it the aircraft's centre projects (`PathPosition`). Where an edge repeats on the path, the earlier entry wins: it is the one a follower behind the lead reaches.
- `PathOffsetFt` turns a `PathPosition` into a distance from the path's start.
- `FollowerToMergeFt` is the follower's distance to the merge: minus its path offset once it stands on the lead's path from the merge (it is past the merge, as a follower that joined behind the lead on its edge is from the start); before that, the rest of its route to the merge. It is null with no route segment left and the follower on no path edge, where a straight distance back to the merge could read with the wrong sign.
- `AlongPathGapFt` is the nose-to-tail gap: the follower's distance to the merge plus the lead's path offset, less half of each aircraft's length. It is null when either input is unknown; the caller then falls back to the straight-line nose-to-tail distance.
- `LeadTailPastMerge` says whether the lead's tail, half its length behind its centre, has passed the merge node.

### FOLLOWG: driving the follow route

`FollowingPhase` (`Phases/Ground/FollowingPhase.cs`) follows its lead over the taxi graph through its own `GroundNavigator`, never by steering at the lead's position. It writes nothing to `Ground.AssignedTaxiRoute`: the route `FOLLOWG` leaves in place stays as it was, and `FollowingPhase.DrivenRouteOf(aircraft)` is the one accessor that answers which route an aircraft actually drives (the follow route or clearing route while following, else the assigned route).

**The follow route.** On its first tick (`TryFollow` starts the phase through `BuildMinimalContext`, which has no aircraft lookup) the phase calls `FollowRoutePlanner.Plan` and installs a `Joinable` plan as one `TaxiRoute`: the `PathToMerge` segments, then the lead's path from the merge node. `MergeSegmentIndex` is the index of the first segment on the lead's path.

A route driven from its first segment gets a fresh navigator, set up with `MaxSpeedKts` at the category taxi speed; a route spliced in at a later segment keeps the navigator, which carries on ([re-planning](#followg-driving-the-follow-route)).

The follow route's end is no navigator stop (`RouteEndSpeedKts` is the taxi speed): whether it is a stop turns on the lead each tick (`RouteEndIsStop`).

Behind a lead at rest, or a lead with route of its own left (it stops at that route's end, which is the follow route's end too), it is: the phase caps the speed on the braking curve at the taxi rate onto the end, measured along the route (`RouteEndCap`, `TaxiRoute.RemainingDistanceFt`). A lead rolling at 1 kt or more with no route left is driving on past the planned end, so the follower drives on to the end and plans again there.

When the route runs out the phase drops it and plans again in the same tick, so a follower whose new plan joins the lead's path never stands a tick with no route.

**Re-planning.** A follower driving a follow route plans again (`FollowRoutePlanner.Replan`) at two moments. On each node arrival, before the next segment is set up (`ReplanAtNode`), it picks up a lead whose path grew past the planned end or changed; a plan there other than `Joinable` keeps the route it is driving.

And on the tick the lead is re-routed (`FollowLeadReroute`): the lead's remaining route no longer matches the lead path the plan was built from (`FollowRoutePlanner.LeadRouteMatchesPath`), as after a new `TAXI` to the lead, or a lead that is itself a follower planning a new follow route.

A re-plan's route starts where the follower stands, without the ahead check ([where the follow route starts](#followg-joining-the-leads-taxi-path)), and never turns it about. Its first move may not run back along the segment that leads the follower into the start or, with none, back along its newest trail edge: the pathfinder's [forbidden first move](./pathfinder.md#goal-set-search-autorouterruntogoals).

A lead driving the follower's edge back the way the follower came, or a lead path that leaves a merge at the start that way, joins nothing (`NoPath`).

A new plan is adopted only when it drives differently from the follower's current segment on: another edge sequence, or the merge at another distance ahead (`DrivesAlike`). One whose route runs through the current segment is spliced in from it, and its give-way replaces any the old plan latched.

One whose route does not pass through the current segment is installed from its start only when that start lies ahead of the follower, within 90° of its heading, and its first edge leaves within 90° of the heading too (`FollowRoutePlanner.DepartsAhead`), so a re-plan at a junction never swerves the follower back across its heading onto another taxiway. Otherwise, at a node arrival the follower keeps the route it is driving; after a lead re-route the route is lost and the follow is unjoinable.

**The route lost.** A lead re-route whose re-plan joins nothing (`NoPath`, `FollowerAhead`), or whose new route starts behind the follower, loses the follow route (`LoseFollowRoute`) and makes the follow unjoinable. An empty re-plan (nothing to drive: the merge is where the follower stands and the lead's path ends) loses the route too, but the follow stays joinable and plans again each tick once the route is dropped.

A follower with some part on a runway or inside its hold line drops the lost route at once and drives its clearing route. Any other keeps it and brakes to rest along it (`IsBrakingLostRoute`): the navigator keeps steering over the lost route, planning nothing, with the speed pinned to zero at the taxi rate, or the firm rate a runway bar ahead needs, so the stop stays on the centreline through any bend or fillet. When the route's end lies inside its stopping distance at the taxi rate, it brakes onto the end at the firm rate (`FollowingPhase.LostRouteEndDecelRate`). The brake also keeps the straight-line nose-to-tail gap to the lead: when the lead is on the ground with its tail ahead of the follower (within 90° of its heading) and the taxi rate would end inside the stop gap behind it, the follower brakes at the firm rate (`FollowingPhase.LostRouteLeadGapDecelRate`); a lead abeam or behind is nothing to brake for. A route that runs out while it still rolls is kept: the navigator stops ticking and the follower holds the last segment's heading at the firm rate to rest. At rest it drops the route and holds.

It never comes to rest inside a runway's hold line. A bar ahead on the rest of the lost route whose runway the follow's crossing clearance covers (one the bar scan passes) is stopped short of: at the taxi rate when that makes the hold line, at the firm rate (`CategoryPerformance.ExpediteExitDecelRate`: jet 7.5, turboprop 6.0, piston 4.5, helicopter 4.0 kt/s) when only that does.

When not even the firm rate makes it, or the nose is already inside that runway's hold line, the follower carries on across under its crossing clearance. Once on the runway's pavement it drops the lost route, and its clearing route takes it on past the far hold line, after which it holds.

**Hold-shorts.** The navigator is given an always-cleared hold-short delegate: `FollowingPhase` owns every runway-bar stop through its own bar scan (`CheckRunwayHoldShort`, `GroundStopBraking`, `TryTakeHoldAtBar`), so the follow route carries no hold-short points.

`MustStopAt` stops the follower at every runway bar it has no crossing for, and always at its own departure bar; the lead's crossing never carries over. A taxiway hold-short the lead was given is no stop for the follower: the gap stops it behind a held lead. At rest at a bar it takes the hold: a `HoldingShortPhase` with a fresh `FollowingPhase` queued behind it.

Two bars are passed. A bar of a runway the follower is exiting (one whose pavement it occupied in this follow, or whose hold line it is inside) is passed when the follower meets it moving away from the runway: the route's segment out of the bar node does not lead nearer the runway's centreline, or, off the route, the heading does not. A bar met heading toward the runway still stops it.

A runway is forgotten as exiting once the follower's tail and both wingtips are farther from its centreline than the farthest of its bars on their side (`TrackExitingRunways`), so a later crossing of it stops at the bar again. The other bar passed is the one a clearing route leaves the runway by.

**Give way at the merge.** A plan with a route to the merge whose merge lies ahead of the lead, or whose lead's tail is not yet past it, installs the follow giving way (`IsGivingWay`).

While it gives way, the follower brakes onto the stop `GroundConflictDetector.GiveWayStop` places short of the lead's track through the merge (the lead's edge into the merge and its first edge out), as a `GIVEWAY` places its stop. The braking is routine when the taxi rate makes the stop, firm when only the max-effort rate does, else a firm stop where it is.

The give-way ends once the follower is past the merge (its route index at `MergeSegmentIndex` or beyond), or the lead's tail is past the merge with the gap at least the stop gap; the follower then falls in behind. A follower that joins behind the lead on the lead's own edge has no route to the merge and never gives way.

**The gap.** The gap is nose to tail along the path (`FollowRoutePlanner.AlongPathGapFt`), or the straight-line nose-to-tail distance (`FollowingPhase.NoseToTailFt`) when the lead or the follower is not on the path. `FollowGap.StopGapFt` and `FollowGap.CloseFollowBandFt` key the stop gap and the close-follow band by both aircraft.

The gap's speed is zero at the stop gap (with 1 ft of settle), else the brake curve `v = sqrt(2·a·d)` to the stop gap at the taxi decel rate, read where this tick's travel leaves the follower, and no faster than taxi speed.

Inside the close-follow band behind a lead moving away from the follower, it is no slower than the lead, with a 5 kt walking-pace floor to close up; a lead backing or coming toward the follower opens no gap, so its speed is not matched.

**Speed.** The published speed is the lowest of the navigator's (taxi speed, corner and route-end limits), the gap's, the give-way stop's, the route end's (behind a lead at rest) and the runway bar's braking curve, with the highest brake rate any cap below the ground speed needs (`ApplyCap`). The speed `ApplyCap` lowers is first held to the ground conflict detector's `Ground.SpeedLimit`, so a cap never publishes a speed above the detector's limit. A target left empty reads as the speed the follower holds, never as a stop: physics clears a target once the aircraft reaches it, and the navigator publishes none on the physics sub-tick it arrives at a node, so a follower keeps its speed across every node arrival. A `HOLD` or `GIVEWAY` on the follower keeps the navigator ticking and pins the speed to zero after it, as the [hold contract](#in-the-phase--tick-system) says.

**No plan.** With no plan the follower holds in position (it brakes to a stop where it is), never steering straight at the lead. With no layout, a `WaitForLead` plan, or an empty `Joinable` plan (no route to the merge and no lead path from it), it plans again each tick. A `NoPath` or `FollowerAhead` plan at runtime makes the follow unjoinable (`IsUnjoinable`), logged as the route lost: it holds for good, planning no more, until a new command replaces it.

The first `NoPath` that makes it unjoinable also has the pilot say so once (`PilotResponder.BuildUnableNoRouteToFollow`, to a solo student on ground or tower, else the RPO's terminal naming the lead); a `FollowerAhead`, or a re-plan whose route starts behind the follower, says nothing. The latch is snapshotted, so a restored follow does not say it again.

**The clearing route.** The follower never holds on a runway or inside its hold line (AIM 4-3-21). With no follow route to drive while its nose, centre or tail is on a runway's pavement or nearer the runway's centreline than the nearest of its bars on that side, it drives a clearing route instead (`StartClearingRoute`, once per runway until a follow route is installed again). A follower off the pavement facing the runway gets no clearing route: it logs a warning and holds.

The clearing route runs from where the follower stands (from a follow route's start, `FollowRoutePlanner.StartOf` without the ahead check, a fillet arc it stands partway round included) by the auto route to the nearest of the runway's bars ahead, skipping an exit another aircraft stands in at rest unless every one is, then on past the bar by the aircraft's length plus the longer of 50 ft and its stopping distance from taxi speed at the taxi brake rate (`ClearingNeedFt`).

Past the bar it takes the straightest continuation that leads no nearer the runway and onto no other runway, on through any junction short of that length, stopping only where the pavement ends. A route that enters another runway, or that brings a follower that started off the pavement nearer this runway, is passed over for the next nearest bar.

A clearing route never reaches past another runway's bar. Between close parallels a long aircraft can therefore run out of route before its tail clears: at SFO a B744 clearing 01R/19L on V stops 70 ft past the bar node, short of 01L/19R's bar, with its tail 45 ft inside 01R/19L's hold line, and the "clearing route ran out" warning tells the instructor it needs a crossing of the next runway. A B738 at the same crossing clears with 8 ft to spare.

On the clearing route the follower keeps the gap to the nearest aircraft ahead on it, the lead or any other.

Once its tail and both wingtips are past the bar's hold line (`IsClearPastBar`; the tail alone for a type with no wingspan in the FAA aircraft data), it brakes to rest at the taxi rate while the navigator keeps steering along the clearing route, so the braking roll stays on the centreline through any bend or fillet (7110.65 3-10-9.b NOTE 1; AIM 4-3-21.b).

At rest the clearing ends and the follower is checked again, so one inside a second, intersecting runway's hold line clears that one too. A clearing route that runs out first logs a warning and holds, saying once that it is not clear of the runway it was clearing: "holding short of runway {next}" when the route's last node has a refused edge entering another runway (`RunwayAheadAtTheHold`, `SayHoldingShortNotClear`; `BuildHoldingShortNotClear`), else "holding position" (`BuildHoldingPositionNotClear`). That hold is latched as held short, which silences the lost-traffic call until a new clearing route or a re-installed follow route clears the latch.

While it brakes along the clearing route past the bar, still rolling and inside no runway's hold line, a follow that is not unjoinable plans again each tick (`ResumeFollowFromBrakingRoll`; whether it is past the bar is read from where it stands, so a restore does the same, and a bar or runway the layout cannot resolve counts as not past). One still inside a second runway's hold line brakes on to rest and clears that runway first. A `Joinable` plan with a route to drive ends the clearing and is installed in that tick, so the follower takes up following without stopping first; its runway-bar stops still apply. Any other plan leaves it braking, to plan again at rest.

**The lead gone.** When the lead is deleted or no longer on the ground, the follow drops its follow route and drives the clearing route if it is inside a runway's hold line.

On a taxiway (inside no runway's hold line, no runway cleared since its last follow route) with an assigned route not yet driven to its end, a `TaxiingPhase` takes over at once on the rest of that route (`ResumeAssignedRoute`). `FOLLOWG` leaves the assigned route's current segment where the taxi was given, so the route is re-anchored first: the auto route from where the follower stands (`FollowRoutePlanner.RouteOnto`, from `StartOf`, starting back along the edge under the aircraft only where that move does not leave back across its heading: `BackAlongEdgeUnder`) onto the nearest node of the route's remaining segments, then the route on from there, with its hold-short points.

The hand-over is refused when the way back crosses a runway hold line, which the assigned route holds no stop for: any bar `HoldShortAnnotator.AddImplicitRunwayHoldShorts` marks on it (arriving at a bar of the assigned route is not crossing), the bar it starts at when it goes on to another bar of that runway, or any node it reaches along a runway centreline edge or on a runway's pavement, so a layout with no bar is covered too (`RunwayEntries`; 7110.65 3-7-2.c/d). The follow becomes unjoinable. When the refusal is the way back entering a runway (`RefuseLeadInAcrossRunway`) the pilot says once that it lost the traffic and holds (`SayLostTrafficHolding`, `BuildLostTrafficHolding`), and says nothing when no assigned route is left to lose, or when it has already said it cannot follow; when a live lead has no route to its path it says once that it cannot follow and asks for taxi instructions (`BuildUnableNoRouteToFollow`). A follower made unjoinable for another reason — a lead re-route, or a plan ahead of its lead — has said nothing, so it still says the lost-traffic line once its lead is gone. A way back that crosses no bar, such as KOAK's B A C B around 28R's end, is taken. An unjoinable follow never resumes its assigned route on its own.

Otherwise it brakes to a stop where it is, and at rest completes into a `HoldingInPositionPhase`.

**Snapshot.** The follow route, merge index, `LeadEdgeIntoMerge`, give-way, unjoinable and braking-along-a-lost-route flags, the clearing route and its bar, and the navigator's state round-trip ([`../snapshots-and-replay.md`](../snapshots-and-replay.md)); a restore without a follow route plans afresh on the next tick.

---

## Key design decisions

### Analog playback over compiled primitives ("Design B")

The class summary (`GroundNavigator.cs:39`) calls this "Design B closed-form playback over `PathPrimitive`s". The aircraft does not snap to nodes. Each route segment compiles into exactly one immutable `PathPrimitive` (`PathPrimitive.cs`):

- **`PathPrimitiveStraight`** — a line from `From` to `To` at `BearingDeg`. Followed by pure-pursuit steering (below); physics advances position.
- **`PathPrimitiveBezier`** — the `GroundArc` fillet played back as its **actual cubic Bézier** (the curve the renderer paints as the centerline), built by `PathPrimitiveBuilder.FromSegment`/`BuildBezier` (`PathPrimitiveBuilder.cs:43`/`:80`).

  `GroundNavigator.TickBezier` (`GroundNavigator.cs:823`) advances the curve parameter by arc-length each tick (`Δt = v·dt / |B'(t)|`, via `CubicBezier.DerivativeMagnitudeFt`) and writes position from `Evaluate(t)` and heading from `TangentBearing(t)` — satisfying I2 (both are pure functions of the one scalar `_bezierT`). Why the Bézier: `MinRadiusOfCurvatureFt` is the Bézier's *tightest* (apex) curvature, not the radius that connects its endpoints.

  For a wide sweeping fillet the apex is far tighter than the endpoint-connecting radius, so reinterpreting it as a single circle undershoots the corner's exit node (the OAK 28R→G corner: a 72 ft circle for endpoints 153 ft apart finished 56 ft short; a systemic scan found ~30–40 % of all OAK/SFO/FLL fillet traversals would undershoot >5 ft).

  Playing the real Bézier ends *exactly* on the to-node (its `P3`), so the next segment starts on-centerline instead of tripping the re-acquire speed gate into a crawl. The traversal-orientation (forward vs reversed) is baked into the stored curve at build time. Guarded by `GroundArcBezierPlaybackGuardTests` (every arc on OAK/SFO/FLL ends within 2 ft of its node).
- **`PathPrimitiveSlowTurn`** — geometrically identical to an arc circle, but the radius is the adaptive corner radius (the comfortable main-gear radius for a free-space aim, down to the tight-turn floor) and the speed cap is `SlowTurnSpeedKts` (≈ 3 kt), or `TurnAboutSpeedKts` for the arcs of a turn about on a taxiway ([below](#entry-alignment-threshold)). Used for entry-alignment tight turns and tight programmatic pivots. Kept as a true-circle primitive — it is synthesised programmatically, not derived from a painted `GroundArc`.

**Invariant I2 (the reason arcs are closed-form):** during an arc primitive, *both* position and heading are pure functions of a single scalar — the aircraft's compass bearing from the arc centre (for Bézier: curve parameter `_bezierT`; for slow-turn: bearing `_arcBearingFromCenterDeg`). They advance together each tick and therefore **cannot drift apart**.

The class summary states this directly (`GroundNavigator.cs:43`): the feedback-saturation "knife-edge" that dogged the older Bezier-waypoint approach cannot occur here by construction. This is why `TickBezier` / `TickSlowTurn` write `ctx.Aircraft.Position` and `TrueHeading` *directly* rather than steering toward a moving waypoint.

**Invariant I7 (no pivot-in-place):** arc and slow-turn ticks refuse to advance the integrator below `ArcSpeedFloorKts = 0.1` kt (`GroundNavigator.cs:92`). A stationary aircraft can't rotate on the spot; it must first roll forward. The navigator sets the speed target and bails, letting physics re-accelerate, then resumes the sweep.

**Invariant I8 (no teleport):** an arc primitive may never write the aircraft farther than it drove.

Closed-form playback has an implicit precondition — the aircraft is *on* the curve when the primitive starts — and nothing upstream guaranteed it: a fillet entered a few feet off-centerline, a route whose first segment was an arc the aircraft was not standing on (the OAK gate-15 report: ~100 ft), or a snapshot without saved playback restored mid-arc (`_bezierT` reset to 0 and the first tick rewound the aircraft to the arc's entry node) all applied the whole offset in one sub-tick.

Three rules now hold it:
- **Entry state is captured on the primitive's first tick, from the live position** (`_arcEntryPending`). A Bézier entered off its start point resumes from `CubicBezier.ClosestT(position)` with the arc length up to that `t` as its progress baseline (`CubicBezier.ArcLengthToNm`), so a mid-arc restore continues from where the aircraft stands.

  A route whose first segment is such a Bézier, with the aircraft within `TaxiEdgeLocator.OnTaxiwayMaxOffsetFt` of the curve at a point strictly between its ends (a follow route started partway round a fillet), also has `SetupSegment` read the entry-alignment check against the curve's tangent at that closest point (`MidCurveEntryBearing`) rather than the curve's departure bearing, so it lays no entry-alignment turn for a heading it already holds.

  The capture waits for the first tick rather than happening at install because phases run *before* physics: the position at install time is one physics step (along the old heading) behind the position the first arc write is compared against.
- **An along-track shortfall is driven, a cross-track residual is bled off — neither is jumped.**

  If the aircraft is still short of the curve's start point along the entry tangent (the projection clamps to `t = 0`), that distance is a *lead-in* (`_bezierLeadInRemainingFt`).

  The primitive advances along the tangent until it is consumed and only then starts the curve, so the arc's arc length is honoured and the aircraft never gains ground it did not drive (an SFO background aircraft handed off 17 ft short of a fillet was "catching up" at 1.3× its ground speed while a pure blend absorbed the gap).

  The remaining cross-track offset is scaled by `max(0, 1 − travelledSinceEntry / blendFt)` and added to the curve point, where `blendFt = min(max(ArcEntryBlendFt, offset / tan MaxBlendTrackDeg), remaining arc length)` (50 ft ≈ 2× a jet's main-gear turn radius; 5°).

  The rate cap keeps the implied track within 5° of the written heading, since a ground vehicle cannot sideslip (aviation review note: a 10 ft offset bled off over 50 ft is an 11° track/heading divergence). The arc-length bound keeps the fillet's end-on-its-node guarantee (the next segment must start on-centerline), so on a short arc the track cap yields — 12 ft over the 87 ft OAK 763→762 fillet is 7.8°.

  Heading stays the curve tangent. An offset beyond `MaxArcEntryOffsetFt` is refused at capture with its own message rather than blended — that is a route that started an arc the aircraft was nowhere near. I2 still holds: position is a pure function of progress plus a constant captured once.
- **`GroundNavigator.ThrowOnTeleport`** (set by the test module initializer like `ThrowOnOrbit`): after every direct position write, `CheckNoTeleport` compares the distance moved against the arc length advanced plus `TeleportToleranceFt` (2 ft) and throws in tests / logs an error in the shipping app.

  The blend rate is offset/50 per foot travelled, so an entry offset above ~2 ft × 50 / (v·dt) still trips the guard — deliberately: a route that starts an arc that far from the aircraft is a routing defect (`TaxiApproachLeg` owns bridging it), and the guard surfaces it rather than smoothing it away. Its first run found three pre-existing sites in 10,000 tests (SFO fillet entries one physics step off; an OAK `TAXI G` issued on 28R, 314 ft short of the exit fillet).
- **Corollary: on a short arc the blend outruns the aircraft.** Because `blendFt` is capped at the remaining arc length, an entry offset is bled off over whatever arc is left, so the surplus written per sub-tick is `offset × ds / remaining` on top of the `ds` driven: 27 ft over 20 ft of arc at 3 kt is ≈ 2 ft a sub-tick, the whole of `TeleportToleranceFt`.

  The signature in the log is a `[Nav] Bezier entry: … (aircraft N ft from the curve start)` line with N larger than the arc's own length — the aircraft was handed a curve it is nowhere near the start of, which is a hand-over defect upstream (the node-aimed fillet case above), not something the blend should absorb.

### Pure-pursuit steering on straights

`GroundNavigator.TickStraight` does **not** steer at the target node directly. It steers toward a look-ahead point projected forward along the *segment line* from the aircraft's foot-of-perpendicular. This makes convergence onto the line first-class: an aircraft that spawned slightly off a taxiway, or got nudged by a prior corner, re-acquires the line rather than cutting diagonally across terrain.

Look-ahead distance is `max(2 × speed × dt, 1.5 × cross-track offset)` clamped to `[LookAheadFloorFt(category), LookAheadCapFt = 50]`, where the floor is the category's main-gear turn radius (`CategoryPerformance.MainGearTurnRadiusFt`: 25 ft jet / 18 turboprop / 15 piston / 10 helicopter).

Pure pursuit commands curvature `2·sin α / L`; a look-ahead shorter than the gear's own minimum radius asks for more curvature than the nose wheel can deliver, so a few feet of offset left by a corner became a ~20° steer and the nose hunted across the line (the post-corner wiggle in the S2-OAK-2 bundle). The floor also stops the look-ahead point collapsing onto the aircraft when nearly stationary; the cap stops it anticipating the next turn too aggressively on long straights.

A **pre-turn blend** blends the steer bearing toward the next segment's departure bearing over the last ≈ 50 ft of a straight, scaled by turn angle — full blend at ≤ 30°, ramping linearly to zero by 90° (`1 - (turnAngle-30)/60`), so sharp turns get little or no blend (those are handled by entry alignment instead).

**Re-acquire speed.** While the aircraft is more than `ReacquireOffsetFt` (4 ft) from the segment's line, `TickStraight` holds it to `ReacquireSpeedKts` (5 kt) so it converges before speeding up; this is what the from-rest stand-exit pivot, which finishes off the outgoing centreline, relies on. The offset is measured against the segment's **unclamped** line (`OffsetFromSegmentLineFt`), not the clamped foot of perpendicular: an early arrival at a node (the loose `NodeArrivalThresholdNm` window, ~91 ft) where the next segment continues on the same line leaves the aircraft on that line, only short of its start, and the clamped measure read that as up to ~90 ft off and dropped a 20 kt taxi to a crawl. The look-ahead above still scales with the clamped offset, which short of a segment's start only stretches it along the same line (bounded by `LookAheadCapFt`). An early arrival at a gentle bend still reads `d·sin δ` off the next line and slows; that case is tracked separately.

### Entry-alignment threshold

When a new segment begins with the aircraft heading far off the segment's first tangent, `GroundNavigator.SetupSegment` builds a `PathPrimitiveSlowTurn` from the aircraft's current pose to the segment's start direction, stashes the real primitive in `_pendingSegmentPrimitive`, and plays the alignment arc first. The aircraft rolls forward at `SlowTurnSpeedKts` while rotating through real arc geometry — no in-place pivot, no heading snap.

A turn about on a taxiway rolls slower, at its own pivot speed (below).

One gate: **heading delta > `GroundNavigator.EntryAlignmentThresholdDeg` = 45°**, lowered to 20° at an unfilleted straight→straight kink (issue #213). It fires regardless of segment length — a bend tighter than the main-gear turn radius cannot be tracked by pure pursuit at any allowed speed (the orbit radius `v/ω` exceeds the short-segment scale even at the slow-turn floor), so it must be rounded.

Normal fillet-smoothed corners stay below the threshold by construction; only wrong-way starts, post-pushback U-turns, and mid-route corners where pure-pursuit diverges produce deltas this large.

Entry alignment is the **safety net** for the pure-pursuit divergence at low speed: when the look-ahead point shifts faster than the aircraft can turn, it orbits. Any segment whose start delta exceeds the threshold gets a slow-turn regardless of route position.

**A reversal picks the side that unwinds into the route's next turn.** An entry sharper than `ReversalEntryThresholdDeg` = 135° is not a corner at all: `GeometricAdmissibility` hard-rejects any *junction* over the per-category limit (135° for a jet), so only the pathfinder's first-edge exemption — which lets a route leave from wherever the aircraft happens to stand — can produce one.

The aircraft is turning around, both sides reach the same tangent, and at exactly 180° the short way is a floating-point coin flip.

Two TAXI starts for an aircraft mid-way along a straight taxi edge produce one by design: a **turn about on segment 0** ([pathfinder.md](./pathfinder.md#where-it-sits--entry-points), route-aware start), in one of two shapes (`TaxiRoute.TurnAboutShape`). A route planned from the edge's far end (`TaxiTurnAboutShape.FromFarEnd`) opens with the free-space leg back along the edge to that node when the approach-leg guards allow one. Without the leg, `TaxiingPhase` ends the turn about once the aircraft reaches that node, since segment 0 then runs on past it.

A route kept from the node ahead although it runs back over the edge (`TaxiTurnAboutShape.InPlace`) opens with the edge itself, driven backwards; any aircraft keeps it when the far end is no better. A type whose gear does not fit a turn about (`TurnAboutFit`) takes it only as the last resort: a controller's TAXI to it is refused, and a scripted TAXI or `TAXIAUTO` takes the route ahead instead, turning about (in place included) only when the route ahead resolves nothing.

`TaxiApproachLeg` adds no leg to that in-place route, and its debug line `[ApproachLeg] no leg to node … the aircraft is past it, 0 ft abeam the line …` means this turn about in place, not a missing approach leg: the aircraft turns about where it stands, not at the junction ahead.

`ShouldReverseAgainstShortWay` therefore sweeps the arc **against** its short way when the route's next turn (`SignedTurnAfterEntry`: a fillet segment's own sweep, else the bend onto the next segment's departure bearing) runs the same sense *and* `2·|Δ| + |next| > 360` — the point where turning the other way and unwinding costs less than letting the two compound.

`PathPrimitiveBuilder.SlowTurnDirected` then sweeps the named direction however far round it is, where `SlowTurn` picks the short way for its caller. SFO spot 9, `TAXI A F 28L`: taxiway A is 180° behind the aircraft and the corner onto it is a further 90° right, so the short-way reversal made one continuous 270° clockwise sweep — net rotation +315° over the window, the "UAL58 did a loop before taxiing" report. Against the short way it is a left turnaround that the corner unwinds: net −45°.

**A reversal is aimed at a node too, and re-anchors the straight.** A half turn ends exactly one diameter *abeam* the outgoing centerline — a semicircle finishes on a line parallel to the one it aimed at, never on it — so the bearing aim leaves pure pursuit tens of degrees off the tangent, re-acquiring a line the aircraft has turned its back on.

A reversal onto a painted leg is therefore solved through `PathPrimitiveBuilder.SlowTurnToPointDirected` (the one-sided `SlowTurnToPoint`, since the tie-break has already ruled out the smaller-sweep side), and the straight that follows is re-anchored on the live position exactly as a free-space leg is (`_entryArcAimedAtNodeOffRealLeg` → `ReanchorFreeSpaceLine`).

Note the floor: an arc aimed at a node *behind* the aircraft must sweep **more** than a half turn to put its exit tangent through that node, approaching 180° only as the node recedes, and `AdaptiveCornerRadiusFt` clamps a 180° deflection to `TightTurnFloorRadiusFt`. SFO node 33 at ~81 ft off a 15 ft radius costs ~201°; that is geometry, not slack. Sub-threshold entries keep the bearing aim, whose exit the adaptive radius already fits to the outgoing leg.

**A reversal on a taxiway is a turn about within the taxiway's width.** `BuildEntryAlignmentArc` tries `BuildTaxiwayTurnAbout` first for any entry at or past `ReversalEntryThresholdDeg`, ahead of the painted-leg aim above and the free-space aim below.

`TurnAboutTaxiwayEdge` admits it only when the aircraft stands strictly inside a straight edge (`AirportGroundLayout.FindOccupiedTaxiEdge`: within `OnTaxiEdgeMaxOffsetFt` of its centreline with its foot inside the edge, so not at a node) and more than the turn radius from both of the edge's end nodes. The route must reverse back over that same edge: a free-space leg running to one of its end nodes, or the painted segment being the edge itself.

The edge must also be a turn-about taxiway (`IsTurnAboutTaxiway`: a named movement-area taxiway, not a ramp connector or a runway centreline, touching no parking or helipad node). Otherwise the comfortable-radius aims run: a mid-route reversal at a junction, where the aircraft arrives at the node or at a tangent point short of it, keeps rounding the corner at its corner speed, as do reversals on ramps, aprons, stand lead-ins and runways (`TaxiwayTurnAboutJunctionTests`).

`BuildTaxiwayTurnAbout` returns null outright when the previous route segment is a runway centreline (`GroundEdge.IsRunwayCenterline`: the exit's virtual approach leg or a real runway edge), so a ≥135° corner off a runway leg — an exit onto an acute crossing taxiway — is rounded at corner speed, never turned about. The aircraft is still on the runway centreline a few feet off the crossing taxiway there, and the 25 ft `OnTaxiEdgeMaxOffsetFt` band would otherwise read it as inside that taxiway (`OakAllExitsTests.OAK28R_C172_ExitJ_TurnsOffAtTheJunctionNotATurnAboutOnJ`).

`SolveTaxiwayTurnAbout` builds two arcs (one when the reversal is re-aimed past the bend, below), both at the type's turn-about radius from `TurnAboutFit.Evaluate`, the function the taxi gate refuses a controller's turn about with ([pathfinder.md](./pathfinder.md#where-it-sits--entry-points)), so the turn drawn and the fit decided share one geometry.

That radius is R = max(MGW/2, 0.466 × wheelbase) from the type's FAA record (a C172 4.2 ft, a C208 5.85 ft; MGW/2 for a taildragger, which pivots on a braked main wheel), or `CategoryPerformance.TightTurnFloorRadiusFt` (8/12/15/8 ft piston/turboprop/jet/helicopter) when the record lacks either figure or the type has none. Both arcs are capped at `CategoryPerformance.TurnAboutSpeedKts`, ω·R: the gear-limited `GroundTurnRate` held on that radius.

A taildragger's turn is drawn centred on R = MGW/2 like any other, a known simplification: its fit assumes the pilot first moves the pivot wheel (rₜ − MGW)/2 off the centreline so the tail swing (rₜ = √(wheelbase² + (MGW/2)²), about 17 ft for a PA18) uses the full width, so in the centred drawing the unmodelled tail would cross the taxiway edge by rₜ − half-width.

For a type that fits, that is well below the 3 kt `SlowTurnSpeedKts`, on purpose: at 3 kt the tight radius would need more yaw than the gear gives. A type that does not fit turns about only on a scripted clearance whose route ahead resolved nothing, on its own larger radius.

**A rolling aircraft brakes to its pivot speed first.** When the aircraft rolls faster than its pivot speed plus `TurnSpeedOvershootKts` (0.2 kt), `PlanRollingTurnAbout` puts a brake leg ahead of the jog: the heading held straight along the occupied edge while it brakes to the pivot speed. It brakes at `CategoryPerformance.TaxiDecelRate` (2 kt/s piston and helicopter, 5 kt/s jet and turboprop), or at the firm `ExpediteExitDecelRate` (4.5 kt/s piston) when the taxi rate leaves no room to turn about before the node ahead. A jet never brakes at the firm rate for a routine turn about: `RollingTurnAboutBrakeRates` gives it the taxi rate alone.

The turn about is solved from the pose where the braking ends, not the aircraft's own: the edge-end clearance, the jog, `FindAimNode`'s 2r and the roll-out hold are all judged there.

A turn about issued while rolling (the route's first segment) is admitted by `ReversalTaxiwayEdge` alone, with no clearance from the edge's ends where the aircraft stands, so one issued within a turning radius of the edge's end still brakes to its pivot speed first. A reversal the route reaches mid-way keeps `TurnAboutTaxiwayEdge`'s clearance from the aircraft's own position, and with it the junction rounding at its corner speed. `TickTurnAboutBrake` flies the leg; it ends, and the jog begins without a stop, once the aircraft is down to pivot speed plus 0.2 kt within `BrakeEndToleranceFt` (1 ft) of the leg's end point. The brake never blends into the turn.

**With no room before the node ahead, the turn about moves to it.** When no rate leaves room, `PlanTurnAboutAtNodeAhead` rolls on along the edge, braking to reach that node at pivot speed (at the gentlest rate that does), and turns about at the node on the type's own radius, back through it onto the reversed route. When no rate reaches the pivot speed by the node, it does not overrun a turn about solved there: it goes straight to the continuation below, and with no room there it stops and holds ([below](#a-rolling-turn-about-with-no-room)). Its jog and reversal must stay on the junction's pavement (`ArcsStayOnPavement`), and no re-aim past the bend is tried there. That pavement (`JunctionPavement`) is the centrelines of the straight edges meeting at the node and the junction's own corner fillets (`JunctionFillets`): every `GroundArc` at the node or at the far end of one of those straight edges whose tangent nodes all lie within `JunctionFilletReachFt` (151 ft: the fillet generator's `FilletConstants.MaxTangentDistFt` of 150 ft plus a foot of slack) of the node, each laid as 16 chords along its curve. A neighbouring junction's fillets, met at the far end of a straight edge, never count. Every sample must lie within the type's bound of one of them (`JunctionPavementBoundFt`: the taxiway half-width less half the FAA main-gear width).

When they leave that pavement, `PlanTurnAboutOnContinuation` rolls on past the node onto the straight edge continuing the occupied one (`TurnAboutContinuation`: a turn-about taxiway within `ContinuationMaxBendDeg` = 30° of its bearing), braking at the taxi rate when that braking still ends inside that edge, else the firm rate (never for a jet), and turns about two radii past the node, or where its braking ends when that is further, rolling out facing back through the node.

The brake leg's chord, from the aircraft to where that braking ends, is held to the same junction pavement, sampled every 2 ft (`ChordStaysOnJunctionPavement`). Each rate whose braking ends inside the edge is checked in turn, and the gentlest whose chord stays on the pavement wins; when none does, the continuation is ruled out. Only the stretch inside the junction is judged, since the aircraft's own position is on pavement: the samples no farther from the node than its own fillets' farthest tangent point (so the judged stretch never reaches the next junction), or, at a node with no fillet, those one turn-about radius or more past the aircraft. When neither the junction's pavement nor such an edge has room, there is no room: a rolling turn about never turns about off the pavement and never falls back to the wide arc. It stops, holds and says unable ([below](#a-rolling-turn-about-with-no-room)).

**A rolling turn about never rolls onto or past a runway holding position.** When the node ahead is a `GroundNodeType.RunwayHoldShort` node (`IsRunwayHoldShortNode`, the test `IsBarNode` uses), the brake leg must end `TurnAboutBarClearanceFt` short of it. That is 2.73R (`TurnAboutReachRadii`, 1 + √3: the main gear's forward reach over the jog and reversal) plus √(R² + d²), d being how far the fuselage nose is ahead of the main gear (the FAA record's cockpit-to-main-gear figure, else half the length).

So the whole aircraft, its wings aside, stays short of the hold line through the turn. It brakes at the taxi rate, else the firm rate, whatever the category (`ShortOfBarBrakeRates`: the one case a jet brakes firmly for a turn about), and neither the node nor anything past it is tried. A turn about on a continuation edge whose far node is a runway holding position keeps the same clearance from that node, at the same rates. `TickTurnAboutBrake` skips `isHoldShortCleared`, so the plan itself places the stop.

#### A rolling turn about with no room

When `PlanRollingTurnAbout` finds no turn about, `HoldUnableToTurnAbout` stops the aircraft and holds it: no room short of a runway holding position even at the firm rate, an overrun of the node ahead with no continuation that has room, a turn about at the node that would leave its pavement with no continuation that has room, or no route node a turning diameter from any turn about it could fly (a route back that ends within 2R, as for a B744 on KOAK W1 edge 0-38).

It brakes to a stop straight ahead (`_turnAboutHold`, played by `TickTurnAboutHold`): at the taxi rate when that stops it short of the node ahead, or, with a runway holding position ahead, with its nose (half its length ahead) short of the hold line; otherwise at the firm rate. It never slides back to a node it overran, and it holds there until a new clearance replaces the route.

A jet away from a runway bar keeps its taxi rate even when that stop runs past the node ahead, as long as the stop ends on the straight edge beyond (`TaxiRateStopStaysOnTheContinuation`): the turn-about taxiway continuing the occupied edge within `ContinuationMaxBendDeg` (`TurnAboutContinuation`) is longer than the overrun past the node; when that edge's far node is a runway holding position, longer than the overrun plus half the jet's length, so its nose stays short of that hold line too; and the stop point, with its chord from the aircraft, stays on the junction's pavement (`StopStaysOnJunctionPavement`). It then stops past the node on that edge.

Otherwise, with no such edge, one shorter than the overrun, a bar inside the stop or a stop off the pavement, the jet brakes at the firm rate and stops short of the node. A jet brakes firmly only to stop short of a runway bar or where its stop straight ahead would not end on the edge beyond. Other categories take the firm rate whenever the taxi rate does not stop them short of the node.

The bar stop holds the nose half the aircraft's length ahead of its centre, the painted-stop convention for an aircraft holding short, while the turn-about clearance from a bar uses the FAA cockpit-to-main-gear figure, because through the turn the nose swings about the main gear.

The pilot says the turn-about refusal once, on the stop's first tick (`SayUnableToTurnAbout`, `PilotResponder.BuildUnableNoRoomToTurnAround`, through `RouteSoloOrRpoTransmission` to a GND or TWR student in solo training; [pilot-phraseology.md](../pilot-phraseology.md)). It is not said as the stop is planned: that happens while the TAXI is dispatched, whose phase context carries neither the solo mode nor the student position, so the call would always reach the terminal as an instructor warning. The navigator logs the stop as a warning: `[Nav] {Callsign}: no room to turn about on {Taxiway} rolling at {Speed} kt; stops in {Stop} ft at {Rate} kt/s with {Room} ft of room to node {Node} (runway holding position: {Bar}), holds and says unable`.

The stop round-trips through the snapshot (`GroundNavigatorPlaybackDto.TurnAboutHold`, with the taxiway named in the unable and whether it has been said, `TurnAboutHoldDto.Taxiway` and `UnableSaid`), so a restore never re-plans it and says unable only if the snapshot was taken before the first tick (`TaxiStartsOnOccupiedTaxiwayTests`).

First a **jog** against the reversal's sense (`PathPrimitiveBuilder.TurnAboutJogDeg`: 60° for an aircraft on the centreline and along it, clamped to 0–90°, not flown under 1°) puts the reversal's turning circle on the centreline.

Then the **reversal**, in the sense `ShouldReverseAgainstShortWay` chose, is played from the jog's exit pose (`PathPrimitiveBuilder.ExitPose`) through `PathPrimitiveBuilder.SlowTurnDirected` to the edge's own bearing toward the node the route reverses to (`BuildRolledOutTurnAbout`, `ReversedEdgeBearingDeg`). It is not aimed at that node: an arc aimed at the node from a radius off the centreline crossed the centreline and had to be turned back.

The turn about is built only when `FindAimNode` finds a node a turning diameter from the jog's exit, not from the aircraft, and the reversal from that exit has a tangent through it (`ReversalReachesAimNode`). Otherwise the comfortable-radius aims run. That node is also where `ReAimPastTheBend` looks for a bend the reversal may cut instead, with no jog ([After a turn about on a taxiway](#after-a-turn-about-on-a-taxiway)).

The turn so spans about one radius either side of the centreline and ends a radius off it, parallel, where a half turn begun on the line ends a whole diameter off it (at the comfortable radius a C172 swung 30 ft off a 25 ft-wide taxiway). A helicopter takes no jog: the reversal alone, which ends a diameter off.

The reversal waits in `_pendingTurnAboutArc` while the jog plays, and `TryEngagePendingTurnAbout` swaps it in on the jog's completion within the same tick, with no heading nudge between the two: the reversal starts on the jog's exit tangent. A reversal re-aimed past the bend carries the painted reversal's aim bookkeeping and re-anchored straight.

A reversal rolled out on the edge's bearing carries no aim bookkeeping; it is marked `_turnAboutReversalOnEdgeBearing` instead.

On its completion `TryLayTurnAboutRollOutLine` lays the straight after it: held on the roll-out bearing to abeam the node (the offset line), or laid on the centreline square to a stop (the square stop line), both [below](#after-a-turn-about-on-a-taxiway). Otherwise the straight re-centres as after any reversal: a free-space leg is re-anchored on the live position (`ReanchorFreeSpaceLine`), and the painted edge driven backwards keeps its own line.

All of it survives a snapshot (`GroundNavigatorPlaybackDto.TurnAboutBrake`, written only while a brake leg is set, `PendingTurnAboutArc`, `TurnAboutReversalPlaying`, `TurnAboutRollsOutAlongEdge`, `TurnAboutReversalOnEdgeBearing`, `TurnAboutRollOutOffsetFt`, `TurnAboutSquareStopLine`).

Pins: `TaxiStartsOnOccupiedTaxiwayTests` (a C172 and a C208 mid-way along KOAK C keep their centre within the 12.5 ft TDG 1A half-width at no more than ω·R on their own turn-about radius, on the free-space leg and on the painted edge driven backwards; a C172 rolling at taxi speed brakes to its pivot speed before the arc, even re-routed within its turn-about radius of the node ahead; with too little room even at the firm rate it rolls past the node ahead and turns about on C beyond, never short of it, and with less than its firm stop to that node it never slides back to it; a B738 turned about by a preset never brakes above its taxi rate; a B738 with no room on KOAK W keeps its taxi rate and stops past the node, and one whose turn about at the node would leave its pavement stops and says unable; a C172 rolling onto the 29 ft W2 edge to a runway bar brakes firm and turns about short of the line; the junction pavement counts a KOAK corner fillet and refuses a brake chord off it; the unable reaches a solo GND or TWR student as a radio call), `TurnAboutFitTests` (the fit, radius and half-width over real FAA records and the fallbacks) and `GroundNavigatorArcRestoreTests` (a restore mid-jog and mid-reversal).

`TaxiwayTurnAboutAimTests` pins the rest: N152SP's turn about on KOAK D, from its recorded ~20 kt, keeps its gear on the pavement. Restored from a snapshot taken mid-brake, N152SP turns about along the same curve as the run never interrupted. The other N152SP pins replay its recorded TAXI and only then slow it, so the route stays the recorded one and the brake leg leaves the turn about where each scenario needs it.

Started 13 ft nearer node 366 at 6 kt, braking at the taxi rate, the straight after the reversal is within the roll-out hold's cap: it rolls out on D's bearing, holds that bearing to abeam the node and turns onto H tangent to H's centreline. Further along D at 15 kt, and as a type with no FAA record at 17 kt, both braking at the firm rate, it re-centres.

It also pins a C172 at a real acute branch re-aimed past the junction, and not when it holds short there; the stops at a bar; and restores mid-reversal, on the held straight, mid node turn and on the square stop line.

Each slowed N152SP pin first asserts the brake rate its navigator planned and where the straight after the reversal falls against the hold's cap.

**The aim node is the first one the arc cannot overshoot, and never past a bar.** Every node aim shares `FindAimNode`, which walks forward from the to-node of the segment it is given (the current segment, or the leg out of the bend a turn about is re-aimed past) to the first node at least a turning *diameter* (`2r`) from the point it is given: the aircraft, or a turn about's jog exit.

A node inside the turning circle has no tangent at all; a node just outside one is reached by an arc longer than the leg running to it, so the arc rolls out past the node and pure pursuit re-acquires a line already behind the aircraft. At SFO gate G10 the first leg is 21 ft and the arc 169°, and chasing that node cost another 84° of turn — aiming at the first node the arc cannot overshoot removes it.

The legs in between are then retired by `TryRetireLegsTheArcAimedPast` when the arc completes: it advances `TaxiRoute.CurrentSegmentIndex` and re-runs `SetupSegment`, which cannot cascade because `SetupSegment` clears the aim bookkeeping before it builds anything.

**That advance does not run `TaxiingPhase.ArriveAtNode`** — AT-ground/AT-taxiway triggers, hold-short insertion and the pre-cleared-crossing handoff all live there — so the walk stops at a bar whatever the distance: any node the route carries a `HoldShortPoint` at (cleared or not; a cleared crossing still hands off to `CrossingRunwayPhase` on arrival) or any `GroundNodeType.RunwayHoldShort` node.

It aims *at* the bar, where the arrival happens normally. **The walk can end on a fillet's far node, so the hand-over is a straight to it, never the fillet's Bézier.**

When the aimed segment is a fillet (`GroundArc`), the arc rolls out on the straight line through the fillet's to-node from somewhere off the curve, so the navigator flies that line instead of the curve (`InstallAimedLineOverFillet`, logged `fillet A->B flown as the aimed line`): from the arc's pending-primitive swap when the aimed segment is the current one (`_nodeAimSegmentIndex`), and from `TryRetireLegsTheArcAimedPast` when the retirement lands on it.

The straight arrives on the fillet's to-node through the ordinary straight arrival, so the owning phase's node arrival still fires. SFO gate E2 (SKW5707 in `5d33df162626.zip`, a `TAXI T7B $7B` fired mid-push): the arc aimed past the free-space leg to the T7B–RAMP fillet's far end, and the fillet became current with the aircraft ~50 ft from its start and ~27 ft abeam it; played as its Bézier, that tripped I8 (pin: `NodeAimedEntryOntoFilletTests`).

**A free-space first leg is aimed at its node, not at its bearing.**

When the segment's from-node is virtual — the approach leg `TaxiApproachLeg` prepends to a route whose first graph node the aircraft is not standing on (a plain `PUSH` leaves it ~100 ft out on the apron), or a ramp-lane cut — the arc's exit tangent is solved to pass *through a route node ahead* (`FindAimNode`, above — the leg's own to-node when the arc cannot overshoot it, otherwise the first node beyond that it cannot) via `PathPrimitiveBuilder.SlowTurnToPoint`.

That is the Dubins arc-then-tangent-line, at the comfortable main-gear turn radius since there is no painted outgoing centerline to exit onto; the direction with the smaller sweep wins, and sweeps over `PathPrimitiveBuilder.MaxAimSweepDeg` = 270° fall back to the bearing aim. The cap is geometric headroom under the 360° orbit guard, not an airmanship limit; the real limits on a powered ground turn are swept envelope and taxi power, which a sweep angle does not express.

The straight's line is then re-anchored at the arc exit (`_segmentFromLat/Lon` = the live position, also on a snapshot restore, where the virtual node is rebuilt at its *original* position), so the straight runs exactly onto the node and the fillet that follows starts on-centerline. Aiming at the bearing alone left the arc exit ~30 ft abeam the line and the fillet's closed-form playback then wrote the aircraft onto its start point — the OAK gate-15 "rotated in place, then snapped to T" report.

**Adaptive rounding radius.** The entry-alignment slow-turn and the incoming tangent-rounding both use an *adaptive* radius (`GroundNavigator.AdaptiveCornerRadiusFt`), not a fixed main-gear turn radius.

When the approach or departure leg is shorter than the comfortable tangent length `T = r·tan(δ/2)` — two junctions closer than `T` apart, e.g. SFO M2 between the B and A crossings (~22 ft for a 118° turn that wants 41.6 ft) — the radius tightens toward a category **tight-turn floor** (`CategoryPerformance.TightTurnFloorRadiusFt`) so the arc still **exits on the outgoing centerline**.

The floor is the path radius of the main-gear axle midpoint, not of the inner main gear: a jet's 15 ft is ~74° of nose-wheel steering on a B737-800, with the inner main gear ~5.6 ft from the turn centre.

The incoming arrival threshold (`StraightArrivalThresholdNm`) relaxes its `0.45·leg` cap to the whole leg only on such a tight leg, so the rounding can begin at the leg start. Without this, a fixed 25 ft arc off a 22 ft leg finishes ~26 ft wide, and pure-pursuit limit-cycles the corner on the short outgoing segment for ~45 s. This is judgmental oversteer (Boeing FCTM / AC 150/5300-13B): the nose may bulge wide of centerline mid-arc but rolls out aligned. Aviation-reviewed.

A node turn laid from a turn about's offset line replaces both: its arrival point and its radius are fitted to the offset ([After a turn about on a taxiway](#after-a-turn-about-on-a-taxiway)).

### After a turn about on a taxiway

**Re-aim past the bend.** Before rolling a reversal out on the edge's bearing, `SolveTaxiwayTurnAbout` tries `ReAimPastTheBend` at the node `FindAimNode` found a turning diameter from the jog's exit. `ReAimBoundFt` admits it only when a leg follows that node and the node is no bar (`IsBarNode`): a cut past a bar would drive through the hold short without arriving at it.

The bend there must also run against the reversal's sense and be sharper than `ReAimMinBendDeg` = 90°, or the route would turn about to the node only to turn most of the way back.

The type must have an FAA main-gear width (`FaaAircraftDatabase`). The bound is the type's taxiway half-width (`TurnAboutFitResult.HalfWidthFt`: the layout carries no design group, so the check assumes a taxiway of the type's own, and 12.5 ft, half a TDG 1A taxiway, for a type with no FAA record) less half that width, and must be positive.

The re-aimed reversal is solved through `SlowTurnToPointDirected` from the aircraft's own pose, with no jog, to the first node on the leg out of the bend at least 2r from the aircraft (`FindAimNode` from that leg, still stopped at a bar). It is kept only when every sample, 1 ft apart (`CutSampleSpacingFt`), of the arc and of the straight from its exit to that node lies within the bound of the turn-about edge's centreline or an outgoing leg's (`PavedCentrelines`, `CutStaysOnPavement`).

A cut that leaves those strips crosses the unpaved wedge between the two taxiways, and a curved outgoing leg is not checked, so it refuses the re-aim. A kept re-aim commits the painted reversal's aim bookkeeping (aim `turn-about-reaimed`), and `TryRetireLegsTheArcAimedPast` retires the legs it was aimed past on completion. At KOAK D the cut onto H leaves the C172's 8.3 ft bound, so N152SP turns about on D.

**The offset line** (also the roll-out hold). A reversal rolled out on the edge's bearing ends off the centreline on the side opposite its own sense. `HoldsRollOutBearing` decides when the plan is built whether the straight after it holds that bearing to abeam the node (`_turnAboutRollsOutAlongEdge`) instead of steering out to the centreline only to turn back in at the node.

It holds only when the route then turns more than `EntryAlignmentThresholdDeg` (45°) to that side onto a straight edge, the node is no bar, and the type has an FAA main-gear width.

`RollOutHoldFits` adds two more: the offset (a radius, a diameter for a helicopter) plus half the main-gear width stays within the type's taxiway half-width of the centreline (the same `TurnAboutFitResult.HalfWidthFt`), and the straight from the reversal's exit to abeam the node is no longer than `RollOutHoldMaxFt`: max(6R, S + ρ·tan(|δ|/2) + R), R the turn-about radius, ρ the comfortable main-gear radius (`CategoryPerformance.MainGearTurnRadiusFt`), δ the route's next turn at the node, and S = 2ρ·sin φ with cos φ = 1 − d/(2ρ), d the held offset.

S is the run an S-turn at ρ needs to bring the offset back to the centreline, so a straight no longer than that S-turn, the node turn's tangent length and a radius has no room to re-centre in. For the C172 into the turn onto H at KOAK node 366 the cap is 39 ft, where 6R alone is 25 ft.

Over a longer straight the pilot re-centres first. The bar and missing-gear-width refusals are logged, and so is the fit check's result.

On the reversal's completion `TryLayTurnAboutRollOutLine` lays the straight from the aircraft along its exit bearing, with the target moved to the point abeam the node, and records the offset there in `_turnAboutRollOutOffsetFt`. An aircraft already past abeam the node re-centres instead. The reversal takes no end-of-arc nudge: the bearing it rolled out on is the one it keeps.

**The arrival threshold on the offset line.** With a sharp corner ahead, `OffsetLineArrivalThresholdNm` replaces the plain tangent length R·tan(δ/2) with R·tan(δ/2) − d·cot δ (d the offset, δ the turn), clamped to the final-node floor and the held line's length. That lays the node turn tangent to both the offset line and the outgoing centreline.

The plain tangent length leaves the arc d·cos δ off the outgoing centreline, inside it for a bend under 90° and across it for one over 90°: N152SP, 8 ft inside a 104° bend onto H at KOAK, ended 2 ft across H's line and was steered 15° past H's bearing to get back.

**The node-turn radius from the offset line.** A sub-tick of travel overruns the tangent point by up to several feet, and an arc of the planned radius begun late crosses the outgoing centreline (N152SP: 5 ft off H). `SetupSegment` therefore keeps the offset into the node turn, and `OffsetLineNodeTurnRadiusFt` fits its radius from where the aircraft stands.

The fit is r = T' / tan(δ/2), with T' = d·cot δ + a and a how far the node still lies ahead along the held line, clamped between `TightTurnFloorRadiusFt` and the planned adaptive radius. That turn takes no end-of-arc nudge either, which would turn it off the centreline it exits on. The offset ends when the node turn completes (`CompleteEntryAlignment`, before any leg retirement sets up a later segment), or at the next segment set-up when no alignment turn is needed.

**The square stop line.** When the straight after a rolled-out reversal ends in a stop (`_currentNodeRequiredSpeed` ≤ 0: an uncleared hold short, or the route's end), `TryLayTurnAboutRollOutLine` lays it on the segment's own centreline through the stop, from abeam the aircraft (`LayStraightSquareToStop`).

The aircraft re-acquires the centreline and stops on it square to the bar, not on the chord from the off-centre roll-out. A stop not ahead on that line leaves the straight re-centred as after any other reversal.

On that line (`_turnAboutSquareStopLine`), `TickStraight` steers in the last look-ahead window at a point along the line past the stop, not at the stop, so a residual offset re-acquiring the centreline does not become the heading the aircraft stops on.

**A hold short reached on the held straight.** `OverrideTargetPosition` takes the `PhaseContext` because it re-lays a held straight: when a stop is aimed at while the offset line is held (an offset set, no primitive pending, a straight playing), it drops the offset and lays the straight on the edge's centreline through the stop, from abeam the offset line's start, with the square-stop steering.

A stop not ahead on that line still drops the offset and leaves the line as it is; both outcomes are logged.

Its callers are `TaxiingPhase.AimAtPaintedBar` (at segment set-up, on a target change, and on `NotifyHoldShortsChanged`'s re-aim) and `CrossingRunwayPhase`. A node turn laid from the offset line keeps the offset when its own segment's bar is aimed at: that bar lies on a straight that does not follow a turn about.

### Speed: corner-speed limits and backward-propagated braking

The navigator never overspeeds into a future turn. `BuildSpeedConstraints` (`GroundNavigator.cs:988`) runs at every `SetupSegment`:

1. Sets `_currentNodeRequiredSpeed` from `CornerSpeed(category, SingleCornerTurnAngle(...), legIn, legOut)` (0 for stops — uncleared hold-shorts and the last segment).
2. Forward-walks remaining segments collecting `(pathDist, requiredSpeed, nodeId)` constraints: one per sample of each future arc's **local cornering-speed profile**, one per future node's corner speed, and 0 at the first uncleared hold-short's **painted stop** (then stops).

   The arc profile is `GroundArc.SpeedProfile(category)`: 17 evenly spaced parameter samples, each `SafeSpeedForRadiusKts` at the local radius of curvature — a lateral-acceleration cap `v = √(a_lat·r)`, `a_lat ≈ 0.13 g`, additionally capped by `CornerSpeedForAngle` and the yaw-rate coupling `ω·r`, floored at `SlowTurnSpeedKts`. `MaxSafeSpeedKts` is the same formula at the tightest point.

   Sampling the profile rather than pinning the arc's tightest speed at its entry matters for the distorted cubics the fillet generator emits at asymmetric junctions. A long gentle sweep with one tight stretch (SFO junction J133's B bend: 107 ft, 56°, 22 ft minimum radius) is braked for only where it is tight instead of being crawled at 3 kt end to end.

   The stop's zero sits at the bar node's along-route distance minus `TaxiRoute.HoldShortSetbackNm` (the setback measured back along the route's chords) minus `SetBackStopMarginFt` = 2 ft, because a taxiway or runway stop can sit several segments before its bar node ([hold-short-placement.md](./hold-short-placement.md)).

   For a set-back stop, `TaxiingPhase.TryHoldAtSetBackStop` caps the navigator's `MaxSpeedKts` to that curve every tick before the navigator runs, and `TaxiingPhase.CapAtUnclearedBar` caps the published target after it ([Stopping at an uncleared bar](#stopping-at-an-uncleared-bar)). The navigator publishes no speed on a node-arrival sub-tick, which over a run of short segments left the aircraft several knots above its curve.

   The hold is taken within 3 ft of the stop at ≤ 3 kt by setting the speed to 0, never moving the aircraft, so at rest the nose is at or behind the bar; another aircraft already holding at that bar makes this one wait where it stops. After release, a taxiway bar's phase drives the remaining segments to the junction, while a runway bar at the route's end finishes the route at the stop and line-up plans from there.
3. Backward-propagates a kinematic decel curve (`v = sqrt(v_next² + 2·a·d)`) between adjacent constraints and into the current node's required speed — skipped when `SlowdownDecelRateKts` is set (below).

`ComputeTargetSpeed` (`GroundNavigator.cs:956`) per-tick takes the min of: the brake curve from the current node's required speed, every future constraint (skipping a bar's stop once that bar is cleared; the route-end stop is never skipped), and a quadratic heading-error scaling (`speedFraction`, full speed at 0° error down to 3 % at ≥ 90°). A safety backstop in `TickStraight` caps target speed so the aircraft can't cover more than 80 % of remaining distance in one tick (overshoot prevention).

**Two braking rates: slowdowns and stops.** `DecelRateKts` (null = `TaxiDecelRate`) is the rate for the whole route. `SlowdownDecelRateKts`, when set, applies only to the in-route slowdowns — corner and arc speeds, and the arc cap — while every stop (the route end, and each bar not yet cleared) stays on its own braking curve at `DecelRateKts`'s rate.

With it set, `BuildSpeedConstraints` skips the back-propagation (one rate would blend the two) and `SplitRateBrakingLimit` plans each constraint on its own curve at its own rate; the published `DesiredDecelRate` is the stop rate on a tick where a stop's curve sets the target (`_stopCurveBinds`), the slowdown rate otherwise.

A softer or firmer slowdown rate therefore never moves a stop. `RunwayExitPhase` sets it on the turn-off to the rate the rollout chose the exit with ([landing-and-runway-exit.md](../landing-and-runway-exit.md) § The turn-off keeps the rollout's rate) and re-applies it before every tick and segment set-up; it is not serialized.

**Current-arc profile cap (`_currentArcProfile`, issue #236).** The braking curve treats a corner arc's speed only as a *future* approach limit, so once the aircraft is *on* a long arc — entered slow, next corner far ahead — the curve permits it to accelerate mid-arc toward taxi max and brake hard on the far side (observed ~19–30 kt through a 72 ft-radius fillet).

`ComputeTargetSpeed` therefore also caps the *current* segment when it is a `GroundArc` by `ArcProfileLimitKts`: the tightest braking curve onto the remaining samples of the arc's `SpeedProfile` (oriented from the segment's from-node by `OrientedProfile`, keyed by `_bezierTraveledFt`), so the aircraft is never faster than the painted radius allows where it is, slows in time for a tighter stretch further along the curve, and is not held at the tightest point's speed over a gentle sweep.

Recomputed each `BuildSpeedConstraints`, so it round-trips through a snapshot for free; null on straights. This is a sim-wide invariant (all corner arcs), not scoped to lane changes.

Corner speeds, turn rate, accel/decel, main-gear turn radius, and slow-turn speed are all category-specific in `AircraftCategory.cs` (`CornerSpeedForAngle`, `GroundTurnRate`, `TaxiAccelRate` (1.0 for every category), `TaxiDecelRate` (5/5/2/2), `MainGearTurnRadiusFt`, `SlowTurnSpeedKts = 3.0`).

Taxi max speed is `TaxiSpeed(category)`, multiplied by `TaxiExpediteMultiplier = 1.3` when the aircraft is expediting, or replaced outright by a controller-commanded cap when `Ground.CommandedTaxiSpeedKts` is set (`SPD <n>` while taxiing — `TaxiingPhase.OnTick`; clamped to `[MinCommandedTaxiSpeedKts, MaxCommandedTaxiSpeed(cat)]`, mutually exclusive with expedite).

This only sets the straight-segment ceiling — the corner/arc/braking/conflict caps above still win. These are the realism constraints: turn rate bounds heading change per tick, and entry-alignment radius floors at the main-gear turn radius so the navigator never asks for a physically impossible turn.

**The turn about on a taxiway already turns on the type's own radius; the other ground turns do not yet.** The turn about uses R = max(MGW/2, 0.466 × wheelbase) (`TurnAboutFit`, [above](#entry-alignment-threshold)), which can fall below the category value (a C172's 4.2 ft against 8).

Decided, not built yet: every other ground turn's main-gear radius is to be `max(categoryValue, 0.466 × FaaAircraftRecord.WheelbaseFt)` (WheelbaseFt / tan 65°), the category value when the type has no wheelbase, upward only: A388 ≈50 ft, B77W ≈47, B744 ≈39, B763 ≈35, B738 stays 25.

An A388 (wheelbase about 100 ft) cannot turn on 25 ft. Because it only raises the radius, `GeometricAdmissibility.MinSteerableArcRadiusFt` (the smallest category radius) is unchanged. It changes about a dozen call sites' signatures and every heavy's ground turns, so its replay desyncs are triaged apart from other ground retunes.

### Stopping at an uncleared bar

An uncleared bar's painted stop puts the centre at the stop and the nose at the holding position marking (AIM 2-3-5.a.1). `TaxiingPhase` caps the published speed for it beyond the navigator's own plan, because the navigator's plan alone can carry the nose over the marking, and because a re-route, `HOLD` or `GIVEWAY` can leave the aircraft inside its taxi-rate stopping distance of the stop.

**The stop curve is led by a tick.** The navigator reads its braking curve where the aircraft stands at the start of the sub-tick, and physics then moves the aircraft a further `v·dt` before the next read. A target taken from that curve trails the aircraft by a tick, and flown at the full brake rate the speed that lag leaves over is never lost again: about `v·dt` past the stop, 8 ft from a piston's 20 kt.

`GroundStopBraking.StopCurveKts` therefore reads the curve where this tick's travel leaves the aircraft, aiming `v·dt` short of the curve's zero point. Its zero is `SetBackStopMarginFt` (2 ft) short of the stop point it is given; the taxi-rate cap passes a stop on the bar's own segment 2 ft farther on, so the curve reaches zero at the navigator's own aim: the stop itself on the bar's own segment, 2 ft short of a set-back stop.

**The cap** (`TaxiingPhase.BrakeForUnclearedBar`) runs after the navigator ticks, since the navigator republishes the brake rate every tick.

It judges the first uncleared bar ahead by `GroundStopBraking.ChooseStopBraking` against the painted stop: the led taxi-rate curve when the taxi rate makes the stop; when only the firm rate (`CategoryPerformance.ExpediteExitDecelRate`) does, that rate published as `DesiredDecelRate` and the led curve at it; when not even the firm rate does, the firm rate and a zero target.

An un-held tick applies it as `CapAtUnclearedBar`. A target physics already cleared on arrival at its goal (`FlightPhysics.ArriveAtGoal`) is capped from the current speed, never raised to the cap. A bar already called unmakeable (`HoldShortPoint.Unable`) keeps its moved stop, and a route-incomplete end keeps the navigator's routine braking; the cap skips both.

**The hold contract near an uncleared bar.** A held aircraft's pinned speed (`HeldSpeedKts`: zero for `HOLD` and a `GIVEWAY` with no give-way point, the give-way curve otherwise) is capped by the same braking after any node arrival (`CapHeldSpeedAtUnclearedBar`). The braking therefore steps up in three stages: the taxi rate; the firm rate when only that makes the stop; and, for a runway bar whose line is not yet lost only, a last-resort dead stop.

The dead stop is taken on the last tick before the nose would reach the marking even at the firm rate (the stop within one tick's max-effort travel plus `SetBackStopMarginFt` + 1 ft), held or not. It is logged as a warning: `[Taxi] {Callsign}: stopped dead short of {Target} at node {NodeId}, nose {ToStop:F1} ft from the holding position marking at {Speed:F1} kt: no brake rate made the line`.

A taxiway bar never takes a dead stop: the firm rate brakes the aircraft toward its stop, and the hold is taken where it gets there (by `TryHoldAtSetBackStop` at a crawl for a set-back stop, by `ArriveAtNode` for a stop on the bar's own segment). The cap overrides a `GIVEWAY`'s rule that it never stops dead: a give-way point past an uncleared runway bar still stops the aircraft at the bar, dead on the last tick if nothing else makes the line.

**A runway line already lost takes a firm stop, then the hold.** A re-route given with the nose already past an uncleared runway bar's marking cannot stop it short. The aircraft brakes at the firm rate as soon as it can, to keep off the runway's pavement, and is never stopped dead from taxi speed. An arrival at the bar's node still faster than 3 kt (`IsRollingOntoUnclearedBar`), held or not, waits while it brakes, so the hold is taken at a crawl.

The overrun is warned once per phase: `[Taxi] {Callsign}: nose {PastFt:F0} ft past the hold line for {Target}, braking from {Speed:F1} kt` while it brakes toward a bar ahead, and `[Taxi] {Callsign}: nose {PastFt:F0} ft past the hold line for {Target}, stationary` for a hold taken standing still past the line (a set-back stop, or the start bar below).

**The bar the route starts on.** A bar on the route's start node is no segment's to-node, so `ArriveAtNode` never sees it. `TaxiingPhase.PassedStartBar` finds the nose past an uncleared runway bar there when the aircraft heads within 45° of the first segment's departure bearing, its nose (the centre projected half the type's length ahead) is at or past the line through the node square to that bearing, and it is within 150 ft of the node plus its firm-rate stopping distance.

The finding is latched on the bar's node (`PassedStartBarNodeId`, snapshotted on `TaxiingPhaseDto`), so the firm-rate stop it starts is never let go part-way as the aircraft slows and the stop carries it away from the node. Clearing the bar drops the latch, and taking the hold clears it.

`TaxiingPhase.TryHoldPastStartBar` runs every tick ahead of the other start checks: while the latch holds, the aircraft brakes at the firm rate and takes the bar's hold once it is down to one sub-tick of that braking, on whichever segment it has reached.

Running every tick lets an `HS` that re-arms the start bar later, and a phase restored from a snapshot, take the hold alike. A route that starts moving past the line warns `route starts on the uncleared hold-short of {Target} … stopping at the firm rate` on its first tick.

With the nose short of the start bar, the start-node hold is `TryHoldAtRouteStartNode`'s: it caps the navigator to a braking curve that reaches zero 15 ft short of the node, and takes the hold within 150 ft of it at 3 kt or less ([hold-short-placement.md](./hold-short-placement.md)). `HoldInsideStoppingDistanceOfBarTests` pins the whole ladder at KOAK: the runway 33 bar on C, and the taxiway E and A bars.


## Per-tick walkthrough

### `SetupSegment` (called on each segment transition)

`GroundNavigator.cs:325`. Runs once when the route advances to a new segment:

1. Compile the current segment to a primitive (`PathPrimitiveBuilder.FromSegment` — returns `PathPrimitiveStraight` or `PathPrimitiveBezier`).
2. Set target node / lat / lon from the segment's to-node; reset `PrevDistToTarget`.
3. **Entry-alignment check** (above). If the start heading delta exceeds the threshold and the segment is long enough, swap in an alignment slow-turn and stash the real primitive.
4. Otherwise install the real primitive.
5. `BuildSpeedConstraints` (`GroundNavigator.cs:424` calls it; defined at `988`) — speed profile for this and all downstream segments.

`TaxiingPhase.SetupCurrentSegment` then overrides the target lat/lon with a hold-short offset position when the to-node carries an uncleared hold-short (`AimAtPaintedBar` → `GroundNavigator.OverrideTargetPosition`), so the aircraft stops at the painted bar, not the intersection node.

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

1. **Arrival check** (`GroundNavigator.cs:634`): arrives when `distNm ≤ arrivalThreshold`, or overshoot, or by the **advance-on-pass rule**. Threshold is tight (`FinalNodeArrivalThresholdNm ≈ 1.8 ft`) on the last segment, stop targets, short edges, or when the next segment is a Bézier arc; otherwise loose (`NodeArrivalThresholdNm ≈ 91 ft`).

   The advance-on-pass rule: the aircraft also arrives when its *along-track projection* (foot-of-perpendicular distance from the segment start) reaches or passes the to-node (`alongNm ≥ edgeLengthNm`), independent of cross-track.

   On the centerline this coincides with normal arrival; off it (a pure-pursuit overshoot of a short chord) it advances to the next segment instead of circling the node. Excluded for stop targets and the last segment (must arrive precisely, not pass). On arrival, nudge heading toward the next segment bearing (bounded by turn rate) and return `ArrivedAtNode`.
2. **Pure-pursuit steering** (`GroundNavigator.cs:680`) toward the look-ahead point, with the pre-turn blend.
3. **Speed**: `ComputeTargetSpeed` (`GroundNavigator.cs:753` calls it; defined at `956`) → 80 %-distance backstop → `ClampBySpeedLimit` → published as `TargetSpeed` (+ `DesiredDecelRate`); physics integrates.

### `TickBezier` / `TickSlowTurn` step order

1. I7 speed floor: if below `ArcSpeedFloorKts`, set target heading to the current tangent, set speed, bail.
1. (first tick only) I8 entry capture: project the live position onto the curve (Bézier) and record the entry offset — see Invariant I8.
2. Advance the curve/arc: for Bézier, by arc-length `dt = v·dt / |B'(t)|` updating the parameter; for slow-turn, by `dAngle = (v·dt)/r` (clamped to remaining sweep), signed by turn direction.
3. Write `Position` and `TrueHeading` directly from the evaluated curve/bearing-from-centre (I2).
4. Mirror heading into `Targets` (so physics doesn't fight the closed-form state) and set speed — `ComputeTargetSpeed` for Béziers (participates in the constraint system), the primitive's `MaxSpeedKts` cap for slow-turns (they do not).
5. When complete (Bézier `_bezierT ≥ 1.0`, slow-turn remaining sweep ≤ 0.01°), nudge heading toward the next bearing and return `ArrivedAtNode`.

   The slow-turn nudge is bounded by `CategoryPerformance.GroundYawRateOnRadius` on the arc's own radius only when the arc is a turn-about reversal (`_turnAboutReversalPlaying`), and by the comfortable main-gear rate (`GroundYawRateAtSpeed`) otherwise.

   `TakesEndOfArcNudge` gives no nudge at all to three slow turns. A turn-about jog hands straight on to its reversal (a turn-about arc pending). A reversal whose straight holds its roll-out bearing (`_turnAboutRollsOutAlongEdge`) keeps that bearing. The node turn laid from a turn about's offset line (`_turnAboutRollOutOffsetFt` set) exits on the outgoing centreline, which the nudge would turn it off.

### Route advance in the owning phase

On `ArrivedAtNode`, `TaxiingPhase.ArriveAtNode` (`TaxiingPhase.cs:221`) fires AT-ground/AT-taxiway triggers, checks for a hold-short (insert `HoldingShortPhase`) or runway crossing, then advances `CurrentSegmentIndex += 1`. If the route is now complete it inserts the terminal phase; otherwise it calls `SetupCurrentSegment` for the next segment.

---

## Caveats & gotchas

### Fillet Bézier orientation and reverse traversal

A `GroundArc` stores its Bézier with `Nodes[0]` = P0 and `Nodes[1]` = P3, with **no implied direction** (`AirportGroundLayout.cs:173`). When the route traverses the arc backward (from-node = `Nodes[1]`), `DirectionalEdge.DepartureBearing` / `ArrivalBearing` flip the tangent bearing by 180° (`AirportGroundLayout.cs:477`).

`PathPrimitiveBuilder.BuildBezier` orients the curve for traversal at build time: the forward direction uses the stored Bézier as-is (`t=0` at the from-node), and reverse traversal reverses the control points so `t` still runs from-node → to-node (`PathPrimitiveBuilder.cs:103–106`). The historical "270° spiral" bug came from a reverse-traversed arc landing the aircraft 180° from the next walk step; the orientation fix ensures the tangent bearings are consistent with the actual curve direction.

### Chord-chain speed limiting via turn-rate feasibility

`SingleCornerTurnAngle` (`GroundNavigator.cs:1190`) reads a single corner's bearing change and prices it through `CornerSpeed` (`GroundNavigator.cs:1165`), which takes the lower of the angle comfort cap (`CategoryPerformance.CornerSpeedForAngle`) and a turn-rate-feasibility cap `v ≤ ω·½L/θ` (where `L` is the shorter adjacent leg, `ω` is the ground turn rate, and `θ` is the turn angle in radians).

On a chord chain the short legs drive that cap down even though each per-bend angle is gentle; because `L ≈ R·θ`, the cap reduces to `v = ω·r` — independent of how finely the curve is chorded — so it converges on the curve's true geometric speed.

Curved ramp taxiways (SFO CG/SIG, curved apron sections) **do** arrive as chord chains from the fillet generator, so this feasibility cap is load-bearing for realistic speeds through such sections (a 50 ft-radius / 90° apron curve should taxi at ~9 kt per 0.13 g lateral-accel, not 30 kt full speed). The gate applies across the whole angle range (lowered to a 1° near-collinear epsilon) so even shallow chord-chain bends get capped. Guarded by `GroundNavigatorCornerSpeedTests`.

### Short-connector transit (steady speed across a lane change)

`DetectShortConnector` (`GroundNavigator.cs`, called from `BuildSpeedConstraints`) recognizes a *short connector*: the current **straight** segment sits in a straight run bracketed on both ends by a turn (a `GroundArc` neighbor, or a `> ConnectorCornerThresholdDeg = 30°` heading change between straights) whose total length is `≤ ShortConnectorMaxLenFt = 250 ft`.

This is a lane change across parallel taxiways via a short cross taxiway — the SFO **A→F1→B** case (issue #236): A and B run parallel ~236 ft apart, F1 is the ~perpendicular connector, and the ~228 ft straight F1 run between the two ~90° corners qualifies.

While on such a run, `ComputeTargetSpeed` caps the target to `_connectorFlowSpeedKts` so the aircraft flows through at a steady low speed instead of accelerating on the connector straight (the braking curve alone permits ~20 kt) and braking back down for the second turn. A real crew flies a short connector as one continuous low-speed maneuver, not settle-wings-level-and-accelerate (AC 120-74B; aviation-reviewed).

The cap is **self-limiting** and only bites on genuinely sharp corners: `_connectorFlowSpeedKts` = the higher of the two bracketing corners' comfortable speeds, where a sharp (`> EntryAlignmentThresholdDeg`) bracketing corner — rounded by a main-gear-turn-radius slow-turn — contributes `TurnRateLimitedSpeedKts(cat, MainGearTurnRadiusFt(cat))` (~5 kt for a jet) and a gentle (30–45°) one contributes the higher `CornerSpeedForAngle`.

So a gentle bracketing turn yields a high (no-op) cap and the length window alone never slows a run; only sharp ~90° lane-change corners pull the speed down.

It caps the speed on the connector **straight**; the entry/exit **turns** of the lane change are rounded by the `#236` pathfinder follow-up (the transition-arc exemption in `SegmentExpander` routes the turn over the taxiway `[A,F1]`/`[B,F1]` fillet arc instead of a square junction pivot; see [`./pathfinder.md`](./pathfinder.md)), and the **current-arc flat cap** (above) holds those arcs to their safe cornering speed. So the whole A→F1→B maneuver now flows smooth *and* slow.

Both ends of the connector must be a turn, so a single corner or a from-rest spot-exit pivot (one turn then a long straight) is unaffected. Recomputed each `SetupSegment`, so it round-trips through a snapshot for free. Guarded by `GroundNavigatorTests.ShortConnector_HoldsSteadyLowSpeed_NoSurge` (unit), `Issue236SfoAF1BConnectorTests` (SFO single-aircraft integration — no surge across the transit), and `Issue236LaneChangeArcTests` (the pathfinder routes A→F1 over the `[A,F1]` arc).

### Entry alignment fires at any segment start (threshold 45°)

`GroundNavigator.EntryAlignmentThresholdDeg = 45.0`. The entry-alignment slow-turn is the catch-all for any misaligned segment start (post-pushback U-turns, mid-route corners where convergence might diverge). It fires at any segment start with a heading delta over the threshold, independent of route position — there is no route-entry gate.

A second threshold sits above it: past `ReversalEntryThresholdDeg = 135.0` the entry is a reversal rather than a corner, and it is turned the way that unwinds into the route's next turn and aimed at a node rather than a bearing; a turn about on a taxiway is instead re-aimed past the bend or rolled out on the edge's own bearing (see **Entry-alignment threshold** above).

### Orbit guard (two-layer defense)

When pure-pursuit can't close the last few feet (bearing-to-target rotates faster than the aircraft can turn), the aircraft can enter a limit cycle around the node. Two mechanisms prevent indefinite circling:

1. **Advance-on-pass** (`TickStraight`, line 660): geometric, not a timeout — the aircraft advances to the next segment as soon as its along-track projection passes the to-node. This breaks the limit cycle the moment the aircraft passes the node, even off the centerline.
2. **Hard orbit invariant** (`Tick`, lines 469–486): net signed turn within a single segment (reset on every segment/primitive start) may never reach `OrbitTurnLimitDeg = 360°` (no legitimate single-segment maneuver does: arcs sweep <180°, slow-turns <180°, straights ≈0°). On breach it **throws in tests** (`ThrowOnOrbit`, set by the test module initializer) and **logs + force-advances in the shipping app** (never crashes a live session).


### Short-approach base + landing geometry

The runway-exit/landing side has its own tuning that interacts with the navigator at the turn off the runway. Do not bump piston `PatternTurnRate` or shorten the virtual inbound segment without re-reading [`../landing-and-runway-exit.md`](../landing-and-runway-exit.md) — a longer virtual segment gives the navigator better turn anticipation, and shortening it produces heading reversals.

### Ground conflict detection / mutual proximity-stop deadlock

`GroundConflictDetector` (`src/Yaat.Sim/GroundConflictDetector.cs`) runs once per physics sub-tick, before every aircraft's phase tick (`SimulationWorld`), and caps `Ground.SpeedLimit`, which the navigator honors.

Physics applies that limit instantly (`FlightPhysics.UpdatePosition`), so a rule that slows rather than stops must make its limit brake-feasible itself: the tug limits (`TowbarBrakingFloorKts`) and the convergence slowdown floor the limit at what the aircraft can shed in one pass (the convergence at the category taxi brake rate, or at the max-effort `ExpediteExitDecelRate` when the routine stop no longer fits before the stop ring); only the 0-kt stops are meant to be instant.

It classifies each aircraft pair into exactly one `PairKind` and resolves it with a uniform **one-holds-one-goes** rule: exactly one aircraft of a close-range conflicting pair is held while the other proceeds — it never stops both. The deterministic holder per kind:

- **SameEdgeHeadOn** (opposite directions on one edge): holder = more remaining route, tie-break by callsign (`ResolveSameEdgeHeadOn`) — the earlier "both stop" rule deadlocked once two routes resolved onto one single-lane segment.
- **Converging merge** (routes share an upcoming node from different edges, then often continue on one shared lane): holder = the aircraft **farther** from the shared node; the nearer one (the merge-order leader) proceeds through the intersection first and the holder follows in trail.

  The closing-proximity safety net is merge-aware (`ApplyConvergenceClosing`) — without it the symmetric closing check pinned **both** aircraft at the merge (the OAK U/W node-17 JSX177-vs-SWA897 deadlock), because at a true merge there is no lateral room for the wingspan-bypass to open. Distance-to-the-shared-node is an acknowledged stand-in for controller merge precedence (real sequencing is closer to time-to-intersection); aviation-reviewed against 7110.65 §3-7-2/§3-7-3 (FOLLOW/BEHIND).
- **Crossing** (paths cross, no shared node): mutual stop → one aircraft holds while the other proceeds. The holder is chosen by `ChooseMutualStopHolder`, in order: a live-traffic shadow is never held; otherwise, when the geometry shows a clear follower — the other aircraft nearly dead-ahead of it (off-nose angles differ by ≥ `FollowerLeadOffNoseMarginDeg`) — the follower holds and the lead (which moves away as it proceeds) goes first, the auto equivalent of FOLLOW/BEHIND (7110.65 §3-7-2.a). This stops a follower being released *through* the stopped aircraft it is trailing when both merge onto one lane (the OAK TE `949→1` drive-through, issue #224).

  Near-symmetric geometry (a true perpendicular crossing / head-on) is next decided by FOLLOWG: when exactly one of the pair is a FOLLOWG follower (current phase `FollowingPhase`) the other holds, so the follower keeps its place; only then does a deterministic callsign tie-break apply. The geometric rule comes first because a mutual stop limits only the holder: a FOLLOWG follower with a crossing aircraft dead ahead must stop for it, never hold it mid-crossing and drive into it.

- **Parallel-lane pass** (two movers whose tracks are within `ParallelTrackToleranceDeg` = 20° of parallel or anti-parallel, each with more lateral room from the other's track than `RequiredLateralClearanceFt` — half-spans plus `WingtipBufferFt` — both now and projected along each aircraft's current route-segment bearing over the longer of the pair's stopping times, capped at 12 s, without crossing that track in between): no holder at all.

  `HasParallelTrackLateralRoom` gates both the closing distance rule in `ComputeClosingLimit` (which otherwise trails a moving obstacle at 5 kt inside ~255 ft and stops inside ~155 ft by straight-line distance) and the head-on rule in `ResolveHeadOn` (300 ft ring, ≥ 150° heading difference, no lateral test of its own).

  Measured at SFO (GC 28/01 bundle): taxiways A and B are ~160 ft apart and opposite-direction passes bottom out at 238 ft, inside both rings, so a B738 on B crawled for 65 s and stopped for 20 s while three aircraft passed on A. A same-corridor head-on has ~0 lateral offset and still holds; a crossing more than 20° off parallel keeps the distance rule.
- **Pushback vs mover** (a pusher and a taxiing aircraft that would each stop for the other): holder = the taxiing aircraft (`TryResolveGiveWayToPushback`, reason `give way to pushback`); the push completes under the graduated closing limit.

  Inside the pair's collision distance the pusher's closing limit is also 0 — a head-on pusher and mover with no lateral room inside ~140 ft is a genuine wedge that only the controller (a new clearance, `BREAK`) resolves, the same residual class as the single-lane head-on below.

A residual mutual proximity-stop is still possible in pathological geometry, but is usually a **routing-layer** issue, not a detector one: two independently-resolved routes assigned opposing directions on a single-lane segment (AMX669 / SKW3404-vs-SWA2208 class) deadlock *correctly* — one holds until the routing or controller resolves it. That is a separate pathfinder concern.

When the navigator appears "stuck", check `Ground.SpeedLimit` and the `[Classify]` / `[Pair]` diagnostic log lines before assuming a navigator bug. The controller's way out is `BREAK`, or `RES` to the stalled aircraft: `GroundCommandHandler.TryResumeTaxi` treats a detector cap at or below `SlowTaxiSpeedKts` (5 kt) on an aircraft that is not held as a stall and breaks the conflict for the same 15 s; a higher cap is trail-speed behind moving traffic and still refuses "Aircraft is not held".

### Snapshot round-trip of the active primitive

`GroundNavigator.ToSnapshot` / `FromSnapshot` persist the active `PathPrimitive` and its progress in `GroundNavigatorDto.Playback` (`GroundNavigatorPlaybackDto`: the primitive as a `PathPrimitiveDto` tagged `Straight` / `Bezier` / `SlowTurn`, the playback and entry-blend values, whether an entry-alignment turn is holding back the segment's own primitive, the orbit-guard turn total and the entry-alignment aim). The held-back primitive itself is not saved; it is rebuilt from the segment.

`FromSnapshot` holds the saved playback instead of installing it: the owning phase's next segment set-up resumes it when that segment's end node matches the saved target node, and drops it otherwise (as does the first navigator tick). `ToSnapshot` writes the held playback while nothing is set up yet, so a snapshot taken between a restore and the first tick stays lossless.

Every navigator user resumes this way: `TaxiingPhase.FromSnapshot` forces `_initialized = false` so the next `OnTick`'s set-up resumes it, and `CrossingRunwayPhase` and `RunwayExitPhase` restore their saved navigator (not a fresh one) before their first set-up.

A restore mid-turn therefore continues the same arc at the same progress, matching the uninterrupted run. A snapshot without `Playback` (written before the field) restores as before: the rebuilt Bézier's first tick projects the live position onto the curve (`ClosestT`) and continues from there (Invariant I8). A free-space first leg re-anchors its line on the live position too, since `TaxiRoute.FromSnapshot` rebuilds the virtual node at its *original* coordinates.

**One exception round-trips: the aimed line over a fillet.** `GroundNavigatorDto.OnAimedLineOverFillet` records that the current fillet segment is being flown as the straight a node-aimed arc handed over on, and `SetupSegment` rebuilds that straight from the saved segment anchor (`SegmentFromLat/Lon`) to the fillet's to-node instead of the Bézier the aircraft is not standing on.

Adding new navigator runtime state means deciding whether it must round-trip; most curve state is deliberately reconstructed, not serialized.

---

## Key files

| File | Role |
|---|---|
| `src/Yaat.Sim/Phases/Ground/GroundNavigator.cs` | The navigator itself — setup, tick dispatch, steering, speed, orbit detection |
| `src/Yaat.Sim/Phases/Ground/TurnAboutFit.cs` | A type's gear fit for a turn about on a taxiway, its turn-about radius and its taxiway half-width, shared with the taxi gate |
| `src/Yaat.Sim/Phases/Ground/PathPrimitive.cs` | Immutable straight / Bézier / slow-turn primitives |
| `src/Yaat.Sim/Phases/Ground/PathPrimitiveBuilder.cs` | Segment → primitive compilation (GroundArc Bézier → PathPrimitiveBezier) |
| `src/Yaat.Sim/Phases/Ground/TaxiingPhase.cs` | Owns the navigator; route management, hold-short / crossing / clearance / parking |
| `src/Yaat.Sim/Phases/Ground/GroundStopBraking.cs` | Stop-braking choice (taxi rate, firm rate, backstop) and the led stop curve, shared by `FOLLOWG`, `GIVEWAY` and the uncleared-bar cap |
| `src/Yaat.Sim/Phases/Ground/RunwayExitPhase.cs` | Owns a navigator over the virtual exit route |
| `src/Yaat.Sim/Phases/Ground/CrossingRunwayPhase.cs` | Owns a navigator over the crossing route |
| `src/Yaat.Sim/Phases/Ground/FollowRoutePlanner.cs` | `FOLLOWG`'s planner and joinability probe: the lead's path (trail, current edge, remaining route), where the follower's route starts (`StartOf`), where it merges onto the lead's path, the re-plan (`Replan`, forbidden first move), whether the lead was re-routed (`LeadRouteMatchesPath`), and the along-path gap |
| `src/Yaat.Sim/Phases/Ground/FollowingPhase.cs` | The ground follow itself: owns a navigator over the follow route or a clearing route, re-plans at nodes and on a lead re-route, brakes along a lost route, gives way at the merge, keeps the gap along the path, stops at runway bars |
| `src/Yaat.Sim/Data/Airport/TaxiRoute.cs` | The route the navigator follows (segments, hold-shorts, index) |
| `src/Yaat.Sim/Data/Airport/AirportGroundLayout.cs` | `GroundArc` bezier fields, `DirectionalEdge` bearings, `MaxSafeSpeedKts` / `SafeSpeedForRadiusKts` / `SpeedProfile` / `TraversalSeconds` |
| `src/Yaat.Sim/AircraftCategory.cs` | All category performance constants (taxi/turn/decel/nose-wheel/corner speeds) |
| `src/Yaat.Sim/GroundConflictDetector.cs` | Per-tick speed-limit capping the navigator honors |

## Related docs

- [`./fillet-generator.md`](./fillet-generator.md) — the Bézier arc geometry the navigator follows.
- [`./pathfinder.md`](./pathfinder.md) — how the `TaxiRoute` is resolved.
- [`../landing-and-runway-exit.md`](../landing-and-runway-exit.md) — landing rollout, runway exit, virtual segments, anti-patterns.
- [`../phases.md`](../phases.md) — phase base contract, lifecycle, auto-append, command acceptance.
- [`../tick-loop.md`](../tick-loop.md) — per-tick order (phases before physics; conflict detection between).
