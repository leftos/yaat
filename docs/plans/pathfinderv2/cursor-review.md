# Taxi Pathfinder v2 — Review (Cursor)

**Date:** 2026-05-28
**Scope:** Code and docs review of `TaxiPathfinder` and `src/Yaat.Sim/Data/Airport/Pathfinding/*` vs legacy `TaxiPathfinder.cs`. Not a full release sign-off.
**Context at review time:** `TaxiPathfinderRouter.Current` defaults to `TaxiPathfinder`; v1 remains available via `TaxiPathfinderV1Adapter` / `UseV2 = false`.

---

## Executive summary

**v2 is a real implementation, not a stub.** It has a coherent architecture that fixes several structural problems in v1. It is **already the production default** via `TaxiPathfinderRouter`.

It is **not** a complete replacement in the sense the project’s own acceptance criteria describe (`docs/plans/taxi-pathfinder-v2.md`): explicit-path parity is thinly tested, issue #165 is only partially addressed at the pathfinder layer, and ~2,800 lines of v1 remain because most tests still call `TaxiPathfinder` static methods directly.

**Compared to v1:** v2 is the better *foundation*; v1 is still the better *proven quantity* for “does the whole sim behave?” until more E2E and explicit-path comparison runs happen under the router.

At review time, **PathfinderGrid** comparison tests (OAK + SFO smoke pairs, 18 cases) all passed with v2 U-turn count ≤ v1.

---

## v2 in a vacuum

### What it is

A three-phase pipeline behind a thin facade (`TaxiPathfinder`, ~194 lines):

| Phase | Role |
|--------|------|
| **Constraint compilation** | `SearchContext.Compile` — destination, hold-shorts, authorized taxiways, category |
| **Search** | `SegmentExpander` (explicit named paths) or `AutoRouter` (A*) |
| **Materialisation** | `RouteMaterialiser` — segments, hold-shorts, truncation, warnings |

Supporting pieces: unified `RouteCostFunction`, `GeometricAdmissibility` as a hard expansion gate, structured `PathfindingFailure`, `PartialRoute` state for search.

Rough size: **~2,270 lines** in `V2/` plus the facade — still smaller than v1’s **~2,840-line** monolith, but `SegmentExpander.cs` alone is **~1,095 lines**. The rewrite is clean at the *design* level, not at the *file-size* level.

Key files:

- `src/Yaat.Sim/Data/Airport/TaxiPathfinder.cs`
- `src/Yaat.Sim/Data/Airport/Pathfinding/AutoRouter.cs`
- `src/Yaat.Sim/Data/Airport/Pathfinding/SegmentExpander.cs`
- `src/Yaat.Sim/Data/Airport/Pathfinding/RouteCostFunction.cs`
- `src/Yaat.Sim/Data/Airport/Pathfinding/GeometricAdmissibility.cs`
- `src/Yaat.Sim/Data/Airport/Pathfinding/SearchContext.cs`
- `src/Yaat.Sim/Data/Airport/Pathfinding/RouteMaterialiser.cs`

Design authority: `docs/plans/taxi-pathfinder-v2-design.md` (binding decisions section).

### Genuine strengths

1. **One cost model** — v1 used different scorers for walk, bridge, and A* branches. v2’s `RouteCostFunction.IncrementalCost` is used consistently; preference multipliers are explicit.

2. **Geometry during search, not after** — `GeometricAdmissibility` rejects bad junctions before they enter the committed route. That directly targets the SKW3404 class of bugs (greedy walk + post-hoc repair).

3. **Explicit mode is search-based** — Segment-by-segment expansion with multiple junction candidates, bounded local search, and a detour fallback (per binding decisions in the design doc). That matches how SFO/OAK actually work (parallel junctions).

4. **Structured failures** — `PathfindingFailure` with `FailureKind` is better for instructors and debugging than v1’s ad hoc `failReason` strings.

5. **Category-aware limits** — `CategoryLimits.MaxHeadingChangeDeg` is wired through `SearchContext` and the public `ITaxiPathfinder` API (jets 135°, helo 175°, etc.).

6. **Pragmatic cost tuning** — Reverse arcs and unauthorized letter taxiways are **soft penalties**, not hard bans, after real-layout experience (SFO parking exits, incomplete auth data). That is the right trade for sim robustness.

7. **Auto A* controls explosion** — Global `bestGScore` pruning plus a 200k expansion cap; cross-field SFO routes were failing at 50k during development.

8. **Revisit policy for explicit paths** — Visited nodes reset between named taxiway segments so routes like `A E B B3 A B1` are allowed; within-segment cycle prevention stays.

