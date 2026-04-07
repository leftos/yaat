# Fillet Arcs — Smooth Turn Geometry in Ground Layout

## Problem

Aircraft negotiate turns at taxiway intersections by steering toward the next node's position, producing sharp corners and heading overshoots. The navigator uses turn anticipation (early arrival) to approximate smooth turns, but this breaks down for:

- **90° exits**: Aircraft arrives at the hold-short pointing ~30° past perpendicular because it chases a nearby virtual node from a bad heading (W6/W7 on OAK runway 30 show 122° total turn instead of ~90°)
- **>90° turns**: The arc path misses the target node entirely — the aircraft curves *around* the intersection, ending up at the right heading but wrong position
- **Heading-based steering attempts**: Steering toward a required heading instead of a position produces runaway speeds for large turns because straight-line distance stops being meaningful

The root cause: the ground graph has **no turn geometry**. Edges are straight lines between nodes, so every intersection is a sharp corner. Real taxiways have painted fillet curves with specific curb radii.

## Solution

Replace intersection nodes with **fillet arcs** during layout construction. At each intersection, every pair of incoming edges gets a fillet: a `GroundArc` (curved) for angled pairs, or a merged straight `GroundEdge` for collinear pairs. The intersection node is then deleted — it has no remaining edges.

### Concept

Given intersection node B with three edges A-B, B-C (perpendicular), B-D (collinear with A-B):

```
Before (sharp corners):         After (fillets replace B):

    A ----B---- D               A ---T1  T2--- D    (straight fillet: T1-T2, B removed)
          |                          (arc)
          C                         ·             T1-C arc (from A side)
                                   T3             T2-C arc (from D side)
                                   C
```

B is gone. Every edge pair through B becomes a fillet:
- **A-B-D** (collinear): T1-T2 straight edge (inner segment between tangent points)
- **A-B-C** (90°): curved arc from tangent point on A-B to tangent point on B-C
- **D-B-C** (90°): curved arc from tangent point on D-B to tangent point on B-C

Tangent points (T1, T2, T3) are new nodes inserted at `R * tan(θ/2)` from B on each edge. They are regular `TaxiwayIntersection` nodes — no new node type.

### Data Model

Both `GroundEdge` (straight) and `GroundArc` (curved) implement a common interface:

```csharp
public interface IGroundEdge
{
    GroundNode[] Nodes { get; }          // [endpointA, endpointB] — no implied direction
    string TaxiwayName { get; }          // display name ("W" or "W/W3" for junction arcs)
    double DistanceNm { get; }           // straight-line length or arc length

    bool MatchesTaxiway(string name);    // does this edge belong to the given taxiway?
    bool SharesTaxiway(IGroundEdge other); // any overlap in taxiway names (W shares with W/W3)
    bool SameTaxiway(IGroundEdge other);   // exact identity (W != W/W3, W == W)
    bool IsRunway { get; }               // TaxiwayName starts with "RWY"
    bool IsRamp { get; }                 // TaxiwayName is "RAMP"
    double MaxSafeSpeedKts(double turnRateDegPerSec); // speed limit for arc radius + turn rate

    GroundNode OtherNode(GroundNode node);
    int OtherNodeId(int nodeId);
    bool HasNode(int nodeId);
    DirectionalEdge Directed(GroundNode fromNode, GroundNode toNode);
}
```

**No bare `string.Equals` on TaxiwayName** — all taxiway name comparisons go through these interface methods. This ensures junction arcs at taxiway intersections are matched correctly.

`GroundArc` stores **center + radius + taxiway names** — angles are derived from `BearingTo(center, node)`, and the minor arc (≤180°) is used by convention. Sweep direction is determined at traversal time by which node is "from" vs "to" in the `DirectionalEdge`.

```csharp
public sealed class GroundArc : IGroundEdge
{
    public required GroundNode[] Nodes { get; init; }
    public required string[] TaxiwayNames { get; init; } // ["W"] or ["W", "W3"] — no precedence
    public required double CenterLat { get; init; }
    public required double CenterLon { get; init; }
    public required double RadiusFt { get; init; }
    public required double DistanceNm { get; init; }
    // TaxiwayName => "W" or "W/W3" (display only — use MatchesTaxiway for checks)
    // MaxSafeSpeedKts => V = ω × R (computed from radius and aircraft turn rate)
    // IGroundEdge methods...
}
```

