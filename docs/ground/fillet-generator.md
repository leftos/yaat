# Fillet Arc Generator — Design & Architecture

> Read this before touching `src/Yaat.Sim/Data/Airport/FilletArcGeneratorV2.cs`, anything under `src/Yaat.Sim/Data/Airport/Fillet/V2/`, the shared `Fillet/FilletGeometry.cs` / `Fillet/FilletConstants.cs`, or the legacy `FilletArcGenerator.cs` / `LegacyFilletArcGenerator.cs` / `FilletProvenance.cs`. The fillet generator turns the raw straight-segment ground graph into one with smooth corner arcs and order-independent junction connectivity. It is layer 1 of the three-layer ground stack — see the [pathfinder](./pathfinder.md) that walks the graph it builds and the [navigator](./navigator.md) that physically follows the arcs it emits. Index: `./README.md`.

## Transition status — V2 is the target, Legacy is the current default

The ground stack is mid V1→V2. **V2 (`FilletArcGeneratorV2` + `Data/Airport/Fillet/V2/*`) is the architecture this doc describes and where all new work goes.** It is geometry-validated (the `Compare_LegacyVsV2_MeetsHardGates` connectivity gate is green on FLL/OAK/SFO) and sim-validated behind the switch (the OAK/SFO/FLL taxi-coverage smoke set + landing/exit scenarios run on `FilletMode.V2` layouts with the V1 pathfinder). The **Legacy** generator (`FilletArcGenerator` / its `LegacyFilletArcGenerator` adapter / `FilletProvenance`) is the **runtime default today** and is being replaced; it is deleted once V2 flips on.

| Layer | V2 component | Runtime default now | Flip gate |
|---|---|---|---|
| Fillet generator (this doc) | `FilletArcGeneratorV2` | `LegacyFilletArcGenerator` (Legacy) | shared joint flip with pathfinder + navigator |

The three layers were each co-tuned against Legacy geometry, so flipping one alone leaves the stack mismatched. The fillet flip is sequenced **first** (pathfinder stays V1 so any failure isolates to fillet geometry), then pathfinder V2, then the navigator review — all shipped together in a single change to `GeoJsonParser.Parse`'s default + `AirportLayoutDownloader`, after which Legacy is deleted (`docs/plans/ground-graph-v2.md`, `docs/plans/filletv2/status.md`).

**The single most important principle for any agent working on V2:** *the V2 graph is correct-but-different, not broken.* It collapses each junction into fewer tangent nodes with larger per-corner bearing steps; it retains membership-matched junction arcs (`C1 - B`); it faithfully preserves source-data quirks (coincident edges, taxiways that connect only via a third connector). When a downstream consumer trips on V2 geometry, **adapt the consumer — do not "fix" the graph.** The two sim regressions the all-V2 sweep surfaced both live in the routing/navigation layer, not the fillet geometry (`docs/plans/filletv2/v2-sim-validation.md`).

---

## What fillets are & where they sit

### Input and output

The generator consumes the **base graph** built by `TaxiwayGraphBuilder` (via `GeoJsonParser`): straight `GroundEdge`s on taxiway and runway-centerline LineStrings, joined at `GroundNode` intersections, plus parking/helipad/hold-short nodes. The base graph has sharp corners — an aircraft following it node-to-node would pivot in place at each intersection.

The generator produces two things, mutating the layout in place:

1. **Corner arcs** — a `GroundArc` (cubic Bezier reinterpreted downstream as a true circle) between two **tangent-cut** nodes on the two arms of a turn, replacing the sharp vertex with a smooth curve. A too-tight corner degrades to a straight **chord** (`GroundEdge`) instead.
2. **An order-independent edge-split** — each original edge is split once at the cuts that land on it; the stub incident to a removed junction is dropped; every other sub-segment survives and reconnects. This is what keeps the graph connected after the junction node is deleted.

`FilletStatistics` (returned, currently advisory) tallies filleted nodes, arcs, collinear merges, coincident merges, and carries the plan's `Warnings`.

### Selection & invocation

`GeoJsonParser.Parse(airportId, geoJson, runwayAirportCode, FilletMode)` is the entry point (`src/Yaat.Sim/Data/Airport/GeoJsonParser.cs:43`). After it builds nodes/edges and rebuilds adjacency (Step 7), Step 8 runs the fillet pass **only when `filletMode != None`** (`GeoJsonParser.cs:288`):

```csharp
FilletGeneratorFactory.Create(filletMode).Apply(layout);
```