### Real weaknesses

1. **Issue #165 is not “solved” by v2 alone** — `Issue165_V2_SkwRoute_ResolvesWithoutFailure` only asserts the route **resolves**. Test comments state the orbit is **GroundNavigator + fillet topology**, and the test **diagnostically expects ≤2 heading violations ≥135°** on the produced route. v2 can still emit geometrically harsh routes that v1 also emitted.

2. **Auto vs explicit asymmetry** — `DirectionReversalCostNm` applies in `SegmentExpander` only, deliberately omitted from A* `IncrementalCost` to preserve heuristic admissibility. Auto-routes may be more “zig-zaggy” than explicit ones for the same airport.

3. **Heavy explicit-path module** — `SegmentExpander` carries variant extension, parking extension, detours, node refs, and junction scoring. Complexity moved out of one file but did not disappear.

4. **Expansion budget risk** — 200k expansions on large graphs is correct for completeness but is a latency/memory footgun; the design doc says to re-evaluate if memory pressure becomes a concern.

5. **Interface/doc drift** — `TaxiPathfinderRouter` XML on `Current` still says it “Defaults to a `TaxiPathfinderV1Adapter`” while code defaults to v2. Small, but it signals the migration story is mid-flight.

6. **No pure-pursuit / sim-drive costing** — Costs are graph-geometric. A route can be “admissible” under 135° yet still nasty for `GroundNavigator` on tight fillets (the #165 gap).

---

## v2 compared to v1

### Architecture

| Aspect | v1 (`TaxiPathfinder`) | v2 (`V2/*` + facade) |
|--------|----------------------|----------------------|
| Structure | Single static class, many special cases | Compile → search → materialise |
| Explicit paths | Greedy `WalkTaxiway` + bridges + optional `SelectBestStopNode` lookahead | `SegmentExpander` local best-first + junction enumeration |
| Auto paths | A* embedded in v1 with separate penalty logic | Dedicated `AutoRouter` A* |
| U-turn handling | `BearingFlipPenaltyNm` post-walk + filters | Hard admissibility gate + soft reverse-arc cost |
| Failure reporting | String `failReason` | `PathfindingFailure` → string at boundary |
| Category | Effectively jet-default in many paths | Passed through `ITaxiPathfinder` |

v2 implements the anti-patterns list from `docs/plans/taxi-pathfinder-v2.md` in spirit: no greedy lock-in, no multi-pass mutation of the committed path, one cost function, unconditional junction exploration in explicit mode (vs v1’s conditional lookahead).

### Behavior (what we can claim today)

**Auto-route (`FindRoute`):**
`PathfinderGrid` tests (OAK + SFO smoke pairs) require:

- Same success/failure as v1
- **V2 U-turn count ≤ V1** (not identical routes)

Routes **will differ** in segment count and distance; the harness explicitly allows that. v2 is judged on **fewer U-turns**, not parity.

Harness: `tests/Yaat.Sim.Tests/Helpers/PathfinderComparison.cs`, `tests/Yaat.Sim.Tests/PathfinderGrid/*StressGridTests.cs`.

**Explicit-route (`ResolveExplicitPath`):**
Much weaker evidence:

- `tests/Yaat.Sim.Tests/Pathfinding/V2/SegmentExpanderTests.cs` + Issue165 resolve test
- **No** `PathfinderComparison` for explicit paths
- `TaxiPathfinderTests` (~48 call sites) still hit **v1 static** directly
- `Issue165SkwTaxiSpinTests.Skw3404_Seg12_PathfinderDiagnostic` still calls **`TaxiPathfinder.ResolveExplicitPath`**, not the router

Production explicit TAXI uses v2 via `TaxiPathfinderRouter`; most regression tests still characterize v1.

**End-to-end (#165):**
`Skw3404_DoesNotOrbitDuringTaxi` runs full replay through `SimulationEngine` (pathfinder + navigator + phases). With v2 as default, that test is the real bar — but it was written for the **combined** stack (parking fix, entry alignment, orbit detector, etc.), not pathfinder isolation. Passing it does not prove v2 alone fixed routing; failing it would not prove v2 alone broke it.

### v1 advantages v2 has not fully displaced

- **Years of bug-shaped patches** — `ApplySameTaxiwayArcShortcuts`, ramp fallbacks, runway bridge heuristics, `EnableLookahead` — each maps to a past failure. v2 replaces the *pattern*; not every edge case has a named regression under v2.

- **Test gravity** — Catalog, FLL backtrack, AMX overshoot, fillet pathfinding, airport E2E, taxi coverage runner: overwhelmingly v1-static or full-sim without `UseV2` toggles.

- **Predictability for instructors** — v1’s greedy routes are wrong but **stable** in recordings. v2 default changes replay determinism for any scenario where routing is re-derived.

### v2 advantages over v1 (where it should win)

- Fewer **search-time** U-turns on auto grids (enforced in CI).
- Clearer **failure** when a named sequence is impossible (after junction + detour attempts).
- **Maintainability** — new behavior goes into `RouteCostFunction` / `GeometricAdmissibility` instead of another filter in a 3k-line file.
- **Category** — real limits instead of jet assumptions everywhere.

### Where v1 might still beat v2

- **Recall**: v1 sometimes returns *some* route that sim then struggles with; v2 may return `null` / `TransitionInfeasible` where v1 brute-forced a path.
- **Latency**: Greedy walk is typically cheaper than 200k-cap A* on cross-field pairs (grid tests log timing but do not fail on it; acceptance criteria want median ≤2× v1).
- **Explicit-path quirks** — v1’s look-ahead and arc shortcuts encode airport-specific wins v2 may not replicate until grids cover named paths.

---

## Migration state

From `docs/plans/taxi-pathfinder-v2.md` retirement criteria:

| Criterion | Status |
|-----------|--------|
| Full test suite on v2 | **Partial** — grid + V2 unit tests; most `TaxiPathfinderTests` still v1 |
| Fix #165 | **Partial** — resolves route; orbit = navigator/layout; violations expected on route |
| Other ground-taxi regressions | **Unclear** — not systematically re-run under router-only at review time |
| OAK/SFO grid ≥ v1 | **Auto only**, U-turn metric — **passing** (18 grid tests) |
| Latency ≤2× v1 | **Not gated** in CI |
| Debuggable failures | **Improved** structurally; not validated on all `FailureKind`s |

**Production:** `GroundCommandHandler`, `GroundViewModel`, `GroundView.axaml.cs`, `TaxiVariantResolver` use `TaxiPathfinderRouter.Current` → **v2**.

**Safety valve:** `TaxiPathfinderRouter.UseV2 = false` restores `TaxiPathfinderV1Adapter`.

---

## Recommendations

1. **Keep v2 as default for auto-route** — grid evidence supports U-turn regression guard.

2. **Extend comparison harness to explicit paths** — Run `PathfinderComparison`-style diffs on SKW3404 (`A E B B3 A B1 Z S`), `TaxiRouteCatalog` named sequences, and a sample of OAK/SFO instructor routes. Assert U-turn ≤ v1 and document intentional failures.

3. **Run coverage under router, not static v1** — `TaxiCoverageRunner`, key E2E recordings (`Issue165SkwTaxiSpinTests`, `OakAllParkingTaxiAutoTests`, etc.) with `TaxiPathfinderRouter` pinned to v2 before deleting `TaxiPathfinder.cs`.

4. **Fix router XML** — Align `TaxiPathfinderRouter` doc comments with v2 default.

5. **Treat remaining ≥135° corners as layout/fillet work** — Paths that resolve but violate jet limits at E/B/B3 are topology constraints; see `docs/plans/filletv2/` rather than stacking more pathfinder-only patches.

6. **Gate latency** — Add a soft CI budget or sampled timing on cross-field pairs before v1 deletion.

---

## Bottom line

**In a vacuum:** v2 is a solid, well-reasoned pathfinder rewrite — unified costing, admissibility-first search, explicit multi-junction handling, and structured failures. It is production-viable for **auto-routing** where the grid says so. It is **not** a finished “delete v1” project: `SegmentExpander` is large, sim-level drivability is still a separate layer, and explicit-path + full regression coverage lags the default switch.

**vs v1:** v2 is the architecture to keep; v1 is the behavior still trusted until explicit-path comparison and recording/E2E suites run systematically through `TaxiPathfinderRouter`. Flipping the default was defensible if auto-route U-turn reduction was the primary pain; it is **not** sufficient to claim #165 or “all taxi bugs” are pathfinder-fixed.

---

## Related docs

- `docs/plans/taxi-pathfinder-v2.md` — requirements and retirement criteria
- `docs/plans/taxi-pathfinder-v2-design.md` — algorithm and binding decisions
- `docs/plans/filletv2/` — fillet generator v2 (orthogonal but coupled to drivability)
- `docs/landing-and-runway-exit.md` — out of scope for pathfinder (rollout uses separate planners)
