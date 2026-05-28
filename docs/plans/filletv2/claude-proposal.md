# Clean-Room Fillet Generator — Claude Proposal

**Status:** Proposal — not implemented
**Companion proposals:** [`codex-proposal.md`](./codex-proposal.md), [`cursor-proposal.md`](./cursor-proposal.md)
**Related:** [`docs/ground-layout-generation.md`](../../ground-layout-generation.md), [`src/Yaat.Sim/Data/Airport/FilletArcGenerator.cs`](../../../src/Yaat.Sim/Data/Airport/FilletArcGenerator.cs)

## Summary

Add `FilletArcGeneratorV2` under `src/Yaat.Sim/Data/Airport/Fillet/` that runs side-by-side with the legacy generator behind a shared `IFilletArcGenerator` interface and a `FilletMode { None, Legacy, V2 }` selector. V2 is built around four ideas:

1. **Arms, not edges, are the planning primitive.** One arm per outbound taxiway run from an intersection.
2. **One tangent per arm, not per pair.** Eliminates `TangentLink` chains and the U-turn class of bugs.
3. **Planning is a pure function over the original graph.** The planner never reads its own mutations. No `RebuildAdjacencyLists` between intersections.
4. **The executor applies an immutable, ordered op list.** Six post-hoc cleanups disappear because the planner never creates the structures they exist to repair.

Both Legacy and V2 implement `IFilletArcGenerator`, so the parity harness, LayoutInspector, and `GeoJsonParser` all consume the same surface. Default remains `Legacy` until a parity harness shows V2 matches or improves all eight existing fillet-test airports.

## Root-cause framing

The legacy pipeline's nine cleanups exist because of one design choice in `FilletArcGenerator.PhaseA_ComputeFillets` (`FilletArcGenerator.cs:998`): tangent placement is computed **per edge pair**. A 4-arm intersection with N=4 edges generates up to N·(N-1)/2 = 6 candidate arcs and up to 6 tangent nodes per arm. The cleanups then fold the duplicates:

| Cleanup | Symptom of per-pair planning |
|---|---|
| `RemoveDuplicateCornerArcs` (`FilletArcGenerator.cs:239`) | Same physical corner, multiple arcs from pair combinatorics |
| `RemoveParallelBypassEdges` (`FilletArcGenerator.cs:385`) | Adjacent fillets create parallel chains on a shared arm |
| `AddDirectShortensFromArcAnchors` (`FilletArcGenerator.cs:596`) | Tangent chains need shortcut edges so the BFS exit walker doesn't U-turn |
| `RescueOrphanedTangentNodes` (`FilletArcGenerator.cs:2087`) | Phase D's edge surgery consumes an edge that another intersection's tangent still needed |
| `MergeCoincidentNodes` (`FilletArcGenerator.cs:1828`) | Adjacent intersections produce coincident tangents (5 ft) and translate bezier P1/P2 incorrectly |
| `RemoveRedundantPreserveEdges` (`FilletArcGenerator.cs:1999`) | Preserve stubs duplicate a shorten edge with the same direction |

If planning is corner-centric and arm-centric, every entry in that table becomes structurally impossible.

## Design principles

1. **Single principle, no mixed mutation.** The planner reads `AirportGroundLayout` only. It produces an immutable `FilletPlan`. The executor reads `FilletPlan` and mutates the layout. The planner cannot read its own output.
2. **Arms are the primitive.** An arm is a contiguous taxiway run from an intersection, through shape-point nodes, ending at a classifiable terminus. There is one arm per outbound `GroundEdge` after `(otherId, taxiwayName)` dedup.
3. **One tangent per arm.** The arm's tangent is the minimum of all per-corner tangent distances that involve the arm. Per-pair tangents (and the `TangentLink` chain they require) are not built.
4. **Corners are keyed, not iterated.** A corner is `(intersectionId, sorted(armA.id, armB.id))`. Each corner is computed once and produces at most one arc.
5. **Shared tangents resolved at planning time.** Two intersections that share an arm negotiate their tangent placements in the same planning pass. If the cuts would overlap, both fall back to a proportional split.
6. **Bezier control points are computed, never translated.** The arc stores `(EdgeBearingAtNode0Deg, EdgeBearingAtNode1Deg, TurnAngleDeg, RadiusFt)` and rebuilds `(P1, P2)` from them on demand. The legacy `+=` translation fix in `MergeCoincidentNodes` becomes a `Rebuild()` call.
7. **Validation, not rescue.** Any plan that would orphan a node fails the build with a logged diagnostic — no `RescueOrphanedTangentNodes` backstop.
8. **Preserve mode is per-arm, not per-node.** Runway-threshold and collinear intersections are flagged at the junction level; the preserve stub is from the intersection to the nearest tangent on that arm. Collinear arms do not contribute arcs.

