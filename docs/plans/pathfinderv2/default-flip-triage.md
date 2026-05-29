# V2 resume plan — default-flip triage and follow-up work

When `TaxiPathfinderRouter._current` was flipped from `TaxiPathfinderV1Adapter` to `TaxiPathfinderV2`, the per-PR test suite went from 1 failure to **56** (excluding the `Category=Nightly` grids, which contributed another 354 — those need their own pass). The default was reverted (commit `46e13d5a`) so the fillet-arc-generator rewrite can land on a green tree first.

This is the entry point when V2 work resumes. It covers:

1. The 56-failure cluster breakdown (the gating list before flipping the default again).
2. Pre-flip work items pulled from `cursor-review.md` that still apply.
3. Items expected to become defunct after the fillet rewrite — re-evaluate before doing them.

**No bandaids** — each test failure needs a verdict on root cause before any change lands:

- **V2-bug**: V2 produces a wrong route or fails to find a valid one. Fix V2.
- **V1-pinned**: Test asserts on V1's specific output (segment count, exact node ids, exact route shape) where the contract is looser. Relax the assertion to match the loosest correct behaviour.
- **Missing-feature**: V2 doesn't yet implement something V1 does (e.g. some variant resolution, hold-short ordering). Implement in V2.
- **Underlying sim issue**: The failure is downstream of routing (GroundNavigator, phase wiring) and V2 just happens to surface it. File a separate issue and route around it.

## Reproducing

Run from `X:\dev\yaat`:

```bash
timeout 300 dotnet test tests/Yaat.Sim.Tests/Yaat.Sim.Tests.csproj \
    --filter "Category!=Nightly" 2>&1 \
    | tee .tmp/test-yaat-v2-prfilter.log | tail -3
```

For a single failure:

```bash
timeout 60 dotnet test tests/Yaat.Sim.Tests/Yaat.Sim.Tests.csproj \
    --filter "FullyQualifiedName~<TestClass>.<TestMethod>" \
    --logger "console;verbosity=detailed" 2>&1 | tee .tmp/<short>.log
```

## Failures grouped by class

### Cluster A — TaxiCoverage smoke set (3 — real V2 routing breadth issue)

These are per-PR (not nightly) and gate the V2 routing surface for the issue-165 class of bugs.

- [ ] `TaxiCoverageOakTests.Pair_ReachesDestinationWithinBudgets(OAK_Gate4-to-30_piston)`
- [ ] `TaxiCoverageOakTests.Pair_ReachesDestinationWithinBudgets(OAK_Gate22-to-30_piston)`
- [ ] `TaxiCoverageOakTests.Pair_ReachesDestinationWithinBudgets(OAK_Gate22-to-30_jet)`

### Cluster B — ExtDuringTouchAndGoTests (6 — likely shared root cause)

All six tests in `ExtDuringTouchAndGoTests` fail. Same class, almost certainly same root cause.

- [ ] `CoptThenBareExt_DuringFinalApproach_ArmsNextUpwind`
- [ ] `CoptThenExtUpwind_DuringFinalApproach_ArmsNextUpwind`
- [ ] `ExtCrosswind_DuringTouchAndGo_StillRejects`
- [ ] `ExtDuringFinalApproach_BeforeTouchAndGo_ArmsNextUpwind`
- [ ] `ExtDuringHoldingShort_ArmsPendingUpwind_Directly`
- [ ] `ExtDuringTouchAndGo_ArmsNextUpwind`

### Cluster C — AirportE2ETests (5 — broad OAK departure coverage)

- [ ] `OAK_FullTaxiToTakeoff_DCBW_HoldShort30_HasPhases`
- [ ] `OAK_TaxiD_NeedsVariantForRunway30`
- [ ] `OAK_TaxiFromPCM_B_ToRunway28L_StopsAtFirstHoldShort`
- [ ] `OAK_TaxiFromParking_DCBW_ToRunway30_HasHoldShortAndPhases`
- [ ] `OAK_TaxiFromParking_D_Succeeds`

### Cluster D — SpotOvershootTaxiRouteTests (3)

- [ ] `TaxiM2ToSpot2_DoesNotOvershoot`
- [ ] `TaxiM4M1ToSpot1_DoesNotOvershoot`
- [ ] `TaxiT9ToSpot9_DoesNotOvershoot`

### Cluster E — S2Oak4RvSidCtoTests (3)

