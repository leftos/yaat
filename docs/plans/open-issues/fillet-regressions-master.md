# Fillet arc & ground navigation — master plan

Tracks fillet arc geometry bugs and ground navigation quality. Originally focused on fillet regressions; now expanded to cover smooth exit behavior and navigator speed management.

## Sub-plans (historical)

- `fillet-arc-taxi-misbehavior-wja1508.md` (Plan A — SFO 28R exit overshoot)
- `sfo-b10-taxi-stall.md` (Plan B — SFO mid-taxi stall)
- `fillet-arcs-sfo-at6-t6b.md` (Plan C — SFO T6/T6B arc symmetry)
- `oak-g-d-missing-fillet-arc.md` (Plan D — OAK G/D missing fillet arc)

## Status summary

| Sub-plan | Status | Notes |
|----------|--------|-------|
| Plan A — WJA1508 exit overshoot | **Likely fixed** | Exit arc selection bugs fixed; needs retest |
| Plan B — SKW3078/DAL2581 taxi stall | **Fixed** (`45d5c06`) | Duplicate edge dedup fix resolved the stall |
| Plan C — SFO T6/T6B arc symmetry | **Fixed** (`e4621d7`) | Half-length cap + preserve intersection fixes |
| Plan D — OAK G/D missing fillet arc | **Fixed** (`45d5c06`, improved by `e4621d7`) | Dedup + half-cap fixes |

