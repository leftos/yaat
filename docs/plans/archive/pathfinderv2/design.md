# Taxi Pathfinder v2 — Design Proposal

---

## Decisions from Review (binding for implementation)

The five open questions raised in section 12 have been resolved as follows. The implementer must follow these decisions; if the implementation suggests one of them is wrong, escalate before deviating.

1. **Infeasible explicit path (§12.1):** **Try reroute, fail if also infeasible.** When `T_i → T_{i+1}` junction at node X fails geometric admissibility, SegmentExpander first tries other junction candidates between the same two taxiways (per §decision-4 below). If all candidates fail, SegmentExpander attempts a bounded local auto-route detour bridging from the end of T_i to the start of T_{i+1}, using only numbered connectors and RAMP edges (no unauthorized letter taxiways). Only if that also fails does it return `TransitionInfeasible` with the full set of attempted junctions in the diagnostic.

2. **Weight calibration (§12.2):** **Hardcoded constants** at the top of `RouteCostFunction.cs`. Calibrate by running OAK + SFO grids and adjusting in code; no tunable config record.

3. **Reverse arc handling (§12.3):** ~~Hard reject in admissibility filter.~~ **REVISED 2026-05-27 after step 5:** **Soft cost penalty.** `GeometricAdmissibility` gates only on the category heading-delta limit (135° for jets); it does not separately exclude reverse arcs. `RouteCostFunction.ReverseArcCostNm = 0.8 nm` provides the soft disincentive. Real layouts (SFO, OAK) have legitimate reverse-traversed arcs at parking exits whose heading-delta is within limits — hard rejecting all reverse arcs was too aggressive. The SKW3404 case is still caught: that specific arc's delta is 180°, which the heading-delta check excludes regardless of direction.

4. **Segment expander junction backtracking (§12.4):** **Try multiple junction candidates between the same two taxiways.** Real airports (SFO especially) have parallel junctions where the controller's intent is satisfied by any of several junction nodes. Failing at the first infeasible junction is a correctness gap. Each per-segment search must enumerate all `T_i ↔ T_{i+1}` junction candidates, score them via the unified cost function, and pick the best feasible one (or report `TransitionInfeasible` only after all candidates fail).

5. **Authorized-taxiway enforcement (§12.5):** **Soft cost penalty** in `RouteCostFunction`, as specified in §6 (0.2 nm per first-use of an unauthorized letter taxiway). v2 emits a `Warning` on the route when this happens. Hard exclusion is rejected — some parking access necessarily crosses unnamed letter taxiways.

6. **`FindFullLengthLineupHoldShort` (§12.6):** Implementer's choice. Either pattern is fine; pick the one that produces fewer cross-file dependencies. *Step 4 outcome:* implemented in `RouteMaterialiser`; `TaxiPathfinder.FindFullLengthLineupHoldShort` delegates to it.

7. **DirectionReversal penalty location (added 2026-05-27 after step 5):** **SegmentExpander only.** Applying `DirectionReversalCostNm` in `RouteCostFunction.IncrementalCost` during A* breaks the heuristic's admissibility — any edge whose mid-bearing is more than 90° off the start→destination direction inflates the g-score, causing exponential expansion across the airport. AutoRouter's A* does NOT add the penalty. SegmentExpander's bounded per-segment local searches will apply it directly when scoring junction candidates.

