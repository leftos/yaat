# Taxi backtrack at FLL: DAL880 with `T T4 B B1 HS 10L` turns wrong way on B

## Status

Partial fix shipped (chain U-turn). **Hairpin U-turn remains** — see "Remaining work" below.

## Context

User report: at FLL (scenario `S1: S1L3 (KFLL East)`), DAL880 was given
`TAXI T T4 B B1 HS 10L` from parking around (26.0738, -80.1443). The aircraft
"turned the wrong way" — visually appeared to backtrack on B.

Bundle: `tests/Yaat.Sim.Tests/TestData/fll-dal880-taxi-backtrack-b-recording.yaat-bug-report-bundle.zip`.
GeoJSON: `tests/Yaat.Sim.Tests/TestData/fll.geojson` (fetched from the vNAS training API).
E2E test: `tests/Yaat.Sim.Tests/Simulation/IssueFllDal880TaxiBacktrackBTests.cs`.

## What was wrong (two independent U-turns)

Investigation found **two** U-turns embedded in the resolved route, each with a
different root cause:

1. **Chain U-turn at junction #56 (T/T4/C).** The fillet generator created
   three tangent nodes for the same direction on T4 toward #57 — `#715↔#717↔#713`
   — connected by `phase-d-tangent-link@56` edges. The arc landed at `#713`
   (north end), but the only `phase-d-shorten@56` edge anchored at `#715`
   (south end). Walking T → T4 north entered via the arc at `#713`, walked
   SOUTH down the chain to `#715` (~36 ft), then jumped NORTH 82 ft via the
   shorten to `#57` — a 154° flip in a few feet of taxi. The aircraft
   physically gets stuck rotating in place at this junction (heading goes
   from ~22° to 180° between t=280 and t=320 with IAS ~3 kt in the bundle).

