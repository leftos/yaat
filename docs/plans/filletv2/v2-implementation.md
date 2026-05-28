# Fillet V2 — implementation specification

**Status:** Authoritative spec for V2 fillet work (May 2026). Multi-agent consensus passes 1–5 were consolidated into this file; intermediate plan docs were removed.

**Triage log:** [`v2-divergences.md`](./v2-divergences.md) — Legacy vs V2 parity findings during step 3f+.

---

## Overview

Replace the legacy in-place fillet pipeline (`FilletArcGenerator.Apply` with Phase A–D and five global repair passes) with a **plan-then-execute** generator:

```
raw layout → planner → immutable FilletPlan → executor → normalizer
```

**Hard invariant:** one physical corner → at most one `GroundArc`.
**Not** an invariant: one cut per arm — arms may have **0..N ordered cuts** when geometry requires it.

**Default:** `FilletMode.Legacy` until step 5 parity + aviation review; then flip to `V2` and delete legacy implementation.

---

## Shipped (steps 1–2 — do not replan)

Under `src/Yaat.Sim/Data/Airport/`:

| Piece | Notes |
|-------|-------|
| `IFilletArcGenerator` | Stateless; mutates layout in place |
| `FilletMode` | `None`, `Legacy`, `V2` |
| `FilletGeneratorFactory`, `FilletArcGeneratorRouter` | Selection |
| `FilletArcGeneratorRegistry.All` | `none` + `legacy` until V2 `Apply` works |
| `FilletStatistics` | Per-pass tallies; V2 adds `Warnings` init property in step 3e |
| `GeoJsonParser.Parse(..., FilletMode)` | Default `Legacy` |
| Comparison harness | `tests/Yaat.Sim.Tests/Helpers/LayoutCloner.cs`, `FilletComparison.cs` |

`FilletArcGeneratorV2` exists but throws until step 3e.

---

## Design principles

| Principle | Detail |
|-----------|--------|
| Per-pair geometry | Each corner pair gets legacy-equivalent Phase A math **before** arm-level resolution |
| Least topology | Prefer **one cut per arm**; add **ordered multi-cut** only when single-cut distorts a valid corner |
| No max-wins footgun | Legacy `PhaseA_ComputeFillets` uses per-pair placements so collinear pairs do not corrupt sharp corners via unconditional MAX — V2 must not reintroduce that |
| Explicit plan | Every split, merge, arc, stub, and removal is a `FilletPlan` op — executor does not infer shortcuts |
| No repair passes | V2 does not run orphan rescue, duplicate-arc removal, parallel bypass, or direct-shorten repair |
| Plan-time shared arms | Adjacent junctions sharing an arm: scale cut sets + `TangentMergeOp`; normalizer 5 ft merge is **backstop only** |
| V2 provenance | `Origin` strings only; delete `FilletProvenance` with legacy in step 5 |

Legacy repair counters on V2 must stay **zero**: `OrphansRescued`, `RedundantPreserveEdgesRemoved`, `DuplicateCornerArcsRemoved`, `ParallelBypassEdgesRemoved`, `DirectShortensAdded`.

---

## Pipeline

```
ManualArcDetector
  → TaxiwayArmBuilder
  → JunctionClassifier
  → CornerPlanner              // per-pair IdealTangentFt + CornerSpec
  → ArmCutResolver             // single-cut-first; multi-cut fallback; calls SharedArmTangentPass
  → FilletPlanBuilder          // freeze immutable FilletPlan
  → FilletPlanExecutor
  → FilletGraphNormalizer
```

---

## Constants (`Fillet/FilletConstants.cs`)

Shared with legacy via delegation in step 3a. V2-specific thresholds (tune in **3f** against OAK/SFO/FLL):

| Name | Initial value | Purpose |
|------|---------------|---------|
| `RadiusFloorFt` | 5.0 | Below this, corner is degenerate |
| `DistortionThreshold` | **2.0** | Reject single-cut when `effective_radius / RequestedRadiusFt >` this |
| `AsymmetryThreshold` | 2.0 | Reject single-cut when `max(ta,tb)/min(ta,tb) >` this |
| `CoincidentNodeThresholdFt` | 5.0 | Coalesce cuts / merge nodes (legacy value) |
| `MinArmSegmentGapFt` | 5.0 | Min distance between distinct cuts on one arm |
| `CollinearThresholdDeg` | 15.0 | From legacy |
| `MinFilletAngleDeg` | 15.0 | From legacy |
| `MaxTangentDistFt` | 150.0 | From legacy |
| `DefaultRadiusFt` | 75.0 | Type max radius for generic taxiway corners |
| `HighSpeedExitRadiusFt` | 150.0 | Runway + turn ≤ 45° |
| `RunwayExitRadiusFt` | 100.0 | Runway + sharper turn |
| `RampRadiusFt` | 50.0 | Ramp edges |

