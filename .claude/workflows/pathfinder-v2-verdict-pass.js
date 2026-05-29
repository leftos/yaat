// Named workflow: root-cause verdict for each fillet-V2 + pathfinder-V2 ship-target
// test failure (the "56-cluster" triage). Invoke with: Workflow({name: 'pathfinder-v2-verdict-pass'})
// (no args needed — the failure list is embedded below; pass args to override).
//
// Depends on per-test failure blocks at .tmp/triage-blocks/<Method>.log. If those are
// missing, regenerate them: flip TaxiPathfinderRouter._current -> TaxiPathfinderV2 and
// TestAirportGroundData() default -> FilletMode.V2, run
//   dotnet test tests/Yaat.Sim.Tests/... --filter "Category!=Nightly" --logger "console;verbosity=detailed" | tee .tmp/triage-v2v2-detail.log
// revert both flips, then split the failure-summary blocks (one file per "Failed <FQN>" block,
// 18KB cap) into .tmp/triage-blocks/. See docs/plans/pathfinderv2/default-flip-triage.md.
export const meta = {
  name: 'pathfinder-v2-verdict-pass',
  description: 'Root-cause verdict for each fillet-V2 + pathfinder-V2 ship-target test failure',
  phases: [{ title: 'Verdict', detail: 'one agent per failing test class; read per-test block file + source, classify' }],
}

const BLOCKS = '.tmp/triage-blocks'