8. **AutoRouter expansion budget (added 2026-05-27 after step 5):** **200,000** (raised from the design doc's initial 50,000 estimate). SFO cross-field routes legitimately explore the 100K+ range; 50K was rejecting valid paths. Re-evaluate if memory pressure becomes a concern.

9. **Aircraft category parameter (added 2026-05-27 after step 5):** The `ITaxiPathfinder` interface does NOT currently expose aircraft category. `TaxiPathfinder.FindRoute`/`FindRoutes` hardcode `AircraftCategory.Jet`. **Step 6 will extend the interface** to accept category and update production callers (`GroundCommandHandler`, `GroundViewModel`) to pass real category. Until then, all routes use jet limits.

10. **Helipad detection (added 2026-05-27 after step 5):** `SearchContext.ResolveDestination` was initially using a name-pattern heuristic (`name.Contains('H')`) to classify parking-vs-helipad. Replaced with node-type lookup: try `FindHelipadByName` first, classify as Helipad if found; otherwise classify via `FindParkingByName` as Parking.

---

## 1. Mental Model

Taxi routing is a **constrained graph search with two distinct input modes** that must
converge on the same internal representation before any planning work begins.

In **explicit-path mode** the controller has already spoken the route: "Taxi A, E, B,
B3 …". The pathfinder's job is not to choose taxiways — it must stitch that declared
sequence together into a chain of concrete graph edges, resolving each taxiway-to-taxiway
transition, and then verify that the resulting edge chain is geometrically drivable. The
hard problem in this mode is at the junction: how do we get from the last node on taxiway
A to the first useful node on taxiway E? Picking the wrong junction arc produces a hard
U-turn; picking none at all produces a gap.

In **auto-route mode** the pathfinder must choose the taxiways too. Now it is a
full search problem: find a sequence of edges from start node to destination node that
minimises a multi-dimensional cost while respecting the declared constraints (authorized
taxiways, runway crossings, hold-short bars).

The key insight that unifies both modes is this: **a route is just a sequence of directed
edges, and every quality criterion — drivability, cost, compliance — can be evaluated on
that sequence.** Planning should explore candidate edge sequences, evaluate them against a
single cost function, and commit to the best one. There is no reason to have separate cost
evaluators for different decision points. There is no reason to mutate a partially-built
sequence mid-walk. There is no reason to make a local choice without knowing whether a
global continuation exists.

So the architecture becomes: **build candidate edge sequences lazily, evaluate each
globally before committing, and emit the best survivor as the final TaxiRoute.** This
is a standard best-first search with global route scoring — the inputs differ between
modes but the search machinery is identical.

The second key insight is that **geometric correctness is a constraint, not a filter.**
Rather than checking a completed route for U-turns after the fact, the search expands only
transitions that are geometrically admissible from the current heading. A junction that
would produce a heading change above the category-specific limit is simply not an edge in
the search graph from that direction of arrival. Dead-ends and infeasible transitions
surface as exhausted branches, not as surprising post-processing failures.

---

## 2. Algorithm

The algorithm has three phases: **Constraint Compilation**, **Directed Graph Expansion**,
and **Route Materialisation**.

### Phase 1: Constraint Compilation

Before the search begins, compile the inputs into a set of hard constraints and a soft
cost model:

- **Waypoint sequence**: the ordered list of taxiway names (explicit mode) or the pair
  (start node, destination node) with any authorized taxiway filter (auto mode).
- **Category limits**: from aircraft category, compute the maximum admissible heading
  change at a node. This is the geometric admissibility predicate used during graph
  expansion.
- **Destination descriptor**: resolve the destination token (runway designator, `@parking`,
  `$spot`, `#noderef`) to a target node ID and a `HoldShortReason`.
- **Explicit hold-short list**: from the `HS rwy` tokens in the command, the set of
  runway designators that must be tagged as `ExplicitHoldShort` stops.
- **Authorized taxiway set**: the named taxiways from the command (or `null` for
  auto-mode unlimited).

None of this mutates layout data. It produces a `SearchContext` record that the
expansion phase reads but never modifies.

### Phase 2: Directed Graph Expansion

The search state is a **partial route** — an ordered list of directed edges, each edge
recorded as `(fromNodeId, toNodeId, edgeRef)`, together with the arrival bearing at the
current head node. The state space is: all partial routes from the start node that satisfy
constraints processed so far.

The search is **hierarchical in explicit mode** and **flat in auto mode**:

**Explicit mode — segment-by-segment expansion:**

The waypoint sequence is broken into segments: for each consecutive pair `(T_i, T_{i+1})`
in the named taxiway list, find a subpath along `T_i` that ends at a junction with `T_{i+1}`,
then take the best-oriented arc into `T_{i+1}`. Each segment expands independently via
a local best-first search constrained to edges belonging to `T_i`; the local search carries
forward the arrival bearing from the previous segment so geometric admissibility is applied
at the junction boundary. After the final named taxiway, an optional parking/runway
extension is appended using the same flat search (below).

Because each segment's search is bounded to nodes on a single named taxiway, the per-segment
search space is small — typically O(10–30 nodes) even at large airports.

**Auto mode — flat best-first (A\*-variant):**

Open a priority queue of partial routes keyed by `g + h`, where:
- `g` = accumulated cost so far (using the cost function from section 6)
- `h` = admissible heuristic: great-circle distance from the current head node to the
  destination, converted to nm and scaled by the minimum-cost-per-nm constant.

At each step, pop the cheapest partial route, expand all admissible outbound edges from
its head node (those that pass the geometric admissibility check given the current arrival
bearing), and push the resulting extended partial routes back. Edges leading to already-visited
nodes in the same partial route are excluded (no revisits). Edges on unauthorized
letter-only taxiways that are not in the authorized set receive a heavy cost penalty rather
than hard exclusion (so a route still exists even when authorization data is incomplete).

The search terminates when the popped partial route reaches the destination node (or a
destination-qualifying hold-short node for runway targets).

Because the cost function is non-negative and the heuristic is admissible, the first route
popped that reaches the destination is globally optimal under that cost function. This is
standard A\* guarantees applied to a directed weighted graph.

**Both modes share the same partial-route structure and cost function.** The only difference
is the top-level driver: explicit mode drives segment-by-segment, auto mode drives a single
A\* from start to destination.

### Phase 3: Route Materialisation

Once the winning edge sequence is committed:

1. Build the `TaxiRouteSegment` list: one entry per directed edge, with `TaxiwayName`
   sourced from the edge (junction arcs use the taxiway name of the arriving side, matching
   the controller-declared segment).
2. Walk the segment list and annotate `HoldShortPoint` entries: every `RunwayHoldShort`
   node encountered gets a `RunwayCrossing` entry; nodes named in the explicit hold-short
   list get an `ExplicitHoldShort` entry.
3. Truncate the route to one segment past the last required stop (destination or last
   explicit hold-short), discarding trailing pavement.
4. Set `DestinationParking` / `DestinationSpot` if the destination was `@parking` or
   `$spot`.
5. Emit `Warnings` for any unauthorized letter taxiway that the pathfinder traversed
   (used only by auto-route extensions beyond the declared path).

This is a single forward pass over the committed sequence. No mutation of intermediate
structures, no re-walking.

---

## 3. Why This Algorithm (vs. Alternatives)

**Alternative 1: Greedy forward walk with local heuristics.**
A greedy walk picks the single best-looking edge at each node without exploring alternatives.
This is fast and simple. It fails precisely when a locally attractive choice leads to a
globally infeasible continuation — which is the root cause of the SKW3404 spin and
essentially every other orbit bug on file. No amount of local heuristic tuning fixes a
greedy walk's inability to backtrack; the tuning just displaces the failure to a different
geometry. Rejected.

**Alternative 2: Two-phase: graph route then geometric repair.**
Find any topologically connected route first (ignoring heading), then repair U-turns
by finding local detours. Appealing because it decouples topology from geometry. The
problem is that geometric repair at a U-turn requires the surrounding subgraph to offer
an alternative — and determining whether one exists is exactly the search problem we
deferred. Worst case: the repair loop produces a repaired route that creates a new U-turn
one step later. The number of repair passes is unbounded. We would need backtracking
anyway, so this approach adds a phase without removing the core complexity. Rejected.

**Alternative 3: Multiple complete route enumeration, then pick best.**
Enumerate all simple paths from start to destination, score each globally, pick the minimum.
Correct in principle. Infeasible in practice: the number of simple paths in an airport
graph is exponential in the number of nodes. Even with pruning, a large airport (OAK, SFO)
has enough connectivity that exhaustive enumeration would blow the 100 ms budget. Rejected.

**The proposed algorithm** — hierarchical A\* with geometric admissibility as a graph
filter — avoids the exponential blowup by pruning inadmissible edges before expansion,
applies a consistent cost function at every decision point (not just at the final
comparison), and guarantees termination by construction (visited-node exclusion bounds the
search depth). The explicit-mode segment-by-segment decomposition exploits the structure of
the controller's instruction to further bound each sub-search to a small node set.

---

## 4. Core Data Structures

```csharp
// The compiled context for one pathfinding call — read-only after construction.
record SearchContext(
    AirportGroundLayout Layout,
    int StartNodeId,
    DestinationDescriptor Destination,
    IReadOnlyList<string> WaypointSequence,        // empty for auto-route
    IReadOnlySet<string>? AuthorizedTaxiways,      // null = all allowed
    IReadOnlySet<string> ExplicitHoldShorts,       // runway IDs from HS tokens
    AircraftCategory Category,
    RoutePreference? Preference,
    Action<string>? DiagnosticLog
);

// Describes what the route must reach.
record DestinationDescriptor(
    int? TargetNodeId,        // null for runway destinations (use runway hold-short)
    string? RunwayId,         // non-null for runway and explicit hold-short destinations
    string? ParkingName,      // non-null for @parking
    string? SpotName,         // non-null for $spot
    DestinationKind Kind
);

enum DestinationKind { Node, Runway, Parking, Spot, EndOfLastTaxiway, Helipad }

// The search state — an immutable partial route.
// Linked list via Previous so we never copy the full route on each expansion.
record PartialRoute(
    int HeadNodeId,
    double ArrivalBearing,          // heading at HeadNodeId after traversing LastEdge
    IGroundEdge LastEdge,           // edge traversed to reach HeadNodeId
    string LastTaxiwayName,         // resolved taxiway name for the last segment
    PartialRoute? Previous,
    int Depth,
    double AccumulatedCost,
    ImmutableHashSet<int> VisitedNodeIds   // for cycle prevention
);

// Priority queue entry for the A* open set.
record SearchFrontierEntry(
    PartialRoute Route,
    double FScore              // AccumulatedCost + heuristic
) : IComparable<SearchFrontierEntry>;

// Per-call accumulated cost components (used for diagnostics and variant scoring).
record RouteCostBreakdown(
    double DistanceNm,
    double TurnBudgetDeg,
    int TaxiwayTransitions,
    int RunwayCrossings,
    int DirectionReversals,
    int ReverseArcPenalties,
    double UnauthorizedTaxiwayCost
);

// Structured failure — returned when the algorithm exhausts the search.
record PathfindingFailure(
    FailureKind Kind,
    string HumanMessage,          // controller-facing one-liner
    string? InfeasibleTaxiway,    // the taxiway that couldn't be reached/exited
    string? InfeasibleTransition, // "A -> E": the specific transition that failed
    string? SuggestedAlternative  // optional hint ("try C instead of A")
);

enum FailureKind {
    StartNodeUnreachable,
    TaxiwayNotConnected,
    TransitionInfeasible,       // geometry prevents the junction
    TransitionAmbiguous,        // variant resolution is ambiguous (multiple candidates)
    DestinationUnreachable,
    SearchExhausted             // budget exceeded — layout quality issue
}

// Variant resolution result — used for T → T1/T2/T3 disambiguation.
record VariantResolutionResult(
    bool IsUnambiguous,
    string? ResolvedVariant,          // non-null when exactly one candidate
    IReadOnlyList<string> Candidates  // all matching variants when ambiguous
);
```

---

## 5. Module / File Layout

All files under `src/Yaat.Sim/Data/Airport/Pathfinding/`.

**`TaxiPathfinder.cs`** — The public implementation of `ITaxiPathfinder`. Thin
orchestrator: constructs a `SearchContext`, delegates to the segment expander (explicit
mode) or A\* driver (auto mode), then calls the materialiser. Contains no search logic
itself. This is the only file that sees both the public interface and the internal modules.

**`SearchContext.cs`** — Record definitions: `SearchContext`, `DestinationDescriptor`,
`DestinationKind`. Also the constraint-compilation logic: resolving destination tokens to
node IDs, reading the aircraft category limits, building the authorized taxiway set from
the command inputs. Pure functions, no side effects on layout.

**`PartialRoute.cs`** — Record definitions: `PartialRoute`, `SearchFrontierEntry`,
`RouteCostBreakdown`. Contains the linked-list materialiser (walk the `Previous` chain to
produce a flat `List<DirectionalEdge>` in forward order). The immutable structure design
is here; no search logic.

**`RouteCostFunction.cs`** — The single cost function used by all decision points. Takes
`(PartialRoute current, IGroundEdge candidate, GroundNode nextNode, SearchContext ctx)` and
returns a `double`. Contains the component weights and the admissible heuristic used by A\*
for `h`. No state; every method is static.

**`GeometricAdmissibility.cs`** — Determines whether a candidate edge can be appended to a
partial route given the current arrival bearing and the aircraft category. Checks: heading
change at the junction, arc traversal direction (forward vs. reverse), fillet tangent
continuity. Returns a `bool` (admit/reject). Also contains the helper that computes the
departure bearing of a candidate edge from a given node — the bearing that the `PartialRoute`
will carry forward on acceptance.

**`SegmentExpander.cs`** — Explicit-mode driver. Walks the waypoint sequence, for each
`(T_i, T_{i+1})` pair runs a bounded best-first search over nodes on `T_i` to find the
best junction into `T_{i+1}`, and chains the results together. Calls `GeometricAdmissibility`
and `RouteCostFunction` at every step. Returns a flat `List<DirectionalEdge>` or a
`PathfindingFailure`. This module is also responsible for variant resolution (W → W1/W2)
at the last segment of the declared path.

**`AutoRouter.cs`** — Auto-mode driver. Runs A\* over the full layout, constrained by
`SearchContext.AuthorizedTaxiways` (soft penalty) and `GeometricAdmissibility` (hard gate).
Returns a flat `List<DirectionalEdge>` or a `PathfindingFailure`. Handles the three
`RoutePreference` flavors by adjusting cost function weights; when preference is `null`,
runs all three and picks the winner by total cost.

**`RouteMaterialiser.cs`** — Phase 3 only. Takes a `List<DirectionalEdge>` plus the
`SearchContext` and produces a `TaxiRoute`. Annotates hold-short points, truncates the
route, sets destination strings. Contains `FindFullLengthLineupHoldShort` for runway
destinations (called both at materialisation time and from the public interface method of
the same name). No search logic.

**`PathfindingFailure.cs`** — Record definitions: `PathfindingFailure`, `FailureKind`. A
single file so the rest of the codebase has one import for the failure type.

---

## 6. Cost Function

All cost components are expressed in **nm-equivalent units**. The function returns a
non-negative scalar that accumulates additively as the partial route grows. Using nm
as the common unit keeps the heuristic admissible (it never exceeds the true remaining
cost) and makes weight choices legible against physical distances.

**Component table:**

| Component | Unit | Base Weight | Notes |
|-----------|------|-------------|-------|
| Segment distance | nm | 1.0 | Raw edge `DistanceNm` |
| Turn budget | deg → nm | 0.0005 nm/deg | Small: 180° turn ≈ 0.09 nm penalty, ~540 ft |
| Taxiway transition | count → nm | 0.05 nm/transition | ~300 ft equivalent per extra named-taxiway hop |
| Runway crossing | count → nm | 0.3 nm/crossing | ~1800 ft equivalent, enough to prefer a longer route with no crossing |
| Direction reversal | count → nm | 0.5 nm/reversal | ~3000 ft; strong disincentive |
| Reverse arc | count → nm | 0.8 nm/arc | Stronger than reversal because arcs produce the hardest spins |
| Unauthorized taxiway | name → nm | 0.2 nm/first-use, 0 thereafter | Per named letter taxiway entered, not per segment — encourages bridging through one unauthorized taxiway rather than none |
| Runway centerline segment | per edge | + distance × 10 | Makes on-runway transit ~10× worse than taxi-only alternative |

**How it combines:** Each time the search extends a partial route by one edge, it adds
`SegmentDistance + TurnPenalty + (transitionPenalty if taxiway changed) + (runwayCrossingPenalty if destination is a RunwayHoldShort node on an unrelated runway) + (reversalPenalty if spatial reversal detected) + (reverseArcPenalty if arc is reverse-traversed) + (unauthorizedPenalty if the edge is on an unauthorized letter taxiway not yet visited)`. The result is accumulated into `PartialRoute.AccumulatedCost`.

**Uniformity:** This function is the only place cost is computed. `SegmentExpander` calls
it when expanding nodes within a taxiway walk. `AutoRouter` calls it when pushing entries
onto the priority queue. There is no separate scorer in the junction-selection step, the
bridge-selection step, the parking-extension step, or anywhere else. Every decision point
passes through this function.

**Heuristic for A\*:** `h = GeoMath.DistanceNm(currentNode, destinationNode)`. This is
admissible because no path can be shorter than the straight-line distance, and the base
weight for distance is 1.0. Since all other cost components are non-negative, the heuristic
never overestimates.

**Preference selector adjustments:** When `RoutePreference.FewestTurns` is active, multiply
`TurnBudget` weight by 5 and `TaxiwayTransition` weight by 5. When `RoutePreference.Shortest`
is active, set all non-distance weights to 0 (pure geodesic optimisation). When
`RoutePreference.Fastest`, add a per-arc speed-cap penalty: `edgeDistance / maxSafeSpeedNmPerSec`
gives a time cost. These are multiplier adjustments on the same scalar output; the cost
function signature does not change.

---

## 7. Mode-by-Mode Handling

**Mode 1 — Explicit named path, implicit endpoint.** SegmentExpander walks each consecutive
taxiway pair. After the last taxiway, the route ends at the natural terminus of that taxiway
(the dead-end node or the last node with no further same-taxiway continuation). No special
casing: SegmentExpander just has no destination node to extend toward, so it stops at the
natural end.

**Mode 2 — Explicit named path with runway target.** SegmentExpander walks all named
taxiways. After the last named taxiway, variant resolution fires: if the last taxiway has
a numbered variant whose hold-short serves the destination runway, the route is extended
onto that variant. `FindFullLengthLineupHoldShort` picks the correct hold-short bar on
the destination runway. If variant resolution is ambiguous, a `TransitionAmbiguous` failure
is returned naming the candidates.

**Mode 3 — Explicit named path with parking target.** After walking all named taxiways,
AutoRouter is invoked in a short mode: from the terminus of the last named taxiway, route to
the parking node using only authorized-ramp edges and numbered connectors (no unauthorized
letter taxiways). This extension is bounded to the local parking subgraph, so it's fast.

**Mode 4 — Explicit named path with spot target.** Identical to Mode 3, substituting a
`Spot` node for the parking node. `FindSpotNodeByName` resolves the `$spot` token upstream;
by the time the pathfinder sees it, it's a node ID.

**Mode 5 — Auto-route to runway.** AutoRouter runs a full A\* from start to the full-length
lineup hold-short on the destination runway. Authorized-taxiway filter is `null`. Runway
crossing penalties keep the route from gratuitously cutting across active runways.

**Mode 6 — Auto-route to parking or spot.** AutoRouter runs A\* from start to the parking
or spot node. Same configuration as Mode 5.

**Mode 7 — Mid-route reroute.** Caller has already snapped the current aircraft position to
a graph node before calling the pathfinder (per the input contract). v2 simply receives a
`fromNodeId` and routes from there. No special treatment — the pathfinder is stateless and
does not know (or care) that this is a reroute rather than an initial assignment.

**Mode 8 — Node-reference traversal.** `#NNNN` tokens are resolved to node IDs by the
upstream parser and treated as single-node waypoints in the waypoint sequence. SegmentExpander
handles them naturally: the transition search from the previous taxiway/node to this node
uses the same geometric admissibility and cost check as any other junction.

**Mode 9 — Mixed.** SegmentExpander processes a heterogeneous waypoint sequence: some
entries are taxiway names, some are node IDs. The local search for each segment handles
either form. No special-casing; the segment expander treats a node-ID waypoint as a
one-node taxiway.

**Mode 10 — Auto-route with preference selector.** AutoRouter receives the `RoutePreference`
from `SearchContext`. Cost function weights are adjusted before the A\* runs. When preference
is `null`, AutoRouter runs three separate A\* calls (FewestTurns, Shortest, Fastest) and
returns the route with the lowest default-weighted cost among the three winners.

**Mode 11 — Helipad target.** `@H1` resolves to a `Helipad`-typed node via `FindHelipadByName`.
The node ID is placed in `DestinationDescriptor.TargetNodeId`. AutoRouter routes to it
exactly as it would to any other parking node. RAMP and numbered connectors give access to
the helipad pad; the cost function does not penalise helipads specifically.

---

## 8. Lookahead, Backtracking, and Termination

**Lookahead:** The algorithm looks as far ahead as necessary, not a fixed horizon. In
explicit mode, each per-segment best-first search exhausts all viable continuations on that
taxiway before committing a junction; the "lookahead" is naturally bounded by the taxiway's
node count. In auto mode, A\* implicitly examines all paths in cost order, so the winning
route is only committed once it has been proven globally optimal up to the search budget.
There is no explicit horizon constant to tune; lookahead depth is a consequence of
cost-ordering, not a separate parameter.

**Backtracking:** Both SegmentExpander and AutoRouter backtrack by construction. In explicit
mode, if no valid junction exists between `T_i` and `T_{i+1}`, the segment search exhausts
its candidate queue and returns failure; SegmentExpander does not try to continue with
the rest of the waypoint sequence. (There is no "undo the previous segment and try
differently" at the inter-segment level — if the controller named a sequence that has no
valid junction, that is a `TransitionInfeasible` failure.) In auto mode, A\* backtracks
implicitly: rejected branches remain on the open set at lower priority and can be revisited
later if their eventual cost proves competitive.

**Termination guarantees:**
1. `VisitedNodeIds` in `PartialRoute` is an immutable set that grows monotonically on every
   extension. No partial route can visit the same node twice, so path length is bounded by
   the number of nodes in the layout.
2. AutoRouter's priority queue can therefore hold at most O(N!) partial routes, but in
   practice the geometric admissibility filter eliminates most extensions immediately,
   keeping the queue tractable.
3. An explicit **node-expansion budget** (`MaxExpansions = 50_000`) guards against
   degenerate layouts with cycles enabled by the rare exception case. If the budget is
   exceeded, the algorithm returns a `SearchExhausted` failure with the best partial route
   found so far (if any) and a diagnostic note that the layout likely has a data quality
   issue.
4. For explicit mode, each per-segment search is additionally bounded by `GetNodesOnTaxiway`
   returning at most O(nodes on one taxiway), which for any real airport is under 200.

**Fallback:** When the search exhausts without reaching the destination, the failure record
carries the deepest viable partial route (if one was found) so the controller-facing message
can name the last successfully reached taxiway rather than giving a generic "no route found".

---

## 9. Layout-Quirk Handling

**Polyline-derived nodes (dense intermediate vertices):** The search graph treats every
node uniformly. Dense chains of collinear nodes on a single taxiway simply add short,
cheap edges that the cost function prices lightly. They do not affect correctness.

**Fillet arcs and natural traversal direction:** `GeometricAdmissibility` checks arc
traversal direction using `GroundArc.TangentBearingAt`. If traversing the arc in the
forward direction produces a tangent delta within the category limit, it is admitted; if
only the reverse direction is within limits, only the reverse is admitted; if neither is,
the arc is excluded. This is not a special case — it falls out naturally from the heading-
change check that all edges pass. No code path special-cases "this is an arc."

**Junction arcs at taxiway crossings:** Identical treatment. A junction arc between taxiway
A and taxiway E has both names; `GeometricAdmissibility` checks whether the heading delta
at the junction is within limits given the arrival bearing. If the layout only provides one
direction of junction arc (asymmetric fillet set), only the supported direction passes the
check. The search will route around the missing direction if one exists.

**Parking and spot nodes on taxiway centerline:** These nodes appear as ordinary graph
nodes with `Parking` or `Spot` type. Zero-length or near-zero-length edges to/from them
produce zero heading change and zero distance cost. The `VisitedNodeIds` set prevents the
search from revisiting them. No special logic needed.

**Numbered taxiways mixed with letters:** The cost function applies the unauthorized-taxiway
penalty only to nodes/edges whose taxiway name matches an authorized-taxiway pattern
(letter-only names that the controller named; all numbered taxiways are free by definition
from the requirements). The distinction is in the `SearchContext.AuthorizedTaxiways` set
construction (done in constraint compilation from the command inputs), not in name-parsing.

**Hold-short nodes with multiple runway IDs:** `RouteMaterialiser` queries `node.RunwayId`
when annotating hold-shorts. If a node's `RunwayId` contains multiple designators (e.g.,
"28L/28R"), it adds one `HoldShortPoint` per designator named in the `RunwayId` string.
This matches the existing `TaxiRoute.AddHoldShortAtIntersection` semantics.

