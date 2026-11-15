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
Step 6: Connect parking/helipads to a lead-in vertex or the nearest taxiway
    ↓
Step 6c: Add the sidecar spot-edge chains
    ↓
Step 7: Wire up node adjacency lists
    ↓
Step 8: Generate fillet arcs at intersections
    ↓
Step 9: Mark each runway bar as hold line or short bar
    ↓
AirportGroundLayout (complete graph)
```

**Entry points**: the `GeoJsonParser.Parse*` overloads (`Parse`, `ParseWithDiagnostics`, `ParseMultiple`) in `src/Yaat.Sim/Data/Airport/GeoJsonParser.cs`, which all build through `ParseCollectingIssues` / `BuildLayout`.

**Inputs**: besides the map's GeoJSON, the airport id and the code runway widths are looked up by, every overload takes a `LayoutInputs(StandLeadIns, VideoMap, SpotEdges)` (`LayoutInputs.cs`), all required.

`StandLeadIns` is the airport's sidecar `standLeadIns` (Step 6); `SpotEdges` is its sidecar `spotEdges` chains (Step 6c, `AirportSidecarCatalog.GetSpotEdges`), empty when it has none and set at every production site; `VideoMap` is its tower-cab videomap (`TowerCabVideoMap.Read`), or null when it has none.

Step 5 pairs the videomap's painted bars with the generated hold-short points (Step 5d). yaat-server passes the map it fetched (`TowerCabMapDownloader`), and the test harness, the precompute builder, `GroundMapCheck` and LayoutInspector pass `VideoMap: null`.

**Critical invariant**: During Steps 2-6, `GroundNode.Edges` adjacency lists remain EMPTY. All edges are added to `layout.Edges` only. At the end of Step 7, `RebuildAdjacencyLists()` populates each node's `Edges` list from `AllEdges` (`layout.Edges` plus the arcs, which exist only after Step 8 and join the lists when it rebuilds them). This timing is essential because earlier steps need to perform lookups on `layout.Edges` directly.

---

## GeoJSON Parser

File: `src/Yaat.Sim/Data/Airport/GeoJsonParser.cs`

### Input Format

The parser expects a GeoJSON FeatureCollection with features typed via `properties.type`:

- **`parking`** — Point geometry. Required property: `name`. Optional: `heading` (integer degrees true, or a numeric string); absent, JSON null, empty or unparseable derives a nose-in heading from the stand's connection (Step 6), an explicit 0 being kept as authored.
- **`taxiway`** — LineString geometry. Required: `name` (taxiway letter/number, e.g., "C", "W3").
- **`spot`** — Point geometry. Required: `name` (intersection or spot identifier).
- **`runway`** — LineString geometry. Required: `name` (e.g., "28R/10L", both ends). Optional: `turnoff`, `noTurnoff`, `patternAltitude`, `patternSize`, `holdShortDistance`, and `threshold` — displaced-threshold distances in feet as `"end1 - end2"` in the same end order as the name (e.g. `"0 - 957"`).
- **`helipad`** — Point geometry. Required: `name`, `heading`. Treated like parking with larger connection radius.

**Coordinate System**: All coordinates are `[longitude, latitude]` per GeoJSON spec. The parser converts to `(latitude, longitude)` internally.

**JSON Preprocessing**: The parser strips leading zeros from numeric literals (e.g., `03` → `3`) using regex before parsing, handling invalid JSON that some GeoJSON sources produce. It also skips `//` line comments, so CMH's map — which carries `///Parking///` header lines inside its feature array — parses.

**Key and value leniency**: Property **keys** are looked up case-insensitively (exact match first, then an `OrdinalIgnoreCase` scan), so an ATCTrainer export whose keys are `Name`/`Heading`/`Turnoff`/`NoTurnoff`/`HoldShortDistance`/`PatternAltitude`/`PatternSize` parses identically to the vNAS maps' `name`/`heading`/`turnoff`/…. The values of `type` are still matched exactly, and a value's kind is still enforced: a `name` given as a JSON number is read as its raw text (`1` → `"1"`), while a missing name or any other kind is logged and skips that feature.