- [ ] `N346G_CtoFromHoldShort_Nimi6_Stores315OnClearanceAndInitialClimb`
- [ ] `N436MS_CtoDuringTaxi_Nimi6_Stores315OnClearanceAndInitialClimb`
- [ ] `OAK6_CtoDuringTaxi_Stores278HeadingFor28R`

### Cluster F — N7lj* recording-replay tests (4)

- [ ] `N7ljCrossingRunwayInfoTests.CrossingRunwayPhase_ReportsCrossingRunwayId_NotDepartureRunway`
- [ ] `N7ljResCrossCommaFormTests.ResCommaCross28L_ClearsCurrentHoldShort_AndPreClearsUpcoming28L`
- [ ] `N7ljResCrossPreClearTests.ResCross28L_ClearsCurrentHoldShort_AndPreClearsUpcoming28L`
- [ ] `N7ljResExplicitHoldShortTests.Res_ClearsExplicitHoldShort_AndAircraftResumesTaxi`
- [ ] `N7ljResExplicitHoldShortTests.Res_ClearsRunwayCrossingHoldShort_AndAircraftResumesTaxi`

### Cluster G — Hold-short & cross-runway tests (10)

- [ ] `OakCross28RHoldShortTests.RerouteFrom28R_ExitSideHoldShort_NotAddedAsCrossing`
- [ ] `OakCrossThenHoldOnNextTaxiwayTests.AfterRes_AircraftCrossesAndHoldsOnC`
- [ ] `OakCrossThenHoldOnNextTaxiwayTests.TaxiGCHs28R_RouteDoesNotWalkFullLengthOfC`
- [ ] `OakExplicitHsAutoCrossTests.ExplicitHs28R_OverridesAutoCross_HoldsOnEntrySide`
- [ ] `OakPostLandingReversalsTests.N436MS_TaxiC_AtJSX1_HasNoReversals`
- [ ] `IssueOakImplicitCrossOnTaxiTests.TaxiAcrossSameRunway_ImplicitlyClearsFirstCrossing`
- [ ] `IssueOakImplicitCrossOnTaxiTests.TaxiAcrossSameRunway_StillHoldsAtDestination`
- [ ] `IssueAmxTaxiOvershootTests.AMX669_HoldsShortOf1L_WithReasonableHeading`
- [ ] `Issue163BareCrossThenHoldTests.BareCrossThenHold_CrossesRunway28RAndHaltsPastFarSideHoldBars`
- [ ] `Issue166CrossShortcutsGrassTests.Ual19_FollowsHTaxiLineThroughRunwayCrossing`

### Cluster H — SFO taxi-route tests (5)

- [ ] `SfoHoldShortTaxiwayTests.N346G_CtoFromSeparateHsCommand_ResolvesDestinationRunway`
- [ ] `SfoHoldShortTaxiwayTests.N346G_LuawFromTaxiwayHoldShort_StoresClearanceAndResumes`
- [ ] `SfoLineupDiagonalTests.N346G_LineUp28R_CompletesWithOnCenterlineAlignedStop`
- [ ] `SfoM2MultiTurnTaxiTests.Test1_SpawnOffM2_TaxisThroughTwoNinetyTurns_AndTakesOff`
- [ ] `SfoRampCrossesRunwayTests.TaxiCommand_AcrossRunways_ShouldFail`
- [ ] `DiagonalLineup28rTests.N436MS_LineUp28R_CompletesWithOnCenterlineAlignedStop`
- [ ] `Bug157leCtoMltStuckTests.TaxiBwRwy30_RouteTerminatesAtHoldShort`

### Cluster I — CTO / initial-climb / departure tests (likely sim-side, not routing)

- [ ] `Issue133Rwy28rTakeoffTests.N172SP_LinesUpOnRunway_AfterLuaw`
- [ ] `Issue133Rwy28rTakeoffTests.N172SP_ReachesHoldShort_AfterTaxiDCB28R`
- [ ] `IssueSaltAfterCtoAltitudeTests.N172SP_AssignedAltitude_PopulatedFromCtoBundledAltitude`
- [ ] `IssueSaltAfterCtoAltitudeTests.N172SP_BuildAltitude_AnnouncesClimbToBundledAltitude`
- [ ] `N152spIfrCtoDeferralTests.BareCto_AcceptedForIfrAircraft`
- [ ] `N152spIfrCtoDeferralTests.Cto360_AcceptedForIfrAircraft`
- [ ] `AdctDuringInitialClimbTests.AdctVpmid_DoesNotCancelInitialClimb`
- [ ] `AtFixDuringInitialClimbTests.AtFixConditional_DoesNotCancelInitialClimb`
- [ ] `AutoCrossRunwayToggleTests.E2E_FirstCrossingClearance_SurvivesAutoCrossToggleOff`

