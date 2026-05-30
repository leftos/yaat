# Ground Layout Generation and Fillet Arc Processing

Comprehensive technical reference for the GeoJSON parser, fillet arc generator, and ground graph construction in YAAT. This document describes the complete pipeline from GeoJSON import through bezier arc generation and edge reconstruction.

## Table of Contents

1. [Pipeline Overview](#pipeline-overview)
2. [GeoJSON Parser](#geojson-parser)
3. [Data Model: AirportGroundLayout](#data-model-airportgroundlayout)
4. [Fillet Arc Generator](#fillet-arc-generator)
5. [TaxiwayWalk and Edge Walking](#taxiwaywalk-and-edge-walking)
6. [Known Issues and Limitations](#known-issues-and-limitations)
7. [Diagnostic Tools](#diagnostic-tools)

---

## Pipeline Overview

The ground layout pipeline transforms GeoJSON geographic data into a connected graph with smooth bezier arcs at intersections:

```
GeoJSON Input
    ↓
Step 1: Parse features (parking, taxiway, spot, runway)
    ↓
Step 2: Process taxiway coordinates → nodes + snapping
    ↓
Step 3: Detect taxiway-taxiway intersections
    ↓
Step 4: Build edges from taxiway segments
    ↓
Step 4b: Remove overlapping edges
    ↓
Step 5: Detect runway crossings → insert hold-short nodes
    ↓
Step 6: Connect parking/spots/helipads to nearest taxiway
    ↓
Step 7: Wire up node adjacency lists
    ↓
Step 8: Generate fillet arcs at intersections
    ↓
AirportGroundLayout (complete graph)
```

**Entry point**: `GeoJsonParser.Parse()` in `src/Yaat.Sim/Data/Airport/GeoJsonParser.cs`

**Critical invariant**: During Steps 2-6, `GroundNode.Edges` adjacency lists remain EMPTY. All edges are added to `layout.Edges` only. At the end of Step 7, `RebuildAdjacencyLists()` populates each node's `Edges` list from the complete `layout.Edges` collection. This timing is essential because earlier steps need to perform lookups on `layout.Edges` directly.

---

## GeoJSON Parser

File: `src/Yaat.Sim/Data/Airport/GeoJsonParser.cs`

### Input Format

The parser expects a GeoJSON FeatureCollection with features typed via `properties.type`:

- **`parking`** — Point geometry. Required properties: `name`, `heading` (integer degrees true). Optional: heading may be a string that needs parsing.
- **`taxiway`** — LineString geometry. Required: `name` (taxiway letter/number, e.g., "C", "W3").
- **`spot`** — Point geometry. Required: `name` (intersection or spot identifier).
- **`runway`** — LineString geometry. Required: `name` (e.g., "28R/10L", both ends).
- **`helipad`** — Point geometry. Required: `name`, `heading`. Treated like parking with larger connection radius.

**Coordinate System**: All coordinates are `[longitude, latitude]` per GeoJSON spec. The parser converts to `(latitude, longitude)` internally.

**JSON Preprocessing**: The parser strips leading zeros from numeric literals (e.g., `03` → `3`) using regex before parsing, handling invalid JSON that some GeoJSON sources produce.

### Step 1: Parse Features

`GeoJsonParser.Parse()` calls `JsonDocument.Parse()` and iterates features:

- Features are classified by `properties.type`
- Malformed features (missing required properties, invalid geometry) are logged and skipped
- Four lists are populated: `parkingFeatures`, `helipadFeatures`, `spotFeatures`, `taxiwayFeatures`, `runwayFeatures`

### Step 2: Process Taxiway Coordinates

**Function**: `TaxiwayGraphBuilder.ProcessTaxiway()`

For each taxiway LineString:

1. Walk the coordinate chain
2. Each coordinate becomes a node (or snaps to an existing node within `SnapToleranceDeg ≈ 0.00003°` / ~10ft)
3. Use `CoordinateIndex` for fast spatial lookups
4. Returns `ProcessedTaxiway` with the ordered list of node IDs

**Snapping**: Two coordinates within ~10ft snap to the same node. This prevents duplicate nodes at nearly-identical positions and enables intersection detection when taxiways overlap.

**CoordinateIndex**: Spatial hash index for O(1) nearest-neighbor lookups. Used throughout construction to snap incoming coordinates to existing nodes.

### Step 3: Detect Intersections

**Function**: `TaxiwayGraphBuilder.DetectIntersections()`

Pairwise line-segment intersection detection between all taxiway LineStrings:

1. For each pair of taxiway segments, compute line-segment intersection using parametric line equations
2. If segments intersect, insert a new node at the intersection point
3. The intersection node is inserted into both taxiways' node chains at the correct positions

**Example**: Taxiways C and H crossing at OAK create a shared junction node. Both C's and H's node lists reference this same node ID.

**Critical detail**: This step creates new nodes, which increases `layout.Nodes`. The processed taxiway chains are updated with the new node IDs so subsequent steps see the correct topology.

### Step 4: Build Edges

**Function**: `TaxiwayGraphBuilder.BuildEdgesFromTaxiway()`

For each processed taxiway, iterate consecutive node pairs and create `GroundEdge` objects:

- Edge: `GroundNode[FromNode, ToNode]`
- TaxiwayName: inherited from the taxiway feature
- DistanceNm: great-circle distance
- Origin: set to diagnostic string (e.g., "GeoJson:taxiway-edge")
- IntermediatePoints: empty (populated only by RunwayCrossingDetector for runway centerlines)

**Deduplication**: Duplicate edges (same endpoints, same taxiway) are skipped.

**Important**: Edges are added to `layout.Edges` ONLY. Node adjacency lists are still empty.

### Step 4b: Remove Overlapping Edges

**Function**: `RemoveOverlappingEdges()`

When two taxiways share an identical segment (same two nodes, different names), one must be removed:

1. Group edges by node pair (order-independent)
2. For each overlapping pair, count how many other edges each taxiway has at each endpoint
3. "Continues" = the taxiway has other edges at that node
4. Keep the taxiway that continues through both endpoints; remove the one that terminates

**Example**: At OAK, taxiways B and B5 might share a segment. If B continues past the segment (other edges at both ends) but B5 terminates (one edge or fewer), B's version is kept and B5's is removed.

### Step 5: Runway Crossing Detection

**Function**: `RunwayCrossingDetector.DetectRunwayCrossings()`

For each runway, this multi-step process inserts hold-short nodes and creates runway centerline connectivity:

#### Step 5a: Runway Rectangle Construction

Build an oriented rectangle from the runway LineString endpoints:

- Centerline: the line connecting the two endpoints
- Heading: bearing along the centerline
- Length: distance between endpoints
- Half-width: extracted from navdata (runway type → ADG group → width), default 150ft
- Hold-short distance: (width / 2) + 75ft from the centerline

#### Step 5b: Classify Nodes

For each node in the layout (excluding runway nodes themselves):

- Compute cross-track distance to the runway centerline (perpendicular distance from node to centerline)
- Compute along-track distance (signed distance along centerline; negative = before runway, positive = past runway)
- Node is "on-runway" if: cross-track distance ≤ (runway-width / 2) AND along-track distance within runway bounds
- Node is "off-runway" otherwise

#### Step 5c: Find Boundary Edges

Identify edges where one endpoint is on-runway and the other is off-runway. These are the crossing edges (excluding RWY-prefixed edges, which are already centerline segments).

#### Step 5d: Insert Hold-Short Nodes

For each boundary edge, process via `ProcessBoundaryEdge()`:

1. Compute the ideal hold-short position: perpendicular projection onto the boundary edge at the correct cross-track distance
2. **Reuse check**: If the off-runway node is close (within ~15ft) AND is not already a hold-short AND is not a junction (doesn't touch multiple taxiways), upgrade it to `RunwayHoldShort` type and `RunwayId`
3. **Otherwise**: Interpolate a new node at the hold-short distance on the boundary edge
4. **If interpolation needed**: Split the boundary edge via `SplitEdgeAtOneNode()` → two sub-edges connected through the new hold-short node

**Junction Protection**: A junction node is one connected to edges from multiple distinct taxiways (checked via `HasMultipleTaxiwayConnections()`, which scans `layout.Edges` since adjacency lists are empty at this stage). Junctions are NEVER reused as hold-short nodes, because aircraft holding short would block other taxiways. Instead, a new hold-short node is interpolated on the specific taxiway edge.

#### Step 5e: Connect On-Runway Nodes

**Function**: `ConnectOnRunwayNodes()`

Link the on-runway sides of each runway crossing. This allows aircraft to cross from one taxiway to another via the runway centerline:

1. For each crossing, find all on-runway nodes (from all taxiways crossing the same runway)
2. Sort by along-track position (from runway start to runway end)
3. Create RWY-prefixed centerline edges connecting them sequentially: `node1 ↔ node2 ↔ node3 ...`

This ensures that taxiways on opposite sides of a runway are connected through the centerline, allowing cross-runway taxi routes.

### Step 6: Connect Parking/Spots/Helipads

**Function**: `ConnectParkingToTaxiway()` and `ConnectToNearestTaxiway()`

For each parking/helipad/spot node:

1. Find the nearest non-parking, non-helipad, non-spot node (i.e., a taxiway intersection)
2. Check distance:
   - Parking: max 0.15 nm (~800ft)
   - Helipad: max 0.3 nm (~1600ft)
   - Spot: treated as existing nodes (created in Step 2)
3. If within threshold, create a RAMP edge to the nearest taxiway node

**RAMP edges**: Edges with `TaxiwayName = "RAMP"`. Used for parking connections, not part of normal taxi routes.

### Step 7: Wire Up Adjacency Lists

**Function**: `AirportGroundLayout.RebuildAdjacencyLists()`

The ONLY place where `GroundNode.Edges` is populated:

```csharp
foreach (var node in Nodes.Values)
{
    node.Edges.Clear();
}
foreach (var edge in AllEdges)  // Both GroundEdge and GroundArc
{
    if (Nodes.TryGetValue(edge.Nodes[0].Id, out var nodeA))
        nodeA.Edges.Add(edge);
    if (Nodes.TryGetValue(edge.Nodes[1].Id, out var nodeB))
        nodeB.Edges.Add(edge);
}
```

Each node's adjacency list is built from the complete `layout.Edges` collection. After this step, the graph is fully connected and ready for traversal.

### Step 8: Generate Fillet Arcs

**Function**: `FilletArcGenerator.Apply()`

Replaces eligible intersection nodes with bezier arcs at turns ≥15°. Details in [Fillet Arc Generator](#fillet-arc-generator) section.

---

## Data Model: AirportGroundLayout

File: `src/Yaat.Sim/Data/Airport/AirportGroundLayout.cs`

### GroundNode

```csharp
public sealed class GroundNode
{
    public int Id { get; init; }                                  // Sequential, assigned during parsing
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public GroundNodeType Type { get; set; }                      // TaxiwayIntersection, Parking, Spot, RunwayHoldShort, Helipad
    public string? Name { get; init; }                            // Parking/spot/helipad identifier
    public TrueHeading? TrueHeading { get; init; }                // Parking heading only
    public RunwayIdentifier? RunwayId { get; set; }               // RunwayHoldShort nodes only
    public List<IGroundEdge> Edges { get; init; } = [];           // Adjacency list, populated in Step 7
    public string? Origin { get; set; }                           // Diagnostic: which code path created this node
    public (double Lat, double Lon)? SourceIntersectionPosition;  // Fillet tangent-point origin (for merging)
}
```

**Node Types**:

- **TaxiwayIntersection**: Regular taxiway junction or shape-point node. Created by GeoJSON parser or FilletArcGenerator.
- **Parking**: Aircraft parking position. Created from `parking` GeoJSON features.
- **Spot**: Named intersection or waypoint. Created from `spot` GeoJSON features.
- **RunwayHoldShort**: Intersection with a runway. Created/upgraded during Step 5.
- **Helipad**: Helicopter landing zone. Created from `helipad` GeoJSON features.

**Node IDs**: Sequential integers assigned during parsing in order: spot nodes → taxiway nodes → intersection nodes → runway hold-short nodes → parking nodes → helipad nodes → tangent nodes (fillet) → merged/coincident nodes.

### IGroundEdge Interface

Common interface for straight and curved edges:

```csharp
public interface IGroundEdge
{
    GroundNode[] Nodes { get; }                             // Fixed-size 2, no direction
    string TaxiwayName { get; }                             // For IGroundEdge, TaxiwayName is well-defined for edges/arcs
    double DistanceNm { get; }
    bool MatchesTaxiway(string name);                       // Case-insensitive match
    bool IsRunwayCenterline { get; }                        // Runway centerline segment?
    bool MatchesRunway(string designator);                  // Matches runway identifier?
    bool IsRamp { get; }                                    // Parking connection edge?
    double MaxSafeSpeedKts(AircraftCategory category);      // Lateral-accel speed cap from curvature
    bool SharesTaxiway(IGroundEdge other);                  // Either taxiway matches?
    bool SameTaxiway(IGroundEdge other);                    // Exact taxiway identity?
    GroundNode OtherNode(GroundNode node);
    int OtherNodeId(int nodeId);
    bool HasNode(int nodeId);
    string? Origin { get; set; }                            // Diagnostic
    DirectionalEdge Directed(GroundNode from, GroundNode to);
}
```

### GroundEdge (Straight)

```csharp
public sealed class GroundEdge : IGroundEdge
{
    public required GroundNode[] Nodes { get; init; }        // [FromNode, ToNode], no direction
    public required string TaxiwayName { get; init; }        // "C", "W3", "RWY28R/10L", "RAMP"
    public required double DistanceNm { get; set; }          // Great-circle distance
    public List<(double Lat, double Lon)> IntermediatePoints { get; init; } = [];  // For runway centerlines only
    public string? Origin { get; set; }
}
```

**Properties**:

- **TaxiwayName**: Identifies the taxiway this edge belongs to. RWY-prefixed names are runway centerlines.
- **IsRunwayCenterline**: True if `TaxiwayName.StartsWith("RWY")` and not a `:link` marker
- **IsRamp**: True if `TaxiwayName == "RAMP"`

**IntermediatePoints**: Used only for runway centerline edges to store the original GeoJSON coordinates when the centerline is curved. Not used in navigation directly (the fillet arcs provide the curves at junctions).

### GroundArc (Bezier Curve)

```csharp
public sealed class GroundArc : IGroundEdge
{
    public required GroundNode[] Nodes { get; init; }                          // [TangentPoint0, TangentPoint1]
    public required double P1Lat { get; set; }                                 // Bezier control point 1
    public required double P1Lon { get; set; }
    public required double P2Lat { get; set; }                                 // Bezier control point 2
    public required double P2Lon { get; set; }
    public required double MinRadiusOfCurvatureFt { get; set; }                // Minimum radius along curve
    public required string[] TaxiwayNames { get; init; }                       // Length 1 or 2
    public required double DistanceNm { get; set; }                           // Arc length via polyline approximation

    // Fillet construction parameters (for recomputation after node merges)
    public double EdgeBearingAtNode0Deg { get; set; }                          // Bearing toward intersection (P1 projection dir)
    public double EdgeBearingAtNode1Deg { get; set; }                          // Bearing toward intersection (P2 projection dir)
    public double TurnAngleDeg { get; set; }                                   // Effective turn angle

    public string? Origin { get; set; }
}
```

**Bezier Curve**: A cubic Bezier curve is defined by four control points:
- P0 = `Nodes[0].(Lat, Lon)` — first tangent point
- P1 = `(P1Lat, P1Lon)` — control point (lies along edge-A direction)
- P2 = `(P2Lat, P2Lon)` — control point (lies along edge-B direction)
- P3 = `Nodes[1].(Lat, Lon)` — second tangent point

The curve is evaluated as: `B(t) = (1-t)³P0 + 3(1-t)²tP1 + 3(1-t)t²P2 + t³P3` for `t ∈ [0,1]`.

**TaxiwayNames**:
- Length 1: Same-taxiway arc (e.g., `["W"]` for a curve along taxiway W)
- Length 2: Junction arc between different taxiways (e.g., `["G", "RWY28R/10L"]` at a runway exit)

**TaxiwayName Property**: Returns a display name. Single name for same-taxiway, `"W - W3"` for junctions (uses " - " to avoid collision with "/" in runway identifiers).

**IsRunwayCenterline**: Always false — arcs are never centerline segments. They are either taxiway junctions or same-taxiway curves.

**IsRunwayJunction**: True if the arc connects a runway (TaxiwayName starts with "RWY") to a non-runway taxiway. Used to identify transitions from runway surface to taxiways.

### DirectionalEdge

A directional view of an edge, capturing a specific traversal direction:

```csharp
public sealed class DirectionalEdge
{
    public IGroundEdge Edge { get; init; }
    public GroundNode FromNode { get; init; }
    public GroundNode ToNode { get; init; }
    public double DepartureBearing { get; }    // Bearing leaving FromNode
    public double ArrivalBearing { get; }      // Bearing arriving at ToNode
}
```

For straight edges: departure and arrival bearings are the same (straight line). For arcs: bearings come from the bezier tangent direction at each endpoint.

### CubicBezier

File: `src/Yaat.Sim/Data/Airport/CubicBezier.cs`

Immutable struct representing a cubic bezier curve:

```csharp
public readonly struct CubicBezier(double p0Lat, double p0Lon, double p1Lat, double p1Lon,
                                   double p2Lat, double p2Lon, double p3Lat, double p3Lon)
```

**Key Methods**:

- **`Evaluate(double t)`**: Returns `(Lat, Lon)` at parameter t ∈ [0,1]
- **`Derivative(double t)`**: Returns `(dLat, dLon)` — tangent vector (unnormalized)
- **`TangentBearing(double t)`**: Bearing (degrees true) of the curve at t, in feet coordinates
- **`RadiusOfCurvatureFt(double t, double refLat)`**: Radius of curvature at t. Formula: κ = |x'y'' - y'x''| / (x'² + y'²)^(3/2), R = 1/κ
- **`MinRadiusOfCurvatureFt(double refLat, int samples)`**: Minimum radius along the curve (worst-case for speed constraints)
- **`ArcLengthNm(int segments)`**: Approximate arc length via polyline (sum of segment distances)
- **`ClosestT(double lat, double lon, int iterations)`**: Find the parameter t where the curve is closest to a point (for path following)

All curvature and bearing computations work in local Cartesian feet for accuracy.

---

## Fillet Arc Generator

File: `src/Yaat.Sim/Data/Airport/FilletArcGenerator.cs`

### Overview

The fillet arc generator replaces sharp intersections with smooth cubic bezier curves. It processes the graph in a single-pass-per-node algorithm divided into four phases:

1. **Phase A**: Evaluate edge pairs, compute tangent distances and radii (no graph mutation)
2. **Phase B**: Create tangent-point nodes on edges
3. **Phase C**: Construct bezier arcs from tangent points
4. **Phase D**: Rebuild edges, removing/shortening originals, creating tangent-links, optionally preserving intersection node

After all nodes are processed, a **Global Merge Pass** (5 iterations) merges coincident nodes within 5ft.

### Eligibility: IsEligibleForFilleting

A node is eligible for filleting if:

1. **Type**: Must be `TaxiwayIntersection` (not Parking, Spot, RunwayHoldShort, Helipad)
2. **Edges**: Must have ≥2 non-arc edges (GroundEdges only at this stage)
3. **Not a centerline projection**: Origin must not be `"RunwayCrossing:centerline-projection"`
4. **Not a shape-point node**: Not exactly 2 edges on the same taxiway (shape points preserve original GeoJSON curvature)
5. **Special cases**:
   - **Runway threshold**: Exactly 1 RWY edge + ≥1 taxiway edge → `preserveNode = true`. Arcs created, but intersection node stays connected via stub edges.
   - **Pure runway endpoint**: Exactly 1 RWY edge, no taxiway edges → ineligible (no turn to smooth)

**preserveNode flag**: Set to true for runway threshold nodes. When true, the intersection node is kept in the graph and connected to tangent points via short stub edges (instead of being deleted entirely). This allows aircraft rolling to the runway end to smoothly transition onto taxiways via arcs while keeping the threshold node reachable.

### Phase A: Pair Evaluation and Tangent Distance Computation

#### Step A1: Collect Edges and Compute Bearings

For each eligible node:

1. Collect all adjacent `GroundEdge` edges, deduplicating by `(OtherNodeId, TaxiwayName)` key
2. For each edge, compute the bearing FROM the intersection TOWARD the other node
3. Use `InitialBearing()` which accounts for intermediate points (curved edges use the first intermediate point instead of the far endpoint)

#### Step A2: Precompute Taxiway Walks

For each edge, call `WalkTaxiway()` to compute how far we can extend along the taxiway if the first edge is too short:

```csharp
var edgeWalks = new Dictionary<GroundEdge, TaxiwayWalkResult>();
foreach (var (edge, _, _) in edgeBearings)
{
    edgeWalks[edge] = WalkTaxiway(edge, intersection, manualArcNodes);
}
```

Returns: `TaxiwayWalkResult` with available length, steps, terminal node, and metadata about shape nodes encountered.

#### Step A3: Iterate Edge Pairs

For each pair (i, j) where i < j:

1. **Skip overlapping pairs**: If both edges go to the same node, skip (not a real turn)
2. **Compute turn angle**: `ComputeTurnAngle(bearingA, bearingB) = 180° - |bearingA - bearingB|`
   - 0° = straight opposite (180° apart)
   - 180° = U-turn (same direction)
3. **Collinear pairs** (turn < 15°): Add to `plannedMerges` (straight-through edge that becomes a single merged edge)
4. **Non-fillet pairs** (15° ≤ turn < 15°): Skip (only "near-collinear" case)
5. **Fillet pairs** (turn ≥ 15°): Compute arc parameters:

#### Step A4: Arc Parameter Computation (Fillet Pairs Only)

For turn angle θ:

```csharp
double halfAngleRad = (turnAngle / 2.0) * (Math.PI / 180.0);
double tanHalf = Math.Tan(halfAngleRad);
```

Compute maximum usable tangent distances:

```csharp
// Available length along walk (up to next intersection or manual arc boundary)
double availableAFt = walkA.AvailableLengthFt;
double availableBFt = walkB.AvailableLengthFt;

// Cap at half-edge-length if terminal node is filleted (prevents consuming shared edges)
bool capA = IsEligibleForFilleting(walkA.TerminalNode) && (walkA.TerminalNode.SourceIntersectionPosition is null);
double maxTangentAFt = capA ? availableAFt / 2.0 : availableAFt;
double maxTangentBFt = capB ? availableBFt / 2.0 : availableBFt;

// Cap at distance to next intersection along each walk
double intersectionCapAFt = DistToFirstIntersectionFt(walkA);
double intersectionCapBFt = DistToFirstIntersectionFt(walkB);
maxTangentAFt = Math.Min(maxTangentAFt, intersectionCapAFt);
maxTangentBFt = Math.Min(maxTangentBFt, intersectionCapBFt);
```

Compute radius:

```csharp
// Maximum radius that fits the available tangent distance
double maxFitRadiusFt = Math.Min(maxTangentAFt, maxTangentBFt) / tanHalf;

// Type-based maximum radius
double maxRadiusFt = SelectMaxRadius(edgeA, edgeB, turnAngle);
// Returns: RampRadiusFt (50ft) for ramps,
//          HighSpeedExitRadiusFt (150ft) for runway exits ≤45°,
//          RunwayExitRadiusFt (100ft) for runway exits >45°,
//          DefaultRadiusFt (75ft) for taxiway junctions

double radiusFt = Math.Min(maxFitRadiusFt, maxRadiusFt);
```

Compute tangent distance:

```csharp
double tangentDistFt = radiusFt * tanHalf;

// Absolute cap (prevents unreasonably long arcs even with no intervening intersection)
if (tangentDistFt > MaxTangentDistFt)  // MaxTangentDistFt = 150ft
{
    tangentDistFt = MaxTangentDistFt;
    radiusFt = tangentDistFt / tanHalf;
}

// Skip degenerate pairs (radius collapsed to < 5ft)
if (radiusFt < 5.0) continue;
```

Add to `plannedArcs` list with `PlacementA` and `PlacementB` (computed next).

### Phase B: Tangent Node Creation

#### Step B1: Compute Tangent Placement

For each planned arc pair, compute where the tangent points land on the edges:

```csharp
var placementA = ComputeTangentPlacement(edgeA, intersection, bearingA, tangentDistNm, walkA);
var placementB = ComputeTangentPlacement(edgeB, intersection, bearingB, tangentDistNm, walkB);
```

**ComputeTangentPlacement()** returns a `TangentPlacement` struct:

```csharp
public sealed class TangentPlacement
{
    public double Lat { get; }                                  // Tangent point position
    public double Lon { get; }
    public double TangentDistNm { get; }                        // Distance from intersection
    public double? BearingTowardIntersectionDeg { get; }        // Bearing from tangent toward intersection
    public List<GroundEdge> WalkedEdges { get; }                // Edges consumed by the walk
    public List<GroundNode> WalkedShapeNodes { get; }           // Shape points encountered
    public List<GroundNode> PassthroughNodes { get; }           // Non-walk-consumed edges we passed through
    public GroundNode? WalkFarNode { get; }                     // Terminal node of the walk
    public bool LandsInManualArc { get; }                       // Tangent lands in a shape-point chain
    public GroundEdge? SplitEdge { get; }                       // Edge to split if landing in manual arc
}
```

**Logic**:

- If tangent distance ≤ first edge length: place tangent directly on the first edge (no walk needed)
- Otherwise: call `InterpolateAlongWalk()` to walk additional edges until reaching the desired distance

`InterpolateAlongWalk()` returns the tangent position and collects metadata about walked edges, shape nodes, and passthrough nodes.

#### Step B2: Get or Create Tangent Node

```csharp
var tanNodeA = GetOrCreateTangentNode(layout, edgeTangentNodes, edgeA, placementA, intersection, ref nextNodeId);
```

**Deduplication**: If an existing tangent node on the same edge is within 5ft of the desired position, reuse it. Otherwise, create a new `TaxiwayIntersection` node.

New nodes have:
- Origin: `"Fillet:tangent-node@{intersectionId} on-{taxiwayName}(→{otherNodeId})"`
- SourceIntersectionPosition: set to the intersection position (used for later coincident-node merging)

### Phase C: Bezier Arc Construction

For each arc pair with tangent nodes `tanNodeA` and `tanNodeB`:

```csharp
// Effective turn angle measured at tangent points
double bearingAToIntersection = placementA.BearingTowardIntersectionDeg ?? (bearingA + 180.0) % 360.0;
double bearingBToIntersection = placementB.BearingTowardIntersectionDeg ?? (bearingB + 180.0) % 360.0;
double effectiveTurnDeg = 180.0 - GeoMath.AbsBearingDifference(bearingAToIntersection, bearingBToIntersection);

// Kappa formula for cubic bezier approximation of a circular arc
double sweepRad = effectiveTurnDeg * (Math.PI / 180.0);
double kappa = (4.0 / 3.0) * Math.Tan(sweepRad / 4.0);

// Control point depth: how far along the tangent direction to place P1 and P2
double radiusNm = radiusFt / GeoMath.FeetPerNm;
double depthA = kappa * radiusNm;
double depthB = kappa * radiusNm;

// Project control points along the tangent directions
var (p1Lat, p1Lon) = GeoMath.ProjectPointRaw(tanNodeA.Latitude, tanNodeA.Longitude, bearingAToIntersection, depthA);
var (p2Lat, p2Lon) = GeoMath.ProjectPointRaw(tanNodeB.Latitude, tanNodeB.Longitude, bearingBToIntersection, depthB);

// Create bezier and compute curvature metrics
var bezier = new CubicBezier(tanNodeA.Latitude, tanNodeA.Longitude, p1Lat, p1Lon, p2Lat, p2Lon, tanNodeB.Latitude, tanNodeB.Longitude);
double minRadiusFt = bezier.MinRadiusOfCurvatureFt(tanNodeA.Latitude, 10);
double arcLengthNm = bezier.ArcLengthNm(20);

// Create GroundArc
layout.Arcs.Add(new GroundArc
{
    Nodes = [tanNodeA, tanNodeB],
    TaxiwayNames = sameTaxiway ? [edgeA.TaxiwayName] : [edgeA.TaxiwayName, edgeB.TaxiwayName],
    P1Lat = p1Lat,
    P1Lon = p1Lon,
    P2Lat = p2Lat,
    P2Lon = p2Lon,
    MinRadiusOfCurvatureFt = minRadiusFt,
    DistanceNm = arcLengthNm,
    EdgeBearingAtNode0Deg = bearingAToIntersection,    // For validation after merges
    EdgeBearingAtNode1Deg = bearingBToIntersection,
    TurnAngleDeg = effectiveTurnDeg,
    Origin = $"Fillet:phase-c-arc@{intersection.Id} {edgeA.TaxiwayName}/{edgeB.TaxiwayName}",
});
```

### Phase D: Edge Reconstruction

After arcs are created, the original edges must be reconstructed:

1. **Remove original edges** at the intersection
2. **Shorten edges** that have tangent points: replace `intersection ↔ otherNode` with `otherNode ↔ tanNodeA`
3. **Create tangent-links**: connect multiple tangent nodes on the same edge
4. **Merge collinear pairs**: create straight edges directly connecting the endpoints of collinear pairs
5. **Reconnect orphaned edges**: edges not touched by filleting (e.g., parking edges) are reconnected to the nearest tangent node
6. **Preserve stubs** (if `preserveNode = true`): keep short edges from intersection to tangent points

#### Step D1: Process Edges with Tangent Points

For each edge with tangent points (sorted by distance from intersection, farthest first):

**For walked edges** (tangent distance > first edge length):

```csharp
if (farthest.Placement.LandsInManualArc)
{
    // Tangent lands in a shape-point chain
    // Split the edge where the tangent lands; keep the chain intact
    var splitEdge = farthest.Placement.SplitEdge;
    // Create sub-edges: splitNodeA→tangentNode, tangentNode→splitNodeB
    // Consume the split edge; keep the rest of the chain
}
else
{
    // Standard walk: consume walked edges, remove walked shape nodes
    // Create passthrough edges: ptNode→tangentNode
    // Shorten edge: farNode→tangentNode
}
```

**For multiple tangent points on same edge** (e.g., near-collinear pair creates large tangent, genuine turn creates small tangent):

- Sort by distance (farthest first)
- Create tangent-link edges connecting them: `tan1 ↔ tan2 ↔ ... ↔ tanN`
- Skip tangent-links that span manual arc chains (the chain edges provide connectivity)

#### Step D2: Merge Collinear Pairs

If `plannedMerges.Count > 0`, set `preserveNode = true` (stubs will provide straight-through connectivity).

```csharp
if (preserveNode)
{
    // Consume original collinear edges; stubs will replace them
    foreach (var (edgeA, _, edgeB, _) in plannedMerges)
    {
        consumedEdges.Add(edgeA);
        consumedEdges.Add(edgeB);
    }
}
else
{
    // Create merged edges: endA ↔ endB
    // endA/endB = tangent point (if pair has tangent) or original endpoint
}
```

#### Step D3: Reconnect Orphaned Edges

Edges not in `consumedEdges` (e.g., parking connections) are reconnected to the nearest tangent node or merge endpoint.

#### Step D4: Remove Original Edges and Node Deletion

```csharp
layout.Edges.RemoveAll(e => consumedEdges.Contains(e));

if (preserveNode)
{
    // Create stubs from intersection to nearest tangent on each edge
    // Handle collinear merge endpoints without tangents
}
else
{
    // Delete intersection node and all edges/arcs referencing it
    layout.Nodes.Remove(intersection.Id);
}
```

### Known Naming Confusion: "ManualArcNodes" and Shape Points

**Critical terminology issue**: The function `DetectManualArcNodes()` returns a set of node IDs, but the name is misleading. The function actually detects **shape-point nodes** (2 edges on same taxiway) which preserve original GeoJSON curvature. The name "manualArcNodes" (plural) suggests chains, but the returned set contains individual shape-point IDs.

Despite the name, these nodes are protected:

- **During filleting**: Shape-point nodes are skipped from filleting (don't have tangent points created for them)
- **During walks**: When extending along a taxiway for tangent placement, the walk stops if it encounters a shape-point chain (set `LandsInManualArc = true`), and the chain edges are not consumed
- **During edge rebuild (Phase D)**: If tangent lands in a shape-point chain, the chain is split but not removed; passthrough edges bypass the chain

The flag `LandsInManualArc` in `TangentPlacement` means "the tangent point lands on an edge that's part of a shape-point chain (manual arc)".

### Global Merge Pass: MergeCoincidentNodes

After all nodes are filleted, a 5-iteration loop merges coincident `TaxiwayIntersection` nodes within 5ft:

```csharp
for (int pass = 0; pass < 5; pass++)
{
    var mergeMap = BuildMergeMap(layout, thresholdNm: 5ft);
    if (mergeMap.Count == 0) break;

    // Rewrite edge/arc node references
    // Translate bezier control points
    // Remove self-loops, degenerate arcs, duplicates
    // Remove orphaned nodes
}
```

**Issue #4 (known limitation)**: When merging, bezier control points P1/P2 are translated by the victim→survivor delta:

```csharp
if (k == 0)  // P0 endpoint
{
    arc.P1Lat += dLat;  // Translate P1
    arc.P1Lon += dLon;
}
```

This preserves the tangent handle vector (P1-P0) but doesn't account for the changed chord geometry. **Fix**: Recompute P1/P2 using the Phase C formula with stored `EdgeBearingAtNode0Deg`, `EdgeBearingAtNode1Deg`, and `TurnAngleDeg`.

---

## TaxiwayWalk and Edge Walking

File: `src/Yaat.Sim/Data/Airport/FilletArcGenerator.cs` (helpers section)

When computing tangent placement, if the desired tangent distance exceeds the first edge length, we "walk" along the taxiway chain to find continuation edges.

### WalkTaxiway

Returns `TaxiwayWalkResult`:

```csharp
public sealed class TaxiwayWalkResult
{
    public List<TaxiwayWalkStep> Steps { get; }                         // Edges and their cumulative distances
    public GroundNode TerminalNode { get; }                            // Where the walk terminates
    public double AvailableLengthFt { get; }                           // Total walked length
    public List<GroundNode> EncounteredShapeNodes { get; }             // Shape-point nodes along the way
    public bool EndsInManualArc { get; }                               // Walk terminated at shape-point chain
}
```

**Walk algorithm**:

1. Start from the edge's far node (away from intersection)
2. At each node, find the continuation edge with matching taxiway name
3. Shape-point nodes are not consumed as walk endpoints, but they stop the walk (they mark the boundary of the original curve geometry)
4. Other intersection nodes terminate the walk
5. Accumulate distance and edge/node metadata

**Shape-point protection**: When the walk encounters a shape-point node (2 edges on same taxiway, no other connections), it:

- Sets `EndsInManualArc = true`
- Stops walking (doesn't consume the shape-point chain)
- Returns the edge that would have been consumed next as `SplitEdge`

This ensures original taxiway curves are preserved and tangent points split the edge at the correct location rather than consuming the curve.

### InterpolateAlongWalk

Given a walk result and desired distance, find the position along the walk:

```csharp
var (lat, lon, bearing, walkedEdges, walkedShapeNodes, passthroughNodes, walkFarNode, landsInManualArc, splitEdge) =
    InterpolateAlongWalk(walk, intersection, tangentDistFt);
```

**Logic**:

1. Iterate walk steps, accumulating distance
2. When cumulative distance exceeds desired distance, interpolate on that edge
3. Return the tangent position and metadata about which edges were walked

---

## Known Issues and Limitations

### ~~Issue #4: MergeCoincidentNodes Control Point Translation~~ — FIXED

The ~980 OAK / ~1909 SFO tangent-misaligned warnings were caused by two validator bugs, not by the merge translation:

1. The validator compared arc tangent direction against adjacent edge departure direction, expecting parallelism. But at a fillet arc endpoint they're anti-parallel (~180° apart) — correct smooth geometry.
2. The validator scanned all same-taxiway edges at the node, producing false positives at RAMP nodes with multiple edges radiating in different directions.

**Fix**: Rewrote `CheckArcTangentAlignment` to compare the arc tangent against the stored construction bearing (`EdgeBearingAtNode0Deg`/`EdgeBearingAtNode1Deg`), accepting both parallel and anti-parallel alignment. Also fixed the stored bearings to use the actual `bearingToIntersection` (which accounts for walked tangent placements) instead of the outbound edge bearing. Result: 0 tangent-misaligned warnings at both OAK and SFO.

### Issue #12: Disconnected K/F Subgraph

**Severity**: Low

**Description**: Two nodes on taxiway K/F are disconnected from the main graph at some airports.

**Root cause**: Likely related to shape-point handling. When shape-point nodes are flagged as `LandsInManualArc` but not properly integrated into Phase D edge rebuild, they can become orphaned.

**Status**: Rare; requires investigation with per-airport traces.

### Issue #18: Exit BFS Skips Fillet Arcs

**Severity**: High (navigation quality)

**Description**: The runway exit pathfinder uses BFS (`FindAdjacentHoldShort`) to find taxiway paths from the centerline. At fillet intersections, the original intersection node is replaced by two tangent nodes connected by an arc. However, Phase D preserves a straight shortcut edge from the original intersection location to one of the tangent nodes (for shape-point protection). The BFS picks the straight shortcut and never enters the arc, resulting in poor path quality (72ft cross-track deviation on OAK 28R exit G).

**Example**: OAK node 359 (G/RWY28R intersection) has edges:
- To node 1288 via G (straight, 0.0152nm) — matches "G" filter ✓
- To node 1289 via RWY28R/10L (straight, 0.0152nm) — doesn't match "G" ✗

The BFS from 359 takes the G edge to 1288 and never reaches 1289, skipping the arc 1289→1288.

**Fix options**:
1. Recognize preserved straight edges and prefer arc paths
2. Use the full taxi pathfinder (TaxiPathfinder) instead of BFS — it's arc-aware and would select better routes
3. Mark preserved edges so BFS can deprioritize them

---

## Diagnostic Tools

### Layout Inspector CLI

File: `tools/Yaat.LayoutInspector/Program.cs`

Query the ground graph from the command line:

```bash
dotnet run --project tools/Yaat.LayoutInspector -- <airport-geojson-path> [options]
```

**Key options**:

- `--node N` — Inspect node N: position, edges, type, name
- `--taxiway T` — List all edges on taxiway T
- `--runway 28R` — Runway 28R: endpoints, orientation, width, centerline nodes
- `--exits 28R` — Find all exits from runway 28R
- `--path N T1 T2` — Multi-hop exit path from node N via taxiways T1, T2, etc.
- `--parking` — List parking positions
- `--spots` — List named spots
- `--debug-fillets` — Trace fillet pair evaluation, tangent placement, Phase D edge rebuild
- `--validate` — Show validation warnings (tangent alignment, degenerate arcs, disconnected subgraphs)
- `--dump > file.json` — Dump entire airport graph to JSON
- `--ticks <csv>` — Animate tick-by-tick navigation with hoverable diagnostics

### Validation Warnings

Enabled via `--validate` on LayoutInspector:

- **arc-tangent-misaligned** (0 at OAK, 0 at SFO): Arc tangent deviates >15° from construction bearing (Issue #4 — fixed)
- **degenerate-arc** (radius < 5ft): Arc collapsed after node merge, removed
- **disconnected-subgraph**: Node orphaned (Issue #12)

### Test Coverage

E2E tests in `tests/Yaat.Sim.Tests/AirportE2ETests.cs`:

- `OAK_HoldShortNodes_NotAtJunctions` — Hold-short nodes never sit at multi-taxiway junctions
- `OAK_NoDuplicateEdgesOnNodes` — No node has duplicate edges in adjacency list
- `OAK_TaxiDF_MultipleHoldShorts_CrossesRunway15_33` — Runway crossing detection
- `GenuineTurnArcs` — No degenerate arcs, all turns are valid
- `OakAllExitsTests` — 8 exit smoothness tests with path deviation metrics

### NavTickDiag

Per-tick diagnostics on aircraft state during ground navigation:

```csharp
public sealed class NavTickDiag
{
    public GroundNode? CurrentNode { get; set; }
    public GroundNode? NextNode { get; set; }
    public double TargetBearing { get; set; }
    public double CurrentBearing { get; set; }
    public double SteerTarget { get; set; }
    public double PathDeviationFt { get; set; }        // Distance from AC to route segment
    public string EdgeType { get; set; }               // "straight" or "arc"
    public double? ArcRadius { get; set; }
}
```

Recorded via `TickRecorder` CSV export for analysis and visualization.

---

## References

- **CLAUDE.md**: Ground layout generation requires Step 2-6 to operate on `layout.Edges` only, with `GroundNode.Edges` populated only in Step 7
- **fillet-regressions-master.md**: Open issues and diagnostic traces
- **landing-and-runway-exit.md**: Landing rollout and runway exit design (uses ground graph)
- **aviation-constants.md**: Turn rates, climb/descent rates, aircraft performance