| `FilletMode` | Factory result | Behavior |
|---|---|---|
| `None` | `NullFilletArcGenerator.Instance` | no-op; raw intersection graph (used by `FilletMode.None` tests + comparison baselines) |
| `Legacy` | `new LegacyFilletArcGenerator()` | delegates to static `FilletArcGenerator.Apply` |
| `V2` | `new FilletArcGeneratorV2()` | plan-then-execute (this doc) |

`FilletMode.cs`, `FilletGeneratorFactory.cs`, and the `IFilletArcGenerator` interface (`Id`, `DisplayName`, `Apply`) form the selection surface. The convenience overloads `Parse(...)` / `Parse(..., applyFillets: bool)` map to `Legacy` / `None` (`GeoJsonParser.cs:35`, `:40`) — **`Legacy` is the parser's default**, which is why production still runs Legacy.

`FilletArcGeneratorRouter` is a separate runtime selector (`Current`, `UseV2`) for callers that want to switch implementation without re-parsing (startup flag, integration tests). It also defaults to Legacy (`FilletArcGeneratorRouter.cs:16`). It has **no `src/` consumers today** and is slated for retirement at the flip; the parser path through `FilletGeneratorFactory` is the live one. `FilletArcGeneratorRegistry.All` lists all three implementations for enumeration/comparison harnesses.

The downstream consumers of the filleted graph: the **[pathfinder](./pathfinder.md)** walks it to build `TaxiRoute`s, and the **[navigator](./navigator.md)** compiles each `GroundArc` into a `PathPrimitiveArc` and follows it closed-form.

---

## Key design decisions & requirements

### Why V2 exists

Legacy is a per-pair, order-dependent pipeline: for each intersection it fillets every edge pair, places tangent nodes, then runs a cascade of repair passes (`AddDirectShortensFromArcAnchors`, `RescueOrphanedTangentNodes`, parallel-bypass removal, reconnect, duplicate-arc removal — see the `FilletEdgeKind` enum in `FilletProvenance.cs`). Those passes mutate-then-repair: they could create an edge in one junction's pass and strip it in another's, and they emitted **zero-distance and reverse-traversed edges** that the pathfinder tripped on (orbit/spin bugs). The chain-planner that tried to make the connectivity order-independent (`FilletArmChainPlanner`, `FilletConnectivityPlanner`) was itself fragile and was deleted. V2 is a clean-room rewrite around two ideas: **plan everything before mutating anything**, and **connectivity is one global edge-split, not a stack of repair heuristics**.

### Plan-then-execute, order-independent

`Apply` builds a pure, immutable `FilletPlan` describing every cut, arc, chord, straight-connector, surviving edge, and node-removal — *then* hands it to `FilletPlanExecutor`, which materializes it in one forward pass with no per-junction mutation loop. Because the plan is computed against the **pre-fillet** layout and the executor never reads back its own partial output, the result is independent of junction processing order. This is the structural property that killed the create-then-strip bug class.

### Global edge-split connectivity & the no-true-disconnection gate

`FilletEdgeSplitPlanner.Plan` (`Fillet/V2/FilletEdgeSplitPlanner.cs:29`) replaces the entire Legacy reconnect/bypass/side-branch machinery. It splits each **original** edge exactly once by the cuts that land on it, drops **only** the stub incident to a removed junction, and keeps every other sub-segment. A removed junction never appears as a surviving endpoint (its endpoints are replaced by cut nodes before any mutation). The correctness bar is the **no-true-disconnection** gate: structural validity + Legacy repair counters all zero + parking→hold-short reachability matching Legacy + **no node present in both layouts reachable in only one** (`docs/plans/filletv2/v2-divergences.md`). Exact hold-short stable-set equality is reported but not required — each generator dissolves a slightly different set of marginal junctions, which is accepted clean-room divergence.

### Runway-bearing parity

`CompareRunwayBearings` reports 0 mismatches vs Legacy. This falls out of two design choices rather than a dedicated pass: the edge-split preserves each source edge's `(Nodes[0]→Nodes[1])` orientation on every sub-segment (`FilletEdgeSplitPlanner` carries `IsRunwayCenterline` through `MakeEdge`, `:179`), and `BuildBezier` projects control points **toward** the junction (`FilletGeometry.cs:130`). Earlier the bezier projected *away* along the arm, producing S-cusps / near-zero-radius arcs and a reversed `RWY10L/28R` segment; the toward-junction projection fixed both.

### Corner-radius policy and the radius floor

The requested radius per corner comes from `FilletGeometry.SelectMaxRadius` (`FilletGeometry.cs:29`), keyed off edge type and turn angle:

