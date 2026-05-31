# Fillet V2 — sim-level validation gate

The fillet V2 connectivity rewrite is **geometry-validated** (the `Compare_LegacyVsV2_MeetsHardGates`
no-true-disconnection gate, see [`v2-divergences.md`](./v2-divergences.md)). This doc is the
**sim-level** gate: does an aircraft actually taxi, land, and exit correctly on a V2-filleted graph?
It is the evidence base for flipping the production default from Legacy to V2.

It mirrors the methodology of the pathfinder effort's
[`../pathfinderv2/default-flip-triage.md`](../pathfinderv2/default-flip-triage.md): reuse scenarios
that are known-good on the baseline, run them on the candidate, and give every divergence a verdict
(V2-bug / assertion-too-tight / artifact-of-the-other-layer) before changing anything.

## Setup — isolate fillet from pathfinder

- **Layout:** parsed with `FilletMode.V2` (via `new TestAirportGroundData(FilletMode.V2)`).
- **Pathfinder:** stays on **V1** (the production default). Per the locked decision, the fillet flip
  is validated *before* the pathfinder flip, so any regression here is unambiguously fillet geometry,
  not routing.
- **Production is untouched:** `GeoJsonParser.Parse` / `AirportLayoutDownloader` still default to
  `FilletMode.Legacy`. These tests load V2 explicitly through the switch.

## What is covered

| Suite | File | What it exercises |
|-------|------|-------------------|
| Taxi coverage | `tests/.../Simulation/GroundTaxi/FilletV2TaxiCoverageTests.cs` | The OAK + SFO + FLL `TaxiCoverageData` smoke pairs (31), run through `TaxiCoverageRunner.Run` on V2 fillets. Spawn at parking / runway-exit, `TAXIAUTO` to a runway hold-short or parking, assert arrival within the V2 A\*-derived time + turn budget and no stall window > 30 s outside a legitimate stop. The FLL pairs also run on Legacy in `TaxiCoverageFllTests` (the baseline), so any FLL regression isolates to fillet, not routing. |
| Landing + exit | `tests/.../Simulation/GroundTaxi/FilletV2LandingExitTests.cs` | OAK 28R no-preference rollout (brakes to a sane turn-off speed over the V2 exit), OAK 28R exit-far-ahead coast, OAK 30/W5 high-speed-exit angle classification, `FindExitAhead` / `FindNearestExit` over V2 geometry. |

The budgets derive from each V2 layout's own optimal A\* route, so the taxi gate measures
*"does it taxi the V2 graph without getting stuck or wildly over-turning"*, not a brittle
Legacy-distance match.

## Current result — GREEN

All 36 V2 tests pass (31 taxi pairs across OAK/SFO/FLL + 5 landing/exit). The 6 FLL pairs are also
green on the Legacy baseline (`TaxiCoverageFllTests`) with path lengths within ~1 % of V2. Full
non-nightly Sim suite: 5528 passed, 1 skipped (the pathfinder-V2-gated
`Issue165 Skw3404_DoesNotOrbitDuringTaxi`), 0 failed.

- Every taxi pair printed `OK … arrived` with path length within a few feet of the V2 optimal,
  `maxConsecutiveZeroProgress ≤ 4 s` (threshold 30 s), and cumulative turn under budget.
- **No `SKIP: no A* route` lines** — fillet V2 stays fully connected for every smoke route under the
  production pathfinder, on all three gate airports.
- The OAK 30/W5 high-speed exit survives V2 filleting and is still classified high-speed (≤ 45°);
  exit-node lookup and runway-side association are preserved.

### Observation (not a failure)

During several taxi runs the ground navigator's slow-turn synthesis declined to engage at a corner,
logging e.g. `[NavV2] Synth trigger: aircraft 44.4ft from planned tangent entry (tol 11.5ft for
r=76.8ft) — skipping synthesis for corner node 252`. The aircraft is slightly off the planned tangent
entry point for the tighter V2 arc, so the synthesis tolerance isn't met and the navigator falls back
to pure-pursuit. In every observed case it recovered without stalling (`maxZeroProgress ≤ 4 s`), so
this is informational. If a future scenario *does* stall on this, the fix is in the navigator's
tangent-entry tolerance / synthesis trigger, not the fillet geometry.

## Remaining before flipping the default