Junction arcs (2 names) belong equally to both taxiways — no precedence between names. A same-taxiway arc (1 name, e.g. an L-curve on taxiway A) is fully "same taxiway" as straight edges on A.

### Taxiway name comparison semantics

Three levels of comparison, each for different use cases:

| Method | W vs W | W vs W/W3 | W/W3 vs W/W3 | Use case |
|--------|--------|-----------|---------------|----------|
| `MatchesTaxiway("W")` | ✓ | ✓ | ✓ | "Does this edge belong to taxiway W?" — walker, pathfinder |
| `SharesTaxiway(other)` | ✓ | ✓ | ✓ | "Any overlap?" — A* transition penalty |
| `SameTaxiway(other)` | ✓ | ✗ | ✓ | "Exact identity?" — continuation checks |

### How the navigator follows an arc

"Carrot on a stick" path tracking (replaces point-to-point steering for arc segments):

1. **Project** the aircraft position onto the arc to find the closest point (parameter `t` in `[0, 1]`)
2. **Lookahead**: advance `t` by a small distance along the arc to get a target point ahead
3. **Steer** toward the lookahead point (same `TurnHeadingToward` as straight edges)
4. **Speed**: compute max safe speed for the arc radius and aircraft turn rate: `V = ω * R`

The aircraft smoothly follows the painted curve. Different aircraft types negotiate the same curb radius at different speeds based on their category turn rate.

### How the renderer draws an arc

Polyline approximation from `BearingTo(center, node)` sweep — produces a smooth curve at any zoom level.

### Graph Structure After Filleting

An intersection node with N edges produces up to N*(N-1)/2 fillets (one per edge pair). After filleting:

- The intersection node is **deleted** from the graph
- Each edge pair becomes either:
  - A **straight edge** (collinear pair, < 15° angle): tangent-point-to-tangent-point inner segment + shortened outer edges
  - A **curved arc** (angled pair, ≥ 15°): `GroundArc` connecting tangent-point nodes
- Tangent-point nodes are inserted on the original edges at `R * tan(θ/2)` from the intersection
- Original edges are shortened to end at the tangent points instead of the intersection
- Collinear merges that have tangent points produce a straight edge between the two tangent points (the inner RWY segment)

A turn at what was previously a single intersection becomes: straight edge → arc → straight edge.

### Arc Geometry

For two edges meeting at angle θ at a node:
- **Curb radius** `R` — sized to fit: `R = min(edgeLenA, edgeLenB) / tan(θ/2)`, clamped to a maximum
- **Tangent distance** `T = R * tan(θ/2)` — distance from intersection to tangent point on each edge
- **Arc length** `L = R * θ` — curve length
- **Arc center** — offset from intersection along the bisector by `R / cos(θ/2)`

### Curb Radius Selection

Radius is computed as `min(maxFitRadius, maxConfiguredRadius)`:
- `maxFitRadius = min(edgeLenA, edgeLenB) / tan(θ/2)` — largest radius that fits both edges
- Max configured radius by edge type:
  - High-speed exit (runway edge, exit angle ≤ 45°): 150ft
  - Runway-to-taxiway exit: 100ft
  - Standard taxiway intersection: 75ft
  - Ramp area: 50ft

No minimum radius — short edges get small arcs rather than being skipped.

### Speed Constraint

Aircraft max speed through an arc of radius R with turn rate ω:
```
max_speed_kts = ω_deg_per_sec * (π/180) * R_ft / 6076.12 * 3600
```

This integrates with the navigator's backward-propagated braking. When approaching an arc segment, the navigator sees the speed constraint and decelerates in advance — same mechanism as braking for hold-short nodes.

### Eligibility

Nodes are **not** filleted if they are:
- `RunwayHoldShort`, `Parking`, `Spot`, or `Helipad` type
- Have fewer than 2 edges
- Are runway endpoints (exactly 1 RWY edge) — these are threshold nodes

