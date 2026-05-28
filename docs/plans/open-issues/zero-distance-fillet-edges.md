# Zero-distance fillet edges — design options B and C

## Background

`FilletArcGenerator.Apply` (`src/Yaat.Sim/Data/Airport/FilletArcGenerator.cs`) walks every taxiway intersection and replaces it with a small graph "fan" of fillet artifacts: tangent-point nodes, `phase-c-arc` `GroundArc`s between them, plus `phase-d-*` `GroundEdge`s that re-attach the new fan to the rest of the graph (see `FilletEdgeKind` in `src/Yaat.Sim/Data/Airport/FilletProvenance.cs`). The `Shorten`/`ShortenDirect` variants are the post-arc straight edges that connect a surviving non-fillet endpoint to the nearest tangent so original geometry survives the rewrite. At SFO, when a tangent placement lands right on top of a preserved intersection node (or two cleanup passes converge on the same point) the resulting `phase-d-shorten` (or `phase-d-preserve`) edge has its two endpoints at the same lat/lon and `DistanceNm ≈ 0`.

Bearing on a straight `GroundEdge` is computed at read time by `DirectionalEdge.DepartureBearing` / `ArrivalBearing` (`AirportGroundLayout.cs:504-513`) as `GeoMath.BearingTo(FromNode.Position, ToNode.Position)`. For two coincident lat/lons this is `Atan2(0, ~0) ≈ 0°`; in practice round-off and the haversine-y/x formula land it on whatever bearing the floating-point residuals dictate, which is the "inherited 118°" the SFO 1471↔30 case exhibits — not literally stored, but indistinguishable from the neighbour's bearing because there is no real direction the math can recover. V2's `SegmentExpander.LocalSearchToJunction` (`src/Yaat.Sim/Data/Airport/V2/SegmentExpander.cs:472-508`) and `AutoRouter` (`src/Yaat.Sim/Data/Airport/V2/AutoRouter.cs:225-242`) treat that bogus bearing as the next arrival bearing and the following admissibility check rejects forward continuation with a 180° delta — bug #165 (SKW3404 SFO taxi spin) on the V2 path.

Option A (in flight): teach the pathfinder to detect `DistanceNm < NoOpEdgeThresholdNm` (~1.2 ft) via `GeometricAdmissibility.IsNoOpEdge`, admit those edges unconditionally, and propagate the *prior* arrival bearing through them. That keeps V2 finding the route, but it leaves the underlying graph semantically wrong: every consumer that ever reads `DepartureBearing`/`ArrivalBearing` on these edges has to remember the same workaround, and `GroundNavigator.SetupCurrentSegment` (`src/Yaat.Sim/Phases/Ground/GroundNavigator.cs:420`) already reads `seg.Edge.DepartureBearing` straight off the materialised route without that guard — `RouteMaterialiser.BuildSegments` (`src/Yaat.Sim/Data/Airport/V2/RouteMaterialiser.cs:54-64`) doesn't strip no-op edges. Options B and C are about deleting the booby trap from the data, not papering over it at every reader.

## Option B — coalesce co-located node pairs post-generation

### Concrete approach

Extend the existing `MergeCoincidentNodes` pass at `FilletArcGenerator.cs:1828` so it doesn't leave behind the cases it currently skips. Today the merge only operates on `GroundNodeType.TaxiwayIntersection` candidates (`BuildMergeMap` at `FilletArcGenerator.cs:2191`) and refuses to merge a "has-runway-edge" node with a "no-runway-edge" node (`FilletArcGenerator.cs:2215-2220`). The 1471↔30 pair survives because one endpoint anchors a runway-crossing/centerline edge while the other does not, so the runway-asymmetry guard rejects them.

Two sub-options inside B, neither requires new files:

**B1 — relax the runway-asymmetry guard for genuinely coincident nodes.** When the geographic separation is below `CoincidentNodeThresholdNm` (≈5 ft) AND one node's `FilletProvenance is TangentNodeProvenance`, prefer the original (non-tangent) node as the survivor and let the rewrite proceed. The tangent was placed there by `GetOrCreateTangentNode` (`FilletArcGenerator.cs:2526`) precisely to be the runway-side anchor — it has nothing the original node doesn't. The runway-displacement watchdog at `FilletArcGenerator.cs:141-172` will catch any case where the merge accidentally walks a centerline edge off bearing.

