# Master plan — Fillet-arc regressions (SFO + OAK)

Consolidates four open-issue plans into one investigation. Supersedes:

- `docs/plans/open-issues/fillet-arc-taxi-misbehavior-wja1508.md` (Plan A — SFO 28R runway exit overshoot)
- `docs/plans/open-issues/sfo-b10-taxi-stall.md` (Plan B — SFO mid-taxi stall + wrong-direction reissue)
- `docs/plans/open-issues/fillet-arcs-sfo-at6-t6b.md` (Plan C — SFO generation-time arc symmetry)
- `docs/plans/open-issues/oak-g-d-missing-fillet-arc.md` (Plan D — OAK G/D missing fillet arc → 180° stall)

## Root cause (confirmed)

All four bugs trace to **`FilletArcGenerator.MergeCoincidentNodes`** — the global merge pass that runs after all intersections are individually filleted. When tangent nodes from different intersections land within 5ft of each other (common on short shared edges), the merge combines them. The original code **translated** bezier control points by a small dLat/dLon offset, but this produced degenerate beziers when the merged node served a completely different intersection geometry. Specifically:

1. **Control points pointing the wrong direction.** An arc created at intersection A with tangent direction toward A's center gets its endpoint merged into a tangent node from intersection B. The control point still points toward A's center — which may be the opposite direction from the arc's actual path at B's position.

2. **Near-collinear arcs with zero-radius curvature.** Same-taxiway continuation arcs (A/A through an intersection) have `TurnAngleDeg ≈ 180°`, producing `sweep ≈ 0°` and `kappa ≈ 0`. The bezier control points collapse onto the endpoints, creating a doubled-back curve with `MinRadiusOfCurvatureFt ≈ 0` and `MaxSafeSpeedKts = 0` → permanent taxi stall.

3. **Stale node object references.** When `BuildMergeMap` repositions a survivor node to the midpoint between two source intersections, arcs and edges that already reference the old survivor object don't get updated, leaving them connected to a phantom position.

## What's done

