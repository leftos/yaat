# Issue #172 — SFO taxiing bugs

> **Status:** initial investigation (seed plan; not yet implemented).
> **Labels:** bug, ground-cmds. **Source:** Discord thread, filed 2026-06-03.
> **Bundle:** `S1-SFO-4 | FD/CD/GC 19/10` (2491 s, 500 snaps, 305 actions, ZOA). Install with
> `python tools/bug_bundle.py install --issue 172 --desc sfo-taxiing`.

## Symptom

A cluster of ~9 distinct ground/taxi defects in one SFO recording (departures push from parking and
taxi to 10R/19; arrivals land and taxi in). Reported items:

- `HS` of a taxiway repeats many times in the command/route echo (`hs b hs b hs b…`).
- JBU577 spins after `taxi G B hs b` (turns back toward the runway, then toward B again).
- KLM605/SWA2208 slow way down passing each other on B/A.
- N984DC goes back onto the runway after `taxi G B hs B`.
- EJA512 turned the wrong way on K (south instead of north).
- JBU2435/FFT2083 slow way down passing each other on M1/M2.
- WJA1521 "M4 is unreachable" when being instructed onto M4 in the first place.
- SKW3359 turned the wrong way.
- UAL2164 turned the wrong way.

Triage each as a **separate TDD sub-task**, following the recording-driven precedent
`tests/Yaat.Sim.Tests/Simulation/Issue165SkwTaxiSpinTests.cs`.

## Sub-bug map

| # | Symptom | Suspected area | First probe | Root cause |
|---|---------|----------------|-------------|------------|
| 1 | `HS` repeats in route echo | `TaxiRoute.ToSummary()` loops **all** `HoldShortPoints`; dupes come from population | `src/Yaat.Sim/Data/Airport/TaxiRoute.cs:166` — dedup at source in `RouteMaterialiser.AnnotateHoldShorts` / `HoldShortAnnotator`, not by masking in `ToSummary` | needs-repro (mechanism confirmed) |
| 2 | JBU577 oscillates after `taxi G B hs b` | junction candidate selection / entry-alignment | `SegmentExpander` junction scoring; `GroundNavigator` entry-alignment + pure-pursuit | needs-repro |
| 3, 6 | B/A and M1/M2 crawl when passing on **parallel** taxiways | `GroundConflictDetector` mis-classifies parallel-edge passing as convergence/crossing | `src/Yaat.Sim/GroundConflictDetector.cs` `ClassifyPair`/`ResolveConvergence` — should respect lateral (wingspan) separation on adjacent parallel edges | needs-repro |
| 4 | N984DC returns to runway after `taxi G B hs B` | route not truncated at the explicit hold-short; residual segments point back to rwy | `RouteMaterialiser.FindTruncationIndex`; `TaxiRoute` explicit-HS insertion | needs-repro |
| 5, 8, 9 | EJA512 (K, south≠north), SKW3359, UAL2164 turn **wrong way** | wrong leg chosen at a same-named fork; or stale `DirectionalEdge` bearing | `SegmentExpander.RouteNamedToNamed` / `WalkToNaturalTerminus` directionality | needs-repro |
| 7 | WJA1521 "M4 unreachable" when first instructed onto M4 | feasibility gate rejects the transition | exact string `No valid path from … — transition infeasible from node N` in `src/Yaat.Sim/Data/Airport/Pathfinding/SegmentExpander.cs`; check `GeometricAdmissibility` heading-delta limit + detour fallback | **log-confirmed**: `SIA31: No valid path from B to B1 — transition infeasible from node 117` |

## Approach

- One failing replay/E2E test per sub-bug. Use `tools/Yaat.LayoutInspector` with `--ticks` +
  `--tick-table` against the recorded tick CSV to inspect trajectories (invoke the `layout-inspect`
  skill for current flags; see `docs/ground/` and `docs/e2e-tdd-issue-debugging.md`).
- Fix at the source, not the symptom. Likely shared roots: **3 ≡ 6** (conflict detector) and
  **5 ≡ 8 ≡ 9** (fork directionality) — fixing one may close several.
- **Mandatory `aviation-sim-expert` review** (ground separation, hold-short, taxi turn behavior;
  cite the local FAA refs, do not web-search).

## Verification

- Per-sub-bug tests green; then `pwsh tools/test-all.ps1` (cross-repo).
- Spot-check via LayoutInspector HTML overlay (`--html … --ticks …`).

## Open questions

- Sub-bug #7 may be an SFO ground-graph **data** issue (B↔B1 geometry) rather than a code bug —
  confirm a ≤max-heading-change path actually exists before relaxing the admissibility gate.
- Confirm whether #1's duplicate hold-shorts originate in pathfinding or in post-pathfinding
  explicit-HS insertion before choosing where to dedup.