Nodes **are** filleted if they are `TaxiwayIntersection` with 2+ edges, including mid-centerline nodes with 2+ RWY edges (the collinear RWY pair merges, and taxiway branches get arcs; tangent points on the RWY edges are connected by a straight inner segment preserving centerline walkability).

## Implementation Plan

### Phase 1: Data Model & Interface ✅

- [x] Define `IGroundEdge` interface with `Nodes`, `TaxiwayName`, `DistanceNm`, `OtherNode`, `OtherNodeId`, `HasNode`, `Directed`
- [x] Make `GroundEdge` implement `IGroundEdge`
- [x] Add `GroundArc` class implementing `IGroundEdge` (center + radius only; angles derived)
- [x] Rename `DirectionalGroundEdge` → `DirectionalEdge` wrapping `IGroundEdge`
- [x] Update `GroundNode.Edges` from `List<GroundEdge>` to `List<IGroundEdge>`
- [x] Update `AirportGroundLayout`: separate `Edges` and `Arcs` collections, `AllEdges` for unified iteration
- [x] Update `RebuildAdjacencyLists` to include arcs
- [x] Update all consumers: `TaxiPathfinder`, `TaxiRouteSegment`, `GroundNavigator`, `GroundRenderer`, `RunwayExitPhase`, `FlightCommandHandler`, `VirtualNode`, tests
- [x] Build with zero warnings, all 2527 tests pass
- [x] Add `MatchesTaxiway`, `SharesTaxiway`, `SameTaxiway`, `IsRunway`, `IsRamp` to `IGroundEdge`
- [x] Replace all bare `string.Equals`/`StartsWith` on `TaxiwayName` with interface methods across entire codebase
- [x] `GroundArc.TaxiwayNames` array (no precedence), display name `"W/W3"` for junctions

### Phase 2: Arc Generation ✅

- [x] Add `FilletArcGenerator` class in `Data/Airport/`
  - Plan-then-execute algorithm: Phase A computes all fillets, Phase B creates tangent nodes, Phase C creates arcs, Phase D rebuilds edges
  - Radius fits to available edge length (`min(edgeLen) / tan(θ/2)`) clamped to configured max
  - Collinear merges produce inner straight edges between tangent points
  - Orphaned edges (e.g., parking connections) reconnect to nearest tangent or merge endpoint
  - Adjacency lists rebuilt between each intersection to handle cascading mutations
  - Skips `RunwayHoldShort`, `Parking`, `Spot`, `Helipad`, runway endpoints
- [x] Unit tests: 90° intersection, collinear merge, 3-way, 4-way, hold-short/parking skip, short-edge radius reduction, arc center position, endpoint radius validation
- [x] Integration test: OAK filleted graph — all nodes reachable, graph connected, A* finds routes, centerline walk works, exit search works
- [x] Call from `GeoJsonParser.Parse()` as Step 8 after `RebuildAdjacencyLists`
- [ ] Add `--fillets` flag to LayoutInspector (no longer needed — fillets always on; consider `--no-fillets` for debugging)

### Phase 3: Pathfinding Integration ✅

- [x] A* pathfinder traverses `IGroundEdge` (cost = `DistanceNm` for both types) — works naturally via adjacency lists
- [x] `TaxiRouteSegment` uses `DirectionalEdge` wrapping `IGroundEdge`
- [x] Exit path resolution (`FindExitFromCenterline`) works with filleted graph (validated with OAK)
- [x] Centerline walking works through tangent-point nodes (inner RWY segment edges)
- [x] Multi-strategy pathfinder: FewestTurns, Shortest, Fastest (see below)
- [x] Walker prefers straight edges over arcs when staying on a taxiway
- [x] `ResolveArcSegmentName` picks contextually correct name for route summaries

### Multi-Strategy Pathfinder

Replaced the single-strategy A* (distance + transition penalty hack) with a proper 3-strategy system:

**Strategies:**
- **FewestTurns**: Minimizes taxiway transitions. Junction arcs (2 names) count as transitions; same-taxiway arcs don't. Distance used as tiebreaker only.
- **Shortest**: Pure distance minimization. Runway edges penalized to prevent backtaxi.
- **Fastest**: Minimizes estimated time. Straight edges use category taxi speed (Jet 30kts, Turboprop 25, Piston 20, Helicopter 15). Arcs use `min(taxiSpeed, arcSafeSpeed)` where safe speed = `ω × R`.

