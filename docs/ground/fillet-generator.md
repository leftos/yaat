# Fillet Arc Generator — Design & Architecture

> Read this before touching `src/Yaat.Sim/Data/Airport/FilletArcGenerator.cs`, anything under `src/Yaat.Sim/Data/Airport/Fillet/`, or the shared `Fillet/FilletGeometry.cs` / `Fillet/FilletConstants.cs`. The fillet generator turns the raw straight-segment ground graph into one with smooth corner arcs and order-independent junction connectivity.
>
> It is layer 1 of the three-layer ground stack — see the [pathfinder](./pathfinder.md) that walks the graph it builds and the [navigator](./navigator.md) that physically follows the arcs it emits. Index: `./README.md`.

## The fillet generator

The fillet generator is a single implementation (`FilletArcGenerator`, namespace `Yaat.Sim.Data.Airport`, with its helpers in the `Yaat.Sim.Data.Airport.Fillet` namespace under `src/Yaat.Sim/Data/Airport/Fillet/`). The Legacy pair-based generator, its `LegacyFilletArcGenerator` adapter, and the `FilletArcGeneratorRouter` selector were deleted when it became the only fillet generator.

`FilletMode` has two values: `None` (raw graph, no-op pass) and `Standard` (the production generator). The other two ground-stack layers — [pathfinder](./pathfinder.md) and [navigator](./navigator.md) — are likewise single-implementation.

**The single most important principle:** *the generated graph is correct-but-different, not broken.* It collapses each junction into fewer tangent nodes with larger per-corner bearing steps; it retains membership-matched junction arcs (`C1 - B`); it faithfully preserves source-data quirks (coincident edges, taxiways that connect only via a third connector). When a downstream consumer trips on the geometry, **adapt the consumer — do not "fix" the graph.**

---

## What fillets are & where they sit

### Input and output

The generator consumes the **base graph** built by `TaxiwayGraphBuilder` (via `GeoJsonParser`): straight `GroundEdge`s on taxiway and runway-centerline LineStrings, joined at `GroundNode` intersections, plus parking/helipad/hold-short nodes. The base graph has sharp corners — an aircraft following it node-to-node would pivot in place at each intersection.

The generator produces two things, mutating the layout in place:

1. **Corner arcs** — a `GroundArc` (cubic Bezier, played downstream as its actual curve) between two **tangent-cut** nodes on the two arms of a turn, replacing the sharp vertex with a smooth curve. A too-tight corner degrades to a straight **chord** (`GroundEdge`) instead.
2. **An order-independent edge-split** — each original edge is split once at the cuts that land on it; the stub incident to a removed junction is dropped; every other sub-segment survives and reconnects. This is what keeps the graph connected after the junction node is deleted.

`FilletStatistics` (returned by `Apply`) tallies filleted nodes, arcs, collinear merges, coincident merges, and carries the plan's `Warnings` and a `FilletReport` (`Report`; see [The fillet report](#the-fillet-report)). Nothing in the layout build reads the tallies; `GeoJsonParser.ParseWithDiagnostics` returns the report as its third item.

### Selection & invocation

