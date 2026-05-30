# Ground Graph V2 — transition main plan

The airport ground stack is moving from V1 to V2 across **three layers that must flip together**. This
file is the entry point and status tracker for the whole transition; each layer keeps its detail in its
own sub-plan (linked below). A fresh agent should be able to start here and find where to continue.

## The three layers

| # | Layer | Role | Component | Sub-plan | Status |
|---|-------|------|-----------|----------|--------|
| 1 | **Fillet generator V2** | builds the ground-graph **geometry** (nodes, edges, arcs + radii) | `FilletArcGenerator` V2 | [`filletv2/`](./filletv2/status.md) | geometry + behind-switch validation **done**; flip gated |
| 2 | **Pathfinder V2** | resolves a `TaxiRoute` over that graph | `TaxiPathfinder` V2 router | [`pathfinderv2/`](./pathfinderv2/default-flip-triage.md) | **WIP** (default-flip triage open) |
| 3 | **Navigator V2 (clean-room)** | **follows** the route+geometry per tick (steering) | `GroundNavigatorV2` behind `GroundNavigatorRouter` | [`navigator-v2/design.md`](./navigator-v2/design.md) | **design reviewed**; impl not started |

> Naming: layer 1 is the *fillet generator* (a.k.a. "generator v2" / "fillet v2"). Layer 3 was originally
> scoped as an *incremental* v1.1 update; the navigator-WS3 failure cluster (freeze + spins) showed the
> Legacy-fillet compensations misfire on V2 geometry, so it is now a **clean-room V2 navigator behind a
> `GroundNavigatorRouter`** — the same clean-V2-behind-a-switch pattern as layers 1 and 2 (design
> reviewed: [`navigator-v2/design.md`](./navigator-v2/design.md)). All three are versioned independently
> but **ship as one switch-over** — see the joint flip gate below.

## Why they flip together

Each layer consumes the previous one's output, and the layers were co-tuned against V1 geometry:

- Pathfinder V2 work is what **prompted** the fillet generator V2 rewrite (V1 fillets produced zero-distance
  / reversed edges the router tripped on).
- Fillet V2's **tighter, cleaner arcs** then exposed latent faults in the **pathfinder** (edge-selection at
  collapsed junctions) and the **navigator** (slow-turn synthesis tolerance) — found by the
  full-suite-on-V2 sweep (see [`filletv2/v2-sim-validation.md`](./filletv2/v2-sim-validation.md)).
- The GroundNavigator carries heavy **Legacy-fillet-specific tuning** (orbit detector, cluster synth
  planner, chord-chain aggregate turn, reverse-arc detection) that must be re-evaluated against V2 geometry.

Flipping any one alone leaves the stack mismatched. The transition completes when all three are green and
flip in a single change, after which V1 of each is deleted.

## Status at a glance

| Workstream | State | Next action |
|------------|-------|-------------|
| Fillet generator V2 | geometry validated; sim-validated behind switch; sweep triaged | hold for layers 2+3, then flip + delete Legacy |
| Pathfinder V2 | WIP, default reverted (56-failure triage open) | work the cluster triage + Codex HIGH findings + fillet-sweep req ① |
| Navigator V2 (clean-room) | design reviewed | build it: §4.4 arc-cap fix first, then interface+router extraction, then `GroundNavigatorV2` |
| **Joint flip** | blocked on 2 + 3 | flip all three together once green |

---

## Workstream 1 — Fillet generator V2 (geometry)

Sub-plan: [`filletv2/status.md`](./filletv2/status.md) · spec [`filletv2/v2-implementation.md`](./filletv2/v2-implementation.md) · divergences [`filletv2/v2-divergences.md`](./filletv2/v2-divergences.md) · sim gate [`filletv2/v2-sim-validation.md`](./filletv2/v2-sim-validation.md)

