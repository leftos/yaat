# Test Coverage Expansion Plan

## Priority 1: Pure Unit Tests (High Value, Easy)

### P1.1 RouteChainer Tests -- DONE (8 tests)
- [x] Last resolved fix matches mid-route token -> appends remainder
- [x] Last fix at end of route -> no append
- [x] Last fix not in route -> no append
- [x] Route tokens with altitude constraints (FIX.A50 -> strip, match FIX)
- [x] Empty resolved list -> early return
- [x] Unknown fixes in remainder -> skip
- [x] Empty route string -> no append
- [x] Case-insensitive match

### P1.2 GeoMath Tests -- DONE (36 tests)
- [x] DistanceNm: identical points (=0), known real-world distance, 1-degree latitude
- [x] BearingTo: cardinal directions (N/S/E/W), northeast diagonal
- [x] TurnHeadingToward: current=target, within max, exceeds max (both directions), 180 ambiguity
- [x] ProjectPoint: N/E/S/W at 60nm, round-trip consistency
- [x] GenerateArcPoints: 90-degree, small arc, full circle, left turn, end-point, cross-360
- [x] SignedCrossTrackDistanceNm: on-line, right (+), left (-), symmetry
- [x] AlongTrackDistanceNm: ahead (+), behind (-), perpendicular (~0), matches DistanceNm

### P1.3 CommandQueue Trigger Tests -- DONE (20 tests)
- [x] ReachAltitude: within 10ft threshold, not met when far
- [x] ReachFix: within 0.5nm threshold, not met when far
- [x] InterceptRadial: within 3 degrees, not met when far
- [x] GiveWay: target gone, airborne, far, same-direction ahead/behind
- [x] DistanceFinal: not met without runway
- [x] Wait: seconds countdown, distance countdown
- [x] Multi-command block: all must complete before advancing
- [x] Null trigger: immediate application
- [x] Immediate command completion, heading completion
- [x] Block advancement, ApplyAction invocation

### P1.4 SimulationWorld Tests -- DONE (13 tests)
- [x] AddAircraft + GetSnapshot returns copy
- [x] RemoveAircraft matching/non-matching
- [x] CID auto-generation
- [x] DrainAllWarnings/Notifications/Scores drains once
- [x] Clear returns count, nulls GroundLayout
- [x] GenerateBeaconCode octal digits
- [x] Tick calls preTick

### P1.5 HoldShortAnnotator Tests -- DONE (14 tests)
- [x] Implicit: single crossing, entry/exit pair, two runways, dedup, non-HS skipped, empty
- [x] Explicit: runway match, taxiway fallback, no match
- [x] Destination: at last node, empty segments
- [x] HoldShortExists: true/false

### P1.6 RunwayCrossingDetector Tests -- DONE (20 tests)
- [x] Diagonal runway (45 heading) cross-track
- [x] Width-based hold-short distance lookup
- [x] Interpolation fraction clamping
- [x] Edge splitting: 2 new edges, original removed
- [x] Node reuse within 50ft

## Priority 2: Phase Transition Tests

### P2.1 Tower Phase Tests -- DONE (30 tests)
- [x] LinedUpAndWaitingPhase: holds at threshold, accepts CTO, rejects others
- [x] GoAroundPhase: TOGA, runway heading, climb 2000ft AGL, assigned heading after 400 AGL, custom target alt
- [x] TouchAndGoPhase: touchdown -> decelerate -> reaccelerate -> airborne, command acceptance
- [x] StopAndGoPhase: full stop -> pause -> reaccelerate -> airborne, command acceptance
- [x] LowApproachPhase: approach speed, climb out at go-around alt, completes at 1500 AGL
- [x] LandingPhase: flare -> touchdown -> rollout to 20kts, command acceptance before/after touchdown
- [x] HoldAtFixPhase: navigate to fix, orbit on arrival, helicopter hover, never self-completes
- [x] HoldPresentPositionPhase: orbit/hover at current position, name reflects direction

### P2.2 Pattern Phase Tests -- DONE (25 tests)
- [x] UpwindPhase: climb, runway heading, completes at crosswind turn, extended never completes
- [x] CrosswindPhase: 90° turn (L/R), continues climb below pattern alt, completes at downwind start
- [x] DownwindPhase: parallel heading, pattern alt, completes at base turn, extended holds
- [x] BasePhase: base heading, descent, completes near final approach course, extended holds
- [x] MidfieldCrossingPhase: heads toward midfield at pattern+500ft, completes on arrival
- [x] PatternGeometry: crosswind heading L/R, downwind reciprocal, pattern altitude

## Priority 3: Command Handler Tests