// The 54 ship-target (pathfinder-V2 + fillet-V2) failures as of the 2026-05-29 re-baseline.
const FAILURES = [
  'Yaat.Sim.Tests.AirportE2ETests.OAK_FullTaxiToTakeoff_DCBW_HoldShort30_HasPhases',
  'Yaat.Sim.Tests.AirportE2ETests.OAK_HoldShortNodes_NotAtJunctions',
  'Yaat.Sim.Tests.AirportE2ETests.OAK_TaxiD_NeedsVariantForRunway30',
  'Yaat.Sim.Tests.AirportE2ETests.OAK_TaxiFromParking_DCBW_ToRunway30_HasHoldShortAndPhases',
  'Yaat.Sim.Tests.GroundCommandHandlerTests.TryTaxi_UnknownTaxiway_Fails',
  'Yaat.Sim.Tests.Pilot.TwoPilotControllerResponseGateE2ETests.S2Oak4_TwoSimultaneousProactives_HoldSecondPilotForFullSilenceWindow',
  'Yaat.Sim.Tests.Simulation.AdctDuringInitialClimbTests.AdctVpmid_DoesNotCancelInitialClimb',
  'Yaat.Sim.Tests.Simulation.AtFixDuringInitialClimbTests.AtFixConditional_DoesNotCancelInitialClimb',
  'Yaat.Sim.Tests.Simulation.AutoCrossRunwayToggleTests.E2E_FirstCrossingClearance_SurvivesAutoCrossToggleOff',
  'Yaat.Sim.Tests.Simulation.AutoCrossRunwayToggleTests.E2E_ToggleOn_DoesNotPopAircraftCurrentlyInHoldingShortPhase',
  'Yaat.Sim.Tests.Simulation.Bug157leCtoMltStuckTests.TaxiBwRwy30_RouteTerminatesAtHoldShort',
  'Yaat.Sim.Tests.Simulation.DiagonalLineup28rTests.N436MS_LineUp28R_CompletesWithOnCenterlineAlignedStop',
  'Yaat.Sim.Tests.Simulation.ExtDuringTouchAndGoTests.CoptThenBareExt_DuringFinalApproach_ArmsNextUpwind',
  'Yaat.Sim.Tests.Simulation.ExtDuringTouchAndGoTests.CoptThenExtUpwind_DuringFinalApproach_ArmsNextUpwind',
  'Yaat.Sim.Tests.Simulation.ExtDuringTouchAndGoTests.ExtCrosswind_DuringTouchAndGo_StillRejects',
  'Yaat.Sim.Tests.Simulation.ExtDuringTouchAndGoTests.ExtDuringFinalApproach_BeforeTouchAndGo_ArmsNextUpwind',
  'Yaat.Sim.Tests.Simulation.ExtDuringTouchAndGoTests.ExtDuringHoldingShort_ArmsPendingUpwind_Directly',
  'Yaat.Sim.Tests.Simulation.ExtDuringTouchAndGoTests.ExtDuringTouchAndGo_ArmsNextUpwind',
  'Yaat.Sim.Tests.Simulation.FilletDiagnosticTests.SKW3078_TaxiAtoB10_AdvancesPastFormerStallSegment',
  'Yaat.Sim.Tests.Simulation.GoAroundPreservesIntentE2ETests.N342T_AfterManualGoAroundFromTouchAndGo_NextCircuitEndsWithTouchAndGoPhase',
  'Yaat.Sim.Tests.Simulation.GoAroundPreservesIntentE2ETests.N436MS_GoAroundFromVisualApproach_NextCircuitEndsWithLandingPhase',
  'Yaat.Sim.Tests.Simulation.Issue133Rwy28rTakeoffTests.N172SP_LinesUpOnRunway_AfterLuaw',
  'Yaat.Sim.Tests.Simulation.Issue133Rwy28rTakeoffTests.N172SP_ReachesHoldShort_AfterTaxiDCB28R',
  'Yaat.Sim.Tests.Simulation.Issue163BareCrossThenHoldTests.BareCrossThenHold_CrossesRunway28RAndHaltsPastFarSideHoldBars',
  'Yaat.Sim.Tests.Simulation.Issue165SkwTaxiSpinTests.Skw3404_Seg12_PathfinderDiagnostic',
  'Yaat.Sim.Tests.Simulation.Issue165SkwTaxiSpinTests.Skw3404_StuckMoment_Diagnostic',
  'Yaat.Sim.Tests.Simulation.Issue166CrossShortcutsGrassTests.Ual19_FollowsHTaxiLineThroughRunwayCrossing',
  'Yaat.Sim.Tests.Simulation.IssueAmxTaxiOvershootTests.AMX669_HoldsShortOf1L_WithReasonableHeading',
  'Yaat.Sim.Tests.Simulation.IssueFllDal880TaxiBacktrackBTests.ResolveExplicitPath_TT4BB1_FromDal880Parking_RouteHasNoUTurn',
  'Yaat.Sim.Tests.Simulation.IssueOakImplicitCrossOnTaxiTests.TaxiAcrossSameRunway_ImplicitlyClearsFirstCrossing',
  'Yaat.Sim.Tests.Simulation.IssueOakImplicitCrossOnTaxiTests.TaxiAcrossSameRunway_StillHoldsAtDestination',
  'Yaat.Sim.Tests.Simulation.N7ljCrossingRunwayInfoTests.CrossingRunwayPhase_ReportsCrossingRunwayId_NotDepartureRunway',
  'Yaat.Sim.Tests.Simulation.N7ljResCrossCommaFormTests.ResCommaCross28L_ClearsCurrentHoldShort_AndPreClearsUpcoming28L',
  'Yaat.Sim.Tests.Simulation.N7ljResCrossPreClearTests.ResCross28L_ClearsCurrentHoldShort_AndPreClearsUpcoming28L',
  'Yaat.Sim.Tests.Simulation.N7ljResExplicitHoldShortTests.Res_ClearsExplicitHoldShort_AndAircraftResumesTaxi',
  'Yaat.Sim.Tests.Simulation.N7ljResExplicitHoldShortTests.Res_ClearsRunwayCrossingHoldShort_AndAircraftResumesTaxi',
  'Yaat.Sim.Tests.Simulation.N929awClandErbRunwayOverrunTests.N342T_TouchAndGoCompletes_AutoCyclesIntoAnotherTouchAndGoCircuit',
  'Yaat.Sim.Tests.Simulation.OakCross28RHoldShortTests.RerouteFrom28R_ExitSideHoldShort_NotAddedAsCrossing',
  'Yaat.Sim.Tests.Simulation.OakCrossThenHoldOnNextTaxiwayTests.AfterRes_AircraftCrossesAndHoldsOnC',
  'Yaat.Sim.Tests.Simulation.OakExplicitHsAutoCrossTests.ExplicitHs28R_OverridesAutoCross_HoldsOnEntrySide',
  'Yaat.Sim.Tests.Simulation.OakGaSpawnTurnAroundTests.TaxiOut_DoesNotSpinNearlyFullCircle',
  'Yaat.Sim.Tests.Simulation.OakNorthFieldTaxiSpinTests.TaxiOut_DoesNotSpinNearlyFullCircle',
  'Yaat.Sim.Tests.Simulation.OakPostLandingReversalsTests.N436MS_TaxiC_AtJSX1_HasNoReversals',
  'Yaat.Sim.Tests.Simulation.OakPostLandingReversalsTests.N9225L_TaxiD_AtNEW1_HasNoReversals',
  'Yaat.Sim.Tests.Simulation.PatternDirectionResetTests.N342T_AfterErbAndCopt_NextCircuitResumesLeftTraffic',
  'Yaat.Sim.Tests.Simulation.S2Oak4RvSidCtoTests.N346G_CtoFromHoldShort_Nimi6_Stores315OnClearanceAndInitialClimb',
  'Yaat.Sim.Tests.Simulation.S2Oak4RvSidCtoTests.N436MS_CtoDuringTaxi_Nimi6_Stores315OnClearanceAndInitialClimb',
  'Yaat.Sim.Tests.Simulation.SfoLineupDiagonalTests.N346G_LineUp28R_CompletesWithOnCenterlineAlignedStop',
  'Yaat.Sim.Tests.Simulation.SfoRampCrossesRunwayTests.TaxiCommand_AcrossRunways_ShouldFail',
  'Yaat.Sim.Tests.Simulation.Skw3078TaxiEAtoB10RouteTests.BundleReplay_LiveRoute_NoImmediateReversal',
  'Yaat.Sim.Tests.Simulation.SpotOvershootTaxiRouteTests.TaxiM2ToSpot2_DoesNotOvershoot',
  'Yaat.Sim.Tests.Simulation.SpotOvershootTaxiRouteTests.TaxiM4M1ToSpot1_DoesNotOvershoot',
  'Yaat.Sim.Tests.Simulation.SpotOvershootTaxiRouteTests.TaxiT9ToSpot9_DoesNotOvershoot',
  'Yaat.Sim.Tests.TaxiPathfinderTests.ResolveExplicitPath_SfoM2_UsesSameTaxiwayArcAtA1Apex',
]

