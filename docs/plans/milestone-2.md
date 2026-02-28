# Milestone 2: Local Control (Tower)

## Context

M0 (proof of concept) and M1 (scenario loading + RPO commands) are complete. Aircraft spawn from ATCTrainer scenarios at airborne positions, respond to heading/altitude/speed commands, and display in CRC. The server uses a target-based architecture: RPO commands set `ControlTargets`, and `FlightPhysics` interpolates toward them each tick.

M2 introduces the **Phase system** — the foundation for all future behavior — and uses it to implement tower operations: takeoff, landing, traffic pattern, touch-and-go, and go-around. This transforms YAAT from an airborne-only RPO tool into a local control trainer.

Work spans three codebases:
- **Yaat.Sim** (`X:\dev\yaat\src\Yaat.Sim\`) — Phase infrastructure, pattern geometry, ground roll physics
- **Yaat.Server** (`X:\dev\yaat-server\src\Yaat.Server\`) — Phase implementations, tower commands, runway data, hub updates
- **Yaat.Client** (`X:\dev\yaat\src\Yaat.Client\`) — Phase display, tower state UI

### Key Design Decisions

**Simplified Phase system.** Per the pilot-ai-architecture doc: "Each aircraft gets a simple phase list — no full Plan/Intent yet, just a current phase driving ControlTargets." We implement the abstract `Phase` base class and a linear phase list, but defer `Plan`, `Intent`, `Contingency`, and `Expectation` classes to M4+ when approach control needs them.

**ClearanceRequirement is essential.** Tower operations are fundamentally clearance-gated: aircraft hold short until LUAW, wait on the runway until CTO, go around if no landing clearance. This is not optional for training realism.

**Runway data from VNAS.** The server's `FixDatabase` already loads NavData.dat which contains airport and runway data. We extend it to expose runway positions, headings, and elevations.

**Phase drives ControlTargets.** The current M1 architecture has RPO commands setting targets directly. In M2, when an aircraft has an active Phase, the Phase sets targets each tick. RPO commands that conflict with the phase (e.g., speed assignment during takeoff roll) are rejected. RPO commands that override the phase (e.g., heading after takeoff) transition to the appropriate state.

---

## Chunk 1: Phase Infrastructure (Yaat.Sim)

Foundation that all tower operations build on.

**AVIATION REVIEW GATE**: aviation-sim-expert MUST validate Phase lifecycle and clearance model before implementation.

- [ ] Create `Phases/Phase.cs` — abstract base class with:
  - `PhaseStatus` enum (Pending, Active, Completed, Skipped)
  - `OnStart(PhaseContext)`, `OnTick(PhaseContext, double deltaSec) -> bool`, `OnEnd(PhaseContext, PhaseStatus)`
  - `Name` and `Description` abstract properties
  - `ClearanceRequirements` list (created via `CreateRequirements()`)
- [ ] Create `Phases/PhaseContext.cs` — provides access to:
  - `AircraftState` (current state)
  - `ControlTargets` (to set each tick)
  - `RunwayInfo` (position, heading, elevation, length — nullable, only set for runway-related phases)
  - `PatternInfo` (pattern parameters — nullable, only set for pattern phases)
  - `ElapsedInPhaseMs` (time since phase became active)
- [ ] Create `Phases/ClearanceRequirement.cs`:
  - `ClearanceType` enum: `ClearedForTakeoff`, `LineUpAndWait`, `ClearedToLand`, `ClearedForOption`, `ClearedTouchAndGo`
  - `ClearanceRequirement` class with `Type`, `IsSatisfied`
- [ ] Create `Phases/PhaseList.cs` — simple linear phase container:
  - `CurrentPhase`, `CurrentIndex`, `IsComplete`
  - `AdvanceToNext()` — completes current, starts next
  - `InsertAfterCurrent(Phase)` — for go-around insertion
  - `SkipTo<T>()` — skip forward to a phase type
  - `Clear()` — remove all phases (aircraft returns to target-only mode)
- [ ] Modify `AircraftState.cs` — add `PhaseList? Phases` field
- [ ] Modify `FlightPhysics.cs` or create `PhaseRunner.cs`:
  - Before the 4-step physics update, tick the current phase
  - Phase's `OnTick` sets `ControlTargets`; then physics interpolates toward them
  - If phase returns `true` (complete), advance to next phase
  - If current phase has unsatisfied clearance requirements, the phase's `OnTick` should handle the "waiting" behavior (e.g., hold position)
- [ ] Wire into `SimulationWorld.TickScenario()` — call phase tick before physics tick for each aircraft

---

## Chunk 2: Runway Data Model (Yaat.Server)

Tower operations need runway geometry: threshold position, heading, elevation, length.

- [ ] Determine runway data source — check if VNAS NavData.dat contains runway records, or if we need a separate data source
  - NavData.dat likely has airport reference points but may not have individual runway thresholds
  - Fallback: extract from scenario data (`primaryAirportId` + runway identifiers) or hardcode for common training airports
- [ ] Create `Data/RunwayInfo.cs`:
  - `string AirportId` (ICAO or FAA id)
  - `string RunwayId` (e.g., "28R", "10L")
  - `double ThresholdLatitude`, `ThresholdLongitude`
  - `double Heading` (magnetic heading of the runway)
  - `double ElevationFt` (threshold elevation)
  - `double LengthFt`, `double WidthFt`
  - `string? ReciprocalId` (the other end)
  - Computed: `double OppositeHeading`, endpoint lat/lon from heading + length
- [ ] Create `Data/IRunwayLookup.cs` + `Data/RunwayDatabase.cs`:
  - `RunwayInfo? GetRunway(string airportId, string runwayId)`
  - `IReadOnlyList<RunwayInfo> GetRunways(string airportId)`
  - Load from whatever data source is available
- [ ] If VNAS data is insufficient, create `Data/RunwayData/` with JSON files for common training airports (at minimum: SFO/KSFO, OAK/KOAK — the ZOA airports used in ATCTrainer scenarios)

---

## Chunk 3: Takeoff Sequence (Yaat.Sim + Yaat.Server)

**AVIATION REVIEW GATE**: aviation-sim-expert MUST validate takeoff physics (Vr, V2, initial climb rates by aircraft category, acceleration rates on ground).

### Phases

- [ ] Create `Phases/Tower/LinedUpAndWaitingPhase.cs`:
  - Aircraft is on the runway, stationary, aligned with runway heading
  - Clearance-gated: requires `ClearedForTakeoff`
  - `OnTick`: hold position (speed = 0, heading = runway heading) until clearance satisfied
  - When clearance received, complete → advance to `TakeoffPhase`
- [ ] Create `Phases/Tower/TakeoffPhase.cs`:
  - `OnStart`: set heading target = runway heading (or assigned heading from CTO command), begin acceleration
  - Ground roll: accelerate from 0 to Vr (rotation speed, ~130-150 kts for jets, ~60-80 kts for props)
  - At Vr: "rotate" — begin pitch up, liftoff occurs ~5-10 kts above Vr
  - After liftoff: climb at V2+10, initial climb rate by aircraft category
  - `OnTick` returns complete when aircraft reaches a configurable altitude (e.g., 400 ft AGL or pattern altitude)
  - Track `IsAirborne` flag — transitions from ground roll to flight
- [ ] Create `Phases/Tower/InitialClimbPhase.cs`:
  - After takeoff phase completes, aircraft continues climbing
  - Maintains runway heading (or assigned heading) and accelerates to cruise climb speed
  - Completes when reaching assigned altitude, or transitions to "no phase" (RPO takes over with direct heading/altitude commands)

### Physics

- [ ] Extend `FlightPhysics.cs` — ground roll mode:
  - When aircraft altitude equals airport elevation and speed < liftoff speed: ground mode
  - No altitude change during ground roll (aircraft stays on runway)
  - Heading locked to runway heading during ground roll (no turns on the ground yet — that's M3)
  - Acceleration rate on ground (higher than airborne — ~3-5 kts/sec for jets)
  - Position updates along runway centerline during ground roll
- [ ] Add `AircraftState.IsOnGround` computed property (altitude within ~50 ft of last known ground elevation, or explicit flag)
- [ ] Add aircraft category Vr/V2 estimates to `AircraftCategorization.cs`

### Commands

- [ ] Add to `CommandParser.cs`:
  - `CTO [hdg]` — Cleared for takeoff (optional heading assignment)
  - `CTOR{deg}` / `CTOL{deg}` — Cleared for takeoff with relative right/left turn of N degrees from runway heading (no space between command and degrees). E.g., on runway 28 (heading 280): `CTOR45` → takeoff heading 325, `CTOL270` → takeoff heading 010. **Note:** `CTOR 270` (with space) parses as `CTO` heading 270 with right turn direction — different command.
  - `LUAW` — Line up and wait
  - `CTOC` — Cancel takeoff clearance
  - `CTOMLT` / `CTOMRT` — Cleared for takeoff, make left/right traffic
- [ ] Add to `ParsedCommand.cs`:
  - `ClearedForTakeoffCommand { AssignedHeading?, TurnDirection?, RelativeTurnDegrees?, TrafficDirection? }`
    - `CTO 270` → AssignedHeading=270, TurnDirection=null (shortest turn)
    - `CTOR 270` → AssignedHeading=270, TurnDirection=Right (same as `CTO 270` but forces right turn)
    - `CTOR270` (no space) → RelativeTurnDegrees=270, TurnDirection=Right, AssignedHeading computed at dispatch from runway heading + 270
  - `LineUpAndWaitCommand`
  - `CancelTakeoffClearanceCommand`
- [ ] Add to `CommandDispatcher.cs`:
  - `CTO`: satisfy `ClearedForTakeoff` clearance requirement on current phase; if aircraft has no phase, build LinedUpAndWaiting → Takeoff → InitialClimb phase list
  - `CTOR`/`CTOL` (with space + heading, e.g., `CTOR 270`): same as `CTO 270` but forces right/left turn direction to the absolute heading
  - `CTOR{deg}`/`CTOL{deg}` (no space, e.g., `CTOR270`): relative turn — compute target heading as runway heading + degrees (right) or - degrees (left), then CTO to that heading with forced turn direction
  - `LUAW`: satisfy `LineUpAndWait` clearance (for future use when aircraft taxi to runway in M3); for now, place aircraft in LinedUpAndWaiting phase
  - `CTOC`: revoke takeoff clearance (only valid during LinedUpAndWaiting before roll begins)
  - `CTOMLT`/`CTOMRT`: CTO + set up pattern re-entry after initial climb

### Scenario Support

- [ ] Add `OnRunway` starting condition support in `ScenarioLoader.cs`:
  - Look up runway from `RunwayDatabase`
  - Place aircraft at runway threshold, aligned with runway heading, speed 0, altitude = field elevation
  - Initialize with `LinedUpAndWaitingPhase` → `TakeoffPhase` → `InitialClimbPhase`

---

## Chunk 4: Landing Sequence (Yaat.Sim + Yaat.Server)

**AVIATION REVIEW GATE**: aviation-sim-expert MUST validate glideslope geometry, touchdown physics, rollout deceleration rates, and go-around climb profiles.

### Phases

- [ ] Create `Phases/Tower/FinalApproachPhase.cs`:
  - Aircraft is on final approach, descending toward the runway
  - Tracks glideslope (3.0° default) from current position to runway threshold
  - `OnTick`: continuously compute required descent rate to stay on glideslope, set altitude/speed targets
  - Clearance-gated: requires `ClearedToLand` (or `ClearedForOption`, `ClearedTouchAndGo`)
  - If no landing clearance by a configurable distance (default: 0.5 nm from threshold for VFR, DA for IFR), trigger go-around
  - Completes when aircraft crosses runway threshold → advance to `LandingPhase`
- [ ] Create `Phases/Tower/LandingPhase.cs`:
  - Flare: reduce descent rate as aircraft approaches runway elevation
  - Touchdown: aircraft reaches runway elevation, transition to ground mode
  - Rollout: decelerate from touchdown speed to taxi speed
  - Completes when aircraft reaches taxi speed (~30 kts) or stops
  - After completion: aircraft is on the ground, no active phase (awaits taxi instructions in M3, or DEL)
- [ ] Create `Phases/Tower/GoAroundPhase.cs`:
  - Triggered by `GA` command or automatic (no landing clearance at decision point)
  - `OnStart`: set full power climb, set heading = runway heading, set target altitude = pattern altitude or as assigned
  - Accelerate to Vy (best rate of climb speed)
  - Completes when reaching target altitude
  - After completion: aircraft has no phase (RPO takes over), or transitions to pattern entry if directed

### Physics

- [ ] Extend `FlightPhysics.cs` — landing mode:
  - Flare model: reduce vertical speed to ~100-200 fpm descent in last 50 ft AGL
  - Touchdown detection: altitude reaches field elevation → transition to ground
  - Rollout deceleration: ~3-5 kts/sec for jets (reverse thrust + brakes), ~2-3 kts/sec for props
  - Position tracks runway centerline during rollout
- [ ] Add glideslope geometry utility:
  - Given runway threshold position, heading, and glideslope angle, compute target altitude at any point along the approach path
  - Compute required descent rate given groundspeed and glideslope angle

### Commands

- [ ] Add to `CommandParser.cs`:
  - `GA` — Go around
  - `FS` — Full stop (explicit landing)
  - `EXIT {twy}` — Exit at taxiway (M3 will implement taxiway routing; for now just mark intent)
  - `EL` / `ER` — Exit left / exit right
  - `LAHSO {rwy}` — Land and hold short of runway (store as intent, physics deferred)
- [ ] Add to `ParsedCommand.cs`:
  - `GoAroundCommand`
  - `FullStopCommand`
  - `ExitRunwayCommand { Direction?, TaxiwayName? }`
  - `LahsoCommand { HoldShortRunway }`
- [ ] Add to `CommandDispatcher.cs`:
  - `GA`: if aircraft is in FinalApproach or Landing phase, transition to GoAround phase
  - `FS`: satisfy landing clearance (for ClearedForOption scenarios where full stop is the choice)
  - `EXIT`/`EL`/`ER`: store exit preference on aircraft state (actual exit routing is M3)

### Scenario Support

- [ ] Add `OnFinal` starting condition support in `ScenarioLoader.cs`:
  - Place aircraft on final approach at configured distance from runway threshold
  - Compute position: offset from runway threshold along approach course
  - Compute altitude: `distanceFromRunway * tan(glideslope) * 6076` (ft per nm, 3° glideslope ≈ 318 ft/nm)
  - Set speed to approach speed for aircraft category
  - Initialize with `FinalApproachPhase` → `LandingPhase`

---

## Chunk 5: Traffic Pattern (Yaat.Sim + Yaat.Server)

**AVIATION REVIEW GATE**: aviation-sim-expert MUST validate pattern geometry, standard altitudes, leg lengths, turn points, and entry procedures.

### Pattern Geometry

- [x] Create `Phases/PatternGeometry.cs`:
  - Given: runway threshold, runway heading, pattern direction (left/right), pattern altitude, pattern size
  - Compute key positions: upwind end, crosswind turn point, downwind start/end (abeam threshold/numbers), base turn point, final turn point
  - Default pattern size: ~1 nm from runway centerline for downwind leg (by aircraft category)
  - PatternDirection enum (Left/Right), PatternWaypoints data class
- [x] Add pattern performance constants to `CategoryPerformance`:
  - PatternAltitudeAgl, PatternSizeNm, CrosswindExtensionNm, BaseExtensionNm
  - DownwindSpeed, BaseSpeed, PatternTurnRate, PatternDescentRate
- [x] Add `FlightPhysics.ProjectPoint()` utility for waypoint computation

### Phases

- [x] Create `Phases/Pattern/UpwindPhase.cs`:
  - After takeoff, flying runway heading until crosswind turn point
  - Climbs to pattern altitude, accelerates toward downwind speed
  - Completes when reaching crosswind turn waypoint or when TC command given
- [x] Create `Phases/Pattern/CrosswindPhase.cs`:
  - Turning perpendicular to runway, climbing to pattern altitude
  - Completes when reaching downwind start waypoint
- [x] Create `Phases/Pattern/DownwindPhase.cs`:
  - Level flight parallel to runway, opposite direction at pattern altitude
  - At downwind speed, maintains pattern altitude
  - IsExtended flag supports EXT command (holds on downwind until TB)
  - Completes at base turn point or when TB command given
- [x] Create `Phases/Pattern/BasePhase.cs`:
  - Turn toward runway, begin descent at PatternDescentRate
  - Decelerates to base speed
  - Completes when reaching final turn waypoint
- [x] Pattern uses existing `FinalApproachPhase` → `LandingPhase` for landing sequence

### Commands

- [x] Add to `CommandParser.cs`:
  - `ELD` / `ERD` — Enter left/right downwind
  - `ELB` / `ERB` — Enter left/right base
  - `EF` — Enter final (straight-in)
  - `MLT` / `MRT` — Make left/right traffic
  - `TC` / `TD` / `TB` — Turn crosswind/downwind/base
  - `EXT` — Extend downwind leg
  - Deferred: `MSA` / `MNA` / `PS {nm}` (not yet implemented)
- [x] Add to `ParsedCommand.cs`:
  - Individual command records for each pattern command
- [x] Add to `CanonicalCommandType.cs`:
  - Pattern command types: EnterLeftDownwind, EnterRightDownwind, etc.
- [x] Add to `CommandDispatcher.cs`:
  - `ELD`/`ERD`: build Downwind → Base → FinalApproach → Landing phase list
  - `ELB`/`ERB`: build Base → FinalApproach → Landing
  - `EF`: build FinalApproach → Landing
  - `MLT`/`MRT`: update waypoints on all remaining pattern phases
  - `TC`: advance from UpwindPhase to next phase
  - `TD`: advance from CrosswindPhase to next phase
  - `TB`: advance from DownwindPhase to next phase (clears extension)
  - `EXT`: set IsExtended on DownwindPhase

---

## Chunk 6: Touch-and-Go, Holds + Special Operations (Yaat.Server)

**AVIATION REVIEW GATE**: aviation-sim-expert MUST validate touch-and-go procedures, speed management during option approaches, and hold/orbit behavior.

- [x] Create `Phases/Tower/TouchAndGoPhase.cs`:
  - After touchdown: brief rollout (~3-5 seconds), then apply takeoff power
  - Accelerate on runway to Vr, rotate, liftoff
  - Essentially: abbreviated LandingPhase → abbreviated TakeoffPhase
  - Completes when airborne at pattern altitude → transitions to pattern or departure
- [x] Create `Phases/Tower/StopAndGoPhase.cs`:
  - After touchdown: full stop on runway, then takeoff roll from zero
  - Decelerate to 0, pause briefly, then accelerate
  - LandingPhase → hold on runway → TakeoffPhase
- [x] Create `Phases/Tower/LowApproachPhase.cs`:
  - Aircraft flies approach path but does NOT touch down
  - At ~50-100 ft AGL: apply go-around power, climb
  - Transitions to pattern or departure climb
- [x] Modify `CommandDispatcher.cs` for option approach commands:
  - `TG`: satisfy `ClearedTouchAndGo` clearance; after FinalApproach, insert TouchAndGo → pattern phases
  - `SG`: satisfy clearance; after FinalApproach, insert StopAndGo → Takeoff → pattern phases
  - `LA`: after FinalApproach, insert LowApproach → pattern or departure phases
  - `ClearedForOption` (`COPT`): general clearance — pilot behavior depends on intent (default: touch-and-go)

### Hold / Orbit Commands

- [x] Create `Phases/HoldPresentPositionPhase.cs`:
  - For winged aircraft (`HPP360L` / `HPP360R`): orbit at present position via continuous 360° turns in the specified direction
  - For helicopters (`HPP`): hover at present position (speed 0, altitude hold)
  - `OnTick`: winged aircraft maintain current altitude/speed and fly a 360° turn, returning to same position; helicopters hold position
  - Completes when RPO issues a new heading/altitude/navigation command (phase is cleared)
- [x] Create `Phases/HoldAtFixPhase.cs`:
  - `HFIX {fix}` — fly to the fix, then hold:
    - Winged aircraft: orbit over the fix via continuous 360° turns (left by default, or as specified)
    - Helicopters: fly to the fix and hold position (hover)
  - `OnTick`: if not yet at fix, navigate to fix (like DCT); once at fix, orbit/hover
  - Completes when RPO issues a new heading/altitude/navigation command
- [x] Add to `CommandParser.cs`:
  - `HPP360L` / `HPP360R` — Hold present position, 360 turns left/right (winged aircraft)
  - `HPP` — Hold present position (helicopters, hover)
  - `HFIX {fix}` — Hold at fix (360 turns for winged, in-position for helicopters)
- [x] Add to `ParsedCommand.cs`:
  - `HoldPresentPositionCommand { TurnDirection?, IsHelicopter }`
  - `HoldAtFixCommand { FixName }`
- [x] Add to `CommandDispatcher.cs`:
  - `HPP360L`/`HPP360R`: set up HoldPresentPositionPhase with turn direction
  - `HPP`: set up HoldPresentPositionPhase in hover mode
  - `HFIX`: set up HoldAtFixPhase with the target fix

---

## Chunk 7: Training Hub + DTO Updates (Yaat.Server)

- [x] Extend `AircraftStateDto` with phase information:
  - `string CurrentPhase` — name of the active phase (e.g., "Downwind", "FinalApproach", "TakeoffRoll")
  - `string AssignedRunway` — active runway assignment
  - `bool IsOnGround` — ground/airborne state
- [x] Extend `DtoConverter.cs` to populate new fields from phase state
- [x] Extend client-side `AircraftDto` and `AircraftModel` with matching fields
- [x] Update `UpdateModel` and `DtoToModel` in `MainViewModel` to map new fields
- [x] CRC DTOs handle ground aircraft correctly (GroundSpeed/Altitude come from AircraftState, set by phases)
- [ ] Add server→client events for phase transitions (deferred — not needed for basic display)

---

## Chunk 8: Client UI Updates (Yaat.Client)

- [x] Extend `AircraftModel.cs` with new fields:
  - `CurrentPhase`, `AssignedRunway`, `IsOnGround`
- [x] Extend `MainWindow.axaml` DataGrid columns:
  - Add "Phase" column showing current phase name
  - Add "Rwy" column showing assigned runway
- [x] Extend `ServerConnection.cs` / `AircraftDto`:
  - `CurrentPhase`, `AssignedRunway`, `IsOnGround` fields with defaults for backward compat
- [x] Update `MainViewModel` UpdateModel/DtoToModel to map new fields
- [x] Command bar support: all tower/pattern commands flow through existing command scheme infrastructure

---

## Implementation Order

```
Chunk 1: Phase Infrastructure             [Yaat.Sim]              ← aviation review gate
    |
Chunk 2: Runway Data Model                [Yaat.Server]
    |
    +---> Chunk 3: Takeoff Sequence        [Yaat.Sim + Yaat.Server] ← aviation review gate
    |         |
    |     Chunk 4: Landing Sequence        [Yaat.Sim + Yaat.Server] ← aviation review gate
    |         |
    |     Chunk 5: Traffic Pattern         [Yaat.Sim + Yaat.Server] ← aviation review gate
    |         |
    |     Chunk 6: Touch-and-Go + Special  [Yaat.Server]            ← aviation review gate
    |
Chunk 7: Hub + DTO Updates                [Yaat.Server]            (can start after Chunk 3)
    |
Chunk 8: Client UI                        [Yaat.Client]            (can start after Chunk 7)
```

Chunks 3-6 are sequential (each builds on the previous). Chunks 7-8 can begin as soon as Chunk 3 introduces phase state to the DTOs.

---

## What's Deferred (M3+)

- **Ground operations**: Parking, pushback, taxi, hold short, runway crossing (M3)
- **Taxiway graph and pathfinding**: A* on taxiway nodes (M3)
- **ASDEX ground target DTOs**: M3
- **Full Plan/Intent/Contingency system**: M4 (approach control needs it for route-based navigation)
- **~~NavigationTarget / DCT waypoint following~~**: Done (basic DCT + route continuation implemented in M1; M4 adds course intercepts and procedure-based nav)
- **Missed approach procedures**: M4 (requires published procedure data)
- **Speed restriction stack**: M4
- **Communication / verbal actions**: M8 (AI mode)
- **Wind effects on pattern**: deferred (Phase architecture supports it when added)
- **Terrain-aware operations**: deferred
- **LAHSO physics**: noted in commands but actual hold-short-of-runway behavior deferred

---

## Verification

- [ ] Start yaat-server, connect YAAT client
- [ ] Load an ATCTrainer scenario with `OnRunway` aircraft
- [ ] Aircraft appears on runway, stationary, in "Lined Up and Waiting" phase
- [ ] Issue `CTO` → aircraft accelerates, rotates, lifts off, climbs
- [ ] Issue `CTO 270` → aircraft takes off and turns to heading 270 after liftoff
- [ ] Load scenario with `OnFinal` aircraft
- [ ] Aircraft appears on final approach, descending toward runway
- [ ] Issue landing clearance → aircraft lands, rolls out, stops
- [ ] Do NOT issue landing clearance → aircraft goes around at decision point
- [ ] Issue `GA` while on final → aircraft executes go-around
- [ ] Issue `CTOR45` on runway 28 → aircraft takes off, turns right 45° from runway heading (280+45=325)
- [ ] Issue `CTOL270` on runway 28 → aircraft takes off, turns left 270° from runway heading (280-270=010)
- [ ] Issue `CTOR 270` (with space) → aircraft takes off, turns right to absolute heading 270 (distinct from `CTOR270`)
- [ ] Issue `CTOMLT` → aircraft takes off, enters left traffic pattern
- [ ] Aircraft flies full pattern: upwind → crosswind → downwind → base → final
- [ ] Issue `TC`, `TD`, `TB` → aircraft turns to the commanded leg early
- [ ] Issue `EXT` on downwind → aircraft continues past normal base turn point
- [ ] Issue `TG` clearance → aircraft touches down and takes off again
- [ ] Issue `LA` → aircraft flies low approach without touching down
- [ ] Phase name displays correctly in client DataGrid
- [ ] CRC shows correct aircraft behavior (altitude, heading, speed transitions)
- [ ] Pattern altitude is correct (~1000 ft AGL)
- [ ] Issue `HPP360L` → aircraft orbits at current position via left 360° turns
- [ ] Issue `HPP360R` → aircraft orbits at current position via right 360° turns
- [ ] Issue `HFIX OAK` → aircraft navigates to OAK, then orbits over it
- [ ] Multiple aircraft can be in the pattern simultaneously