- [x] **FLL taxi coverage.** Added a 6-pair FLL smoke set (terminal→10L/28R departures, high-speed and
      90° runway-exit→terminal taxi-ins); green on both Legacy and V2.
- [x] **Full-suite-on-V2 sweep.** Done as a throwaway run (defaults reverted). Only **5 failures**
      across the whole non-nightly suite — all genuine V2 behavioral regressions, **zero brittle
      Legacy-pinned assertions**. Triage below.
- [ ] **Aviation-realism review** of the V2 radius / preserve policy (turn-speed sign-off), per the
      mandatory review rule. Adding test coverage didn't change sim behavior, so this is gated on the
      flip itself, not on this doc.
- [ ] **Retire the vestigial `FilletArcGeneratorRouter`.** `FilletArcGeneratorRouter.Current` / `UseV2`
      has no consumers in `src/` — the real lever is the `FilletMode` argument to `GeoJsonParser.Parse`.
      The router exists only for `FilletGeneratorInterfaceTests`. Decide during the flip: make it the
      actual default lever, or delete it.

## Full-suite-on-V2 sweep — triage

Method: temporarily flip the test-visible fillet defaults to V2 (`GeoJsonParser.Parse` /
`ParseMultiple` 3-arg default + `TestAirportGroundData()` parameterless), run `Category!=Nightly`,
revert. All 5 failing tests pass on Legacy (verified), so each is V2-specific. No assertion merely
needs relaxing — every failure is a real routing/navigation regression at a specific junction.