**B2 — add a dedicated post-pass `CollapseZeroDistanceEdges`.** Iterate `layout.Edges` once; for any `GroundEdge` whose `DistanceNm < 1e-7` (≈0.6 ft, well under any real intersection separation), keep one endpoint as the survivor (preferring non-`TangentNodeProvenance`, then non-RescueOrphan, then lower id for determinism), redirect every edge and arc reference to the survivor, drop the zero-distance edge itself, then call the existing `RemoveDuplicateEdges`/`RemoveDuplicateArcs`/`RemoveRedundantArcs`/orphan cleanup. This is effectively the lower half of `MergeCoincidentNodes` factored out and triggered on zero-edge-distance instead of node-position-distance — and it catches cases B1's relaxation misses (e.g. two tangent nodes from different intersections that happened to land on top of each other).

Either way the signature is unchanged: `FilletArcGenerator.Apply(AirportGroundLayout)` still returns a `FilletStatistics` record. Add one field, `ZeroDistanceEdgesCollapsed` (or roll it into `CoincidentNodesMerged`), and log accordingly.

### Consumers affected

- `RouteMaterialiser.BuildSegments` (`V2/RouteMaterialiser.cs:54-64`) — currently emits a `TaxiRouteSegment` per edge; after B, the zero-distance segments simply don't exist in the graph and won't appear in routes. No code change needed.
- `GroundNavigator.SetupCurrentSegment` (`Phases/Ground/GroundNavigator.cs:420`) and `BuildSpeedConstraints` (which also reads `Edge.ArrivalBearing`/`DepartureBearing`, see `GroundNavigator.cs:634, 713, 928, 945, 972, 1022`) stop seeing bogus 0° / inherited bearings because the offending segments are gone. The existing Option A guard in `SegmentExpander.cs:506-508` and `AutoRouter.cs:231-233` becomes dead code that can stay as a belt-and-braces defence.
- `LayoutInspector` (`tools/Yaat.LayoutInspector/HtmlRenderer.cs`, `LayoutAnalyzer.cs`, `TextFormatter.cs`) reads `Origin` strings purely for display — fewer `Fillet:phase-d-shorten@... #X↔#Y` entries in the dump and one fewer node id on the rendered map, but no behavioural dependence. Verified by grepping the tool: no consumer parses `phase-d-*` semantically.
- Tests:
  - `tests/Yaat.Sim.Tests/FilletArcGeneratorTests.cs` checks `stats.OrphansRescued`, `stats.CoincidentNodesMerged`, and reads `FilletEdgeProvenance.Kind == FilletEdgeKind.RescueOrphan`. Existing counts shift; tests that pin specific counts (e.g. `stats.OrphansRescued == 0`) need to be re-baselined against the new pass output.
  - `tests/Yaat.Sim.Tests/Simulation/FilletDiagnosticTests.cs` greps `Origin.Contains("phase-c-arc@268 ")` — unaffected, since corner arcs aren't the target of B.
  - `tests/Yaat.Sim.Tests/Simulation/OakGaSpawnTurnAroundTests.cs` and `tests/Yaat.Sim.Tests/TaxiPathfinderTests.cs` reference `phase-d-shorten` only in commentary, not assertions.
  - `tests/Yaat.Sim.Tests/Simulation/Issue165SkwTaxiSpinTests.cs` — currently relies on the route resolving correctly; B should make it pass on V2 without Option A's bearing-propagation special case being needed.

The renderer-side risk that "the pair was visible as two dots" is purely cosmetic; SFO's 1471 and 30 sit on top of each other already, so merging them removes a phantom marker the operator never noticed.

### Risks / known landmines

