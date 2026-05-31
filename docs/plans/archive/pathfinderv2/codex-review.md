# TaxiPathfinder v2 Review

Date: 2026-05-28

Scope: static review only. I did not run tests, per request.

## Verdict

TaxiPathfinder v2 is a much better architecture than v1: it has a small public facade, decomposed routing/search/materialisation components, structured failures, category-aware turn admissibility, and explicit-route logic that is no longer one giant method. In a vacuum, it is a promising replacement foundation.

It is not ready to replace v1 yet. Several v1 behaviors that matter to controller-facing semantics are missing or regressed, especially runway-destination hold-short semantics, reciprocal-runway hold-short matching, full-length lineup threshold selection, and authorized-route containment during detours. The search code also has a correctness risk from pruning by node while the route state is path-dependent.

My recommendation: keep v1 as the default until the high findings below are fixed and v2 gets broader behavior-level coverage over existing v1 scenarios, especially explicit taxi instructions and runway hold-short workflows.

## Findings

### High: Runway-destination routes lose `DestinationRunway` semantics

V2 materialises runway hold-short points as either `ExplicitHoldShort` or `RunwayCrossing`, but I did not find a path that emits `HoldShortReason.DestinationRunway`. `TaxiRoute.ToSummary()` only includes the destination runway text when that reason is present, so a taxi-to-runway route can lose the `RWY <id>` summary semantics that downstream code/tests expect.

References:

- `src/Yaat.Sim/Data/Airport/Pathfinding/RouteMaterialiser.cs:84`
- `src/Yaat.Sim/Data/Airport/Pathfinding/RouteMaterialiser.cs:154`
- `src/Yaat.Sim/Data/Airport/TaxiRoute.cs:153`
- v1 contrast: `src/Yaat.Sim/Data/Airport/HoldShortAnnotator.cs:283`
- v1 call site: `src/Yaat.Sim/Data/Airport/TaxiPathfinder.cs:431`

Recommendation: restore a distinct destination-runway annotation in v2 materialisation/truncation, then cover it with behavior tests that assert both the hold-short reason and the route summary.

### High: Explicit hold-short matching is too literal for reciprocal runways

V2 checks explicit hold-shorts with exact string membership against the runway id found on the hold-short node. That misses normal reciprocal strings such as an instruction to hold short `28R` when the graph node is tagged `28R/10L`. In that case v2 can leave the hold-short as a runway crossing instead of promoting it to an explicit hold-short.

References:

- `src/Yaat.Sim/Data/Airport/Pathfinding/RouteMaterialiser.cs:84`
- v1 contrast: `src/Yaat.Sim/Data/Airport/HoldShortAnnotator.cs:177`
- v1 contrast: `src/Yaat.Sim/Data/Airport/HoldShortAnnotator.cs:204`

Recommendation: reuse the same `RunwayIdentifier.Contains(...)` style matching v1 uses, rather than exact string matching.

### High: Full-length lineup hold-short selection is runway-end ambiguous

V2's `FindFullLengthLineupHoldShort` approximates the requested threshold by finding runway-centerline geometry and then choosing a farthest point from the runway centroid. That does not use the requested runway end's threshold coordinates, so reciprocal runways can choose the wrong end depending on graph shape and enumeration order. V1 asks `NavigationDatabase` for the requested runway and selects near the real threshold.

References:

- `src/Yaat.Sim/Data/Airport/Pathfinding/RouteMaterialiser.cs:238`
- `src/Yaat.Sim/Data/Airport/Pathfinding/RouteMaterialiser.cs:294`
- v1 contrast: `src/Yaat.Sim/Data/Airport/TaxiPathfinder.cs:647`

Recommendation: use the navigation database runway threshold for the requested designator in v2 as v1 does, with the current geometric fallback only as a fallback.

### High: A* pruning is not state-aware enough to be correct

`AutoRouter` and the explicit-path local search prune by best cost per node id. That would be valid if future admissibility/cost depended only on the node. Here it depends on route state: arrival bearing, last edge, last taxiway, aircraft category, and visited nodes. A cheaper arrival at a node can suppress a slightly more expensive arrival that has a viable heading or taxiway continuity for the next edge.

References:

- `src/Yaat.Sim/Data/Airport/Pathfinding/AutoRouter.cs:115`
- `src/Yaat.Sim/Data/Airport/Pathfinding/AutoRouter.cs:208`
- `src/Yaat.Sim/Data/Airport/Pathfinding/AutoRouter.cs:218`
- `src/Yaat.Sim/Data/Airport/Pathfinding/AutoRouter.cs:228`
- `src/Yaat.Sim/Data/Airport/Pathfinding/SegmentExpander.cs:396`
- `src/Yaat.Sim/Data/Airport/Pathfinding/SegmentExpander.cs:492`

Recommendation: key the closed/best-cost state by the route state that affects future expansion, at minimum node plus arrival/last-edge equivalence, or avoid pruning states that are not dominance-comparable.

### High: Detour fallback can silently use unauthorized full taxiways

The detour comments say fallback detours permit only numbered connectors and `RAMP` edges. The implementation builds a detour context by clearing `AuthorizedTaxiways`, then runs the normal auto-router. That allows all taxiways with no unauthorized-taxiway penalty during selection. For controller-issued taxi routes, this is risky: FAA 7110.65 3-7-2 and AIM 4-3-18 both center on specific taxi routing and explicit runway crossing instructions.

References:

- `src/Yaat.Sim/Data/Airport/Pathfinding/SegmentExpander.cs:1122`
- `src/Yaat.Sim/Data/Airport/Pathfinding/SegmentExpander.cs:1171`
- `src/Yaat.Sim/Data/Airport/Pathfinding/SegmentExpander.cs:1187`
- `src/Yaat.Sim/Data/Airport/Pathfinding/SearchContext.cs:67`
- `src/Yaat.Sim/Data/Airport/Pathfinding/RouteCostFunction.cs:153`

Recommendation: make the fallback policy explicit in the admissibility/cost layer. If the intended policy is numbered/RAMP-only, enforce that. If a broader fallback is intentionally allowed, surface it as a warning/failure mode rather than silently routing across unassigned full taxiways.

### Medium: `FindRoutes` is not a real v1 alternative-route replacement

V1 implements a Yen-style k-shortest alternative generator and can return multiple alternatives per strategy. V2 returns exactly one route when a preference is supplied, and at most one unique route for each of three hard-coded preferences when preference is null. The client asks for up to four alternatives, but v2 can only produce three and often fewer.

References:

- `src/Yaat.Sim/Data/Airport/TaxiPathfinder.cs:70`
- `src/Yaat.Sim/Data/Airport/TaxiPathfinder.cs:690`
- `src/Yaat.Client/ViewModels/GroundViewModel.cs:789`

Recommendation: either document v2 as intentionally weaker for alternatives, or port a state-aware k-alternative search before treating it as a v1 replacement.

### Medium: Natural-terminus walking is still greedy

One of the stated v2 goals is to avoid v1's greedy lock-in. The last explicit-path leg can still use `WalkToNaturalTerminus`, which chooses the best immediate next edge one step at a time without global backtracking. That can choose a locally attractive branch that misses the intended taxiway terminus.

References:

- `src/Yaat.Sim/Data/Airport/Pathfinding/SegmentExpander.cs:619`
- `docs/plans/pathfinderv2/requirements.md:124`

Recommendation: use the same multi-candidate search discipline for natural terminus expansion that v2 uses elsewhere, or restrict the greedy walk to cases where topology proves the next edge is forced.

### Medium: `RAMP` is treated as a letter-only taxiway

`SearchContext.IsLetterOnlyTaxiway` returns true for any non-empty taxiway name without digits and without `#`. That includes `RAMP`, so `RAMP` edges can receive unauthorized-taxiway penalties and warnings even though the requirements classify `RAMP` as apron/parking access.

References:

- `src/Yaat.Sim/Data/Airport/Pathfinding/SearchContext.cs:97`
- `src/Yaat.Sim/Data/Airport/Pathfinding/RouteCostFunction.cs:153`
- `src/Yaat.Sim/Data/Airport/Pathfinding/RouteMaterialiser.cs:210`
- `docs/plans/pathfinderv2/requirements.md:169`

Recommendation: classify `RAMP` separately from lettered taxiways in the shared taxiway-name classifier.

### Medium: Fastest cost mixes units

