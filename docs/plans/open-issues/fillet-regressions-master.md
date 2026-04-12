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
- [x] OAK 28R exits: null, B, C1, G, H, J, P, E — **PASS** (8/8, all within 35ft max deviation)
- [x] OAK 30 exits: null, W1-W7 — **PASS** (8/8, all within 35ft)
- [x] OAK validation: 0 disconnected subgraphs, 0 tangent-misaligned (issue #4 fixed)
- [x] Plan B: DAL2581 — **DELETED** (test asserted route segment > 19, brittle to fillet topology changes; fixed underlying pathfinder bug — see issue #9)

---

## Open issues — fillet geometry

### ~~1-3, 5, 10, 11~~ — all FIXED (see commit history)

### ~~4. MergeCoincidentNodes translates control points instead of recomputing~~ — **FIXED** (validator bug)

The ~980 OAK / ~1909 SFO warnings were caused by validator bugs, not merge translation:
1. Validator expected parallel alignment but fillet arc endpoints are anti-parallel with adjacent edges
2. Validator compared against all same-taxiway edges instead of the specific construction edge
3. Stored `EdgeBearingAtNode0Deg` used outbound bearing instead of actual `bearingToIntersection`

Fix: rewrote `CheckArcTangentAlignment` to compare against stored construction bearing, accepting parallel/anti-parallel. Fixed stored bearings. Result: 0 warnings at OAK and SFO.

### 6. `RebuildAdjacencyLists` called per-node is O(N×E) (LOW — performance)

Not a correctness issue. Fix: targeted adjacency update touching only changed edges.

### ~~9. DAL2581 test hang~~ — **FIXED** (`WalkTaxiwayToward` BFS rewrite)

Two bugs in `TaxiPathfinder.WalkCurrentTaxiwayToTarget` / `WalkTaxiwayToward`:

1. **Junction arcs picked over straight edges.** The walk-along-taxiway loop did not
   prefer straight edges over arcs (the main `WalkTaxiway` loop already does this).
   At a fillet junction, the walk picked a junction-arc edge that turned onto the
   crossing taxiway/runway and dead-ended. Fixed by adding the same straight-vs-arc
   bucket the main loop uses.

2. **Greedy walk picks wrong fork at multi-branch junctions.** Even after preferring
   straight edges, `PickBestWalkEdge` picked the fork whose endpoint was geographically
   closer to the target taxiway. At SFO node #141 the closer fork (#1748) is a spur
   that dead-ends; the correct fork (#1478) had to be discovered by backtracking.
   Fixed by replacing the greedy walk with a real BFS over the walk-taxiway sub-graph.

The DAL2581 fixture test was asserting `maxSegReached > 19` against a brittle pre-fillet
segment count. With fillet tangent nodes the route is much longer (124 segments) and
segment 19 is no longer the stall point. Test deleted; SKW3078 still covers the same
flow.

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

Redesigned `AssertSmoothExit` to measure cross-track deviation from the edge's infinite line. Added `PathDeviationFt` to `NavTickDiag`, polyline arc navigation (bezier subdivided into ~15ft waypoints), tight arrival threshold for arcs and short edges. Thresholds tightened to 35ft (50ft for >120° turns). All 24 OAK tests pass.

**SFO**: Not yet migrated (has pre-existing relaxation ordering failures to fix first).

### ~~15. GroundNavigator speed scaling too gentle for large heading errors~~ — **FIXED**

Changed from linear `clamp(1 - angleDiff/120, 0.15, 1.0)` to quadratic `max(0.03, 1 - (angleDiff/90)²)`. Gives: 0°=100%, 45°=75%, 60°=56%, 90°=3%. Modest path deviation improvement on most exits (2-4ft reduction in max deviation).

### ~~16. Dead field `_nextSegmentBearing` in GroundNavigator~~ — **FIXED**

Implemented pre-turning: when within ~50ft of a junction node with a known next-segment bearing, blends the steer target toward the outbound bearing. Uses new `GeoMath.BlendBearings()`. Also switched `ComputeArcSteering` to use local curvature at the lookahead point (floored at min-radius speed) instead of global min-radius, allowing faster traversal on gentle arc sections.

### ~~18. Exit BFS skips fillet arcs — takes straight shortcut~~ — **FIXED**

Split BFS seeding into two passes: arcs first, then straight edges. Arcs claim the visited set so straight shortcuts to already-visited nodes are skipped. OAK G deviation: 72ft → 28ft. OAK P: 63ft → 6ft.

**Open question:** Should `RunwayExitPhase` hand off to the taxi pathfinder instead of maintaining a separate BFS? The pathfinder already handles arc-aware routing. The BFS exit selection logic (position, speed, preference, exit angle) would remain; only the "route from branch to hold-short" portion would use the pathfinder.

### ~~19. Preserve stubs cut straight across curved taxiways~~ — **FIXED**

Three fixes:
1. Preserve stubs connect to the first neighbor (`otherNode`) when the tangent is past shape-point nodes, instead of a straight 150ft shortcut to the tangent.
2. `RescueOrphanedTangentNodes` post-fillet pass reconnects tangent nodes left orphaned when a later intersection's Phase D consumed their connecting edge.
3. `RemoveRedundantPreserveEdges` post-fillet pass removes collinear preserve stubs that are superseded by shorter shorten/tangent-link edges from other intersections in the same direction. Plus collinear stub deduplication within the same intersection.

### ~~17. Fixed bezier lookahead fraction in ComputeArcSteering~~ — **FIXED**

Replaced `t + 0.15` parameter-based lookahead with distance-based `AdvanceByDistance(bezier, t, 40ft)`. Walks along the curve accumulating arc length to find the lookahead parameter. Dramatically improved exit J (145° turn): 182ft → 85ft max deviation.

---

## Recommended fix order

1. ~~**Issue #14** — Redesign exit smoothness metric (path deviation)~~ ✓ DONE (OAK)
2. ~~**Issue #15** — Navigator speed scaling (quadratic + lower floor)~~ ✓ DONE
3. ~~**Issue #17** — Distance-based arc lookahead~~ ✓ DONE
4. ~~**Issue #16** — Pre-turning or delete dead field~~ ✓ DONE
5. ~~**Issue #18** — Exit BFS arc priority~~ ✓ DONE
6. ~~**Issue #19** — Preserve stubs + orphan rescue + dedup~~ ✓ DONE
7. ~~**Issue #12** — Disconnected K/F subgraph~~ ✓ DONE
8. ~~**Issue #4** — Merge recomputation (tangent-misaligned warnings)~~ ✓ DONE
9. ~~**Issue #9** — DAL2581 test hang (graph connectivity / pathfinder)~~ ✓ DONE
10. **Issue #6** — Performance

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
