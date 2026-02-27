# Milestone 1: Scenario Loading & Basic RPO Commands

## Context

YAAT's M0 proof-of-concept is complete: CRC sees hardcoded aircraft, the Avalonia client connects and displays them. M1 transforms YAAT into a functional RPO trainer by adding scenario loading, flight physics with three-axis interpolation, and RPO commands (heading/altitude/speed/squawk).

Work spans three codebases:
- **Yaat.Sim** (`X:\dev\yaat\src\Yaat.Sim\`) — shared physics + state library
- **Yaat.Server** (`X:\dev\yaat-server\src\Yaat.Server\`) — scenario loading, commands, hub expansion
- **Yaat.Client** (`X:\dev\yaat\src\Yaat.Client\`) — scenario browser, command input, sim controls

## Pre-step: CLAUDE.md Updates

### A) lc-trainer warning
Add to `X:\dev\yaat\CLAUDE.md` under "Related Repositories":

> **lc-trainer is NOT a trusted reference.** It is WIP, flawed, and unreviewed. It may be used as inspiration but every aviation detail drawn from it MUST be reviewed by the `aviation-sim-expert` agent. Do not port code from lc-trainer without independent validation. Prefer a fresh, well-organized approach over copying its patterns.

### B) Command scheme note
Add a note in CLAUDE.md architecture section about the command scheme being client-side.

---

## Chunk 1: ControlTargets + AircraftState Extensions (Yaat.Sim)

Foundation that everything else builds on.

### Create `ControlTargets.cs`
```
Yaat.Sim/ControlTargets.cs
```
- `TargetHeading` (double?) — degrees magnetic
- `PreferredTurnDirection` (TurnDirection?) — Left/Right/null=shortest
- `TargetAltitude` (double?) — feet MSL
- `DesiredVerticalRate` (double?) — fpm override (positive=climb)
- `TargetSpeed` (double?) — indicated airspeed in knots
- `TurnDirection` enum: Left, Right

Deferred to M4: `NavigationTarget`, `HeadingNavTarget`, `TargetMach`, `AtPilotsDiscretion`.

### Modify `AircraftState.cs`
Add properties:
- `ControlTargets Targets { get; } = new()` — always non-null
- `string TransponderMode { get; set; } = "C"` — "C", "Standby", "Ident"
- `bool IsIdenting { get; set; }` — temporary ident flash
- `double VerticalSpeed { get; set; }` — for CRC display + climb/descent tracking
- `string FlightRules { get; set; } = "IFR"`
- `int CruiseAltitude { get; set; }`
- `int CruiseSpeed { get; set; }`

---

## Chunk 2: Flight Physics Enhancement (Yaat.Sim)

**AVIATION REVIEW GATE**: aviation-sim-expert MUST validate all rate values before implementation.

### Rewrite `FlightPhysics.cs`
New entry point `Update(AircraftState, double deltaSeconds)` replaces `UpdatePosition`. Calls four steps:

1. **UpdateHeading** — standard rate turn toward `TargetHeading`
   - ~3 deg/sec (aviation-sim-expert to confirm)
   - Honors `PreferredTurnDirection`; defaults to shortest path
   - Snaps when within 0.5° of target; clears `PreferredTurnDirection`
   - Handles wrap-around (350→10 turns right, not left 340°)

2. **UpdateAltitude** — climb/descend toward `TargetAltitude`
   - Rate from `DesiredVerticalRate` if set, otherwise default by aircraft category
   - Updates `VerticalSpeed` property; sets to 0 when level
   - Snaps when within threshold

3. **UpdateSpeed** — accel/decel toward `TargetSpeed`
   - Rate by aircraft category
   - Snaps when within threshold

4. **UpdatePosition** — existing heading+groundspeed→lat/lon math (unchanged)

### Create `AircraftCategory.cs`
- Enum: `Jet`, `Turboprop`, `Piston`
- Static `Categorize(string aircraftType)` — prefix-based heuristic
- Performance constants per category (turn rate, climb/descent fpm, accel/decel knots/sec)
- **All values subject to aviation-sim-expert review**

### Modify `SimulationWorld.cs`
- Change `Tick()` to call `FlightPhysics.Update()` instead of `FlightPhysics.UpdatePosition()`

---

## Chunk 3: Command Scheme Infrastructure (Yaat.Sim + Yaat.Client)

Client-side verb translation. The server always receives canonical (ATCTrainer) format.

### Design

A `CommandScheme` defines how user-typed input maps to canonical commands. Two built-in presets:

**ATCTrainer** (canonical): `FH 270`, `TL 270`, `TR 090`, `LT 20`, `RT 30`, `CM 240`, `DM 50`, `SPD 250`, `SQ 1234`, `DEL`

**VICE**: `H270`, `L270`, `R090`, `T20L`, `T30R`, `C240`, `D50`, `S250`, `SQ1234`, `X`

Key differences: VICE uses single-letter verbs, no spaces between verb and arg, and reversed syntax for relative turns (`T20L` vs `LT 20`).

### Create `Yaat.Sim/Commands/CanonicalCommandType.cs`
Shared library so both server and client can reference the types.

```csharp
public enum CanonicalCommandType
{
    FlyHeading, TurnLeft, TurnRight,
    RelativeLeft, RelativeRight, FlyPresentHeading,
    ClimbMaintain, DescendMaintain,
    Speed, Mach,
    Squawk, SquawkIdent, SquawkVfr,
    SquawkNormal, SquawkStandby, Ident,
    Delete, Pause, Unpause, SimRate, SquawkAll
}
```

### Create `Yaat.Client/Services/CommandScheme.cs`
- `CommandScheme` class with `Name`, `Dictionary<CanonicalCommandType, CommandPattern>`
- `CommandPattern`: verb string + format pattern (e.g., `"{verb} {heading}"` vs `"{verb}{heading}"`)
- `CommandSchemeParser.Parse(string input, CommandScheme scheme) → (CanonicalCommandType, args)?`
- `CommandSchemeParser.ToCanonical(CanonicalCommandType, args) → string` — produces ATCTrainer format for sending to server
- Built-in factory methods: `CommandScheme.AtcTrainer()`, `CommandScheme.Vice()`
- Load/save custom schemes from JSON

### Create `Yaat.Client/Services/CommandSchemeStore.cs`
- Loads active scheme from `commandScheme.json` in app data dir
- Falls back to ATCTrainer preset
- Save/load/reset functionality

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

### Create `Commands/ParsedCommand.cs`
Record types for each command:
- `FlyHeadingCommand(int Heading)`
- `TurnLeftCommand(int Heading)`, `TurnRightCommand(int Heading)`
- `LeftTurnCommand(int Degrees)`, `RightTurnCommand(int Degrees)`
- `FlyPresentHeadingCommand`
- `ClimbMaintainCommand(int Altitude)`, `DescendMaintainCommand(int Altitude)`
- `SpeedCommand(int Speed)` — 0 means cancel restriction
- `SquawkCommand(uint Code)`, `SquawkIdentCommand(uint Code)`, `SquawkVfrCommand`, `SquawkNormalCommand`, `SquawkStandbyCommand`, `IdentCommand`
- `DeleteCommand`, `PauseCommand`, `UnpauseCommand`, `SimRateCommand(int Rate)`
- `SquawkAllCommand`
- `UnsupportedCommand(string RawText)` — for PUSH/TAXI/WAIT/SAY etc. (M2/M3)

### Create `Commands/CommandParser.cs`
- `static ParsedCommand? Parse(string input)` — ATCTrainer canonical format only
- Altitude parsing: values < 1000 → multiply by 100 (e.g., `CM 240` = 24000 ft, `CM 5000` = 5000 ft)
- Ground commands (PUSH, TAXI, WAIT, SAY) → `UnsupportedCommand` with warning

### Create `Commands/CommandDispatcher.cs`
- `Dispatch(ParsedCommand, AircraftState) → CommandResult`
- Maps each command to ControlTargets changes:
  - FlyHeading → `Targets.TargetHeading`, clear turn direction
  - TurnLeft → `Targets.TargetHeading` + `PreferredTurnDirection = Left`
  - LeftTurn → compute relative heading, set target + direction
  - ClimbMaintain/DescendMaintain → `Targets.TargetAltitude`
  - Speed → `Targets.TargetSpeed` (0 = clear)
  - Squawk → `BeaconCode`
  - SquawkVfr → `BeaconCode = 1200`
  - SquawkNormal/Standby → `TransponderMode`
  - Ident → `IsIdenting = true`
  - Delete → remove from world

---

## Chunk 5: Scenario Models + ScenarioLoader (Yaat.Server)

### Create `Scenarios/ScenarioModels.cs`
Deserialization models matching ATCTrainer JSON:
- `Scenario` (top-level)
- `ScenarioAircraft` with callsign, type, transponder mode, starting conditions, flight plan, preset commands, spawn delay, auto-track conditions
- `StartingConditions` with polymorphic `type` discriminator → `CoordinatesCondition`, `FixOrFrdCondition`, `ParkingCondition`, `OnRunwayCondition`, `OnFinalCondition`
- `ScenarioFlightPlan`, `PresetCommand`, `InitializationTrigger`, `AutoTrackConditions`, `AircraftGenerator`, `ScenarioAtc`, `FlightStripConfiguration`

Use `System.Text.Json` with `[JsonPolymorphic]` / `[JsonDerivedType]` on `StartingConditions`.

### Create `Scenarios/ScenarioLoader.cs`
- `Load(string json, IFixLookup) → ScenarioLoadResult`
- For `Coordinates`: directly map lat/lon/alt/speed/heading
- For `FixOrFrd`: resolve via FrdResolver, set alt/speed from condition fields
  - If no speed: default by altitude band + aircraft category (aviation-sim-expert to advise)
  - If no heading: compute heading toward `navigationPath` waypoint, or 0 with warning
- For `Parking`/`OnRunway`/`OnFinal`: skip with "deferred to M2/M3" warning
- Populate flight plan fields on AircraftState from scenario flightplan
- Preserve spawnDelay for deferred spawning

### Create `Scenarios/FrdResolver.cs`
- Parse FRD strings: `OAK060012` → fix=OAK, radial=060, distance=12nm
- Project lat/lon along radial from fix position using great-circle math
- If string is a bare fix name (no radial/distance suffix), resolve directly

### Create `Data/IFixLookup.cs` + `Data/FixDatabase.cs`
- `IFixLookup.GetFixPosition(string name) → (double Lat, double Lon)?`
- `FixDatabase` loads from a static JSON file (`Data/Fixes/zoa-fixes.json`)
- Extract ZOA-area fixes (VORs, waypoints) for initial testing
- Interface allows swapping to full NavData loading in M4

---

## Chunk 6: Training Hub Expansion + Sim Engine (Yaat.Server)

### Modify `SimulationHostedService.cs`
- Add `bool _isPaused`, `double _simRate = 1.0`
- When paused: tick still broadcasts current state but doesn't advance physics
- Sim rate: pass `deltaSeconds * _simRate` to physics
- Add scenario clock (`double _scenarioElapsedSeconds`)
- Delayed aircraft queue: check each tick, spawn when delay elapsed
- Initialization triggers: check each tick, dispatch SQALL etc. at timeOffset
- Remove hardcoded default aircraft — server starts empty
- Expose methods: `LoadScenario()`, `SendCommand()`, `DeleteAircraft()`, `Pause()`, `Resume()`, `SetSimRate()`

### Modify `TrainingHub.cs`
New hub methods:
- `LoadScenario(string scenarioJson) → LoadScenarioResult`
- `SendCommand(string callsign, string command) → CommandResultDto`
- `DeleteAircraft(string callsign)`
- `PauseSimulation()` / `ResumeSimulation()`
- `SetSimRate(int rate)`

New server→client events:
- `ScenarioLoaded(string name, int count)`
- `AircraftDeleted(string callsign)`
- `SimulationStateChanged(bool isPaused, int simRate)`
- `AircraftSpawned(AircraftStateDto aircraft)` — when delayed aircraft appears

### Modify `TrainingDtos.cs`
Expand `AircraftStateDto` with: TransponderMode, VerticalSpeed, AssignedHeading, AssignedAltitude, AssignedSpeed, Departure, Destination, Route, FlightRules.

Add: `LoadScenarioResult`, `CommandResultDto`.

### Modify `DtoConverter.cs`
- `ToTrainingDto()`: include new AircraftState fields
- `ToEramTarget()`: include VerticalSpeed
- `ToFlightPlan()`: use scenario-populated flight plan data
- `ToStarsTrack()`: reflect TransponderMode properly

---

## Chunk 7: Client UI — Scenario, Commands, Sim Controls (Yaat.Client)

### Modify `ServerConnection.cs`
Add methods: `LoadScenarioAsync`, `SendCommandAsync`, `DeleteAircraftAsync`, `PauseSimulationAsync`, `ResumeSimulationAsync`, `SetSimRateAsync`

Add events: `AircraftDeleted`, `AircraftSpawned`, `SimulationStateChanged`

Expand `AircraftDto` with assigned values, flight plan, transponder mode.

### Modify `AircraftModel.cs`
Add observable properties: `AssignedHeading`, `AssignedAltitude`, `AssignedSpeed`, `TransponderMode`, `VerticalSpeed`, `Departure`, `Destination`, `Route`, `FlightRules`, `IsSelected`.

### Modify `MainViewModel.cs`
- Scenario: `ScenarioFilePath`, `BrowseScenarioCommand` (file dialog), `LoadScenarioCommand`
- Aircraft selection: `SelectedAircraft` bound to DataGrid selection
- Command input: `CommandText` property, `SendCommandCommand` on Enter
  - Uses active `CommandScheme` to translate user input to canonical before sending
  - Global commands (PAUSE, UNPAUSE, SIMRATE) don't need aircraft selection
- Command history: `ObservableCollection<string>` last 50 commands, arrow up/down
- Sim controls: `IsPaused`, `PauseCommand`/`ResumeCommand`, `SimRate`, `SetSimRateCommand`
- Handle server events: AircraftDeleted, AircraftSpawned, SimulationStateChanged

### Modify `MainWindow.axaml`
Layout (top to bottom):
1. **Connection bar**: URL + Connect + Status (existing)
2. **Scenario bar**: File path + Browse + Load
3. **Aircraft DataGrid**: Existing columns + AssignedHdg, AssignedAlt, AssignedSpd, Dep, Dest, Rules, Squawk
4. **Sim controls**: Pause/Resume toggle + SimRate selector (1x/2x/4x/8x)
5. **Command bar**: Selected aircraft label + command TextBox + Send

### Create Settings modal (new Window)
- `SettingsWindow.axaml` + `SettingsViewModel.cs`
- Tab: **Command Scheme**
  - Dropdown: preset selection (ATCTrainer / VICE / Custom)
  - Table: CommandType | Current Verb | Format Pattern — editable
  - Reset to ATCTrainer / Reset to VICE buttons
  - Save button
- Opened from a Settings button/menu in MainWindow

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

Chunks 4 and 5 can be built in parallel after chunks 1-2. Chunk 3 (command scheme types) can also proceed in parallel since it's mostly independent infrastructure.

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

1. Start yaat-server, connect YAAT client
2. Browse to an ATCTrainer scenario JSON with `FixOrFrd`/`Coordinates` aircraft
3. Load scenario → airborne aircraft appear in DataGrid and CRC
4. Select aircraft, type `FH 270` → aircraft turns in CRC
5. Type `CM 50` → aircraft climbs to 5000
6. Type `SPD 200` → aircraft adjusts speed
7. Pause → aircraft freeze; Resume → they move again
8. SimRate 4x → movement accelerates
9. Squawk commands: `SQ 1234` → code changes in CRC
10. Switch to VICE command scheme in Settings → `H270` works equivalently
11. Aircraft with `spawnDelay` appear at correct times after scenario load
12. SQALL initialization trigger fires at configured offsets
