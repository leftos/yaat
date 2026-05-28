# Fillet V2 — divergence and triage log

Track every Legacy vs V2 mismatch during parity work. Classify each entry as **accepted improvement**, **V2 bug**, or **legacy-only quirk**.

**V2 algorithm:** [`v2-implementation.md`](./v2-implementation.md)

## Edge-split rewrite — current status (supersedes the pass-6 connectivity sections below)

The connectivity layer is now a single order-independent **global edge-split**
(`FilletEdgeSplitPlanner`): each original edge is split once by the cuts that land on it,
only the stub incident to a removed junction is dropped, every other sub-segment is kept,
and a degenerate corner arc (radius < floor) degrades to a straight chord so connectivity
never depends on a fragile arc. `BuildBezier` control points now project toward the junction
(into the corner) instead of back out along the arm.

`Compare_LegacyVsV2_MeetsHardGates` **passes** on FLL/OAK/SFO. The gate is now a
**no-true-disconnection** gate: structural valid + repair counters zero + parking→hold-short
reachability match + **no node present in both layouts reachable in only one**. Exact
hold-short stable-set equality is reported but not required.

### Accepted classification divergences (not connectivity bugs)

Each generator dissolves (fillets away) a slightly different set of marginal junctions. Every
divergent node is present in exactly one layout — there are zero true disconnections, and
parking reachability is identical (100%). **Class: accepted — V2 clean-room eligibility.**

| Airport | Nodes | Direction | Meaning |
|---------|-------|-----------|---------|
| FLL | 105, 106, 357 | only-legacy (`inLegacy`, not `inV2`) | V2 fillets these (B×B12 short X-crossings; J8 runway crossing); legacy keeps the vertex |
| OAK | 204, 217, 222 | only-v2 (`inV2`, not `inLegacy`) | V2 preserves these; legacy fillets them away |
| SFO | 104, 535 | only-v2 | V2 preserves these; legacy fillets them away |

Decode harness: `FilletReachabilityDiagnosticTests`. The `Fillet:phase-d-*` "gap-next" nodes in
the decode are legacy tangent artifacts — ignore them; the gate compares pre-fillet node ids.

## Shipped interface (steps 1–2 — do not replan)

Implemented under `src/Yaat.Sim/Data/Airport/`: `IFilletArcGenerator`, `FilletMode`, `FilletGeneratorFactory`, `FilletArcGeneratorRouter`, `FilletArcGeneratorRegistry.All` = `none` + `legacy` until V2 works, `GeoJsonParser.Parse(..., FilletMode)` default `Legacy`. Comparison: `tests/Yaat.Sim.Tests/Helpers/LayoutCloner.cs`, `FilletComparison.cs`.

## Step 1 follow-ups (addressed or scheduled)

| Item | Status | Notes |
|------|--------|-------|
| `FilletStatistics.cs` in `Fillet/` with `Yaat.Sim.Data.Airport` namespace | **Fixed** | Moved to `src/Yaat.Sim/Data/Airport/FilletStatistics.cs` |
| `FilletArcGeneratorV2` in `Registry.All` while `Apply` throws | **Fixed** | V2 omitted from `All` until step 3; factory still exposes `FilletMode.V2` |
| V2 `NotImplementedException` message | **Fixed** | Single clear sentence |
| LayoutInspector `--fillet` / `--fillet-diff` | **Open** | Step 4 |

## Step 2 harness gaps (step 3f)

| Gap | Status | Notes |
|-----|--------|-------|
| Corner-bucket min-radius diff | **Fixed (soft)** | `FilletComparisonGates` ±10% bucket compare; triage in airport sections below |
| Runway centerline bearing diff | **Fixed (soft)** | Indexed; not hard-fail in `Compare_LegacyVsV2_MeetsHardGates` |
| Structural validity checks | **Fixed** | `ValidateStructural` on all generators |
| Connectivity gates | **Fixed (metric)** | Stable pre-fillet node IDs reachable from hold shorts + parking→hold-short set equality |
| `LayoutCloner` `GroundRunway` fields | **Open** | Fillet-scoped clone; extend if reused outside fillet compare |

## Parity runs (3f)

V2 is **runnable** (step 3e). Pass **6** adds plan ops (`ArmBypassOp`, `StraightConnectorOp`, `ReconnectEdgeOp`) — see [`v2-pass-6-connectivity-ops.md`](./v2-pass-6-connectivity-ops.md).

`Compare_LegacyVsV2_MeetsHardGates` **fails** OAK/SFO/FLL as expected until the planner contract is complete. Do not patch execute/normalizer to green the test.