| Corner involves | Turn angle | Requested radius |
|---|---|---|
| a RAMP edge | any | `RampRadiusFt = 50 ft` |
| a runway centerline | ≤ 45° (high-speed exit) | `HighSpeedExitRadiusFt = 150 ft` |
| a runway centerline | > 45° | `RunwayExitRadiusFt = 100 ft` |
| neither (taxiway×taxiway) | any | `DefaultRadiusFt = 75 ft` |

The arc the executor actually builds is sized to `min(requested, EffectiveMinRadiusFt(tangent geometry))` (`FilletPlanExecutor.cs:90`) so the stored `MinRadiusOfCurvatureFt` — the value the navigator reads for turn-speed back-propagation — is honest, not an over-bulged requested-radius bezier. The **radius floor** is `FilletConstants.RadiusFloorFt = 5 ft`: a corner whose effective radius drops below the floor is rejected at plan time (`ArmCutResolver.cs:186`) or, if it slips through to execution, degrades to a chord (below). V2's corner radii run **tighter** than Legacy at many junctions because its cut placement caps tangent distance to avoid overrunning adjacent intersections — an accepted bidirectional policy difference, not pursued to exact parity (`docs/plans/filletv2/v2-divergences.md` "Corner-radius").

### Manual-arc detection

Before classifying junctions, `ManualArcDetector.Detect` (`Fillet/ManualArcDetector.cs`) excludes **shape-point nodes**: a `TaxiwayIntersection` with exactly two edges of the **same** taxiway name. These are vertices on a pre-existing curve drawn in the source GeoJSON, not real turn junctions — filleting them would corrupt the hand-drawn arc. Excluded node IDs are skipped in `Apply`'s main loop and threaded into the arm-walk so a walk crosses them transparently.

### Tangent cuts and stable-anchor redirects

A tangent cut is a new node placed a computed distance **along an arm, away from the junction**, where the arc tangents off. When a cut lands within `CoincidentNodeThresholdFt = 5 ft` of a **pre-existing stable node** — a `TaxiwayIntersection`, `Spot`, `Parking`, `Helipad`, or `RunwayHoldShort` that is not a runway-centerline projection (`FilletPlanCutRedirect.IsStableAnchorTarget`) — the plan **redirects** the cut onto that existing node instead of materializing a duplicate tangent on top of it (`ExtendWithStableAnchors`, `FilletPlanCutRedirect.cs:19`). This keeps the graph from sprouting near-coincident node pairs the normalizer would just have to merge. The redirect is the source of a subtle namespace invariant — see Caveats.

> The eligible-target set originally included only `TaxiwayIntersection`, so a cut landing on a spot/parking/hold-short endpoint was **not** redirected — it materialized a duplicate node joined by a **zero-distance edge**. That no-op edge's meaningless 0° bearing survived into the materialized route, and `GroundNavigator` followed it, producing a visible taxi wiggle (SFO had 11 such edges, OAK 10+, FLL 1; the raw geojson and Legacy have none). Widening `IsStableAnchorTarget` to all five stable types fixed it. Guard: `FilletV2CornerSpanGuardTests.V2_EdgeSplit_NoZeroDistanceEdges` (no edge under ~1.2 ft at SFO/OAK/FLL).

### Eligibility & the runway-preserve rule

`FilletEligibility.IsEligible` (`Fillet/FilletEligibility.cs`) gates which intersections get filleted, shared with Legacy:

- runway-centerline-projection nodes: never eligible.
- a node with **one** runway edge **and** ≥1 non-runway edge: eligible but **`preserveNode = true`** — the junction node survives (it is a runway/taxiway connection that must stay a discrete vertex; e.g. a runway hold-short tie-in).
- a node with one runway edge and no other edges: not eligible.
- a two-edge same-taxiway node: not eligible (it is a shape point).

`preserveNode` junctions are classified `JunctionKind.Preserve` and never added to `nodesToRemove` — their corners still get arcs, but the original vertex stays.

---

## Step-by-step pipeline walkthrough — `FilletArcGeneratorV2.Apply`

`FilletArcGeneratorV2.Apply` (`src/Yaat.Sim/Data/Airport/FilletArcGeneratorV2.cs:16`):

**1. Manual-arc detection + ID seeding.** `ManualArcDetector.Detect` collects shape-point node IDs. `maxNodeId` = the largest existing node ID. Two counters are seeded:
   - `idCounter` (new tangent-cut **node** IDs) starts at `maxNodeId + 1`.
   - `nextCutId` (plan-internal **cut** IDs, typed `CutId`) starts at **`maxNodeId + 1_000_000`** — a disjoint high range. The `CutId` newtype makes cut-vs-node confusion a compile error; the offset is defense-in-depth (see Caveats).

