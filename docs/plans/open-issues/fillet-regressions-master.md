# Fillet arc regressions — master plan

Tracks all open fillet arc geometry bugs. Supersedes four sub-plans:

- `fillet-arc-taxi-misbehavior-wja1508.md` (Plan A — SFO 28R exit overshoot)
- `sfo-b10-taxi-stall.md` (Plan B — SFO mid-taxi stall)
- `fillet-arcs-sfo-at6-t6b.md` (Plan C — SFO T6/T6B arc symmetry)
- `oak-g-d-missing-fillet-arc.md` (Plan D — OAK G/D missing fillet arc)

## Status summary

| Sub-plan | Status | Notes |
|----------|--------|-------|
| Plan A — WJA1508 exit overshoot | **Unknown** | No direct test; may be resolved by geometry fixes or may be a separate pathfinder/navigator issue |
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
- [x] Layout Inspector HTML with hover tooltips, highlights, annotations
- [x] `--debug-fillets` flag on LayoutInspector for tracing fillet decisions

### Fixes applied (this session)
- [x] **Half-length cap** (`e4621d7`): Cap tangent distance at half the edge length when the far end is an original intersection eligible for filleting. Skip the cap for tangent nodes from prior fillets (identified by `SourceIntersectionPosition`) to avoid half-of-half over-capping.
- [x] **Preserve intersection node** (`d1f1a8f`): Intersections with collinear pairs (e.g., W3/U through W) keep their center node with stub edges carrying correct taxiway names, instead of deleting + collinear merging (which lost name boundaries).
- [x] **Taxiway walk** (`244a898`): When the first edge from an intersection is too short for the desired tangent distance, walk along subsequent same-taxiway shape-point edges to find room. Tangent node interpolated along the walk chain; intermediate edges/nodes consumed.
- [x] **Effective turn angle** (`552a8ea`): Phase C recomputes the turn angle from bearings at the tangent points (not the intersection) when the tangent walked past a curve. Fixes near-collinear arcs where taxiways diverge.
- [x] **Same-node pair skip** (`642fd40`): Skip pairs where both edges go to the same destination node (overlapping edges like B and B5 sharing the same physical segment).
- [x] **Per-pair tangent nodes** (`b5ff95f`): Each arc pair computes its own tangent positions independently. Near-collinear pairs with huge tangent distances no longer corrupt other pairs' arcs via max-wins. Coincident positions on the same edge are deduplicated (5ft threshold).

### Current metrics (after all fixes)

| Metric | Baseline | Current | Delta |
|--------|----------|---------|-------|
| SFO degenerate-radius | 115 | **41** | **-74** |
| OAK degenerate-radius | 79 | **27** | **-52** |
| SFO tangent-misaligned | 1920 | 2009 | +89 |
| OAK tangent-misaligned | 1009 | 1074 | +65 |
| SFO edge-missing-node | 0 | **42** | +42 |
| OAK edge-missing-node | 1 | **30** | +29 |

### Test status
- [x] Plan B: SKW3078 — **PASS** (as of `d1f1a8f`)
- [x] Plan C: SFO T6/T6B stub symmetry — **PASS** (as of `e4621d7`)
- [x] Plan D: OAK G/D arcs — **PASS** (as of `d1f1a8f`)
- [ ] Plan B: DAL2581 — **UNKNOWN** (test hangs at 30s timeout, likely due to edge-missing-node)
- [ ] GenuineTurnArcs — **FAIL** (41 SFO degenerate turn arcs)

**IMPORTANT**: Simulation tests (DAL2581, SKW3078) are hanging due to `edge-missing-node` dangling references. The per-pair tangent change (`b5ff95f`) introduced 42+30 dangling edge refs. These cause the A* pathfinder to loop indefinitely. Must fix edge-missing-node before sim tests will pass. This was not investigated yet — it's the top priority for the next session.

## Open issues

Issues are ordered by priority.

