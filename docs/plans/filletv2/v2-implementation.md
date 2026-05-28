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
