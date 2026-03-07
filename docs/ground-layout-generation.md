# Ground Layout Generation from GeoJSON

Reference doc for how `GeoJsonParser` and `RunwayCrossingDetector` build `AirportGroundLayout` from GeoJSON.

## GeoJSON Input Format

Combined airport GeoJSON files live at `yaat-server/ArtccResources/{ARTCC}/airports/{icao}.geojson`. Each file is a `FeatureCollection` with features typed via `properties.type`:

- **`parking`** — Point geometry. Properties: `name`, `heading`.
- **`taxiway`** — LineString geometry. Properties: `name` (taxiway letter/number).
- **`spot`** — Point geometry. Properties: `name`.
- **`runway`** — LineString geometry. Properties: `name` (e.g., `"28R/10L"`).
- **`helipad`** — Point geometry. Properties: `name`.

Coordinates are `[lon, lat]` per GeoJSON spec.

## Processing Pipeline (GeoJsonParser.Parse)

### Step 1: Parse features

Iterates the FeatureCollection, classifying each feature by `properties.type`. Collects:
- `parkingFeatures` — name, lat, lon, heading
- `taxiwayFeatures` — name, coordinate chains
- `spotFeatures` — name, lat, lon
- `runways` — name, coordinate chains

### Step 2: Process taxiway coordinates

For each taxiway LineString, `TaxiwayGraphBuilder.ProcessTaxiway` walks the coordinate chain:
- Each coordinate becomes a node (or snaps to an existing node within `SnapToleranceDeg ≈ 0.00003°` / ~10ft)
- Uses `CoordinateIndex` for spatial lookups
- Returns a `ProcessedTaxiway` with the ordered list of node IDs

### Step 3: Detect intersections

`TaxiwayGraphBuilder.DetectIntersections` performs pairwise intersection detection between all taxiway LineStrings:
- For each pair of taxiway segments, computes line-segment intersection
- Creates new nodes at intersection points
- Inserts intersection nodes into both taxiways' node chains

**Key insight**: This is how junction nodes (e.g., the C/H junction at OAK) get shared between taxiways — both taxiway chains reference the same node ID.

### Step 4: Build edges

`TaxiwayGraphBuilder.BuildEdgesFromTaxiway` creates `GroundEdge` objects for each consecutive node pair in each taxiway's chain. Edges are added to `layout.Edges`. Duplicate edges (same endpoints + same taxiway name) are skipped.

**Important**: Node adjacency lists (`GroundNode.Edges`) are NOT populated at this step. Only `layout.Edges` has the edges.

### Step 5: Runway crossing detection

For each runway, `RunwayCrossingDetector.DetectRunwayCrossings`:

1. **Build runway rectangle** — oriented rectangle from runway LineString endpoints, with heading, length, half-width, and hold-short distance
2. **Classify nodes** — each node is either "on-runway" or "off-runway" based on cross-track and along-track distance to the runway rectangle
3. **Find boundary edges** — edges where one endpoint is on-runway and the other is off-runway (excludes RWY-prefixed edges)
4. **Insert hold-short nodes** via `ProcessBoundaryEdge`:
   - Computes the ideal hold-short distance from centerline (based on runway width → ADG group → AC 150/5300-13B Table 3-2)
   - If the off-runway node is close enough AND is not already a hold-short AND is not a junction: **reuse** it (upgrade to RunwayHoldShort)
   - Otherwise: **interpolate** a new node at the correct cross-track distance on the boundary edge, split the edge via `SplitEdgeAtOneNode`
5. **Connect on-runway nodes** via `ConnectOnRunwayNodes` — links the on-runway sides of each crossing with RWY-prefixed centerline edges so that taxiways crossing the same runway are connected in the graph

#### Junction Detection

A junction node is one connected to edges from multiple distinct taxiways (e.g., the C/H intersection). The `HasMultipleTaxiwayConnections` method scans `layout.Edges` (not `node.Edges`, which are empty at this stage) to detect junctions.

Junction nodes should NOT be reused as hold-short nodes because aircraft holding short would block other taxiways. Instead, a new hold-short node is interpolated on the specific taxiway edge between the runway boundary and the junction.

#### Hold-Short Distance

Hold-short distance from centerline = `(runwayWidth / 2) + 75ft`. This places the node 75ft from the runway edge, biased closer to the runway than FAA AC 150/5300-13B Table 3-2 standards to avoid placing nodes near nearby taxiway junctions.

Default runway width when navdata is unavailable: 150ft (→ hold-short at 150ft from centerline).

### Step 6: Connect parking/spots/helipads

Parking, spot, and helipad nodes are connected to the nearest taxiway node within a max distance via RAMP edges. Edges are added to `layout.Edges` only.

### Step 7: Wire up adjacency lists

Iterates ALL `layout.Edges` and populates each node's `Edges` list. This is the ONLY place where `GroundNode.Edges` gets populated.

**Critical timing**: Steps 2-6 operate on `layout.Edges` only. Any code in Steps 2-6 that needs to check node connectivity must use `layout.Edges`, not `node.Edges`.

## Key Invariants

1. **Node IDs** are sequential integers starting from 0, assigned in processing order (parking → taxiway vertices → intersection nodes → crossing detector HS nodes → spots → helipads)
2. **Edge names**: taxiway edges use the taxiway name (e.g., "C", "H", "W3"); parking/spot connections use "RAMP"; runway centerline edges use "RWY{name}" (e.g., "RWY28R/10L")
3. **Hold-short nodes** have `Type = RunwayHoldShort` and `RunwayId` set to the `RunwayIdentifier` they protect
4. **No duplicate edges** on node adjacency lists — Step 7 iterates `layout.Edges` once, so each edge appears exactly once per node
5. **Junction nodes** are never reused as hold-short — interpolation always creates a new node on the specific taxiway edge

## Common Debugging Scenarios

### Hold-short at wrong location
Check if `HasMultipleTaxiwayConnections` correctly identifies the node as a junction. Remember it must scan `layout.Edges` (not `node.Edges`) because node adjacency isn't populated during crossing detection.

### Duplicate edges on nodes
Ensure no code in Steps 2-6 adds edges to `node.Edges` directly (only `layout.Edges`). Step 7 handles all node adjacency wiring.

### Missing runway crossing connections
`ConnectOnRunwayNodes` links on-runway nodes from different taxiway crossings. If two taxiways cross the same runway but aren't connected, check that both HS nodes' on-runway neighbors are found and sorted by along-track position.

### Exit node on runway surface
`FindExitByTaxiway` and `FindNearestExit` skip nodes with RWY-prefixed edges (`HasRunwayCenterlineEdge`). Nodes created by `ConnectOnRunwayNodes` have RWY edges and are correctly excluded from exit searches.

## Test Coverage

E2E tests in `tests/Yaat.Sim.Tests/AirportE2ETests.cs` use real GeoJSON from `yaat-server/ArtccResources/ZOA/airports/`. Tests gracefully skip if the files aren't available.

Key tests:
- `OAK_HoldShortNodes_NotAtJunctions` — verifies no hold-short node sits at a multi-taxiway junction
- `OAK_NoDuplicateEdgesOnNodes` — verifies no node has duplicate edges in its adjacency list
- `OAK_TaxiDF_MultipleHoldShorts_CrossesRunway15_33` — verifies runway crossing hold-shorts exist
- `SFO_LayoutLoads_HasMultipleRunwayHoldShorts` — verifies SFO has hold-shorts for multiple runways
