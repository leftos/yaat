# Fillet V2 — active status

**Owner:** Claude (direct implementation; the `claude-response.md` ↔ `cursor-progress.md` handoff loop has ended — those two files are historical).
**Spec + learnings:** [`v2-implementation.md`](./v2-implementation.md) ("Connectivity rewrite" section).
**Gate:** `Compare_LegacyVsV2_MeetsHardGates` (FLL/OAK/SFO) in `tests/Yaat.Sim.Tests/Fillet/`.

## Strategy

1. ~~Reach structural-green first, commit a checkpoint.~~ **Done** (commit `a7b0e963`).
2. **Connectivity-layer rewrite (global edge-split) — landed.** `Compare_LegacyVsV2_MeetsHardGates`
   passes FLL/OAK/SFO under the no-true-disconnection gate.

Plan doc: [`../../../../../Users/Leftos/.claude/plans/i-ve-had-a-lot-sorted-mccarthy.md`] (Workstreams A/B/C).

### What changed (Workstream A)

- New pure `FilletEdgeSplitPlanner`: split each original edge once by its cuts; drop only the
  removed-junction stub; degenerate corner arc → straight chord. Emits `SurvivingEdgeOp`.
- `FilletPlanExecutor` rewritten to materialize the surviving-edge list once (no per-junction
  mutation loop), removing the order-dependence and the create-then-strip bug class.
- `FilletPlanBuilder` calls the edge-split instead of `FilletArmChainPlanner` /
  `FilletConnectivityPlanner` (those are now dead — removed in the `ref:` commit).
- `CornerArcOp` carries `JunctionNodeId` (corner ids are per-junction, not global — fixed an
  arc-to-junction mis-match that silently corrupted arc geometry).
- `FilletGeometry.BuildBezier` projects control points toward the junction (was projecting away
  → S-cusps / `minR≈0` arcs the normalizer deleted).
- Gate redefined to no-true-disconnection (structural + repair-zero + parking-match + no node
  present in both layouts reachable in only one).

Keep: geometry layer (`TaxiwayArmBuilder`, `JunctionClassifier`, `CornerPlanner`,
`ArmCutResolver`, `FilletGeometry`, `TaxiwayWalk`), gate harness, `FilletReachabilityDiagnostics`.

## Gate result (edge-split)

`Compare_LegacyVsV2_MeetsHardGates` **passes** FLL/OAK/SFO: structural ok, repair counters
zero, parking reachability matches, no true disconnection. The remaining hold-short stable-set
differences are accepted classification divergences (8 marginal junctions, see
[`v2-divergences.md`](./v2-divergences.md)) — each present in exactly one layout, parking 100%
reachable both ways.

## Commits (done)

1. **`feat:`** edge-split planner + executor + gate redefinition + obsolete-test cleanup (`2d706b38`).
2. **`ref:`** delete dead connectivity layer + dependent diagnostics.
3. **`fix:`** clean-arc bezier (honest arc radius) — Workstream C correctness fix.

## Workstream outcomes

- **A (connectivity):** done — hard gate green on FLL/OAK/SFO (no-true-disconnection).
- **B (runway bearing):** done — `CompareRunwayBearings` reports 0 mismatches; the edge-split's
  orientation preservation + the toward-junction bezier fixed `#39↔#644` with no dedicated change.
- **C (corner radius):** clean-arc fix applied (honest geometry; mismatch count down ~25–35%).
  Residual is an **accepted bidirectional policy difference** (see [`v2-divergences.md`](./v2-divergences.md)) —
  not pursued, as full parity would clone legacy's radius computation + relax the overrun-preventing
  cut caps. Soft gate; hard gate unaffected.