`RouteCostFunction` says costs are nautical-mile-equivalent, but the `Fastest` branch adds seconds (`distance / speed`) into the same scalar used for distance and penalties. That makes "fastest" hard to reason about and weakens the admissibility claim.

References:

- `src/Yaat.Sim/Data/Airport/Pathfinding/RouteCostFunction.cs:3`
- `src/Yaat.Sim/Data/Airport/Pathfinding/RouteCostFunction.cs:80`

Recommendation: keep one unit for the full cost model, or rename/document the mixed scalar and adjust heuristic claims accordingly.

### Low: Detour expansion limit is declared but not used

`MaxDetourExpansions` exists, but bounded detours call `AutoRouter.Run` without applying that limit. The actual cap appears to be the broader auto-router expansion limit.

References:

- `src/Yaat.Sim/Data/Airport/Pathfinding/SegmentExpander.cs:19`
- `src/Yaat.Sim/Data/Airport/Pathfinding/SegmentExpander.cs:1187`

Recommendation: either enforce the intended bounded detour cap or remove the unused constant/comment.

### Low: Priority tie-breaker comment disagrees with the code

The auto-router says shallower routes should be preferred on exact ties, but the priority subtracts `Depth * 1e-9`, which gives deeper routes a slightly lower priority in .NET's min-priority queue.

Reference:

- `src/Yaat.Sim/Data/Airport/Pathfinding/AutoRouter.cs:251`

Recommendation: fix the sign or the comment.

### Low: Client preview category can diverge from simulation routing

The command handler passes the aircraft's real category into routing. Several client preview paths hard-code `AircraftCategory.Jet`, so previews can disagree with sim execution for turboprops, pistons, or helicopters.

References:

- `src/Yaat.Client/ViewModels/GroundViewModel.cs:572`
- `src/Yaat.Client/ViewModels/GroundViewModel.cs:796`
- `src/Yaat.Client/ViewModels/GroundViewModel.cs:1151`
- `src/Yaat.Client/ViewModels/GroundViewModel.cs:1254`
- `src/Yaat.Sim/Commands/GroundCommandHandler.cs:66`

Recommendation: feed the same aircraft category into preview routing that the command handler will use for execution.

## In a Vacuum

V2's shape is good. `TaxiPathfinder` is small, routing concerns are split into `AutoRouter`, `SegmentExpander`, `RouteCostFunction`, `GeometricAdmissibility`, `RouteMaterialiser`, and `SearchContext`, and failures are first-class values instead of just empty routes. This is the right direction for maintainability.

The strongest idea is moving geometric legality into the search, not only post-processing. If an edge is impossible for a jet because of arrival heading and turn angle, it should be rejected while expanding the route. That is much better than v1's tendency to discover bad geometry late.

The main weakness is that v2 is not consistently state-aware. Once cost/admissibility depends on how the route arrived at a node, the search state has to encode that arrival context. Otherwise the implementation can look like A* but not have A*'s correctness properties.

The second weakness is semantic completeness. The architecture is clean, but several aviation-facing route meanings still leak or collapse during materialisation: destination runway versus crossing, explicit hold-short versus reciprocal runway id, and assigned-taxiway versus fallback detour.

## Compared To V1

V1 is harder to maintain. The old pathfinder has large methods, intertwined explicit-path and auto-routing behavior, post-hoc annotators, and more special cases in one place than is healthy. V2 is easier to reason about module-by-module and should be easier to improve.

V1 is also more battle-hardened. It still owns mature behavior around destination runway hold-shorts, reciprocal runway matching, true threshold-based full-length lineup selection, start-node taxiway authorization, and k-shortest alternative routes. V2 currently reimplements some of that surface but does not yet match all of those semantics.

The practical comparison is therefore split:

- Architecture: v2 wins.
- Search extensibility: v2 should win once state pruning is fixed.
- Current controller-facing behavior: v1 still wins in important runway and explicit-route cases.
- Alternative route generation: v1 currently wins.
- Readability and future maintainability: v2 wins.
- Swap readiness: v1 remains safer today.

## Static Test Coverage Review

I reviewed tests statically and did not run them.

