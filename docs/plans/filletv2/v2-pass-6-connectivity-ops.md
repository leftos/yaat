# Fillet V2 — pass 6 (connectivity ops)

**Status:** Authoritative addendum to [`v2-implementation.md`](./v2-implementation.md) step 3f. No new geometry algorithm — only the plan surface deferred from pass 5.

**Context:** [`v2-patchwork-vs-planning.md`](./v2-patchwork-vs-planning.md) — executor/normalizer heuristics during 3f were the wrong layer. The harness exists to **find planner gaps**, not to be made green by patching execute.

---

## Rule

Every time you reach for an executor heuristic, **stop**, name the missing `FilletPlan` op, add it in `FilletPlanBuilder` / `ArmCutResolver` / `FilletConnectivityPlanner`, and keep `FilletPlanExecutor` literal.

Patches in `FilletPlanExecutor` or `FilletGraphNormalizer` mean the planner is **under-specified** — not a problem to solve at the patch site. If hard gates still fail after adding named ops, **name the next missing op** instead of patching around it.

---

## Work order

1. **Commit** doc + harness in isolation (no executor / normalizer band-aids). `Compare_LegacyVsV2_MeetsHardGates` fails on OAK/SFO/FLL — that is the planner gap surfacing correctly.
2. **Revert** executor/normalizer patches (`arm-bypass`, `corner-connector`, `ConnectMissingTangentPairs`, `ReconnectOrphanedEdges`, `RescueArcOnlyV2TangentNodes`).
3. **This document** — enumerate ops + completeness contract.
4. **Implement** ops in the planner; unit tests assert **plan shape** (not only executed layout). Example: junction X with parking spur Y → one `ReconnectEdgeOp(Y → cut K)`.
5. **Rerun** hard gates. On failure → name the next op; do not patch execute.

---

## New plan ops

### `StraightConnectorOp`

**When:** A `CornerSpec` at junction J was resolved with owning cuts on both arms but **no** `CornerArcOp` (degenerate radius, demotion, or `cutA == cutB` skip).

**Semantics:**

| Field | Rule |
|-------|------|
| Endpoints | `CutIdAtArmA`, `CutIdAtArmB` from resolver maps — **resolved cut positions**, not `IdealTangentFt` |
| Geometry | Straight `GroundEdge` between the two tangent nodes at execute time |
| Taxiway | If `EdgeA.SharesTaxiway(EdgeB)` → that taxiway; else `EdgeA.TaxiwayName` (same rule as corner-arc same-taxiway branch) |
| Not a substitute for | `CornerArcOp` when radius ≥ floor |

**Owner:** `ArmCutResolver` (or `FilletConnectivityPlanner` called from builder immediately after resolve).

---

### `ArmBypassOp`

**When:** Junction J is processed (arm roots in `EdgesToRemove`), arm A has **zero** cuts on that junction, and J is not `PreserveNode` (junction removed).

**Semantics:**

| Field | Rule |
|-------|------|
| Endpoints | `RemoteNodeId` = `RootEdge.OtherNode(J)`; `TerminalNodeId` = `arm.TerminalNode` |
| Geometry | Single straight edge replacing the removed root edge, bypassing deleted J |
| Taxiway / runway | From `arm.RootEdge` |

**When not:** `PreserveNode` — preserve/collinear paths in executor already stub J to arms.

**Owner:** `FilletPlanBuilder` (has layout + junction + cut result).

---

### `ReconnectEdgeOp`

**When:** Layout edge E touches junction J, E is **not** an arm root (not already in `EdgesToRemove`), and J is filleted (in `junctionPlans`).

**Semantics:**

| Field | Rule |
|-------|------|
| `OtherNodeId` | Non-junction endpoint of E (e.g. parking node) |
| `TargetCutId` | Nearest cut on the arm whose `TaxiwayName` matches `E.TaxiwayName`; tie-break by minimum distance from `OtherNode` to cut position along graph |
| Multi-cut arms | Pick cut on matching arm only; do not attach parking to wrong arm’s tangent |
| `PreserveNode` | Target may be J itself if J is in candidate set; if J is removed, target must be a cut tangent, never a soon-deleted J |
| Consumption | E is added to `EdgesToRemove`; executor adds replacement edge `OtherNode → tangent(TargetCutId)` |

**Owner:** `FilletConnectivityPlanner` in `FilletPlanBuilder.Build(layout, …)`.

---