### Diagnostic infrastructure
- [x] Recording landed as `tests/Yaat.Sim.Tests/TestData/sfo-s1-ground-control-28-01-recording.yaat-bug-report-bundle.zip`
- [x] Layout snapshots captured in `.tmp/` for all trouble corners (SFO 28R/D, node 1235, A/T6/T6A/T6B, OAK #1208)
- [x] Diagnostic test class: `tests/Yaat.Sim.Tests/Simulation/FilletDiagnosticTests.cs` with 5 regression assertions
- [x] **Provenance tracking** (`Origin` property) on all `GroundNode`, `GroundEdge`, `GroundArc` — traces every element back to its creation site (GeoJson, TaxiwayGraphBuilder, FilletArcGenerator phase, RunwayCrossingDetector, etc.) and records merge/reposition events
- [x] **Fillet construction parameters** stored on `GroundArc`: `EdgeBearingAtNode0Deg`, `EdgeBearingAtNode1Deg`, `TurnAngleDeg` — enables post-merge recomputation
- [x] **`SourceIntersectionPosition`** stored on tangent-point `GroundNode` — enables midpoint positioning during merge
- [x] **Layout Inspector enhancements:**
  - Arc detail in text output: radius, maxSafe, tangent, turn angle, bearings, origin
  - Edge/arc hover tooltips in interactive HTML (priority: arcs > edges > nodes)
  - SVG renderer removed (HTML-only now)
  - **Validation pass** (`LayoutValidator.cs`) runs automatically, checks:
    - `arc-stale-node-ref` / `edge-stale-node-ref`: node object position doesn't match `layout.Nodes`
    - `arc-tangent-misaligned`: arc tangent at endpoint deviates >45° from adjacent straight edge
    - `arc-degenerate-radius`: genuine turn arc (>30°) with `maxSafe < 1kt`
    - `self-loop-edge` / `self-loop-arc`: both endpoints are the same node
    - `orphan-node`: node with zero edges

### Fixes applied (in working tree, not yet committed)
- [x] **`FilletArcGenerator.MergeCoincidentNodes`**: Replaced control-point translation with `RecomputeArcControlPoints`. For each endpoint: if it moved (merged/repositioned), uses chord direction as tangent; if it didn't move, preserves original edge bearing. Depth = `chord/3`.
- [x] **`FilletArcGenerator.BuildMergeMap`**: When two tangent nodes from different source intersections merge, repositions the survivor to the **midpoint between the two source intersection centers** (not either tangent position). This gives arcs from both intersections equal room for their curves.
- [x] **`FilletArcGenerator.BuildMergeMap`**: Returns `repositionedSurvivors` dictionary so the merge loop can update existing arcs/edges that reference the old survivor object.
- [x] **`FilletArcGenerator.MergeCoincidentNodes` arc loop**: Detects repositioned survivors and updates arc node references + recomputes control points, with `+repos(id)` provenance tag.

### Regression test results
- [x] **Plan B stalls fixed**: SKW3078 and DAL2581 advance past former stall segment 19 (arc 1218→1221) — no more `MaxSafeSpeedKts = 0` stalls
- [x] **Plan C symmetry fixed**: SFO A/T6, A/T6A, A/T6B all have 2 junction arcs with `radius > 1ft`
- [x] **Plan D arcs non-degenerate**: OAK #1208 arcs all have `MaxSafeSpeedKts > 0.1kt`
- [x] **126 existing fillet/exit/ground tests pass** with zero regressions
- [ ] Plan A (WJA1508 exit overshoot) not yet investigated — WJA1508 didn't traverse any arcs in the 60s diagnostic window
- [ ] Skipped OAK tests not yet re-enabled

## What's still open

### 1. Bezier control point depth formula is mathematically wrong (HIGH priority, ROOT CAUSE)

**The deepest remaining issue — upstream of #2 and #3.** The Phase C Bezier approximation uses `κ * tangentDist` as the control point depth, but the correct formula for approximating a circular arc is `κ * radius`, where `κ = (4/3) * tan(sweep/4)`.

For a 90° turn, `tangentDist = radius * tan(45°) = radius`, so it happens to be correct. But for other angles:
- At 60° turn: `tangentDist = radius * tan(30°) ≈ 0.577 * radius` — control points are **42% too shallow**
- At 120° turn: `tangentDist = radius * tan(60°) ≈ 1.732 * radius` — control points are **73% too deep**, overshooting past the intersection center

This affects `MinRadiusOfCurvatureFt` and therefore `MaxSafeSpeedKts` on every arc that isn't exactly 90°. The post-merge `RecomputeArcControlPoints` uses `chord/3` as an ad-hoc approximation, which has the same problem — it's angle-dependent and only accidentally correct near 90°.

**Fix:** Use `(4/3) * tan(sweep/4) * radius` in both:
- Phase C initial arc construction (lines ~292-297)
- `RecomputeArcControlPoints` post-merge recomputation

Where `radius = tangentDist / tan(halfAngle)` is already computed. This single formula is correct for all turn angles.

### 2. Both-nodes-moved merge produces degenerate straight Bezier (HIGH priority)

When both endpoints of an arc move during `MergeCoincidentNodes` (common on short shared edges between two filleted intersections), `RecomputeArcControlPoints` places both control points along the chord direction. This creates a near-straight-line Bezier with `MinRadiusOfCurvatureFt → double.MaxValue` — the arc has no speed constraint at all, which is unconservative for what was originally a genuine turn.

This case is triggered by the fix for root cause #3 (stale node references) — when `BuildMergeMap` repositions a survivor to the midpoint, arcs referencing both endpoints get both flagged as moved.

**Fix:** When both nodes moved, use the stored `TurnAngleDeg` and find the actual adjacent edge bearings at the new positions to derive proper tangent directions, rather than falling back to chord direction.

### 3. Systemic arc tangent misalignment (HIGH priority)

Layout validator reports **1143 `arc-tangent-misaligned` warnings on OAK alone**. Arcs approach their adjacent straight edges at sharp angles (often 180° — completely reversed tangent).

**Root cause:** Phase C computes the bezier tangent direction as `edgeBearing + 180°` (toward the intersection center). This is correct at creation time. But after:
- The intersection node is removed (Phase D, line 511)
- Adjacent edges are shortened/merged
- The global merge moves tangent nodes

...the tangent direction no longer aligns with the adjacent straight edge at the tangent point.

**Proposed fix:** After all merges, recompute each arc's control point directions using the **actual bearing of the adjacent straight edge** at each endpoint, combined with the correct depth formula from #1. For each arc endpoint:
1. Find the straight `GroundEdge` at that node that shares a taxiway name with the arc
2. Compute the bearing from the node along that edge
3. Set the control point direction = that bearing reversed (so the arc is tangent to the edge)
4. Depth = `(4/3) * tan(sweep/4) * radius` (from #1)

Fixing #1's depth formula first makes this fix produce geometrically correct arcs rather than approximately-correct ones.

### 4. Degenerate-radius arcs on genuine turns (MEDIUM priority)

29 arcs on OAK with `TurnAngleDeg > 30°` but `maxSafe < 1kt`. Multiple contributing causes:
- **Wrong depth formula (#1)** — produces incorrect curvature for non-90° turns
- **Both-nodes-moved degeneration (#2)** — produces zero curvature on merged arcs
- **`RadiusOfCurvatureFt` numerical instability** — when Bezier `speed` (first derivative magnitude) is near-zero, `speed³/cross` can produce `0` or `NaN` instead of `double.MaxValue`. The guard checks `cross < 1e-12` but not `speed < 1e-9`. A degenerate zero-length arc from a zero-length input edge hits this path.
- **Zero-length input edges** — if a degenerate edge survives graph construction (coincident coordinates in GeoJSON), `tangentDistFt → 0`, `radiusFt → 0`, producing a zero-area Bezier that gets added to `layout.Arcs` despite being meaningless.

**Fix:** Items #1 and #2 resolve most cases. Additionally:
- Guard `speed < 1e-9 → return double.MaxValue` in `RadiusOfCurvatureFt` before the `speed³/cross` division
- Skip arc creation when `tangentDistFt < 1.0` (degenerate input)

### 5. Collinear merge silently drops one taxiway name (MEDIUM priority)

When two edges from different taxiways are collinear through an intersection, the merged edge always takes `edgeA.TaxiwayName` (line ~398). There is no check or log when the names differ. This means pathfinder queries for `edgeB`'s taxiway name won't find the merged segment, breaking taxiway continuity.

**Possible contributor to #8** (wrong-direction reissue) — if `TaxiPathfinder.PickBestStartEdge` searches by taxiway name, a dropped name could cause it to skip the correct edge.

**Fix:** Log a warning when `edgeA.TaxiwayName != edgeB.TaxiwayName`. Consider preserving both names (e.g., a `TaxiwayNames` list on `GroundEdge`, matching the existing pattern on `GroundArc`).

### 6. `RebuildAdjacencyLists()` called per-node is O(N*E) (MEDIUM priority, performance)

`RebuildAdjacencyLists()` iterates all edges and all nodes. It's called once per intersection node in the fillet loop (line ~56). For SFO with hundreds of intersections and thousands of edges, this is quadratic. A targeted adjacency update (only touching changed edges) would be much more efficient.

Not a correctness issue, but affects airport load time for large airports.

### 7. Disconnected parking/helipad nodes with no warning (LOW priority)

`ConnectToNearestTaxiway` silently returns when no taxiway node is within `maxDistNm`. The parking/helipad node is added to the layout but has zero edges — unreachable in the graph. Aircraft assigned to that gate cannot taxi, with no log message explaining why.

Additionally, the method finds the nearest *node*, not the nearest point on an *edge*. A parking spot near the midpoint of a long taxiway segment connects to a distant endpoint rather than getting a projection point on the segment. This can produce unnecessarily long RAMP edges or exceed `ParkingConnectMaxNm` when a closer point-on-edge exists.

**Fix:** Log a warning when a parking/helipad cannot be connected. Consider point-to-edge projection for closer connections.

### 8. SKW3078 wrong-direction reissue (LOW priority)

Plan B also reported that SKW3078's taxi reissue at t=1076 produced a 143-segment route going the wrong direction. This is a `TaxiPathfinder.PickBestStartEdge` issue (ignores aircraft heading). Not a fillet bug — separate fix surface (`TaxiPathfinder.cs`, `GroundCommandHandler.cs`). See also #5 (collinear merge name loss) as a possible contributor.

### 9. Plan A — WJA1508 exit overshoot (LOW priority, may be resolved by #1-#3)

WJA1508 didn't traverse any arcs in the 60-second diagnostic window after landing. Needs a longer observation window or investigation of whether the exit path uses arcs at all. May be a separate issue (exit selection, not fillet geometry).

### 10. Defensive GroundNavigator speed floor (LOW priority)

Aviation-sim-expert recommended flooring `_currentNodeRequiredSpeed` to a minimum taxi crawl (2kt) so missing/degenerate arcs never permanently deadlock the sim. This is defense-in-depth, not a root cause fix.

### 11. Parking-approach straight edge preservation (FUTURE)

Fillet arcs on edges leading to parking spots should preserve enough straight edge for the aircraft body. Not yet investigated.

### 12. `JsonDocument` not disposed in GeoJsonParser (LOW priority, resource leak)

`JsonDocument.Parse()` at lines 40 and 123 rents memory from `ArrayPool<byte>` but the result is never disposed. Each airport load leaks the rented buffers. Fix: wrap in `using var doc = ...`.

### 13. Degenerate shortened edges logged at Debug, not Warning (LOW priority)

When a shortened edge is less than 1 ft (line ~357), it's logged at `Debug` level and still added to the layout. A zero-length edge can cause the navigator to loop on traversal. Should be `Warning` level, and the edge should be skipped (merge the tangent node directly with `otherNode`).

### 14. Arc dedup key uses unsorted display name (LOW priority)

`RemoveDuplicateArcs` uses `arc.TaxiwayName` (the joined display string) as part of the dedup key. Two arcs with `TaxiwayNames = ["W", "W3"]` vs `["W3", "W"]` produce different keys and won't be deduplicated. Should normalize by sorting `TaxiwayNames` in the key.

### 15. Merge convergence loop has no non-convergence warning (LOW priority)

`MergeCoincidentNodes` caps at 5 passes but doesn't log if it exits due to the cap rather than convergence. A transitive chain longer than 5 nodes would leave residual coincident nodes. Add a warning when the loop exits at the cap.

## Needs investigation (upstream, pre-fillet)

### A. TaxiwayGraphBuilder.InsertNodeInChain may desync NodeIds and Coords

`InsertNodeInChain` (TaxiwayGraphBuilder.cs ~line 182) inserts into `tw.NodeIds` but does **not** insert into `tw.Coords`. In contrast, `EnsureNodeInChain` inserts into both. After `InsertNodeInChain` runs, `NodeIds` is longer than `Coords` — their indices no longer correspond. `DetectIntersections` iterates `tw.Coords` for segment geometry using indices that assume alignment with `NodeIds`. Misalignment could produce phantom intersections or miss real ones, feeding incorrect topology into FilletArcGenerator.

**Status:** Needs verification — read both methods and confirm whether this is a real desync or if the reviewer misread the code.

### B. GeoJsonParser exception filter catches too narrowly

Only `InvalidOperationException` is caught (line 83). `JsonElement.GetProperty` throws `KeyNotFoundException`, `GetDouble()` throws `FormatException`, `int.Parse` (line 316) throws `FormatException`/`OverflowException`. A single malformed feature with e.g. `"heading": "abc"` crashes the entire airport parse instead of being skipped.

**Fix:** Widen the catch to include `JsonException`, `KeyNotFoundException`, `FormatException`, `OverflowException` — or catch `Exception` with specific logging.

### C. CoordinateIndex.FindNearest returns first-found, not actual nearest

The method returns the first node within snap tolerance, not the closest one. When multiple nodes fall within tolerance (common near closely-spaced parallel taxiways or hold-short lines), the result depends on dictionary iteration order — non-deterministic. Could affect graph topology consistency.

**Fix:** Track minimum distance and return the true nearest, or rename to `FindWithinTolerance`.

## Critical files (current state)

| File | Changes made |
|------|-------------|
| `src/Yaat.Sim/Data/Airport/AirportGroundLayout.cs` | Added `Origin`, `SourceIntersectionPosition` to `GroundNode`; `Origin` to `IGroundEdge`/`GroundEdge`/`GroundArc`; `EdgeBearingAtNode0Deg`, `EdgeBearingAtNode1Deg`, `TurnAngleDeg` to `GroundArc` |
| `src/Yaat.Sim/Data/Airport/FilletArcGenerator.cs` | `RecomputeArcControlPoints` (new), `BuildMergeMap` midpoint positioning + `repositionedSurvivors` output, `MergeCoincidentNodes` arc loop with repos detection, Phase C stamps construction params, Phase B stamps `SourceIntersectionPosition` |
| `src/Yaat.Sim/Data/Airport/CubicBezier.cs` | No changes (reverted the `speed < 0.1` threshold) |
| `src/Yaat.Sim/Data/Airport/GeoJsonParser.cs` | Origin stamps on nodes/edges |
| `src/Yaat.Sim/Data/Airport/TaxiwayGraphBuilder.cs` | Origin stamps |
| `src/Yaat.Sim/Data/Airport/RunwayCrossingDetector.cs` | Origin stamps |
| `src/Yaat.Sim/Data/Airport/TaxiPathfinder.cs` | Origin stamp on virtual ramp edge |
| `src/Yaat.Sim/Data/Airport/VirtualNode.cs` | Origin stamps |
| `tools/Yaat.LayoutInspector/LayoutValidator.cs` | **New** — validation pass |
| `tools/Yaat.LayoutInspector/LayoutAnalyzer.cs` | Arc detail + bearing + origin in `BuildNodeInfo`, `ComputeArcTangentAtNode` helper |
| `tools/Yaat.LayoutInspector/QueryResults.cs` | `ArcDetail` record with construction params, `Origin` on `NodeInfo`/`EdgeInfo` |
| `tools/Yaat.LayoutInspector/TextFormatter.cs` | Arc detail + origin in node display |
| `tools/Yaat.LayoutInspector/HtmlRenderer.cs` | Arc detail fields in JSON |
| `tools/Yaat.LayoutInspector/inspector-template.html` | Edge/arc hover tooltips |
| `tools/Yaat.LayoutInspector/SvgRenderer.cs` | **Deleted** |
| `tools/Yaat.LayoutInspector/Program.cs` | Removed SVG path, added auto-validation |
| `tests/Yaat.Sim.Tests/Simulation/FilletDiagnosticTests.cs` | **New** — 5 regression tests |
| `tests/Yaat.Sim.Tests/TestData/sfo-s1-ground-control-28-01-recording.yaat-bug-report-bundle.zip` | **New** — shared recording |

## Next step

Fix the systemic tangent misalignment (open issue #1). This is the core remaining geometry problem — arcs need to be tangent to their adjacent straight edges at each endpoint.