**2. Per-junction classification.** For each node in ID order, skip if it is a manual-arc node, ineligible, or has fewer than 2 edges. Otherwise `JunctionClassifier.Classify` (`Fillet/V2/JunctionClassifier.cs`):
   - `TaxiwayArmBuilder.BuildArms` walks each incident edge outward (through shape points) into a `TaxiwayArm` (`Fillet/V2/TaxiwayArm.cs`): root edge, taxiway name, bearing from the junction, available length, an `IntersectionCapFt` that bounds how far a cut may sit before overrunning the next intersection, a `TaxiwayArmTerminus` classification (OtherIntersection / ShapePoint / RunwayCenterline / HoldShort / Parking / DeadEnd), and the full `TaxiwayWalk.WalkResult`.
   - `CornerPlanner.PlanCorners` (`Fillet/V2/CornerPlanner.cs`) pairs arms. A pair below `CollinearThresholdDeg = 15°` turn is a **collinear pair** (a straight-through, no arc); a pair below `MinFilletAngleDeg = 15°` is skipped; otherwise it is a `CornerSpec` carrying turn angle, requested radius, ideal tangent distance, and arm bearings. The junction's `JunctionKind` is `Skip` / `Simple` / `MultiCorner` / `Preserve`.

**3. Per-junction arm-cut resolution.** `ArmCutResolver.Resolve(junction, ref nextCutId)` (`Fillet/V2/ArmCutResolver.cs:14`) decides where each arm is cut:
   - For each arm, a **candidate distance** is derived from the ideal tangent distances of its corners (averaged when they cluster within the coincident threshold, else `min(max, IntersectionCapFt)`), capped at `MaxTangentDistFt = 150 ft`.
   - A corner is flagged **distorted** when its effective radius is below the floor, more than `DistortionThreshold = 2×` the requested radius, or its two tangent distances are more than `AsymmetryThreshold = 2×` apart. Distorted arms take an **ordered multi-cut** path: positions are coalesced (`IdealCoalesceThresholdFt = 2 ft`) and gap-enforced (`MinArmSegmentGapFt = 5 ft`, demoting cuts that crowd). Non-distorted arms take a single cut; a sub-threshold cut is clamped to `5 ft + 1` with a `SubThresholdCutSkipped` warning.
   - Each cut becomes a `ResolvedArmCut` (id, junction, arm, distance, position, bearing-toward-junction, owning corner IDs). Corners map to `(cutA, cutB)` → a `CornerArcOp`; corners not arc-able fall to `StraightConnectorOp`. `SharedArmTangentPass.ApplyIntraArmCoalesce` adds `TangentMergeOp`s for cuts on one arm landing within 5 ft of each other; `ApplyCrossArmCoalesce` adds them for cuts on **different arms of the same junction** within 5 ft (e.g. an `A` tangent and a collinear `A8`/`RAMP` tangent) — these used to be merged only by the post-execute normalizer, which manufactured duplicate corner arcs.

**4. Plan building.** `FilletPlanBuilder.Build` (`Fillet/V2/FilletPlanBuilder.cs:7`) aggregates all junction results, then:
   - `SharedArmTangentPass.ApplyCrossJunction` scales and merges cut sets on a physical arm **shared between two adjacent junctions** so their cuts don't collide in the middle (`Fillet/V2/SharedArmTangentPass.cs:49`).
   - `FilletPlanCutRedirect.BuildSurvivorMap` builds a union-find survivor map over the tangent merges; `ExtendWithStableAnchors` redirects coincident cuts onto pre-fillet stable nodes and returns the **authoritative set of anchor node IDs used**. `PruneCuts` keeps only surviving cut IDs; `RedirectCornerArcs` / `RedirectStraightConnectors` rewrite op endpoints through the survivor map, dropping self-pairs. The redirected ops are then **deduped by resolved endpoint pair** (keeping one op per node pair, preferring the single-name corner — requirement ①), so the cross-arm coalesce above yields exactly one arc per pair instead of a single-name + membership twin.
   - `FilletEdgeSplitPlanner.Plan` computes consumed + surviving edges (above).
   - The result is a `FilletPlan` (`Fillet/V2/FilletPlan.cs`) with `Cuts`, `TangentMerges`, `CornerArcs`, `StraightConnectors`, `SurvivingEdges`, `JunctionNodesToRemove`, `EdgesToRemove`, `Warnings`, and `StableAnchoredEndpointIds`. `FilletPlanConsistency.ValidateCutReferences` / `ValidateNodeReferences` throw if any op references an unknown cut or a to-be-removed node — a fail-fast guard, not silent repair.