## Architecture

```
src/Yaat.Sim/Data/Airport/Fillet/
  IFilletArcGenerator.cs       # shared interface; Legacy + V2 both implement
  FilletMode.cs                # enum: None, Legacy, V2
  FilletStatistics.cs          # moved out of legacy; common result record
  FilletGeneratorFactory.cs    # FilletMode → IFilletArcGenerator
  FilletConstants.cs           # MinFilletAngleDeg, CollinearThresholdDeg, radius caps, MaxTangentDistFt
  FilletGeometry.cs            # pure math: turn angle, kappa, bezier rebuild from stored bearings
  Legacy/
    LegacyFilletArcGenerator.cs   # thin IFilletArcGenerator wrapper around existing static class
  V2/
    Arm.cs
    ArmBuilder.cs                 # AirportGroundLayout → IReadOnlyList<Arm>
    Junction.cs
    JunctionClassifier.cs         # eligibility + preserve flagging (replaces IsEligibleForFilleting)
    CornerPlan.cs                 # one (armA, armB) corner with tangent distances and bezier params
    FilletPlan.cs                 # immutable; Junctions, Corners, ArmCuts, TangentMerges
    FilletPlanner.cs              # layout → FilletPlan (pure, no mutation)
    FilletPlanExecutor.cs         # FilletPlan + layout → mutated layout
    FilletArcGeneratorV2.cs       # IFilletArcGenerator implementation
```

### `IFilletArcGenerator` — the common surface

```csharp
namespace Yaat.Sim.Data.Airport.Fillet;

/// <summary>
/// Common surface for all fillet arc generators (Legacy, V2, future variants).
/// Implementations must be stateless — Apply() can be called concurrently against
/// different layouts. They mutate the passed layout in place and return a
/// statistics record describing what they did.
/// </summary>
public interface IFilletArcGenerator
{
    /// <summary>Stable identifier for diagnostics, logs, and the LayoutInspector
    /// <c>--fillet=</c> flag (e.g., "legacy", "v2").</summary>
    string Name { get; }

    /// <summary>The mode this generator implements. Must round-trip with
    /// <see cref="FilletGeneratorFactory.Create"/>.</summary>
    FilletMode Mode { get; }

    /// <summary>Apply fillet arcs to all eligible intersections in <paramref name="layout"/>.
    /// Mutates in place. <paramref name="options"/> carries diagnostic toggles
    /// (debug logging, validation warnings) shared across implementations.
    /// Returns a <see cref="FilletStatistics"/> record; the schema is shared so
    /// the parity harness can diff results without knowing which implementation
    /// produced them.</summary>
    FilletStatistics Apply(AirportGroundLayout layout, FilletOptions options = default);
}

public readonly record struct FilletOptions(
    bool EmitDebugLogs = false,
    bool EmitValidationWarnings = true,
    bool FailFastOnOrphans = false);   // V2 honors; legacy ignores
```

`FilletStatistics` (already a `sealed record` at `FilletArcGenerator.cs:10`) moves into `Fillet/FilletStatistics.cs` unchanged. V2 fills `OrphansRescued`, `RedundantPreserveEdgesRemoved`, `DuplicateCornerArcsRemoved`, `ParallelBypassEdgesRemoved`, `DirectShortensAdded` with zero — those fields stay in the schema so the parity harness can assert V2 reports zero on each (the structural impossibility claim).

### `FilletGeneratorFactory`

```csharp
public static class FilletGeneratorFactory
{
    public static IFilletArcGenerator Create(FilletMode mode) => mode switch
    {
        FilletMode.None => NullFilletArcGenerator.Instance,        // returns FilletStatistics.Empty
        FilletMode.Legacy => new LegacyFilletArcGenerator(),
        FilletMode.V2 => new FilletArcGeneratorV2(),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
}
```

