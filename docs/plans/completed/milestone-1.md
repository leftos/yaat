# Milestone 1: Scenario Loading & Basic RPO Commands

## Context

YAAT's M0 proof-of-concept is complete: CRC sees hardcoded aircraft, the Avalonia client connects and displays them. M1 transforms YAAT into a functional RPO trainer by adding scenario loading, flight physics with three-axis interpolation, and RPO commands (heading/altitude/speed/squawk).

Work spans three codebases:
- **Yaat.Sim** (`X:\dev\yaat\src\Yaat.Sim\`) — shared physics + state library
- **Yaat.Server** (`X:\dev\yaat-server\src\Yaat.Server\`) — scenario loading, commands, hub expansion
- **Yaat.Client** (`X:\dev\yaat\src\Yaat.Client\`) — scenario browser, command input, sim controls

## Pre-step: CLAUDE.md Updates

- [x] Add lc-trainer warning to CLAUDE.md under "Related Repositories"
- [x] Add command scheme note to CLAUDE.md architecture section

---

## Chunk 1: ControlTargets + AircraftState Extensions (Yaat.Sim)

Foundation that everything else builds on.

- [x] Create `ControlTargets.cs` with TargetHeading, PreferredTurnDirection, TargetAltitude, DesiredVerticalRate, TargetSpeed, TurnDirection enum
- [x] Modify `AircraftState.cs`: add Targets, TransponderMode, IsIdenting, VerticalSpeed, FlightRules, CruiseAltitude, CruiseSpeed

Deferred to M4: `NavigationTarget`, `HeadingNavTarget`, `TargetMach`, `AtPilotsDiscretion`.

---

## Chunk 2: Flight Physics Enhancement (Yaat.Sim)

**AVIATION REVIEW GATE**: aviation-sim-expert MUST validate all rate values before implementation.

- [x] Rewrite `FlightPhysics.cs` with `Update(AircraftState, double deltaSeconds)` calling four steps:
  - [x] UpdateHeading — standard rate turn, PreferredTurnDirection, snap at 0.5°, wrap-around
  - [x] UpdateAltitude — climb/descend toward TargetAltitude, DesiredVerticalRate or category default
  - [x] UpdateSpeed — accel/decel toward TargetSpeed by category
  - [x] UpdatePosition — existing heading+groundspeed→lat/lon math
- [x] Create `AircraftCategory.cs` — Jet/Turboprop/Piston enum, Categorize(), performance constants
- [x] Modify `SimulationWorld.cs` — Tick() calls FlightPhysics.Update()

---

## Chunk 3: Command Scheme Infrastructure (Yaat.Sim + Yaat.Client)

Client-side verb translation. The server always receives canonical (ATCTrainer) format.

- [x] Create `Yaat.Sim/Commands/CanonicalCommandType.cs` — shared enum for all command types
- [x] Create `Yaat.Client/Services/CommandScheme.cs` — CommandScheme class with ATCTrainer() and Vice() presets, CommandPattern
- [x] Create `Yaat.Client/Services/CommandSchemeParser.cs` — Parse() and ToCanonical() for both space-separated and concatenated modes
- [x] Create persistence (UserPreferences) — loads/saves active scheme from preferences.json

### ATCTrainer preset mapping
| Command | Verb | Format |
|---------|------|--------|
| FlyHeading | `FH` | `FH {hdg}` |
| TurnLeft | `TL` | `TL {hdg}` |
| TurnRight | `TR` | `TR {hdg}` |
| RelativeLeft | `LT` | `LT {deg}` |
| RelativeRight | `RT` | `RT {deg}` |
| FlyPresentHeading | `FPH` | `FPH` |
| ClimbMaintain | `CM` | `CM {alt}` |
| DescendMaintain | `DM` | `DM {alt}` |
| Speed | `SPD` | `SPD {spd}` |
| Squawk | `SQ` | `SQ {code}` |
| Delete | `DEL` | `DEL` |

### VICE preset mapping
| Command | Verb | Format |
|---------|------|--------|
| FlyHeading | `H` | `H{hdg}` |
| TurnLeft | `L` | `L{hdg}` |
| TurnRight | `R` | `R{hdg}` |
| RelativeLeft | `T` | `T{deg}L` |
| RelativeRight | `T` | `T{deg}R` |
| FlyPresentHeading | `H` | `H` |
| ClimbMaintain | `C` | `C{alt}` |
| DescendMaintain | `D` | `D{alt}` |
| Speed | `S` | `S{spd}` |
| Squawk | `SQ` | `SQ{code}` |
| Delete | `X` | `X` |

---

## Chunk 4: Server-Side Command Parser + Dispatcher (Yaat.Server)

Server always receives canonical (ATCTrainer) format — either from client or from scenario presetCommands.

- [x] Create `Commands/ParsedCommand.cs` — record types for each command (FlyHeading, TurnLeft, ClimbMaintain, Speed, Squawk, Delete, etc.)
- [x] Create `Commands/CommandParser.cs` — Parse() with altitude shorthand (< 1000 → ×100), ground commands → UnsupportedCommand
- [x] Create `Commands/CommandDispatcher.cs` — Dispatch() maps commands to ControlTargets/state changes

---

## Chunk 5: Scenario Models + ScenarioLoader (Yaat.Server)

- [x] Create `Scenarios/ScenarioModels.cs` — deserialization models matching ATCTrainer JSON (Scenario, ScenarioAircraft, StartingConditions with polymorphic types)
- [x] Create `Scenarios/ScenarioLoader.cs` — Load() for Coordinates and FixOrFrd conditions, skip Parking/OnRunway/OnFinal with warning
- [x] Create `Scenarios/FrdResolver.cs` — parse FRD strings, project lat/lon via great-circle math
- [x] Create `Data/IFixLookup.cs` + `Data/FixDatabase.cs` — fix position lookup (replaced static JSON with VNAS NavData pipeline)

---

## Chunk 6: Training Hub Expansion + Sim Engine (Yaat.Server)

- [x] Modify `SimulationHostedService.cs` — pause/resume, sim rate, scenario clock, delayed aircraft queue, initialization triggers, remove hardcoded aircraft
- [x] Modify `TrainingHub.cs` — LoadScenario, SendCommand, DeleteAircraft, PauseSimulation, ResumeSimulation, SetSimRate + server→client events
- [x] Modify `TrainingDtos.cs` — expand AircraftStateDto, add LoadScenarioResult, CommandResultDto
- [x] Modify `DtoConverter.cs` — include new fields in ToTrainingDto, ToEramTarget, ToFlightPlan, ToStarsTrack

---

## Chunk 7: Client UI — Scenario, Commands, Sim Controls (Yaat.Client)

- [x] Modify `ServerConnection.cs` — add LoadScenarioAsync, SendCommandAsync, DeleteAircraftAsync, PauseSimulationAsync, ResumeSimulationAsync, SetSimRateAsync + events + expanded DTOs
- [x] Modify `AircraftModel.cs` — add AssignedHeading, AssignedAltitude, AssignedSpeed, TransponderMode, VerticalSpeed, Departure, Destination, Route, FlightRules, IsSelected
- [x] Modify `MainViewModel.cs` — scenario browsing, command input with scheme translation, command history, sim controls, server event handlers
- [x] Modify `MainWindow.axaml` — connection bar, scenario bar, expanded DataGrid, sim controls, command bar, overlays (delete confirm, scenario switch, active scenarios)
- [x] Create `SettingsWindow.axaml` + `SettingsViewModel.cs` — General/Commands/Advanced tabs, preset switching, verb editing, admin mode

---

## Implementation Order

```
Chunk 1: ControlTargets + AircraftState     [Yaat.Sim]
    ↓
Chunk 2: Flight Physics                      [Yaat.Sim]  ← aviation review gate
    ↓
Chunk 3: Command Scheme Infrastructure       [Yaat.Sim + Yaat.Client]
    ↓ (parallel with ↓)
Chunk 4: Server Command Parser + Dispatcher  [Yaat.Server]
Chunk 5: Scenario Models + Loader            [Yaat.Server]
    ↓ (both feed into ↓)
Chunk 6: Hub Expansion + Sim Engine          [Yaat.Server]
    ↓
Chunk 7: Client UI                           [Yaat.Client]
```

---

## What's Deferred (M2/M3+)

- `Parking`, `OnRunway`, `OnFinal` starting conditions
- Ground commands: PUSH, TAXI, WAIT, HS, CROSS, HOLD, RES
- Phase base class and phase system
- ClearanceRequirement and clearance gating
- Tower commands: CTO, LUAW, GA, TG, etc.
- Traffic pattern phases
- `aircraftGenerators` support
- `NavigationTarget` / DCT waypoint navigation
- `SpeedRestrictionStack` (250 below 10,000, etc.)
- Plan / Intent system
- Communication / verbal actions
- SAY preset command
- Wind effects (architecture allows it, deferred for simplicity)

---

## Verification

- [ ] Start yaat-server, connect YAAT client
- [ ] Browse to an ATCTrainer scenario JSON with `FixOrFrd`/`Coordinates` aircraft
- [ ] Load scenario → airborne aircraft appear in DataGrid and CRC
- [ ] Select aircraft, type `FH 270` → aircraft turns in CRC
- [ ] Type `CM 50` → aircraft climbs to 5000
- [ ] Type `SPD 200` → aircraft adjusts speed
- [ ] Pause → aircraft freeze; Resume → they move again
- [ ] SimRate 4x → movement accelerates
- [ ] Squawk commands: `SQ 1234` → code changes in CRC
- [ ] Switch to VICE command scheme in Settings → `H270` works equivalently
- [ ] Aircraft with `spawnDelay` appear at correct times after scenario load
- [ ] SQALL initialization trigger fires at configured offsets
