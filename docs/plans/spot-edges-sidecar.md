PROTOCOL: 1 overlay: none (no docs/agents/explore.md; CLAUDE.md not separately read, docs/README.md read instead); 2 README: done; 3 Task Index: read via grep, rows "Ground layout parsing" (architecture.md:117) and "Ground taxiing" (:68), no row for sidecar-driven layout edits; 4 design docs: ground-layout-generation.md, ARTCCs/README.md read, no YAAT-2100 decision doc found; 5 source: done (GeoJsonParser, sidecar definition/loader/hash, mock data); 6 search: used for LayoutInputs/IsRamp site counts, said so below.

All paths are under X:/dev/yaat unless marked WT = X:/dev/yaat.wt/yaat-67/yaat (the worktree on branch yaat-67 holding the ratchet test and mocks). Line numbers are against the main checkout read in this run.

# YAAT-2100 design map: sidecar-driven spot edges and charted taxilanes

## 1. The existing airport sidecar and the precedent

Files and flow (the `standLeadIns` path is the precedent to copy end to end):
- On disk: `src/Yaat.Sim/Data/ARTCCs/{ARTCC}/Airports/{airport}.json` (ZMA has `mia.json`, `fll.json`). Schema documented in `src/Yaat.Sim/Data/ARTCCs/README.md` (section "Airports", `### standLeadIns` at ~565) and mirrored for users in `ARTCC_CUSTOMIZATION.md`.
- Raw shape: `AirportSidecarFile` (`src/Yaat.Sim/Data/AirportSidecarDefinition.cs:308`), one `List<...Entry>` per section, `[JsonPropertyName]`; `StandLeadIns` at :377. Entry class `StandLeadInEntry` :273 (stand, taxiway, `point` = [lon,lat], notes).
- Validated shape: `AirportSidecar` record :391, `StandLeadIns` :440; validated item `StandLeadIn(Stand, Taxiway, LatLon Point, Notes)` :300.
- Loader: `src/Yaat.Sim/Data/AirportSidecarLoader.cs` builds `AirportSidecar` at :114-136 (`StandLeadIns = ParseStandLeadIns(...)` :134); `ParseStandLeadIns` :172, `StandLeadInProblem` :200 (bad entry -> `result.Warnings.Add("{file}: standLeadIns[i] (stand): problem, skipping")`, duplicate stand warns, last wins).
- Merge across files: `src/Yaat.Sim/Data/AirportSidecarCatalog.cs` `MergeStandLeadIns` :147 (called :74), `GetStandLeadIns(airportId)` :173.
- Into the parser: `LayoutInputs(IReadOnlyList<StandLeadIn> StandLeadIns, TowerCabVideoMap? VideoMap)` (`src/Yaat.Sim/Data/Airport/LayoutInputs.cs:12`) -> `LayoutSource.Inputs` (`GeoJsonParser.cs:152`) -> `BuildLayout` (:300) reads `source.Inputs.StandLeadIns` at :393.
- Construction sites of LayoutInputs in src (must all pass the new data): `Data/Airport/AirportLayoutDownloader.cs:140`, `Scenarios/GroundMapCheck.cs:40`, `Data/Airport/Precompute/PrecomputeEntryBuild.cs:72` (passes `VideoMap: null`), plus tools `Yaat.SpeechSandbox/AirportLayoutFile.cs`, `Yaat.LayoutInspector/LayoutAnalyzer.cs`, and tests `Helpers/TestAirportGroundData.cs`, `Helpers/PinnedSfoGroundData.cs`. Search: `new LayoutInputs\(|LayoutInputs\.` over *.cs found 133 occurrences in 67 files, almost all tests (34 in GeoJsonParserTests.cs). So adding a record member forces a mass edit: give the new member a default (`= []`) or add it as an optional positional parameter, and keep call sites that pass nothing compiling.
- Parser post-processing precedent: Step 6 `UsableStandLeadIns` (:551, validates against the layout, warns and ignores: "names no parking stand"/"names N parking stands"), `ConnectStandLeadIn` (:589: nearest vertex of the named taxiway among edges `CanConnectToEdge` allows, within `ParkingConnectMaxNm` 0.15 nm, else warn + fall back; adds a `GroundEdge` with `TaxiwayName "RAMP"`, `Origin "GeoJson:parking-connector"`). Edges are added to `layout.Edges` only (adjacency stays empty until Step 7).
- Precompute/cache: `AirportSidecarHash.ForStandLeadIns` / `OfStandLeadIns` (`Data/Airport/Precompute/AirportSidecarHash.cs:53,72`) -> `PrecomputeKey.StandLeadInHash` (`PrecomputeKey.cs:38`, compared :101) -> built in `PrecomputeEntryBuild.cs:41` and `PrecomputeBuilder.cs:189`, compared :206. README says: lead-in edit stales layout+push entries, any other sidecar edit stales push entry only.