const tests = Array.isArray(args) && args.length > 0 ? args : FAILURES

function classOf(fqn) {
  const base = fqn.includes('(') ? fqn.slice(0, fqn.indexOf('(')) : fqn
  return base.slice(0, base.lastIndexOf('.'))
}
function methodOf(fqn) {
  const base = fqn.includes('(') ? fqn.slice(0, fqn.indexOf('(')) : fqn
  return fqn.slice(base.lastIndexOf('.') + 1)
}

const byClass = new Map()
for (const fqn of tests) {
  const c = classOf(fqn)
  if (!byClass.has(c)) byClass.set(c, [])
  byClass.get(c).push(methodOf(fqn))
}
const groups = [...byClass.entries()].map(([cls, methods]) => ({ cls, methods }))
log(`Verdicting ${tests.length} failures across ${groups.length} test classes`)

const SCHEMA = {
  type: 'object',
  additionalProperties: false,
  required: ['cls', 'verdicts'],
  properties: {
    cls: { type: 'string' },
    verdicts: {
      type: 'array',
      items: {
        type: 'object',
        additionalProperties: false,
        required: ['method', 'verdict', 'owner', 'evidence', 'fixHint', 'confidence'],
        properties: {
          method: { type: 'string' },
          verdict: { type: 'string', enum: ['V2-bug', 'missing-feature', 'V1-pinned', 'underlying-sim', 'fixed-by-fillet-v2'] },
          owner: { type: 'string', enum: ['pathfinder-WS2', 'navigator-WS3', 'phase-sim', 'assertion-relax', 'none'] },
          evidence: { type: 'string' },
          fixHint: { type: 'string' },
          confidence: { type: 'string', enum: ['high', 'medium', 'low'] },
        },
      },
    },
  },
}

