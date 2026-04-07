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
    string TaxiwayName { get; }
    double DistanceNm { get; }           // straight-line length or arc length

    GroundNode OtherNode(GroundNode node);
    int OtherNodeId(int nodeId);
    bool HasNode(int nodeId);
    DirectionalEdge Directed(GroundNode fromNode, GroundNode toNode);
}
```

`GroundArc` stores only **center + radius** — angles are derived from `BearingTo(center, node)`, and the minor arc (≤180°) is used by convention. Sweep direction is determined at traversal time by which node is "from" vs "to" in the `DirectionalEdge`.

```csharp
public sealed class GroundArc : IGroundEdge
{
    public required GroundNode[] Nodes { get; init; }
    public required string TaxiwayName { get; init; }
    public required double CenterLat { get; init; }
    public required double CenterLon { get; init; }
    public required double RadiusFt { get; init; }
    public required double DistanceNm { get; init; }
    // IGroundEdge methods...
}
```

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
- [ ] Call from `GeoJsonParser.Parse()` — **deferred** (breaks 64+ tests that depend on unfilleted node IDs/topology)
- [ ] Add `--fillets` flag to LayoutInspector

### Phase 3: Pathfinding Integration ✅

- [x] A* pathfinder traverses `IGroundEdge` (cost = `DistanceNm` for both types) — works naturally via adjacency lists
- [x] `TaxiRouteSegment` uses `DirectionalEdge` wrapping `IGroundEdge`
- [x] Exit path resolution (`FindExitFromCenterline`) works with filleted graph (validated with OAK)
- [x] Centerline walking works through tangent-point nodes (inner RWY segment edges)

### Phase 4: Navigator Arc Following ✅

- [x] `GroundNavigator` detects `GroundArc` segments and switches to carrot-on-a-stick steering
  - Projects aircraft position onto arc via bearing from center
  - Computes lookahead point 10° ahead along the arc
  - Steers toward lookahead point
  - Computes max speed from arc radius and category turn rate (`V = ω * R`)
- [x] Arc speed limit applied alongside heading-error scaling and multi-segment braking
- [ ] Integrate arc speed constraint with backward-propagated braking in `SetupSegment` — **deferred**
- [ ] Evaluate removing turn anticipation — **deferred** (needs E2E validation with fillets enabled)

### Phase 5: Rendering ✅

- [x] `GroundRenderer`: `DrawArcSegment` draws arcs as polyline curves for route visualization
- [x] `FrameRenderer` (TickAnimator): draws arc edges in green polyline
- [ ] Verify visual quality in Ground View at various zoom levels — **deferred** (needs fillets enabled in production)

### Phase 6: Integration & Validation — NOT STARTED

#### Enabling fillets in production

- [ ] Hook `FilletArcGenerator.Apply` into `GeoJsonParser.Parse()` as Step 8 after `RebuildAdjacencyLists`
- [ ] Update or rewrite 64+ tests that depend on specific unfilleted node IDs, edge counts, or graph topology
- [ ] Add `GroundArcDto` to `ServerConnection.cs` and update `GroundLayoutDto` / `ReconstructLayout` for client-server transmission
- [ ] Update `docs/architecture.md`

#### Known problem exits (must pass before merge)

- [ ] **OAK 30 W6** (B738, 90° exit): was 122° total turn, should be ~90°
- [ ] **OAK 30 W7** (B738, 90° exit): was 124° total turn
- [ ] **OAK 28R P** (C172, 128° exit): heading reversals
- [ ] **SFO 28R K** (B738, 90° exit): was 118° total turn, heading reversals

#### Navigator integration

- [ ] Backward-propagate arc speed constraints through `SetupSegment` (currently only applied in `Tick`)
- [ ] Evaluate removing turn anticipation once arcs handle all turns
- [ ] Test arc following at various speeds and aircraft categories

#### Additional tests needed

- [ ] Navigator arc following unit tests (projection, lookahead, speed constraint)
- [ ] All OakAllExitsTests pass with fillets (34 tests)
- [ ] All Sfo28rAllExitsTests pass with fillets
- [ ] End-to-end taxi with fillet arcs (A* finds paths, aircraft follow them smoothly)
- [ ] TickAnimator before/after GIFs for visual comparison

## What This Replaces

- **Turn anticipation** in `GroundNavigator` — evaluate whether it can be removed entirely. Arcs provide the smooth path that turn anticipation was approximating.
- **`RequiredOutboundHeading`** — backed out previously. Fillet arcs solve heading alignment structurally.
- **Virtual segments in `RunwayExitPhase`** — may be simplified since the arc handles turn geometry.

## Key Files

- `src/Yaat.Sim/Data/Airport/AirportGroundLayout.cs` — `IGroundEdge`, `GroundArc`, `DirectionalEdge`, graph structure
- `src/Yaat.Sim/Data/Airport/FilletArcGenerator.cs` — arc generation algorithm
- `src/Yaat.Sim/Data/Airport/GeoJsonParser.cs` — hook point for arc generation (Step 8, currently not called)
- `src/Yaat.Sim/Phases/Ground/GroundNavigator.cs` — arc-following mode (`ComputeArcSteering`)
- `src/Yaat.Sim/Phases/Ground/RunwayExitPhase.cs` — exit route building
- `src/Yaat.Sim/Data/Airport/TaxiRoute.cs` — route segments supporting `IGroundEdge`
- `src/Yaat.Sim/Data/Airport/TaxiPathfinder.cs` — A* traversal over `IGroundEdge`
- `src/Yaat.Client/Views/Ground/GroundRenderer.cs` — `DrawArcSegment` for route rendering
- `tools/Yaat.TickAnimator/FrameRenderer.cs` — arc edge rendering
- `tests/Yaat.Sim.Tests/FilletArcGeneratorTests.cs` — unit tests
- `tests/Yaat.Sim.Tests/FilletPathfindingTests.cs` — pathfinding integration tests
- `tests/Yaat.Sim.Tests/FilletVisualizerTests.cs` — HTML visualization for debugging
- `tests/Yaat.Sim.Tests/FilletArcDumpTests.cs` — detailed trace dump for debugging