**API:**
```csharp
// Single best route (FewestTurns strategy — most natural taxi instruction)
TaxiRoute? FindRoute(layout, fromNodeId, toNodeId)

// Multi-route suggestions (like a navigation app)
List<TaxiRoute> FindRoutes(layout, from, to,
    preference: null,        // null = all 3 strategies, or specify one
    maxRoutes: 4,
    aircraftType: "B738")    // affects Fastest strategy's arc speed limits
```

**Scoring & ranking:**
1. Each strategy runs Yen's K-shortest, producing up to `maxRoutes` candidates
2. Every candidate is scored by every strategy on 0.0–1.0 (1.0 = best for that metric)
3. A route's final score = max across strategies (surfaces it if it's best at anything)
4. Deduplicated by taxiway sequence, sorted by score descending, top N returned

### Phase 4: Navigator Arc Following — IN PROGRESS

- [x] `GroundNavigator` detects `GroundArc` segments and switches to carrot-on-a-stick steering
  - Projects aircraft position onto arc via bearing from center
  - Computes lookahead point 10° ahead along the arc
  - Steers toward lookahead point
  - Computes max speed from arc radius and category turn rate (`V = ω * R`)
- [x] Arc speed limit applied alongside heading-error scaling and multi-segment braking
- [x] `DirectionalEdge.DepartureBearing` / `ArrivalBearing` — tangent-aware bearings for arcs
- [x] `SetupSegment` uses edge bearings for turn angle computation
- [x] Arc `MaxSafeSpeedKts` back-propagated in `SetupSegment` forward walk
- [x] Forward walk uses `edge.DistanceNm` instead of straight-line distance
- [x] Turn anticipation suppressed for arc segments (current or next)
- [ ] Rework `ComputeArcSteering` for bezier curves (blocked on arc geometry fix)
- [ ] Evaluate removing turn anticipation entirely once bezier arcs handle all turns

### Phase 5: Rendering ✅

- [x] `GroundRenderer`: `DrawArcSegment` draws arcs as polyline curves for route visualization
- [x] `FrameRenderer` (TickAnimator): draws arc edges in green polyline
- [ ] Verify visual quality in Ground View at various zoom levels — **deferred** (needs fillets enabled in production)

### Phase 6: Integration & Validation — IN PROGRESS

#### Enabling fillets in production

- [x] Hook `FilletArcGenerator.Apply` into `GeoJsonParser.Parse()` as Step 8 after `RebuildAdjacencyLists`
- [x] Fix double-filleting in fillet test files (removed manual `Apply` calls)
- [x] Fix hardcoded node ID references (`OakSpeedProfileTests` → dynamic branch node lookup)
- [x] Taxi route resolution working — walker uses `MatchesTaxiway` for junction arcs
- [x] Walker prefers straight edges over arcs when staying on the same taxiway
- [x] A* transition penalty uses `SharesTaxiway` to avoid penalizing junction arcs
- [x] `ResolveArcSegmentName` picks contextually correct name for route summaries
- [ ] Fix remaining 15 test failures (down from 39 at start)
- [ ] Add `GroundArcDto` to `ServerConnection.cs` and update `GroundLayoutDto` / `ReconstructLayout` for client-server transmission
- [ ] Update `docs/architecture.md`

#### Blocked: arc geometry must be fixed first

All remaining test failures trace back to broken arc geometry in `FilletArcGenerator`. The navigator changes (tangent bearings, arc speed back-propagation, turn anticipation suppression) are structurally correct but can't produce good results when the arcs themselves curve the wrong way.

**Sequence:**
1. Replace circular arcs with cubic bezier curves in `FilletArcGenerator`
2. Update `GroundArc` data model (bezier control points)
3. Update `ComputeArcSteering` to follow bezier curves
4. Update `SetupSegment` with bezier-aware speed constraints and tangent bearings
5. Run tests — most behavioral failures should resolve with correct geometry
6. Investigate remaining failures individually (see `docs/plans/open-issues/fillet-test-failures.md`)

