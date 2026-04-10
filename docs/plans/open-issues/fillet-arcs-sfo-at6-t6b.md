# Fix: Fillet arcs missing at SFO A/T6 and A/T6B intersections

## Context

At SFO, taxiway A crosses three short terminal stub taxiways very close together:
T6, T6A, T6B (all 6–9 points each, spanning only ~0.001° — roughly 300–400 ft of A).
Each crossing is a "T" where A(west) and A(east) both need a fillet arc onto the
perpendicular stub taxiway — so every intersection should generate **2 arcs**
(one for each turn direction off A).

Observed by the user, visually and via pathfinding:
- **A/T6A** correctly has 2 arcs.
- **A/T6** and **A/T6B** each have only 1 arc.

Consequence: when a pilot is routed (e.g., from the A/D intersection to gate D15
via A → T6B), pathfinding sees no arc on one side of the T, forcing the aircraft
to taxi past, U-turn, and come back to catch the only arc that exists. Bad
geometry and unrealistic taxi.

The intent of the fillet algorithm is symmetric — at a 3-edge T intersection it
should always produce 2 arcs (+ 1 collinear merge for A_west↔A_east). Something
about the A/T6 and A/T6B junctions (but not A/T6A) breaks that symmetry.

## Hypotheses to investigate

These are lines of inquiry, **not a commitment to a fix**. The actual root cause
will be identified in Phase 1 below before any code is changed.

- **H1 — Close-neighbor tangent collapse.** After A/T6 is filleted, its tangent
  point on the A-east edge lands close to A/T6B (which is ~100 ft away on A).
  When A/T6B is then filleted, its A-west tangent lands near A/T6's tangent.
  The post-fillet `MergeCoincidentNodes` (5 ft threshold, `FilletArcGenerator.cs`
  :506) collapses them. Arcs whose endpoints become identical are removed as
  degenerate (`:564`), duplicate (`:567`), or redundant vs a straight edge
  (`:571` — `RemoveRedundantArcs` matches on any taxiway name in `arc.TaxiwayNames`,
  so an arc tagged `["A","T6"]` is removed if a straight `A` edge exists with the
  same two endpoints).

- **H2 — Single tangent-per-edge loses a pair's geometry.** `RecordTangentPoint`
  (`:806`) keeps only the largest tangent distance when an edge participates in
  multiple pairs. At a T intersection, edge T6 is in two pairs (A_west↔T6 and
  A_east↔T6). If the two pairs compute different turn angles (because A has a
  slight bend at the junction via `IntermediatePoints` — see `InitialBearing`
  `:860`), the smaller-angle pair's arc is drawn between mismatched tangent
  distances. Shouldn't *delete* an arc, but could interact with H1 to make one
  degenerate.

- **H3 — A polyline bend.** At SFO the "A" taxiway is long and slightly
  curved. If A has an intermediate point right at the T6/T6B junction, the
  parser may split it so A_west and A_east bearings differ by, say, 170° (10°
  turn) instead of 180°. That's still < `CollinearThresholdDeg` (15°,
  `:16`) so it's classified as a collinear merge — fine. But if the bend is
  sharper (say 20°), A_west and A_east are now an *arc pair* themselves, and the
  topology is 3 arcs + 0 merges instead of 2 arcs + 1 merge. Could produce
  asymmetric tangent-distance interactions.

- **H4 — Pair never generated.** The simplest possibility: for one of the two
  expected pairs, the nested loop at `FilletArcGenerator.cs:193-235` never adds
  it to `plannedArcs` at all. E.g. the edge got turned into a `GroundArc` by an
  *earlier* fillet iteration on an adjacent node — the collection loop at
  `:157-164` skips arcs, so the pair is silently dropped.

H4 is the most suspicious given that A/T6A works and A/T6/T6B don't:
A/T6A might be processed first (by node id order), consuming the A edges from
both sides, leaving A/T6 and A/T6B with only partial straight-edge adjacency.

## Phase 1 — Investigate (no fixes yet)

Debug together, not via sub-agents (per project memory).

- [ ] **Dump topology at the three intersections.** Use
  `tools/Yaat.LayoutInspector` against `tests/Yaat.Sim.Tests/TestData/sfo.geojson`
  both *with* and *without* fillets applied. The inspector supports
  `--taxiway T6`, `--taxiway T6A`, `--taxiway T6B`, and `--node <id>`, and has
  an `applyFillets` flag in `LayoutAnalyzer.Load` (`LayoutAnalyzer.cs:19`).
  Add a CLI switch or small ad‑hoc diff if one isn't already exposed.
  - Record: node ids at each A/Txx intersection, edge count per node, bearings
    of each edge, whether edges are `GroundEdge` or `GroundArc`.
  - Compare A/T6A (working) vs A/T6 and A/T6B (broken) — what is structurally
    different?

- [ ] **Add targeted debug logging in `FilletNode`.** Temporarily log, *only*
  for the three SFO intersection node ids, inside `FilletArcGenerator.FilletNode`
  (`src/Yaat.Sim/Data/Airport/FilletArcGenerator.cs:149`):
  - `:157-164` — count and types of edges collected (how many `GroundEdge` vs
    how many skipped `GroundArc`).
  - `:193-235` — for each pair: bearingA, bearingB, turnAngle, and whether it
    was classified merge / arc / dropped.
  - `:267-322` — for each planned arc: whether Phase C actually emitted it, or
    skipped due to `tanNodeA.Id == tanNodeB.Id` (`:276`).
  - After `MergeCoincidentNodes` (`:506`): count of arcs removed as degenerate
    (`:564`), duplicate (`:568`), or redundant (`:571`).