`GeoJsonParser.Parse(airportId, geoJson, runwayAirportCode, inputs, FilletMode)` is the entry point (`src/Yaat.Sim/Data/Airport/GeoJsonParser.cs`; `inputs` is the airport's `LayoutInputs`: stand lead-ins, spot-edge chains and the tower-cab videomap). After it builds nodes/edges (including the sidecar spot-edge chains, Step 6c) and rebuilds adjacency (Step 7), Step 8 runs the fillet pass **only when `filletMode != None`**; Step 9 then marks each runway bar as its runway's hold line or a short fallback bar, once every node is in its final place:

```csharp
FilletGeneratorFactory.Create(filletMode).Apply(layout, inputs.CornerFillets);
```

| `FilletMode` | Factory result | Behavior |
|---|---|---|
| `None` | `NullFilletArcGenerator.Instance` | no-op; raw intersection graph (used by `FilletMode.None` tests + comparison baselines) |
| `Standard` | `new FilletArcGenerator()` | plan-then-execute (this doc) |

`FilletMode.cs`, `FilletGeneratorFactory.cs`, and the `IFilletArcGenerator` interface (`Id`, `DisplayName`, `Apply`) form the selection surface. `Apply(layout, overrides)` takes the airport's per-corner overrides (`CornerFilletOverrides`, from the sidecar `cornerFillets`, `CornerFilletOverrides.None` for none); the generator applies each one that matches a corner (suppress, radius, force and pinned `tangentFt`; see [Per-corner overrides](#per-corner-overrides)). The convenience overloads `Parse(..., inputs)` / `Parse(..., inputs, applyFillets: bool)` map to `Standard` / `None` — **`Standard` is the parser's default**, and `ParseWithDiagnostics` / `ParseMultiple` parse with it too. `FilletArcGeneratorRegistry.All` lists both implementations (`None`, `Standard`) for enumeration harnesses.

The downstream consumers of the filleted graph: the **[pathfinder](./pathfinder.md)** walks it to build `TaxiRoute`s, and the **[navigator](./navigator.md)** compiles each `GroundArc` into a `PathPrimitiveBezier` and follows its cubic Bézier closed-form.

---

## Key design decisions & requirements

### Why this design

The deleted Legacy generator was a per-pair, order-dependent pipeline: for each intersection it fillets every edge pair, places tangent nodes, then runs a cascade of repair passes (`AddDirectShortensFromArcAnchors`, `RescueOrphanedTangentNodes`, parallel-bypass removal, reconnect, duplicate-arc removal).

Those passes mutate-then-repair: they could create an edge in one junction's pass and strip it in another's, and they emitted **zero-distance and reverse-traversed edges** that the pathfinder tripped on (orbit/spin bugs).

The chain-planner that tried to make the connectivity order-independent (`FilletArmChainPlanner`, `FilletConnectivityPlanner`) was itself fragile and was deleted. The current generator is a clean-room design around two ideas: **plan everything before mutating anything**, and **connectivity is one global edge-split, not a stack of repair heuristics**.

### Plan-then-execute, order-independent

`Apply` builds a pure, immutable `FilletPlan` describing every cut, arc, chord, straight-connector, surviving edge, and node-removal — *then* hands it to `FilletPlanExecutor`, which materializes it in one forward pass with no per-junction mutation loop.

Because the plan is computed against the **pre-fillet** layout and the executor never reads back its own partial output, the result is independent of junction processing order. This is the structural property that killed the create-then-strip bug class.

### Global edge-split connectivity & the no-true-disconnection gate

`FilletEdgeSplitPlanner.Plan` (`Fillet/FilletEdgeSplitPlanner.cs`) splits each **original** edge exactly once by the cuts that land on it, drops **only** the stub incident to a removed junction, and keeps every other sub-segment. A removed junction never appears as a surviving endpoint (its endpoints are replaced by cut nodes before any mutation).

The correctness bar is the **no-true-disconnection** gate: structural validity + repair-counter fields all zero + parking→hold-short reachability preserved + **no pre-fillet node left reachable in only one of the pre/post-fillet layouts**.

### Runway-bearing parity

`CompareRunwayBearings` reports 0 mismatches vs Legacy. This falls out of two design choices rather than a dedicated pass: the edge-split preserves each source edge's `(Nodes[0]→Nodes[1])` orientation on every sub-segment (`FilletEdgeSplitPlanner` carries `IsRunwayCenterline` through `MakeEdge`), and `BuildBezier` projects control points **toward** the junction (`FilletGeometry.cs`).

Earlier the bezier projected *away* along the arm, producing S-cusps / near-zero-radius arcs and a reversed `RWY10L/28R` segment; the toward-junction projection fixed both.

### Corner-radius policy and the radius floor

The requested radius per corner comes from `FilletGeometry.SelectMaxRadius` (`FilletGeometry.cs`), keyed off edge type and turn angle:

| Corner involves | Turn angle | Requested radius |
|---|---|---|
| a RAMP edge | any | `RampRadiusFt = 50 ft` |
| a runway centerline | ≤ 45° (high-speed exit) | `HighSpeedExitRadiusFt = 600 ft` |
| a runway centerline | > 45° | `RunwayExitRadiusFt = 100 ft` |
| neither (taxiway×taxiway) | any | `DefaultRadiusFt = 75 ft` |

The arc the executor actually builds is sized to `min(requested, EffectiveMinRadiusFt(tangent geometry))` (`FilletPlanExecutor.cs`) so the stored `MinRadiusOfCurvatureFt` — the value the navigator reads for turn-speed back-propagation — is honest, not an over-bulged requested-radius bezier.

The **radius floor** is `FilletConstants.RadiusFloorFt = 5 ft`: a corner whose effective radius drops below the floor is rejected at plan time (`ArmCutResolver.cs`) or, if it slips through to execution, degrades to a chord (below). Corner radii are deliberately capped where two intersections sit close together: cut placement bounds tangent distance to avoid overrunning the adjacent intersection, so a tight junction gets a tighter arc rather than one that overshoots.

### Manual-arc detection

Before classifying junctions, `ManualArcDetector.Detect` (`Fillet/ManualArcDetector.cs`) excludes **shape-point nodes**: a `TaxiwayIntersection` with exactly two edges of the **same** taxiway name. These are vertices on a pre-existing curve drawn in the source GeoJSON, not real turn junctions — filleting them would corrupt the hand-drawn arc. Excluded node IDs are skipped in `Apply`'s main loop and threaded into the arm-walk so a walk crosses them transparently.

### Tangent cuts and stable-anchor redirects

A tangent cut is a new node placed a computed distance **along an arm, away from the junction**, where the arc tangents off.

When a cut lands within `CoincidentNodeThresholdFt = 5 ft` of a **pre-existing stable node** — a `TaxiwayIntersection`, `Spot`, `Parking`, `Helipad`, or `RunwayHoldShort` that is not a runway-centerline projection (`FilletPlanCutRedirect.IsStableAnchorTarget`) — the plan **redirects** the cut onto that existing node instead of materializing a duplicate tangent on top of it (`ExtendWithStableAnchors`, `FilletPlanCutRedirect.cs`).

This keeps the graph from sprouting near-coincident node pairs the normalizer would just have to merge. The redirect is the source of a subtle namespace invariant — see Caveats.

> The eligible-target set originally included only `TaxiwayIntersection`, so a cut landing on a spot/parking/hold-short endpoint was **not** redirected — it materialized a duplicate node joined by a **zero-distance edge**.
>
> That no-op edge's meaningless 0° bearing survived into the materialized route, and `GroundNavigator` followed it, producing a visible taxi wiggle (SFO had 11 such edges, OAK 10+, FLL 1; the raw geojson has none). Widening `IsStableAnchorTarget` to all five stable types fixed it. Guard: `FilletCornerSpanGuardTests.EdgeSplit_NoZeroDistanceEdges` (no edge under ~1.2 ft at SFO/OAK/FLL).

### Eligibility & the runway-preserve rule

`FilletEligibility.IsEligible` (`Fillet/FilletEligibility.cs`) gates which intersections get filleted, shared with Legacy:

- runway-centerline-projection nodes: never eligible.
- a node with **one** runway edge **and** ≥1 non-runway edge: eligible but **`preserveNode = true`** — the junction node survives (it is a runway/taxiway connection that must stay a discrete vertex; e.g. a runway hold-short tie-in).
- a node with one runway edge and no other edges: not eligible.
- a two-edge same-taxiway node: not eligible (it is a shape point).

`preserveNode` junctions are classified `JunctionKind.Preserve` and never added to `nodesToRemove` — their corners still get arcs, but the original vertex stays.

**A collinear through-pair also preserves the node.** `JunctionClassifier.Classify` classifies a junction `Preserve` whenever `CornerPlanner.PlanCorners` finds a collinear pair (two arms turning less than `CollinearThresholdDeg`), whatever `FilletEligibility` said: the straight-through is kept as the original vertex with its arms, and only the junction's real corners get arcs. `FilletPlanBuilder` likewise removes a junction node only when it is neither `PreserveNode` nor holds a collinear pair.

### Runway-crossing centerline projection

`RunwayCrossingDetector.ConnectOnRunwayNodes` (parse stage, **before** fillet) links each taxiway hold-short representative to the runway centerline. When the representative sits ≥5 ft off the centerline it needs a point **on** the centerline; `ResolveCenterlineProjectionNode` either reuses a pre-existing `TaxiwayIntersection` already within `CoincidentNodeThresholdFt` (5 ft) of the projected point or, failing that, mints a `RunwayCrossing:centerline-projection` node there.

Reusing rather than minting is what keeps the projection from landing coincident with an existing intermediate node — the case the retired post-execute coincident-node merge used to clean up.

The connector itself is a `RWY…:link` edge. It is **not** a runway centerline (`GroundEdge.IsRunwayCenterline` excludes the `:link` suffix) and **not** a taxi corner: `GroundEdge.IsRunwayCrossingLink` flags it and `TaxiwayArmBuilder` skips it so fillet never builds an arm onto a runway crossing. Without that exclusion, reusing a node as a projection target would attach a `:link` arm that fillet curves onto, producing a 0 ft edge-split fragment (the artifact the post-hoc merge formerly masked).

---

## Step-by-step pipeline walkthrough — `FilletArcGenerator.Apply`

`FilletArcGenerator.Apply` (`src/Yaat.Sim/Data/Airport/FilletArcGenerator.cs`):

**1. Manual-arc detection + ID seeding.** `ManualArcDetector.Detect` collects shape-point node IDs. `maxNodeId` = the largest existing node ID. Two counters are seeded:
   - `idCounter` (new tangent-cut **node** IDs) starts at `maxNodeId + 1`.
   - `nextCutId` (plan-internal **cut** IDs, typed `CutId`) starts at **`maxNodeId + 1_000_000`** — a disjoint high range. The `CutId` newtype makes cut-vs-node confusion a compile error; the offset is defense-in-depth (see Caveats).

**2. Per-junction classification.** For each node in ID order, skip if it is a manual-arc node, ineligible, or has fewer than 2 edges. Otherwise `JunctionClassifier.Classify` (`Fillet/JunctionClassifier.cs`):
   - `TaxiwayArmBuilder.BuildArms` walks each incident edge outward (through shape points) into a `TaxiwayArm` (`Fillet/TaxiwayArm.cs`): root edge, taxiway name, bearing from the junction, available length, an `IntersectionCapFt` that bounds how far a cut may sit before overrunning the next intersection, a `TaxiwayArmTerminus` classification (OtherIntersection / ShapePoint / RunwayCenterline / HoldShort / Parking / DeadEnd), and the full `TaxiwayWalk.WalkResult`.
   - `CornerPlanner.PlanCorners` (`Fillet/CornerPlanner.cs`) pairs arms. A pair below `CollinearThresholdDeg = 15°` turn is a **collinear pair** (a straight-through, no arc); a pair below `MinFilletAngleDeg = 15°` is skipped; a **near-hairpin** pair (turn above `NearHairpinThresholdDeg`, two taxiways grazing at an acute angle) is skipped, since its fillet would collapse to an un-taxiable chord; a pair whose ideal tangent distance gives a radius under `RadiusFloorFt` is skipped; otherwise it is a `CornerSpec` carrying turn angle, requested radius, ideal tangent distance, and arm bearings. The junction's `JunctionKind` is `Skip` / `Simple` / `MultiCorner` / `Preserve`.

**3. Per-junction arm-cut resolution.** `ArmCutResolver.Resolve(junction, ref nextCutId)` (`Fillet/ArmCutResolver.cs`) decides where each arm is cut:
   - For each arm, a **candidate distance** is derived from the ideal tangent distances of its corners (averaged when they cluster within the coincident threshold, else `min(max, IntersectionCapFt)`), capped at `MaxTangentDistFt = 150 ft`.
   - A corner is flagged **distorted** when its effective radius is below the floor, more than `DistortionThreshold = 2×` the requested radius, or its two tangent distances are more than `AsymmetryThreshold = 2×` apart.

     Distorted arms take an **ordered multi-cut** path: positions are coalesced (`IdealCoalesceThresholdFt = 2 ft`) and gap-enforced (`MinArmSegmentGapFt = 5 ft`, demoting cuts that crowd). Non-distorted arms take a single cut; a sub-threshold cut is clamped to `5 ft + 1` with a `SubThresholdCutSkipped` warning.
   - Each cut becomes a `ResolvedArmCut` (id, junction, arm, distance, position, bearing-toward-junction, owning corner IDs). Corners map to `(cutA, cutB)` → a `CornerArcOp`; corners not arc-able fall to `StraightConnectorOp`.

     `SharedArmTangentPass.ApplyIntraArmCoalesce` adds `TangentMergeOp`s for cuts on one arm landing within 5 ft of each other; `ApplyCrossArmCoalesce` adds them for cuts on **different arms of the same junction** within 5 ft (e.g. an `A` tangent and a collinear `A8`/`RAMP` tangent) — these used to be merged only by the post-execute normalizer, which manufactured duplicate corner arcs.

**4. Plan building.** `FilletPlanBuilder.Build` (`Fillet/FilletPlanBuilder.cs`) aggregates all junction results, then:
   - `SharedArmTangentPass.ApplyCrossJunction` reconciles cut sets on a physical arm **shared between two adjacent junctions** so their cuts don't collide in the middle.

     Two pairing sources: arms that *terminate* at the neighbor junction (a taxiway that ends there), and arms whose walks *pass through* the neighbor on a shared runway-centerline chain (paired via cuts landing on the same original edge — a runway crossing many taxiway junctions; only the widened high-speed-exit cuts reach far enough to collide there).

     Per pair: opposing cut sets that would leave a straight shorter than `MinSharedArmClearGapFt` (= the navigator's 50 ft pure-pursuit look-ahead cap — anything shorter is an orbit-trap sliver) are scaled down to restore the clearance; sets already overlapping or near-abutting are scaled to abut and their far cuts merged into one shared tangent node (arc-to-arc, no straight at all). Guard: `FilletCornerSpanGuardTests.RunwayCenterline_NoSliverStraightsBetweenDifferentJunctionsCuts`.
   - `SharedArmTangentPass.ApplyGlobalCoincidentCutCoalesce` then merges **any** two cuts across the whole plan whose (post-scaling) positions land within 5 ft — the plan-time equivalent of the node-coincidence test the post-execute normalizer used to run, moved earlier and applied to cuts.

     It catches cross-junction coincidences `ApplyCrossJunction`'s farthest-pair merge misses (adjacent junctions' tangent cuts on a shared taxiway 1–4 ft apart); the union-find survivor map absorbs the overlap with the intra-arm/cross-arm/cross-junction merges.
   - `FilletPlanCutRedirect.BuildSurvivorMap` builds a union-find survivor map over the tangent merges; `ExtendWithStableAnchors` redirects coincident cuts onto pre-fillet stable nodes and returns the **authoritative set of anchor node IDs used**. `PruneCuts` keeps only surviving cut IDs; `RedirectCornerArcs` / `RedirectStraightConnectors` rewrite op endpoints through the survivor map, dropping self-pairs.

     The redirected ops are then **deduped by resolved endpoint pair** (keeping one op per node pair, preferring the single-name corner — requirement ①), so the cross-arm coalesce above yields exactly one arc per pair instead of a single-name + membership twin.
   - `FilletEdgeSplitPlanner.Plan` computes consumed + surviving edges (above).
   - The result is a `FilletPlan` (`Fillet/FilletPlan.cs`) with `Cuts`, `TangentMerges`, `CornerArcs`, `StraightConnectors`, `SurvivingEdges`, `JunctionNodesToRemove`, `EdgesToRemove`, `Warnings`, and `StableAnchoredEndpointIds`. `FilletPlanConsistency.ValidateCutReferences` / `ValidateNodeReferences` throw if any op references an unknown cut or a to-be-removed node — a fail-fast guard, not silent repair.

**5. Execution.** `FilletPlanExecutor.Execute` (`Fillet/FilletPlanExecutor.cs`):
   - `MaterializeCutNodes` creates one `GroundNode` per surviving cut (origin `tangent-cut@J.../<taxiway>`), recording each cut's `SourceIntersectionPosition`.
   - Remove `EdgesToRemove` **before** adding survivors (so a survivor identical to a consumed edge isn't deduped then orphaned), then emit each `SurvivingEdgeOp` as a `GroundEdge` (origin `edge-split/<taxiway>` or `edge-split-redirect/<taxiway>`).
   - For each `CornerArcOp`: resolve both tangent endpoints, size the arc to `min(corner.RequestedRadiusFt, EffectiveMinRadiusFt)`, build the bezier via `FilletGeometry.BuildBezier`. **If `bez.MinRadiusFt < RadiusFloorFt`, emit a chord** (`GroundEdge`, origin `corner-chord@J.../<taxiway>`) instead of a degenerate arc; otherwise add a `GroundArc` (origin `corner@J.../<taxiwayA>/<taxiwayB>`), single-named when both edges share a taxiway, two-named otherwise.
   - Emit `StraightConnectorOp`s (origin `straight-connector@J.../<taxiway>`), then delete every node in `JunctionNodesToRemove` and its incident edges/arcs.

**6. Normalization.** `FilletGraphNormalizer.Normalize` (`Fillet/FilletGraphNormalizer.cs`) recomputes edge/arc distances and arc radii from final node positions, rebuilds adjacency, drops self-loops and sub-floor arcs, and removes isolated intersection nodes.

There are deliberately **no repair passes** and **no coincident-node merge** — the plan guarantees no coincident tangent cuts (intra-arm + cross-arm + global cut-coalesce, steps 3–4), and the runway-crossing projector reuses a pre-existing coincident node rather than minting one (see [Runway-crossing centerline projection](#runway-crossing-centerline-projection) below).

Guarded WITHOUT any post-hoc merge by `FilletCornerSpanGuardTests.NoCoincidentIntersectionNodes` + `CornerArcs_NoDuplicateNodePairs` + `EdgeSplit_NoZeroDistanceEdges`.

### `FilletGeometry` — the bezier (`Fillet/FilletGeometry.cs`)

`BuildBezier(tanA, tanB, bearingAToJunction, bearingBToJunction, requestedRadius)` (`FilletGeometry.cs`):
- Reverses each arm's toward-junction bearing to a from-tangent bearing, computes the effective turn `180 - |Δbearing|`, the sweep, and `kappa = (4/3)·tan(sweep/4)` (the standard cubic-bezier circular-arc approximation).
- Projects control points P1/P2 from each tangent **along the from-tangent bearing into the corner** at depth `kappa·radius`. P0 = `tanA`, P3 = `tanB`.
- Returns `MinRadiusFt` (sampled `MinRadiusOfCurvatureFt`), `ArcLengthNm`, the effective turn, and the two from-tangent bearings — all stored on the `GroundArc`. The navigator plays this Bézier directly by arc-length (it does **not** re-fit a circle); `MinRadiusOfCurvatureFt` feeds its turn-speed cap and back-propagated braking (see [`./navigator.md`](./navigator.md) Invariant I2 and `MaxSafeSpeedKts`).

`EffectiveMinRadiusFt` (`FilletGeometry.cs`) is the conservative plan-time radius estimate, `min(tangentA, tangentB) / tan(turn/2)` — asymmetric cubics built from endpoint positions alone under-estimate radius when the two tangents differ, so this is the value gated against the floor.

---

## Caveats & gotchas

Verify each against current code before relying on it.

### Cut-ID / node-ID type distinction (and the offset invariant behind it)

Planning **cut IDs** and graph **node IDs** are **type-distinct**, so confusing them is a compile error. `CutId` (a `readonly record struct`, `Fillet/CutId.cs`) keys the cut dictionaries (`Cuts`, `prunedCuts`, `cutNode`); a redirected endpoint is a `FilletEndpoint` — a sealed `Cut(CutId) | Node(int)` union (`Fillet/FilletEndpoint.cs`). The executor resolves by matching the union, not by a bare-int lookup (`FilletPlanExecutor.cs`):

```csharp
GroundNode? ResolveEndpoint(FilletEndpoint ep) => ep switch
{
    FilletEndpoint.Cut cut   => cutNode[cut.Id],          // resolved cut → materialized tangent node
    FilletEndpoint.Node node => layout.Nodes[node.NodeId], // stable anchor → pre-existing node
    _ => throw new InvalidOperationException(...),
};
```

A node `int` can no longer be passed where a `CutId` is expected. **As defense-in-depth** (and because `GroundNode.Id` is still `int`), cut IDs are also seeded at `maxNodeId + 1_000_000` (`FilletArcGenerator.cs`) so the two ranges stay numerically disjoint.

**The cautionary story:** cut IDs used to start at 1, sharing the `int` namespace with graph node IDs (start at 0), and the redirect map / resolver passed both as bare `int`. When `ExtendWithStableAnchors` redirected a tangent cut onto a pre-existing intersection node, the resolver looked the substituted node ID up in the cut-node map **first** — and if a cut ID from a **different junction** happened to equal that node ID, it returned the wrong tangent point.

The bezier then degenerated to a chord spanning the two far-apart points: SFO had **52 corner-chord edges over 300 ft, the longest ~9533 ft** (effectively airport-spanning garbage edges).

The fix was the disjoint range (cut IDs start at `maxNodeId + 1_000_000`) + having `ExtendWithStableAnchors` return the authoritative anchor-ID set (`StableAnchoredEndpointIds`), hardened by making the distinction type-level (`CutId` newtype). The guard is `tests/Yaat.Sim.Tests/Fillet/FilletCornerSpanGuardTests.cs` — **no `corner` arc or chord spans more than 300 ft at SFO, OAK, or FLL**. If you touch cut-ID seeding, the redirect, or endpoint resolution, this test is your tripwire.

### Corner arcs vs corner-chords

A `corner@...` entry is a real `GroundArc` (smooth Bezier). A `corner-chord@...` entry is a straight `GroundEdge` — the **degenerate fallback** emitted when `bez.MinRadiusFt < FilletConstants.RadiusFloorFt` (`FilletPlanExecutor.cs`).

The chord keeps the two tangent cuts connected rather than relying on a sub-floor arc the normalizer would delete, so it is load-bearing for connectivity — do not remove it. A chord at a corner means that corner is too tight to round; the aircraft takes it as a sharp vertex.

### Source-data quirks are preserved, not "fixed"

The generator mirrors the source graph. Two recurring shapes look like bugs but are not:

- **Coincident / duplicate source edges** — e.g. FLL `C` and `C1` are drawn as near-coincident LineStrings in the GeoJSON. The generator keeps both; it does not dedupe them. This is source data.
- **Taxiways that connect only through a third connector** — e.g. SFO `A` and `B1` have **no direct edge** in the source; they connect only via `Q`. The old Legacy repair passes could synthesize a bridging junction node to make `A` and `B1` look directly adjacent; **the generator does not invent that adjacency**. A consumer that assumes `A`→`B1` is one hop must instead route `A`→`Q`→`B1`.

The principle: **mirror the source; adapt consumers, don't invent adjacency.** This is why the pathfinder gained a mandatory-connector insertion path (see [`./pathfinder.md`](./pathfinder.md)) rather than the graph being patched.

### Membership junction arcs ("X - Y") and `MatchesTaxiway`

A corner arc between two **differently-named** taxiways carries both names (`TaxiwayNames` length 2; e.g. `["C1","B"]`) and renders its display name with a `" - "` separator — `C1 - B` (`AirportGroundLayout.cs`; the `" - "` separator avoids colliding with `/` in runway IDs like `RWY30/12`). `GroundArc.MatchesTaxiway(name)` returns true if the queried name is **any** of the arc's names (`AirportGroundLayout.cs`).

Downstream, a bare-taxiway walk for `B` therefore *sees* a `C1 - B` arc as a valid `B` step — the arc is a legitimate turn-connector, not noise. This membership semantics is what makes the pathfinder's "single-name continuation beats membership-only arc" rule necessary; see [`./pathfinder.md`](./pathfinder.md) "Membership junction arcs". Do not strip the second name to make membership stricter — the arc genuinely belongs to both taxiways.

### Edge / arc / node origin strings (debugging)

Every generated element stamps an `Origin` string, surfaced in LayoutInspector tooltips (`--html`) and the corner-span guard test. Grep these when diagnosing geometry:

| Origin prefix | Element | Emitted by |
|---|---|---|
| `tangent-cut@J<id>/<taxiway>` | tangent-cut node | `FilletPlanExecutor.cs` |
| `edge-split/<taxiway>` | surviving sub-segment | `FilletEdgeSplitPlanner.cs` |
| `edge-split-redirect/<taxiway>` | cutless-arm redirect segment | `FilletEdgeSplitPlanner.cs` |
| `corner@J<id>/<taxiwayA>/<taxiwayB>` | corner **arc** | `FilletPlanExecutor.cs` |
| `corner-chord@J<id>/<taxiway>` | corner **chord** (degenerate fallback) | `FilletPlanExecutor.cs` |
| `straight-connector@J<id>/<taxiway>` | straight connector | `FilletPlanExecutor.cs` |

`Origin` is `[JsonIgnore]` (runtime-only; not persisted in snapshots). It is a plain string; the deleted Legacy generator instead stamped `Fillet:`-prefixed strings (`Fillet:phase-c-arc@`, `Fillet:phase-d-shorten@`, etc.) through a typed-discriminator system, which was removed with it.

### Plan warnings are diagnostics, not failures

`PlanWarning` codes (`Fillet/PlanWarning.cs`) — `DEGENERATE_RADIUS`, `SINGLE_CUT_REJECTED`, `CORNER_DEMOTED`, `SHARED_ARM_SCALED`, `COINCIDENT_CUT_MERGED`, `NO_OWNING_CUT`, `FORCED_CORNER_UNPRESERVES`, `SUB_THRESHOLD_CUT_SKIPPED` (`UNCONSUMED_INCIDENT_EDGE` and `UNCONSUMED_RECONNECT_SAFETY_NET` are defined in `PlanWarning.cs` but nothing emits them) — are collected onto `FilletPlan.Warnings` / `FilletStatistics.Warnings` and logged.

They flag corners that couldn't be rounded as requested, not generation failures. A surge in `DEGENERATE_RADIUS` / `CORNER_DEMOTED` on a new airport is a triage signal (tight or distorted geometry), not a crash.

### The fillet report

`FilletStatistics.Report` is a `FilletReport` (`FilletReport.cs`), built by `FilletReportCollector` as the pass runs; it is `FilletReport.Empty` for `FilletMode.None`. It lives only on the returned statistics: nothing stores it on `AirportGroundLayout`, so it reaches no recording, snapshot or wire message.

`Corners` has a row per junction-level skip of a taxiway intersection with two or more edges (an arm-less row: shape point, ineligible, no corner pairs) and per arm pair the corner loop visited, each with an outcome (`Built`, `ChordDegraded`, `Skipped`), a `FilletSkipReason` when it was not built as an arc, the requested and achieved radius, and the planner warnings raised for it. `CornerPlanner` skips a pair as same far node, collinear, below `MinFilletAngleDeg`, near-hairpin (turn over `NearHairpinThresholdDeg`) or under the radius floor; each is a skip reason.

`Overrides` has a row per `CornerFilletOverride`, resolved by `FilletOverrideResolver` against the pre-fillet layout before any corner is cut. An override matches a corner when the junction is within 10 ft of its apex, the arm pair carries its two taxiway names (case-insensitive, either order) and each arm's bearing is within 15 degrees of the one it names. No corner is `Orphaned`, several is `Ambiguous`, and an override whose corner another override also names is `Ambiguous` too, so the result never depends on sidecar order. A `Matched` override is applied (next section) and its row says what that did: `Effect` is `Applied`, `NotReachable` (the pass never visits the corner: a hand-drawn arc's shape point, a junction with fewer than two edges, a runway centerline projection, or an intersection the pass does not fillet that the override does not force) or `Rejected` (a force or a pinned tangent the pass refuses; `RejectedReason` is the skip reason, `RejectedRadiusFt` the tightest radius for `Unsteerable`). `Effect` is null for an `Orphaned` or `Ambiguous` row. An override that asks for none of suppress, radius, force or pinned tangents changes nothing and is `Applied`. A row's `Warnings` name the edits the pass ignored: those a suppress wins over (`suppress wins: radiusFt and tangentFt ignored`), a radius two pins win over (`both tangents pinned: radiusFt ignored`, `one tangent pinned and mirrored: radiusFt ignored`), or what a refused pin hit (the node of a `PinOnNode`, the arm a mirrored pin was refused on).

### Per-corner overrides

`FilletOverrideResolver.ResolveCorners` keeps the corner each matched override names, and `FilletCornerEdits` hands it to `CornerPlanner` as a lookup by junction and arm pair, so no sidecar order decides geometry. An override touches only its own corner: the junction's other corners keep the cuts they have without it. The same sidecar builds the same arcs byte for byte on one machine; x64 and arm64 can differ in the last bits, so the tests pin radii within 1e-6 ft.

- **`suppress`**: the pair gets no fillet and skip reason `Suppressed`. The junction node and every arm's stub stay (the junction is kept like a through pair's), so the pair stays a sharp corner, and the junction's other corners are planned as if this one asked for no tangent cut.
- **`radiusFt`**: replaces the edge types' maximum radius as the corner's requested radius. It is still a ceiling: the arc is sized to the smaller of it and what the tangent geometry allows, so a short arm gives a smaller radius than the entry names.
- **`force`**: plans the corner past the checks that would skip it, on any taxiway intersection of two or more edges that is not a runway centerline projection: not an eligible intersection, collinear, below `MinFilletAngleDeg`, near-hairpin, under the radius floor. Where the planner would plan the corner anyway the force changes nothing in the sizing. Where only the force plans it, the corner's tangent stays out of its arms' shared sizing so the junction's other corners keep their cuts, and a through pair that is filleted loses the preserved node it had (`FORCED_CORNER_UNPRESERVES`).

- **`suppress` with other edits**: a suppress wins over the entry's radius, force and pinned tangents (`CornerFilletOverride.AsApplied`), and both the sidecar load and the override's report row warn the ignored edits (`CornerFilletOverride.SuppressIgnoredEditsWarning`).
- **`tangentFt`**: pins the corner's tangent cuts at those distances from the apex (the pinned-tangent pass below). The arc's radius is the one the pins allow (`CornerSpec.RadiusFromPins`), so the entry's radius is ignored.

A few checks a force never bypasses. The generator refuses such a forced corner and plans the pass again without its force (`PlanHonouringEdits`), so a refused force leaves the layout as the unforced pass builds it; the corner carries the skip reason and the override is `Rejected`. The reasons are `SameFarNode`, `NoTurn` (the pair turns under `MinForcedTurnDeg`, 1 degree), `Unsteerable` (the arc's tightest radius is under `GeometricAdmissibility.MinSteerableArcRadiusFt`, 10 ft), `SharedArmClearGap` (the tangent cut would leave less than the shared-arm clear gap to the neighbouring junction's cuts), `UnresolvedEndpoint` and `CoincidentCuts`. `ForcedCornerCheck` decides this from the plan before the executor changes the layout.

`PlanHonouringEdits` plans the pass, then withdraws refused edits one kind at a time and plans again: first every pin the plan itself refused (the planner's bounds, node and conflict checks, and the plan's `PinOverlapsNeighbour` and `PinConflict`), then every refused force, then every pin whose built arc `PinnedCornerCheck` refuses. Each round withdraws at least one edit, so the loop ends; a withdrawn edit is not retried. A refused pin leaves the entry's force and radius in place, and a refused force leaves its pins.

### The pinned-tangent pass

A pinned tangent is a fixed point: once honoured, no pass moves it. It runs through the pipeline as follows.

- **Planning (`CornerPlanner.WithPins`).** Each pin is matched to its arm by taxiway name and bearing. A pin on one arm only is mirrored onto the other at the same distance, clamped to that arm's far limit (`PinFarLimitFt`: `PinnedTangentFarMarginFt`, 5 ft, short of the arm's far node or of the first node along it another taxiway joins), so every honoured corner has both pins. This is the one place a pin's bounds are judged: under `MinPinnedTangentFt` (5 ft) is `PinTooClose`, past the arm's length less 5 ft `PinBeyondArm`, past the far limit `PinPastIntersection`, and within `GeometricAdmissibility.NoOpEdgeThresholdNm` (about 1.2 ft) of a node on the arm's walk `PinOnNode` (never snapped onto it). A refusal takes the entry's other pin with it and is recorded on the collector. `RefuseConflictingPins` then refuses, as `PinConflict`, the pins of every two corners of the junction whose pins on one arm lie within 5 ft of each other.
- **Cut placement (`ArmCutResolver.PlacePinnedCuts`).** After the arm's sized cuts are placed as without the pin, each pinned arm gets a cut of its own at the pinned distance (`ResolvedArmCut.Pinned`), numbered with the forced-only cuts so the sized cuts keep their ids. A sized cut only pinned corners took is dropped from the junction and returned as a sizing-only cut (`JunctionCutResult.SizingOnlyCuts`), so the shared-arm sizing still counts its reach and the neighbouring junctions are sized as without the pin.
- **Shared arms (`SharedArmTangentPass`).** Pinned cuts stay out of the shared-arm sizing and scaling. A pinned cut reaching more than 5 ft past the neighbouring junction's cut on their shared arm is refused as `PinOverlapsNeighbour`; one that leaves less than `MinSharedArmClearGapFt` of straight is honoured with the warning `PINNED_SHORT_STRAIGHT`. Every coalesce merge (`MergeOf`) keeps a pinned cut over an unpinned one.
- **Plan building (`FilletPlanBuilder`).** `RefusePinnedMerges` refuses, as `PinConflict`, both pins of every merge group (from any pass) that holds two pinned cuts, directly or through unpinned cuts, and drops the merges that would move them. `BuildSurvivorMap` keeps a pinned root; `ExtendWithStableAnchors` never redirects a pinned cut onto a stable node; `DedupRank` puts a pinned corner above every unpinned one for an endpoint pair.
- **The built arc (`PinnedCornerCheck`).** Once every force left stands, the arc each pinned corner would get, built as `FilletPlanExecutor.CornerBezier` builds it, is refused as `Unsteerable` when it degrades to a chord or its tightest radius is under 10 ft, with that radius (0 for a chord).
- **After execution.** `FilletArcGenerator.ReportDroppedPinnedArcs` reports a pinned corner whose arc the normalizer removed as skipped (`DegenerateRadius`), so no pinned arc is dropped silently.

A pin alone (no force, no radius) on a pair the pass skips builds nothing, and the override is `Rejected` with the pair's skip reason; with a force the corner is built at the pins.

### `FilletStatistics` legacy-repair counters are pinned at zero

The generator reports `CoincidentNodesMerged`, `OrphansRescued`, `RedundantPreserveEdgesRemoved`, `DuplicateCornerArcsRemoved`, `ParallelBypassEdgesRemoved`, `DirectShortensAdded` all **= 0** by construction (`FilletArcGenerator.cs`) — those describe Legacy repair passes the generator doesn't have.

`CoincidentNodesMerged` is zero because the post-execute coincident-node merge was deleted (the plan + the runway-crossing projector guarantee no coincident nodes); the normalizer's remaining structural cleanup count (degenerate arcs/edges + isolated nodes removed) is logged separately, not surfaced as a statistic.

The connectivity gate **requires** these to stay zero. They exist only because `FilletStatistics` retains the shape it had when the generator was diff-compared against the now-removed Legacy generator.

---

## Legacy — removed

The Legacy pair-based generator, its `LegacyFilletArcGenerator` adapter, and the `FilletArcGeneratorRouter` selector were deleted when this generator became the only one. Per project policy there are **no compatibility shims** — do not add Legacy fixes; fix forward in the current generator. The shared helpers `ManualArcDetector` and `FilletEligibility` survive and are used by it.

---

## Key files

| File | Role |
|---|---|
| `src/Yaat.Sim/Data/Airport/FilletArcGenerator.cs` | Generator entry — ID seeding, per-junction loop, plan→execute→normalize |
| `src/Yaat.Sim/Data/Airport/Fillet/JunctionClassifier.cs` | Classify a junction into arms + corners + `JunctionKind` |
| `src/Yaat.Sim/Data/Airport/Fillet/TaxiwayArmBuilder.cs` | Walk each incident edge into a `TaxiwayArm` |
| `src/Yaat.Sim/Data/Airport/Fillet/CornerPlanner.cs` | Pair arms into `CornerSpec`s / collinear pairs; skips a pair as same far node, collinear, below `MinFilletAngleDeg`, near-hairpin or under the radius floor, each reported to the `FilletReportCollector` |
| `src/Yaat.Sim/Data/Airport/Fillet/ArmCutResolver.cs` | Resolve tangent-cut positions (single + ordered multi-cut) |
| `src/Yaat.Sim/Data/Airport/Fillet/ForcedCornerCheck.cs` | Decide from a plan whether a forced corner may be built (recorded skip, unresolved or coincident endpoint, `Unsteerable` arc) |
| `src/Yaat.Sim/Data/Airport/Fillet/PinnedCornerCheck.cs` | `PinRefusal`; decide from a plan whether a pinned corner's built arc keeps its pins (a chord or an `Unsteerable` arc refuses them) |
| `src/Yaat.Sim/Data/Airport/Fillet/SharedArmTangentPass.cs` | Intra-arm coalesce + cross-arm coalesce + cross-junction shared-arm scaling/merge |
| `src/Yaat.Sim/Data/Airport/Fillet/FilletPlanBuilder.cs` | Assemble the immutable `FilletPlan` |
| `src/Yaat.Sim/Data/Airport/Fillet/FilletPlanCutRedirect.cs` | Union-find survivor map + stable-anchor redirect |
| `src/Yaat.Sim/Data/Airport/Fillet/FilletEdgeSplitPlanner.cs` | Order-independent global edge-split |
| `src/Yaat.Sim/Data/Airport/Fillet/FilletPlanExecutor.cs` | Materialize cuts, emit edges/arcs/chords, remove junctions |
| `src/Yaat.Sim/Data/Airport/Fillet/FilletPlan.cs` | The plan record + `SurvivingEdgeOp` / op records |
| `src/Yaat.Sim/Data/Airport/Fillet/CutId.cs` | `readonly record struct CutId` — planning ID, type-distinct from node IDs |
| `src/Yaat.Sim/Data/Airport/Fillet/FilletEndpoint.cs` | Sealed `Cut(CutId) \| Node(int)` union for redirected endpoints |
| `src/Yaat.Sim/Data/Airport/Fillet/FilletPlanConsistency.cs` | Fail-fast plan validation (cut + node references) |
| `src/Yaat.Sim/Data/Airport/Fillet/FilletGeometry.cs` | Shared bezier construction + radius math |
| `src/Yaat.Sim/Data/Airport/Fillet/FilletConstants.cs` | Shared thresholds (radius floor, coincident, gaps, radii) |
| `src/Yaat.Sim/Data/Airport/Fillet/FilletEligibility.cs` | Shared intersection eligibility + preserve rule |
| `src/Yaat.Sim/Data/Airport/Fillet/ManualArcDetector.cs` | Shape-point (manual-arc) exclusion |
| `src/Yaat.Sim/Data/Airport/Fillet/FilletGraphNormalizer.cs` | Post-execute recompute + defensive merge (no repair) |
| `src/Yaat.Sim/Data/Airport/IFilletArcGenerator.cs` | Generator interface (`Id`, `DisplayName`, `Apply`) |
| `src/Yaat.Sim/Data/Airport/CornerFilletOverrides.cs` | `CornerFilletOverride` / `CornerFilletOverrides`: the airport's sidecar per-corner overrides, passed to `Apply` (applied: suppress, radius, force, pinned tangents) |
| `src/Yaat.Sim/Data/Airport/FilletReport.cs` | `FilletReport`: per-corner rows (`Built` / `ChordDegraded` / `Skipped`, `FilletSkipReason`) and per-override rows (`Matched` / `Orphaned` / `Ambiguous`, and a matched one's `FilletOverrideEffect`), on `FilletStatistics.Report` |
| `src/Yaat.Sim/Data/Airport/Fillet/FilletReportCollector.cs` | Collects the report's corner rows as the planner, executor and generator run |
| `src/Yaat.Sim/Data/Airport/Fillet/FilletOverrideResolver.cs` | Match each `CornerFilletOverride` to a pre-fillet corner (apex, taxiway pair, arm bearings) |
| `src/Yaat.Sim/Data/Airport/FilletMode.cs` | Enum: `None` (no-op) \| `Standard` (production) |
| `src/Yaat.Sim/Data/Airport/FilletGeneratorFactory.cs` | Factory: `FilletMode` → `IFilletArcGenerator` |
| `src/Yaat.Sim/Data/Airport/NullFilletArcGenerator.cs` | No-op implementation for `FilletMode.None` |
| `src/Yaat.Sim/Data/Airport/FilletArcGeneratorRegistry.cs` | Registry: enumerate all implementations |
| `src/Yaat.Sim/Data/Airport/GeoJsonParser.cs` | Invokes the chosen generator at parse Step 8 |
| `src/Yaat.Sim/Data/Airport/AirportGroundLayout.cs` | `GroundArc` (bezier fields, `TaxiwayNames`, `MatchesTaxiway`), `GroundNode`, `GroundEdge` |
| `tests/Yaat.Sim.Tests/Fillet/FilletCornerSpanGuardTests.cs` | Guard: cut-ID-collision detection (≤ 300 ft corner spans) |

**Historical plan docs** (deleted — this doc is the durable reference; git history has `docs/plans/archive/filletv2/` and `docs/plans/archive/ground-graph-v2.md`).

**Tooling:** `tools/Yaat.LayoutInspector` with `--fillet-mode standard|none` inspects the chosen graph; `--node N`, `--dump`, `--html [--ticks]`, and `--debug-fillets` (verbose fillet debug logging) are the workhorses for ground/exit/taxi debugging.

## Related docs

- [`./pathfinder.md`](./pathfinder.md) — consumes the filleted graph to build `TaxiRoute`s (membership-arc handling, mandatory-connector insertion).
- [`./navigator.md`](./navigator.md) — compiles each `GroundArc` into a `PathPrimitiveBezier` and follows the actual cubic Bézier closed-form (`MaxSafeSpeedKts`).
- [`../landing-and-runway-exit.md`](../landing-and-runway-exit.md) — runway-exit geometry that interacts with high-speed-exit fillet radii.