---

## `ArmCutResolver` algorithm

Compatibility-gated: try single cut per arm; fall back to ordered multi-cut only for arms that fail validation.

### Per junction

```
For each corner c:
  c.IdealTangentFt = legacy-equivalent symmetric tangent (radius × tan(turn/2), capped)

For each arm A (non-collinear / non-preserve-only):
  requests = (cornerId, IdealTangentFt) for corners on A
  ideals = sorted distances from requests

  // Single-cut candidate
  if max(ideals) - min(ideals) <= CoincidentNodeThresholdFt:
    candidate[A] = average(ideals)
  else:
    candidate[A] = min(max(ideals), IntersectionCapFt, MaxTangentDistFt)

  For each corner c:
    ta = candidate[c.ArmA]; tb = candidate[c.ArmB]
    r = EffectiveMinRadiusFromBezier(ta, tb, c.TurnAngleDeg)
    if r < RadiusFloorFt OR r / c.RequestedRadiusFt > DistortionThreshold
       OR max(ta,tb)/min(ta,tb) > AsymmetryThreshold:
      mark arms c.ArmA, c.ArmB as needing multi-cut

  For each arm A NOT marked:
    emit one ResolvedArmCut(distance=candidate[A], OwningCornerIds=all corners on A)

  For each arm A marked:
    positions = sorted distinct IdealTangentFt; coalesce within 5 ft
    enforce MinArmSegmentGapFt; cap; emit ordered ResolvedArmCut per position
    emit PlanWarning(SINGLE_CUT_REJECTED) once per arm

  Re-validate all corners against actual cut positions; drop degenerate with PlanWarning

SharedArmTangentPass:
  For OtherIntersection terminus: scale endpoint cut lists if extents overlap; merge within 5 ft → TangentMergeOp

Demotion loop until stable:
  1. smallest effective radius (< floor)
  2. smallest turn angle above CollinearThresholdDeg
  3. lowest CornerId (reachability tiebreak deferred to 3f)
  → PlanWarning(CORNER_DEMOTED); record real airports in v2-divergences.md
```

### Canonical 3-way test (0° / 100° / 200°, 75 ft radius)

- Single-cut MAX on arm 0° = 63 ft inflates corner (0°,200°) to ~357 ft effective radius → **> 2.0 × 75** → multi-cut.
- Arms 0° and 200° get cuts at **~13 ft** and **~63 ft** (50 ft gap).
- Arm 100° stays single cut at 63 ft.
- All three corners keep ~75 ft authored radius.

---

## Module layout

```
src/Yaat.Sim/Data/Airport/
  FilletArcGeneratorV2.cs
  Fillet/
    FilletConstants.cs
    FilletGeometry.cs          // turn angle, radius caps, bezier build, effective min radius
    FilletEligibility.cs
    ManualArcDetector.cs
    FilletGraphNormalizer.cs
    V2/
      TaxiwayArm.cs
      TaxiwayArmTerminus.cs     // enum: OtherIntersection, ShapePointTerminus, RunwayCenterline, HoldShort, Parking, DeadEnd
      PolylineChain.cs
      TaxiwayArmBuilder.cs
      JunctionKind.cs           // Skip, Simple, MultiCorner, Preserve
      JunctionPlan.cs
      JunctionClassifier.cs
      CornerSpec.cs
      CornerPlanner.cs
      ArmCutResolver.cs
      SharedArmTangentPass.cs
      FilletPlan.cs
      FilletPlanBuilder.cs
      FilletPlanExecutor.cs
      PlanWarning.cs
```

Internal `V2EdgeKind` (diagnostics only, not serialized): `ArmSubEdge`, `PreserveStub`, `RunwaySplit`. **No** separate `TangentLink` — multi-cut sub-segments use `ArmSubEdge`.

---

## Data types

