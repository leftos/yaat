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
| Taxi coverage | `tests/.../Simulation/GroundTaxi/FilletV2TaxiCoverageTests.cs` | The existing OAK + SFO `TaxiCoverageData` smoke pairs (25), run through `TaxiCoverageRunner.Run` on V2 fillets. Spawn at parking / runway-exit, `TAXIAUTO` to a runway hold-short or parking, assert arrival within the V2 A\*-derived time + turn budget and no stall window > 30 s outside a legitimate stop. |
| Landing + exit | `tests/.../Simulation/GroundTaxi/FilletV2LandingExitTests.cs` | OAK 28R no-preference rollout (brakes to a sane turn-off speed over the V2 exit), OAK 28R exit-far-ahead coast, OAK 30/W5 high-speed-exit angle classification, `FindExitAhead` / `FindNearestExit` over V2 geometry. |

The budgets derive from each V2 layout's own optimal A\* route, so the taxi gate measures
*"does it taxi the V2 graph without getting stuck or wildly over-turning"*, not a brittle
Legacy-distance match.

## Current result — GREEN

All 30 tests pass (25 taxi pairs + 5 landing/exit). Full non-nightly Sim suite: 5516 passed,
1 skipped (the pathfinder-V2-gated `Issue165 Skw3404_DoesNotOrbitDuringTaxi`), 0 failed.

- Every taxi pair printed `OK … arrived` with path length within a few feet of the V2 optimal,
  `maxConsecutiveZeroProgress ≤ 4 s` (threshold 30 s), and cumulative turn under budget.
- **No `SKIP: no A* route` lines** — fillet V2 stays fully connected for every smoke route under the
  production pathfinder.
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

- [ ] **FLL taxi coverage.** `TaxiCoverageData` has no FLL smoke pairs; only OAK + SFO are covered
      here. FLL is the third comparison-gate airport and has its own divergent junctions
      (105/106/357, B×B12, J8). Author a small FLL parking→runway / exit→parking smoke set and run it
      on V2 the same way.
- [ ] **Full-suite-on-V2 sweep.** This gate runs curated pairs on V2 but the *whole* suite still runs
      on Legacy. Before the flip, do one run of the full Sim suite with the default flipped to V2 (the
      "flip default now, then triage" pass the pathfinder effort used) and triage the delta — recording
      replays, exit-overlap, hold-short, and lineup tests are pinned to Legacy geometry and will need
      verdicts.
- [ ] **Aviation-realism review** of the V2 radius / preserve policy (turn-speed sign-off), per the
      mandatory review rule. Adding test coverage didn't change sim behavior, so this is gated on the
      flip itself, not on this doc.
- [ ] **Retire the vestigial `FilletArcGeneratorRouter`.** `FilletArcGeneratorRouter.Current` / `UseV2`
      has no consumers in `src/` — the real lever is the `FilletMode` argument to `GeoJsonParser.Parse`.
      The router exists only for `FilletGeneratorInterfaceTests`. Decide during the flip: make it the
      actual default lever, or delete it.

## Reproduce

```bash
cd X:\dev\yaat
dotnet build tests/Yaat.Sim.Tests/Yaat.Sim.Tests.csproj -p:TreatWarningsAsErrors=true 2>&1 | tee .tmp/build.log
timeout 300 dotnet test tests/Yaat.Sim.Tests/Yaat.Sim.Tests.csproj --no-build \
    --filter "FullyQualifiedName~FilletV2TaxiCoverageTests|FullyQualifiedName~FilletV2LandingExitTests" \
    --logger "console;verbosity=detailed" 2>&1 | tee .tmp/v2val.log
```