- [ ] **Run a small repro program** (or an xUnit test tagged `[Fact(Skip=...)]`
  while investigating) that loads `sfo.geojson`, applies fillets, and for each
  of the three intersection regions prints the surviving arcs whose endpoints
  sit on A and Txx. Confirm the asymmetry matches the user's visual observation
  (2 arcs at A/T6A, 1 arc each at A/T6 and A/T6B). This becomes the seed for
  the failing test in Phase 2.

- [ ] **Decide the root cause** before editing production code. Write it into
  this plan file under a "Root cause" heading.

## Phase 2 — Failing test (TDD)

Once Phase 1 identifies the actual cause, encode it as a failing test *before*
fixing. Per project memory: write the failing test, confirm it fails, then fix.

- [ ] Add `SFO_FilletArcs_TTerminalStubs_EachHaveTwoArcs` to
  `tests/Yaat.Sim.Tests/FilletArcGeneratorTests.cs` (existing file at
  `tests/Yaat.Sim.Tests/FilletArcGeneratorTests.cs:1`) or as a new file
  alongside `FilletPathfindingTests.cs` (which already has a `LoadSfo()` helper
  around `:24-32`).
  - Load `TestData/sfo.geojson` via the same mechanism `FilletPathfindingTests`
    uses.
  - For each of A/T6, A/T6A, A/T6B:
    - Find the two arcs whose `TaxiwayNames` contain both `"A"` and the stub
      name (i.e. the two A↔Txx fillets).
    - Assert count == 2 and that one arc is on each side of the junction
      (distinguishable by checking which A-side tangent node each arc's A end
      sits closer to — e.g. compare longitude relative to the junction).
  - This test must fail against `main` before any fix lands.

- [ ] Add a narrower unit test in `FilletArcGeneratorTests.cs` that reproduces
  the root cause with a synthetic 3-edge layout (no real geojson), so future
  regressions are caught without loading SFO.

## Phase 3 — Fix

**Left intentionally unspecified** — depends on Phase 1 findings. Candidate
shapes of fix for each hypothesis:

- If **H4** (pair never generated due to arc-skip at `:157-164`): the
  intersection-processing order matters. Options:
  1. Process intersections in an order that fillets tight clusters together
     before their shared neighbors are touched (e.g. sort by local edge density,
     or do a two-pass algorithm that plans all arcs globally before mutating).
  2. When collecting edges at `:157-164`, also recognise "virtual" edges via
     arc endpoints on the adjacent tangent nodes so pairs aren't lost.

- If **H1** (coincident merge removes legit arcs): tighten the redundancy
  check at `:696-722` so `RemoveRedundantArcs` doesn't remove an arc whose
  `TaxiwayNames` represent a *different turn* than the straight edge it
  collides with, even when they share a single name.

- If **H2/H3** (bearing/tangent interaction via intermediate points or sharp
  A bend): store a per-pair tangent distance instead of a per-edge max, so
  each arc's geometry is internally consistent.

- [ ] Implement the fix with the smallest change that restores symmetry.
- [ ] Remove all temporary debug logging added in Phase 1.

## Phase 4 — Verification

- [ ] `dotnet test tests/Yaat.Sim.Tests --filter "FullyQualifiedName~Fillet"` —
  all fillet tests pass, including the new SFO ones.
- [ ] `dotnet test tests/Yaat.Sim.Tests --filter "FullyQualifiedName~SFO"` —
  existing SFO E2E tests (`AirportE2ETests.cs` around `:730-900`) still pass
  with no new regressions.
- [ ] `pwsh tools/test-all.ps1` — full build + test across yaat and yaat-server.
- [ ] `dotnet build -p:TreatWarningsAsErrors=true` — zero warnings.
- [ ] **Visual verification**: run the client, load a SFO scenario, open the
  ground view, and confirm both A/T6 and A/T6B now show fillet arcs on *both*
  sides of the A junction (matching what A/T6A already shows).
- [ ] **Pathfinding verification**: route an aircraft from the A/D intersection
  to gate D15 (via A → T6B) and confirm the route no longer makes the
  observed U-turn.

## Critical files

- `src/Yaat.Sim/Data/Airport/FilletArcGenerator.cs` — fillet generation and
  cleanup (903 lines, main focus).
- `src/Yaat.Sim/Data/Airport/AirportGroundLayout.cs` — `GroundNode`, `GroundEdge`,
  `GroundArc`, `RebuildAdjacencyLists` (`:457`), `AllEdges` (`:451`).
- `src/Yaat.Sim/Data/Airport/GeoJsonParser.cs:244-248` — where `FilletArcGenerator.Apply`
  is called in the parse pipeline (step 8).
- `tests/Yaat.Sim.Tests/FilletArcGeneratorTests.cs` — unit tests to extend.
- `tests/Yaat.Sim.Tests/FilletPathfindingTests.cs` — has `LoadSfo()` helper for
  real-geojson tests (`:24-32`).
- `tests/Yaat.Sim.Tests/TestData/sfo.geojson` — test data.
- `tools/Yaat.LayoutInspector/LayoutAnalyzer.cs:19` — `applyFillets` flag for
  before/after comparison.
- `docs/airport-layouts/sfo-layout.md:136-138` — T6/T6A/T6B reference data.

## Non-goals

- Not rewriting the fillet algorithm. Smallest targeted fix for the symmetry
  bug only.
- Not touching pathfinding. The observed zig-zag is a *consequence* of the
  missing arc; once the arc exists, pathfinding recovers on its own.
- No migration path or feature flag — unreleased project, replace in place.