### Cluster J — Pattern / Go-Around (3)

- [ ] `PatternDirectionResetTests.N172SP_AfterFhVector_PreservesPersistentMrt`
- [ ] `PatternDirectionResetTests.N342T_AfterErbAndCopt_NextCircuitResumesLeftTraffic`
- [ ] `GoAroundPreservesIntentE2ETests.N436MS_GoAroundFromVisualApproach_NextCircuitEndsWithLandingPhase`

### Cluster K — Pilot speech / command-handler (2)

- [ ] `Pilot.TwoPilotControllerResponseGateE2ETests.S2Oak4_TwoSimultaneousProactives_HoldSecondPilotForFullSilenceWindow`
- [ ] `GroundCommandHandlerTests.TryTaxi_UnknownTaxiway_Fails`

---

## Follow-up work (distilled from `cursor-review.md` and `codex-review.md`)

Both reviews flagged structural issues not tracked by individual test failures. Codex went deeper on V2 semantics (5 HIGH findings with file:line refs); Cursor went deeper on testing/migration ergonomics. Address all of these alongside the cluster walk above.

### Pre-flip — required before the default flips again

#### Codex HIGH findings (real V2 behavioural gaps)

- [ ] **`DestinationRunway` hold-short reason is missing in V2 materialisation.** Routes to a runway destination should emit `HoldShortReason.DestinationRunway` so `TaxiRoute.ToSummary()` includes the `RWY <id>` semantics. V2 only emits `ExplicitHoldShort` / `RunwayCrossing`. (`src/Yaat.Sim/Data/Airport/V2/RouteMaterialiser.cs:84, 154`; V1 contrast `src/Yaat.Sim/Data/Airport/HoldShortAnnotator.cs:283`)
- [ ] **Explicit hold-short matching is too literal for reciprocal runways.** V2 uses exact string membership; misses `28R` matching `28R/10L`. Use `RunwayIdentifier.Contains(...)` instead. (`src/Yaat.Sim/Data/Airport/V2/RouteMaterialiser.cs:84`; V1 contrast `HoldShortAnnotator.cs:177, 204`)
- [ ] **Full-length lineup hold-short selection is runway-end ambiguous.** V2's `FindFullLengthLineupHoldShort` uses farthest-from-centroid geometry; reciprocal runways can pick the wrong end. Use `NavigationDatabase` threshold for the requested designator as V1 does, with current geometry as fallback. (`src/Yaat.Sim/Data/Airport/V2/RouteMaterialiser.cs:238, 294`; V1 contrast `TaxiPathfinder.cs:647`)
- [ ] **A\* pruning is not state-aware.** `AutoRouter` and `LocalSearchToJunction` prune by best cost per node id, but future admissibility/cost depends on arrival bearing, last edge, last taxiway, aircraft category, and visited nodes. A cheaper arrival can suppress a slightly-more-expensive viable arrival. Key closed/best-cost state by the route state that affects future expansion. (`AutoRouter.cs:115, 208, 218, 228`; `SegmentExpander.cs:396, 492`)
- [ ] **Detour fallback can silently use unauthorized full taxiways.** Detour context clears `AuthorizedTaxiways` then runs the normal auto-router, which lets all taxiways through with no unauthorized-taxiway penalty. Make the fallback policy explicit — enforce numbered/RAMP-only or surface as a warning/failure. (`SegmentExpander.cs:1122, 1171, 1187`; `SearchContext.cs:67`; `RouteCostFunction.cs:153`)

#### Test coverage (Cursor + Codex coverage gaps)