The factory is the only place callers select an implementation. `GeoJsonParser.Parse(..., FilletMode mode)` resolves to the factory; tests parameterize over `FilletMode` and call `FilletGeneratorFactory.Create(mode).Apply(layout)`; LayoutInspector accepts `--fillet=legacy|v2|none` and dispatches the same way.

### `LegacyFilletArcGenerator`

```csharp
public sealed class LegacyFilletArcGenerator : IFilletArcGenerator
{
    public string Name => "legacy";
    public FilletMode Mode => FilletMode.Legacy;

    public FilletStatistics Apply(AirportGroundLayout layout, FilletOptions options = default)
        => FilletArcGenerator.Apply(layout);   // unchanged static class
}
```

The legacy static class stays intact behind this wrapper through the parity phase. After the switch + delete, the wrapper and the static class go together.

### `NullFilletArcGenerator`

```csharp
public sealed class NullFilletArcGenerator : IFilletArcGenerator
{
    public static readonly NullFilletArcGenerator Instance = new();
    public string Name => "none";
    public FilletMode Mode => FilletMode.None;
    public FilletStatistics Apply(AirportGroundLayout layout, FilletOptions options = default)
        => FilletStatistics.Empty;
}
```

Makes `FilletMode.None` a real shipping mode (raw graph, no fillet) without parser branches.

### Data flow

```
AirportGroundLayout (pre-fillet)
   │
   ▼  ArmBuilder
IReadOnlyList<Arm>                 (one per outbound edge at every eligible intersection)
   │
   ▼  JunctionClassifier
IReadOnlyList<Junction>            (classified: Simple | MultiCorner | Preserve | Skip)
   │
   ▼  FilletPlanner
FilletPlan                          (immutable: corners, arm-cuts, shared-tangent merges)
   │
   ▼  FilletPlanExecutor
AirportGroundLayout (post-fillet)
```

### `Arm`

```csharp
public sealed record Arm(
    int Id,                              // assigned by ArmBuilder
    GroundNode RootIntersection,
    GroundEdge RootEdge,                 // the original outbound edge
    string TaxiwayName,
    double BearingDeg,                   // bearing from intersection along the arm
    PolylineChain Chain,                 // ordered (lat, lon, cumulativeFt) including endpoint
    double LengthFt,
    ArmTerminus Terminus,                // OtherIntersection | ShapePointTerminus | RunwayCenterline | HoldShort | Parking | DeadEnd
    GroundNode TerminalNode,
    bool IsRunwayCenterline,
    bool EndsAtShapePointChain);

public enum ArmTerminus { OtherIntersection, ShapePointTerminus, RunwayCenterline, HoldShort, Parking, DeadEnd }
```

`ArmBuilder` replaces `WalkTaxiway` (`FilletArcGenerator.cs:2622`) but is called once per (intersection, edge) pair globally, not per-pair-per-intersection. `PolylineChain.OffsetAlongArm(distFt)` returns `(lat, lon, bearingToward)` in O(1) for tangent placement, eliminating the per-pair walk in `ComputeTangentPlacement`.

### `Junction` and classification

```csharp
public sealed record Junction(
    GroundNode IntersectionNode,
    IReadOnlyList<Arm> Arms,
    JunctionKind Kind,
    bool PreserveIntersection,
    IReadOnlyList<(int ArmIndexA, int ArmIndexB)> CornerPairs,        // turns ≥15°
    IReadOnlyList<(int ArmIndexA, int ArmIndexB)> CollinearPairs);    // turns <15°

public enum JunctionKind { Skip, Simple, MultiCorner, Preserve }
```

`JunctionClassifier` collapses the existing `IsEligibleForFilleting` (`FilletArcGenerator.cs:805`) and the per-intersection eligibility scan into one pass:

- **Skip**: shape-point node (2 same-taxiway arms), centerline-projection origin, pure runway endpoint, or `<2` arms.
- **Simple**: 2 arms, turn ≥ 15°, no runway threshold.
- **MultiCorner**: ≥3 arms; iterate `(armA, armB)` corner pairs.
- **Preserve**: at least one collinear pair OR exactly 1 RWY arm + ≥1 taxiway arm. Collinear arms produce stubs, not arcs.

### `CornerPlan`