#### Navigator improvements (done, waiting for correct geometry)

- [x] `DirectionalEdge.DepartureBearing` / `ArrivalBearing` — tangent-aware bearings
- [x] `SetupSegment` uses edge bearings instead of node-to-node bearings for turn angles
- [x] Arc speed constraints back-propagated in `SetupSegment` forward walk
- [x] Forward walk uses `edge.DistanceNm` (arc length) instead of straight-line distance
- [x] Turn anticipation suppressed when current or next segment is an arc
- [x] `_nextSegmentIsArc` flag for turn anticipation decisions

#### Additional tests needed

- [ ] Synthetic fillet test: runway + taxiways at 20°–160° angles (use `tools/Yaat.Scratch`)
- [ ] Navigator bezier following unit tests (projection, lookahead, speed constraint)
- [ ] All OakAllExitsTests pass with fillets
- [ ] All Sfo28rAllExitsTests pass with fillets
- [ ] End-to-end taxi with fillet arcs
- [ ] TickAnimator before/after GIFs for visual comparison

## What This Replaces

- **Turn anticipation** in `GroundNavigator` — evaluate whether it can be removed entirely. Arcs provide the smooth path that turn anticipation was approximating.
- **`RequiredOutboundHeading`** — backed out previously. Fillet arcs solve heading alignment structurally.
- **Virtual segments in `RunwayExitPhase`** — may be simplified since the arc handles turn geometry.

## Decisions & Learnings

### Arc taxiway naming (decided)
An arc at a junction between taxiways W and W3 has `TaxiwayNames = ["W", "W3"]` — no precedence. Display name uses `" · "` separator (e.g., `"W · W3"`) to avoid collision with `"/"` in runway identifiers. A same-taxiway arc (L-bend on taxiway A) has `TaxiwayNames = ["A"]` and is fully "same taxiway" as straight edges on A.

### IsRunway → IsRunwayCenterline (decided)
Renamed `IsRunway` to `IsRunwayCenterline` on `IGroundEdge`. For `GroundEdge`, it checks `TaxiwayName.StartsWith("RWY")`. For `GroundArc`, always returns `false` — a runway centerline is straight by definition. Junction arcs between runway and taxiway are not centerline segments.

Added `GroundArc.IsRunwayJunction` — true when the arc has one RWY name and one non-RWY name. These arcs are the exit/entry transitions between runway and taxiway.

Added `IGroundEdge.MatchesRunway(string designator)` — proper runway designator matching that parses `"RWY10L/28R"` format and checks each end. Replaces all bare `TaxiwayName.Contains(designator)` patterns.

### FindAdjacentHoldShort branchTwy fix (decided)
When the BFS seeds from a junction arc, the branch name must be the non-runway taxiway name (via `GroundArc.FirstNonRunwayName()`), not the composite display name. The display name `"G · RWY28R/10L"` can't match subsequent single-name `GroundEdge`s in the BFS.

### Walker straight-edge preference (decided)
When `WalkTaxiway` walks along a taxiway, it prefers straight `GroundEdge`s over `GroundArc`s. Arcs are only used as fallback when no straight edge continues the taxiway. This prevents the walker from detouring through junction arcs when collinear straight edges exist.

### Pathfinder redesign (decided)
Replaced the single A* with transition penalty hack with a 3-strategy system (FewestTurns/Shortest/Fastest). Each runs Yen's K-shortest. Routes scored 0.0–1.0 per strategy, final score = max across strategies. Deduplicated by taxiway sequence. `FindRoute` defaults to FewestTurns (most natural taxi instruction).

### No bare string comparisons on TaxiwayName (enforced)
All taxiway name checks go through `IGroundEdge` methods (`MatchesTaxiway`, `SharesTaxiway`, `SameTaxiway`, `IsRunwayCenterline`, `IsRamp`, `MatchesRunway`). This is critical for junction arcs where the display name doesn't match either individual taxiway. Tests still have some bare comparisons on `TaxiRouteSegment.TaxiwayName` (plain string on the segment, not `IGroundEdge`) — those are fine.