## 2. Proposed schema

Recommendation: ONE category, `spotEdges`, covering both needs. A taxilane the vNAS map lacks is just a chain whose interior points are lat/lon (new nodes) and whose end points are existing spots or taxiway nodes. A second `taxilanes` category would duplicate the point-resolution grammar and the validation. Split only if the owner wants taxilanes to join movement-area/non-movement overrides in the same entry (see open decision 5, which a single category also handles by a `class` field or by the existing `nonMovementTaxilanes` list).

Point grammar (one object per point; exactly one resolver key):
- `{ "spot": "22" }` the existing Spot node named `$22` (store without `$`; accept a leading `$` and strip it). Must resolve to exactly one Spot node, else warn and skip the entry.
- `{ "junction": ["N","N7"] }` the node shared by edges of both taxiways (exactly two names). 0 matches: warn. >1: choose nearest to optional `near`, else warn and skip.
- `{ "taxiway": "P", "near": [lon,lat] }` the vertex of P nearest `near` within a cap (reuse the 0.15 nm cap and `CanConnectToEdge` filter of `ConnectStandLeadIn`). Optional `"snap": "foot"` splits the edge at the perpendicular foot (mints a node) instead of using a vertex; default vertex, since a foot split on an edge a stand connector hangs off is fine but a vertex is what every precedent does.
- `{ "point": [lon,lat] }` a new TaxiwayIntersection node (Origin `Sidecar:spot-edge`), snapped through `coordIndex` (10 ft) onto an existing node when one is there.

Entry:
```
"spotEdges": [
  {
    "name": "N",            // edge TaxiwayName: a taxiway/taxilane name, or "RAMP"
    "oneWay": false,        // optional; true = first->last only (reuses OneWayConstraint machinery, see Section 4)
    "path": [ {...point}, {...point}, ... ],   // >= 2 points, consecutive points joined by one straight edge each
    "notes": "..."
  }
]
```
JSON coordinates are [lon,lat] like every other sidecar field. Parsed type: `SpotEdgeEntry` / `SpotEdgeChain(string Name, bool OneWay, IReadOnlyList<SpotEdgePoint> Path, string? Notes)` with `SpotEdgePoint` a record carrying Kind plus fields.

