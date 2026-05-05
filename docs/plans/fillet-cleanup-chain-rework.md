# Fillet Cleanup Chain Rework

**Status:** open, design phase. No code yet.
**Origin:** Item #9 from a clean-room review of `src/Yaat.Sim/Data/Airport/FilletArcGenerator.cs`. The other 15 review items have already landed.

## TL;DR

`FilletArcGenerator` runs six post-passes after the per-intersection
fillet construction loop. Each pass exists because per-pair-independent
tangent placement creates messes that the next pass cleans up. Each
new airport-specific bug tends to add another pass. The end goal of
this rework is to eliminate or sharply reduce these passes by making
tangent placement *graph-aware* — i.e., aware of neighboring
intersections sharing the same edges — at planning time instead of
patching up the result.

## The cleanup chain (current state)

`Apply` calls these in order, after `FilletNode` runs once per intersection:

1. **`MergeCoincidentNodes`** (~115 LOC, 5-pass loop) — merges
   TaxiwayIntersection nodes within 5 ft of each other, including
   bezier-handle translation for arcs whose endpoints moved. Now
   recomputes radius before the degenerate-arc filter (fix #1).
2. **`RescueOrphanedTangentNodes`** (~95 LOC) — finds tangent nodes
   with only arc edges (no straight edges) and connects them to the
   nearest same-taxiway node. Pure workaround for connectivity gaps
   in PhaseD1.
3. **`RemoveRedundantPreserveEdges`** (~65 LOC) — removes preserve
   edges when a closer same-direction edge already covers the path.
   Now requires a triangle-inequality between-check (fix #7).
4. **`RemoveDuplicateCornerArcs`** (~115 LOC) — when upstream
   intersections inject parallel bypass edges, the per-pair iterator
   produces multiple arcs at the same corner with smaller radii.
   Keeps the largest-radius arc per cluster.
5. **`RemoveParallelBypassEdges`** (~110 LOC) — removes phase-d
   straight-edge chains when a longer "bypass" exists alongside a
   shorter same-taxiway chain (SFO @141 ↔ @268 pattern).
6. **`AddDirectShortensFromArcAnchors`** (~195 LOC) — when an
   intersection emits multiple tangent nodes for the same direction,
   the chain has only one shorten anchor. Adds direct shorten edges
   from arc anchors at the opposite endpoint (FLL U-turn pattern).

`MergeCoincidentNodes` is essentially mandatory geometry cleanup
across intersection boundaries. The other five each have a docstring
that describes which airport / which corner motivated them. That
pattern is the smell.

## Why per-pair tangent placement creates the mess

For an intersection X with N edges, PhaseA computes one tangent
distance *per edge pair* (`(N choose 2)` pairs). Each pair's tangent
distance is `radius * tan(turnAngle / 2)`, capped at the edge length
(`MaxTangentDistFt = 150ft`). On any given edge there can be
*multiple* tangent placements at different distances, deduplicated
only when within 5 ft of each other (`CoincidentNodeThresholdNm`).

When a single physical edge is shared by two adjacent intersections X
and Y, neither knows what the other is going to place on the edge.
The intersection cap at line 1116 (`DistToFirstIntersectionFt`)
limits each side to half the available length — but this is a static
budget, not a coordinated allocation. The result is a mix of:

- Tangent nodes from X that walk past Y's edge boundary.
- Tangent chains on the same physical edge from both X and Y, which
  manifest as parallel chains the bypass-removal pass has to clean.
- Multiple corner arcs at the same physical corner, when parallel
  bypass edges existed pre-fillet (the duplicate-corner-arc pass).

## Recent context that helps

Several recent commits make this rework more tractable:

| Commit | Why it matters |
|--------|----------------|
| `e7a51fc0` (#3) | Generalized PhaseD1 to split *every* chain tangent's edge, not just `farthest`. Removes some of `RescueOrphanedTangentNodes`'s reason for existing. |
| `930a1080` (#11) | **Typed `FilletProvenance`.** The cleanup passes now pattern-match on `CornerArcProvenance`, `TangentNodeProvenance`, `FilletEdgeProvenance(kind, …)` instead of parsing origin strings. Reading `RemoveParallelBypassEdges` and `AddDirectShortensFromArcAnchors` is no longer an exercise in regex. |
| `4400b5df` (#20) | `Apply` returns `FilletStatistics`. Tests can assert on per-pass counts without scraping logs. |

Every new edge created by a fillet phase has a typed `FilletProvenance`
attached. Use this exclusively in any new code — do not re-introduce
`Origin?.StartsWith(…)` patterns.

## Proposed direction (sketch — not prescriptive)

Three increasingly ambitious options:

### Option A — Per-edge tangent budget, allocated globally

For each physical edge, before any per-pair planning runs, allocate
a "tangent budget" between its two endpoint intersections. Each
intersection then picks tangent distances within its half of the
edge. Eliminates parallel chains by construction.

- Lower-risk: keeps the existing PhaseA/B/C/D structure.
- Should let us delete `RemoveDuplicateCornerArcs`,
  `RemoveParallelBypassEdges`.
- Doesn't address the `AddDirectShortensFromArcAnchors` (multi-tangent
  chain inside a single intersection's edge) case directly.

### Option B — Per-intersection planner that knows neighbor pairs

Each intersection enumerates its edge pairs, but a planner pass first
gathers *all* (intersection, edge, pair) requirements globally. When
two pairs at the same intersection both want a tangent on edge E,
the planner emits *one* tangent at the further distance and routes
the closer pair's arc through that one node. (Currently the closer
pair gets its own tangent, which is what causes the chain.)

- Should let us delete `AddDirectShortensFromArcAnchors`.
- Some risk of changing arc geometry — closer pair's arc was sized
  for closer tangent; reusing the further tangent changes its radius.
- May still need duplicate-corner-arc cleanup for pre-existing
  parallel pavement that's actually distinct corners.

### Option C — Full graph-aware pipeline

Treat each physical taxiway pavement as a 1-D coordinate system. All
fillet operations on that pavement (multiple intersections, multiple
pairs) project onto the 1-D coordinate, get sorted by position, and
emit tangent nodes in a single coordinated pass. The bezier
construction, edge splitting, and chain connectivity all derive from
the sorted positions.

- Highest payoff: deletes 4–5 of the 6 cleanup passes.
- Largest scope: rewrites PhaseA/B/C/D as a coordinated graph
  algorithm. Probably 2–3 days of design + implementation + testing.

## Test infrastructure to lean on

- **`MultipleTangentsInManualArcChain_NoRescueOrphansNeeded`** in
  `tests/Yaat.Sim.Tests/FilletArcGeneratorTests.cs` — synthetic
  scenario that asserts `stats.OrphansRescued == 0`. Add similar
  assertions for `DuplicateCornerArcsRemoved`, `ParallelBypassEdgesRemoved`,
  `DirectShortensAdded` in scenarios that previously triggered them.
  When all four can drop to zero across the airport test suite, the
  corresponding cleanup passes can be deleted.
- **`Airport_FilletProducesCleanGraph`** runs the fillet generator
  against OAK, SFO, SJC, FAT, HWD, MER, RNO. Asserts no degenerate
  edges, no coincident pairs, ≥90% reachability, valid arc radii,
  and per-node connectivity. This is the integration safety net.
- **`FilletStatistics`** (`Apply`'s return value) is the right
  observability surface for "did this rework actually remove the
  cleanup work?" Track each statistic at the airport level.
- **LayoutInspector** (`tools/Yaat.LayoutInspector`) renders
  airports interactively with `--html`. Use it when investigating
  why a specific corner produces a duplicate / orphan / bypass.

## Watch out for

- **Node ID stability.** Several tests hard-coded specific tangent
  node IDs. Fix #3 already shifted IDs and required updating
  `ResolveExplicitPath_SfoM2_UsesSameTaxiwayArcAtA1Apex` to identify
  arcs by intersection-of-origin. Any rework that changes pair
  iteration order or PhaseBC's arc-creation order will likely shift
  more IDs. Check failing tests for ID hard-coding before assuming
  a real regression.
- **`MergeCoincidentNodes` translates bezier handles.** It does this
  to preserve tangent direction, but it's a workaround for
  tangent-position drift across adjacent intersections. A
  graph-aware planner could place tangents at the same shared point
  in the first place, eliminating the merge step's reason to touch
  bezier handles at all.
- **Manual-arc shape-point chains.** `IsShapePointNode` excludes
  2-edges-same-twy nodes from filleting; they're treated as curve
  geometry. PhaseD1's manual-arc branch (now generalized in #3) is
  a separate code path from the standard walk. Any rework needs to
  preserve the "don't destroy manual curves" property.
- **Runway centerline edges** also flow through the manual-arc
  branch (because `landsInManualArc = edge.IsRunwayCenterline`).
  The variable is overloaded — see review item #13. A rename to
  something like `RequiresEdgeSplit` would clarify.

## Suggested first move

Before picking an option, write airport-level statistics tests that
record current values of `OrphansRescued`,
`DuplicateCornerArcsRemoved`, `ParallelBypassEdgesRemoved`,
`DirectShortensAdded`, `RedundantPreserveEdgesRemoved` for each of
the seven test airports. These become the "this rework didn't make
things worse" guardrail and the "this rework deleted the need for
pass X at airport Y" success metric.