Current v2 coverage is useful but narrow. The unit tests cover synthetic graphs and basic success/failure paths. The grid comparison tests compare v1 and v2 for auto-routing, but they do not cover explicit `ResolveExplicitPath` behavior, and they do not prove parity for route summaries, hold-short reasons, warnings, or selected runway-end thresholds.

The Issue 165 v2 test asserts that the route resolves, but it does not fail on geometric violations. The full replay-style orbit guard is currently skipped, so it does not protect the default behavior at review time.

The biggest missing coverage areas are:

- [ ] Destination runway routes emit `DestinationRunway` hold-short reasons and preserve `RWY <id>` summaries.
- [ ] Explicit hold-short instructions match reciprocal runway node ids such as `28R` against `28R/10L`.
- [ ] Full-length lineup uses the requested runway end, not the reciprocal threshold.
- [ ] Explicit-route detours cannot use unauthorized full taxiways silently.
- [ ] V2 alternative route generation returns meaningful alternatives comparable to v1, or the UI expectation is reduced.
- [ ] Non-jet preview routing uses the same aircraft category as command execution.
- [ ] State-dependent pruning preserves viable arrivals with different bearings/last edges.

## Replacement Gate

Before flipping v2 on by default or deleting v1, I would require:

- [x] Fix the high findings in this review. *(All 5 landed: DestinationRunway reason, reciprocal HS matching, full-length lineup threshold, state-aware A\* pruning, detour authorized-taxiway policy.)*
- [x] Add behavior tests for the missing runway/hold-short semantics. *(Phase 5 — see Resolutions.)*
- [x] Run existing v1 taxi-pathfinder behavior tests against the v2 router where practical. *(V2/router behaviour coverage lives in the `*_OnV2` suites + `FilletV2TaxiCoverageTests` + the explicit-path comparison; the synthetic v1-algorithm unit tests stay pinned to v1 and are deleted with it.)*
- [x] Expand comparison tests to explicit-path routes, not only auto `FindRoute`. *(`PathfinderComparison.CompareExplicit` + `ExplicitPathComparisonTests`.)*
- [x] Decide whether v2 must support v1-style k alternatives. *(Decided: no — v2 is intentionally per-preference; see Resolutions.)*
- [ ] Keep v1 available until v2 passes the same controller-facing scenarios, not just the same synthetic graph scenarios. *(Held until the joint flip — `docs/plans/ground-graph-v2.md`.)*

## Resolutions (Phase 5, 2026-05-30)

| Finding | Severity | Resolution |
|---------|----------|------------|
| DestinationRunway reason lost | HIGH | Fixed (v2 RouteMaterialiser emits it); `ToSummary` "RWY <id>" behaviour test added. |
| Reciprocal HS matching too literal | HIGH | Fixed (`RunwayIdentifier.Contains`); test `ExplicitHoldShort_ConfiguredInContext_TaggedCorrectly`. |
| Full-length lineup runway-end ambiguous | HIGH | Fixed (authoritative `NavigationDatabase` threshold); test `…PicksBarNearestRequestedThreshold`. |
| A\* pruning not state-aware | HIGH | Fixed (key by `(node, 1°-bearing-bucket)`); `StateAwarePruningTests` + necessity sweep. |
| Detour can use unauthorized full taxiways | HIGH | Fixed (soft penalise-and-warn, `MaxDetourExpansions` enforced); detour tests. |
| `FindRoutes` not real k-shortest | MED | **Decided: accept v2's per-preference model** (≤3 routes; client requests 3; documented). |
| Natural-terminus walking still greedy | MED | **Resolved by constraint** (final-leg only, direction-biased, single-name-preferring, parking/spot off-ramped; no failing repro). |
| `RAMP` treated as letter-only taxiway | MED | Fixed — excluded from `IsLetterOnlyTaxiway` (aviation-reviewed). |
| `Fastest` cost mixes units | MED | **Decided: keep + document** (generic scalar; admissible-but-weak Fastest heuristic; never the default preference). |
| Detour expansion limit unused | LOW | Stale — `MaxDetourExpansions` is enforced (`SegmentExpander.cs` `RunBoundedDetour`). |
| Priority tie-breaker comment | LOW | Fixed the comment (code correctly prefers deeper routes — standard A\* tie-break). |
| Client preview category divergence | LOW | Fixed — real category threaded through every preview path (`GroundViewModel.CategoryFor`). |
