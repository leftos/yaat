# GroundEdge Bidirectional Refactor

## Problem

`GroundEdge` has `FromNodeId`/`ToNodeId` (and now `FromNode`/`ToNode`) which imply directionality, but edges are bidirectional — the same edge object is stored in both endpoint nodes' adjacency lists. When a `TaxiRouteSegment` traverses an edge in the opposite direction, `Edge.ToNode` gives the wrong endpoint. This caused a real bug (OAK E2E test failure) and required a workaround (`DestinationNode`/`OriginNode` on `TaxiRouteSegment`).

## Design

### GroundEdge becomes non-directional

```csharp
public sealed class GroundEdge
{
    public required GroundNode[] Nodes { get; init; }  // Fixed-size 2
    public required string TaxiwayName { get; init; }
    public required double DistanceNm { get; init; }
    public List<(double Lat, double Lon)> IntermediatePoints { get; init; } = [];
}
```

- `Nodes[0]` and `Nodes[1]` are the two endpoints — no implied direction
- Remove `FromNodeId`/`ToNodeId`/`FromNode`/`ToNode`/`OtherNode`
- Graph traversal code uses `edge.Nodes[0]`/`edge.Nodes[1]` and determines which is "other" from context

### DirectionalGroundEdge for navigation

```csharp
public sealed class DirectionalGroundEdge
{
    public required GroundEdge Edge { get; init; }
    public required GroundNode FromNode { get; init; }
    public required GroundNode ToNode { get; init; }

    public string TaxiwayName => Edge.TaxiwayName;
    public double DistanceNm => Edge.DistanceNm;
}
```

- Created when building routes/paths — captures the direction decision
- Multiple instances can reference the same `GroundEdge` (different directions, or same direction for U-turns)
- Factory: `GroundEdge.Directed(fromNode, toNode)` or similar

### TaxiRouteSegment uses DirectionalGroundEdge

```csharp
public sealed class TaxiRouteSegment
{
    public required DirectionalGroundEdge Edge { get; init; }
    public string TaxiwayName => Edge.TaxiwayName;

    // Derived from Edge — no redundant FromNodeId/ToNodeId
    public int FromNodeId => Edge.FromNode.Id;
    public int ToNodeId => Edge.ToNode.Id;
}
```

- Remove `DestinationNode`/`OriginNode` (directional edge already has `FromNode`/`ToNode`)
- `FromNodeId`/`ToNodeId` become computed from the directed edge (needed for serialization/snapshots)

### GroundNavigator simplification

```csharp
// Current (with workaround):
var targetNode = seg.DestinationNode;

// After refactor:
var targetNode = seg.Edge.ToNode;  // Always correct — direction is baked in
```

## Blast radius

~70 usage sites across 16+ files. Key areas:

| Area | Files | Sites | Impact |
|------|-------|-------|--------|
| Graph model | AirportGroundLayout.cs, TaxiRoute.cs | ~15 | Data model change |
| Layout construction | GeoJsonParser, TaxiwayGraphBuilder, RunwayCrossingDetector | ~10 | Edge creation |
| Graph traversal (BFS/DFS) | AirportGroundLayout.cs | ~20 | `edge.FromNodeId == currentNode.Id ? ...` pattern |
| Pathfinding | TaxiPathfinder.cs | ~15 | Edge creation + traversal |
| Navigation | GroundNavigator.cs | ~5 | Simplified |
| Conflict detection | GroundConflictDetector.cs | ~5 | Segment comparison |
| Tests | 7 test files | ~40+ | Edge construction |

## Execution strategy

### Phase 1: GroundEdge → Nodes[2]
- [ ] Add `Nodes` array to `GroundEdge`, keep `FromNodeId`/`ToNodeId` as computed props temporarily
- [ ] Update all edge construction sites to use `Nodes = [nodeA, nodeB]`
- [ ] Update graph traversal (BFS/DFS) to use `Nodes` instead of ID pattern
- [ ] Remove `FromNodeId`/`ToNodeId`/`FromNode`/`ToNode`/`OtherNode`

### Phase 2: DirectionalGroundEdge
- [ ] Create `DirectionalGroundEdge` class
- [ ] Add factory method on `GroundEdge`: `Directed(fromNode, toNode)`
- [ ] Update `TaxiRouteSegment.Edge` type from `GroundEdge` to `DirectionalGroundEdge`
- [ ] Update all segment construction sites
- [ ] Remove `DestinationNode`/`OriginNode` from `TaxiRouteSegment`

### Phase 3: Navigator + phases
- [ ] Simplify `GroundNavigator` to use `seg.Edge.ToNode` directly
- [ ] Update all phase code that accesses segment edges
- [ ] Update `VirtualNode.CreateEdge`/`CreateSegment` to produce `DirectionalGroundEdge`

### Phase 4: Tests + cleanup
- [ ] Update all test edge construction
- [ ] Remove `WireEdgeNodeReferences` (no longer needed — `Nodes` set at construction)
- [ ] Remove `TaxiRoute.WireEdgeNodeReferences`
- [ ] Snapshot serialization: serialize `Nodes[0].Id`/`Nodes[1].Id` + direction indicator

## Serialization consideration

`TaxiRouteDto` currently stores `FromNodeId`/`ToNodeId` per segment. With the refactor:
- The DTO stays the same (IDs for serialization)
- `FromSnapshot` creates `DirectionalGroundEdge` by looking up the edge in the graph and choosing direction based on the segment's from/to IDs