### 1. edge-missing-node: dangling edge references after walk (CRITICAL — blocks sim tests)

The taxiway walk consumes intermediate edges and removes shape-point nodes, but edges created by PRIOR fillet iterations that reference the removed nodes are not cleaned up. When those nodes are later filleted and removed, edges pointing to them become dangling references.

**Root cause**: When intersection X is filleted, edges created by earlier intersections (e.g., shortened edges from int Y's fillet) that reference X's node are not in X's `consumedEdges` set. X gets removed but the edge survives.

**Suspected fix**: When removing an intersection node (non-preserve case), also remove all edges referencing it: `layout.Edges.RemoveAll(e => e.Nodes[0].Id == intId || e.Nodes[1].Id == intId)`. This was tried earlier in the session and worked, but needs to be re-verified with the per-pair changes.

### 2. Pre-existing manual arcs destroyed by filleting (MEDIUM — visual quality)

GeoJSON authors sometimes create manual arcs using chains of short edges with frequent nodes (e.g., OAK W5, W6, W7). The fillet generator treats the shape-point nodes in these chains as intersections eligible for filleting, destroying the original arc geometry and replacing it with straight edges + incorrect fillet arcs.

**Example**: OAK W6 — original chain of short edges forming a smooth curve from W toward the runway. After filleting, the arc structure is destroyed: a fillet arc is added at one end (connecting to W7), but the rest of the original arc becomes a long straight edge (697→699).

**Proposed approach**: Detect pre-existing manual arcs by identifying chains of shape-point nodes (2 edges, same taxiway) that form a curve (cumulative bearing change exceeds a threshold, e.g., 30°). Exclude these chains from filleting — they already provide the smooth curve geometry that fillets are designed to create.

**Detection heuristic**: Walk same-taxiway shape-point chains. If the total bearing change from start to end exceeds ~30° AND the chain has 3+ nodes, it's a manual arc. Mark all nodes in the chain as non-eligible for filleting.

### 3. Bezier control point depth formula (HIGH — root cause of remaining degenerate arcs)

**Two compounding math errors in Phase C.**

**Bug A — sweep angle inverted.** `sweep = 180° - turnAngle` but correct is `sweep = turnAngle`. The effective-turn-angle fix (`552a8ea`) partially mitigates this by recomputing the turn at tangent positions, but the formula itself is still wrong.

**Bug B — depth uses tangentDist instead of radius.** `depth = kappa * tangentDist` but correct is `depth = kappa * radius`.

**Fix:** Replace with:
```csharp
double sweepRad = effectiveTurnDeg * (Math.PI / 180.0);
double kappa = (4.0 / 3.0) * Math.Tan(sweepRad / 4.0);
double radiusNm = radiusFt / GeoMath.FeetPerNm;
double depthA = kappa * radiusNm;
double depthB = kappa * radiusNm;
```

**Note**: When tested in isolation (without other fixes), this made degenerate arcs worse (115→170) because near-180° turns got even larger control points. With the walk + effective turn angle + per-pair tangent fixes now in place, it should work correctly. Test carefully.

### 4. MergeCoincidentNodes translates control points instead of recomputing (HIGH)

The global merge pass translates P1/P2 by `(survivor - victim)`. This preserves the tangent handle vector but doesn't account for the changed chord geometry. The `EdgeBearingAtNode0Deg`, `EdgeBearingAtNode1Deg`, and `TurnAngleDeg` fields were added to enable proper recomputation but are never used in the merge path.

**Fix**: After merge, recompute P1/P2 using the same formula as Phase C with stored construction params. Requires issue #3 to be fixed first.

### 5. Near-U-turn produces degenerate geometry (MEDIUM)

When `turnAngle` approaches 180°, `tan(halfAngle)` → ∞, producing enormous tangent distances and tiny radii. The per-pair tangent fix prevents these from corrupting other pairs, but the near-U-turn pair's own arc is still degenerate.

**Current state**: The effective-turn-angle fix helps when the taxiways diverge, but some near-180° pairs at simple T-junctions don't diverge enough.

**Fix**: Skip pairs where `radiusFt < 5.0` or `tangentDistFt < 1.0`. These can't produce useful arcs.

### 6. `RebuildAdjacencyLists` called per-node is O(N×E) (LOW — performance)

Called once per intersection in the fillet loop. For SFO with hundreds of intersections and thousands of edges, this is quadratic. Not a correctness issue.

**Fix**: Targeted adjacency update touching only changed edges.

### 7. Plan A — WJA1508 exit overshoot (LOW — needs investigation)

May be a pathfinder issue (exit selection), not fillet geometry. May be resolved by fixing issues #1–#4.

### 8. Defensive speed floor in GroundNavigator (LOW — defense in depth)

Floor `_currentNodeRequiredSpeed` to 2kt minimum so degenerate arcs never permanently deadlock the sim. Not a root cause fix.

## Pre-fillet issues (upstream)

### A. TaxiwayGraphBuilder.InsertNodeInChain may desync NodeIds and Coords

`InsertNodeInChain` inserts into `tw.NodeIds` but not `tw.Coords`. After `InsertNodeInChain`, indices no longer correspond. Could produce phantom intersections or miss real ones.

### B. GeoJsonParser exception filter catches too narrowly

Only `InvalidOperationException` is caught. Widen to include `JsonException`, `KeyNotFoundException`, `FormatException`, `OverflowException`.

### C. CoordinateIndex.FindNearest returns first-found, not actual nearest

Returns the first node within snap tolerance, not the closest. Fix: track minimum distance.

### D. JsonDocument not disposed — memory leak

`JsonDocument.Parse()` rents memory but is never disposed. Fix: `using var doc = ...`.

## Recommended fix order

1. **Issue #1** (edge-missing-node) — CRITICAL, blocks sim tests
2. **Issue #2** (pre-existing manual arcs) — prevents destroying GeoJSON author's arc geometry
3. **Issue #3** (kappa formula) — pure math fix, should work now with walk + effective turn in place
4. **Issue #5** (near-U-turn guard) — skip degenerate pairs
5. **Issue #4** (merge recomputation) — requires #3 first
6. **Issues #6, #7, #8** — low priority

After each fix: rebuild, run `FilletDiagnosticTests` with `timeout 30`, regenerate Layout Inspector HTML for OAK and SFO, compare warning counts and visual arc geometry.

## Critical files

| File | Role |
|------|------|
| `src/Yaat.Sim/Data/Airport/FilletArcGenerator.cs` | All fillet logic: pair iteration, tangent placement, bezier construction, edge rebuild, global merge |
| `src/Yaat.Sim/Data/Airport/CubicBezier.cs` | Bezier evaluation, curvature, arc length |
| `src/Yaat.Sim/Data/Airport/AirportGroundLayout.cs` | `GroundNode`, `GroundEdge`, `GroundArc` with construction params |
| `tools/Yaat.LayoutInspector/LayoutValidator.cs` | Validation pass (stale refs, tangent alignment, degenerate arcs) |
| `tools/Yaat.LayoutInspector/Program.cs` | `--debug-fillets` flag for tracing fillet decisions |
| `tests/Yaat.Sim.Tests/Simulation/FilletDiagnosticTests.cs` | 6 regression tests + debug trace |

## Session commits (chronological)

| Commit | Description |
|--------|-------------|
| `e4621d7` | fix: cap fillet tangent distance at half edge length for shared edges |
| `d1f1a8f` | fix: preserve intersection node when collinear pairs exist |
| `244a898` | wip: taxiway walk for fillet tangent placement past short edges |
| `552a8ea` | fix: recompute effective turn angle at tangent points for bezier arcs |
| `642fd40` | fix: skip fillet pairs where both edges go to the same node |
| `b5ff95f` | feat: per-pair tangent nodes and same-node pair skip |
