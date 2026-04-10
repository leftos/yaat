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

### 1. Systemic arc tangent misalignment (HIGH priority)

**The main remaining issue.** Layout validator reports **1143 `arc-tangent-misaligned` warnings on OAK alone**. Arcs approach their adjacent straight edges at sharp angles (often 180° — completely reversed tangent).

**Root cause:** Phase C computes the bezier tangent direction as `edgeBearing + 180°` (toward the intersection center). This is correct at creation time. But after:
- The intersection node is removed (Phase D, line 511)
- Adjacent edges are shortened/merged
- The global merge moves tangent nodes

...the tangent direction no longer aligns with the adjacent straight edge at the tangent point. The arc curves away from the edge instead of being tangent to it.

**Proposed fix:** After all merges, recompute each arc's control point directions using the **actual bearing of the adjacent straight edge** at each endpoint, not the stale stored `EdgeBearingAtNode0Deg`/`EdgeBearingAtNode1Deg`. For each arc endpoint:
1. Find the straight `GroundEdge` at that node that shares a taxiway name with the arc
2. Compute the bearing from the node along that edge
3. Set the control point direction = that bearing reversed (so the arc is tangent to the edge)
4. Depth = `chord/3` or derived from the turn angle and chord

This is the right fix because it ensures arcs are always tangent to their adjacent edges regardless of merge history.

### 2. Degenerate-radius arcs on genuine turns (MEDIUM priority)

29 arcs on OAK with `TurnAngleDeg > 30°` but `maxSafe < 1kt`. These are real turns whose bezier geometry is still broken after the recomputation — likely because the `chord/3` depth isn't appropriate for all turn angles. The tangent alignment fix (#1) may resolve most of these since correct tangent directions produce better bezier shapes.

### 3. Plan A — WJA1508 exit overshoot (LOW priority, may be resolved by #1)

WJA1508 didn't traverse any arcs in the 60-second diagnostic window after landing. Needs a longer observation window or investigation of whether the exit path uses arcs at all. May be a separate issue (exit selection, not fillet geometry).

### 4. SKW3078 wrong-direction reissue (LOW priority)

Plan B also reported that SKW3078's taxi reissue at t=1076 produced a 143-segment route going the wrong direction. This is a `TaxiPathfinder.PickBestStartEdge` issue (ignores aircraft heading). Not a fillet bug — separate fix surface (`TaxiPathfinder.cs`, `GroundCommandHandler.cs`).

### 5. Defensive GroundNavigator speed floor (LOW priority)

Aviation-sim-expert recommended flooring `_currentNodeRequiredSpeed` to a minimum taxi crawl (2kt) so missing/degenerate arcs never permanently deadlock the sim. This is defense-in-depth, not a root cause fix.

### 6. Parking-approach straight edge preservation (FUTURE)

Fillet arcs on edges leading to parking spots should preserve enough straight edge for the aircraft body. Not yet investigated.

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