**5. Execution.** `FilletPlanExecutor.Execute` (`Fillet/V2/FilletPlanExecutor.cs:12`):
   - `MaterializeCutNodes` creates one `GroundNode` per surviving cut (origin `V2:tangent-cut@J.../<taxiway>`), recording each cut's `SourceIntersectionPosition`.
   - Remove `EdgesToRemove` **before** adding survivors (so a survivor identical to a consumed edge isn't deduped then orphaned), then emit each `SurvivingEdgeOp` as a `GroundEdge` (origin `V2:edge-split/<taxiway>` or `V2:edge-split-redirect/<taxiway>`).
   - For each `CornerArcOp`: resolve both tangent endpoints, size the arc to `min(corner.RequestedRadiusFt, EffectiveMinRadiusFt)`, build the bezier via `FilletGeometry.BuildBezier`. **If `bez.MinRadiusFt < RadiusFloorFt`, emit a chord** (`GroundEdge`, origin `V2:corner-chord@J.../<taxiway>`) instead of a degenerate arc; otherwise add a `GroundArc` (origin `V2:corner@J.../<taxiwayA>/<taxiwayB>`), single-named when both edges share a taxiway, two-named otherwise.
   - Emit `StraightConnectorOp`s (origin `V2:straight-connector@J.../<taxiway>`), then delete every node in `JunctionNodesToRemove` and its incident edges/arcs.

**6. Normalization.** `FilletGraphNormalizer.Normalize` (`Fillet/FilletGraphNormalizer.cs`) recomputes edge/arc distances and arc radii from final node positions, rebuilds adjacency, runs a **defensive** 5 ft coincident-node merge, drops self-loops and sub-floor arcs, and removes isolated intersection nodes. There are deliberately **no repair passes** here. Same-junction cross-arm coincidences are now merged at plan time (step 3), so the defensive merge only still catches **cross-junction** coincidences (adjacent junctions' tangent cuts on a shared taxiway); moving those into the plan to retire the post-hoc merge entirely is tracked as a follow-up. Guarded by `FilletV2CornerSpanGuardTests.V2_CornerArcs_NoDuplicateNodePairs` + `V2_NoCoincidentIntersectionNodes`.

### `FilletGeometry` — the bezier (`Fillet/FilletGeometry.cs`)

`BuildBezier(tanA, tanB, bearingAToJunction, bearingBToJunction, requestedRadius)` (`:112`):
- Reverses each arm's toward-junction bearing to a from-tangent bearing, computes the effective turn `180 - |Δbearing|`, the sweep, and `kappa = (4/3)·tan(sweep/4)` (the standard cubic-bezier circular-arc approximation).
- Projects control points P1/P2 from each tangent **along the from-tangent bearing into the corner** at depth `kappa·radius`. P0 = `tanA`, P3 = `tanB`.
- Returns `MinRadiusFt` (sampled `MinRadiusOfCurvatureFt`), `ArcLengthNm`, the effective turn, and the two from-tangent bearings — all stored on the `GroundArc`. The navigator relies on the bezier hugging a true circle to within ~1 ft for radius ≥ 50 ft (see [`./navigator.md`](./navigator.md) Invariant I2).

`EffectiveMinRadiusFt` (`:89`) is the conservative plan-time radius estimate, `min(tangentA, tangentB) / tan(turn/2)` — asymmetric cubics built from endpoint positions alone under-estimate radius when the two tangents differ, so this is the value gated against the floor.

---

## Caveats & gotchas

Verify each against current code before relying on it.

### Cut-ID / node-ID type distinction (and the offset invariant behind it)

Planning **cut IDs** and graph **node IDs** are **type-distinct**, so confusing them is a compile error. `CutId` (a `readonly record struct`, `Fillet/V2/CutId.cs`) keys the cut dictionaries (`Cuts`, `prunedCuts`, `cutNode`); a redirected endpoint is a `FilletEndpoint` — a sealed `Cut(CutId) | Node(int)` union (`Fillet/V2/FilletEndpoint.cs`). The executor resolves by matching the union, not by a bare-int lookup (`FilletPlanExecutor.cs:33`):

```csharp
GroundNode? ResolveEndpoint(FilletEndpoint ep) => ep switch
{
    FilletEndpoint.Cut cut   => cutNode[cut.Id],          // resolved cut → materialized tangent node
    FilletEndpoint.Node node => layout.Nodes[node.NodeId], // stable anchor → pre-existing node
    _ => throw new InvalidOperationException(...),
};
```

A node `int` can no longer be passed where a `CutId` is expected. **As defense-in-depth** (and because `GroundNode.Id` is still `int`), cut IDs are also seeded at `maxNodeId + 1_000_000` (`FilletArcGeneratorV2.cs`) so the two ranges stay numerically disjoint.

**The cautionary story (commit `05be106e`, hardened to types in `c08662ac`):** cut IDs used to start at 1, sharing the `int` namespace with graph node IDs (start at 0), and the redirect map / resolver passed both as bare `int`. When `ExtendWithStableAnchors` redirected a tangent cut onto a pre-existing intersection node, the resolver looked the substituted node ID up in the cut-node map **first** — and if a cut ID from a **different junction** happened to equal that node ID, it returned the wrong tangent point. The bezier then degenerated to a chord spanning the two far-apart points: SFO had **52 corner-chord edges over 300 ft, the longest ~9533 ft** (effectively airport-spanning garbage edges). The first fix was the disjoint range + having `ExtendWithStableAnchors` return the authoritative anchor-ID set (`StableAnchoredEndpointIds`); the follow-up made the distinction type-level. The guard is `tests/Yaat.Sim.Tests/Fillet/FilletV2CornerSpanGuardTests.cs` — **no `V2:corner` arc or chord spans more than 300 ft at SFO, OAK, or FLL**. If you touch cut-ID seeding, the redirect, or endpoint resolution, this test is your tripwire.

### Corner arcs vs corner-chords

A `V2:corner@...` entry is a real `GroundArc` (smooth Bezier). A `V2:corner-chord@...` entry is a straight `GroundEdge` — the **degenerate fallback** emitted when `bez.MinRadiusFt < FilletConstants.RadiusFloorFt` (`FilletPlanExecutor.cs:103`). The chord keeps the two tangent cuts connected rather than relying on a sub-floor arc the normalizer would delete, so it is load-bearing for connectivity (`docs/plans/filletv2/status.md` "Gotchas") — do not remove it. A chord at a corner means that corner is too tight to round; the aircraft takes it as a sharp vertex.

### Source-data quirks are preserved, not "fixed"

V2 mirrors the source graph. Two recurring shapes look like bugs but are not:

- **Coincident / duplicate source edges** — e.g. FLL `C` and `C1` are drawn as near-coincident LineStrings in the GeoJSON. V2 keeps both; it does not dedupe them. This is source data (`docs/plans/ground-graph-v2.md:128`).
- **Taxiways that connect only through a third connector** — e.g. SFO `A` and `B1` have **no direct edge** in the source; they connect only via `Q`. Legacy's repair passes could synthesize a bridging junction node to make `A` and `B1` look directly adjacent; **V2 does not invent that adjacency**. A consumer that assumes `A`→`B1` is one hop must instead route `A`→`Q`→`B1`.

The principle: **V2 mirrors the source; adapt consumers, don't invent adjacency.** This is why the pathfinder gained a mandatory-connector insertion path (see [`./pathfinder.md`](./pathfinder.md)) rather than the graph being patched.

### Membership junction arcs ("X - Y") and `MatchesTaxiway`

A corner arc between two **differently-named** taxiways carries both names (`TaxiwayNames` length 2; e.g. `["C1","B"]`) and renders its display name with a `" - "` separator — `C1 - B` (`AirportGroundLayout.cs:310`; the `" - "` separator avoids colliding with `/` in runway IDs like `RWY30/12`). `GroundArc.MatchesTaxiway(name)` returns true if the queried name is **any** of the arc's names (`AirportGroundLayout.cs:312`). Downstream, a bare-taxiway walk for `B` therefore *sees* a `C1 - B` arc as a valid `B` step — the arc is a legitimate turn-connector, not noise. This membership semantics is what makes the pathfinder's "single-name continuation beats membership-only arc" rule necessary; see [`./pathfinder.md`](./pathfinder.md) "Membership junction arcs". Do not strip the second name to make membership stricter — the arc genuinely belongs to both taxiways.

### Edge / arc / node origin strings (debugging)

Every V2-emitted element stamps an `Origin` string, surfaced in LayoutInspector tooltips (`--html`) and the corner-span guard test. Grep these when diagnosing geometry:

| Origin prefix | Element | Emitted by |
|---|---|---|
| `V2:tangent-cut@J<id>/<taxiway>` | tangent-cut node | `FilletPlanExecutor.cs:193` |
| `V2:edge-split/<taxiway>` | surviving sub-segment | `FilletEdgeSplitPlanner.cs:180` |
| `V2:edge-split-redirect/<taxiway>` | cutless-arm redirect segment | `FilletEdgeSplitPlanner.cs:180` |
| `V2:corner@J<id>/<taxiwayA>/<taxiwayB>` | corner **arc** | `FilletPlanExecutor.cs:124` |
| `V2:corner-chord@J<id>/<taxiway>` | corner **chord** (degenerate fallback) | `FilletPlanExecutor.cs:105` |
| `V2:straight-connector@J<id>/<taxiway>` | straight connector | `FilletPlanExecutor.cs:139` |

`Origin` is `[JsonIgnore]` (runtime-only; not persisted in snapshots). The legacy `FilletProvenance` typed-discriminator system (`Fillet:phase-c-arc@`, `Fillet:phase-d-shorten@`, etc.) is Legacy-only — V2 uses plain origin strings, not `FilletProvenance`.

### Plan warnings are diagnostics, not failures

`PlanWarning` codes (`Fillet/V2/PlanWarning.cs`) — `DEGENERATE_RADIUS`, `SINGLE_CUT_REJECTED`, `CORNER_DEMOTED`, `SHARED_ARM_SCALED`, `NO_OWNING_CUT`, `SUB_THRESHOLD_CUT_SKIPPED`, `UNCONSUMED_*` — are collected onto `FilletPlan.Warnings` / `FilletStatistics.Warnings` and logged. They flag corners that couldn't be rounded as requested, not generation failures. A surge in `DEGENERATE_RADIUS` / `CORNER_DEMOTED` on a new airport is a triage signal (tight or distorted geometry), not a crash.

### `FilletStatistics` legacy-repair counters are pinned at zero

V2 reports `OrphansRescued`, `RedundantPreserveEdgesRemoved`, `DuplicateCornerArcsRemoved`, `ParallelBypassEdgesRemoved`, `DirectShortensAdded` all **= 0** by construction (`FilletArcGeneratorV2.cs:76`) — those describe Legacy repair passes V2 doesn't have. The connectivity gate **requires** them to stay zero. They exist only so V2 and Legacy share the `FilletStatistics` shape for comparison.

---

## Legacy (being removed) — V1

`src/Yaat.Sim/Data/Airport/FilletArcGenerator.cs` is the static implementation; `LegacyFilletArcGenerator.cs` is the thin `IFilletArcGenerator` adapter that delegates to it (`FilletArcGenerator.Apply`). It is the **current runtime default** via `FilletGeneratorFactory.Create(FilletMode.Legacy)` from `GeoJsonParser.Parse`. Recognize Legacy by:

- **Per-pair filleting** — for each intersection, every edge pair gets a tangent placement + arc/merge, then the intersection node is deleted (`FilletArcGenerator.cs:5` summary). Constants (`MinFilletAngleDeg`, radii, `MaxTangentDistFt`, coincident thresholds) are duplicated as `private const` in `FilletArcGenerator.cs:15` — the shared values now also live in `FilletConstants.cs`.
- **Repair-pass cascade** — `AddDirectShortensFromArcAnchors`, `RescueOrphanedTangentNodes`, parallel-bypass removal, reconnect, duplicate-arc removal. These are the order-dependent mutate-then-repair passes V2's single edge-split replaced; their kinds are enumerated in `FilletProvenance.cs` (`FilletEdgeKind`).
- **`FilletProvenance`** (`FilletProvenance.cs`) — the typed discriminator (`TangentNodeProvenance`, `CornerArcProvenance`, `FilletEdgeProvenance`) attached to Legacy nodes/edges/arcs so its cleanup passes can pattern-match instead of parsing origin strings. **V2 does not use it.**

`DetectManualArcNodes` and the eligibility logic are shared (V2 calls `ManualArcDetector` / `FilletEligibility`; Legacy has near-identical inline copies). At the flip, `FilletArcGenerator.cs`, `LegacyFilletArcGenerator.cs`, `FilletProvenance.cs`, the vestigial `FilletArcGeneratorRouter`, and the unused `FilletMode` plumbing are deleted. Per project policy there are **no compatibility shims** — do not add Legacy fixes; fix forward in V2.

---

## Key files

| File | Role |
|---|---|
| `src/Yaat.Sim/Data/Airport/FilletArcGeneratorV2.cs` | V2 entry — ID seeding, per-junction loop, plan→execute→normalize |
| `src/Yaat.Sim/Data/Airport/Fillet/V2/JunctionClassifier.cs` | classify a junction into arms + corners + `JunctionKind` |
| `src/Yaat.Sim/Data/Airport/Fillet/V2/TaxiwayArmBuilder.cs` | walk each incident edge into a `TaxiwayArm` |
| `src/Yaat.Sim/Data/Airport/Fillet/V2/CornerPlanner.cs` | pair arms into `CornerSpec`s / collinear pairs |
| `src/Yaat.Sim/Data/Airport/Fillet/V2/ArmCutResolver.cs` | resolve tangent-cut positions (single + ordered multi-cut) |
| `src/Yaat.Sim/Data/Airport/Fillet/V2/SharedArmTangentPass.cs` | intra-arm coalesce + cross-arm coalesce + cross-junction shared-arm scaling/merge |
| `src/Yaat.Sim/Data/Airport/Fillet/V2/FilletPlanBuilder.cs` | assemble the immutable `FilletPlan` |
| `src/Yaat.Sim/Data/Airport/Fillet/V2/FilletPlanCutRedirect.cs` | union-find survivor map + stable-anchor redirect |
| `src/Yaat.Sim/Data/Airport/Fillet/V2/FilletEdgeSplitPlanner.cs` | order-independent global edge-split |
| `src/Yaat.Sim/Data/Airport/Fillet/V2/FilletPlanExecutor.cs` | materialize cuts, emit edges/arcs/chords, remove junctions |
| `src/Yaat.Sim/Data/Airport/Fillet/V2/FilletPlan.cs` | the plan record + `SurvivingEdgeOp` / op records |
| `src/Yaat.Sim/Data/Airport/Fillet/V2/CutId.cs` | `readonly record struct CutId` — planning ID, type-distinct from node IDs |
| `src/Yaat.Sim/Data/Airport/Fillet/V2/FilletEndpoint.cs` | sealed `Cut(CutId) \| Node(int)` union for redirected endpoints |
| `src/Yaat.Sim/Data/Airport/Fillet/V2/FilletPlanConsistency.cs` | fail-fast plan validation (cut + node references) |
| `src/Yaat.Sim/Data/Airport/Fillet/FilletGeometry.cs` | shared bezier construction + radius math |
| `src/Yaat.Sim/Data/Airport/Fillet/FilletConstants.cs` | shared thresholds (radius floor, coincident, gaps, radii) |
| `src/Yaat.Sim/Data/Airport/Fillet/FilletEligibility.cs` | shared intersection eligibility + preserve rule |
| `src/Yaat.Sim/Data/Airport/Fillet/ManualArcDetector.cs` | shape-point (manual-arc) exclusion |
| `src/Yaat.Sim/Data/Airport/Fillet/FilletGraphNormalizer.cs` | post-execute recompute + defensive merge (no repair) |
| `src/Yaat.Sim/Data/Airport/IFilletArcGenerator.cs` · `FilletMode.cs` · `FilletGeneratorFactory.cs` · `FilletArcGeneratorRouter.cs` | selection surface |
| `src/Yaat.Sim/Data/Airport/GeoJsonParser.cs` | invokes the chosen generator at parse Step 8 (`:288`) |
| `src/Yaat.Sim/Data/Airport/AirportGroundLayout.cs` | `GroundArc` (bezier fields, `TaxiwayNames`, `MatchesTaxiway`), `GroundNode`, `GroundEdge` |
| `src/Yaat.Sim/Data/Airport/FilletArcGenerator.cs` · `LegacyFilletArcGenerator.cs` · `FilletProvenance.cs` (legacy) | V1 — being deleted |
| `tests/Yaat.Sim.Tests/Fillet/FilletV2CornerSpanGuardTests.cs` | the cut-ID-collision tripwire (≤ 300 ft corner spans) |

**Plan docs (transient, this doc supersedes them):** `docs/plans/filletv2/{status,v2-implementation,v2-divergences,v2-sim-validation}.md`, `docs/plans/ground-graph-v2.md` (Workstream 1). The `v2-implementation.md` executor/data-types/module-layout sections are explicitly historical; its "Connectivity rewrite" section + `v2-divergences.md` are the authoritative legacy-of-record.

**Tooling:** `tools/Yaat.LayoutInspector` with `--fillet-mode none|legacy|v2` inspects the chosen graph; `--node N`, `--dump`, `--html [--ticks]`, and `--debug-fillets` (verbose fillet debug logging) are the workhorses for ground/exit/taxi debugging.

## Related docs

- [`./pathfinder.md`](./pathfinder.md) — consumes the filleted graph to build `TaxiRoute`s (membership-arc handling, mandatory-connector insertion).
- [`./navigator.md`](./navigator.md) — compiles each `GroundArc` into a `PathPrimitiveArc` and follows it closed-form (Bezier-as-true-circle, `MaxSafeSpeedKts`).
- [`../landing-and-runway-exit.md`](../landing-and-runway-exit.md) — runway-exit geometry that interacts with high-speed-exit fillet radii.