**Dead-end stubs:** Any node with only one adjacent edge is a dead-end. The search
naturally terminates there (no extensions possible that don't revisit the predecessor).
The cost function receives no expansion candidates; the branch is exhausted.

**Missing fillet arcs (sharp-corner layouts):** When no arc supports a junction, the
straight edges on either side of the intersection node are the only candidates. The
heading change at the sharp corner is evaluated by `GeometricAdmissibility`. If the
change exceeds the category limit, the junction is excluded — and the failure message
identifies the specific node and heading delta, which is also a diagnostic signal that the
layout is missing a fillet.

**The SKW3404 / issue #165 constraint specifically:** The route `A E B B3 A B1 Z S` at
SFO produces a U-turn in v1 because the junction from B back to A (the second A in the
sequence) uses a reverse-traversed fillet arc that produces a ~180° heading flip. In v2,
`GeometricAdmissibility` rejects that arc for any category limit. SegmentExpander must
then find an alternative path from B to the A segment that reaches B3 without that reverse
arc. If no alternative exists, it returns `TransitionInfeasible("B → A", ...)` with a
clear message — which is the correct behavior: the controller-named path is geometrically
infeasible. The test `Skw3404_DoesNotOrbitDuringTaxi` asserts the aircraft does not orbit;
v2 satisfies this by either producing a drivable route (if an alternative exists) or by
refusing to issue an undrivable one (if no alternative exists — in which case the
`GroundCommandHandler` should surface the failure to the controller).

---

## 10. Diagnostic and Failure Model

**Diagnostic callback (developer-facing):** `SearchContext.DiagnosticLog` is an
`Action<string>?`. When non-null, the algorithm emits structured one-line messages at
decision points:

```
[v2:segment] twy=A  from=1249  expanding 7 candidates
[v2:segment] twy=A  node=1253  cost=0.021  turn=12.1deg  admitted
[v2:segment] twy=A  node=1260  cost=0.019  turn=8.3deg   admitted
[v2:junction] A→E  node=1265  arc=4812  headingDelta=32.4deg  admitted
[v2:junction] A→E  node=1265  arc=4813  headingDelta=178.2deg REJECTED (>135deg limit)
[v2:committed] twy=E  from=1265  5 segments
```

This format is designed to be grep-able: `[v2:junction]` lines give the full
accept/reject record for every junction considered. `[v2:FAIL]` lines appear on
structural failures. The `ExplicitPathOptions.DiagnosticLog` delegate in the interface
contract is wired directly through to `SearchContext.DiagnosticLog`.

**Structured failure (controller-facing):** `PathfindingFailure.HumanMessage` is a
one-line string the controller can act on:

| `FailureKind` | Example `HumanMessage` |
|---|---|
| `TaxiwayNotConnected` | "Cannot find taxiway E from current position on A" |
| `TransitionInfeasible` | "No valid arc from B to A at node 1407 (heading delta 178°) — layout may be missing a fillet" |
| `TransitionAmbiguous` | "Runway 28L is served by both W1 and W2 from W — specify W1 or W2" |
| `DestinationUnreachable` | "Cannot reach @D8 from the end of taxiway K" |
| `SearchExhausted` | "Route search exceeded budget near node 2304 — possible layout data gap" |

**Distinguishing controller error from algorithm confusion:** The failure kind encodes this:
- `TaxiwayNotConnected`, `TransitionInfeasible`, `TransitionAmbiguous`, `DestinationUnreachable`
  are **user input problems** — the controller named something the airport geometry cannot
  satisfy. The message is written for a controller to understand and retry.
- `SearchExhausted` is an **algorithm/layout problem** — the search ran into its budget,
  which on a correctly-authored layout should never happen for any reasonable command.
  The developer diagnostic log will contain the partial route and the last expansion
  attempt, identifying the problematic graph region.

**Null vs failure:** `ResolveExplicitPath` returns `null` and sets `failReason` (per the
interface contract). Internally, all paths use `PathfindingFailure`; the adapter in
`TaxiPathfinder` converts to the null/string interface at the boundary.

---

## 11. Test Strategy

**Unit tests for isolated modules** (`TaxiPathfinderUnitTests.cs`):
- `RouteCostFunction`: construct `PartialRoute` instances programmatically and assert
  that cost components accumulate correctly; verify preference-selector weight adjustments.
- `GeometricAdmissibility`: parametric tests over heading delta × arc direction × category
  → expected admit/reject.
- `RouteMaterialiser`: given a hand-crafted `List<DirectionalEdge>`, assert correct
  `HoldShortPoint` annotation, truncation, and destination string population.
- `VariantResolutionResult`: assert unambiguous/ambiguous correctly for mock layouts with
  one vs. two variants serving the same runway.

**Integration tests against real layouts** (plugging into the existing test harness):
All existing tests listed in the requirements doc must pass. The `TaxiPathfinderRouter.UseV2`
flag enables v2 without changing any test structure.

**Regression: SKW3404** (`Issue165SkwTaxiSpinTests`): The existing test
`Skw3404_DoesNotOrbitDuringTaxi` is the pass criterion. v2 must either produce a drivable
route (max stuck < 20 s) or fail fast with a `TransitionInfeasible` message that surfaces
to the controller — either outcome satisfies the test because an infeasible path that is
reported as such is preferable to a path that causes an orbit.

**Coverage grid** (`TaxiCoverageRunner`): The parking-to-runway grid for OAK and SFO
runs under both v1 and v2. Expected results: v2 success rate ≥ v1 on both airports, v2
U-turn count ≤ v1 on both airports.

**Comparison harness**: For each `(layout, input, category)` triple in the existing E2E
suite, emit `v1Route` and `v2Route` side-by-side. Differences are flagged as either
improvements (v2 has fewer U-turns or lower total cost) or regressions (v2 produces a
worse or null route where v1 produced a good one).

**Determinism test**: Run the same A\* call twice in the same process. Assert bitwise-
identical `TaxiRoute` output. Also run once with diagnostic callback enabled and once
without; assert routes are identical.

---

## 12. Open Questions

1. **Infeasible explicit path: fail or reroute?** When the controller names `A E B B3 A B1 Z S`
   and the B→A transition is geometrically infeasible (as in issue #165), v2 returns a
   `TransitionInfeasible` failure. This bubbles up to `GroundCommandHandler`, which would
   display an error to the instructor. Is that the desired UX, or should v2 silently find
   the nearest alternative junction (e.g., take A at the next available node past the bad
   arc)? The tradeoff: silent reroute is smoother but hides layout data gaps; hard failure
   is noisier but catches both bad controller instructions and bad layout data.

2. **Weight calibration.** The cost component weights in section 6 are initial estimates.
   They need calibration against real routing outcomes (OAK/SFO grid results). Should the
   first code pass hardcode them as constants, or expose them as a tunable configuration
   record that can be adjusted without recompiling? (Tunable is more flexible; hardcoded is
   simpler and avoids config drift.)

3. **Reverse arc: hard reject or cost-only?** Section 9 says `GeometricAdmissibility`
   rejects arcs whose heading delta exceeds the category limit. Reverse arcs can produce
   near-180° deltas (the SKW3404 case). But some layouts may have no forward arc at a
   junction — is it better to admit the reverse arc at very high cost (so it's only picked
   when no alternative exists) or to hard-reject it (cleaner but may produce more
   `TransitionInfeasible` failures on sparse layouts)? The requirements say no reverse arc
   traversal unless the geometry is "symmetric enough" — the definition of that threshold
   is unspecified.

4. **Segment expander backtracking at inter-segment level.** Currently the design fails
   fast if `T_i → T_{i+1}` has no valid junction — it does not attempt to walk further
   along `T_i` past the failed junction node to find another junction point. Some airport
   geometries have multiple junction points between two taxiways (taxiway A may cross
   taxiway E in two places). Should SegmentExpander try all junction candidates and pick
   the best, rather than failing at the first failed junction? This would make explicit
   mode behave more like auto-route in terms of robustness, at the cost of more internal
   search work per segment.

5. **Authorized taxiway enforcement strictness.** The cost function applies a soft penalty
   for unauthorized letter taxiways (makes it expensive, not impossible). Is soft-penalty
   the right model, or should unauthorized letter taxiways be a hard exclusion for auto-
   routing and only soft for explicit-path bridging? Hard exclusion might produce more
   `DestinationUnreachable` failures at airports where some routes necessarily cross an
   unnamed letter taxiway to reach parking.

6. **`FindFullLengthLineupHoldShort` contract.** The interface exposes this as a public
   method. The current design places its implementation in `RouteMaterialiser`. Should v2
   expose this as a separate thin public method that delegates to the materialiser, or
   should `TaxiPathfinder` implement it independently of the route-building path? The
   implementation is small either way; the question is whether the interface method is
   called from places other than within a full `ResolveExplicitPath` call.

---

## 13. Implementation Order

**Step 1 — Scaffold and plumbing** (first testable milestone: existing interface compiles
with stub bodies). Create `V2/` directory, stub `TaxiPathfinder.cs` implementing
`ITaxiPathfinder` with `throw new NotImplementedException()`. Wire `TaxiPathfinderRouter.UseV2`
to instantiate v2. Ensure the solution builds without warnings.

**Step 2 — Data structures and cost function** (unit-testable in isolation). Implement
`SearchContext.cs`, `PartialRoute.cs`, `PathfindingFailure.cs`, and `RouteCostFunction.cs`
with all record definitions and the cost calculation. Write `TaxiPathfinderUnitTests`
covering cost accumulation and heuristic properties. No layout interaction yet.

**Step 3 — Geometric admissibility** (unit-testable with mock edges). Implement
`GeometricAdmissibility.cs`. Parametric unit tests covering straight edges, forward arcs,
reverse arcs, and the category-limit threshold. This step does not require a real layout.

**Step 4 — Route materialisation** (unit-testable with hand-crafted edge lists). Implement
`RouteMaterialiser.cs`. Tests: hold-short annotation, truncation, destination string
population. Confirms the `TaxiRoute` output shape before any search logic exists.

**Step 5 — Auto router** (integration-testable against OAK/SFO). Implement `AutoRouter.cs`
with A\* over the full layout. Wire into `TaxiPathfinder.FindRoute` and `FindRoutes`.
Run `TaxiCoverageRunner` against OAK and SFO to establish a baseline success rate.

**Step 6 — Segment expander** (integration-testable against explicit-mode tests). Implement
`SegmentExpander.cs` with junction search and variant resolution. Wire into
`TaxiPathfinder.ResolveExplicitPath`. Run all existing TAXI-related unit tests. This is
where the SKW3404 regression must pass.

**Step 7 — Diagnostics and failure messages** (polish). Refine the diagnostic callback
output format. Ensure all `FailureKind` values produce useful `HumanMessage` strings.
Add the `SearchExhausted` partial-route capture. Verify the developer diagnostic log
is sufficient to diagnose a new layout-data gap without running the simulator.

**Step 8 — Full test suite verification** (acceptance). Run `pwsh tools/test-all.ps1`
with `TaxiPathfinderRouter.UseV2` enabled. Resolve any remaining regressions. Run the
comparison harness against v1 on OAK and SFO grids. When all six acceptance criteria from
the requirements doc are met, remove v1 and the feature flag.

The first testable milestone (steps 1–4) produces no real routing but confirms that the
output shape is correct and the cost function behaves as specified. Steps 5–6 are the bulk
of the implementation; step 5 can be done in parallel with step 6 since they have no
shared mutable state.

**Implementation complexity estimate:** Steps 1–4 are mechanical (2–3 days). Step 5
(AutoRouter/A\*) is moderate — the algorithm is standard but the admissibility check and
cost-function integration require careful testing (3–5 days). Step 6 (SegmentExpander)
is the most uncertain: the segment-by-segment junction search must handle asymmetric
fillet layouts, ambiguous variants, and the specific topological oddities of SFO and OAK
that produced the known failure cases (4–6 days, assuming some iteration on weight tuning
and admissibility thresholds after seeing real grid results). Step 7 is a day. Step 8
depends on how many regressions surface; budget 2–4 days. Total: 12–19 days of focused
implementation. The design document and the existing regression test suite are the scaffolding;
the implementation is medium-sized but not large.