| Test | Symptom | Verdict |
|------|---------|---------|
| `IssueFllDal880TaxiBacktrackBTests.ResolveExplicitPath_TT4BB1_…RouteHasNoUTurn` | `U-turn at seg 60→61: B #765→#767 then #767→#765` (180°) | **V2-bug — edge-split reversal stub** at FLL B/C1 |
| `IssueFllDal880TaxiBacktrackBTests.DAL880_TaxiTT4BB1HS10L_RouteHasNoUTurn` | same B/C1 U-turn (recording variant) | **V2-bug** — same root |
| `Issue166CrossShortcutsGrassTests.Ual19_FollowsHTaxiLineThroughRunwayCrossing` | UAL19 route to @F14 `reversal at index 85: (1160→43) then (43→1160)` on taxiway A; UAL19 never reaches `CrossingRunwayPhase` by t=314 | **① pathfinder edge-selection** over collapsed SFO 43/1160 junction (confirmed — see below) |
| `IssueAmxTaxiOvershootTests.AMX669_HoldsShortOf1L_WithReasonableHeading` | **frozen at taxi seg 0 from t=1** (ias=0, never advances, target #1283); `[NavV2] Synth trigger: aircraft 30.8ft from planned tangent entry (tol 6.0ft for r=40.0ft) — skipping synthesis for corner node 770` | **② GroundNavigator freeze** at a tight V2 arc near route start (confirmed — NOT a reversal) |
| `S2Oak4RvSidCtoTests.N436MS_CtoDuringTaxi_Nimi6_Stores315…` | no pending `InitialClimbPhase` at t=10 during CTO-while-taxiing replay | **③ CTO/replay cascade** — downstream of a taxi-route difference; re-triage after ①/② |

### Root causes (confirmed, 2 + 1 cascade)

The investigation used `FilletV2ReversalStubDiagnosticTests` (junction edge dumps for both fillet
generators) and an AMX669 V2 trajectory dump.

1. **① Pathfinder `WalkTaxiway` edge-selection over collapsed V2 junctions (FLL DAL880 ×2, SFO Issue166).**
   The graph is fully connected (matches the green no-true-disconnection gate) — this is *not* a
   connectivity break. V2's edge-split collapses each junction into **fewer tangent nodes** than Legacy
   (SFO J43: 5 nodes vs 9; FLL J75 similar) with **larger bearing steps**, and every junction retains
   the fillet **junction arcs** (`C1 - B`, `B - C`, `A - RAMP`, `A - Q1`) that a bare-taxiway walk matches
   **by membership** (`GroundArc.MatchesTaxiway` checks `TaxiwayNames` membership). Where Legacy gave the
   walk an unambiguous near-straight pure-name continuation, V2's true continuation is now an *arc* whose
   bearing competes closely with a membership-matching junction arc or a short edge. `TaxiPathfinder.WalkTaxiway`'s
   straightest-continuation heuristic then picks the wrong edge — a dead-end spur (FLL: B→767, a C/C1
   node) or an oscillation (SFO: 1160↔43) — producing the `X→Y→X` reversal. Trigger is V2 geometry;
   the latent fault is the V1 pathfinder edge-selection.
2. **② GroundNavigator slow-turn synthesis vs tight V2 arcs (AMX669).** AMX669 freezes at taxi segment 0
   (ias 0, zero movement, target #1283). The navigator declines slow-turn synthesis at a tight r=40 ft
   V2 arc because the aircraft is 30.8 ft off the planned tangent entry (tolerance 6 ft), and pure-pursuit
   fails to produce forward motion — the aircraft never starts taxiing. This is the GroundNavigator
   (downstream of routing), not the pathfinder. The synth tolerance does not scale with the (now tighter)
   V2 arc radius.
3. **③ CTO/replay cascade (N436MS).** Expected downstream of a taxi-route difference; re-triage after
   ①/② rather than touching the CTO/InitialClimb path.

**Conclusion:** V2 is close to drop-in — 5 real regressions, no brittle assertions, 2 root causes. Both
live in the *routing/navigation* layer, not in the fillet geometry itself (the geometry passes the
connectivity gate). They sit in **two different components**, so they are tracked separately:

- **① → pathfinder.** Route *resolution*. Pathfinder V1 is being replaced, so do not patch V1 — folded
  into the [pathfinder V2 plan](../pathfinderv2/default-flip-triage.md) as a V2-router requirement.
- **② → GroundNavigator.** Route *following*. The pathfinder swap does not touch the navigator, so this
  is its own review — see below.

## Navigator review (root cause ②) — a fillet-V2 flip prerequisite

The GroundNavigator (the per-tick steerer in `TaxiingPhase`, not the pathfinder) physically follows the
route over the fillet geometry. It is **shared** — unchanged by the pathfinder V1→V2 swap — and it carries
a large amount of tuning built specifically against *Legacy* fillet-arc quirks: the orbit detector, the
cluster / slow-turn **synthesis planner**, chord-chain aggregate-turn handling, reverse-arc
"natural-forward" detection, and the pure-pursuit entry-alignment thresholds.

Fillet V2 produces different geometry (cleaner single arcs, fewer tangent nodes, different radii), so the
navigator needs its own review against V2 — independent of the pathfinder:

- [ ] **Synthesis tolerance vs tight V2 arcs.** On an r=40 ft V2 arc the planned-tangent-entry tolerance
      (~6 ft) is too small: AMX669 was 30.8 ft off, synthesis was skipped, pure-pursuit produced no forward
      motion, and the aircraft **froze at taxi seg 0**. Scale the tolerance with arc radius and/or guarantee
      a non-freezing pure-pursuit fallback. Repro: `IssueAmxTaxiOvershootTests.AMX669_HoldsShortOf1L_WithReasonableHeading`;
      the benign `[NavV2] Synth trigger … skipping` lines in the *passing* taxi smoke runs show it operating
      at the tolerance edge on V2 arcs.
- [ ] **Audit Legacy-fillet-specific compensations for dead/wrong behavior on V2.** Orbit detector, cluster
      synth planner, chord-chain aggregate turn, reverse-arc natural-forward detection — these were tuned for
      Legacy artifacts (chord chains, reverse-traversed arcs). Determine which are no longer needed (V2 may
      not produce the artifact) vs which need re-tuning for V2 arc radii. Re-validate the existing navigator
      regression tests (the OAK/SFO taxi-spin pins) against V2 geometry.
- [ ] **Aviation-realism review (mandatory)** of any navigator tolerance/turn-speed change on the (tighter)
      V2 arcs — turn-rate and corner-speed realism.

This review gates the fillet-V2 default flip alongside the pathfinder V2 work; the two flip together.

## Reproduce

```bash
cd X:\dev\yaat
dotnet build tests/Yaat.Sim.Tests/Yaat.Sim.Tests.csproj -p:TreatWarningsAsErrors=true 2>&1 | tee .tmp/build.log
timeout 300 dotnet test tests/Yaat.Sim.Tests/Yaat.Sim.Tests.csproj --no-build \
    --filter "FullyQualifiedName~FilletV2TaxiCoverageTests|FullyQualifiedName~FilletV2LandingExitTests" \
    --logger "console;verbosity=detailed" 2>&1 | tee .tmp/v2val.log
```
