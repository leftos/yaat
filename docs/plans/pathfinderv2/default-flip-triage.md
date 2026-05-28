# V2 default-flip — test failure triage

When `TaxiPathfinderRouter._current` was flipped from `TaxiPathfinderV1Adapter` to `TaxiPathfinderV2`, the per-PR test suite went from 1 failure to **56** (excluding the `Category=Nightly` grids, which contributed another 354 — those need their own pass).

This doc tracks each failure and our review of it. **No bandaids** — each one needs a verdict on root cause before any change lands:

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
