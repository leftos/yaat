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

### P3.1 GroundCommandHandler Tests
- [ ] TryTaxi: no layout -> fail
- [ ] TryTaxi: not on ground -> fail
- [ ] TryTaxi: unknown taxiway -> fail
- [ ] TryPushback: not at parking -> fail
- [ ] TryPushback: taxiway + facing resolution
- [ ] TryCrossRunway: from HoldingShort -> satisfy
- [ ] TryCrossRunway: pre-clear in route
- [ ] TryHoldShort: runtime insertion
- [ ] TryFollow: target not found
- [ ] Auto-cross-runway clears hold-shorts

### P3.2 DepartureClearanceHandler Tests
- [ ] TryDepartureClearance from HoldingShort
- [ ] TryDepartureClearance from Taxiing (pre-store)
- [ ] TryDepartureClearance from LineUp (pre-satisfy)
- [ ] CTO during taxi with no runway -> fail
- [ ] SID resolution fallback (no CIFP -> NavData)
- [ ] Closed-traffic departure (no InitialClimb, pattern mode)
- [ ] ResolveLegsToTargets: PI skip, RF/AF arc expansion

### P3.3 PatternCommandHandler Tests
- [ ] Wrong-side detection: Downwind triggers midfield, Base does not
- [ ] Aircraft >1nm -> PatternEntryPhase inserted
- [ ] Aircraft <=1nm -> PatternEntryPhase skipped
- [ ] Landing clearance with no pattern -> append circuit
- [ ] Extend downwind on wrong leg -> rejection
- [ ] Pattern direction inference

## Priority 4: E2E Tests with Real Airports

### P4.1 OAK E2E
- [ ] Full taxi-to-takeoff: NEW7 -> D -> hold short 30 -> lineup -> CTO -> takeoff
- [ ] Pushback + taxi: parking -> pushback facing D -> taxi D W -> RWY30
- [ ] Multiple hold-shorts: route crossing two runways
- [ ] Auto-cross-runway: same route with flag -> hold-shorts cleared

### P4.2 SFO E2E
- [ ] Complex hold-short patterns with parallel runways
- [ ] Taxiway variant inference

### P4.3 Pattern Circuit E2E
- [ ] Full circuit: entry -> upwind -> crosswind -> downwind -> base -> final -> touchdown
- [ ] Touch-and-go -> second circuit
- [ ] Go-around from final -> pattern re-entry

## Priority 5: Strengthen Existing Tests

### P5.1 Edge Cases for Existing Test Files
- [ ] SpeedCommandTests: simultaneous floor+ceiling, floor>ceiling clash
- [ ] NavigationCommandTests: unknown fix, null lookup error paths
- [ ] TakeoffDepartureTests: wind interaction
- [ ] GroundPhaseTests: pushback other directions, invalid hold-short nodes
- [ ] ConflictAlertDetectorTests: climbing away, descending into

## Priority 6: Client Service Tests

### P6.1 Testable Client Logic
- [ ] CommandInputController: autocomplete ranking, history nav, callsign prefix
- [ ] FixSuggester: prefix search, empty input, no matches, exact match
- [ ] UserPreferences: JSON round-trip, missing fields -> defaults
- [ ] FdRegionMapping: known mappings, unknown ARTCC
- [ ] ScenarioDifficultyHelper: classification boundaries