- [ ] **Strengthen `Issue165_V2_SkwRoute_ResolvesWithoutFailure`** in `tests/Yaat.Sim.Tests/Pathfinding/V2/SegmentExpanderTests.cs`. Today the assertion is `Assert.NotNull(route)`. Replace with: no 180° corners on the resolved route, and V2 U-turn count ≤ V1 for the same waypoint sequence.
- [ ] **Add explicit-path coverage to `PathfinderComparison`** (`tests/Yaat.Sim.Tests/Helpers/PathfinderComparison.cs`). Today the grid harness only diffs auto-route. Mirror it for named sequences — SKW3404's `A E B B3 A B1 Z S`, `TaxiRouteCatalog` entries, a representative slice of instructor scripts. Same metric: V2 U-turn count ≤ V1.
- [ ] **Re-pin the V1-static call sites.** ~48 call sites in `TaxiPathfinderTests.cs` and the `Skw3404_Seg12_PathfinderDiagnostic` diagnostic (`Issue165SkwTaxiSpinTests.cs:217`) still call `TaxiPathfinder.ResolveExplicitPath` directly. Either migrate them to `TaxiPathfinderRouter.Current` or label them explicitly as V1-only regression pins.
- [ ] **Add behaviour tests for the Codex HIGH findings:** `DestinationRunway` reason + `RWY <id>` summary; reciprocal runway matching (`28R` vs `28R/10L`); full-length lineup picks requested runway end; detour cannot silently use unauthorized full taxiways; non-jet preview routing uses the same category as command execution; state-dependent pruning preserves viable arrivals with different bearings.

#### Design gaps to decide on (Codex MED)

- [ ] **`FindRoutes` is not a real k-shortest alternative replacement.** V2 returns ≤3 alternatives (one per hard-coded preference); client asks for 4 (`Yaat.Client/ViewModels/GroundViewModel.cs:789`). V1 uses Yen-style k-shortest. Either port the k-alternative search or document V2 as intentionally weaker. (`TaxiPathfinderV2.cs:70`; V1 contrast `TaxiPathfinder.cs:690`)
- [ ] **Natural-terminus walking is still greedy.** `WalkToNaturalTerminus` picks the best immediate next edge one step at a time. V2 was supposed to avoid V1's greedy lock-in. Apply the multi-candidate search discipline V2 uses elsewhere, or restrict the greedy walk to forced-next-edge topologies. (`SegmentExpander.cs:619`; `docs/plans/pathfinderv2/requirements.md:124`)
- [ ] **`RAMP` misclassified as letter-only taxiway.** `IsLetterOnlyTaxiway` returns true for `RAMP`, so RAMP edges get unauthorized-taxiway penalties / warnings even though the requirements classify `RAMP` as apron/parking access. (`SearchContext.cs:97`; `RouteCostFunction.cs:153`; `RouteMaterialiser.cs:210`)
- [ ] **`Fastest` cost mixes units.** `RouteCostFunction` is documented as NM-equivalent but the `Fastest` branch adds `distance / speed` (seconds) into the same scalar as distance and penalties. Pick one unit or rename/document the mixed scalar. (`RouteCostFunction.cs:3, 80`)
### During-flip — when the default flips

- [ ] **Update `TaxiPathfinderRouter` XML doc comments.** When the default flips again, update the class header and the `Current` property prose (`src/Yaat.Sim/Data/Airport/TaxiPathfinderRouter.cs:23–26`). The default has been toggled twice without keeping the doc in sync.

### Post-flip — cleanup before deleting V1

- [ ] **Stand up a latency CI budget** for cross-field pairs. The 200k expansion cap in `AutoRouter` (`src/Yaat.Sim/Data/Airport/V2/AutoRouter.cs:12`) is a footgun. Design doc target was median ≤ 2× V1; even a soft trait-gated test that prints the median and fails on >5× would be enough.
- [ ] **Split `SegmentExpander.cs`** (1095 lines as of `46e13d5a`). Self-contained sub-pipelines to extract: variant resolution (`TryVariantExtension` / `ExtendToVariant`), detour fallback (`TryDetour` / `RunBoundedDetour`), node-ref routing (`RouteToNodeRef` / `RouteFromNodeRefToTaxiway`).
- [ ] **Reconcile `DirectionReversalCostNm` asymmetry.** Currently applied in `SegmentExpander.LocalSearchToJunction` only, omitted from `AutoRouter.IncrementalCost` to preserve A\* heuristic admissibility (design decision §7). Cursor flagged that auto-routes will be more zig-zaggy than explicit ones. Either accept the asymmetry and pin it with a test, or move the reversal cost into the heuristic itself so admissibility still holds.
- [ ] **`MaxDetourExpansions` is declared but not enforced.** Bounded detours call `AutoRouter.Run` without applying the limit. (`SegmentExpander.cs:19, 1187`) Either enforce it or remove the unused constant.
- [ ] **Priority tie-breaker sign is wrong.** AutoRouter comment says shallower routes win on ties, but the code subtracts `Depth * 1e-9`, giving deeper routes lower priority in .NET's min-priority queue. (`AutoRouter.cs:251`) Fix the sign or the comment.
- [ ] **Client preview category mismatch.** `GroundViewModel` preview paths hard-code `AircraftCategory.Jet`; command-handler execution uses the real aircraft category. Previews diverge from sim execution for turboprops/pistons/helicopters. (`GroundViewModel.cs:572, 796, 1151, 1254`; `GroundCommandHandler.cs:66`)