Worked MIA example (chain ORDER IS ILLUSTRATIVE; spot positions are real, from WT/.tmp/mocks/yaat67-spot-link/raw/spots.json, pos = lat,lon; foot distance = distance to the nearest vNAS taxiway edge):
- $5 (25.799303,-80.275375) N 199 ft; $6 (25.799118,-80.277546) N 235 ft; $7 (25.799234,-80.278914) N 172 ft; $8 (25.799130,-80.281424) N 174 ft; $9 (25.799025,-80.283144) N/N7 184 ft; $10 (25.798944,-80.284262) N 197 ft.
- $22 (25.790250,-80.275111) P 284 ft; $23 (25.790260,-80.274871) P 342 ft; $24 (25.789185,-80.274545) Q10 400 ft.
- Spots $5-$10 run east-west along one line ~170-235 ft south of N; they read as a chain parallel to N (the Jeppesen taxilane the owner calls charted). Illustrative entries (lane name and join choice are placeholders for the owner to confirm against the chart):
```
{
  "airportId": "KMIA",
  "spotEdges": [
    {
      "name": "RAMP",
      "notes": "Spots 5-10 chain, chain order illustrative: charted apron taxilane south of N, joined to N at both ends",
      "path": [
        { "junction": ["N","N7"] },
        { "spot": "9" },
        { "spot": "10" }
      ]
    },
    {
      "name": "RAMP",
      "path": [
        { "taxiway": "N", "near": [-80.275404, 25.799850] },
        { "spot": "5" },
        { "spot": "6" },
        { "spot": "7" },
        { "spot": "8" },
        { "spot": "9" }
      ]
    },
    {
      "name": "RAMP",
      "notes": "Apron lane by concourses H/J, order illustrative",
      "path": [
        { "taxiway": "P", "near": [-80.275657, 25.789645] },
        { "spot": "22" },
        { "spot": "23" },
        { "spot": "24" },
        { "taxiway": "Q10", "near": [-80.275615, 25.788658] }
      ]
    }
  ]
}
```
Notes on the example: the near-points are the mock's foot points (spots.json `foot`, converted to lon,lat) so the join sits where the existing taxiway is closest to the spot, which is the mock's best guess, not the chart. A real charted lane that is not straight between spots needs intermediate `{ "point": [...] }` entries; with only straight spot-to-spot edges the lane passes through spot centres, which the owner should accept or refine per chain.

Edge name decision: `RAMP` makes the edges connector-class (`IsRamp`, excluded from fillets/hold-short neighbours/`CanConnectToEdge`); a real lane name (e.g. a new `"N8"`) makes them classifiable lanes (MovementAreaClassification, RampLaneReposition). See open decision 2.

## 3. Where it runs in BuildLayout, ids, validation

Insert a new Step 6c in `GeoJsonParser.BuildLayout` right after `ConnectGateGroupsToAnchors(parkingNodes, layout)` (GeoJsonParser.cs:436) and before `layout.RebuildAdjacencyLists()` (:439). Why:
- After Steps 2-5 so every taxiway edge and node, runway hold-short node and short-bar drop (Step 5b :390) is final; spot nodes exist since Step 1 (:319).
- After parking/helipad/gate-group connectors so those still pick their nearest edge from the vNAS taxiways alone (a new RAMP edge is already excluded by `CanConnectToEdge`, but a named lane edge would not be; running later keeps every existing stand connection and `StandDepartures.Classify` foot (:415) unchanged). If the owner wants stands to hang off the new lanes, run before Step 6 instead and say so.
- Before Step 7 (invariant: GroundNode.Edges empty until then; the new step reads `layout.Edges` only, like `ConnectStandLeadIn` :591) and before Step 8 fillets, so the new lane junctions get arcs (RampRadiusFt 50 for RAMP; DefaultRadiusFt for named lanes) and Step 9 `MarkShortBars` is unaffected.
- Ids: mint with `nextNodeId++` from the existing local (:313). Because the step runs after helipads, every existing node id (spots, taxiway, intersection, hold-short, parking, helipad) is unchanged; only fillet tangent nodes (Step 8) and later merged nodes shift for an airport that uses the feature. Splitting an existing edge at a foot: remove the old `GroundEdge`, add two sub-edges with the same TaxiwayName and Origin `Sidecar:spot-edge-split`; never split an edge touching a RunwayHoldShort node, a parking/helipad node, or a runway centreline (reuse `CanConnectToEdge`, :589 area). Do not split when the foot lies within `GeometricAdmissibility.NoOpEdgeThresholdNm` of a vertex: use the vertex.
- New edges: `GroundEdge { Nodes=[a,b], TaxiwayName=name, DistanceNm=GeoMath.DistanceNm, Origin="Sidecar:spot-edge" }`; skip a pair already joined by an edge of that name; skip a zero-length pair (same node twice) with a warning.
- Sidecar validation at load (AirportSidecarLoader, new `ParseSpotEdges` next to `ParseStandLeadIns`): blank name, fewer than 2 points, a point with zero or more than one resolver key, `junction` not exactly two non-blank names, `near`/`point` not two numbers, `taxiway` without `near`. Each warns `"{file}: spotEdges[i] ({name}): {problem}, skipping"`; a bad point skips the whole entry (a chain with a hole is wrong, unlike a list item). Duplicate entry (same name and identical path) warns, last wins.
- Layout-time validation (parser, in a `UsableSpotEdges`-style helper, Log.LogWarning like :567): spot names resolving to 0 or >1 Spot nodes; junction with no shared node; taxiway/near finding no vertex within 0.15 nm; a chain whose two consecutive resolved nodes are the same node. The entry is ignored whole; nothing is half-applied.
- Merge: `AirportSidecarCatalog.MergeSpotEdges` + `GetSpotEdges(airportId)` mirroring `MergeStandLeadIns` :147.