2. **Hairpin U-turn at the T4-B junction (#61).** FLL's `T4` is encoded in
   the GeoJSON as a single 9-point V-shaped LineString that pinches at the
   T-junction `#56` and reaches B at TWO points (`#53` west, `#61` east).
   It's effectively two physical T4 connectors named with one feature. When
   the walker enters T4 from the east-T-T4 fillet arc, it lands on the east
   leg and terminates at `#61` where T4 meets B at a 167° angle (sharp
   left turn that's geometrically a near-U-turn). The proper path uses the
   west leg to reach B at `#53` where T4 meets B perpendicular.

## What was fixed

A new `AddDirectShortensFromArcAnchors` post-fillet cleanup pass in
`src/Yaat.Sim/Data/Airport/FilletArcGenerator.cs`. For each tangent chain
where the existing shorten anchor sits at one chain endpoint AND there's a
DIFFERENT arc anchor at the OPPOSITE endpoint, the pass adds a direct
`phase-d-shorten-direct` edge from the opposite arc anchor to the shorten
target. This eliminates the chain detour for walkers exiting that corner arc.

The pass is intentionally narrow:
- Requires the chain to have ≥2 arc anchors (multi-corner sharing one tangent line).
- Requires the existing shorten to anchor at a chain endpoint.
- Only adds direct edges for arc anchors at the OPPOSITE endpoint from the shorten.

Additive only — no existing edges (tangent-links, arcs, original shortens) are
modified. Tested against OAK (176 edges added), SFO (similar), no regressions
in the full Yaat.Sim + yaat-server suites (4222 + 493 tests pass).

## Remaining work — hairpin U-turn at #61

The second U-turn (at `#61` from T4's V-shape) is **not addressed**. The test
in `IssueFllDal880TaxiBacktrackBTests.cs` currently only asserts no
`within-T4` U-turn (chain fix). The B-direction-on-#61 issue still produces
a 167° flip in the route the test does not check.

Investigations attempted in the chain-fix session:

- **Pathfinder lookahead for runway destinations** (extending `SelectBestStopNode`
  to fire when `DestinationRunway` is set, and always-realizing the bridge route
  via `FindRoutes` to honor multi-corner same-taxiway arc shortcuts at the apex).
  Worked locally for FLL but **regressed**:
  - `SfoRampCrossesRunwayTests.TaxiCommand_AcrossRunways_ShouldFail` —
    pathfinder began finding cross-runway bridges via T41E/C/D when the user
    only specified A and E. Safety-critical.
  - `OakExplicitHsAutoCrossTests` and `N7ljResExplicitHoldShortTests` —
    failed when the route resolution returned different paths.
  - `TaxiPathfinderTests.VariantInference_*` — the variant-inference contract
    ("`TAXI D` to runway 30 should fail when D doesn't reach 30 directly")
    was bypassed because the new look-ahead found indirect bridges.
- **Bridge-validation guard** that rejects bridges using unauthorized
  letter-only taxiways: fixed the variant tests but didn't fix SFO.

The fundamental tension: the FLL fix needs the pathfinder to be smarter about
multi-leg same-named taxiways (V-shapes), but adding "smartness" tends to
relax constraints that other tests depend on.

Approaches worth exploring next:

1. **GeoJSON-level hairpin split.** Detect V-shaped LineStrings (interior
   vertex turn > ~120°) and split into two features at the apex. Each leg
   becomes its own LineString. Universal, doesn't change pathfinder logic.
2. **Hairpin-aware pathfinder.** At entry-edge selection, when the named
   taxiway has multiple disjoint reachable regions from the current position,
   evaluate each by simulated continuation cost and pick the leg with the
   smaller total turn (not just shortest distance). Limit the lookahead to
   the named-taxiway path so unauthorized cross-runway bridges stay rejected.
3. **Scenario-level workaround.** Define preset taxi routes for FLL (the
   `T-T3-B → 10R` preset already exists; add a `T-T4-B-B1 → 10L` preset that
   names the west leg explicitly via a node-reference like
   `T T4 #53 B B1 HS 10L`).

## Plan

- [x] Bundle in TestData; FLL geojson fetched.
- [x] Failing E2E test asserts route resolution.
- [x] Diagnose root cause #1 (chain U-turn at #56).
- [x] Fix (AddDirectShortensFromArcAnchors); aviation-sim-expert review skipped
      because the fix is graph-cleanup, not aviation logic.
- [x] Confirm tests pass; `pwsh tools/test-all.ps1` clean.
- [x] CHANGELOG.md (Fixed) entry.
- [ ] **Open**: address the hairpin U-turn at #61. Pick approach 1, 2, or 3
      above (or a hybrid). Investigate in a fresh session — the deep state
      from the partial-fix session will be helpful but not load-bearing.
- [ ] Once the hairpin is fixed, strengthen the FLL test to assert no U-turn
      ANYWHERE in the route (currently only asserts within-T4).
- [ ] Delete this plan file after the hairpin fix ships.

## Anchors

- `src/Yaat.Sim/Data/Airport/FilletArcGenerator.cs` —
  `AddDirectShortensFromArcAnchors` (~line 496).
- `src/Yaat.Sim/Data/Airport/TaxiPathfinder.cs` — `ResolveExplicitPath`
  (~line 107), `SelectBestStopNode` (~1137), `PickBestStartEdge` (~1703),
  `PickBestWalkEdge` (~1778), `WalkPathDistancesFrom` (~1076).
- `tests/Yaat.Sim.Tests/Simulation/IssueFllDal880TaxiBacktrackBTests.cs`.
- `docs/e2e-tdd-issue-debugging.md`.
- Auto-memory: `project_taxiway_naming_convention.md`,
  `feedback_exit_toward_parking.md`.

## Verification (chain fix, completed)

- E2E test in `IssueFllDal880TaxiBacktrackBTests.cs` asserts no within-T4
  U-turn (chain fix). Both `[Fact]` methods pass.
- Full Yaat.Sim test suite: 4222/4222 pass.
- Cross-repo `pwsh tools/test-all.ps1`: yaat 4222 + yaat-server 493 all pass.

## Out of scope (handled elsewhere)

- Recommended-taxi-routes UI restructuring — see
  `per-artcc-custom-taxi-routes.md`.
- NKS7103's `T T8 Q J J1 J J1` route in the same bundle (suspicious duplicate
  `J J1`). Track separately if it represents a real bug.
- Other unrelated FLL pathfinder bugs not exercised by this scenario.