### Probably-defunct after the fillet rewrite — re-evaluate before doing

These changes addressed symptoms of the same fillet data quality the rewrite targets. If the rewrite produces clean fillet edges by construction, both become dead code.

- [ ] **`IsNoOpEdge` guard in V2** (commit `edd96bf7`, `src/Yaat.Sim/Data/Airport/V2/GeometricAdmissibility.cs`). If the rewrite removes zero-distance edges at the source, this is no longer load-bearing. Decide: keep as defence-in-depth, or rip out so V2 reads bearings cleanly.
- [ ] **B1's tangent-on-anchor merge pass in `FilletArcGenerator`** (commit `fd7e35fe`, `BuildMergeMap` second pass). If the rewrite makes co-located tangent/anchor placements impossible by construction, this post-merge pass is no longer needed.

## Fillet-V2 interaction findings (from the fillet V2 sim-validation sweep)

The fillet-arc-generator V2 rewrite (`docs/plans/filletv2/`) is geometry-validated and was sim-validated
behind the switch. A full-suite run with the fillet default flipped to V2 (pathfinder still on V1)
produced only 5 failures, and triage (`docs/plans/filletv2/v2-sim-validation.md`) traced them to the
**routing/navigation layer**, not the fillet geometry. Because pathfinder V1 is being replaced, these are
recorded here as V2 requirements rather than V1 fixes. **When fillet V2 and pathfinder V2 flip together,
the V2 router + navigator must handle these or the failures resurface.**

- [ ] **Prefer single-name continuation over membership-matching junction arcs (reinforces the greedy
      natural-terminus gap above).** V2's edge-split collapses each junction into fewer tangent nodes with
      larger bearing steps, and retains fillet junction arcs named e.g. `C1 - B`, `A - RAMP`, `A - Q1`.
      A bare-taxiway walk matches these arcs **by membership** (`GroundArc.MatchesTaxiway` checks
      `TaxiwayNames` membership), so when continuing taxiway `B` the walk sees the `C1 - B` junction arc as
      a valid `B` continuation. Where the true continuation is now an *arc* whose bearing competes closely
      with the junction arc (or a short edge), V1's straightest-continuation heuristic picks the wrong one
      → dead-end spur / oscillation → `X→Y→X` reversal (FLL B/C1 nodes 765↔767; SFO A nodes 1160↔43).
      **V2 requirement:** when extending the *same* named taxiway, an exact single-name edge/arc must rank
      strictly above a junction arc that matches only by membership; and the multi-candidate search (not a
      one-step-greedy `WalkToNaturalTerminus`) must reject a candidate that immediately backtracks. Repro:
      `IssueFllDal880TaxiBacktrackBTests`, `Issue166CrossShortcutsGrassTests` (both pass on Legacy fillets).
> **Out of scope here:** fillet-V2 root cause ② (AMX669 freeze) is a **GroundNavigator** issue
> (route *following*, not route resolution), and pathfinder V2 does not change the navigator. It is
> tracked as a separate navigator review in
> [docs/plans/filletv2/v2-sim-validation.md](../filletv2/v2-sim-validation.md), not as a pathfinder V2
> requirement.

- [ ] **Re-run the fillet-V2 full-suite sweep after ① + the navigator review.**
      `S2Oak4RvSidCtoTests.N436MS_CtoDuringTaxi…` (a CTO/InitialClimb replay cascade) is expected to clear
      once the routing/navigation causes are fixed; re-triage rather than touch the CTO path directly.

### Replacement gate (Codex)

Before flipping V2 on by default or deleting V1, the Codex review explicitly required:

- All HIGH findings fixed
- Behavior tests added for missing runway/hold-short semantics
- Existing V1 taxi-pathfinder behaviour tests running against the V2 router
- Explicit-path comparison tests (not only auto `FindRoute`)
- Decision on k-alternative support
- V1 stays available until V2 passes the same controller-facing scenarios — not just the same synthetic-graph scenarios