## 4. Site inventory: what reads the new edges, with a ruling

Count basis: Grep `IsRamp\b` in src = 38 occurrences across 21 files; `"RAMP"` in src = 38 across 15 files (both measured this run). Rulings assume the edge name RAMP unless noted.
- Pathfinder (`Data/Airport/Pathfinding/*`, `TaxiPathfinder`): reads `layout.Edges`/`AllEdges` adjacency; new edges are plain edges. RULING: needs no change for connectivity; this is what makes `TAXI $9` route (WT SpotLinkTests `Mia_RouteFromAStandToSpot9_Exists`). Check `SegmentExpander.cs:488,817,975` (destination Spot kind) and `SearchContext.cs` RAMP handling (1 hit) and the avoid/ramp costing in `RouteCostFunction` for a RAMP penalty that might make routes through a spot-edge chain too costly or refuse; unmeasured, verify by the red test.
- Ramp classification: `IsRamp` is `TaxiwayName=="RAMP"` (`AirportGroundLayout.cs:271,709`). RULING: use RAMP for the spot-to-spot connectors when the owner just wants links; use a lane name only when the lane must classify as a ramp taxilane (RampLaneReposition `IsRampTaxilane` :2008, `MovementAreaClassification` Rule 1 NotALane for RAMP, Rules 4/5 for named lanes) - then add the name to `nonMovementTaxilanes` or rely on Rule 5 (joined to at most one single-letter taxiway). A named lane joining N and P is Rule 4 (two single letters) = movement area, which is probably wrong for an apron lane, so pair it with `nonMovementTaxilanes`.
- `MovementAreaClassification` also warns unknown sidecar names (`WarnUnknownNames`): `nonMovementTaxilanes` names for a new lane resolve against layout names, which now include the new lane (OK since the layout includes it).
- Fillets (`FilletArcGenerator`, `Fillet/FilletGeometry.cs`): eligibility is TaxiwayIntersection with >=2 non-arc edges; a new junction on an existing taxiway gets 3 edges and becomes eligible; an N-vertex with an extra RAMP edge fillets with RampRadiusFt 50. RULING: accept; add a fixture asserting no degenerate arc at the new MIA joins. Spot nodes (Type Spot) are not eligible; interior `point` nodes with exactly two edges of the same name are shape points (preserved). RunwayHoldShort nodes never eligible.
- Hold-short placement (Step 5 `RunwayCrossingDetector`, `HoldShortAnnotator.cs`): runs before Step 6c so new edges cannot create or move bars; `HoldShortAnnotator` has 1 IsRamp hit (unmeasured detail). RULING: no change; keep new edges away from runway rectangles (warn if a resolved node is Type RunwayHoldShort or inside a runway rectangle).
- Precompute cache hash: `StandLeadInHash` covers only lead-ins, so a spotEdges edit would NOT stale the committed layout entry. RULING: replace with a layout-inputs hash covering both (rename `StandLeadInHash` -> `LayoutSidecarHash` or add `SpotEdgeHash` field to `PrecomputeKey` :38/:88/:101, `PrecomputeEntryBuild.cs:41`, `PrecomputeBuilder.cs:189,206`, plus `AirportSidecarHash` `OfSpotEdges`); notes excluded from the canonical text. Update `src/Yaat.Sim/Data/PrecomputeCache/README.md` and `PrecomputeKeyTests`/`PrecomputeStoreTests` fixtures, and regenerate committed cache entries for the airports with entries.
- Tug / push (`TugMovePlanner.cs` IsRamp at :3312,3315,4003,4097-4106 `IsRampOnly`; `PushTargetPlanner.cs` 1 hit; `TugTaxiwayClearance.cs`): new RAMP edges between spots are RAMP-only edges not touching Parking; `IsRampOnly(edge) && other.Type != Parking` at TugMovePlanner:3858,4097 treats RAMP-only links as non-routable except to parking. RULING: verify push-target output for MIA is unchanged or deliberately changed; stale push entries are rebuilt by the existing sidecar hash.
- Spawn snap (`GroundSpawnSnap.cs:177`): skips IsRamp edges when snapping a position to a taxiway; RULING: unchanged; a lane named other than RAMP becomes a snap target, which is good for the charted lanes.
- Movement/ASDE: `Asdex/AsdexHoldBarBuilder.cs`, `SimulationEngine.Asdex.cs` (1 IsRamp hit each) - RULING: no change expected, verify.
- Ground navigator / TaxiingPhase (`Phases/Ground/GroundNavigator.cs` 2 IsRamp hits, `TaxiingPhase.cs` 1 RAMP): speed caps and ramp speed; RULING: a RAMP edge gets ramp taxi speed, desired for apron lanes.
- Route text (`TaxiRouteFormatter.cs`, `TaxiEdgeLocator.cs`, `GroundLayoutSpeechNames.cs`): a named lane would be spoken/printed as a taxiway; RAMP is hidden or "RAMP". RULING: owner's choice (decision 2).
- One-way: reuse `OneWayConstraint` via a synthesized constraint from `oneWay: true`; do not fork a second one-way mechanism. RULING: v1 omit `oneWay` and use the existing `oneWayEdges` section which already snaps points to nodes; document that. Smaller surface.
- Layout serialization/DTO to the client (`LayoutSerializationTests.cs`, `Yaat.Client.Core/Services/ServerConnection.cs` RAMP/IsRamp hit, `GroundViewModel.cs` 4 RAMP hits, `Views/Ground/GroundRenderer.cs` 2 IsRamp hits): the client draws edges; RAMP edges likely drawn faint or hidden (unmeasured). RULING: decide how the spot chains are drawn (decision 4). Origin string `Sidecar:spot-edge` is diagnostic only.
- LayoutInspector (`tools/Yaat.LayoutInspector/LayoutAnalyzer.cs`): constructs LayoutInputs with `StandLeadIns`; RULING: pass `AirportSidecarCatalog.GetSpotEdges` when a sidecar is available, or give an `--artccs` option; `--spots` lists spots, add "edges: n". Also `GroundMapCheck` (Scenarios) should warn if a spot has no edge.
- SpeechSandbox `AirportLayoutFile.cs`: pass the new inputs or keep default empty.

