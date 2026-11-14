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