- [x] Order-independent global edge-split connectivity rewrite (replaces the rotten chain-planner)
- [x] No-true-disconnection gate green on FLL/OAK/SFO
- [x] Runway-bearing parity (0 mismatches); corner-radius clean-arc fix (residual = accepted policy diff)
- [x] Sim-validation behind the switch: OAK/SFO/FLL taxi-coverage + landing/exit on `FilletMode.V2`
- [x] Full-suite-on-V2 sweep (throwaway flip) + triage → 5 failures, all in routing/navigation layer
- [ ] **(gated)** Flip default to `FilletMode.V2` in `GeoJsonParser.Parse` overloads + `AirportLayoutDownloader` — only after layers 2 + 3 are green
- [ ] Delete Legacy generator (`FilletArcGenerator`, `LegacyFilletArcGenerator`, `FilletProvenance`) + retire the vestigial `FilletArcGeneratorRouter` (no `src/` consumers today)

## Workstream 2 — Pathfinder V2 (routing)

Sub-plan: [`pathfinderv2/default-flip-triage.md`](./pathfinderv2/default-flip-triage.md) · design [`pathfinderv2/design.md`](./pathfinderv2/design.md) · requirements [`pathfinderv2/requirements.md`](./pathfinderv2/requirements.md)

- [ ] Triage the 56-failure cluster from the last default-flip (clusters A–K) — verdict each (V2-bug / V1-pinned / missing-feature / underlying-sim)
- [x] Codex HIGH findings — 4 of 5 done: DestinationRunway hold-short reason (parity with V1; was auto-crossing the destination runway), reciprocal-runway matching (`RunwayIdentifier.Contains`), full-length lineup end (authoritative `NavigationDatabase` threshold, deleted the centroid coin-flip), detour authorized-taxiway policy (soft penalize-and-warn + enforced `MaxDetourExpansions`)
- [x] **Codex HIGH #5 — state-aware A\* pruning:** `AutoRouter.RunAstar` + `SegmentExpander.LocalSearchToJunction` now key the closed set by `(nodeId, 1°-bearing-bucket)` via `GeometricAdmissibility.PruningStateKey` using the propagated arrival bearing. Necessity proved by a dense V2-fillet oracle sweep (`StateAwarePruningNecessityTests`, 8,294 OAK/SFO/FLL pairs): node-id keying → 10 false `DestinationUnreachable` (OAK `S8B`, FLL `SHE4`) + 785 sub-optimal routes; fix → 0/0/0. Sweep promoted to a standing guard; synthetic + real (`OAK 28R→S8B`, `FLL 10L→SHE4`) repros in `StateAwarePruningTests`. Latency ~1.8× on a sub-3ms op (negligible)
- [x] **Fillet-sweep requirement ①:** single-name continuation now beats a membership-only taxiway-junction arc in BOTH walkers — `WalkToNaturalTerminus` (hard tier) and `LocalSearchToJunction` (soft penalty `MembershipJunctionArcContinuationCostNm` on a membership-arc continuation; runway-crossing arcs excluded via `GroundArc.IsMembershipTaxiwayJunctionArc`). Reproduced via a dense two-token sweep (29 real diversions across SFO/FLL → 0); guarded by `Req1MembershipArcSweepTests` + `JunctionContinuationTests.IntermediateWalk_*`
- [x] **Fillet V2 duplicate corner arcs — planning-layer fix:** the single-name + membership-twin corner arcs (e.g. `[M1]`/`[M1 - M5]`, `[A - RAMP]` ×2) came from the post-execute normalizer merging same-junction cross-arm coincident nodes. Moved that merge into the plan (`SharedArmTangentPass.ApplyCrossArmCoalesce`) + dedup corner-arc/straight ops by resolved endpoint pair in `FilletPlanBuilder` (prefer single-name). Guard: `FilletV2CornerSpanGuardTests.V2_CornerArcs_NoDuplicateNodePairs`. No executor band-aid; the normalizer no longer merges same-junction nodes
- [x] **Retire the post-hoc node-merge (full):** deleted `FilletGraphNormalizer.MergeCoincidentNodesDefensive` entirely — the normalizer now only recomputes distances, drops self-loops/degenerate arcs, and sweeps isolated nodes. Cross-junction coincident tangent cuts are merged in the plan (`SharedArmTangentPass.ApplyGlobalCoincidentCutCoalesce`, run after `ApplyCrossJunction` so it sees scaled positions; union-find absorbs overlap with the intra-arm/cross-arm/cross-junction passes). The one non-cut coincidence (SFO `01R/19L` centerline projection landing 2.4 ft from a taxiway intermediate) is fixed at its source: `RunwayCrossingDetector.ResolveCenterlineProjectionNode` reuses a coincident pre-existing intersection instead of minting a node. That exposed a latent issue the post-hoc merge had masked — a reused node carries a `RWY…:link` arm that fillet would curve onto, producing a 0 ft edge-split fragment; fixed by excluding runway-crossing links from fillet arms (`GroundEdge.IsRunwayCrossingLink` → `TaxiwayArmBuilder`). Guards green WITHOUT any post-hoc merge: `V2_NoCoincidentIntersectionNodes`, `V2_EdgeSplit_NoZeroDistanceEdges`, `V2_CornerArcs_NoDuplicateNodePairs` (sfo/oak/fll); #4 necessity HARD=0; req-① sweep 0
- [ ] Decide k-alternative support; coverage tests; latency budget
- [ ] Flip default `TaxiPathfinderRouter` to V2