## 5. Test plan (red first) and docs

Red tests, written before the feature, in `tests/Yaat.Sim.Tests/Data/Airport/` (extend WT `SpotLinkTests.cs`, do not start over):
1. `Mia_RouteFromAStandToSpot9_Exists` (already in WT, TAXI route from D32 to $9) goes red on current code, green once `ZMA/Airports/mia.json` carries `spotEdges` for the MIA spots. Note: the WT test comment says spots are "linked to the nearest taxiway when parsed" - that is the superseded approach; rewrite the XML summary and the test names to the sidecar approach. Also add the command-level case `TAXI $9` through `GroundCommandHandler`.
2. `Mia_EverySpotHasAnEdge` stays (all 29 MIA spots; mock says 27 of 29 are >150 ft from a taxiway by its measure, owner says 28; reconcile with the mock threshold when pinning).
3. Parser unit tests with a tiny inline GeoJSON (pattern: `StandLeadInTests.cs`, uses `TestStandLeadIns`): each point kind resolves; edge named RAMP is IsRamp; junction ambiguity; unknown spot warns and keeps the layout identical to no-entry; foot split keeps all pre-existing node ids (assert ids of nodes 0..N-1 equal the no-sidecar parse) and ids are appended; entry never splits an edge touching a hold-short.
4. Loader tests (pattern: `BlockedTurnSidecarTests`, `ImplicitConnectorTests`): each validation warning text, merge across two files, last-wins.
5. Hash: `PrecomputeKeyTests` - editing spotEdges changes the layout hash; editing notes does not; editing another section leaves it current.
6. Ratchet: `EveryCommittedLayout_EverySpotHasAnEdge` with `KnownUnlinkedSpots` seeded from WT/.tmp/unlinked.txt (293 spots across 24 layouts; the file lists them as `airport $name`, prefix case varies, e.g. `iah`, so compare case-insensitively on the airport id or normalise as the test does via the TestData file name). MIA's 29 leave the baseline when its sidecar lands. The test already asserts both "unexpected" and "no longer seen", so the baseline shrinks only by deletion as each airport gets sidecars; no loosening possible.
7. Regression: pathfinder routes (Pathfinding/*), fillet comparison (`Fillet/FilletComparisonGateTests`) and precompute build tests for MIA, and `ground/` golden tests unaffected for airports with no `spotEdges`.

Docs to update (same item): `src/Yaat.Sim/Data/ARTCCs/README.md` (new `### spotEdges` section + the sentence "the one sidecar section the layout build reads is standLeadIns" at ~559 and "Editing this section stales..." at ~584 become two sections), `ARTCC_CUSTOMIZATION.md` (user-facing, synced to Discord - the Discord sync workflow means the edit should be proofread before merge; see `docs/discord-integration.md`), `docs/ground-layout-generation.md` (Pipeline Overview list, new Step 6c paragraph, "Inputs" sentence at :49, the "Step 6" last sentence at :218 "reads standLeadIns only", Node IDs list :289), `docs/ground/pathfinder.md` if routing wording changes, `src/Yaat.Sim/Data/PrecomputeCache/README.md`, `docs/architecture.md` Task Index (new row, Section 6), `CHANGELOG.md`.

## 6. OPEN DECISIONS for the owner (with a recommendation)

1. One category or two. Recommend ONE (`spotEdges`), as in Section 2: a taxilane is a chain with coordinate points. Split only if lanes must declare a movement class in the same entry.
2. Edge name for spot-to-spot connectors: `RAMP` vs a lane name. Recommend: `RAMP` for spot-to-taxiway links (no new names, no classification rule interplay, excluded from fillet/`CanConnectToEdge`); a real taxilane name only for the charted lanes the owner wants controllers to name ("TAXI ... via lane X"), always with `nonMovementTaxilanes` listing it. If undecided, ship RAMP only.
3. Run position: after Step 6b/gate groups (recommended; stand connectors unchanged) vs before Step 6 (stands may hang off the new lanes). Recommend after.
4. Client drawing of the new edges: draw like other RAMP connectors (recommended) vs highlight them as sidecar-supplied. Needs a client check (GroundRenderer).
5. Hash design: extend `StandLeadInHash` into one layout-inputs hash (recommended; one field, one stale rule) vs add a second `SpotEdgeHash` (smaller diff, two fields).
6. One-way inside `spotEdges` vs only through the existing `oneWayEdges`. Recommend leave one-way to `oneWayEdges` in v1.
7. Whether a missing spot in a chain skips the whole entry (recommended) or only the pair.
8. Whether the ratchet fails on a spot with no edge in a layout with no sidecar entry (recommended: yes, baseline file as in the WT test) vs warn only. 24 airports, 293 spots is too many for hand-authored sidecars in this item; ship the feature + MIA and baseline the rest.
9. Distance cap for `taxiway`+`near`: 0.15 nm (reuse) vs larger for charted lanes. Recommend reuse, and require `near` to be inside it.

## DEPENDS ON
- The branch feat/spot-link-split holds the YAAT-2100 item. WT yaat-67 holds the ratchet test (red), `.tmp/unlinked.txt` and mocks; none of it merged. The "nearest-taxiway link" approach in that test's summary is superseded by the owner's ruling and must be rewritten.
- No other item dependency found. Chart data for the real chain order/lane names is not available to me: the chain order, lane names and junction choices in Section 2 are illustrative and need the owner or Jeppesen to confirm.
- Precompute cache entries committed for MIA must be rebuilt after the sidecar lands (cache stale rule in `Data/PrecomputeCache/README.md`).

## BRANCH
feat/spot-link-split (as stated in the task); not verified by git in this run (the worktree branch `git branch --show-current` printed `yaat-67`, so the test file in WT lives on yaat-67 and must be brought onto feat/spot-link-split).

## DOC DRIFT
- `src/Yaat.Sim/Data/ARTCCs/README.md` ~559 says `standLeadIns` is "the one sidecar section the layout build does read"; `docs/ground-layout-generation.md:218` also says "reads the sidecar's `standLeadIns` only". Both become wrong when `spotEdges` lands (the code, `GeoJsonParser.cs:393`, is right for today). Owner of the fix: these two docs, in this item.
- `AirportSidecarDefinition.cs:436-439` XML summary ("The only section the layout build reads") likewise.
- WT `SpotLinkTests.cs` summary (nearest-taxiway link at parse) contradicts the owner's ruling; fix in the test.
- `docs/ground-layout-generation.md:289` Node IDs order omits that parking nodes come after runway hold-short and that gate-group connectors add edges only; minor, not checked against `BuildLayout` beyond :313-436 (spots first, taxiway, intersections, hold-short, parking, helipads agree).

## TASK INDEX GAP
Took many reads beyond the Task Index row (README, definition, loader, catalog, hash, parser, MovementAreaClassification, two docs). Missing row: "Add a per-airport sidecar section that changes the ground layout build (like `standLeadIns`)": `src/Yaat.Sim/Data/AirportSidecarDefinition.cs` (Entry, File, validated record) -> `AirportSidecarLoader.cs` (Parse*, warnings) -> `AirportSidecarCatalog.cs` (Merge*/Get*) -> `Data/Airport/LayoutInputs.cs` -> `Data/Airport/GeoJsonParser.cs` (`BuildLayout` step) -> `Data/Airport/Precompute/AirportSidecarHash.cs` + `PrecomputeKey.cs` + `PrecomputeEntryBuild.cs` + `PrecomputeBuilder.cs` -> the LayoutInputs construction sites (`AirportLayoutDownloader.cs`, `GroundMapCheck.cs`, LayoutInspector, SpeechSandbox) -> tests (`StandLeadInTests`, `PrecomputeKeyTests`) -> `ARTCCs/README.md`, `ARTCC_CUSTOMIZATION.md`, `ground-layout-generation.md`.

## MISSING ENTRY POINT
none (docs/README.md and docs/architecture.md with a Task Index exist). Glossary terms "spot", "stand lead-in", "gate group", "sidecar" are used in a project sense; `CONTEXT.md` is the glossary per README.md:15 but was not searched for them in this run: unmeasured.
