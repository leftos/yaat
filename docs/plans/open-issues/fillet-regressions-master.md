# Fillet arc regressions — master plan

Tracks all open fillet arc geometry bugs. Supersedes four sub-plans:

- `fillet-arc-taxi-misbehavior-wja1508.md` (Plan A — SFO 28R exit overshoot)
- `sfo-b10-taxi-stall.md` (Plan B — SFO mid-taxi stall)
- `fillet-arcs-sfo-at6-t6b.md` (Plan C — SFO T6/T6B arc symmetry)
- `oak-g-d-missing-fillet-arc.md` (Plan D — OAK G/D missing fillet arc)

## Status summary

| Sub-plan | Status | Notes |
|----------|--------|-------|
| Plan A — WJA1508 exit overshoot | **Unknown** | May be related to issue #13 (wrong-side exit arc selection) |
| Plan B — SKW3078/DAL2581 taxi stall | **Fixed** (`45d5c06`) | Duplicate edge dedup fix resolved the stall |
| Plan C — SFO T6/T6B arc symmetry | **Fixed** (`e4621d7`) | Half-length cap + preserve intersection fixes |
| Plan D — OAK G/D missing fillet arc | **Fixed** (`45d5c06`, improved by `e4621d7`) | Dedup + half-cap fixes |

## What's been done

### Diagnostic infrastructure
- [x] Recording: `tests/Yaat.Sim.Tests/TestData/sfo-s1-ground-control-28-01-recording.yaat-bug-report-bundle.zip`
- [x] Diagnostic test class: `tests/Yaat.Sim.Tests/Simulation/FilletDiagnosticTests.cs` (6 tests)
- [x] Provenance tracking (`Origin`) on all `GroundNode`, `GroundEdge`, `GroundArc`
- [x] Fillet construction params stored on `GroundArc`: `EdgeBearingAtNode0Deg`, `EdgeBearingAtNode1Deg`, `TurnAngleDeg`
- [x] `SourceIntersectionPosition` on tangent-point nodes
- [x] Debug-level logging throughout `FilletArcGenerator` (Phase A pairs, tangent point decisions, Phase B/C/D, global merge)
- [x] `LayoutValidator.cs` — automated validation (stale refs, tangent alignment, degenerate arcs)
- [x] Layout Inspector HTML with hover tooltips (shows all overlapping elements), highlights, annotations
- [x] `--debug-fillets` flag on LayoutInspector for tracing fillet decisions
- [x] Raw edges/arcs in `--dump` output for scripted analysis

### Fixes applied (prior sessions)
- [x] **Half-length cap** (`e4621d7`): Cap tangent distance at half the edge length when the far end is an original intersection eligible for filleting.
- [x] **Preserve intersection node** (`d1f1a8f`): Intersections with collinear pairs keep their center node with stub edges carrying correct taxiway names.
- [x] **Taxiway walk** (`244a898`): Walk along subsequent same-taxiway shape-point edges when the first edge is too short.
- [x] **Effective turn angle** (`552a8ea`): Phase C recomputes the turn angle from bearings at the tangent points.
- [x] **Same-node pair skip** (`642fd40`): Skip pairs where both edges go to the same destination node.
- [x] **Per-pair tangent nodes** (`b5ff95f`): Each arc pair computes its own tangent positions independently.