- The runway-asymmetry guard exists for a reason: at runway thresholds, the fillet places a tangent slightly off the centerline so a turning aircraft transitions cleanly. The current 5 ft `CoincidentNodeThresholdFt` and the runway/non-runway-mismatch check together keep that small offset from being merged into the centerline. B1's relaxation must keep both nodes' edges runway-bearing-stable. The existing `RUNWAY DISPLACED` log scan (`FilletArcGenerator.cs:141-172`) already fires per-intersection but only during the FilletNode pass; the cleanup that runs after needs the same scan, or B1 must restrict itself to cases where the runway-side and taxi-side endpoint positions match to sub-foot precision.
- `bridge-path-reverse-arc-scoring` and `pure-pursuit-entry-alignment-60deg` memories: V1's `BfsToTaxiway`/`SelectBestBridgeCandidate` walks edges and counts reverse-traversed arcs along the bridge. Collapsing 1471 into 30 removes a 1-hop hop that the bridge currently passes through. There's a non-zero chance a route that today bridges `parking → 1471 → 30` now bridges directly `parking → 30` with a different arc orientation at the new endpoint. Mitigation: add `tests/Yaat.Sim.Tests/Simulation/OakGaSpawnTurnAroundTests.cs`'s SIG4/GA3/GA7 cases to the test matrix when changing the merge — they're the regression canary for this class of bug.
- Cluster-synth planner (`GroundNavigator.PlanSynthesisLookahead`) consumes segment lengths in feet (`CornerLookaheadFt = 120 ft`, etc.). Removing one ~0 ft segment shifts segment indices but not lengths; cluster detection should be neutral. Worth re-running the existing N7lj recording-replay tests.
- Bug-bundle snapshots and `tests/Yaat.Sim.Tests/TestData/*-recording.zip` files don't serialise node ids — recordings reference aircraft by callsign and lat/lon, so collapsing nodes is invisible to replay.

### Effort + test strategy

**Medium**, with a B1-only variant being **small**. B1 is a ~30-line change inside `BuildMergeMap` plus a survivor-preference helper; B2 is a new ~120-line method plus a clean place to wire it after `MergeCoincidentNodes` in `Apply`. The shared cost is the runway-bearing safety net and the test rebaseline pass.

Tests to add:

- `FilletArcGeneratorTests` — load `sfo.geojson`, assert no `GroundEdge` exists with `DistanceNm * GeoMath.FeetPerNm < 1.0` after `Apply`. This is the canonical zero-distance-edge guard and should cover every fixture without naming specific node ids.
- `FilletArcGeneratorTests` — for each test airport (`sfo`, `oak`, `fll`), assert that no two distinct nodes share a position to within `NoOpEdgeThresholdNm` (the V2 threshold). Same guard, expressed as a node-pair invariant.
- `Issue165SkwTaxiSpinTests.Skw3404_DoesNotOrbitDuringTaxi` should pass under V2 without the `IsNoOpEdge` bearing-propagation branch. After B lands, temporarily revert the V2 `IsNoOpEdge` arrival-bearing branch in a scratch commit and confirm #165 still passes — that's the proof B obviated Option A's special case.

Tests likely to break:

- Any `FilletArcGeneratorTests` test that pins an exact node count or arc count for SFO/OAK/FLL — rebaseline.
- `Skw3404_Seg12_PathfinderDiagnostic` and similar diagnostic tests that print resolved routes will show one fewer segment per collapsed pair.

### Recommendation: do

It directly removes the wrong data instead of training every reader to mask it. Pick B1 first (cheap and targeted at the runway-threshold case the 1471↔30 SFO pair is in); fall back to B2 only if airports keep producing other coincident-pair classes the guard relaxation misses.

## Option C — fix `FilletArcGenerator`

### Concrete approach

The `phase-d-shorten` edge exists because Phase D1 (`PhaseD1_ShortenEdges`, `FilletArcGenerator.cs:1213-1479`) needs to keep the non-fillet "far" end of the original edge attached to the new fillet fan — the aircraft can still walk from far node to tangent and the route is preserved. The far node is `edge.OtherNode(intersection)` and the tangent is the farthest tangent placed on that edge; the new edge spans from one to the other with the original taxiway name (`Shorten`/`ShortenDirect`) or to the preserved intersection itself (`Preserve`, `PhaseD4_CleanupAndFinalize:1654-1717`). Each emit path is unconditional: it always creates the edge even if `dist ≈ 0`.

Three sub-options:

**C1 — skip emission when the geometry would be degenerate.** At each `layout.Edges.Add(new GroundEdge { ... DistanceNm = shortenDist ... })` site (`FilletArcGenerator.cs:1306, 1334, 1416, 1679, 1706` for the four kinds: passthrough, shorten-with-nearest, standard-shorten, preserve-direct, preserve-neighbour), gate on `shortenDist > NoOpEdgeThresholdNm` (move that constant from V2 into a shared place — `FilletArcGenerator` is the producer, V2 currently borrows it as a reader). Verify nothing depended on the edge existing for graph connectivity by running the existing orphan-rescue pass (`RescueOrphanedTangentNodes`, `FilletArcGenerator.cs:2087`) — if it has to rescue more nodes than baseline, the skip broke connectivity. The skip-paths log a debug line so we can see in which intersections the case fires.