```csharp
public sealed record CornerPlan(
    int JunctionId,
    int ArmIndexA,
    int ArmIndexB,
    double TurnAngleDeg,                 // measured at the tangent points
    double RadiusFt,
    double TangentDistAFt,
    double TangentDistBFt,
    LatLon TangentPosA,
    LatLon TangentPosB,
    double BearingAToCenterDeg,
    double BearingBToCenterDeg,
    BezierParams Bezier);                // P1/P2 derived from bearings + radius
```

Each corner is computed once. The corner key is `(JunctionId, min(armA.Id, armB.Id), max(armA.Id, armB.Id))`, so the duplicate-corner pass is unnecessary.

### Shared tangents between adjacent intersections

After `FilletPlanner` builds all junctions and corners, a final pass walks every arm `A` whose `Terminus == OtherIntersection`. The arm is shared between two junctions `J1` and `J2`. Each junction proposes a tangent distance along the arm. If `tangentDistJ1 + tangentDistJ2 > armLengthFt`:

- Both junctions' tangent distances are scaled down proportionally so their sum equals `armLengthFt - 1ft` (small gap to prevent zero-length edges, which already burned us in `c3a8d334`).
- If the resulting radii fall below `5ft`, both corners are demoted to "no arc" (passthrough straight edge) with a logged warning.

If `tangentDistJ1 + tangentDistJ2 ≈ armLengthFt` (within 5 ft), the two tangents collapse to a single shared tangent node — recorded as a `TangentMerge` in the plan. This replaces the `MergeCoincidentNodes` global pass for the common case (adjacent fillets).

### `FilletPlan` ops

```csharp
public sealed record FilletPlan(
    IReadOnlyList<ArmCut> ArmCuts,                       // SplitArmAt(arm, distFt) → tangentNodeId
    IReadOnlyList<TangentMerge> SharedTangents,          // two cuts on the same arm collapse to one node
    IReadOnlyList<CornerPlan> Corners,                   // one arc each
    IReadOnlyList<PreserveStub> PreserveStubs,           // intersection → nearest tangent on arm
    IReadOnlyList<int> IntersectionsToRemove,            // non-preserve junctions
    IReadOnlyList<GroundEdge> EdgesToRemove);            // original edges consumed by arm cuts
```

### Executor

`FilletPlanExecutor.Apply(layout, plan)` runs in fixed order:

1. **SplitArms** — create all tangent nodes; emit straight sub-edges along each arm at the cut.
2. **MergeSharedTangents** — collapse coincident cuts to one node; rewire arm sub-edges.
3. **CreateArcs** — build `GroundArc` for each `CornerPlan` from stored bearings (`FilletGeometry.BuildBezier`).
4. **AddPreserveStubs** — for preserved junctions, add `intersection → nearest-tangent-on-arm` stubs.
5. **RemoveLegacy** — delete `EdgesToRemove` and `IntersectionsToRemove`.
6. **Normalize** — `RecomputeDistances` (already exists at `FilletArcGenerator.cs:1978`); `RebuildAdjacencyLists` once at the end.

No step reads the layout's adjacency lists. Sub-edges are added to `layout.Edges` only; tangent node IDs are looked up from the plan. Each op is independent.

## What disappears

| Legacy code | V2 status |
|---|---|
| `RebuildAdjacencyLists` inside per-intersection loop (`FilletArcGenerator.cs:95`) | Removed. Planner doesn't read adjacency from its own output. |
| `RemoveDuplicateCornerArcs` (`FilletArcGenerator.cs:239`) | Removed. Corner key prevents duplicates. |
| `RemoveParallelBypassEdges` (`FilletArcGenerator.cs:385`) | Removed. One tangent per arm = no parallel chains. |
| `AddDirectShortensFromArcAnchors` (`FilletArcGenerator.cs:596`) | Removed. No tangent chains to bypass. |
| `RescueOrphanedTangentNodes` (`FilletArcGenerator.cs:2087`) | Replaced with build-time validation; planner failure = logged warning + skip the corner. |
| `RemoveRedundantPreserveEdges` (`FilletArcGenerator.cs:1999`) | Removed. Preserve stub policy goes to nearest tangent only. |
| `MergeCoincidentNodes` global pass (`FilletArcGenerator.cs:1828`) | Reduced to `SharedTangents` resolution in `FilletPlanner`. Bezier P1/P2 rebuild via `FilletGeometry.BuildBezier`, never `+=` translation. |
| `FilletEdgeKind` enum (9 variants) | Reduced to a `V2FilletKind` enum: `ArmCut`, `Preserve`, `Arc`. The plan describes the structure; tags on edges are diagnostic only. |
| `LandsInManualArc` / `SplitEdge` / `SourceIntersectionPosition` carried via `TangentPlacement` | Replaced with `ArmTerminus.ShapePointTerminus` and `ArmCut.OnShapePointChain` — explicit in the plan. |