| Airport | V2 structural | Hold-short stable match | Parking match | Notes |
|---------|---------------|-------------------------|---------------|-------|
| FLL | FAIL (edges ref removed node ids 3–6) | 20 only-legacy | OK | Execute no longer throws; planner gaps remain |
| OAK | FAIL (degenerate 0 ft edges; edge S refs 203/850) | 50 only-legacy | OK | `NO_OWNING_CUT=1` on at least one spur |
| SFO | FAIL (degenerate 0 ft edges on taxiway A) | 56 only-legacy | OK | Same degenerate class as OAK |

### Pass-6 follow-ups (landed)

| Item | Status | Notes |
|------|--------|-------|
| `SelectTargetCut` taxiway match only | **Fixed** | No nearest-cut fallback; `PlanWarning(NoOwningCut)` + spur left out of `EdgesToRemove` unless `PreserveNode` |
| `ArmChainEdgeOp` + merge redirect | **Fixed** | `FilletArmChainPlanner` + `FilletPlanCutRedirect`; `PruneCuts` syncs `CutId` to survivor key; executor keys `cutToNode` by dictionary key |
| Shared-arm tail to filleted junction | **Fixed** | Chain to far junction near cut via `TryResolveSharedJunctionFarCut`; omit tail when far junction filleted and cuts merged |

**Next planner gaps (name before patching — do not executor-patch):**

| # | Name | Symptom | Notes |
|---|------|---------|-------|
| 1 | **`JunctionIncidentEdgeOp`** (or stricter `EdgesToRemove` contract) | Edges ref deleted intersection ids (FLL 3–6; OAK edge S / 203–850) | Every edge incident to `JunctionNodesToRemove` must be in `EdgesToRemove` or replaced before execute. |
| 1 | **Shared-arm cross-junction** (`TryResolveSharedJunctionFarCut` + empty branch) | 20–56 only-legacy | Connector exists at `FilletArmChainPlanner:64-71` when far cut resolves; **silent no-op at L73-79** when it does not. Tighten match first; `SharedArmConnectorOp` only if needed. See [`claude-response.md`](./claude-response.md). |
| 2 | **Sub-threshold cut filter in `ArmCutResolver`** | Degenerate self-loops (`V2:shorten@J*`) | Origin decode: shorten dominates. Loops appear **post-normalizer** coincident merge, not same id at `AddEdge`. Fix: do not emit cuts with `DistanceAlongArmFt < CoincidentNodeThresholdFt`. |
| 3 | **`JunctionIncidentEdgeOp`** | Missing node refs on edges | `EdgesToRemove` completeness. |

**Deferred:** `JunctionPromoteToPreserveOp` (option a) / `DroppedSpurOp` — after gap 1–3 rerun; may be unnecessary if `NoOwningCut` rate drops.

`ArmChainEdgeOp` + merge redirect is **done**.

### Degenerate self-loop decode (OAK/SFO diagnostic)

Run: `FilletDegenerateEdgeDiagnosticTests`.

| Finding | Detail |
|---------|--------|
| Dominant `Origin` | **`V2:shorten@J*/{twy}`** — OAK `T 753→753` → `V2:shorten@J149/T`. |
| Mechanism (corrected) | Cut at very small `DistanceAlongArmFt`; tangent within 5 ft of remote; **`FilletGraphNormalizer.MergeCoincidentNodesDefensive`** after execute rewrites edge to same node. Not same id at executor (`idCounter` allocates fresh tangent). |
| Fix layer | **`ArmCutResolver`** — filter sub-threshold cuts at creation. Not redirect-time shorten suppress unless resolver insufficient. |

### `NoOwningCut` policy (decision pending implementation)

| Option | Op name | Verdict |
|--------|---------|---------|
| (a) Promote junction to preserve | **`JunctionPromoteToPreserveOp`** | **Recommended** — spur uses junction node; no invented taxiway semantics. |
| (b) Drop spur | **`DroppedSpurOp`** | Traceability only; loses connectivity. |
| (c) Nearest cut any taxiway | — | **Rejected** (same class as old patchwork). |

### Expected comparison deltas (not bugs)

| Topic | Legacy | V2 | Triage |
|-------|--------|-----|--------|
| Plan-time radius gate | Cubic `MinRadiusOfCurvatureFt` from tangent positions | `EffectiveMinRadiusFt` = `min(ta,tb)/tan(turn/2)` (geometric) | **Accepted** for gating; execution still uses `BuildBezier` for `GroundArc.MinRadiusOfCurvatureFt`. Symmetric cuts match; asymmetric junctions may show corner-bucket deltas under the ±10% harness — interpret as plan-vs-bezier divergence, not connectivity failure. |

### Template

```markdown
### [AIRPORT] — short description

- **Class:** accepted improvement | V2 bug | legacy-only quirk
- **Metric:** e.g. arc count, corner bucket, BFS reachability
- **Legacy:** …
- **V2:** …
- **Action:** …
```