```csharp
public sealed record CornerSpec(
    int CornerId,
    int JunctionNodeId,
    int ArmIdA,
    int ArmIdB,
    double TurnAngleDeg,
    double RequestedRadiusFt,
    double IdealTangentFt,
    double BearingAToJunctionDeg,
    double BearingBToJunctionDeg);

public sealed record ResolvedArmCut(
    int CutId,
    int JunctionNodeId,
    int ArmId,
    double DistanceAlongArmFt,
    LatLon Position,
    double BearingTowardJunctionDeg,
    IReadOnlyList<int> OwningCornerIds);

public sealed record ArmCutOp(int CutId);
public sealed record TangentMergeOp(int CutIdA, int CutIdB);
public sealed record CornerArcOp(int CornerId, int CutIdAtArmA, int CutIdAtArmB);
public sealed record PreserveStubOp(int JunctionNodeId, int CutId);

public sealed record FilletPlan(
    IReadOnlyDictionary<int, ResolvedArmCut> Cuts,
    IReadOnlyList<ArmCutOp> ArmCuts,
    IReadOnlyList<TangentMergeOp> TangentMerges,
    IReadOnlyList<CornerArcOp> CornerArcs,
    IReadOnlyList<PreserveStubOp> PreserveStubs,
    IReadOnlyList<int> JunctionNodesToRemove,
    IReadOnlySet<GroundEdge> EdgesToRemove,
    IReadOnlyList<PlanWarning> Warnings);

public sealed record PlanWarning(int? JunctionNodeId, int? CornerId, string Code, string Message);
```

**`PlanWarning.Code` (minimum):** `DEGENERATE_RADIUS`, `SINGLE_CUT_REJECTED`, `CORNER_DEMOTED`, `SHARED_ARM_SCALED`, `NO_OWNING_CUT`.

`FilletStatistics` — add when wiring V2 (step 3e):

```csharp
public IReadOnlyList<PlanWarning> Warnings { get; init; } = [];
```

---

## Executor

Reads `FilletPlan` only; never mutates then re-decides.

1. **CreateTangentNodes** — one node per cut; `Origin` = `"V2:tangent-cut@J{junctionId}/{taxiway}"`.
2. **ApplyTangentMerges** — collapse pairs; rewire `CutId` refs.
3. **SplitArmsIntoSubEdges** — straight segments in **distance-from-junction order**; all `ArmSubEdge`. Runway: split, do not consume.
4. **CreateCornerArcs** — `FilletGeometry.BuildBezier` from stored bearings + cut positions; P1/P2 recomputed, never `+=` translated.
5. **AddPreserveStubs** — preserved junctions only.
6. **RemoveConsumedTopology** — planned removals only.
7. **FilletGraphNormalizer** — recompute distances/radii, rebuild adjacency once, validate; 5 ft coincident merge backstop only.

Forbidden: eligibility, radius selection, orphan rescue, duplicate-arc dedup, parallel bypass, direct-shorten inference.

---

## Normalizer

- Recompute `GroundEdge.DistanceNm` and arc `DistanceNm` / `MinRadiusOfCurvatureFt`.
- Rebuild adjacency **once**.
- Validate: no missing refs, self-loops, zero-length edges, arcs with `MinRadiusOfCurvatureFt < 5`.
- Defensive 5 ft merge + bezier rebuild if planner missed a `TangentMergeOp` (non-zero `CoincidentNodesMerged` on V2 → triage).

---

## Comparison gates (3f / 4)

| Gate | Rule |
|------|------|
| Structural | Valid graph for every generator |
| Repair counters | Five legacy repair fields **== 0** on V2 |
| Corner buckets | Position + taxiway pair + bearing ~5° + min radius ±10% |
| Runway bearing | ±1° per centerline segment |
| Connectivity | Hold-short BFS set equality **and** parking → hold-short BFS |
| Warnings | Every real-airport `PlanWarning` row in `v2-divergences.md` |
| `LayoutCloner` | Full `GroundRunway` field copy before broad parity reuse |

LayoutInspector (step 4): `--fillet=none|legacy|v2`, `--fillet-diff <airport>`.

---

## Implementation steps