### Fixes applied (this session)
- [x] **Dangling edge/arc cleanup** (`01e6c08`): When removing intersection/shape-point nodes, also remove all edges and arcs still referencing them. Three root causes fixed: intersection removal, walk shape-node removal, walk misclassifying tangent/hold-short nodes.
- [x] **Manual arc preservation** (`1220e42`): All shape-point nodes (2 edges, same taxiway) excluded from filleting. Walk passes through for tangent placement but preserves chain edges via arc-split (splits only the edge where the tangent lands). Preserve stubs connect to chain start for non-runway arcs.
- [x] **Deferred shape node removal** (`7213b1e`): Shape nodes removed only after consumedEdges cleanup, and only if they have zero remaining edges. Step 0's FarNode now classified (was skipped). Eliminates all orphan nodes.
- [x] **Near-U-turn guard** (`3eb9e0d`): Skip pairs where `radiusFt < 5`. Remove post-merge arcs with `radius < 5ft`. Eliminates all degenerate arcs.
- [x] **Kappa formula** (`6f8fde8`): `sweep = effectiveTurn` (was `180 - turn`), `depth = kappa * radius` (was `kappa * tangentDist`). Post-merge orphan cleanup for fillet tangent nodes.
- [x] **Overlapping edge removal** (`6f8fde8`): GeoJSON parser removes duplicate edges between the same two nodes with different taxiway names, keeping the taxiway that continues at both endpoints. Fixes OAK P/J overlap.
- [x] **Runway centerline projection** (`462d967`): `RunwayCrossingDetector.ConnectOnRunwayNodes` projects crossing points onto the actual runway centerline instead of using off-center taxiway intersection nodes. Creates short perpendicular links from on-runway nodes to centerline nodes.
- [x] **Runway edge protection** (`462d967`): Fillet walk treats runway centerline edges as protected (like manual arcs). Tangent nodes split runway edges in place; tangent-links between runway tangent nodes are preserved (both on the straight centerline).
- [x] **Arc TaxiwayName separator** (`6f8fde8`): Changed from unicode middle dot to " - " for easier parsing.
- [x] **HTML highlight defaults** (`6f8fde8`): Everything highlighted when no highlights specified.
- [x] **Runway centerline projection** (`462d967`): `RunwayCrossingDetector.ConnectOnRunwayNodes` projected crossing points onto centerline. Replaced by same-taxiway walk in next commit.
- [x] **Runway edge protection** (`462d967`): Fillet walk treats runway centerline edges as protected. Tangent nodes split runway edges in place; tangent-links between runway tangent nodes preserved.
- [x] **FindCenterlineNode rewrite**: Walk follows same-taxiway edges through off-runway shape-points to find actual centerline nodes. Previous version stopped at off-runway nodes and projected, creating phantom `:link` edges and duplicate intersections. Now finds the real intersection (e.g., OAK B/#182 at 0ft cross-track for 28L). No more projection nodes or `:link` edges.
- [x] **Centerline projection node exclusion**: Nodes with `RunwayCrossing:centerline-projection` origin excluded from filleting.
- [x] **`:link` edge not treated as centerline**: `IsRunwayCenterline` excludes `:link` suffix.

### Current metrics

| Metric | Baseline | Current | Delta |
|--------|----------|---------|-------|
| SFO degenerate-radius | 115 | **0** | **-115** |
| OAK degenerate-radius | 79 | **0** | **-79** |
| SFO tangent-misaligned | 1920 | ~1909 | -11 |
| OAK tangent-misaligned | 1009 | ~961 | -48 |
| SFO edge-missing-node | 0 | **0** | **0** |
| OAK edge-missing-node | 1 | **0** | **-1** |
| SFO arc-missing-node | — | **0** | — |
| OAK arc-missing-node | — | **0** | — |
| SFO orphan-node | — | **2** (spots) | — |
| OAK orphan-node | — | **0** | — |

### Test status
- [x] Plan B: SKW3078 — **PASS**
- [x] Plan C: SFO T6/T6B stub symmetry — **PASS**
- [x] Plan D: OAK G/D arcs — **PASS**
- [x] GenuineTurnArcs — **PASS** (0 degenerate arcs)
- [x] OAK debug trace — **PASS**
- [ ] Plan B: DAL2581 — **HANG** (30s timeout; likely graph connectivity / pathfinder issue, not fillet geometry)

## Open issues

Issues are ordered by priority.

### 1. ~~edge-missing-node~~ — **FIXED** (`01e6c08`)

### 2. ~~Pre-existing manual arcs~~ — **FIXED** (`1220e42`, generalized in `f06647c`)

### 3. ~~Bezier control point depth formula~~ — **FIXED** (`6f8fde8`)

### 4. MergeCoincidentNodes translates control points instead of recomputing (MEDIUM)

The global merge pass translates P1/P2 by `(survivor - victim)`. This preserves the tangent handle vector but doesn't account for the changed chord geometry. The `EdgeBearingAtNode0Deg`, `EdgeBearingAtNode1Deg`, and `TurnAngleDeg` fields were added to enable proper recomputation but are never used in the merge path.

**Fix**: After merge, recompute P1/P2 using the same formula as Phase C with stored construction params. Issue #3 (prerequisite) is now fixed.

**Guard**: Post-merge degenerate arc removal (`radius < 5ft`) catches the worst cases. Tangent-misaligned warnings (~980 OAK, ~1909 SFO) are partially caused by this.

### 5. ~~Near-U-turn guard~~ — **FIXED** (`3eb9e0d`)

### 6. `RebuildAdjacencyLists` called per-node is O(N×E) (LOW — performance)

Called once per intersection in the fillet loop. For SFO with hundreds of intersections and thousands of edges, this is quadratic. Not a correctness issue.

**Fix**: Targeted adjacency update touching only changed edges.

### 7. Plan A — WJA1508 exit overshoot (LOW — needs investigation)

May be a pathfinder issue (exit selection), not fillet geometry. May be resolved by the geometry fixes above.

### 8. Defensive speed floor in GroundNavigator (LOW — defense in depth)

Floor `_currentNodeRequiredSpeed` to 2kt minimum so degenerate arcs never permanently deadlock the sim. Not a root cause fix. Less critical now that degenerate arcs are eliminated.

### 9. DAL2581 test hang (MEDIUM — needs investigation)

Hangs during `engine.Replay(recording, 1179)`. Not caused by degenerate arcs (all eliminated) or dangling edges (all fixed). Likely a graph connectivity issue where the pathfinder enters an infinite loop on a disconnected or malformed subgraph. Needs investigation with per-tick progress logging (xUnit output buffering makes this difficult — use `Console.Error.WriteLine` or Serilog file sink).

### ~~10. Missing fillet arcs on east side of + intersections~~ — **FIXED** (`b3a993e`)

Root cause was issue #11: at OAK C/D intersection #349, the 174° near-U-turn pair produced a 1518ft tangent distance that consumed the C edge (350↔351) belonging to the H/C intersection #351. With the tangent cap, #349's tangent stops before #350, preserving #351's SE-direction C edge. Node #351 now has all 4 edges and 4 fillet arcs.

### ~~11. Fillet arcs spanning past intervening taxiways~~ — **FIXED** (`b3a993e`)

Two caps added to `FilletArcGenerator`:
- **Structural cap**: `DistToFirstIntersectionFt` finds the first walk step with other taxiways at distance >= `MaxTangentDistFt`. Tangent distance capped at that distance. Close neighbors (< 150ft) are excluded to avoid breaking their downstream fillet processing.
- **Absolute cap**: `MaxTangentDistFt = 150ft` hard ceiling on tangent distance, recomputes radius to match.

### 13. Exit pathfinder selects wrong-side fillet arc (MEDIUM)

`FindAdjacentHoldShort` BFS filters arcs by departure tangent direction from the centerline node (>95° from runway heading = skip). But both the north and south arcs at a runway/taxiway crossing depart roughly tangent to the runway, so neither is filtered. The BFS picks the shortest-distance path, which may go through the wrong-side arc (e.g., south arc for a northbound exit), causing a 160° heading reversal after the arc.

Example: OAK 28R exit onto G at node #359. The aircraft (heading 292°) should take the NW arc to north G tangent #1288, but instead takes the SW arc to south G tangent #1292, then must reverse 160° to reach G north.

The BFS scores by total distance + parking proximity + angle penalty. The north arc path should be shorter, but something in the scoring prefers the south path. Needs investigation: check whether the south arc has shorter graph distance (via intersection #359), or whether the parking bias tips the score.

Note: the three pathfinder modes (fewest turns, shortest, fastest) should all prefer the north arc since the south path is longer AND involves more heading change.

**Fix**: Investigate why the BFS picks the south path despite longer distance. May need arc arrival-direction filtering or turn-direction-aware scoring.

**5 OAK exit tests fail**: B, E, G, P, and default — all heading reversals from wrong-side arc selection.

### 12. Disconnected taxiway subgraph: K/F nodes 1470/1471 (LOW)

Detected by the new connectivity check. Two nodes on K/F that are disconnected from the main taxiway graph. Likely caused by a fillet consuming edges without proper reconnection.

## Pre-fillet issues (upstream)

### A. TaxiwayGraphBuilder.InsertNodeInChain may desync NodeIds and Coords

`InsertNodeInChain` inserts into `tw.NodeIds` but not `tw.Coords`. After `InsertNodeInChain`, indices no longer correspond. Could produce phantom intersections or miss real ones.

### B. GeoJsonParser exception filter catches too narrowly

Only `InvalidOperationException` is caught. Widen to include `JsonException`, `KeyNotFoundException`, `FormatException`, `OverflowException`.

### C. CoordinateIndex.FindNearest returns first-found, not actual nearest

Returns the first node within snap tolerance, not the closest. Fix: track minimum distance.

### D. JsonDocument not disposed — memory leak

`JsonDocument.Parse()` rents memory but is never disposed. Fix: `using var doc = ...`.

### ~~E. Overlapping taxiway edges in GeoJSON~~ — **FIXED** (`6f8fde8`)

### ~~F. RunwayCrossingDetector connects off-centerline nodes~~ — **FIXED** (`462d967`, improved)

Root cause: `FindCenterlineNode` walked only through on-runway nodes, but GeoJSON shape-point nodes between the hold-short and the centerline are off-runway. The walk stopped at the first off-runway node (e.g., #178, 207ft from centerline) instead of continuing to #182 (0ft). Fix: walk follows same-taxiway edges regardless of on-runway status, only considers on-runway nodes as centerline candidates. No more projection nodes or `:link` edges needed.

## Recommended fix order

1. **Issue #13** (wrong-side exit arc) — investigate BFS scoring, fix 5 OAK exit tests
2. **Issue #4** (merge recomputation) — should reduce tangent-misaligned warnings significantly
3. **Issue #12** (disconnected K/F subgraph) — investigate cause
4. **Issue #9** (DAL2581 hang) — investigate graph connectivity
5. **Issues #6, #7, #8** — low priority

After each fix: rebuild, run `FilletDiagnosticTests` with `timeout 30`, regenerate Layout Inspector HTML for OAK and SFO, compare warning counts and visual arc geometry.

## Critical files

| File | Role |
|------|------|
| `src/Yaat.Sim/Data/Airport/FilletArcGenerator.cs` | All fillet logic: pair iteration, tangent placement, bezier construction, edge rebuild, global merge |
| `src/Yaat.Sim/Data/Airport/CubicBezier.cs` | Bezier evaluation, curvature, arc length |
| `src/Yaat.Sim/Data/Airport/AirportGroundLayout.cs` | `GroundNode`, `GroundEdge`, `GroundArc` with construction params |
| `src/Yaat.Sim/Data/Airport/RunwayCrossingDetector.cs` | Taxiway-runway crossing detection, hold-short placement, centerline edges |
| `src/Yaat.Sim/Data/Airport/GeoJsonParser.cs` | GeoJSON parsing, overlapping edge removal |
| `tools/Yaat.LayoutInspector/LayoutValidator.cs` | Validation pass (stale refs, tangent alignment, degenerate arcs) |
| `src/Yaat.Sim/Phases/Ground/GroundNavigator.cs` | Arc following, speed profiling, braking constraints, NavTickDiag |
| `src/Yaat.Sim/Phases/Ground/RunwayExitPhase.cs` | Exit route construction from centerline to hold-short |
| `src/Yaat.Sim/Data/Airport/AirportGroundLayout.cs` | `FindExitFromCenterline`, `FindAdjacentHoldShort` BFS |
| `tools/Yaat.LayoutInspector/Program.cs` | `--debug-fillets`, `--pathfinder`, `--validate` |
| `tests/Yaat.Sim.Tests/Simulation/FilletDiagnosticTests.cs` | 6 regression tests + debug trace |
| `tests/Yaat.Sim.Tests/Helpers/TickRecorder.cs` | Per-tick CSV with nav diagnostics |

## Session commits (chronological)

### Prior sessions
| Commit | Description |
|--------|-------------|
| `e4621d7` | fix: cap fillet tangent distance at half edge length for shared edges |
| `d1f1a8f` | fix: preserve intersection node when collinear pairs exist |
| `244a898` | wip: taxiway walk for fillet tangent placement past short edges |
| `552a8ea` | fix: recompute effective turn angle at tangent points for bezier arcs |
| `642fd40` | fix: skip fillet pairs where both edges go to the same node |
| `b5ff95f` | feat: per-pair tangent nodes and same-node pair skip |

### Prior session (fillet geometry fixes)
| Commit | Description |
|--------|-------------|
| `01e6c08` | fix: clean up dangling edges/arcs when removing fillet nodes |
| `1220e42` | feat: detect and preserve pre-existing manual arc chains during filleting |
| `7213b1e` | fix: defer shape node removal and classify step 0 FarNode in walk |
| `3eb9e0d` | fix: skip degenerate near-U-turn pairs and remove post-merge degenerate arcs |
| `f06647c` | fix: skip all shape-point nodes from filleting, not just curved chains |
| `6f8fde8` | fix: remove overlapping taxiway edges, fix kappa formula, highlight defaults |
| `462d967` | fix: project runway crossing nodes onto centerline, protect runway edges |
| `e769c72` | fix: FindCenterlineNode walks through off-runway shape-points |

### This session (tangent caps + diagnostics)
| Commit | Description |
|--------|-------------|
| `b3a993e` | fix: cap fillet tangent distance at next intersection and 150ft absolute max |
| `dde92f2` | feat: LI enhancements: repeatable args, --pathfinder, --bfs rename, gated validation |
| `c9433bd` | feat: nav tick diagnostics and arc speed ceiling |