## Workstream 3 — Navigator V2 (clean-room)

Detail: [`navigator-v2/design.md`](./navigator-v2/design.md) (reviewed by `architect-reviewer` +
`aviation-sim-expert`). Origin of the decision: [`filletv2/v2-sim-validation.md`](./filletv2/v2-sim-validation.md)
§ "Navigator review (root cause ②)" + the navigator-WS3 failure cluster.

The shared `GroundNavigator` carries heavy Legacy-fillet compensations (slow-turn synthesis, cluster
detection, chord-chain aggregate-turn, orbit-stall backstop, reverse-arc tangent-flip) that misfire on
V2's cleaner geometry — the AMX669 freeze and the EDG320/N172SP spins. Rather than re-tune soon-to-be-
deleted shared code under a V1-regression tax, build a **clean V2 navigator behind a `GroundNavigatorRouter`
factory** (default V1 until the joint flip), keeping the durable core (closed-form arc playback, pure-
pursuit, backward-propagated braking, entry alignment, I7) and dropping the chord-chain machinery.

- [x] **§4.4a lateral-accel arc-cap (separable, benefits V1+V2) — DONE.** Replaced `GroundArc.MaxSafeSpeedKts`'s
      kinematic `v=r·ω` cap with a lateral-accel cap `min(√(a_lat·r), CornerSpeedForAngle(category, TurnAngleDeg))`
      floored at `SlowTurnSpeedKts` (a_lat≈0.13 g). Signature `(double turnRate)`→`(AircraftCategory)`; all call
      sites (V1/V2 pathfinder, navigator, LayoutInspector, test helpers) updated; formula unit tests rewritten and
      degenerate-arc diagnostics rewired off the now-floored speed proxy onto radius. V1 suite green (6781/0);
      under V2+V2 the crossing no longer brakes to ~0 (AfterRes momentum preserved) and the 3.0 kt floor removes the
      AMX669 no-motion freeze. *(`GroundTurnRate` 20/25/35 unchanged — the earlier "3°/s" framing was a diagnostic artifact.)*
- [ ] **§4.4b crossing-momentum guard:** §4.4a already preserves ground speed across the crossing→taxiing handoff;
      add the explicit min-gs-through-crossing ≥ ~5 kt regression assertion alongside the Phase-4 V2 navigator work
      (deferred to there so it lands when AfterRes goes green under V2+V2, avoiding a V1-default false-fail).
- [x] **`IGroundNavigator` + `GroundNavigatorRouter` (static factory) extraction — DONE.** All construction
      sites route through the factory; grep gate holds (zero `new GroundNavigator`/`FromSnapshot` outside the
      router); B2 `OverrideTargetPosition` seam replaces the mutable `TargetLat/Lon` setters; V1 stays default.
      Pure refactor — full cross-repo suite green (Sim 6781, Client 772+70, Server 618).