| Step | Work | Runnable |
|------|------|----------|
| **3a** | Extract `FilletConstants`, `FilletGeometry`, `FilletEligibility`, `ManualArcDetector`; legacy delegates | No change |
| **3b** | `TaxiwayArm`, `TaxiwayArmTerminus`, `PolylineChain`, `TaxiwayArmBuilder` + walk parity tests | No |
| **3c** | `JunctionClassifier`, `CornerPlanner`, `CornerSpec`, corner-key dedup tests | No |
| **3d** | `ArmCutResolver`, `SharedArmTangentPass`, `FilletPlan`/`FilletPlanBuilder`, `PlanWarning` + resolver unit tests | No |
| **3e** | `FilletPlanExecutor`, wire `FilletArcGeneratorV2.Apply`, V2 in `Registry.All`, `FilletStatistics.Warnings` | **V2 runnable** |
| **3f** | Extend `FilletComparison`, OAK/SFO/FLL parity, tune thresholds, triage divergences | Legacy default |
| **3f-pass6** | Plan connectivity ops (`ArmBypassOp`, `StraightConnectorOp`, `ReconnectEdgeOp`); literal execute; harness hard-fail surfaces planner gaps | See [`v2-pass-6-connectivity-ops.md`](./v2-pass-6-connectivity-ops.md) |
| **4** | LayoutInspector fillet flags | Tooling |
| **5** | Aviation review; default `V2`; delete `FilletArcGenerator.cs`, `LegacyFilletArcGenerator.cs`, `FilletProvenance.cs` | V2 default |

### 3d required tests

- Simple 90° → single cut per arm.
- Symmetric 4-way → single cut per arm.
- 3-way 0°/100°/200° → multi-cut on two arms; ideal radii.
- Requests within 5 ft → coalesced single cut.
- Shared short arm → proportional scale + merge.
- SFO @268-style dedup → one arc per physical corner key.
- Demotion order + lowest `CornerId` tiebreak.
- Level-3 plan → repair counters still 0 after 3e apply.

---

## Intentional V2 divergences (do not “fix” to legacy)

| Area | Legacy | V2 |
|------|--------|-----|
| Mixed junction | Per-pair Phase A + repair passes | Per-pair plan + gated multi-cut; repair stats 0 |
| Bezier after merge | `+=` control-point translation | Rebuild from bearings |
| Shared arm | Post-hoc `MergeCoincidentNodes` | Plan-time scale + `TangentMergeOp` |
| Unfittable corner | Silent distortion / repair | `PlanWarning` + skip/demote |

Document every parity difference in [`v2-divergences.md`](./v2-divergences.md).

---

## Legacy reference

- Algorithm source: `src/Yaat.Sim/Data/Airport/FilletArcGenerator.cs` (`PhaseA_ComputeFillets` ~989+, per-pair comment ~998–1001).
- Tests: `tests/Yaat.Sim.Tests/FilletArcGeneratorTests.cs`, `tests/Yaat.Sim.Tests/Fillet/FilletComparisonTests.cs`.

---

## Connectivity rewrite (post rounds 1–14)

The geometry layer (`TaxiwayArmBuilder`, `JunctionClassifier`, `CornerPlanner`, `ArmCutResolver`, `FilletGeometry`) is sound and stays. The **connectivity/execution layer** — `FilletPlanExecutor` edge construction plus the pass-6 `FilletArmChainPlanner` / `FilletConnectivityPlanner` reconnect/bypass/side-branch passes — is being rewritten. The pass-6 work drifted back into per-junction walk reconstruction with order-dependent node removal: exactly the legacy pathology V2 set out to kill.

### Known hard cases (learnings from rounds 1–14)

These are the cases every connectivity attempt must satisfy. Each one broke a prior round.

1. **Order-independence is the core invariant.** The executor must compute the final surviving node set *before* building any edge, build every edge against that set, and materialize once. The bug class behind every round-9→14 structural failure was *create-then-strip*: an edge built for junction A is later deleted when junction B is removed, or a chain op references a node a later pass deletes. Never remove nodes incrementally inside the per-junction loop; never build an edge whose endpoint can later vanish.

2. **The removal set must be single-sourced.** The executor removed junction nodes on `!PreserveNode`; the planner computed `JunctionNodesToRemove` from a narrower predicate. The two drifted, so the consistency validator accepted ops referencing nodes the executor then deleted → `shorten@J{N} references missing node {N-1}`. There must be exactly one `removedNodes` set, consulted by both planner validation and executor.

3. **Preserved junctions must stay wired to their tangents.** FLL 301, OAK 28/155/305/387, SFO 538/823 all ended isolated (`Node has no edges`) because they were `PreserveNode=true`, their original edges were consumed, and nothing reconnected them to their arc tangents. The `MaxPreserveToCutSpanFt = 5 ft` cap (added to stop one spurious long shortcut) severed every preserved junction whose tangent sat at a normal 50–150 ft distance. A preserved junction connects to the nearest cut on **each** of its arms — no distance cap.

