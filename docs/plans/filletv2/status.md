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

## Where production stands

**Production still runs Legacy.** `AirportLayoutDownloader` → `GeoJsonParser.Parse(...)` defaults to
`FilletMode.Legacy`. V2 is **geometry-validated only** (the comparison gate + interface/router +
synthetic `ArmCutResolver` tests) — **never sim-validated**: no taxi/landing/replay test runs aircraft
on a V2-filleted graph. Runtime switch exists (`FilletArcGeneratorRouter.UseV2` / `FilletMode.V2` via
`FilletGeneratorFactory`).

> **Two independent "V2"s — do not conflate.** This is **fillet V2** (arc generator). Separate effort:
> **TaxiPathfinder V2** (`TaxiPathfinderRouter.UseV2`, `docs/plans/pathfinderv2/`). The
> `Issue165SkwTaxiSpinTests` "UseV2" reference is the *pathfinder*, not fillet. Both reshape the ground
> graph, so a fillet default-flip interacts with the pathfinder default-flip — sequence them together.

## Next phase — default-flip campaign

Goal: make V2 the default and delete Legacy. Approach (locked): **validate behind the switch first**
(Legacy stays default), **fillet before pathfinder** (pathfinder stays V1 so failures isolate to fillet).

1. **Validate in the real sim, behind the switch — IN PROGRESS (green so far).** Sim-level gate added:
   the OAK + SFO + FLL taxi-coverage smoke set + landing/exit scenarios run on `FilletMode.V2` layouts
   with the V1 pathfinder. All 36 V2 tests pass (FLL also green on the Legacy baseline); full non-nightly
   Sim suite green (5528 pass / 1 skip / 0 fail). See [`v2-sim-validation.md`](./v2-sim-validation.md).
   Full-suite-on-V2 sweep done (throwaway flip, reverted): **5 failures**, all real V2 regressions, no
   brittle Legacy-pinned assertions. Triaged in `v2-sim-validation.md`. **Blockers before the flip:**
   (a) edge-split reversal stubs at FLL B/C1 + SFO 43/1160 (3 tests — `X→Y` then `Y→X` in the surviving
   edge set), (b) navigator slow-turn synth tolerance too tight for tight V2 arcs (AMX669), (c) one
   replay cascade expected to clear after a/b.
2. **Fix the triaged blockers**, re-run the sweep, then **flip default** to `FilletMode.V2` in
   `GeoJsonParser.Parse` overloads + `AirportLayoutDownloader`.
3. **Aviation-realism review** (MANDATORY) on radius/preserve semantics → turn-speed sign-off.
4. **Delete Legacy** — `FilletArcGenerator.cs`, `LegacyFilletArcGenerator`, `FilletProvenance`, unused
   `FilletMode` plumbing; retire the vestigial `FilletArcGeneratorRouter` (no `src/` consumers today).

### Gotchas for the next agent

- Corner-arc/straight-connector endpoint resolution in `FilletPlanExecutor.ResolveId` uses cut-id-first
  lookup (a latent cut-id vs node-id keyspace overlap); `SurvivingEdgeOp` uses discriminated endpoints so
  it's unaffected. Works on gate airports; revisit only if a collision surfaces.
- The **degenerate corner arc → straight chord** fallback in the executor is load-bearing for
  connectivity — don't remove it.
- `v2-implementation.md` executor/data-types/module-layout sections are historical (banner added); its
  "Connectivity rewrite" section + `v2-divergences.md` are authoritative for connectivity.