function buildPrompt(cls, methods) {
  const short = cls.split('.').pop()
  return [
    `You are triaging ONE test class in the YAAT ground-stack V2 transition. Repo root: X:\\dev\\yaat. DO NOT modify any files. DO NOT run \`dotnet test\` (the working tree's runtime default is pathfinder V1 + Legacy fillet, so these tests PASS now and would mislead you; builds would also contend with other agents).`,
    ``,
    `These failures were captured under the SHIP TARGET (pathfinder V2 + fillet V2). For EACH of your failing methods there is a small pre-extracted failure block at ${BLOCKS}/<Method>.log containing: the assertion (Error Message), the test source location (Stack Trace -> file:line), and the route/nav diagnostics (Standard Output Messages: resolved TaxiRoute, per-tick phase/twy/nearestNodes, NavV2 logs).`,
    ``,
    `Test class: ${cls}`,
    `Failing methods (${methods.length}): ${methods.join(', ')}`,
    ``,
    `STRICT input rule: read ONLY your own block files — \`ls ${BLOCKS}/\` then Read ${BLOCKS}/<Method>.log for each method (some have a "__2" suffix if a method name collides across classes; disambiguate by the callsign/params shown inside the block). Do NOT read the global log .tmp/triage-v2v2-detail.log. Do NOT read other classes' blocks.`,
    ``,
    `Then for each method:`,
    `1. Read the block file (assertion + route/phase dump).`,
    `2. Read the test source — \`rg -l "class ${short}" tests/Yaat.Sim.Tests/\` then Read it — to understand the scenario (taxi/clearance commands, setup) and the CONTRACT it asserts.`,
    `3. Read the relevant production code for the root cause. Routing: src/Yaat.Sim/Data/Airport/V2/ (SegmentExpander, AutoRouter, RouteMaterialiser, RouteCostFunction) + src/Yaat.Sim/Data/Airport/ (TaxiVariantResolver, GroundCommandHandler). Following: the GroundNavigator. Phases: src/Yaat.Sim/Phases/.`,
    ``,
    `Assign a VERDICT per method (enum):`,
    `- "V2-bug": V2 produces a WRONG route, fails to find a VALID one, or ACCEPTS an invalid one (routes onto an unknown taxiway, across runways, auto-extends past the named taxiways when it should reject). Owner pathfinder-WS2.`,
    `- "missing-feature": V2 lacks something V1 has — variant/connector auto-resolution ("TAXI W to rwy 30" with connectors W1..W7), hold-short ordering, etc. Owner pathfinder-WS2.`,
    `- "V1-pinned": the test asserts V1-SPECIFIC output (exact node ids, segment counts, exact route/arc shape, a specific taxiway when several are equally valid) where the contract is looser and V2's behavior is still CORRECT -> relax the assertion. Owner assertion-relax.`,
    `- "underlying-sim": failure is DOWNSTREAM of route resolution — route FOLLOWING in the GroundNavigator (taxi spins, spot overshoot, post-landing reversals, lineup/on-centerline-stop, the AMX669 freeze) OR phase wiring (pattern direction, go-around intent, touch-and-go ext arming, CTO/InitialClimb cascade, pilot-speech timing). Owner navigator-WS3 (following) or phase-sim (phase wiring).`,
    `- "fixed-by-fillet-v2": only if it actually now PASSES (unlikely — all assigned failed under V2+V2).`,
    ``,
    `ALREADY-LANDED fixes — do NOT re-flag as gaps: #1 DestinationRunway hold-short reason; #2 reciprocal-runway matching (28R vs 28R/10L); #3 authoritative full-length lineup threshold (centroid deleted); #4 state-aware A* pruning; #5 detour authorized-taxiway soft policy + MaxDetourExpansions; req (1) single-name beats membership-only junction arc; fillet V2 #28/#29 (no duplicate corner arcs / coincident nodes; post-hoc node-merge deleted).`,
    ``,
    `Routing (WS2) vs following (WS3) test: if the RESOLVED route (segments + hold-shorts, printed in the block) is correct but the aircraft physically spins/overshoots/reverses/stops-misaligned while FOLLOWING it -> navigator-WS3. If the route itself is wrong/missing/over-permissive -> pathfinder-WS2.`,
    ``,
    `Reference docs: docs/ground/pathfinder.md (routing), docs/ground/navigator.md (following).`,
    ``,
    `Return StructuredOutput {cls, verdicts:[{method, verdict, owner, evidence, fixHint, confidence}]}. evidence = concrete Expected-vs-Actual + what the route/phase actually did (cite file:line / the block). fixHint = the concrete next step (which function or which assertion). confidence = high/medium/low.`,
  ].join('\n')
}

phase('Verdict')
const results = await parallel(
  groups.map((g) => () =>
    agent(buildPrompt(g.cls, g.methods), { label: `verdict:${g.cls.split('.').pop()}`, phase: 'Verdict', schema: SCHEMA })
  )
)

const ok = results.filter(Boolean)
const flat = ok.flatMap((r) => r.verdicts.map((v) => ({ cls: r.cls, ...v })))
const tally = {}
for (const v of flat) tally[v.verdict] = (tally[v.verdict] || 0) + 1
log(`Verdicts: ${flat.length} methods — ${JSON.stringify(tally)}`)
return { totalMethods: flat.length, classesReturned: ok.length, tally, verdicts: flat }
