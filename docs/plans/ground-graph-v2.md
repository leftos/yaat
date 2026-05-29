# Ground Graph V2 — transition main plan

The airport ground stack is moving from V1 to V2 across **three layers that must flip together**. This
file is the entry point and status tracker for the whole transition; each layer keeps its detail in its
own sub-plan (linked below). A fresh agent should be able to start here and find where to continue.

## The three layers

| # | Layer | Role | Component | Sub-plan | Status |
|---|-------|------|-----------|----------|--------|
| 1 | **Fillet generator V2** | builds the ground-graph **geometry** (nodes, edges, arcs + radii) | `FilletArcGenerator` V2 | [`filletv2/`](./filletv2/status.md) | geometry + behind-switch validation **done**; flip gated |
| 2 | **Pathfinder V2** | resolves a `TaxiRoute` over that graph | `TaxiPathfinder` V2 router | [`pathfinderv2/`](./pathfinderv2/default-flip-triage.md) | **WIP** (default-flip triage open) |
| 3 | **Navigator v1.1** | **follows** the route+geometry per tick (steering) | `GroundNavigator` (in `TaxiingPhase`) | [navigator review](./filletv2/v2-sim-validation.md) (§ Navigator review) | **not started** |

> Naming: layer 1 is the *fillet generator* (a.k.a. "generator v2" / "fillet v2"). Layer 3 is an
> *incremental* update to the existing navigator (hence **v1.1**, not a rewrite). All three are versioned
> independently but **ship as one switch-over** — see the joint flip gate below.

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
| Navigator v1.1 | not started | scope + start the navigator review (root cause ②) |
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
- [ ] **Codex HIGH #5 — state-aware A\* pruning (deferred careful pass):** key the closed set in `AutoRouter.RunAstar` + `SegmentExpander.LocalSearchToJunction` by `(nodeId, 5°-bearing-bucket)` using the propagated arrival bearing — a node-id-only closed set lets a cheaper arrival suppress the only viable different-bearing arrival. Failing-first synthetic repro + OAK/SFO grid baseline; land with the latency budget
- [ ] **Fillet-sweep requirement ①:** prefer a single-name continuation over a junction arc that matches the walked taxiway only by **membership** (`GroundArc.MatchesTaxiway`), and reject a candidate that immediately backtracks — V2's collapsed junctions make the wrong edge win the straightest-continuation heuristic (FLL B/C1 765↔767, SFO A 1160↔43). Repro: `IssueFllDal880TaxiBacktrackBTests`, `Issue166CrossShortcutsGrassTests`
- [ ] Decide k-alternative support; coverage tests; latency budget
- [ ] Flip default `TaxiPathfinderRouter` to V2

## Workstream 3 — Navigator v1.1 (following)

Detail: [`filletv2/v2-sim-validation.md`](./filletv2/v2-sim-validation.md) § "Navigator review (root cause ②)". May graduate to its own `docs/plans/navigator-v1.1/` once work begins.

The GroundNavigator is **shared** (unchanged by the pathfinder swap) and physically steers the aircraft
along the route. Its V1 tuning targets Legacy fillet geometry; V2 needs its own review.

- [ ] **Synth tolerance vs tight V2 arcs.** r=40 ft V2 arc + 6 ft tangent-entry tolerance → AMX669 froze at taxi seg 0 (synthesis skipped, pure-pursuit produced no motion). Scale tolerance with arc radius and/or guarantee a non-freezing pursuit fallback. Repro: `IssueAmxTaxiOvershootTests.AMX669_HoldsShortOf1L_WithReasonableHeading`
- [ ] **Audit Legacy-fillet-specific compensations** (orbit detector, cluster synth planner, chord-chain aggregate turn, reverse-arc natural-forward detection) for dead/wrong behavior on V2 geometry — some may be removable, some need re-tuning
- [ ] Re-validate the navigator regression pins (OAK/SFO taxi-spin recordings) against V2 geometry
- [ ] **Aviation-realism review (mandatory)** of any tolerance / turn-speed change on the tighter V2 arcs

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