- [x] **`GroundNavigatorV2` skeleton — DONE.** Clean-room extraction behind `GroundNavigatorRouter.UseV2`
      (default V1): keeps the durable core (closed-form arc/slow-turn playback, pure-pursuit + pre-turn blend,
      backward-propagated braking, entry alignment, I7 floor), drops the Legacy compensations (slow-turn
      synthesis, `TryDetectCluster`, chord-chain `EffectiveTurnAngleAt` → single-corner read, orbit-stall).
      Builds clean; under V2+V2 it is behaviorally equivalent to V1 on the cluster (same 17/20, identical
      AfterRes trace) — the verbatim-core extraction introduces no regressions. The 3 residuals live in the
      *kept* core, so they're the iterate-to-green step below, now doable without a V1-regression tax.
- [ ] **Iterate the V2+V2 cluster to green (step 5):** the 3 residuals are kept-core tuning, not dropped-
      compensation artifacts — EDG320 (entry-align 109° + pure-pursuit overshoot → spin), N436MS (following
      too slow), AfterRes (crosses with momentum but reaches the hold-phase ~2 s late). Fail-first repro per
      anchor test; aviation review on any speed/turn change.
- [ ] Validate against the E2E taxi suite under V2+V2 (AMX669, the spin guards, AfterRes crossing,
      S2Oak4, coverage smoke) → 0 failures; **aviation-realism review (mandatory)** of any further
      turn-speed change.

---

## Joint flip gate

The transition is one switch-over, not three. Before flipping:

1. Fillet generator V2 — done (validated behind switch).
2. Pathfinder V2 — green on its own gate (per `pathfinderv2/default-flip-triage.md`).
3. Navigator v1.1 — green (AMX669-class freezes fixed; regression pins re-validated; aviation sign-off).
4. **Re-run the fillet-V2 full-suite sweep** with all three on V2 — expect 0 failures (incl. the N436MS CTO/replay cascade, root cause ③, which should clear once routing + navigation are correct).

Then, in a single change: flip `GeoJsonParser.Parse` default + `AirportLayoutDownloader` (fillet) **and**
`TaxiPathfinderRouter` (pathfinder) to V2, ship the navigator changes, and delete V1 of each layer.

## Current focus / next up

**Reframe (validated):** the full all-V2 test suite is **not** a discovery tool — it hangs (unbounded
"tick until X" ground tests loop forever when an aircraft deadlocks; pathfinder-V2 latency spikes). It is
the **Phase-6 validation gate**, run once when the work is believed done. Drive the work with **targeted,
scoped V2 tests** (pathfinder-V2 *on* fillet-V2 — the ship config, which nothing exercised before).
Key finding: **pathfinder V2 was only ever validated on Legacy fillets**; adapting it to V2's
collapsed-junction geometry is the open Phase-2 work.

Landed (pathfinder V2 on fillet V2):
- [x] `LayoutInspector --fillet-mode legacy|v2|none` (inspect the V2 graph directly).
- [x] **Parking→taxiway bridge** — V2 `SegmentExpander` now bridges a RAMP-only parking start onto the
      first taxiway (`BridgeStartToTaxiway`, mirrors V1 `BfsToTaxiway`). Was the root of cluster-C
      `OAK_TaxiFromParking*` failures.
- [x] **Requirement ① (natural-terminus walk)** — single-name continuation now ranks strictly above a
      membership-only junction arc in `WalkToNaturalTerminus` (SFO `1160` validated, fail-first proven).