## Completeness contract

For every junction J that appears in `FilletPlan` (has cuts, corner arcs, collinear ops, or `JunctionNodesToRemove`):

1. **Arm roots:** Every `arm.RootEdge` ∈ `EdgesToRemove` has either arm-cut chain ops, `ArmBypassOp`, or collinear/preserve executor path documented in plan.
2. **Corners:** Every `CornerSpec` at J has exactly one of: `CornerArcOp`, or `StraightConnectorOp`, or explicit `PlanWarning` with no connectivity obligation (`NO_OWNING_CUT` with no cuts).
3. **Non-arm edges:** Every edge touching J not in `EdgesToRemove` has a `ReconnectEdgeOp` unless J is preserved and the edge is handled by `PreserveStubOp` / preserve-collinear (future: stub list).
4. **No orphan tangents:** Every cut in `Cuts` at J participates in at least one: arm chain edge, `CornerArcOp`, `StraightConnectorOp`, `ReconnectEdgeOp` target, or `TangentMergeOp` survivor chain. If not → plan incomplete (add op or merge), not normalizer rescue.

**Harness:** Stable pre-fillet node IDs reachable from hold shorts must match Legacy vs V2 when the contract holds. Parking → hold-short set equality unchanged.

---

## Executor (literal only)

Execute in junction loop, in order:

1. Materialize cuts → tangent nodes
2. Arm cut chains (`ArmCutOp` / existing shorten/sub/tail)
3. `CornerArcOp` → `GroundArc`
4. `StraightConnectorOp` → `GroundEdge` between cut tangents
5. Collinear / preserve (unchanged)
6. `ArmBypassOp` → `GroundEdge`
7. `ReconnectEdgeOp` → `GroundEdge`, mark source edge consumed
8. Remove `EdgesToRemove` + junction per plan

**Forbidden at execute:** nearest-neighbor search, “if no arc add edge”, tangent rescue, clique wiring.

**Normalizer:** Recompute distances, coincident merge backstop, dangling ref prune only — no `RescueArcOnlyV2TangentNodes`.

---

## Tests (plan shape)

| Test | Assert |
|------|--------|
| 90° single corner | No `StraightConnectorOp`; 2 cuts; 1 `CornerArcOp` |
| Demoted / degenerate corner with cuts | `StraightConnectorOp` with expected cut ids; no `CornerArcOp` |
| Junction + parking spur | Exactly one `ReconnectEdgeOp(parkingId, targetCutId)`; spur edge in `EdgesToRemove` |
| Arm with no cuts, J removed | One `ArmBypassOp` per cutless arm |
| Two adjacent junctions, shared arm merge | `ArmChainEdgeOp` uses survivor cut ids only; no `TerminalNodeId` to removed junction |
| `FilletPlanBuilder` on synthetic layout | Contract rows 1–3 for built plan |

Execution/layout parity remains `Compare_LegacyVsV2_MeetsHardGates` (expected fail until contract satisfied).

---

## Round 2 planner gaps (post pass-6 follow-up)

See [`v2-divergences.md`](./v2-divergences.md), [`claude-response.md`](./claude-response.md), [`cursor-progress.md`](./cursor-progress.md). **Three** gaps (+ deferred spur policy).

| Priority | Fix | When |
|----------|-----|------|
| 1 | Tighten **`TryResolveSharedJunctionFarCut`**; eliminate silent empty branch `FilletArmChainPlanner:73-79` | Cross-junction `lastCut→farCut` already exists when match succeeds |
| 2 | **`ArmCutResolver`** sub-threshold cut filter (`DistanceAlongArmFt < CoincidentNodeThresholdFt`) | Degenerate `V2:shorten@*` post-normalizer merge |
| 3 | **`JunctionIncidentEdgeOp`** | Missing node refs |
| defer | **`JunctionPromoteToPreserveOp`** | After gate rerun; optional if `NoOwningCut` still high |
| if needed | **`SharedArmConnectorOp`** | Only if (1) does not close reachability |

---

## What we avoid

If executor patches stay, step 5 (delete legacy) is hollow — same repair class under new names ([`v2-patchwork-vs-planning.md`](./v2-patchwork-vs-planning.md)).

## Cost

3f extends by one planning pass. Decisions deferred from pass 5 are decided here (cut vs ideal positions, parking→which tangent). Algorithm work (3a–3e) is done; remaining work is **connectivity bookkeeping** once this contract is written.