## What is reused verbatim

- `CubicBezier` (`src/Yaat.Sim/Data/Airport/CubicBezier.cs`) — value type for evaluation, tangent bearing, min radius, arc length.
- `GeoMath.ProjectPointRaw`, `BearingTo`, `DistanceNm`, `AbsBearingDifference`.
- `GroundNode`, `GroundEdge`, `GroundArc`, `AirportGroundLayout` — no changes to the public contract.
- `RecomputeDistances` from `FilletArcGenerator.cs:1978` — extracted to `FilletGraphNormalizer`, called by both generators.
- Constants `MinFilletAngleDeg = 15.0`, `CollinearThresholdDeg = 15.0`, radius caps (50/75/100/150 ft), `MaxTangentDistFt = 150.0`, `CoincidentNodeThresholdFt = 5.0` — extracted to `FilletConstants` and consumed by both.
- Eligibility rules: shape-point detection, centerline-projection exclusion, runway-threshold preserve — same semantics, different home (`JunctionClassifier`).

## Critical files

| Action | File |
|---|---|
| New | `src/Yaat.Sim/Data/Airport/Fillet/IFilletArcGenerator.cs` |
| New | `src/Yaat.Sim/Data/Airport/Fillet/FilletMode.cs`, `FilletStatistics.cs`, `FilletGeneratorFactory.cs`, `FilletConstants.cs`, `FilletGeometry.cs`, `NullFilletArcGenerator.cs` |
| New | `src/Yaat.Sim/Data/Airport/Fillet/Legacy/LegacyFilletArcGenerator.cs` (`IFilletArcGenerator` wrapper) |
| New | `src/Yaat.Sim/Data/Airport/Fillet/V2/*.cs` (Arm, ArmBuilder, Junction, JunctionClassifier, CornerPlan, FilletPlan, FilletPlanner, FilletPlanExecutor, FilletArcGeneratorV2) |
| Modify | `src/Yaat.Sim/Data/Airport/GeoJsonParser.cs` (`FilletMode mode` parameter, threaded through `Parse` overloads at line 35/38/90/166; resolved via `FilletGeneratorFactory.Create`) |
| Modify | `tests/Yaat.Sim.Tests/FilletArcGeneratorTests.cs` (parameterize tests by `IFilletArcGenerator`; tests run against every registered generator) |
| New | `tests/Yaat.Sim.Tests/Fillet/FilletParityTests.cs` (cross-implementation parity harness) |
| Modify | `tools/Yaat.LayoutInspector/Program.cs` (`--fillet=legacy|v2|none`, `--fillet-diff`) |
| Modify | `docs/ground-layout-generation.md` (V2 section, comparison workflow) |
| Modify | `docs/architecture.md` (entry for `Fillet/` subfolder) |
| Delete after parity | `src/Yaat.Sim/Data/Airport/FilletArcGenerator.cs`, `FilletProvenance.cs`, `Fillet/Legacy/`. Project rule: no shims. `IFilletArcGenerator` itself remains. |

## Implementation order

Each step is a separate commit. Each commit ends with `dotnet build -p:TreatWarningsAsErrors=true` + `pwsh tools/test-all.ps1` green.