- [x] **V-shaped / multi-leg taxiway junction reachability** — `SegmentExpander` now runs a bounded
      recursive look-ahead (`ResolveSequence` + `ProbeTailCost`, mirrors V1 `SelectBestStopNode`): each
      junction candidate is scored by the cost of resolving the *remaining* sequence from it, and the
      whole-airport detour is suppressed inside a probe so a continuation that would need one is a strong
      negative signal. FLL `T T4 B B1 HS 10L` now enters T4 at the apex (#56), walks the NW arm to #682,
      turns onto B and west to the B1 10L hold-short (#339) — no hairpin/backtrack. Also fixed two latent
      variant-resolution bugs surfaced once the route resolved: `IsNumberedVariant` treated `B10`/`B11` as
      variants of `B1` (false positive → spurious ambiguity), and `Run` ran `TryVariantExtension` even when
      the walk already reached the destination hold-short (would back-track to it). `Fll_ResolveExplicitPath_TT4BB1_OnV2`
      now green; full `Pathfinding.V2` suite 88/88, V2-pathfinder grids/sim 69/70 (1 pre-existing skip).
- [x] **Issue #165 (the origin bug) resolved on the ship config** — SKW3404 SFO `TAXI A E B B3 A B1 Z S`
      now honors the instructed taxiway order (stays on B to the B/B3 junction before rejoining A; no
      B3-off-A shortcut) with no 180° spin. Regression test `Sfo_Skw3404_TaxiAEBB3AB1ZS_OnV2`. Resolved by
      the bridge + req ① terminus + look-ahead + the goal-directed multi-candidate search (passes pre- and
      post-look-ahead — it's a guard, not a new fix).
- [x] **Mandatory-connector notification** — when two cleared taxiways have no direct junction (verified via
      `junctionCandidates == 0`), the resolver inserts the connector and emits an informative route
      notification (“A and B1 do not connect directly — taxi via Q”) instead of a misleading
      “not in authorized path” warning. Also stopped warning on RAMP at the parking-bridge/arrival ends and
      on junction-arc segments. SFO A↔B1-via-Q confirmed source-data (A/B1 have no direct edge in any fillet
      mode; Legacy's A/B1 junction was a fillet artifact).
- [x] **Fillet V2 corner-chord namespace bug fixed + hardened** *(Workstream 1)* — cut IDs collided with node
      IDs (both raw `int`), so a redirected stable-anchor resolved to the wrong tangent cut → airport-spanning
      `V2:corner-chord` edges (SFO 52 over 300 ft, max ~9533 ft → 0). Fixed by a disjoint cut-ID offset, then
      hardened to a compile-time `CutId` newtype + `FilletEndpoint` (Cut|Node) union so the conflation can't
      recompile. Guard test `FilletV2CornerSpanGuardTests` (≤300 ft, SFO/OAK/FLL). Audit found the only other
      site (`FilletEdgeSplitPlanner.ResolveCut`) was the same latent twin, now also covered.
- [x] **Ground-stack design docs** — `docs/ground/{README,fillet-generator,pathfinder,navigator}.md`
      (agent-facing; V2 = architecture, V1 flagged legacy). Linked from CLAUDE.md.

Active frontier (pathfinder V2, tracked in TaskList):
- [ ] 5 Codex HIGH findings (confirmed still open in code: no `DestinationRunway` hold-short reason,
      non-reciprocal hold-short matching, centroid full-length lineup, state-aware A\* pruning, detour
      authorized-taxiway policy); named routing failures (`Sfo*`, `SpotOvershoot*`, …).
- [ ] Ground deadlock (`GroundConflictDetector` mutual proximity-stop) — re-evaluate after routing; may be
      route-induced (AMX669 was routed the wrong way before deadlocking).
- [ ] Navigator v1.1 audit (Workstream 3): re-evaluate the Legacy-fillet compensations on V2 geometry +
      the AMX669 freeze (synth tolerance vs tight V2 arcs); aviation sign-off.

> Req ① for the multi-segment path (`LocalSearchToJunction`) has **no failing repro** — that search is
> goal-directed (A\* to a specific junction), so it does not divert onto a membership arc the way the greedy
> `WalkToNaturalTerminus` did. Re-open only if a concrete V2-on-V2 backtrack surfaces.

Do **not** patch pathfinder V1 or the navigator for V1 geometry — both V1s are being replaced. The
fillet-V2 graph is correct-but-different (membership junction arcs are legitimate turn-connectors; the
FLL coincident C/C1 edges are source-data, not a fillet bug) — adapt the consumer, don't "fix" the graph.