### Test fixup approach (learned)
Enabling fillets broke 39 tests. Most were fixable by: (1) removing manual `FilletArcGenerator.Apply` calls (double-filleting), (2) replacing bare `string.Equals` with `MatchesTaxiway`, (3) replacing hardcoded node IDs with semantic lookups. The remaining failures are behavioral — the filleted graph changes how aircraft navigate.

### Circular arc geometry is fundamentally broken (learned)
The `FilletArcGenerator` has two critical bugs:

1. **Shared tangent points corrupt arc geometry.** When an edge participates in multiple arc pairs (e.g., a taxiway at a 4-way intersection), each pair computes a different tangent distance. The "keep largest" logic forces all pairs to use the same tangent point, but the arc center is computed for a different tangent distance. Result: center-to-node distance doesn't match the declared radius (verified: 100ft radius but 215ft center-to-node distance).

2. **Circular arcs can't handle asymmetric tangent constraints.** At a crossing intersection, two arcs share a tangent point on each edge. The arc on the acute side needs a small tangent distance; the arc on the obtuse side needs a large one. A circular fillet requires equal tangent distances on both edges (`R = d / tan(θ/2)`). When the shared tangent distance is forced by one pair, the other pair's circle can't pass through both tangent points.

Verified by visual inspection: arcs at 90° intersections (H at OAK) look correct. Arcs at non-90° intersections (P, J at OAK) curve outward instead of inward.

### Replace circular arcs with cubic bezier curves (decided)
Circular arcs are the wrong primitive. A cubic bezier naturally solves the asymmetric tangent problem:
- P0 = tangent point on edge A (shared with other curves on that edge)
- P1 = control point along edge A direction (depth controls curvature)
- P2 = control point along edge B direction
- P3 = tangent point on edge B (shared with other curves on that edge)

Benefits:
- Tangent directions at endpoints are trivially correct (P0→P1 and P2→P3)
- Asymmetric tangent distances are natural — control point distances are independent
- One tangent point per edge is fine — each curve pair adjusts its control points
- No "center + radius" to get wrong
- `MaxSafeSpeedKts` computed from minimum radius of curvature along the curve

This requires reworking:
- `GroundArc` data model: store P0/P1/P2/P3 instead of center/radius
- `ComputeArcSteering` in navigator: project onto bezier, lookahead along curve
- `MaxSafeSpeedKts`: compute from tightest curvature along bezier
- `TangentBearingAt`: trivial with bezier (P0→P1 or P2→P3)
- `FilletArcGenerator`: compute control points instead of circle center
- Renderers: draw bezier polyline instead of circular arc

## Key Files

- `src/Yaat.Sim/Data/Airport/AirportGroundLayout.cs` — `IGroundEdge`, `GroundArc`, `DirectionalEdge`, graph structure, `MatchesTaxiway`/`SharesTaxiway`/`SameTaxiway`
- `src/Yaat.Sim/Data/Airport/FilletArcGenerator.cs` — arc generation algorithm
- `src/Yaat.Sim/Data/Airport/GeoJsonParser.cs` — Step 8: `FilletArcGenerator.Apply` after `RebuildAdjacencyLists`
- `src/Yaat.Sim/Phases/Ground/GroundNavigator.cs` — arc-following mode (`ComputeArcSteering`)
- `src/Yaat.Sim/Phases/Ground/RunwayExitPhase.cs` — exit route building
- `src/Yaat.Sim/Data/Airport/TaxiRoute.cs` — route segments supporting `IGroundEdge`
- `src/Yaat.Sim/Data/Airport/TaxiPathfinder.cs` — 3-strategy A* + Yen's K-shortest, `RoutePreference` enum
- `src/Yaat.Client/Views/Ground/GroundRenderer.cs` — `DrawArcSegment` for route rendering
- `tools/Yaat.TickAnimator/FrameRenderer.cs` — arc edge rendering
- `tests/Yaat.Sim.Tests/FilletArcGeneratorTests.cs` — unit tests
- `tests/Yaat.Sim.Tests/FilletPathfindingTests.cs` — pathfinding integration tests
- `tests/Yaat.Sim.Tests/FilletVisualizerTests.cs` — HTML visualization for debugging
- `tests/Yaat.Sim.Tests/FilletArcDumpTests.cs` — detailed trace dump for debugging