1. **interface-and-factory** — Introduce `IFilletArcGenerator`, `FilletMode`, `FilletStatistics` (move from legacy), `FilletOptions`, `FilletGeneratorFactory`, `NullFilletArcGenerator`, and `LegacyFilletArcGenerator` wrapping the existing static class. Thread `FilletMode` through `GeoJsonParser.Parse` overloads. No behavior change.
2. **extract-constants-and-geometry** — Pull `FilletConstants` + `FilletGeometry` (turn angle, bezier rebuild, kappa) out of `FilletArcGenerator`. Legacy calls into them. No behavior change. Confirms the math is portable.
3. **arm-builder** — `Arm`, `PolylineChain`, `ArmBuilder`. Unit tests against synthetic chains (shape-point stop, runway-centerline stop, dead end, cycle guard).
4. **junction-classifier** — Replaces `IsEligibleForFilleting`. Tests match the legacy eligibility table.
5. **planner** — `CornerPlan`, `FilletPlanner`. Pure function; tests assert plan shape on the same synthetic graphs used by `FilletArcGeneratorTests`.
6. **executor** — `FilletPlanExecutor` + `FilletArcGeneratorV2` (implements `IFilletArcGenerator`). Synthetic-graph tests pass.
7. **parity-harness** — `FilletParityTests` pulls generators from `AllGenerators()` MemberData (Legacy + V2), runs OAK, SFO, FLL, ZOA, KORD through both, asserts metrics:
   - Node count by `GroundNodeType` — exact.
   - Arc count — within ±5% (intentional dedup may differ).
   - Per-corner radius (keyed by `(intersectionId, taxiway-pair-sorted, bearings-rounded-5°)`) — within ±10%.
   - BFS connectivity from each parking node to each runway hold-short — must match.
   - Per-runway centerline bearing — within 1° (legacy's own runway-displacement warning).
8. **layout-inspector** — `--fillet=legacy|v2|none` (resolves through `FilletGeneratorFactory`); `--fillet-diff <airport>` dumps per-intersection mismatch JSON to `.tmp/`.
9. **switch-and-delete** — Flip `GeoJsonParser` default to `FilletMode.V2`. Aviation-realism review on radius table + preserve semantics. In the same commit, delete `FilletArcGenerator.cs`, `LegacyFilletArcGenerator.cs`, `FilletProvenance.cs`, the `Legacy` member of `FilletMode`, and the now-unused enum variants. `IFilletArcGenerator` survives so future variants can plug in.

## Comparison harness — concrete

Because both implementations satisfy `IFilletArcGenerator`, the harness is implementation-agnostic. New tests pivot over generator instances rather than mode enums:

```csharp
public static IEnumerable<object[]> AllGenerators() =>
    from gen in new IFilletArcGenerator[]
    {
        new LegacyFilletArcGenerator(),
        new FilletArcGeneratorV2(),
    }
    from airport in new[] { "OAK", "SFO", "FLL", "ZOA_KORD" }
    select new object[] { gen, airport };

[Theory]
[MemberData(nameof(AllGenerators))]
public void Fillet_AllGenerators_NoMissingNodeRefs(IFilletArcGenerator gen, string airport)
{
    var layout = TestAirport.BuildRaw(airport);                     // FilletMode.None
    var stats = gen.Apply(layout);
    AssertNoMissingNodeRefs(layout);
    AssertNoSelfLoops(layout);
    AssertNoZeroLengthEdges(layout);
}

[Theory]
[InlineData("OAK")]
[InlineData("SFO")]
[InlineData("FLL")]
[InlineData("ZOA_KORD")]
public void FilletParity_NodeCounts(string airport)
{
    var legacyLayout = TestAirport.Build(airport, FilletMode.Legacy);
    var v2Layout = TestAirport.Build(airport, FilletMode.V2);

    foreach (var nodeType in Enum.GetValues<GroundNodeType>())
    {
        int legacyCount = legacyLayout.Nodes.Values.Count(n => n.Type == nodeType);
        int v2Count = v2Layout.Nodes.Values.Count(n => n.Type == nodeType);
        Assert.Equal(legacyCount, v2Count);
    }
}

[Theory]
[InlineData("OAK")]
public void FilletParity_V2ReportsZeroOnImpossibleCleanups(string airport)
{
    var layout = TestAirport.BuildRaw(airport);
    var stats = new FilletArcGeneratorV2().Apply(layout);

    // V2 is structurally incapable of producing the legacy cleanups' inputs.
    Assert.Equal(0, stats.OrphansRescued);
    Assert.Equal(0, stats.RedundantPreserveEdgesRemoved);
    Assert.Equal(0, stats.DuplicateCornerArcsRemoved);
    Assert.Equal(0, stats.ParallelBypassEdgesRemoved);
    Assert.Equal(0, stats.DirectShortensAdded);
}
```

Generators dropped into `AllGenerators()` automatically pick up structural tests (no-orphans, no-self-loops, no-zero-length-edges). The parity tests pair Legacy and V2 specifically; a hypothetical V3 would add a second parity slot.

Additional tests: `FilletParity_ArcCount_Within5Percent`, `FilletParity_CornerRadiusWithin10Percent`, `FilletParity_ParkingToHoldShortConnectivity`, `FilletParity_RunwayCenterlineBearings`.

Every mismatch surfaced by the parity harness is triaged into one of:
- **Accepted improvement** — V2 produces strictly better geometry (e.g. one corner arc where legacy had three duplicates). Document in `docs/plans/filletv2/v2-divergences.md`.
- **V2 bug** — fix in V2 planner/executor.
- **Legacy-only quirk** — current behavior we don't want to preserve (e.g. `phase-d-passthrough` parallel bypasses). Document but don't replicate.

## Verification

End-to-end checklist run before flipping the default:

1. `dotnet build -p:TreatWarningsAsErrors=true 2>&1 | tee .tmp/build.log` — zero warnings.
2. `pwsh tools/test-all.ps1 2>&1 | tee .tmp/test-all.log` — both repos pass; legacy and V2 tests both run.
3. `dotnet run --project tools/Yaat.LayoutInspector -- ZOA OAK.geojson --fillet=v2 --validate 2>&1 | tee .tmp/oak-v2-validate.log` — zero `arc-tangent-misaligned`, zero `disconnected-subgraph`, zero `degenerate-arc` warnings.
4. Same for `SFO`, `FLL`, `KORD`.
5. `dotnet run --project tools/Yaat.LayoutInspector -- ZOA OAK.geojson --fillet-diff > .tmp/oak-diff.json` — human review of every accepted divergence.
6. Replay-archive smoke: run a known-good OAK ground-routing recording (`tests/Yaat.Sim.Tests/TestData/issue-*.zip`) under V2 and confirm aircraft reach destinations without spin / U-turn / orphan-routing failures.
7. Aviation-realism review on radius table and preserve semantics (one-shot, since constants are reused).

## Risks and assumptions

- **One tangent per arm is acceptable.** The legacy per-pair design's defense (`FilletArcGenerator.cs:1008-1013`) was that a "near-collinear pair's large tangent distance corrupts other pairs' arcs via max-wins." Near-collinear pairs (turn < 15°) hit the `Preserve` path in V2 and produce no arc, so the cap-collision they worry about cannot happen. Verify with synthetic 4-way + one near-collinear pair test in step 4.
- **Shared-tangent proportional split is acceptable.** Two adjacent intersections each wanting 100 ft on an 80 ft arm get 40 ft each. Radii halve. If both drop below the 5 ft floor, both corners go straight (no arc). The legacy `intersectionCap` logic (`FilletArcGenerator.cs:1058`) does this implicitly; V2 makes it explicit.
- **Per-arm tangent must still respect runway protection.** Arms with `IsRunwayCenterline = true` cannot have their start segment shortened — the cut creates an `ArmCut.OnRunway = true` op that splits the centerline edge rather than consuming it (same semantics as legacy's `LandsInManualArc` runway carve-out at `FilletArcGenerator.cs:2352`).
- **Adjacency rebuild cost.** Removing in-loop `RebuildAdjacencyLists` (called per intersection in legacy) means V2 rebuilds once at the end. For OAK that's a 5-10ms one-shot vs N×O(E) per-intersection — a net wins. Confirm via `FilletParity_Performance` benchmark.
- **Legacy is deleted, not deprecated.** Project rule: no shims, no migration paths.

## Open clarifications (to confirm before implementation)

1. Should `FilletMode.None` be a real shipping mode (raw graph, no fillet) for debugging, or only `Legacy` / `V2`? Codex proposal mentions `None`; legacy already supports `applyFillets: false`. Recommend keeping it.
2. Acceptable parity-test tolerance for arc count: codex says `±0`, cursor says `document deltas`. Recommend `±5%` per airport with a hard cap of `±10%` and per-divergence review.
3. Diagnostic provenance — `FilletProvenance` records are non-serialized and only feed `Origin`/log output. Recommend dropping the abstract record + 9 enum variants entirely; V2 nodes/edges/arcs get a single `ConstructionTag` string field reused for diagnostics. Removes pattern-match coupling between cleanups (which V2 doesn't have anyway).

---