**C2 — collapse the tangent placement upstream.** When `GetOrCreateTangentNode` is asked to place a tangent within `NoOpEdgeThresholdNm` of an existing intersection node on the same edge, return the existing intersection node instead of creating a new tangent. The dedup logic at `GetOrCreateTangentNode:2535-2545` already does this for *previously created tangents on the same edge*; extending it to also check the intersection's far node (or the preserved intersection itself) prevents the duplicate from ever being created. This is the cleanest fix because no `phase-d-shorten` edge ever needs to be skipped — the survivor *is* the tangent.

**C3 — abandon `phase-d-shorten` as a real edge.** This is the "shorten is just a logical operation, not a real edge" framing. It would mean rewriting Phase D so the original `GroundEdge` between far node and intersection is mutated in-place (one endpoint replaced with the tangent) rather than consumed-and-replaced. Major surgery: the consumed-edges pattern (`ctx.ConsumedEdges`, `FilletArcGenerator.cs:922`) is central to Phase D's correctness, and the immutable-`Nodes` decision on `GroundEdge` would need revisiting. Listed for completeness but it's a multi-day refactor that touches every phase-d path.

### Consumers affected

Same surface as B: `RouteMaterialiser`/`GroundNavigator`/`LayoutInspector`/`FilletArcGeneratorTests`. The differences:

- C1 (skip-emission) can leave `RescueOrphanedTangentNodes` doing more work — a `TangentNodeProvenance` node that would have had a `phase-d-shorten` to the far node now has none, and the rescue pass invents a `RescueOrphan` edge instead. That edge's distance is the real geographic distance, so it doesn't have the zero-bearing problem; but it does mean the diagnostic output gets noisier (`stats.OrphansRescued > 0` where today it's 0 on most airports).
- C2 (upstream collapse) removes the tangent node entirely when it would coincide with the intersection. Anything that *counts* tangent nodes per intersection — there's nothing in `src/` or `tests/` that does, grepped — would see one fewer per affected pair. `AddDirectShortensFromArcAnchors` (`FilletArcGenerator.cs:596`) groups by `(IntersectionId, Taxiway, DestinationNodeId)` and processes chains of `members.Count < 2`. Fewer members means fewer chains qualifying for the U-turn fix; needs verification on the FLL chain that motivated `AddDirectShortensFromArcAnchors` in the first place.
- C3 would touch enough that a code-impact survey is its own task; deferring.

### Risks / known landmines

- **Why does `phase-d-shorten` exist at all?** Re-reading the code and docstring: it's "the non-fillet endpoint stays connected to the new fillet fan via a straight edge with the original taxiway's name." Three reasons it can't be silently dropped:
  1. **Taxiway name preservation.** The shorten edge carries `edge.TaxiwayName` (e.g. "B", "T") so a walker stepping off the intersection onto the taxiway sees the right name in `MatchesTaxiway`. The original edge gets `ConsumedEdges`-removed, so without the shorten there is no edge on the original taxiway leaving the far node.
  2. **Bridge connectivity for V1.** `TaxiPathfinder.BfsToTaxiway` (`TaxiPathfinder.cs`, see [project_taxi_pathfinder_two_paths.md]) bridges across explicit user `TAXI` commands by walking same-taxiway edges. If the shorten is dropped and the corner arc is the only path off the intersection, V1's same-taxiway bridge BFS dead-ends.
  3. **Phase D4's preserve-mode fallback** depends on having a tangent node to stub to (`FilletArcGenerator.cs:1679`). If the tangent didn't get created (C2), the preserve fallback creates a `Preserve` edge directly to the original neighbour — which is fine geometrically but skips the "tangent on first edge" smoothness check.

  So C1 is safe iff the skipped case is *also* one where dropping connectivity is benign — i.e. the tangent and the intersection are the same point, meaning the corner arc already lands at the intersection. C2 makes this explicit by never creating the tangent in the first place.

- C2's interaction with `AddDirectShortensFromArcAnchors` (the FLL U-turn fix) needs targeted regression. The original FLL repro is in commentary in `FilletArcGenerator.cs:574-595` — there's no test pinned to a specific issue number for it. A worktree run of `FilletArcGeneratorTests.Apply_Fll_NoOrphansRescued` (line 498-ish based on the earlier grep) is the closest gate.
- C1's `OrphansRescued` count drift is benign for runtime but breaks `FilletArcGeneratorTests` assertions that pin `OrphansRescued == 0`. Either rebaseline or tighten C1 so the orphan-rescue case doesn't fire on the zero-distance class (skip when the would-be shorten's far endpoint is already adjacent to the tangent's arc — i.e. the corner-arc itself replaces the shorten).
- Cross-repo: yaat-server consumes the same `AirportGroundLayout` via the shared `Yaat.Sim` reference. No yaat-server code reads `Origin`/`FilletProvenance` (grep confirms — `src/Yaat.Sim/Data/Airport/FilletProvenance.cs` is the only producer). Cross-repo build verification via `pwsh tools/test-all.ps1` is still mandatory because Yaat.Sim signature changes (e.g. adding a `FilletStatistics` field) require a sibling-repo recompile.

### Effort + test strategy

**Small** for C1 (~30 lines plus tests), **small-medium** for C2 (~50 lines in `GetOrCreateTangentNode` plus careful re-routing of Phase D when the tangent collapses into the intersection — the existing `tanNodeA.Id == tanNodeB.Id` branch at `FilletArcGenerator.cs:1142-1153` shows the shape of the handling). **Large** for C3.

Tests to add:

- `FilletArcGeneratorTests` — same zero-distance and coincident-pair invariants as B's tests. They're orthogonal to the fix path and should pass under either option.
- A targeted test that places a synthetic tangent within 1 ft of an intersection in a hand-built mini-layout, runs `Apply`, and asserts the resulting graph has no `phase-d-shorten` edges of `DistanceNm < NoOpEdgeThresholdNm`. This is the unit-level guard.
- Re-run `Issue165SkwTaxiSpinTests` to confirm V2 succeeds; same proof-by-removal of Option A's bearing-propagation branch as in B.

Tests likely to break:

- `FilletArcGeneratorTests` orphan-rescue assertions (C1) — rebaseline.
- Any test that observes a specific arc/edge count at SFO or OAK — rebaseline. Spot-checked the SFO node 268 / SFO node 16 patterns mentioned in `FilletDiagnosticTests` — they're targeting `phase-c-arc` (corner arcs), not phase-d-shorten, so should be unaffected.

### Recommendation: defer behind B

C1 fixes the symptom but leaves the architecture (zero-distance straight edges with a synthesised TaxiwayName) in place — `FilletArcGenerator` could still emit one tomorrow via a different code path (the four emit sites are all candidates for the same bug). C2 fixes it at the source but is harder to reason about because the tangent-vs-intersection collapse has knock-on effects on `AddDirectShortensFromArcAnchors`. B (especially B1) is the smallest blast radius for the most consumer-visible fix.

C2 is the right long-term home if B's runway-asymmetry relaxation turns out to be brittle. C1 is the right short-term home if B causes any unforeseen route regression at the runway-fillet corner — a single-line emission-skip is easy to ship behind a feature flag for one release.

## Comparison + recommendation

**Pick B1 first.** It's the smallest change that deletes the bad data: relax `BuildMergeMap`'s runway-asymmetry rejection when one node is a `TangentNodeProvenance` co-located with the other within ~1 ft, prefer the non-tangent survivor, and rely on the existing post-merge cleanup (`RemoveDuplicateEdges`, `RemoveDuplicateArcs`, `RemoveRedundantArcs`, orphan-pruning) to flush out the consequences. Option A's `IsNoOpEdge` bearing-propagation in `SegmentExpander`/`AutoRouter` stays as defence-in-depth.

**Trigger for promoting to B2 or C2.** If, after B1 ships, the post-fillet validator (the existing `dotnet test` invariant tests plus the new "no zero-distance edges" assertion) still flags any airport, that's the signal that B1's tangent-survivor preference doesn't cover every case. At that point either:
- Add B2 (a generic post-pass that collapses any zero-distance edge regardless of node provenance), or
- Drop into C2 (refuse to create the duplicate tangent in the first place at `GetOrCreateTangentNode`).

Both are independent of B1 and either can be added without reverting it.

**Do not invest in C3.** The `phase-d-shorten` edges are real data — they carry the taxiway name across the new fillet fan and underpin V1's `BfsToTaxiway` bridges. Removing them as a category requires re-architecting Phase D and the cost dwarfs the bug.