4. **Shared edges between two adjacent filleted junctions connect tangent-to-tangent.** Edge `A—B` where both A and B are filleted: A's cut (toward B) and B's cut (toward A) split it into `[A..cutA] [cutA..cutB] [cutB..B]`. The middle survives as one tangent→tangent sub-edge; the inner stubs are consumed (or become preserve stubs). A removed junction node must **never** appear as an edge endpoint — it is always replaced by its cut tangent on the relevant arm. This is what `SharedArmTangentPass` / `TryResolveSharedJunctionFarCut` approximated incompletely.

5. **Arms overlap.** `TaxiwayWalk` continues through intermediate junctions that have a same-taxiway continuation (it stops only on a same-taxiway branch count ≠ 1). So one taxiway edge can sit on the walk of several junctions' arms. Per-junction walk reconstruction therefore double-emits (J67, J139, J181 all rebuilt the T6 corridor through node 301) and forced the dedup/guard sprawl. The rewrite processes each **original edge once**, splitting it by whatever cuts land on it from any junction — overlap is handled structurally, not by dedup.

6. **Runway-centerline arms** must be split, not consumed, and must not chain through intermediate stable walk steps (an aircraft does not stop at a mid-runway intersection). `IsRunwayCenterline` arms keep their centerline and only receive tangent splits at corners.

7. **Hold-shorts are structurally inviolable.** A `RunwayHoldShort` node must never be left with zero edges; its pre-fillet link to the parent intersection survives fillet unconditionally.

8. **Coincident merges (e.g. SFO 359/887, 2.4 ft apart)** are resolved by redirecting the V2 tangent cut onto the coincident pre-fillet stable (`FilletPlanCutRedirect.ExtendWithStableAnchors`). The merge must not produce a self-loop: after redirect, a `shorten` whose two endpoints both resolve to the survivor (`887→887`) must be dropped before/within the normalizer, and adjacency rebuilt before the isolated-node sweep.

### Rewrite model — global edge-split executor

Returns to the proposal's original §Executor intent ("SplitArmsIntoSubEdges in distance-from-junction order"), done **globally and order-independently** rather than per-junction.

**Precompute (pure):**
- `removed` = non-preserve filleted junction node ids (single source of truth; executor never recomputes from `!PreserveNode`).
- `tangentByCut[cutId]` = one GroundNode per surviving cut, after `TangentMerge` + stable-anchor redirect (coincident cut → existing pre-fillet stable node, no new node).
- `cutsOnEdge[edge]` = cuts whose arm-walk straddling step is that original edge (map each cut to the walk step whose cumulative-distance range contains `DistanceAlongArmFt`).
- `consumedSteps` per arm = walk steps strictly inside the outermost cut (fully consumed), plus the straddling step (split, inner part consumed).

**Materialize once:**
1. **Nodes** = preFillet − `removed` + tangent nodes (− merged duplicates).
2. **Original edges**: for each original edge, split at the cuts on it. Emit each segment between consecutive split points / surviving endpoints as a straight edge, EXCEPT the inner segment adjacent to a filleted junction (dropped if the junction is removed; emitted as a preserve stub if preserved). Fully-consumed steps (inside the outermost cut, no cut on them) are dropped. A removed-junction endpoint is replaced by its cut tangent; if a segment would still reference a removed node, drop + warn.
3. **Corner arcs**: one `GroundArc` per `CornerArcOp`, `tangent(cutA)`–`tangent(cutB)`, control points from `FilletGeometry.BuildBezier`.
4. **Straight connectors / collinear-through**: tangent→tangent per op.
5. **Hold-short safety**: any `RunwayHoldShort` left with zero edges gets its pre-fillet parent link restored.
6. Set `layout.Edges`/`layout.Arcs`/`layout.Nodes` to the built sets; **no incremental removal**.
7. **Normalizer**: recompute distances/radii, rebuild adjacency, drop self-loops/degenerate edges, merge 5 ft coincident (backstop), rebuild adjacency **again** before the isolated-intersection sweep (the missing second rebuild is why round-14 self-loops/isolated nodes survived).

Deletes on completion: `FilletArmChainPlanner`, the `ArmChainEdgeOp`/`ReconnectEdgeOp`/`ArmBypassOp` reconnect sprawl, the `MaxPreserveToCutSpanFt`/`MaxHoldShortTangentSpanFt` band-aid caps, and the executor's `!PreserveNode` incremental node deletion.
