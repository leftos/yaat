# Taxi Pathfinder v2 — Requirements & Cleanroom Rewrite Plan

## Why a v2

The current `TaxiPathfinder.cs` (~3200 lines) has grown into a layered patchwork: a primary walk, multiple bridge strategies, post-walk arc-shortcut rewriting, an optional look-ahead, and a half-dozen filter helpers that each defend against a specific past bug. Recent investigation of issue #165 (SKW3404 SFO spin) revealed the structural cost: local heuristics make decisions that, taken together, produce routes with hard U-turns the simulator cannot drive. Patching each U-turn site individually keeps adding code without fixing the underlying greediness.

The codebase is now mature enough that the *requirements* on a pathfinder are clearly understood. We can afford to write a v2 from scratch, guided by accumulated learnings, and swap it in behind a feature flag for comparative testing before retiring v1.

This document captures **what** the pathfinder must do — inputs, outputs, behaviors, constraints, anti-patterns — without dictating **how**. The implementation team will design the algorithms.

---

## Inputs

The pathfinder receives, in some combination per call:

- The **airport ground layout** — a graph of nodes (parking, spots, intersections, hold-shorts, ramps) and edges (straight taxiway segments, fillet arcs within a single taxiway, junction arcs connecting two taxiways).
- A **start node** — where the aircraft currently is.
- A **user-issued path**, zero or more of:
  - Named taxiways (e.g. `A`, `B3`, `Z`) in the order to traverse them.
  - Explicit node references (`#142`) for unusual situations.
- A **destination**, exactly one of:
  - A runway designator — bare token like `28L` (taxi to a hold-short bar on that runway).
  - A parking name with `@` prefix — `@D8`, `@TERM2`, `@H1` for helipads.
  - A spot number with `$` prefix — `$32`.
  - A node reference (`#NNNN`).
  - *None* — destination is implied as "end of the last named taxiway" (a TAXI without a target).
- **Explicit hold-shorts** — extra runway designators the controller named with an `HS` keyword mid-path (e.g. `TAXI A B HS 28L`), to be honored as hard stops along the route.
- **Authorization context** — which taxiways the controller named (vs. ones the pathfinder may use freely for bridging/parking access).
- **Aircraft category context** (jet/turboprop/piston/helo). Affects: turn-rate assumptions, taxi-speed assumptions for the *Fastest* preference, the smallest viable fillet radius (a jet cannot make a turn a piston can), and category-conditional geometric correctness checks. v1 currently hard-codes jet defaults in one place; v2 must take category as an explicit parameter and use it consistently.
- **Auto-route preference** — when the caller invokes auto-routing (modes 5, 6, 11), an optional preference selector: *fewest-turns*, *shortest*, or *fastest*. When unspecified, the pathfinder may merge all three and pick the best by its global cost function. When specified, the pathfinder respects the preference.

The pathfinder must work uniformly whether the user provides a fully-specified path, no path at all, or anything in between.

### Inputs the pathfinder does NOT receive

These belong to caller layers (`GroundCommandParser`, `GroundCommandHandler`) and never reach the pathfinder:

- **Runway-crossing authorization** (`taxi.CrossRunways`, the `CROSS rwy` keyword) and AutoCross mode. The pathfinder always tags every runway crossing with a `RunwayCrossing` hold-short; downstream code (`TaxiRouteAutoCross.Apply` + `GroundCommandHandler.TryTaxi` post-processing) flips per-hold-short `IsCleared` flags based on authorization. v2 produces the unauthorized-baseline route; clearance flagging is downstream.
- **Mid-route mutations** from `RES [CROSS rwy] [HS target]` or bare `CROSS`. These mutate an already-resolved route's hold-short list directly and never reinvoke the pathfinder.
- **Command flag suffixes** (`NODEL` etc.). Parsed upstream; affect aircraft state, not routing.
- **Mid-segment aircraft position.** When the aircraft is between two graph nodes, `GroundCommandHandler` snaps `(position, heading)` to the nearest graph node via `AirportGroundLayout.FindNearestNodeForTaxi` *before* calling the pathfinder. v2's contract is strictly node-to-node.

---

## Outputs