### P3.1 GroundCommandHandler Tests -- DONE (21 tests)
- [x] TryTaxi: no layout -> fail
- [x] TryTaxi: unknown taxiway -> fail
- [x] TryTaxi: valid path succeeds with route
- [x] TryPushback: not at parking -> fail
- [x] TryPushback: at parking succeeds, with taxiway, with heading
- [x] TryCrossRunway: from HoldingShort -> satisfy
- [x] TryCrossRunway: pre-clear in route, no matching HS -> fail, no route -> fail
- [x] TryHoldShort: not on ground -> fail, no route -> fail, no layout -> fail
- [x] TryFollow: no active phase -> fail, not on ground -> fail
- [x] Auto-cross-runway clears hold-shorts
- [x] TryHoldPosition/TryResumeTaxi: on ground, not on ground, not held

### P3.2 DepartureClearanceHandler Tests -- DONE (18 tests)
- [x] TryDepartureClearance from HoldingShort (LUAW + CTO)
- [x] TryDepartureClearance from Taxiing (pre-store), no route -> fail
- [x] TryDepartureClearance from LineUp (pre-satisfy CTO), LUAW rejected
- [x] Closed-traffic departure (no InitialClimb, pattern mode set)
- [x] ResolveLegsToTargets: PI skip, unknown fix skip, altitude constraints, dedup
- [x] BuildDepartureMessage: CTO with altitude, LUAW
- [x] FormatDepartureInstructionSuffix: default, runway heading, on course, direct fix, closed traffic

### P3.3 PatternCommandHandler Tests -- DONE (12 tests)
- [x] Wrong-side detection: Downwind/Base trigger midfield crossing
- [x] Correct side: no midfield crossing
- [x] Aircraft >1nm -> PatternEntryPhase inserted
- [x] Aircraft <=1nm -> PatternEntryPhase skipped
- [x] No runway -> fail
- [x] Extend downwind on wrong leg -> rejection
- [x] TryPatternTurnBase: from downwind, from wrong leg
- [x] Pattern direction change, no runway -> fail

## Priority 4: E2E Tests with Real Airports

### P4.1 OAK E2E
- [ ] Full taxi-to-takeoff: NEW7 -> D -> hold short 30 -> lineup -> CTO -> takeoff
- [ ] Pushback + taxi: parking -> pushback facing D -> taxi D W -> RWY30
- [ ] Multiple hold-shorts: route crossing two runways
- [ ] Auto-cross-runway: same route with flag -> hold-shorts cleared

### P4.2 SFO E2E
- [ ] Complex hold-short patterns with parallel runways
- [ ] Taxiway variant inference

### P4.3 Pattern Circuit E2E -- DONE (11 tests)
- [x] Full circuit from upwind: completes Upwind→Crosswind→Downwind→Base→FinalApproach
- [x] Full circuit from downwind: skips Upwind/Crosswind
- [x] Touch-and-go: auto-cycles into second circuit (UpwindPhase)
- [x] Go-around from final: DispatchCompound replaces with GoAroundPhase
- [x] PatternBuilder: BuildCircuit all entry legs (4), TouchAndGo vs Landing, BuildNextCircuit, UpdateWaypoints

## Priority 5: Strengthen Existing Tests

### P5.1 Edge Cases for Existing Test Files -- DONE (26 tests)
- [x] SpeedCommandTests: simultaneous floor+ceiling (7 tests: command sequencing, via-mode floor>ceiling clash, physics floor+ceiling enforcement)
- [x] NavigationCommandTests: unknown fix/null lookup (5 tests: empty DCT, CFIX at-or-below, CVIA without SID, JARR empty body)
- [x] TakeoffDepartureTests: heading wrapping + command acceptance (5 tests: 350+90, 020-90 wrap, ground roll rejection/cancel/delete)
- [x] GroundPhaseTests: pushback other directions + pre-cleared hold-short (4 tests: south/west/north push, pre-cleared crossing)
- [x] ConflictAlertDetectorTests: climbing away, descending into (5 tests: climb-away, descend-into, vertical-only convergence, both-on-final)

## Priority 6: Client Service Tests

### P6.1 Testable Client Logic -- DONE (35 tests)
- [ ] CommandInputController: autocomplete ranking, history nav, callsign prefix (deferred — heavy UI deps)
- [x] FixSuggester: GetTextBeforeLastWord, CollectRouteFixNames, TryAddFixSuggestions edge cases (11 tests)
- [ ] UserPreferences: JSON round-trip, missing fields -> defaults (deferred — file I/O, static paths)
- [x] FdRegionMapping: known mappings (9 theory cases), unknown ARTCC, case-insensitive, empty (4 tests)
- [x] ScenarioDifficultyHelper: GetAvailableDifficulties, GetCountsPerCeiling, FilterByDifficulty (10 tests)