**Displaced thresholds**: A runway's `Coordinates` endpoints are always the **pavement** ends, and so are
the `RunwayInfo.ThresholdLatitude`/`Longitude` the nav database builds from vNAS (probed against the shipped
airport maps, they agree within ~5 ft). The authored `threshold` displacement is kept separately on
`GroundRunway.ThresholdDisplacementFtByEnd`; `LandingThreshold.Resolve(runway, layout)` is what turns a
`RunwayInfo` into its landing threshold. **Which datum a caller wants is a real decision** — see
[`landing-and-runway-exit.md`](landing-and-runway-exit.md#displaced-thresholds-which-datum) for the table.
Short version: arrivals (approach, flare, pattern abeam/base, approach gate, §3-10-3 landing spacing) use
the landing threshold; departures and anything covering the physical surface (runway rendering,
`RunwayCrossingDetector`, `TakeoffPhase`, `LineUpGeometry`, runway-intersection distances, the
`ConflictAlertDetector` corridor) stay on the pavement end.

### Step 1: Parse Features

`GeoJsonParser.ParseCollectingIssues` (behind `Parse` and `ParseWithDiagnostics`) calls `JsonDocument.Parse()` and `ClassifyFeatures` iterates the features (`ParseMultiple` classifies its merged files' features directly):

- Features are classified by `properties.type`
- A feature whose parse throws `InvalidOperationException` (for example a wrong value type) is logged and skipped; no other exception type is caught per feature
- Five lists are populated: `parkingFeatures`, `helipadFeatures`, `spotFeatures`, `taxiwayFeatures`, `runwayFeatures`

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
- Hold-short distance: the runway feature's authored `holdShortDistance` from the centerline, unless it is far below the standoff rule's value, else the rule (`HoldShortStandoff`: runway width, length, CIFP ILS category and elevation; [hold-short-placement.md](ground/hold-short-placement.md))

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

0. **Pair with paint** (`SeatCrossing`, `RunwayPaintPairing`): with a videomap, the crossing is paired with the first painted bar on its taxiway that the placement can seat (innermost along the taxiway first, snap point within D_rule − 60 ft … D_rule + 100 ft), and the ideal position below is that bar's snap point, D_paint, instead of the rule's D_rule. The node records `PaintSegmentIndex`. Details: [hold-short-placement.md](ground/hold-short-placement.md#seating-on-the-painted-bar)
1. Compute the ideal hold-short position: perpendicular projection onto the boundary edge at the correct cross-track distance
2. **Reuse check**: A paint-paired bar first merges a plain taxiway shape point within 6 ft of its snap point (moved onto the snap point from the runway side unless that crowds a neighbour within 5 ft; [hold-short-placement.md](./ground/hold-short-placement.md)). Otherwise, if the off-runway node is close (within `HoldShortSnapFt`, 5 ft; for a paint-paired bar, to its snap point and not more than 1 ft on the runway side of it) AND is not already a hold-short AND is not a junction (doesn't touch multiple taxiways), upgrade it to `RunwayHoldShort` type and `RunwayId`
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

**Functions**: `ConnectParkingToTaxiway()`, `ConnectStandLeadIn()`, `ConnectToNearestTaxiway()` and `FindNearestConnectorTarget()`

Spots are existing nodes (created in Step 2) and get no connector. For each parking and helipad node:

1. A parking stand its airport's sidecar lists in `standLeadIns` (`UsableStandLeadIns` keeps an entry that names exactly one parking stand) connects to the vertex nearest the entry's point among the nodes with an edge of the named taxiway that `CanConnectToEdge` allows, within 0.15 nm of the point; that vertex is the stand's foot. A bad entry warns and falls through to step 2. Helipads never take a lead-in.
2. Otherwise find the taxiway edge nearest the node by perpendicular distance, among the edges `CanConnectToEdge` allows: never a RAMP-named line, a runway centreline, a runway crossing link, or an edge touching a parking or helipad node.
3. Check the perpendicular distance: parking max 0.15 nm (~800ft), helipad max 0.3 nm (~1600ft).
4. If within the threshold, create a RAMP edge (origin `GeoJson:parking-connector`) to that edge's nearer vertex, or its farther one when the nearer is within the fillet no-op distance of the node. No edge is split.

**Derived heading** (`ApplyDerivedStandHeading`): a parking stand whose GeoJSON heading is absent, JSON null, empty or unparsable gets its true heading from the bearing of the foot back to the stand (nose-in, facing away from the pavement it rolled in from), rounded to whole degrees with 360 as 0, set through `GroundNode.SetTrueHeading` once the stand is connected. The foot is, in order: the connection foot (the sidecar lead-in vertex, else the nearest-taxiway connector foot); the perpendicular foot on the nearest eligible taxiway edge at any distance (heading only, the stand stays unconnected); else the heading stays 0 with a warning. A foot under 1 ft from the stand falls to the next source. An authored heading, 0 included, is kept; a helipad with no heading stays 0. A derived stand classifies as a push-back stand by the rule below; a GA ramp that is really pull-through takes a sidecar `standDeparture` override, never a name rule in the parser.

A parking stand's departure (`StandDepartures.Classify`) is then classified from the foot of the perpendicular onto the edge its connector joined, or from the lead-in vertex.

After all the parking nodes and helipads, `ConnectGateGroupsToAnchors()` adds `GeoJson:gate-group-connector` RAMP edges joining each gate to the connected anchor of its gate group. The layout build reads the sidecar's `standLeadIns` and `spotEdges` only; see [`standLeadIns`](../src/Yaat.Sim/Data/ARTCCs/README.md#standleadins).

**RAMP edges**: Edges with `TaxiwayName = "RAMP"`. Used for parking connections, not part of normal taxi routes.

### Step 6c: Sidecar Spot-Edge Chains

**Function**: `SpotEdgeBuilder.Apply()` (`SpotEdgeBuilder.cs`)

Runs after the stand and gate-group connectors, so every stand keeps the connection it gets without the chains, and before the adjacency lists and fillets. Each chain of the airport's sidecar `spotEdges` has its points resolved to nodes: a spot, the node two taxiways share, a taxiway's vertex nearest a position (within 0.15 nm), or a position, which reuses a node within the snap tolerance or mints a new node.

Consecutive points are joined by one straight edge named after the chain, origin `Sidecar:spot-edge`. The fillet pass re-mints an edge it cuts at a junction under its own origin.

A plain spot link is a `RAMP` edge; a charted lane carries its chart name, and the sidecar's `nonMovementTaxilanes` marks it non-movement when it is apron. A pair already joined by an edge of that name gets no second edge.

An entry whose point does not resolve, resolves to a runway hold-short, parking, helipad or runway centreline node, is a `point` within snap tolerance of a stand or helipad, or repeats a node in consecutive points is skipped whole with a warning. The loader refuses a chain named with an `RWY` prefix or a crossing-link suffix. Schema: [`spotEdges`](../src/Yaat.Sim/Data/ARTCCs/README.md#spotedges).

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

Each node's adjacency list is built from the complete edge collection (`AllEdges`); the same call builds the taxiway-to-nodes index behind `GetNodesOnTaxiway` and clears the exit search's memos. After this step, the graph is fully connected and ready for traversal.

### Step 8: Generate Fillet Arcs

**Function**: `FilletArcGenerator.Apply()`

Replaces eligible intersection nodes with bezier arcs at turns ≥15°. Details in [Fillet Arc Generator](#fillet-arc-generator) section.

### Step 9: Mark Short Bars

**Function**: `RunwayCrossingDetector.MarkShortBars()`

With every node in its final place, sets `GroundNode.IsShortBar` on each `RunwayHoldShort` node whose runway is in the layout: true for a short fallback bar inside the holding distance, false for the runway's hold line (a paint-paired bar is always false). See [ground/hold-short-placement.md](ground/hold-short-placement.md).

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
    public bool? IsShortBar { get; set; }                         // RunwayHoldShort nodes of a runway in the layout: short fallback bar (true) or hold line (false); set in Step 9, serialized only when non-null
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

### Stand fit

File: `src/Yaat.Sim/Data/Airport/StandFit.cs`

`StandFit.MaxWingspanFt` estimates the largest wingspan a Parking node takes from the layout's stand spacing, and `Fits` compares an aircraft's wingspan to it.

- **Neighbours**: another Parking node within 300 ft that sits beside the stand (its offset along the stand's heading no larger than its offset across it). Alternates of the stand do not count, so alternates never cap each other: two stands under 35 ft apart, or two whose names differ only by trailing letters (`9-4`, `9-4A`) under 150 ft apart. A stand with no heading counts every neighbour in range at its full distance.
- **Span**: the smallest lateral gap to a counting neighbour, taken as shared by two aircraft of the same span, less the wingtip clearance of the design group the span falls in (10 ft for ADG I and II, 15 ft for III, 25 ft for IV to VI), capped at that group's wingspan ceiling. Never below ADG I's ceiling.
- **No neighbour**: the airport's ceiling, the design group of its widest runway. The estimate never exceeds that ceiling.
- **Override**: a sidecar `parkingAreas[].maxAdg` replaces the estimate for the area's stands, above or below it (an override above the airport ceiling is logged and used). See [`ARTCC_CUSTOMIZATION.md`](../ARTCC_CUSTOMIZATION.md).

The clearances are ICAO Annex 14 stand clearances by code letter, which share ADG I to VI's span bands: industry practice, not an FAA figure.

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

The generator is `FilletArcGenerator` (`src/Yaat.Sim/Data/Airport/FilletArcGenerator.cs`, entry point `Apply`), a plan-then-execute pipeline over the types in `src/Yaat.Sim/Data/Airport/Fillet/`. Its design, eligibility and runway-preserve rule (including the collinear through-pair rule), plan building and execution are documented in [ground/fillet-generator.md](ground/fillet-generator.md). The earlier pair-based Legacy generator (Phase A-D) was removed and is not described here.

---

## TaxiwayWalk and Edge Walking

File: `src/Yaat.Sim/Data/Airport/Fillet/TaxiwayWalk.cs`

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

**Description**: The runway exit pathfinder uses BFS (`FindAdjacentHoldShort`) to find taxiway paths from the centerline. At fillet intersections, the original intersection node is replaced by two tangent nodes connected by an arc.

However, Phase D preserves a straight shortcut edge from the original intersection location to one of the tangent nodes (for shape-point protection). The BFS picks the straight shortcut and never enters the arc, resulting in poor path quality (72ft cross-track deviation on OAK 28R exit G).

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
public record NavTickDiag(
    int TargetNodeId, double DistToTargetNm, double BearingToTargetDeg, double AngleDiffDeg,
    double TargetSpeedKts,
    double BrakingLimitKts,    // braking-curve ceiling this tick; double.MaxValue when none computed
    double ArcSpeedLimitKts,   // fillet arc cornering-speed cap; double.MaxValue off an arc / none computed
    bool OnArc, double NodeRequiredSpeedKts, double PathDeviationFt, double SegFromLat, double SegFromLon);
```

Recorded via `TickRecorder` JSON export for analysis and visualization.

---

## References

- **CLAUDE.md**: Ground layout generation requires Step 2-6 to operate on `layout.Edges` only, with `GroundNode.Edges` populated only in Step 7
- **fillet-regressions-master.md**: Open issues and diagnostic traces
- **landing-and-runway-exit.md**: Landing rollout and runway exit design (uses ground graph)
- **aviation-constants.md**: Turn rates, climb/descent rates, aircraft performance

## File map

The `## Key files` lists in [ground/fillet-generator.md](ground/fillet-generator.md), [ground/pathfinder.md](ground/pathfinder.md) and [ground/navigator.md](ground/navigator.md) are curated extracts for reading those docs; this File map is the list to update when a file is added, moved or removed.

One entry per file; paths are relative to `src/Yaat.Sim/`.

```
# Data/
Data/TaxiRouteDefinition.cs    # Per-route value type (path tokens, destination, canonical-command synthesis) carried in the sidecar's taxiRoutes section.
Data/AirportSidecarDefinition.cs / AirportSidecarLoader.cs / AirportSidecarCatalog.cs  # Unified per-airport ground sidecar from Data/ARTCCs/{ARTCC}/Airports/{airport}.json (avoidTaxiways + taxiRoutes + implicitConnectors + crossingPairs + oneWayEdges + blockedTurns + adw + exitDirections (+ cargoOperators, gaOperators and parkingAreas, which feed the arrival-class and remark-parking exit side; gaOperators also feeds ArrivalParkingPicker's GA pool; + nonGateParking, the layout stand names ArrivalParkingPicker never picks)
                               # + exitCapacity + movementAreaTaxiways/nonMovementTaxilanes override sections
                               # + standDeparture per-stand PushBack/TaxiOut/Either overrides + standDepartureAreas runway-side rules, read via GetStandDepartureOverrides / GetStandDepartureAreas + standLeadIns per-stand taxiway-vertex connectors, read via GetStandLeadIns by GeoJsonParser at layout build + spotEdges named point chains (SpotEdgeChain: spot, junction, taxiway+near or point), validated by the loader and read via GetSpotEdges into LayoutInputs.SpotEdges
                               # + asdexLandingTaxiways (the taxiway names the ASDE-X taxiway-landing alert is limited to; absent = any taxiway, [] = none; two files for one airport union), read via GetAsdexLandingTaxiways by SimulationEngine.TickAsdexAlerts through AsdexConfiguredTaxiways.Filter; AirportSidecarLoader.LoadFiles parses a given file list).
                               # NavigationDatabase.AirportSidecars exposes GetAvoidedTaxiways / GetTaxiRoutes / GetImplicitConnectors / GetCrossingPairs / GetOneWayConstraints / GetBlockedTurns / GetAdwWindows / GetExitDirection / GetExitCapacity. avoidTaxiways read by SearchContext.Compile for auto routes only (two-pass: avoided taxiway used only when destination otherwise unreachable; explicit TAXI unaffected). taxiRoutes validated lazily at menu-build time via TaxiPathfinder.ResolveExplicitPath; surfaced in GroundView's right-click "Preset taxi route" submenu. implicitConnectors authorize a named connector (e.g. SFO LF) in SearchContext.BuildAuthorizedTaxiwaySet only when the cleared sequence places its two 'between' taxiways adjacent, AND let SegmentExpander.Run thread that connector instead of crossing at the taxiways' apex; SegmentExpander also runs adaptive bridge-entry selection (shallow pass + deep pass on dead-end finds — issue #396). blockedTurns forbid one intersection corner (the L/F apex) for AUTO + explicit routes and suppress its fillet arc in Ground View. exitDirections override the default exit (turn-off) side per landing runway end, optionally per arrivalClass (ga/cargo; GetExitDirection(airport, runway, class), with GetCargoOperators, GetGaOperators and GetParkingAreas, read by ArrivalExitSide) — consulted first by AirportGroundLayout.InferPreferredExitSide, ahead of the GeoJSON turnoff (whose reciprocal-end value is derived by flipping) and the layout heuristics (issue #405, KMIA 26R).
Data/ExitCapacityDefinition.cs # exitCapacity sidecar DTO (ExitCapacityEntry) and the validated ExitCapacityRule (runway, optional CrossoverRunway for the crossovers form, taxiway, maxAircraft, maxAircraftAboveCwt, cwtThreshold) the loader produces; read via AirportSidecarCatalog.GetExitCapacity.
Data/Airport/Precompute/PrecomputeKey.cs # Precompute cache key: GeoJSON md5, NavData serial, layout format version, layout and push-target algorithm hashes, the airport's sidecar hash, its layout sidecar hash (LayoutSidecarHash: the sidecar standLeadIns and spotEdges, AirportSidecarHash.ForLayoutSidecar), its tower-cab videomap hash (VideoMapHash, "" = no map) and its hold-short standoff-input hash (StandoffInputHash, HoldShortStandoff.InputHash, "" = no navdata runway);
                               # LayoutMatches (also compares LayoutSidecarHash, VideoMapHash and StandoffInputHash) / PushTargetsMatch (the push half also compares the sidecar hash); CurrentOffline / PushTargetsMatchOffline compare the offline fields only, for a reader with no vNAS.
Data/Airport/Precompute/PrecomputeEntry.cs / PushTargetEntry.cs # One airport's cache entry (key, parsed layout, per-stand per-design-group push targets) and one stand's targets for one design group: PushTargetEntry (stand, group, targets; empty for a taxi-out stand), PrecomputedPushTarget (kind, name, note, command, facings, path length, moves), PushTargetKind (Taxilane | Taxiway | Spot).
Data/Airport/Precompute/PushMoveEntry.cs # One move of a stored push plan as plain fields (kind, shape, tight, creep, dwell, point, line travel, stop, distance, facing, planned end), so a stored plan can be flown as the live push would fly it.
Data/Airport/Precompute/PushTargetPlanner.cs # Computes an airport's push targets: per named push-back stand with a heading and per design-group envelope, the bare PUSH <twy> and PUSH $spot targets within TugMovePlanner.MaxGoalDistanceFt that the tug planner accepts, planned as a live push would be with no other aircraft about; ComputeStand plans one stand and group, ComputeHeld an aircraft held where a push left it (from its pose, dropping targets ending within StayPutDistanceFt / StayPutHeadingDeg of it); a target over PathCapFt (3 x the envelope's length, floor 700 ft) is dropped; a TaxiOut stand gets empty lists.
Data/Airport/Precompute/PushTargetLiveCheck.cs # Menu-open check of a stored push target: Check re-flies its moves with the aircraft's own outline and turn radius and sweeps them against parked/held neighbours (TugNeighbourSweep), returning PushTargetLiveVerdict Clear | Blocked(callsign of the first blocker) | Unflyable (incomplete, or ending over half the aircraft's length from the stored end; a floating-stop-line end is judged on completion alone). Not in the hashed push set.
Data/Airport/Precompute/DesignGroupEnvelopes.cs # The per-design-group envelope (greatest length, span capped at the group's ceiling, wheelbase and main gear width over the group's fixed-wing FAA records, each taken separately) and the record that set each; Build makes them, Load reads the shipped Data/PrecomputeCache/design-group-envelopes.json, FootprintOf makes the ADG-I to ADG-VI footprint the planner plans with.
Data/Airport/Precompute/AirportSidecarHash.cs # SHA-256 of an airport's ground sidecars (Data/ARTCCs/{ARTCC}/Airports/*.json whose airportId matches): the key's PushSidecarHash, so a sidecar edit stales the push half only; ForLayoutSidecar / OfLayoutSidecar hash the standLeadIns (stand, taxiway, lat/lon) and the spotEdges chains (name and points), not notes, into LayoutSidecarHash, which stales the layout half too; an airport without chains hashes as its lead-ins alone.
Data/Airport/StandDeparture.cs # StandDeparture (PushBack | TaxiOut | Either) and StandDepartures: Classify (the geometric answer: the perpendicular foot of the parking connector less than 90 degrees off the stand's heading is TaxiOut; foot within 1 ft or exactly broadside is PushBack), StandDepartureOf (the one reader: the sidecar standDeparture override by stand name, else the first matching standDepartureAreas rule, else the node's stored answer), StandDepartureArea, WarnAboutOverrides.
Data/Airport/Precompute/PrecomputeEntryBuild.cs # Builds one airport's entry the way the server parses its map: CurrentKey, then Build (parse, plan push targets, write to the store). In both hashed source sets.
Data/Airport/Precompute/GeoJsonMd5.cs # The one definition of a map's key MD5: lowercase hex of the GeoJSON text as vNAS served it, UTF-8 without a BOM. In both hashed source sets.
Data/Airport/Precompute/PrecomputeBuilder.cs # The maintainer tool's testable work: Check (offline against the build-time source hashes, sidecar hash and layout format version; with PrecomputeOnlineFacts also the map MD5 and NavData serial) giving PrecomputeCheckResult statuses Current | Missing | Unreadable | LayoutStale | PushTargetsStale, and RefreshEnvelopes.
Data/Airport/Precompute/ScenarioAirportCollector.cs # The airports scenarios use: each primaryAirportId and each aircraft's airportId (not flight-plan airports), folded to FAA ids, distinct, sorted ordinal.
Data/Airport/Precompute/PrecomputeAirportList.cs # Reads and writes Data/PrecomputeCache/airports.txt: a # header line, then one FAA id per line, sorted, LF.
Data/Airport/Precompute/PushTargetSeed.cs # The shipped push-target seed for a caller with its own layout (the client): Find(airportId, stand, group) reads the airport's file once without materializing the layout and answers the entry only when the key is current offline (PrecomputeKey.PushTargetsMatchOffline); no file, a stale key, or no entry gives null (parse and open failures logged at Warning).
Data/Airport/Precompute/PushPlanStore.cs # The push plan of each push-back stand a jet or turboprop is parked on, made off the tick and read by the AI push rule: PlanParked (a scenario load's prepare and a reload, in parallel), PlanSpawned (a spawn, only with AI Ground active), TryGet / PlanStand (the rule's read and its on-tick fallback); keyed on layout identity, stand and design group; a derived cache, never snapshotted; one stand's failure is logged and skipped. A server room owns one (TrainingRoom.PushPlans); CreateShipped for tests.
Data/Airport/Precompute/PrecomputeStore.cs # Reads and writes {FAA}.json.br entries: upper-cased id, push targets ordered by (stand, design group) so output is byte-identical; GUID temp file + retried move.
                               # PrecomputeSourceHashes (generated at build into obj/ by Yaat.Sim.csproj's GeneratePrecomputeSourceHashes target): Layout and PushTargets hashes plus the
                               # file lists behind them (Data/Airport/**/*.cs minus Precompute/, the Tug*.cs planner files and the pathfinder/navigator exclusions, plus GeoMath.cs; the push set adds the entry types).
Data/PrecomputeCache/          # The committed per-airport cache files, airports.txt (the airports they cover) and their README; *.json.br pinned binary in .gitattributes.
Data/OneWayConstraintDefinition.cs # One-way taxiway + blocked-turn DTOs (coordinate-polyline form: ordered [lon,lat] path. OneWay: path = allowed direction; block reverse|both. BlockedTurn: >=3-point L-shape through an intersection apex, always bidirectional/hard).
Data/Airport/Pathfinding/VisitedNodeSet.cs # Per-PartialRoute visited-node set: immutable sorted int[] (copy-on-Add, binary-search Contains); replaced ImmutableHashSet<int>
Data/Airport/Pathfinding/PolylineSnapper.cs # Shared snap-to-node + connected-span trace (direct edge / taxiway-restricted BFS) for the coordinate-polyline sidecar constructs; used by OneWayResolver and BlockedTurnResolver.
Data/Airport/Pathfinding/OneWayResolver.cs / OneWayMode.cs # Resolves oneWayEdges against a layout into a forbidden directed-move set (ConditionalWeakTable per-layout cache, one set per wake class: GetForbiddenMoves(layout, wakeClass) skips every constraint whose ExemptWakeClasses holds that class; aircraft-less searches pass AircraftlessWakeClass = Large). GetOneWayLaneTaxiways names every taxiway on any constraint span, ignoring exemptions — the one-way lanes the resolver may imply. SearchContext.IsForbiddenMove gates AutoRouter (HardExclude, auto routes) ; RouteMaterialiser emits a wrong-way warning (Warn, explicit routes + the auto two-pass fallback in TaxiPathfinder.RunWithAvoidance).
Data/Airport/Pathfinding/RouteCostFunction.cs # Single cost function for every search: distance, turn budget (head-node delta + a fillet's sweep), taxiway transitions, runway crossings; Fastest adds traversal time (straight at taxi speed, arc via GroundArc.TraversalSeconds) plus the corner's speed dip and nose-wheel-radius pivot sweep
Data/Airport/Pathfinding/GeometricAdmissibility.cs # Hard gates on every edge: per-category heading-change limit (AdmitsTurn, the turn test the goal-set search's onward-edge check shares), MinSteerableArcRadiusFt (fillets tighter than the smallest category main-gear turn radius are not routable); PruningStateKey = (node, 1° arrival-bearing bucket, arrival taxiway) for the state-aware closed set
Data/Airport/Pathfinding/AutoRouter.cs # Flat A* over the ground graph (auto routes, detours, reach probes): Run / RunWithCost (also returns the search's accumulated cost), same-side centerline-run re-run, MaxExpansions cap
                               # RunToGoals: one A* pass to the cheapest of several goal nodes (GoalSet: heuristic = the least straight-line heuristic over the goals, memoised per node), returning a GoalRoute (goal reached, route materialised against the context re-targeted at it, search Cost); a goal with an onward edge is reached only on an arrival GeometricAdmissibility.AdmitsTurn admits, and the request's forbidden moves seed the banned-move set; Run is the single-goal case of the same search
                               # Rejects SearchContext.ForbiddenFirstMove as the route's first edge only (IsForbiddenFirstMove), on both passes: RelaxHardGates keeps it
Data/Airport/Pathfinding/SegmentExpander.cs # Explicit named-taxiway resolver: per-segment local searches, junction picks scored by tail probes (ProbeTailCost + ProbeDestinationReachCost, the latter priced with the A*'s own cost), parking→taxiway bridge, implicit connectors, detour fallback. A parking-stop pick that reaches the destination only through the junction node without driving a straight edge of the named taxiway is rejected unless its extension stays confined to the cleared taxiways and apron (issue #454). The destination extension (ExtendToDestination) hard-excludes every uncleared movement-area taxiway (UnclearedMovementAreaTaxiways: not cleared, not RAMP, not a ramp taxilane, not runway pavement) and falls back to TryExtendViaImpliedLeadIn, which admits one such taxiway as the gate's or spot's implied lead-in when the route drives at most MaxImpliedLeadInFt (1,000 ft) of it before reaching apron (ImpliedDestinationLeadIn) — otherwise the caller holds the aircraft short of it (issue #461). The extension always hard-excludes one-way moves; after the lead-in it tries TryExtendViaOneWayLane (one uncleared one-way lane implied, never reaching a runway holding position; the lead-in obeys the same runway refusal). TryAutoRoutedStartLeg re-resolves a clearance whose first taxiway BridgeStartToTaxiway cannot reach behind an auto-routed start leg over nonmovement pavement (≤ MaxStartLegFt 4,000 ft, at most one implied one-way lane, no runway holding position). FindRampConfinedRoute is the ramp-only auto-route (movement-area taxiways excluded, one implied one-way lane at most). Implied lanes land in TaxiRoute.ImpliedLanes with RouteMaterialiser.NotInClearanceWarning ("M1 not in clearance")
Data/Airport/Pathfinding/ClearOfRunwayEnds.cs # ClearOfRunwayEnd (runway crossed, near taxiway, taxiway carried into, advisory) and ClearOfRunwayEnds (per-search record of the nodes a bare crossing was ended on, shared across `with` copies so only the route that ends on a recorded node reports it)
Data/Airport/Pathfinding/StraightOverGeometry.cs # Internal: the straight-over leg between two taxiways that meet only across a runway, measured on the lines they are flown on: TaxiwayLine (join node, outward bearing, walk to its bar), StraightOverLeg (virtual edges, corners, entry and landing real nodes), StraightOver (leg or why none), StraightOverGeometry.Build (Continue to the meeting point, else the set-back chord); a pure function of the layout, the category's turn limit judged by SegmentExpander.RouteStraightOver
Data/Airport/Pathfinding/PartialRoute.cs # Immutable linked-list search state: head node, arrival bearing, last edge/taxiway, accumulated cost, visited node set (copy-on-Add sorted int[]), BridgeProgress (a bridge search only, part of the A* closed-set key); MaterialiseEdges
Data/Airport/Pathfinding/BridgeRules.cs # BridgeRules (start runway, fromTaxiway, whether the bridge starts on it, the letter-only taxiways its connector may not drive) and BridgeProgress (arrival edge, feet driven along the start runway, whether the connector has begun): the inputs of SearchContext.BridgeMoveRefusal, the one judgement of a bridge move that AutoRouter and SegmentExpander.BridgeDropReason both call.
Data/Airport/ApproachIds.cs    # Approach ids that name no CIFP procedure: Visual(rwy) = VIS28R, TryParseVisual, ExpectingText (the "Expecting ..." system line)
Data/Airport/LandingThreshold.cs # Resolve(RunwayInfo, AirportGroundLayout? | GroundRunway?) -> the end's LANDING threshold: the pavement threshold projected downfield by the airport map's `threshold` displacement, along RunwayInfo's own course (so it stays on the centerline every along/cross-track calc uses). Falls back to the pavement threshold with no layout, which is what pre-existing replays depend on. Arrival-side callers (FinalApproachPhase, LandingPhase, LowApproachPhase, ApproachNavigationPhase, InterceptCoursePhase, PatternGeometry, ApproachGateDatabase, SoloTrainingEvaluator, and the §5-7-1.b.4 final window: ApproachCommandHandler.IsOnFinal, FlightPhysics.AutoCancelSpeedAtFinal, SPD's 5 nm-final rejection, the arrival-spacing and same-runway-protection passes) go through it; departure/surface callers deliberately do not. Datum table: landing-and-runway-exit.md.
Data/Airport/AdwResolver.cs    # Resolves the adw sidecar section against a layout (per-layout cached, same shape as BlockedTurnResolver) into AdwMark line segments: an Outer and an Inner tick per window, laid perpendicular to the arrival runway's final approach course at the published ranges from its landing threshold (positive = outbound on final, negative = past the threshold, down the runway). DtoConverter -> GroundLayoutDto.AdwMarks -> GroundRenderer.DrawAdwMarks. Display-only; no simulation behavior reads it. See ground-rendering.md.
Data/Airport/StandFit.cs       # The largest wingspan a parking stand takes: MaxWingspanFt / Fits(layout, stand, airport ceiling, sidecar maxAdg override). The nearest side-by-side stand within 300 ft (alternates excluded) sets the gap, half of it less the wingtip clearance of the group the span falls in; no neighbour = the airport ceiling; never below ADG I; the sidecar maxAdg replaces the estimate. See "Stand fit". Logger category StandFit.
Data/Airport/Pathfinding/BlockedTurnResolver.cs # Resolves blockedTurns against a layout (per-layout cached) into forbidden pivot turn-triples (prev,apex,next), forbidden bypass-arc moves, and hidden corner-arc pairs. Gated hard in AutoRouter + SegmentExpander (SearchContext.IsBlockedTurn / IsBlockedArcMove, both AUTO and explicit). HiddenArcPairs -> GroundArcDto.HiddenInGroundView (DtoConverter) -> GroundRenderer omits the arc.

# Data/Airport/
IAirportGroundData.cs          # Interface: GetLayout(airportId) + GetSourceGeoJson(airportId)
AirportLayoutDownloader.cs     # Fetches airport ground GeoJSON from vNAS training API; caches under %LOCALAPPDATA%/yaat/cache/airports/
TaxiEdgeLocator.cs             # Static EdgeUnder(layout, position, lastEdge): the taxi edge under an aircraft (near the last edge first, then the whole layout; runway centrelines and ramp connectors excluded; OnTaxiwayMaxOffsetFt 50), shared by FollowingPhase and FollowRoutePlanner
                               # DrivenEdgeUnder: the same with ramp connectors, keeping the last edge while the aircraft rounds a fillet arc off it (FilletArcRounding) — what the TaxiEdgeTrail records
                               # OnFilletArcMaxOffsetFt (8 ft): how far off a fillet's curve a follow route still starts on it; InsideClosestPoint (a curve's nearest point strictly between its ends), shared with GroundNavigator's mid-curve entry
AirportGroundLayout.cs         # Graph: IGroundEdge interface, GroundNode, GroundEdge (straight), GroundArc (bezier fillet arc: P1/P2 control points + MinRadiusOfCurvatureFt, SafeSpeedForRadiusKts per category, SpeedProfile(category) cached local-curvature speed samples, TraversalSeconds). DirectionalEdge (traversal direction). MeasureExitAngle (the one exit-angle rule: path, else hold-short node, else 90°). Exit search: ExitListTaxiways (a runway's exit-list taxiways, noTurnoff left out), TaxiwaysSeededAhead, and per-layout search memos built once the layout is frozen.
                               # GroundRunway.ThresholdDisplacementForEnd / LandingThresholdForEnd expose the vNAS map's per-end `threshold` displacement (Coordinates endpoints are *pavement* ends, and so are RunwayInfo's). Read by AdwResolver and by Data/Airport/LandingThreshold.cs.
                               # FindTaxiStartNode(position, heading, TaxiStartTraffic) / PickTaxiStart (also returns the Blocker): the node a taxi starts at — the parking node it stands at, else the end of the edge/arc ahead on a taxiway, else the nearest heading-aligned node (FindNearestAlignedNode), else FindNearestNode; a free-space start that is an occupied stand's parking node or whose leg passes a parked aircraft is replaced by the first clear taxi node within OccupiedStandStartSearchFt (300 ft), else kept with its Blocker; TAXI starts there, and FOLLOWG (FollowRoutePlanner.StartOf) when its own trail gives no start
                               # AllEdges (Edges+Arcs), FindAdjacentHoldShort (BFS, max 12 hops; returns Side; hops onto a joining taxiway's bar at a dead end, passes another runway's bar moving away; FindAdjacentHoldShortForListing for enumerations), FindExitFromCenterline (walk centerlines, returns side+walk node), FindOnSidePreferredExit (lookahead: defer off-side, prefer later on-side), FindExitPath, FindNearestHoldShortAhead, ComputeExitAngle
CubicBezier.cs                 # Bezier math utilities; used by FilletArcGenerator (arc generation) and GroundNavigator (path following)
IFilletArcGenerator.cs         # Pluggable fillet contract; None + Standard implementations; FilletMode on GeoJsonParser.Parse
FilletMode.cs                  # Fillet mode enum: None (no-op pass) / Standard (the real generator)
NullFilletArcGenerator.cs      # No-op generator for FilletMode.None and raw-layout tests
FilletGeneratorFactory.cs    # FilletMode → IFilletArcGenerator (None / Standard)
FilletArcGeneratorRegistry.cs# Enumerates implemented generators (None, Standard)
FilletStatistics.cs          # Per-pass fillet tallies returned by Apply
FilletArcGenerator.cs      # The fillet generator: classify junctions → resolve cuts → plan → execute → normalize
Fillet/                        # Plan-then-execute fillet pipeline (edge-split connectivity)
  FilletGeometry.cs            # Turn angle, ideal tangent, cubic-bezier build (control points project toward the junction)
  FilletGraphNormalizer.cs     # Post-execute: recompute distances, drop self-loops/degenerate arcs, sweep isolated nodes (no coincident-node merge — plan guarantees none)
  CutId.cs                     # Type-distinct cut identifier (no Origin-string parsing)
  FilletEndpoint.cs            # Type-distinct fillet endpoint; with CutId lets cleanup passes pattern-match instead of parsing Origin strings
  TaxiwayArmBuilder.cs         # One arm per outbound edge; TaxiwayWalk along same-named taxiway
  JunctionClassifier.cs        # Eligibility + Skip/Simple/MultiCorner/Preserve + collinear pairs
  CornerPlanner.cs             # Arm-pair corners (≥15°) and collinear pairs (<15°)
  ArmCutResolver.cs            # Tangent-cut placement per arm; corner arcs + straight connectors; tangent merges
  FilletEdgeSplitPlanner.cs    # Order-independent connectivity: split each original edge once by its cuts, drop only removed-junction stubs → SurvivingEdgeOp
  FilletPlanBuilder.cs         # Assemble the immutable FilletPlan (cuts, merges, corner arcs, surviving edges, nodes/edges to remove)
  FilletPlanExecutor.cs        # Materialize cut nodes + surviving edges + corner arcs (degenerate arc → straight chord) in one pass; remove consumed edges + removed junctions
  FilletPlanCutRedirect.cs     # Union-find survivor map for tangent merges + stable-anchor binding
  (also FilletPlan/JunctionPlan/CornerSpec/ResolvedArmCut plan model + TaxiwayArm(Terminus), JunctionKind, FilletEligibility, ManualArcDetector, SharedArmTangentPass, PlanWarning, FilletConstants, FilletPlanConsistency)
GroundLayoutSpeechNames.cs     # Static: a layout's taxiway names (no runway centerlines or ramps), gate names (parking + helipad) and spot names, sorted; shared by MainViewModel's speech context and the context-layer decoder
RunwayIdentifier.cs            # Struct: runway designator parsing/matching; NormalizeDesignator (zero-pad canonical) + ToDisplayDesignator/ToDisplayString (FAA no-leading-zero display form)
                               # FromApproachId: extracts the runway designator from a procedure id ("I29RY" → "29R", "VDM-A" → null for circling); shared by CifpParser and InterceptCoursePhase.GetRunwayHeading
TaxiRoute.cs                   # Resolved path: TaxiRouteSegment (DirectionalEdge wrapping IGroundEdge) + HoldShortPoints (with dynamic lat/lon offset) + DestinationParking/DestinationSpot (+ DestinationParkingArea: a full parking area's ramp-edge hold, no stand) + completion. ToSummary takes an optional includeTaxiway filter so a caller can render only the clearance as issued (GroundCommandHandler's TAXI readback). HoldShortReason.RouteIncomplete marks a route's end held short of a taxiway the destination needs but the clearance did not reach (issue #461). NextUnclearedHoldShortAhead / HasTaxiwayHoldShortAhead / IsTaxiwayClassHoldShort pick the bar a bare RES pre-clears, in route order, taxiway or spot class only. ImpliedLanes lists the one-way lanes the resolver implied, uncleared (named in the readback, exempt from the not-in-the-route-issued warning; not snapshotted). ClearanceTaxiways lists the taxiways the TAXI clearance named, in order (set by GroundCommandHandler.SetClearanceTaxiways; what a TAXI readback and a CROSS/RES continuation readback may say besides ImpliedLanes; null when unknown; snapshotted)
                               # ToSegmentSnapshot/ResolveSegment write and resolve one TaxiSegmentDto: every snapshot segment, the route's and FollowingPhaseDto.LeadEdgeIntoMerge
                               # alike, resolves through ResolveSegment, so two nodes joined by more than one edge restore to the same one
                               # RemainingDistanceFt(position, segmentCount): the distance along the route to the end of its first segmentCount segments, the leg in progress
                               # measured straight from the position (a chord on a fillet); shared by FollowingPhase's route-end cap, FollowRoutePlanner and TaxiingPhase's bar check
TaxiTurnAboutShape.cs          # Enum None/FromFarEnd/InPlace: how a TAXI turns an aircraft about on the straight edge it stood mid-way along (TaxiRoute.TurnAboutShape + TurnAboutTargetNodeId, set by GroundCommandHandler.ReportTurnAbout; sent to the ground overlay, which draws it as sent)
AutoCrossRunwayMode.cs         # Enum Off/All/InactiveOnly: which runway crossings the scenario pre-clears (InactiveOnly leaves the active-runway list's runways to a CROSS)
TaxiRouteAutoCross.cs          # Applies the AutoCrossRunwayMode to a route's RunwayCrossing hold-shorts; reused at TAXI-resolution, on a mid-session mode change and on an ARWY under InactiveOnly (SimulationWorld.ApplyAutoCrossToActiveTaxiRoutes)
TaxiRouteFormatter.cs          # SINGLE owner of route -> taxiway-name extraction: TaxiwayLegs (decomposes junction composite labels "W - W6"=GroundArc.TaxiwayNames, stays on the name being followed, drops RAMP, flags runway legs) + CleanTaxiwaySequence (legs minus runways, readable TAXI form) + BuildReadableTaxiPath (clean names + terminal #node pin for a mid-taxiway stop). TaxiRoute.ToSummary/FormatTaxiwaySequence call TaxiwayLegs so the readback, Aircraft List column, and draw-route Copy cannot drift apart
TaxiClass.cs                 # Public record pairing AircraftCategory and WakeTurbulenceData.WakeClass for a graph search (TaxiClass.Of(aircraft)); ResolveExplicitPath, LineupBarShadowTest and both FindFullLengthLineupHoldShort take it in place of the pair
TaxiPathfinder.cs            # Taxi pathfinder (static; every public method takes the aircraft's AircraftCategory and WakeTurbulenceData.WakeClass (a TaxiClass for the four methods TaxiClass.cs names), carried on SearchContext.WakeClass to pick the one-way constraints that bind): FindRampConfinedRoute (node→node inside the ramp, SegmentExpander.FindRampConfinedRoute), ResolveExplicitPathDetailed (SegmentExpander, structured PathfindingFailure for handler reaction — issue #396), ResolveExplicitPath (message-only wrapper), FindRoute/FindRoutes (A* AutoRouter, per-preference), FindRunwayRoute (TAXIAUTO), FindAdjacentRunwayRoute (bare TAXI <rwy>: only the bar the aircraft is already at / a ≤600 ft straight run to it, else null → refused), FindFullLengthLineupHoldShort (first bar of RouteMaterialiser.OrderFullLengthLineupBars, the aircraft's own side of the runway; takes the category and wake class), LineupBarShadowTest / LineupBarReach / RanksAsShadowed / LineupBarShadowCache (per-layout cached shadow results). Along-runway travel is hard-limited to runways named in the clearance (SearchContext.AllowedCenterlineNames + same-side-run validation in AutoRouter); a bare final taxiway holds at its transition junction. See Data/Airport/Pathfinding/ + docs/ground/pathfinder.md
                             # FindRouteToNearestGoal: the route to the cheapest of a set of goal nodes in one A* pass (AutoRouter.RunToGoals → GoalRoute; FOLLOWG's merge search); its last parameter is a NearestGoalRequest: the goal ids, a forbidden first move, the onward edges a goal must be turnable onto (GoalOnwardEdges), and the moves never taken.
                             # FindRoute, FindRoutes and FindRouteToNearestGoal search through RunWithAvoidance<T>: a hard-gated pass, then SearchContext.RelaxHardGates when SearchContext.HasHardGates and pass 1 found nothing
ExplicitPathOptions.cs         # RoutePreference enum + ExplicitPathOptions input bag (pathfinder inputs)
HoldShortTarget.cs             # Structured HS target: Target (taxiway/runway/spot) + optional OnTaxiway location — the C@J command form (issue #358) — or IsSpot for the $17 spot form (issue #394; MatchKey keeps the $ sigil in HoldShortPoint.TargetName); TryParse/Parse/ToCanonical round-trip both syntaxes
VirtualNode.cs                 # Factory for virtual ground nodes (position-hashed negative ids — deterministic across processes and snapshot restores); CreateEdge, CreateSegment, OffsetBefore/OffsetPast (OffsetBefore walks a fillet arc by its length, not its chord)
MovementAreaClassification.cs  # Per-layout movement vs non-movement (ramp taxilane) verdict per taxiway name, five ordered rules (single letter/runway/RAMP; runway hold-short -> movement; parking gate one edge off -> taxilane; joins 2+ single-letter taxiways -> movement; else taxilane) plus the sidecar overrides both ways
MovementTaxiwayLookup.cs       # Public static: the movement-area taxiway a position stands on (At = MidEdge, else Nearest; ramp taxilanes never named), shared by GroundCommandHandler.OccupiedTaxiway and HoldingShortPhase's "holding position on {taxiway}" report
RampLaneReposition.cs          # TryPlanRampConfinedRoute = the ramp-confined route of a plain TAXI naming only a gate/spot from inside the ramp (StartsOffMovementArea): the ramp graph (TaxiPathfinder.FindRampConfinedRoute), else one clear free-space cut across the apron between ramp-confined head and tail (TryPlanRampConfinedCut; FindLegObstacle wingtip check shared with the spot line-up). Ramp-lane cuts the map does not connect: IsRampTaxilane (reads MovementAreaClassification)/AreSiblingLanes, TryPlan = start cut onto the first cleared lane (SFO M3 → M4, #396), TryPlanDestinationCut = a direct cut from the named lane straight to the stand tried first (TryPlanDirectStandCut, issue #454), else last cleared lane → apron → the stand's sibling lane (OAK TE → TC @22, #400); every stand cut rolls in on the stand's heading (RollInApproachNode: one fuselage out on the centreline, on the heading's reciprocal) rather than arriving at the crossing's own angle — TryPlanResolvedRouteCut (now given the aircraft's fuselage length) does the same for a route that already resolved the long way round; free-space VirtualNode legs + graph routes; EntersSpotAlongItsLane requires a spot destination's tail to land on the spot's own lane with at least one fuselage length of run (SpotAlignmentRunFt, floored at 100 ft) so the aircraft is straight by the marking instead of crossing it
TaxiApproachLeg.cs             # Bridges an aircraft's live position to the node its resolved taxi route starts at with a free-space RAMP leg (a VirtualNode segment), so a PUSH that rests short of the route's start node is driven to it instead of teleported onto the route in one tick; also bridges a landing rollout to the runway-exit fillet ahead via the along-runway-roll case; a route starting on a runway bar gets a leg to it only from behind the bar's painted stop, measured along the occupied edge or the taxi fillet arc ending at the bar
AircraftFootprint.cs           # The aircraft dimensions the tug planner and its helpers read (type code, length, wingspan, wheelbase, category), with fallbacks resolved: FromType does the FAA lookups once; ResolveWingspanFt is the shared wingspan rule (FAA span, else 118/90/36/40 ft by category). TugRequest, TugParkedNeighbour, GroundOutlineSize, TugPathCheck, TugTaxiwayClearance and TugKinematics take it instead of a type string
AircraftBody.cs                # AircraftBodyPart (Nose / LeftWingtip / RightWingtip / Tail) and AircraftBody(LengthFt, WingspanFt): Of(type) from AircraftLength + AircraftFootprint.ResolveWingspanFt; PointOf(part, main-gear position, heading) puts the nose and tail half the length ahead/behind and the wingtips half the span abeam; LeadingParts (nose + wingtips) vs AllParts
HoldLineFootprint.cs           # Whether an aircraft body at a pose passes any runway's hold line, for a manoeuvre begun at a start pose: FirstCrossing(position, heading, parts) -> HoldLineCrossing(runway, part, point). Past = on the pavement, or inside the runway's bars line within MarkedBarReachFt (200 ft) along the runway of a bar on that side; runways the aircraft is cleared onto are not checked, parts already past at the start are held off the pavement only
HoldShortStandoff.cs           # The hold-short standoff rule: BaseSeparationFt (S: paint calibration, else the authored holdShortDistance unless >15 ft under a 250 ft class rule, else the rule by width, length, CIFP ILS category and elevation; HoldShortStandoffResult), CenterlineStandoffFt (D = S + W_twy/2*|cos θ|), TaxiwayWidthProxyFt, RunwayStandoffInputs (Lookup/Of from NavigationDatabase), InputHash (the precompute StandoffInputHash); docs/ground/hold-short-placement.md
StartPose.cs                   # StartPose(MainGear, HeadingTrueDeg, Body): where an aircraft stands when a route starts at it; Of(AircraftState) / Of(position, heading, type); PartPositions. Decides, with HoldShortAnnotator.StartBarHolds, whether a runway bar at the route's start node holds the aircraft (a route leading away from the runway never does)
RunwayTaxiStart.cs             # RunwayTaxiStart(FromCenterline, AssignedRunway): how a taxi start treats an aircraft standing on a runway centreline, decided from its phase name and assigned runway; every state but the landing rollout (Landing, Runway Exit) starts on the centreline where it stands; shared by TAXI, FOLLOWG and the client's previews (AirportGroundLayout.PickTaxiStart)
RunwayHoldBars.cs              # A runway's hold-short bar nodes (per-layout ConditionalWeakTable index), CentrelineFt, IsRightOfCentreline, IsInsideBarLine / DepthPastMarkingFt (the painted marking: a segment square to the bar's taxiway axis, W_twy/2 each side, each part judged against the main gear; docs/runway-fouling.md), BarTaxiwayName / BarTaxiwayEdges (the one bar-naming rule: straight off-centreline edges only); NearestSameSideBar / SameSideBars (bars on a point's side of the centreline); shared by FollowingPhase.IsInsideHoldLine, HoldLineFootprint and RunwayOccupancy's fouling test
TugKinematics.cs               # The tug-move motion body (pure, deterministic): PushbackPose (see PushbackPose.cs)/TugMove (Straight, ToPoint, ViaLine with the roll-out capture law, TurnTo; Tight/Creep/DwellBefore flags), curvature-limited SteerTravel (turns by at most step/R — a stopped aircraft never rotates), Advance/Record/IsComplete, Simulate (per-move traces sampled every 5 ft), TurnRadiusFt from the FAA wheelbase. Shared by TugMovePlanner (to plan) and PushbackPhase (to fly) — docs/ground/pushback.md
TugParkedNeighbours.cs         # Build over NeighbourCandidates (NeighbourCandidate.cs): the parked/held aircraft within RangeFt (400 ft) of a tug start pose, excluding self and any already-overlapping one; shared by GroundCommandHandler and the client push-route preview; FindStartOverlap → TugStartOverlap (its Refusal is the one OverlapRefusal text both sides show)
TugNeighbourSweep.cs           # The parked-neighbour sweep as one internal body: FirstFoulAlongFt, ClosestFt, RowClearanceFt, RunPathFrom over a plan's move traces and a TugParkedNeighbour (GroundOutlineSweep floor); shared by TugMovePlanner's NeighbourPass and PushTargetLiveCheck
TugMovePlanner.cs              # Plans every PUSH/PUSHF form and PUSHM/PUSHMF as a chain of TugMoves: TugGoal (Spot/Stand/Node/TaxiwayLine/StraightBackTo/Facing/Clear), TugRequest → TugPlan; stand push-off, faced goals via T1/T2/T3 candidate templates simulated and ranked (fewest reversals, shortest path, then template order) — except a lane goal (a spot reached off a stand) instead ranks by path length plus a penalty on lateral departure from the stand's lead-in line and gets a straight-then-line T0 candidate per side (push-off, straight back along the lead-in line, then a push capturing the spot's lane), and a TaxiwayLine goal with a facing taxiway (PUSH <twy> <facing-twy>) ranks by least total nose rotation: near the facing's junction, candidates in both directions along the taxiway end nose-toward it one routine turn radius short; farther out the facing only picks the direction, ending as soon as the push is lined up (TugPlan.FacingTaxiwayIsFar past FarFacingJunctionFt = 1,500 ft) — bare PUSH <twy> split across/alongside at AcrossAngleDeg (45°), TugAmendment for the #167 mid-push facing change, SpotStopGeometry; the chosen candidate for a non-movement-area goal is then run through TugTaxiwayClearance (alley clearance), which may swap it for one — including a fouling-fallback T0 with a shortened straight, and as a last resort a stepped T4 — that stays outside the taxiways' object-free areas (KeepOffTheMovementArea); every lane candidate passes the ±100° swing band and ranks below stand-clear ones when it enters an empty stand's footprint (TugEmptyStands); when a parked neighbour drops a candidate, TugLazyPools adds angled push-offs, extended straights and the multi-point T5 before refusing; TugPlan carries structured Warnings (TugFoulsTaxiwayWarning, TugLongPushWarning past LongPushToTaxiwayFt = 500 ft) that GroundCommandHandler turns into RPO-only readback notes; refuses through TugPathCheck, or NeighbourRefusal — a Faced-goal candidate whose flown path swings into one of TugRequest.ParkedNeighbours (fed by TugParkedNeighbours.Build, 400 ft of the start pose, on the server and in the client preview alike), the same GroundOutlineSweep floor GroundConflictDetector holds a move under way to, so the planner never accepts a candidate the detector would dead-stop mid-manoeuvre; single-shape goals have no alternative template to fall back on and keep going to the tug's own outline-stop instead. A forced request (TugRequest.Forced) skips the taxiway/alley-clearance/overswing/parked-neighbour rules that would refuse or steer a plain tow away (IsClear always true, no swing band, TugPathCheck.Check judges only the runway and holding-position rules), records each rule its kept candidate broke as TugPlan.ForcedOverrides (NoteForcedOverrides: TugPathCheck.SkippedTaxiwayHits for the taxiway rules, the alley clearance and overswing via NoteForcedFouling/NoteForcedOverswing, the parked-neighbour sweep via NoteForcedNeighbours/NeighbourPass — AddOverride keeps one entry per rule, a neighbour's at the closest pass), ranks past a parked neighbour by TugNeighbourClearance.Shortlist first when the chosen candidate breaks the floor (RanksPastNeighbours/ForcedPastANeighbour/BestPastNeighbours: keeping the floor beats the most room beats an overlap, within DecidingMarginFt = 5 ft of the best), and — when a faced goal keeps no candidate and none was refused or hint-dropped — falls back to ForcedFallbackCandidates: a stand push-off then a geometric tow to a point on the goal's approach line at ForcedFallbackRunInRadii line-capture radii back, either side, then the move onto the line, ranked by IsShorterTow (path length, then reversals) rather than the real-push rotation keys
TugPathCheck.cs                # Flown-path check for a candidate plan: footprint vs runway half-width, footprint side vs holding-position edges, taxiway goals: the centre's overshoot past the goal taxiway's charted polyline clamped at its ends, a fillet-rounded end or bend taken at the arc's corner (half a wingspan; CentrelineOf/ChartedNode); other goals: fuselage vs movement-area pavement the goal did not name (exempt: the taxiway straight behind the stand within 300 ft; per-goal leaving/arriving runs seeded by the pavement the fuselage stands across, extended along the same-name chain within a fuselage length); FirstCrossing/NearestAlongside ray helpers for the straight-back rule. Check's forced flag (PUSHF/PUSHMF) limits the judged rules to the runway and holding-position ones; SkippedTaxiwayHits instead returns every taxiway rule a forced tow's kept candidate broke — the overshoot, or each movement-area taxiway crossed, one per taxiway rather than refusing at the first — for TugPlanBuilder.NoteForcedPathOverrides to record as TugPlan.ForcedOverrides
PavementClassifier.cs          # Classifies the pavement a tug move would cross (runway / holding position / movement-area taxiway vs ramp), reading MovementAreaClassification so TugPathCheck can test every edge on the field; a leg may touch movement-area pavement within a set distance of either end. Not a Tug file, so it stays in the layout set
PushbackPose.cs                # PushbackPose (position + nose true heading, shared by the tug planner and PushbackPhase; FlipForKind turns a nose heading into the travel direction and back) and PushbackLegKind (Push/Pull). In the layout set
TaxiStartTraffic.cs            # The aircraft a taxi start is picked for (callsign, type) and the other ground aircraft (NeighbourCandidates) the pick must not start through (ParkedOrHeldOthers); TaxiStartTraffic.Of(AircraftState, candidates). Read by AirportGroundLayout.PickTaxiStart
NeighbourCandidate.cs          # Plain per-aircraft input to the parked-neighbour check (pose, type, stand, hold/phase/speeds); the server fills it from AircraftState, the client from AircraftModel. In the layout set
EdgeGeometry.cs                # PointsFrom: a ground edge's centreline as points from one end node, intermediate points between. Shared by the tug planner and layout code. In the layout set
AirplaneDesignGroup.cs         # AirplaneDesignGroup enum (I-VI, AC 150/5300-13B Table 1-2) + AirplaneDesignGroups: TryParse (roman), SmallestCoveringSpan and OfRecord (an FAA record's group: its ADG, else the smallest group covering its span), GroupForType (an aircraft type's group: its record's, else the smallest group covering its resolved wingspan), MaxWingspanFt, TaxiwayObjectFreeHalfWidthFt and TaxiwaySeparationFt (both AC 150/5300-13B Table 4-1) per group. FromRunwayWidth buckets an airport's own group from its widest runway (shared by HoldShortAnnotator.WingtipClearanceFloorFt); ForTaxiway derives one taxiway's group as the smaller of the airport's own and the largest group whose TaxiwaySeparationFt fits the spacing to its nearest parallel movement-area taxiway (within 15° of heading, held for at least 300 ft, sampled every 25 ft) — cached per layout and MovementAreaClassification
TugLayoutIndex.cs              # The per-layout facts the tug planner's checks read on every plan, built once per layout and shared by every plan at the airport (internal, ConditionalWeakTable cache): every edge in AllEdges order with its names and whether it touches a runway holding position, the holding positions, the headed named parking stands, and each edge's movement-area name per MovementAreaClassification. Rebuilt when the layout's node, edge or arc count changes; an in-place edit that keeps the counts is not caught. Used by TugPathCheck and TugMovePlanner — docs/ground/pushback.md
TugOutlineClearance.cs         # The tug planner's own GroundOutline.Clearance without the per-call segment arrays (same segments, order and arithmetic, so the same figure to the bit), for its per-pose checks — docs/ground/pushback.md
TugTaxiwayClearance.cs         # The alley clearance: how far a tug move's outline (fuselage/wing/tailplane, GroundOutline without the tug's lead) reaches into the object-free area of the movement-area taxiways near it, against each one's AirplaneDesignGroups half-width plus a 5 ft margin (ClearanceMarginFt); a wider taxiway's centreline is clipped where a narrower one's own zone overlaps it, so a taxiway running up to and across a narrower one protects nothing past the narrower one's own zone. TaxiwaysFouledAt (which taxiways the outline already touches at a pose, exempted from later checks); Measure/Fouls scan a candidate's flown path for the deepest penetration (TugTaxiwayFouling: taxiway, TugFootprintPart, peak depth, and exposure = penetration × feet flown). Built once per layout + classification and shared by every plan at the airport; used by TugMovePlanner to keep a spot/stand/node push out of the taxiways it does not name
TaxiwayGraphBuilder.cs         # Graph construction from GeoJSON nodes/edges
GeoJsonParser.cs               # GeoJSON→layout; DetectRunwayCrossings via SplitEdgeAtNode; ParseWithDiagnostics also returns the dropped features; every Parse* overload takes the airport's LayoutInputs, whose StandLeadIns Step 6 joins to a named taxiway vertex (ConnectStandLeadIn) instead of the nearest edge, then Step 6c hands the SpotEdges chains to SpotEdgeBuilder.Apply
SpotEdgeBuilder.cs             # SpotEdgeBuilder.Apply: resolves each sidecar spotEdges chain's points to layout nodes (a point may mint a node) and joins consecutive points by straight edges named for the chain (RAMP for a spot link, the chart name for a lane; origin Sidecar:spot-edge), after the stand connectors and before the adjacency lists; an entry that does not resolve, or touches a hold-short, parking, helipad or runway centreline node, is skipped whole with a warning
LayoutInputs.cs                # LayoutInputs(StandLeadIns, VideoMap, SpotEdges): the per-airport inputs a layout is built from besides its map, all required, passed to every GeoJsonParser.Parse*; SpotEdges is set at every production site from AirportSidecarCatalog.GetSpotEdges; VideoMap null = the airport has no tower-cab videomap (yaat-server passes the map it fetched; the test harness, the precompute builder, GroundMapCheck and LayoutInspector pass null)
TowerCabVideoMap.cs            # TowerCabVideoMap.Read(geoJson): the two-point segments (LineString and MultiLineString parts, painted hold-short bars) of a tower-cab videomap GeoJSON, in file order, with a SHA-256 Hash of them (the precompute key's VideoMapHash); a skipped feature or malformed position is logged at Warning, a non-FeatureCollection root throws JsonException; VideoMapSegment(A, B)
TowerCabMapDownloader.cs       # The one downloader for a tower-cab videomap, shared by the client's ground view and yaat-server's layout build: FetchAsync(baseUrl, artccId, videoMapId) → HttpCacheResult via HttpFileCache (HeadLastModified), cached at %LOCALAPPDATA%/yaat/cache/towercab-maps/{ARTCC}/{id}.geojson; an empty base URL makes no request and serves the cached file (or none)
GroundMapIssue.cs              # GroundMapIssue + GroundMapIssueKind: one feature GeoJsonParser left out of a layout (unknown/missing type, malformed), for the scenario validator's ground-map check
CoordinateIndex.cs             # Spatial index for coordinate-based lookups
RunwayPaintPairing.cs          # Internal: pairs a crossing's generated hold-short point with a painted tower-cab videomap bar on its taxiway (Candidates, innermost first; snap band D_rule-60..+100 ft; PaintPair, PaintSource); RunwayCrossingDetector seats the bar on the first the placement can seat, see docs/ground/hold-short-placement.md
RunwayCrossingDetector.cs      # Detect taxiway/runway crossings; PlanRunway resolves each runway's S (HoldShortStandoff) and seats hold-short bars at D, S plus the angled-taxiway term (MeasureCrossingAngleDeg), clamped short of where the path from S first enters another runway's holding area (CrossingStandoffNm), or at the paired painted bar's D_paint (SeatCrossing, PairWithPaint; GroundNode.PaintSegmentIndex), splitting twin-named edges between the same node pair at each bar (SplitTwinEdgesAtNode); `MarkShortBars` flags each bar hold line vs short bar as the last build step — see docs/ground/hold-short-placement.md
RunwayIntersectionCalculator.cs # Runway centerline/projected-path intersections for LAHSO and solo-training runway scoring. Reported distances are on the PAVEMENT datum (their consumers are departures rolling from it); ComputeHoldShortDistanceNm is the exception and subtracts the displacement, so a LAHSO distance is an available landing distance.
RunwayEntryPoint.cs            # Classify a runway hold short as full-length vs intersection departure for a given end: nearest hold short (along-track on the pavement centerline) is full length, plus an OPPOSITE-side one within OppositeSideBandFt or on the same taxiway (one entrance reachable from both sides); SAME-side extras are always named intersections. Display only, feeds RunwayDepartureQueue
HoldShortAnnotator.cs          # Public: annotate hold-short points on taxi routes;
                               # ComputeHoldShortPositions (takes a nameTailRunway namer, so this precompute-hashed file never references RunwayCrossingEnd; CombinedName names both ends, RunwayCrossingEnd.TailOverNamer the active end)
                               # offsets taxiway HS by fuselage length (skips an Unable bar, whose position is the stop TaxiingPhase moved it to; the client's push preview calls it; the walk-back is bounded only by the aircraft's position and a runway bar behind, and a stop just past a runway records its real setback, docs/ground/hold-short-placement.md).
                               # Owns RouteCrossesRunwayAfterStart, the shared "is the route's start bar the entry side of a crossing it makes" predicate — RouteMaterialiser.AnnotateStartCrossing uses the same one so the two annotators cannot drift (see docs/ground/pathfinder.md).
                               # WingtipClearanceFloorFt (the crossed-taxiway wingtip clearance floor) is now derived from AirplaneDesignGroups.MaxWingspanFt(AirplaneDesignGroups.FromRunwayWidth(...)) / 2 + 25 ft rather than its own hardcoded per-bucket table — same figures, one source of truth shared with the alley clearance
RunwayCrossingEnd.cs           # Which end of a runway a pilot names from a bar's combined "28R/10L" target: `Spoken` is the end the room's ARWY lists as active (the nearer when both are, else the end nearest the aircraft, the first end when unresolved), `HasActiveEnd` says whether either end is listed, `TailOverNamer` names the runway of a tail-over route warning — the pilot's hold-short report, the hold-short texts and the AI's CROSS agree
```