### Test status
- [x] Plan B: SKW3078 — **PASS**
- [x] Plan C: SFO T6/T6B stub symmetry — **PASS** (needs recheck after tangent caps)
- [x] Plan D: OAK G/D arcs — **PASS**
- [x] GenuineTurnArcs — **PASS** (0 degenerate arcs)
- [x] OAK debug trace — **PASS**
- [x] OAK 28R exits: null, B, C1, G, H, J, P — **PASS** (7/8)
- [x] OAK 28R exit E — **PASS** (path deviation metric replaced heading reversal; E's zig-zag no longer a false failure)
- [ ] Plan B: DAL2581 — **HANG** (30s timeout; graph connectivity / pathfinder issue)

---

## Open issues — fillet geometry

### ~~1-3, 5, 10, 11~~ — all FIXED (see commit history)

### 4. MergeCoincidentNodes translates control points instead of recomputing (MEDIUM)

The global merge pass translates P1/P2 by `(survivor - victim)`. Doesn't account for changed chord geometry. Tangent-misaligned warnings (~980 OAK, ~1909 SFO) are partially caused by this.

**Fix**: After merge, recompute P1/P2 using Phase C formula with stored construction params.

### 6. `RebuildAdjacencyLists` called per-node is O(N×E) (LOW — performance)

Not a correctness issue. Fix: targeted adjacency update touching only changed edges.

### 9. DAL2581 test hang (MEDIUM)

Likely graph connectivity issue. Needs investigation with per-tick progress logging.

### ~~12. Disconnected taxiway subgraph: K/F nodes~~ — **FIXED**

Tangent nodes 1457/1458 at OAK K/F intersection (#439) were orphaned because:
1. Collinear pair K(→438)/K(→440) forced `preserveNode=true`
2. Walk through shape-point node 440 set `LandsInManualArc=true`
3. Preserve stub used `redirectToChain`, bypassing tangent nodes and connecting directly to far node 440
4. Tangent nodes had only the arc between them, no graph connectivity

Fix: always connect preserve stubs to the nearest tangent node instead of redirecting past it. The redirect was designed for actual arc chains but triggered on shape-point walks, disconnecting tangent nodes.

### ~~13. Exit pathfinder selects wrong-side fillet arc~~ — **FIXED** (`f6b0170`)

Three bugs in `FindAdjacentHoldShort`:
1. `visited.Add` before arc departure filter — rejected arc's endpoint marked visited, blocking valid non-arc paths to same node
2. BFS path used original centerlineNode for all seeds, but cluster expansion means seeds come from different cluster nodes
3. Inferred exit side not applied when taxiway was specified

---

## Open issues — ground navigation quality

### ~~14. Exit smoothness metric measures heading monotonicity instead of path deviation~~ — **FIXED** (OAK)

Redesigned `AssertSmoothExit` to measure path deviation (distance from aircraft to current route segment) instead of heading reversals. Added `PathDeviationFt` to `NavTickDiag`, computed per-tick in `GroundNavigator.Tick()` using `CubicBezier.ClosestT` for arcs and `GeoMath.DistanceToSegmentFt` for straight edges. Thresholds: 100ft for normal exits, 200ft for >120° turns.

**Results (OAK)**: avg deviation 12-27ft, max 65-82ft for standard exits. Exit E (previously failing heading-reversal) now passes. Exit J (145° turn, 182ft max) passes under the relaxed threshold — will improve with #15/#17.

**SFO**: Not yet migrated (has pre-existing relaxation ordering failures to fix first).

### ~~15. GroundNavigator speed scaling too gentle for large heading errors~~ — **FIXED**

Changed from linear `clamp(1 - angleDiff/120, 0.15, 1.0)` to quadratic `max(0.03, 1 - (angleDiff/90)²)`. Gives: 0°=100%, 45°=75%, 60°=56%, 90°=3%. Modest path deviation improvement on most exits (2-4ft reduction in max deviation).

### ~~16. Dead field `_nextSegmentBearing` in GroundNavigator~~ — **FIXED**

Implemented pre-turning: when within ~50ft of a junction node with a known next-segment bearing, blends the steer target toward the outbound bearing. Uses new `GeoMath.BlendBearings()`. Also switched `ComputeArcSteering` to use local curvature at the lookahead point (floored at min-radius speed) instead of global min-radius, allowing faster traversal on gentle arc sections.

### 18. Exit BFS skips fillet arcs — takes straight shortcut (HIGH)

`FindAdjacentHoldShort` BFS builds the exit path by following edges that match the exit taxiway name. At fillet intersection nodes (e.g., OAK node 359 for G/RWY28R), the original intersection was split into tangent nodes (1288 on G, 1289 on RWY28R) with a fillet arc between them. But the fillet generator **preserved the original straight edge** 359→1288 (on taxiway G), so the BFS picks the straight shortcut and never enters the arc.

**Example (OAK 28R exit G):** Path is `359→1288→1290→360→361`. The arc 1289→1288 is never visited. The aircraft cuts straight from 359 to 1288 (bearing 17.5°) instead of following the 99ft-radius curve. This produces 72ft cross-track deviation through the turn.

**Root cause analysis:** The BFS starts at node 359 (the centerline intersection node). From 359, it sees:
- Edge to 1288 via **G** (straight, 0.0152nm) — matches taxiway filter ✓
- Edge to 1289 via **RWY28R/10L** (straight, 0.0152nm) — doesn't match "G" ✗
The arc 1289→1288 is on "G - RWY28R/10L" but the BFS never reaches 1289 because the only edge from 359 to 1289 is labeled RWY28R/10L.

**Questions to evaluate:**
1. Would the taxi pathfinder (TaxiPathfinder with FewestTurns/Shortest/Fastest strategies) produce a better path here? It's tuned to prefer arcs at junctions — does its scoring penalize skipping them?
2. Should `RunwayExitPhase` hand off to the pathfinder instead of maintaining a separate BFS? The BFS system (`FindExitFromCenterline` → `FindAdjacentHoldShort`) was built before fillet arcs existed. The pathfinder already handles arc-aware routing for taxi commands. Unifying would avoid duplicating arc-awareness logic.
3. Key constraint: the exit BFS does more than pathfinding — it also decides *which* exit to take based on the aircraft's position, speed, preference, and exit angle. The pathfinder would only replace the "route from branch node to hold-short" portion, not the exit selection logic.
4. Simpler alternative: fix `FindAdjacentHoldShort` to recognize that 359→1288 is a fillet-preserved edge that bypasses an arc, and prefer the arc path (359→1289→arc→1288) instead. This could be done by checking if both endpoints of a straight edge are tangent nodes with an arc between them.

### ~~17. Fixed bezier lookahead fraction in ComputeArcSteering~~ — **FIXED**

Replaced `t + 0.15` parameter-based lookahead with distance-based `AdvanceByDistance(bezier, t, 40ft)`. Walks along the curve accumulating arc length to find the lookahead parameter. Dramatically improved exit J (145° turn): 182ft → 85ft max deviation.

---

## Recommended fix order

1. ~~**Issue #14** — Redesign exit smoothness metric (path deviation)~~ ✓ DONE (OAK)
2. ~~**Issue #15** — Navigator speed scaling (quadratic + lower floor)~~ ✓ DONE
3. ~~**Issue #17** — Distance-based arc lookahead~~ ✓ DONE
4. ~~**Issue #16** — Pre-turning or delete dead field~~ ✓ DONE
5. **Issue #18** — Exit BFS skips fillet arcs / consider pathfinder for exits
6. **Issue #4** — Merge recomputation (tangent-misaligned warnings)
7. **Issues #9, #12** — Graph connectivity bugs
8. **Issue #6** — Performance

After each fix: run OAK exit tests, generate tick animations via LI `--ticks`, compare path deviation metrics.

---

## Pre-fillet issues (upstream)

### A. TaxiwayGraphBuilder.InsertNodeInChain may desync NodeIds and Coords

### B. GeoJsonParser exception filter catches too narrowly

### C. CoordinateIndex.FindNearest returns first-found, not actual nearest

### D. JsonDocument not disposed — memory leak

### ~~E. Overlapping taxiway edges~~ — FIXED

### ~~F. RunwayCrossingDetector connects off-centerline nodes~~ — FIXED

---

## Diagnostic infrastructure

| Tool | Purpose |
|------|---------|
| `--debug-fillets` on LI | Traces fillet pair evaluation, tangent placement, Phase D edge rebuild |
| `--validate` on LI | Validation warnings (tangent alignment, degenerate arcs, disconnected subgraphs) |
| `--pathfinder N T1 T2` on LI | Full taxi pathfinder with diagnostic trace |
| `--ticks <csv>` on LI | Animated tick overlay with play/pause, scrubber, hoverable nav diagnostics |
| `NavTickDiag` on `AircraftState` | Per-tick navigator decisions (target, distance, bearing, speeds, arc status) |
| `TickRecorder` CSV | Full nav diagnostics in CSV for analysis |
| `FilletDiagnosticTests` | 6 regression tests for fillet geometry |
| `OakAllExitsTests` | 8 exit smoothness tests + tick dump diagnostics |

## Critical files

| File | Role |
|------|------|
| `src/Yaat.Sim/Data/Airport/FilletArcGenerator.cs` | Fillet logic: pair iteration, tangent placement, bezier construction, edge rebuild, global merge |
| `src/Yaat.Sim/Data/Airport/CubicBezier.cs` | Bezier evaluation, curvature, arc length |
| `src/Yaat.Sim/Data/Airport/AirportGroundLayout.cs` | Ground graph, `FindExitFromCenterline`, `FindAdjacentHoldShort` BFS, `InferPreferredExitSide` |
| `src/Yaat.Sim/Phases/Ground/GroundNavigator.cs` | Arc following, speed profiling, braking constraints, NavTickDiag |
| `src/Yaat.Sim/Phases/Ground/RunwayExitPhase.cs` | Exit route construction, inferred side, navigator setup |
| `src/Yaat.Sim/AircraftCategory.cs` | Turn rates, decel rates, corner speeds, coast speeds |
| `tools/Yaat.LayoutInspector/Program.cs` | CLI queries, pathfinder, tick animation |
| `tests/Yaat.Sim.Tests/Simulation/OakAllExitsTests.cs` | Exit smoothness tests + tick dump diagnostics |
| `tests/Yaat.Sim.Tests/Helpers/TickRecorder.cs` | Per-tick CSV with nav diagnostics |

## Session commits (chronological)

### Early sessions (fillet geometry)
| Commit | Description |
|--------|-------------|
| `e4621d7` | fix: cap fillet tangent distance at half edge length for shared edges |
| `d1f1a8f` | fix: preserve intersection node when collinear pairs exist |
| `244a898` | wip: taxiway walk for fillet tangent placement past short edges |
| `552a8ea` | fix: recompute effective turn angle at tangent points for bezier arcs |
| `642fd40` | fix: skip fillet pairs where both edges go to the same node |
| `b5ff95f` | feat: per-pair tangent nodes and same-node pair skip |

### Fillet geometry fixes (continued)
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

### Tangent caps + exit fixes + diagnostics
| Commit | Description |
|--------|-------------|
| `b3a993e` | fix: cap fillet tangent distance at next intersection and 150ft absolute max |
| `dde92f2` | feat: LI enhancements: repeatable args, --pathfinder, --bfs rename, gated validation |
| `c9433bd` | feat: nav tick diagnostics and arc speed ceiling |
| `1be8a51` | fix: exit BFS expands tangent-link cluster, LI tick animation, remove TickAnimator |
| `72df787` | docs: update master plan |
| `050b07b` | ref: phases use static SimLog instead of ctx.Logger, deferred logger |
| `f6b0170` | fix: exit BFS visited-before-filter bug, cluster path, inferred side |
| `626ab56` | feat: LI tick animation: hoverable tick dots, stable player layout |