A successful call produces a **route** consisting of:

- An ordered list of route segments, each with a directed edge (start node → end node), the taxiway name traversed, distance, departure/arrival bearings, and a kind tag (straight vs. arc).
- An ordered list of hold-short points — `HoldShortPoint` records carrying `NodeId`, `TargetName` (runway designator), and `Reason` enum (`RunwayCrossing` for incidental crossings the route makes; `ExplicitHoldShort` for ones the controller named via `HS rwy` mid-path). The `IsCleared` and `ClearedByAutoCross` flags are downstream-owned and start `false` from the pathfinder.
- The route is truncated to end one segment past the last needed hold-short / destination — the pathfinder does not include trailing pavement past where the aircraft must stop. (v1 uses `FindOneSegmentPastLastRunwayCrossingExit`; v2 must reproduce the behavior, however it implements it.)
- `DestinationParking` / `DestinationSpot` strings on the route, set when the destination was `@parking` or `$spot` (downstream phases use these to identify the parking name for display and arrival behavior).
- A `Warnings` list — non-fatal soft signals to surface to the controller, e.g. "Taxiing via X to reach Y" when the pathfinder had to use a taxiway the controller didn't explicitly authorize. v1 adds warnings during bridge selection; v2 must produce equivalent warnings on equivalent triggers.
- `CurrentSegmentIndex` initialized to 0 (mutated by downstream phases as the aircraft progresses — not the pathfinder's concern after route construction).

A failed call produces a **structured failure** that the controller-facing layer can render as an actionable message: which taxiway or node could not be reached, what the obstacle was, and (where possible) a suggested alternative phrasing.

The pathfinder is **idempotent**: identical inputs produce identical routes. It does not mutate the layout, the aircraft, or any global state. Output is a value the caller owns.

### Output contract — TaxiRoute structure compatibility

The route object returned **must be type-compatible** with v1's `TaxiRoute` shape, because downstream consumers (`TaxiingPhase`, `GroundNavigator`, `RunwayExitPhase`, `PhaseRunner`, snapshot serialization, replay, snapshot DTO via `TaxiRouteDto`) all read the same structure. v2 returns instances of the existing `TaxiRoute` class, not a v2-specific type. The fields and semantics that must round-trip identically: `Segments` and their `TaxiRouteSegment` shape, `HoldShortPoints` list, `DestinationParking` / `DestinationSpot` strings, `Warnings` list, `CurrentSegmentIndex` (initialized 0), summary helpers like `ToSummary()`.

v2 owns the *algorithm*. The *output type* is shared with v1; v2 produces instances of the same record/class. This keeps v2 plug-replaceable without rewriting downstream phases.

### Determinism under recording/replay

Recordings store the aircraft's resolved route at the time the TAXI command was issued (via snapshot serialization). On replay, the simulator re-issues the original command and expects to get **the same route back** so downstream phases drive the same path. v2 must therefore produce results that depend only on (layout snapshot + command inputs + aircraft category) — no time-of-day, no random sampling, no global cache that could differ between recording and replay.

---

## Path-finding Modes

The pathfinder must support these usage shapes from a single entry point. The caller distinguishes by which fields are populated:

1. **Explicit named path with implicit endpoint** — controller named taxiways, no target. Walk them in order; stop at the natural end of the last taxiway. Example: `TAXI A E B`.

2. **Explicit named path with runway target** — controller named taxiways AND a target runway. Walk the path; stop at the appropriate hold-short bar on the named runway (preferring the lineup hold-short for that runway, not an intermediate cross-runway hold-short). Example: `TAXI A B 28L`.

3. **Explicit named path with parking target** — controller named taxiways AND a parking destination. Walk the path; extend via numbered ramp taxiways to reach the parking spot from the end of the named path. Example: `TAXI K E @D8`.

4. **Explicit named path with spot target** — same as parking but resolves a spot number to a node. Example: `TAXI K E $32`.

5. **Auto-route to runway** — no named path; pathfinder picks the route to the named runway's hold-short. Example: `TAXI 28L`.

6. **Auto-route to parking / spot** — no named path; pathfinder picks the route to the destination. Example: `TAXI @D8`.

7. **Mid-route reroute** — aircraft already taxiing; new TAXI command issued. The aircraft's current `(position, heading)` is snapped upstream to the nearest graph node, then v2 routes from that node. The fact that the aircraft is moving (not parked) is irrelevant to v2; only the resolved start node matters.

8. **Node-reference traversal** — embedded `#NNNN` tokens for unusual cases (controller naming a specific node when no taxiway name is convenient).

9. **Mixed** — combinations of the above (named path with node-ref midway, etc.).

10. **Auto-route with preference selector** — for modes 5–6 (no named path), the auto-router may be asked to optimize differently: *fewest turns* (controller-style preference, fewer separately-named taxiways), *shortest* (pure distance), or *fastest* (time-aware, accounts for fillet-arc slow-down). v1 currently runs all three internally and picks the best by an internal scorer; v2 may unify them, expose them, or do the same merge — but must produce a route consistent with the requirement set above regardless.

11. **Helipad target** — `@H1` is a helipad parking position resolvable via `FindHelipadByName` (falls through to parking and spot resolution). When a helicopter taxis on the ground (not via ATXI), the pathfinder routes to the helipad node like any other parking. ATXI (air-taxi) does *not* use the pathfinder — it creates an `AirTaxiPhase` that flies straight at 100 ft AGL, so helipads-via-ATXI are out of v2's scope.

A controller's standard taxi instruction usually maps to mode 1, 2, or 3. Modes 5–8 are user-experience conveniences that should produce instructor-issuable routes (i.e., routes a human controller would have spoken aloud). Mode 11 is an operational reality the v2 must support gracefully.

---

## Geometric Correctness Requirements

A valid route is one the simulator can actually drive without orbiting, oscillating, or stalling. Concretely:

- **No U-turn at any interior node**. At every node the aircraft passes through, the heading change between arrival and departure must be physically executable given the aircraft category. Practically: ≤ ~135° per node, with tighter limits where the geometry leaves no run-up distance.
- **No reverse-direction arc traversal** unless the arc geometry is symmetric enough that traversing it the "wrong way" still produces a continuous tangent at both endpoints.
- **Fillet arcs flow naturally**. A fillet arc's tangent at its exit node must match the natural travel direction of the next segment. If the layout has no arc supporting the required direction, the pathfinder must use a different path through the node, not insert a wrong-direction arc.
- **Junction arcs (cross-taxiway) used only when oriented**. A junction arc connecting taxiway X and Y at node N has a definite direction of natural flow. The pathfinder must not traverse it in the reverse direction.
- **Hold-shorts visited in spatial order along the path**, not just listed unordered.
- **Doesn't cross an active runway** without an explicit authorization in the input or destination context.
- **Stays on authorized pavement**. Controller-named full taxiways (`A`, `Y`) must be respected as boundaries — the pathfinder cannot detour through unnamed letter taxiways. Numbered ramp/connector taxiways (`A1`, `M1`, `AY1`) may be used freely for bridging and parking access.
- **No revisiting a node** within a single route, except where the layout genuinely requires it (e.g., a one-way loop).

The geometric correctness criteria apply equally to routes the pathfinder discovers automatically AND to routes the user explicitly named. If a user-named path is geometrically infeasible, the pathfinder must report failure, not silently produce an undrivable route.

---

## Behavioral Requirements

These shape *how* the pathfinder reasons, even though we're not prescribing the algorithm:

- **Multi-step lookahead at every decision point**. When picking an edge, simulate at least 2–3 hops ahead before committing. A choice that looks good locally must produce a viable continuation; if no continuation is viable, the choice must be rejected.

- **Backtracking on dead-ends**. If a chosen branch leads to a node from which no further progress is possible, the pathfinder must unwind the last decision and try an alternative. Locally-correct decisions accumulating into a globally-infeasible route must be detected and undone.

- **Global cost optimization, not just local feasibility**. Between two feasible routes, the pathfinder should prefer the one a human controller would naturally choose: shorter total distance, fewer total turns, fewer transitions between taxiways, fewer runway crossings, no zig-zag.

- **Stable behavior across irrelevant variations**. Changing aircraft category should not flip the route choice unless geometry truly demands it (e.g., a tight fillet a jet cannot make).

- **Graceful degradation on imperfect input**. A user can name taxiways in an unusual order; if a route exists, the pathfinder should find it. If no route exists in the controller-named sequence, report which transition failed.

- **Tolerates layout quirks** (see "Layout conventions" below) without crashing or producing garbage. Degenerate zero-length adjacencies, parking nodes on taxiway centerlines, dead-end stubs, asymmetric fillet sets — all must be handled.

- **Explicit handling of "wrong-way" requests**. If the user names a taxiway sequence that physically requires going against the natural flow of a one-way segment, the pathfinder must either route around it or fail with a clear message — not silently insert a U-turn.

---

## Cost Model

The pathfinder evaluates candidate routes using a multi-dimensional cost. Components, roughly ordered by impact:

- **Total distance** (in feet or nm) — primary scalar.
- **Total turn budget** — sum of absolute heading changes at interior nodes. Severe turns count more than gentle.
- **Number of taxiway transitions** — controllers prefer routes that stay on one taxiway for as long as possible.
- **Number of runway crossings** — each crossing costs both time (must hold) and risk.
- **Direction reversals** — visiting nodes in a non-monotonic spatial order is bad even when it's not technically a U-turn.
- **Reverse-arc penalty** — discouraging arc traversals that go against the arc's natural tangent direction.
- **Unauthorized-taxiway penalty** — bridging via a letter taxiway the controller didn't name costs more than via a numbered connector.
- **Hold-short violation** — landing inside a hold-short region without a stop is a hard failure, not a cost.

The relative weighting of these components is part of the implementation design, not a requirement. The requirement is that the model exists and is *uniformly applied* — every decision point uses the same cost function so the global pick is consistent.

---

## ATC Realism Constraints

The pathfinder produces routes that controllers would issue and pilots would fly. This implies:

- **Routes follow operational ground flow.** Most airports have preferred taxi routes for each runway direction (north flow vs south flow at SFO, for instance). The pathfinder should bias toward these where they exist as data, otherwise produce routes that don't gratuitously cross opposing traffic.

- **Hold-short specificity.** A hold-short bar is a specific pavement marking at a specific node. The pathfinder identifies the correct bar — the lineup hold-short for the destination runway (the one closest to the runway's full-length departure point), not an opportunistic intermediate cross-runway hold-short.

- **Cross-runway awareness.** If the route necessarily crosses a runway the aircraft is not landing on or departing from, a hold-short is added at the crossing point. Subsequent re-clearance from the controller will release it.

- **Direction respect on one-way taxiways.** Where layout data marks a taxiway as one-way, the pathfinder respects it.

- **Pavement classes.** The layout names edges with four distinct classes, each with different usage rules:
  - **Letter-only taxiways** (`A`, `Y`, `F`) — full named pavement that controllers explicitly authorize. The pathfinder must traverse them only when listed in the controller's path, never as a free bridging corridor.
  - **Numbered taxiways** (`A1`, `M1`, `AY1`) — short fillets and connectors controllers don't usually name. The pathfinder may use them freely.
  - **RAMP-named edges** — apron / parking-area pavement without a controller-issued name. Used for parking↔taxiway transitions and for spawn-position bridging from parking spots. Not a transit corridor between named taxiways.
  - **Runway centerline edges** (`RWY28L`, etc.) — cost-penalized fallback for the rare case where the named path requires using a runway as a connector (e.g., back-taxi authorized). Almost never selected when a taxi-only alternative exists.

- **Authorization vs freedom.** Controller-named taxiways form a hard constraint — the pathfinder must traverse them in order, must not skip any, and must not substitute. Unnamed taxiways used for bridging/extension are the pathfinder's free choice, subject to cost.

- **Variant resolution.** When a controller names a base taxiway letter (e.g. `W`) but the destination runway's hold-short bar is on a numbered variant (`W1`, `W2`), the pathfinder must auto-extend the last leg onto the variant whose hold-short serves the destination runway. If multiple variants serve the same runway, this is **structured ambiguity** — the pathfinder returns a failure naming the candidate variants so the controller can disambiguate. (v1 has a `TaxiVariantResolver` that does this; v2 must preserve the behavior.)

- **Runway-edge bridging.** Some routes legitimately use a runway centerline as a transit corridor between two taxiway segments (when authorized, e.g., back-taxi). This is allowed but heavily cost-penalized — v2 must offer this as a fallback when no taxi-only route exists. The cost penalty is set high enough that any taxi-only alternative dominates.

---

## Diagnostic Requirements

A pathfinder that "just works" until it doesn't is unmaintainable. The v2 must support diagnosis at three levels:

- **Controller-facing.** On failure, a one-line message naming the obstacle: "cannot reach taxiway B3 from current position", "no hold-short on runway 28L via the named taxiways", "B3 does not connect to A". Specific enough for the controller to retry with corrected phrasing.

- **Developer-facing log.** A pluggable diagnostic callback that, when enabled, traces the pathfinder's decisions step by step: which candidates were considered at each branch, which was picked, why. Sufficient to reproduce the reasoning offline without re-running the simulator.

- **Replayable test cases.** The pathfinder's behavior must be reproducible from a layout + inputs alone — no hidden global state, no random number generators, no time-of-day effects. The same inputs always produce the same output.

The diagnostic output should be useful for the **layout-quality feedback loop**: when the pathfinder repeatedly fails or U-turns in the same node region, that's a hint the layout data has a missing fillet, a wrong direction, or a degenerate adjacency. The diagnostic stream should make these layout gaps obvious.

---

## Performance Requirements

Taxi routes are computed at controller-issue time (one TAXI command produces one routing call). Aircraft do not re-plan every tick. Practical bounds:

- **Latency.** A single call should complete in tens of milliseconds for a typical large-airport route (≤ ~100 segments). Worst case under 100 ms.
- **Memory.** Per-call working set under a few megabytes. No long-lived caches outside an explicit warm-up pass at layout load time.
- **No background threads.** Synchronous in-thread computation.
- **Determinism preserved under iteration.** Backtracking and lookahead must terminate; no unbounded depth without a hard cap that triggers a graceful failure.
- **Thread-safety.** TAXI commands from multiple aircraft can arrive concurrently on the server side; the pathfinder must be safe to call from multiple threads against the same layout. Layout-load-time precomputed indices are read-only after load. No per-call shared mutable state.

These bounds are loose — the v1 pathfinder typically finishes in a few milliseconds. The goal is not to match v1's raw speed but to stay comfortably within real-time interactive bounds.

---

## Test Requirements

For v2 to be considered swap-ready, it must:

- **Pass the v1 test suite** (all existing TAXI-related tests pass with v2 selected). This is the baseline — v2 cannot regress behavior the project has already committed to.

- **Pass the issue #165 repro test** (SKW3404 SFO E→B→B3 U-turn must not occur, or must be flagged as an infeasible user-issued path).

- **Pass any new regression tests** added during v2 implementation for cases v1 mishandles (we know of several beyond #165; some may surface during testing).

- **Have a focused unit-test suite** covering the helper primitives (cost function, lookahead, backtracking, hold-short selection, parking extension, runway-cross identification) independently of the integration tests.

- **Be testable in isolation** — no SimulationEngine, no SignalR, no networking. The pathfinder consumes a layout and produces a route; tests construct layouts directly.

- **Support the comparison harness** described below — every layout/input pair the harness throws at it produces a deterministic output that can be diffed against v1's.

---

## Layout Conventions to Design Around

The vNAS-sourced ground data has conventions and quirks v2 must accommodate gracefully:

- **Polyline-derived nodes.** Each named taxiway is a polyline; intermediate vertices become nodes. A taxiway is a sequence of straight-edge-connected nodes.

- **Fillet arcs at corners.** Where a taxiway bends, a fillet arc node-edge replaces the sharp corner. The arc has a natural direction of traversal — its `Nodes[0]` is the entry and `Nodes[1]` is the exit. Reverse traversal is geometrically problematic when the curvature is significant.

- **Junction arcs at taxiway-to-taxiway intersections.** Two named taxiways crossing at a single node may be connected by one or more junction arcs (small fillets that round the corner). A junction arc carries both taxiway names in its name list. Like fillet arcs, junction arcs have natural traversal direction.

- **Asymmetric fillet sets.** A junction may have only one direction of junction arc instead of two — i.e., the layout supports E→B traffic at node N but not B→E. v2 must detect this and route around (e.g., by extending the preceding walk past N).

- **Parking and spot nodes on taxiway centerline.** Some parking/spot nodes are placed exactly on a taxiway's centerline. The B taxiway, for instance, may pass through spot 30. Walks along B may traverse the spot node, including via degenerate (zero-length) adjacent edges. v2 must handle without producing spurious U-turn segments.

- **Numbered taxiways that mix with letters.** A1 is an A-taxiway connector; it's a numbered ramp the pathfinder can use freely. AY1 may bridge between A and Y. The naming convention is data-only — v2 must consult node and edge metadata, not parse names structurally.

- **Hold-short nodes carry runway metadata.** A `RunwayHoldShort`-typed node has a `RunwayId` field naming the runway it protects. The same physical bar may be tagged with multiple runways (e.g., "28L/28R" if a stop protects both).

- **Dead-end stubs.** Some taxiway segments end at a paint-line edge (no continuation). v2 must recognize these as dead-ends and not generate routes that require continuation through them.

- **Missing data.** Older layouts may lack fillet arcs entirely at some corners. v2 must produce a route even where the only available geometry is sharp-corner straights.

---

## Anti-Patterns Observed in v1 (Things NOT to Repeat)

A summary of structural issues identified during the issue #165 investigation and prior debugging:

- **Greedy walk that locks in.** v1's walk loop commits to each step before knowing whether downstream steps are viable. Once a wrong edge is taken, no mechanism unwinds.

- **Local heuristics that don't compose.** v1 has several filters that each defend against a specific past bug; they do not consult each other and can re-introduce the bugs they were meant to defend against (e.g., the single-candidate fast path bypasses the flip-free filter entirely).

- **Multiple PATH-mutating passes.** WalkTaxiway, BridgeToTaxiway, ApplySameTaxiwayArcShortcuts, and the implicit re-walk when bridge is chosen all mutate the same segment list. Their ordering and interactions are subtle. v2 should produce its result in one pass — exploratory work happens on candidate routes, not on the committed result.

- **Conditional lookahead.** SelectBestStopNode only runs when a destination hint is present. The same look-ahead is needed for purely path-driven calls but doesn't run there. The lookahead must be unconditional.

- **Cost functions inconsistent across decision points.** v1's bridge picker uses one scorer, the start-edge picker uses another, the walk-edge picker uses a third. v2 should have one cost function used everywhere.

- **Hidden ordering dependencies.** v1's behavior depends on the order in which walks are executed, which arcs are inserted post-walk, which filters fall back. v2 should make ordering explicit or remove it as a degree of freedom.

- **Edge cases as separate code paths.** v1 has special-cased code for "first taxiway", "last taxiway", "single edge available", "no edge available", "destination is parking", "destination is runway", "destination is spot". Most of these can collapse into a single uniform model with the right data structure.

---

## Modern Paradigms Worth Considering

For inspiration, not prescription. The implementation team chooses what fits:

- **Hierarchical pathfinding.** Plan at the *taxiway* level first (sequence of taxiways), then resolve each segment at the *node* level. Used in RTS games and large-graph navigation. Maps naturally to the "named path vs auto-route" duality.

- **A\* with a multi-dimensional cost function.** A single A\* search over a state space that includes both position and incoming-direction. The cost function applies uniformly. v1 already uses A* internally but only for unconstrained bridge searches.

- **Backtracking search with iterative deepening.** Explore candidate paths to a fixed depth; on dead-end, unwind and try a sibling. Common in constraint satisfaction.

- **Beam search.** Maintain top-K candidate routes; prune below threshold. Useful when many partial routes look equally good locally.

- **Funnel-style smoothing.** After finding a topologically valid route, smooth it by collapsing chord-chains into arcs where the geometry supports it. v1's `ApplySameTaxiwayArcShortcuts` is a primitive version of this.

- **Two-phase planning.** Phase 1: produce a topologically valid (graph-connected) route from start to destination. Phase 2: verify and repair geometric realizability — detect U-turns, find local alternatives. Decouples the two concerns v1 conflates.

- **Constraint propagation.** Hold-shorts, runway crossings, authorized taxiways, direction preferences encoded as constraints; the search respects them by construction rather than checking them post-hoc.

- **Pure-pursuit-aware costing.** The simulator drives the route using pure-pursuit + corner planning. The cost model can incorporate "can pure-pursuit actually drive this corner at this speed?" rather than purely geometric criteria.

---

## Comparison Harness

We swap v1 ↔ v2 via a runtime selector:

- A single configuration flag (env var, config key, or solution-level option) chooses which implementation a call goes to. Both implementations conform to the same public interface.

- The simulator and all production code call the interface, not a concrete class. No code path has special knowledge of which implementation is active.

- A **comparison test fixture** can run both implementations on the same input and emit a diff:
  - Same route (modulo arc-vs-straight-equivalent rewrites)? Pass.
  - Different routes? Surface a structured comparison: segment-by-segment lat/lon, total distance, turn count, U-turn count, run time.
  - One fails, other succeeds? Especially noteworthy; usually the swap candidate's win.

- The **E2E recording suite** (`Skw3404_*`, `OakNorthFieldTaxiSpin*`, `OakAllParkingTaxiAuto*`, etc.) runs against whichever implementation is selected. We can run each suite under both implementations as a CI matrix.

- **Layout-stress fixture.** Iterate every parking → every runway hold-short on a representative airport (OAK and SFO at minimum); record route per pair under each implementation; produce a route-quality table (distance, turns, U-turns, success rate). v2 is ready to retire v1 when this table shows ≥ v1 on every metric for both airports.

The comparison harness is not throwaway scaffolding — once v2 is the default, it remains as a regression detector for future pathfinder changes.

---

## Acceptance Criteria for v2 Retiring v1

The v1 implementation is removed from the tree when **all** of:

1. v2 passes the entire existing test suite (Yaat.Sim.Tests, Yaat.Client tests, both airports' E2E recordings).
2. v2 fixes issue #165 (SKW3404 SFO) — either produces a navigable route or reports the user-issued path as infeasible.
3. v2 fixes the other open ground-taxi issues at the time of swap (catalog below).
4. The comparison harness shows v2 ≥ v1 on route-quality metrics across the OAK and SFO parking-to-runway grids.
5. v2's median latency stays within 2× v1's.
6. The diagnostic stream produces useful output on the failure cases — failures are debuggable from the log alone.

Until all six clear, v2 lives alongside v1 behind the selector flag.

### Specific regression cases v2 must handle

These test classes exist as regressions for past pathfinder bugs and must all pass under v2:

- `Issue165SkwTaxiSpinTests` — SFO E→B→B3 U-turn (the immediate motivation).
- `OakNorthFieldTaxiSpinTests` — OAK north-field orbit signature.
- `OakAllParkingTaxiAutoTests` — OAK every parking → 28R auto-route survival.
- `OakCrossThenHoldOnNextTaxiwayTests` — cross-runway authorization handling.
- `IssueFllDal880TaxiBacktrackBTests` — FLL backtrack on B taxiway.
- `Skw3078TaxiEAtoB10RouteTests` — E-A to B10 routing.
- `IssueAmxTaxiOvershootTests` — overshoot at a fillet apex.
- `FilletPathfindingTests` — fillet-arc behavior across airports.
- `AirportE2ETests` — broad E2E.
- `TaxiPathfinderTests` — focused unit tests on the v1 surface.
- `TaxiRouteCatalogTests` — catalog validation pathways.
- `TaxiCoverageRunner` — the OAK/SFO parametric grid (smoke + nightly).

Each of these is a hard floor — v2 cannot regress any of them.

### TaxiRouteCatalog interaction

`TaxiRouteCatalog` holds pre-defined taxi routes per airport, loaded from ARTCC JSON. At menu-build time, the catalog calls into the pathfinder to **validate** each route against the current layout (i.e., re-resolve the named taxiway sequence and confirm it produces a navigable route). v2 must support this validation use case — it's the same `ResolveExplicitPath`-style call, just invoked offline from a UI context rather than from a runtime TAXI command.

---

## Out of Scope for v2

To bound the rewrite:

- **Layout data fixes.** Where v2 reveals a layout-data quality issue (missing fillet, wrong direction, degenerate adjacency), v2 reports it cleanly but does not fix the underlying data. Layout quality is a separate workstream.

- **Pushback routing.** Pushback uses a different code path (`PushbackToSpotPhase`) and a different physical model. Not part of v2's surface.

- **Landing rollout / runway exit.** `LandingPhase` and `RunwayExitPhase` use their own geometric planners. v2's domain starts when the aircraft is fully on the taxi graph.

- **Mid-taxi conflict resolution.** When two aircraft are converging at a node, the conflict-resolution code (separate) chooses who holds. v2 is single-aircraft pathfinding.

- **Voice / pilot speech.** v2 produces routes; speech generation reads them.

- **TAXI command parsing.** Command parsing (extracting `@parking`, `$spot`, `HS rwy`, `NODEL`, runway designators, etc.) happens in `GroundCommandParser` upstream. v2 receives already-parsed inputs.

- **Layout Inspector tool compatibility.** `Yaat.LayoutInspector` currently uses some v1-specific entry points for offline graph analysis. The inspector may continue to use v1 (or get updated separately); v2 is not required to expose every internal helper.

- **Migration tooling for existing tests.** Tests that rely on v1's exact segment list (rather than route correctness) need to be revisited — but that's a known cost of the rewrite, accepted by this plan.

---

## Scrutinizer Review Checklist

This section is for the **scrutinizer agent** during consensus implementation. Every section of v2 the implementer drafts must be reviewed against:

**Correctness**
- [ ] Does this section satisfy a requirement listed above?
- [ ] Does it handle all the modes (1–11) the requirement enumerates?
- [ ] Does it respect the geometric correctness rules (no U-turn, no reverse-arc, fillet flow)?
- [ ] Does it respect the ATC realism rules (authorization, hold-short specificity, runway-cross handling)?
- [ ] Does it produce TaxiRoute objects compatible with downstream phases?
- [ ] Does it correctly leave out the things the pathfinder does NOT own (CrossRunways auth, NODEL flags, mid-segment snapping, post-pathfinder route mutations)?

**Anti-pattern avoidance**
- [ ] Is the cost function applied uniformly at every decision point?
- [ ] Are there any "first-match" / "single-candidate fast path" / "fall through on filter zero" patterns that bypass the planning logic?
- [ ] Are there multiple PATH-mutating passes? If so, is their order explicit and justified?
- [ ] Is lookahead unconditional, or only fires under specific conditions?
- [ ] Are edge cases handled by branching code, or by data-model uniformity?

**Robustness**
- [ ] What happens with a layout that has missing/asymmetric fillets at every junction?
- [ ] What happens with a degenerate zero-length edge?
- [ ] What happens with a parking node on the taxiway centerline?
- [ ] What happens when the user names a geometrically infeasible path?
- [ ] What happens when the start node equals the destination node?
- [ ] What happens when the layout has no fillet arc available for a corner the route needs to take?

**Determinism**
- [ ] No time-of-day input, no random seed, no global counter.
- [ ] Identical (layout, inputs, category) → identical TaxiRoute.
- [ ] No state shared across calls except read-only layout-load-time indices.

**Test alignment**
- [ ] Will this change cause regression in `OakNorthFieldTaxiSpinTests` / `Issue165SkwTaxiSpinTests` / the other listed regression cases?
- [ ] If a test must change, is the change a *correctness improvement* over v1's behavior, or a regression?
- [ ] Is there a focused unit test for the new helper / strategy?

**Diagnosability**
- [ ] Does the diagnostic stream explain why this decision was made?
- [ ] On failure, does the structured failure message name the specific obstacle?

The scrutinizer's job during consensus is to use this checklist on every meaningful patch the implementer proposes. Disagreements are surfaced via `SendMessage`; consensus is reached by revision until both agree, or escalated to the user when stuck.
