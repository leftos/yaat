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
| `Issue166CrossShortcutsGrassTests.Ual19_FollowsHTaxiLineThroughRunwayCrossing` | route to F14 `reversal at index 85: (1160→43) then (43→1160)`; UAL19 never reaches `CrossingRunwayPhase` by t=314 | **V2-bug — edge-split reversal stub** at SFO 43/1160 (cascades to replay timing) |
| `IssueAmxTaxiOvershootTests.AMX669_HoldsShortOf1L_WithReasonableHeading` | never reaches 1L hold-short in 300 s; `[NavV2] Synth trigger: aircraft 30.8ft from planned tangent entry (tol 6.0ft for r=40.0ft) — skipping synthesis for corner node 770` | **V2-bug — navigator synth tolerance too tight for tight V2 arcs** (may share root with a prior reversal pushing the aircraft off the tangent) |
| `S2Oak4RvSidCtoTests.N436MS_CtoDuringTaxi_Nimi6_Stores315…` | no pending `InitialClimbPhase` at t=10 during CTO-while-taxiing replay | **underlying / replay cascade** — downstream of a taxi-route difference; re-triage after the above fixes |

### Root causes (2–3, not 5)

1. **Edge-split reversal stubs (3 tests).** At a few junctions the V2 surviving-edge set contains a
   short there-and-back: the route walks `X→Y` then `Y→X`. Clean on Legacy, so V2 introduced the stub;
   the V1 pathfinder faithfully walks the V2 graph. This is the "crossed-middle / backward sub-segment"
   hazard the rewrite plan called out (Workstream A, Step 4) surfacing at FLL B/C1 and SFO 43/1160.
   **Fix is in the edge-split** (drop the degenerate there-and-back sub-segment), then re-run the
   no-true-disconnection gate + this suite. Needs aviation-realism review (turn-speed) if arc shapes
   change.
2. **Navigator synth tolerance vs tight V2 arcs (1 test, possibly shared with #1).** The earlier benign
   `[NavV2] Synth trigger … skipping` observation turns fatal here: with the aircraft 30.8 ft off the
   planned tangent for an r=40 ft arc (tol 6 ft), slow-turn synthesis is skipped and AMX669 can't make
   the corner. Either the 30.8 ft offset comes from a prior reversal (→ #1) or the synth tolerance
   needs to scale with arc radius. Investigate which before changing the navigator.
3. **Replay cascade (1 test).** Likely downstream of a taxi-route difference; expected to clear once
   #1/#2 are fixed. Re-triage rather than touch the CTO/InitialClimb path blindly.

**Conclusion:** V2 is close to drop-in — 5 real regressions, no brittle assertions. The blocker is a
small number of edge-split reversal stubs (root cause #1) plus the navigator-tolerance interaction
(#2). Fix those, re-run the sweep, then the flip is a small step.

## Reproduce

```bash
cd X:\dev\yaat
dotnet build tests/Yaat.Sim.Tests/Yaat.Sim.Tests.csproj -p:TreatWarningsAsErrors=true 2>&1 | tee .tmp/build.log
timeout 300 dotnet test tests/Yaat.Sim.Tests/Yaat.Sim.Tests.csproj --no-build \
    --filter "FullyQualifiedName~FilletV2TaxiCoverageTests|FullyQualifiedName~FilletV2LandingExitTests" \
    --logger "console;verbosity=detailed" 2>&1 | tee .tmp/v2val.log
```
