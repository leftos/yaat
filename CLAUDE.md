# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

YAAT (Yet Another ATC Trainer) is an instructor/RPO desktop client for air traffic control training. It connects to a separate [yaat-server](https://github.com/leftos/yaat-server) instance via SignalR and displays/controls simulated aircraft. The server also feeds CRC (the VATSIM radar client) via a CRC-compatible SignalR+MessagePack hub.

## Build & Run

```bash
# Build the entire solution from repo root
dotnet build

# Run the client app
dotnet run --project src/Yaat.Client
```

Requires .NET 8 SDK and a running yaat-server instance (default `http://localhost:5000`).

The solution uses `.slnx` format (`yaat.slnx`). If your IDE doesn't support `.slnx`, use `dotnet` CLI directly.

## Architecture

> **Keep this section up-to-date.** Before each git commit, review the Architecture section and update it to reflect any structural changes (new files, renamed files, moved responsibilities, new patterns). Outdated architecture docs mislead both developers and LLMs.

Three projects across two repos. **Yaat.Sim** is the shared simulation library referenced by both Yaat.Client and yaat-server.

**Yaat.Sim owns all simulation and aviation logic.** Flight physics, phase behavior, pattern geometry, performance constants, command dispatching, command queue — everything aviation or simulation belongs in Yaat.Sim. The server is a thin comms layer. If server code makes aviation decisions, move it to Yaat.Sim.

### Yaat.Client — Avalonia desktop app (`src/Yaat.Client/`)

```
Logging/
  AppLog.cs                    # Static logger factory; Initialize() in Program.Main()
  FileLoggerProvider.cs        # Writes to %LOCALAPPDATA%/yaat/yaat-client.log

Models/
  AircraftModel.cs             # ObservableObject wrapping AircraftDto fields
                               # Computed: StatusDisplay, PhaseSequenceDisplay, ClearanceDisplay,
                               #   DistanceFromFix; StatusSortComparer for DataGrid
                               # FromDto() factory + UpdateFromDto() for DTO→model mapping
  TerminalEntry.cs             # Immutable entry for terminal/radio log (Kind: Command/Response/System/Say)

Services/
  ServerConnection.cs          # Single SignalR client to /hubs/training (JSON protocol)
                               # DTOs defined inline: AircraftDto, LoadScenarioResultDto, etc.
  CommandScheme.cs              # Maps CanonicalCommandType → CommandPattern (aliases + format)
                               # Factory methods: AtcTrainer(), Vice(); DetectPresetName()
  AtcTrainerPreset.cs           # SpaceSeparated scheme: FH 270, CM 240, DM 50, SPD 250
  VicePreset.cs                 # Concatenated scheme: H270, C240, D50, S250
  CommandSchemeParser.cs        # Parse() for single commands, ParseCompound() for ;/, syntax
                               # ToCanonical() always outputs ATCTrainer format
  CommandMetadata.cs            # Static registry: CommandInfo per type (label, sample arg, IsGlobal)
  CommandInputController.cs     # Autocomplete suggestions (callsign/command/fix/route fix)
                               # History navigation (up/down with prefix filter)
                               # FixDb binary search for nav fix prefix matching
  UserPreferences.cs            # JSON persistence to %LOCALAPPDATA%/yaat/preferences.json
                               # Fields: CommandScheme, UserInitials, admin state, window geometries, grid layout

ViewModels/
  MainViewModel.cs              # Root ViewModel (no DI); owns ServerConnection, UserPreferences,
                               #   CommandInputController; SendCommandAsync pipeline:
                               #   chat detection → global cmd → callsign resolve → ParseCompound → hub
                               # Nav data init (VnasDataService + FixDatabase) fire-and-forget in ctor
                               # Distance reference: resolves fix/FRD, computes per-aircraft distances
                               # ApplyScenarioResult() shared by load/rejoin/reconnect
  SettingsViewModel.cs          # Modal: VerbMappingRow collection for alias editing; preset detection
  *Converter.cs                 # IValueConverters: Connect/Pause/Dock buttons, terminal entry colors

Views/
  MainWindow.axaml.cs           # Creates MainViewModel; DataGrid column order/sort persistence;
                               #   distance reference flyout with fix search; terminal dock/undock
  CommandInputView.axaml.cs     # Keyboard handling: Esc/Up/Down/Tab/Enter for suggestions/history
  TerminalPanelView.axaml.cs    # Auto-scroll terminal with user-scroll detection
  TerminalWindow.axaml.cs       # Pop-out terminal window (shares MainViewModel DataContext)
  SettingsWindow.axaml.cs       # Modal settings dialog
  WindowGeometryHelper.cs       # Save/restore window position+size; validates on-screen visibility
```

### Yaat.Sim — Shared simulation library (`src/Yaat.Sim/`)

No UI dependencies. Deps: Google.Protobuf (NavData), Microsoft.Extensions.Logging.Abstractions.

```
# Core state & physics
AircraftState.cs               # Mutable entity per aircraft: position, flight plan, identity, control,
                               #   ground state, PendingWarnings feedback list
ControlTargets.cs              # Autopilot-style targets: heading, altitude, speed, NavigationRoute
                               #   (List<NavigationTarget>); FlightPhysics reads each tick
FlightPhysics.cs               # Static. 6-step Update(): navigation → heading → altitude → speed
                               #   → position → command queue. Trigger checking (ReachAltitude,
                               #   ReachFix, InterceptRadial, ReachFrdPoint, GiveWay). Geo helpers.
                               #   NormalizeHeading/NormalizeHeadingInt (internal, shared with CommandDispatcher)
GeoMath.cs                     # Static. DistanceNm (haversine), BearingTo, TurnHeadingToward
SimulationWorld.cs             # Thread-safe aircraft collection. GetSnapshot/GetSnapshotByScenario,
                               #   Tick/TickScenario (with preTick callback), DrainWarnings,
                               #   GenerateBeaconCode
CommandQueue.cs                # CommandBlock (trigger + Action<AircraftState> closure + TrackedCommands),
                               #   BlockTrigger (type + fix/alt/radial/callsign params),
                               #   TrackedCommand (Heading/Altitude/Speed/Navigation/Immediate/Wait)
AircraftCategory.cs            # AircraftCategory enum (Jet/Turboprop/Piston)
                               # AircraftCategorization: static Initialize() from AircraftSpecs.json
                               # CategoryPerformance: all aviation constants (turn rates, climb/descent
                               #   rates, pattern sizes, approach/landing speeds, taxi speeds, holding
                               #   speeds). All validated by aviation-sim-expert.
GroundConflictDetector.cs      # Static. ComputeSpeedOverrides(): pairwise ground proximity checks,
                               #   returns max-speed dictionary; called by server preTick

# Commands/
Commands/CanonicalCommandType.cs  # Enum of every command (heading, altitude, speed, transponder,
                               #   navigation, tower, pattern, hold, ground, spawn, sim control)
Commands/ParsedCommand.cs      # Discriminated union records for all command types;
                               #   CompoundCommand/ParsedBlock/BlockCondition hierarchy
Commands/CommandDispatcher.cs  # Static. DispatchCompound(): phase interaction (CanAcceptCommand →
                               #   Allowed/Rejected/ClearsPhase), builds CommandBlocks with closures.
                               #   ApplyCommand: switch setting ControlTargets per command type.
                               #   TryApplyTowerCommand: delegates to TryXxx helpers per command.
Commands/CommandDescriber.cs   # Static. Description/classification extracted from CommandDispatcher:
                               #   DescribeCommand (terse), DescribeNatural (human-readable),
                               #   ToCanonicalType, ClassifyCommand, IsTowerCommand, IsGroundCommand
Commands/AltitudeResolver.cs   # Plain int or AGL format (KOAK010) → feet MSL via IFixLookup
Commands/RouteChainer.cs       # After DCT to an on-route fix, appends remaining route fixes

# Phases/ — structured clearance-gated behavior (tower, pattern, ground)
Phases/Phase.cs                # Abstract base: Name, Status, Requirements (ClearanceRequirement list),
                               #   OnStart/OnTick/OnEnd, CanAcceptCommand → CommandAcceptance,
                               #   SatisfyClearance. When CurrentPhase != null, CommandQueue is bypassed.
Phases/PhaseList.cs            # Mutable list on AircraftState: AssignedRunway, TaxiRoute,
                               #   LandingClearance, TrafficDirection (pattern mode auto-cycles),
                               #   mutation: Start/AdvanceToNext/InsertAfterCurrent/ReplaceUpcoming/
                               #   SkipTo<T>/Clear
Phases/PhaseRunner.cs          # Static. Drives lifecycle: start → tick → advance. Auto-appends
                               #   RunwayExitPhase after landing, or next pattern circuit in pattern mode.
Phases/PhaseContext.cs         # Readonly tick context: Aircraft, Targets, Category, DeltaSeconds,
                               #   Runway, FieldElevation, GroundLayout, AircraftLookup
Phases/CommandAcceptance.cs    # Enum: Allowed (clearance satisfied), Rejected, ClearsPhase
Phases/ClearanceType.cs        # Enum: LineUpAndWait, ClearedForTakeoff, ClearedToLand,
                               #   ClearedForOption, ClearedTouchAndGo, ClearedStopAndGo, RunwayCrossing
Phases/RunwayInfo.cs           # Runway geometry: threshold/end lat/lon, heading, elevation, dimensions
Phases/GlideSlopeGeometry.cs   # AltitudeAtDistance, RequiredDescentRate (3° default)
Phases/PatternGeometry.cs      # Computes 7 pattern waypoints from RunwayInfo + category + direction
Phases/PatternBuilder.cs       # BuildCircuit (from any leg), BuildNextCircuit, UpdateWaypoints

# Phases/Tower/ — departure, approach, landing phases
Phases/Tower/LinedUpAndWaitingPhase.cs  # Holds at threshold; awaits ClearedForTakeoff
Phases/Tower/TakeoffPhase.cs            # Ground roll → Vr liftoff → completes at 400ft AGL
Phases/Tower/InitialClimbPhase.cs       # Climb to 1500ft AGL or assigned altitude
Phases/Tower/FinalApproachPhase.cs      # Glideslope descent; auto-go-around at 0.5nm if no clearance
Phases/Tower/LandingPhase.cs            # Flare → touchdown → rollout to 20 kts
Phases/Tower/GoAroundPhase.cs           # TOGA power, runway heading, climb to 1500ft AGL
Phases/Tower/TouchAndGoPhase.cs         # Brief rollout then re-accelerate, completes at 400ft AGL
Phases/Tower/StopAndGoPhase.cs          # Full stop, pause, then takeoff from zero
Phases/Tower/LowApproachPhase.cs        # Fly glideslope to low alt, climb out without landing
Phases/Tower/HoldAtFixPhase.cs          # Navigate to fix, then 360° orbits or helicopter hover
Phases/Tower/HoldPresentPositionPhase.cs # 360° orbits or hover at current position

# Phases/Pattern/ — traffic pattern legs
Phases/Pattern/UpwindPhase.cs           # Completes at crosswind turn point
Phases/Pattern/CrosswindPhase.cs        # Completes at downwind start
Phases/Pattern/DownwindPhase.cs         # Descent starts abeam; completes at base turn
Phases/Pattern/BasePhase.cs             # Completes when aligned with extended centerline
Phases/Pattern/MidfieldCrossingPhase.cs # Crosses midfield at pattern alt + 500ft

# Phases/Ground/ — surface movement phases
Phases/Ground/AtParkingPhase.cs         # Awaits pushback/taxi/delete
Phases/Ground/PushbackPhase.cs          # Push to target heading or default distance
Phases/Ground/TaxiingPhase.cs           # Follows TaxiRoute segments; auto-inserts HoldingShort
Phases/Ground/HoldingShortPhase.cs      # Awaits RunwayCrossing clearance
Phases/Ground/CrossingRunwayPhase.cs    # Crosses to far-side node
Phases/Ground/RunwayExitPhase.cs        # Exits to nearest exit node; emits "clear of runway"
Phases/Ground/HoldingAfterExitPhase.cs  # Awaits taxi/delete after runway exit
Phases/Ground/FollowingPhase.cs         # Speed-matches leader with 180ft following distance

# Data/ — navigation, airport, and VNAS data
Data/IFixLookup.cs             # Interface: GetFixPosition, GetAirportElevation
Data/IRunwayLookup.cs          # Interface: GetRunway, GetRunways
Data/FixDatabase.cs            # Implements both; indexed from VNAS NavDataSet protobuf + custom fixes
                               #   AllFixNames sorted array for binary-search autocomplete
                               #   ExpandRoute() for route-fix suggestions
Data/CustomFixDefinition.cs    # JSON: Name, Aliases, optional Lat/Lon or Frd string
Data/CustomFixLoader.cs        # Scans data/custom_fixes/**/*.json
Data/FrdResolver.cs            # Fix-Radial-Distance → lat/lon via spherical projection

# Data/Airport/ — ground layout graph
Data/Airport/IAirportGroundData.cs      # Interface: GetLayout(airportId) → AirportGroundLayout?
Data/Airport/AirportGroundLayout.cs     # Graph: Nodes (parking/spot/holdShort/intersection) + Edges
                                        # FindNearestExit (heading-aware), GetRunwayHoldShortNodes
Data/Airport/GroundNode.cs              # {Id, Lat, Lon, Type, Name?, RunwayId?, Edges}
Data/Airport/GroundEdge.cs              # {From, To, TaxiwayName, DistanceNm, IntermediatePoints}
Data/Airport/TaxiRoute.cs              # Resolved path: Segments + HoldShortPoints + completion tracking
Data/Airport/TaxiPathfinder.cs         # ResolveExplicitPath (user-specified taxiways), FindRoute (A*)
Data/Airport/GeoJsonParser.cs          # GeoJSON → AirportGroundLayout (7-step build with snap grid)

# Data/Vnas/ — VNAS data pipeline
Data/Vnas/VnasDataService.cs   # Downloads NavData protobuf + AircraftSpecs/CWT; serial-based cache
Data/Vnas/AiracCycle.cs        # AIRAC cycle calculator (epoch Jan 23 2025, 28-day cycles)
Data/Vnas/VnasConfig.cs        # DTO for configuration API response

# Scenarios/ — aircraft spawning
Scenarios/AircraftInitializer.cs  # InitializeOnRunway, InitializeAtParking, InitializeOnFinal
                                  # Returns PhaseInitResult (phases + position/speed)
Scenarios/AircraftGenerator.cs    # Generates AircraftState from SpawnRequest (type/airline tables)
Scenarios/SpawnRequest.cs         # Spawn descriptor: rules, weight, engine, position type + params

# Proto/
Proto/nav_data.proto           # Compiled by Grpc.Tools → NavDataSet (Airports, Fixes, Sids, Stars)
```

### yaat-server — ASP.NET Core server (`X:\dev\yaat-server\`)

Separate repo. References Yaat.Sim via sibling project ref (preferred) or git submodule fallback. The server provides: SignalR comms with clients, CRC protocol compatibility, scenario loading, session management, and broadcast fan-out.

```
src/Yaat.Server/
  Program.cs                   # DI setup (all singletons), VNAS init, route mapping.
                               #   Validates AdminPassword at startup (refuses to start without it).
  YaatOptions.cs               # IOptions: AdminPassword from config/env

  Hubs/
    TrainingHub.cs             # Standard SignalR hub (/hubs/training, JSON). Delegates all logic
                               #   to SimulationHostedService methods.
    CrcWebSocketHandler.cs     # Raw WebSocket upgrade handler for /hubs/client
                               #   Depends on CrcBroadcastService (not SimulationHostedService)
    CrcClientState.cs          # Per-CRC-connection state machine: handshake → StartSession →
                               #   ActivateSession → Subscribe(topics) → receive broadcasts.
                               #   Topic subscriptions: StarsTracks, FlightPlans, EramTargets,
                               #   EramDataBlocks, AsdexTargets, AsdexTracks, TowerCabAircraft
                               #   Depends on CrcBroadcastService for BuildInitialData
    CrcClientManager.cs        # ConcurrentDictionary registry; BroadcastAsync fan-out
    NegotiateHandler.cs        # POST /hubs/client/negotiate → fake negotiation JSON for CRC
    ApiStubHandler.cs          # GET/POST /api/* → [] (satisfies CRC startup probes)

  Simulation/
    SimulationHostedService.cs # Central orchestrator. IHostedService with 1-second PeriodicTimer.
                               #   Tick: TickScenario (with GroundConflictDetector preTick) →
                               #   ProcessDelayedSpawns → ProcessTriggers → BroadcastUpdates
                               #   (training group + admins). Also the API surface called by
                               #   TrainingHub for all operations. CRC broadcast delegated to
                               #   CrcBroadcastService.
    CrcBroadcastService.cs     # CRC wire-protocol broadcast (extracted from SimulationHostedService).
                               #   BroadcastUpdatesAsync (per-tick), BroadcastDeletesAsync,
                               #   BuildInitialData (topic subscription). Owns TopicFormatter.
                               #   Depends on CrcClientManager + CrcVisibilityTracker.
    ScenarioSession.cs         # Per-scenario state: clients, pause, simRate, elapsed time,
                               #   delayed spawn queue, trigger queue, cleanup timer
    ScenarioSessionManager.cs  # Thread-safe session registry + client→scenario reverse lookup
                               #   + admin filter tracking
    CrcVisibilityTracker.cs    # Per-aircraft CRC visibility: STARS (100ft AGL + 5s coast),
                               #   ASDEX (per-airport range/ceiling), TowerCab (20nm/4000ft AGL)
    DtoConverter.cs            # AircraftState → CRC DTOs (StarsTrack, FlightPlan, EramTarget,
                               #   EramDataBlock, TowerCab, Asdex) + training AircraftStateDto

  Commands/
    CommandParser.cs           # Server-side canonical command parsing (all verbs)
    ServerCommands.cs          # Server-only records (DEL, PAUSE, etc.)

  Scenarios/
    ScenarioLoader.cs          # JSON → aircraft: resolves 5 position types (coordinates, fix/FRD,
                               #   onRunway, onFinal, parking) via Yaat.Sim initializers.
                               #   CreateBaseState() shared across all loading methods.
    ScenarioModels.cs          # Deserialization models: Scenario, ScenarioAircraft,
                               #   StartingConditions, ScenarioFlightPlan, PresetCommand, triggers

  Spawn/
    SpawnParser.cs             # Parses ADD command args → SpawnRequest

  Protocol/                    # CRC binary wire format (raw WebSocket, not standard SignalR)
    VarintCodec.cs             # LEB128 encode/decode
    MessageFraming.cs          # Varint-length-prefixed framing (Frame/Parse)
    SignalRMessageParser.cs    # MessagePack array → typed AppMessage records
    SignalRMessageBuilder.cs   # Build binary Invocation/Response/NilAck/Ping messages

  Dtos/
    TrainingDtos.cs            # JSON DTOs: AircraftStateDto, LoadScenarioResult, CommandResultDto,
                               #   ScenarioSessionInfoDto, DeleteAllResultDto, TerminalBroadcastDto
    CrcDtos.cs                 # MessagePack [Key(N)] DTOs: StarsTrackDto (37 fields),
                               #   FlightPlanDto (33 fields), EramTargetDto, EramDataBlockDto,
                               #   TowerCabAircraftDto, AsdexTargetDto/TrackDto, SessionInfoDto
    CrcEnums.cs                # CRC-compatible enums (StarsCoastPhase, TransponderMode, etc.)
    TopicFormatter.cs          # Topic class + custom MessagePack formatter

  Data/
    ArtccConfig.cs             # VNAS ARTCC config models (recursive FacilityConfig tree)
    ArtccConfigService.cs      # On-demand ARTCC download; extracts ASDEX/TowerCab airport info

  Udp/
    UdpStubServer.cs           # UDP port 6809: RegisterConnection ack + keepalive pings

  Logging/
    FileLoggerProvider.cs      # File logger to yaat-server.log (overwrites each startup)
```

**Key patterns:**
- `ServerConnection` is the single SignalR client connecting to `/hubs/training` (JSON protocol, not MessagePack). DTOs (`AircraftDto`, `LoadScenarioResultDto`, `CommandResultDto`, etc.) are records defined in the same file.
- ViewModels use `[ObservableProperty]` and `[RelayCommand]` from CommunityToolkit.Mvvm — fields are `_camelCase`, auto-generated properties are `PascalCase`
- SignalR callbacks arrive on a background thread; ViewModels marshal to UI via `Avalonia.Threading.Dispatcher.UIThread.Post()`
- No DI container — `MainWindow` creates `MainViewModel` directly, which instantiates `ServerConnection` as a field
- `SimulationWorld.GetSnapshot()` returns a shallow list copy; callers should treat returned `AircraftState` objects as read-only
- `UserPreferences` persists to `%LOCALAPPDATA%/yaat/preferences.json` (command scheme, admin settings, window geometry)
- `AppLog` is a static logger factory; logs to `%LOCALAPPDATA%/yaat/yaat-client.log`

**Command parsing pipeline:**
RPO commands are parsed client-side using a configurable `CommandScheme` (ATCTrainer or VICE presets). The flow:
1. User types input in command bar (optionally prefixed with callsign)
2. `MainViewModel.SendCommandAsync()` resolves callsign via partial match
3. `CommandSchemeParser.ParseCompound()` handles compound syntax: `;` separates sequential blocks, `,` separates parallel commands, `LV {alt}` / `AT {fix}` prefix conditions
4. Each command is translated to canonical ATCTrainer format (`FH 270`, `CM 240`, etc.)
5. The full canonical string is sent to the server via `SendCommand(callsign, canonicalString)`

The server builds a `CommandQueue` of `CommandBlock`s from the canonical string. Each block has an optional `BlockTrigger` (reach altitude, reach fix) and an `ApplyAction` that sets `ControlTargets`. `FlightPhysics.UpdateCommandQueue()` checks triggers and advances blocks each tick.

**Command naming convention:**
When adding new commands, match the existing command names from ATCTrainer and/or VICE where possible. See `docs/command-aliases-reference.md` and keep it updated as you go.

**Command completeness rule — MANDATORY:**
Every value in `CanonicalCommandType` (in Yaat.Sim) MUST have a corresponding entry in:
1. `CommandScheme.AtcTrainer()` patterns
2. `CommandScheme.Vice()` patterns
3. `CommandMetadata.AllCommands`

Unit tests in `tests/Yaat.Client.Tests/CommandSchemeCompletenessTests.cs` enforce this — `dotnet test` will fail if any are missing. When adding a new `CanonicalCommandType` value, update all three locations in the same commit.

**Communication flow:**
```
YAAT Client (this repo)  ──SignalR JSON──>  yaat-server  <──SignalR+MessagePack──  CRC
     /hubs/training                                           /hubs/client
```

The training hub uses standard ASP.NET SignalR with JSON. The CRC hub uses raw WebSocket with varint+MessagePack binary framing (handled entirely by yaat-server).

**SignalR hub methods (client→server):**
- `GetAircraftList()` → `List<AircraftDto>` — called on connect
- `LoadScenario(scenarioJson)` → `LoadScenarioResultDto` — load and start scenario
- `LeaveScenario()` — leave current scenario
- `GetActiveScenarios()` → `List<ScenarioSessionInfoDto>` — list resumable scenarios
- `RejoinScenario(scenarioId)` → `LoadScenarioResultDto` — rejoin existing scenario
- `SendCommand(callsign, command)` → `CommandResultDto` — issue RPO command
- `DeleteAircraft(callsign)` / `DeleteAllAircraft()` / `ConfirmDeleteAll()`
- `PauseSimulation()` / `ResumeSimulation()` / `SetSimRate(rate)`
- `AdminAuthenticate(password)` / `AdminGetScenarios()` / `AdminSetScenarioFilter(scenarioId?)`
- `Heartbeat` — 30-second keepalive

**Server→client events:**
- `AircraftUpdated(AircraftDto)` — pushed on each sim tick
- `AircraftSpawned(AircraftDto)` — new aircraft (e.g., from delayed spawn)
- `AircraftDeleted(callsign)` — aircraft removed
- `SimulationStateChanged(isPaused, simRate)` — pause/rate changes

## Tech Stack

- .NET 10, C# with nullable enabled, implicit usings
- Avalonia UI 11.2.5 with Fluent theme (dark mode) + DataGrid — source at https://github.com/AvaloniaUI/Avalonia (DataGrid is in a separate repo: https://github.com/AvaloniaUI/Avalonia.Controls.DataGrid)
- CommunityToolkit.Mvvm 8.4.0 for MVVM source generators
- Microsoft.AspNetCore.SignalR.Client 10.0.3

## Related Repositories

- **yaat-server** (`X:\dev\yaat-server`) — ASP.NET Core 10 server with simulation engine, CRC protocol, training hub
- **vatsim-server-rs** (`X:\dev\vatsim-server-rs`) — Rust reference implementation for CRC protocol (DTO field ordering, varint framing)
- **lc-trainer** (`X:\dev\lc-trainer`) — Previous WPF ATC trainer (reference for flight physics, scenario format). ATCTrainer scenario JSON examples at `X:\dev\lc-trainer\docs\atctrainer-scenario-examples\`

> **lc-trainer is NOT a trusted reference.** It is WIP, flawed, and unreviewed. It may be used as inspiration but every aviation detail drawn from it MUST be reviewed by the `aviation-sim-expert` agent. Do not port code from lc-trainer without independent validation. Prefer a fresh, well-organized approach over copying its patterns.

**vNAS source code reference** (`X:\dev\towercab-3d-vnas\docs\repos\`) — Snapshots of official vNAS C# repos. Use these as authoritative references for CRC protocol compatibility, data models, and messaging DTOs:
- **common-master** (`Vatsim.Nas.Common`) — Shared utilities: `GeoCalc`, `NavCalc`, `GeoPoint`, `ParsedAltitude`, `TransponderMode`, `TurnDirection`, `NetworkRating`, `Metar`, etc.
- **data-master** (`Vatsim.Nas.Data`) — Data models for navigation (`Airport`, `Fix`, `Runway`, `Sid`, `Star`), training scenarios (`Scenario`, `ScenarioAircraft`, `TrainingAirport`), aircraft specs (`AircraftSpec`, `AircraftCwt`), and facility configuration (`StarsConfiguration`, `EramConfiguration`, `TowerCabConfiguration`, etc.)
- **messaging-master** (`Vatsim.Nas.Messaging`) — SignalR entities/DTOs (`EramTrackDto`, `StarsTrackDto`, `FlightPlanDto`, `TowerCabAircraftDto`, `ClearanceDto`), commands (`ProcessStarsCommandDto`, `JoinSessionDto`), and topics (`Topic`, `TopicCategory`). This is the definitive reference for CRC hub message shapes.

**vNAS Configuration API** (`https://configuration.vnas.vatsim.net/`) — Returns serials and URLs for aircraft specs, CWT data, and nav data (used by `VnasDataService` for cache staleness checks). Also lists environment endpoints (Live, Sweatbox 1/2, Test) with their SignalR hub and API base URLs.

**vNAS Data API** (`https://data-api.vnas.vatsim.net/api/artccs/{id}`, e.g. `.../ZOA`) — Full ARTCC facility configuration. Recursively nested child facilities (TRACONs, ATCTs), each with: positions (frequencies, callsigns, transceivers), STARS/ERAM/ASDEX/TDLS configuration, tower cab config (video maps, tower location), flight strips config, and neighboring facility references. Use this to understand real facility hierarchies and operational configuration.

## Aviation Realism — MANDATORY

This project simulates real-world air traffic control. **Every feature touching aviation must be reviewed by the `aviation-sim-expert` agent** (via the Task tool with `subagent_type: "aviation-sim-expert"`). This is not optional.

**Always use the aviation-sim-expert agent when:**
- Implementing or modifying flight physics (climb/descent profiles, speed schedules, turn rates, fuel burn, wind effects, performance envelopes)
- Writing or reviewing pilot AI behavior (decision-making, SOPs, communication patterns, compliance with ATC instructions)
- Implementing ATC logic (separation minima, clearance delivery, approach/departure sequencing, vectoring, altitude assignments, holding patterns)
- Writing radio communication code (phraseology, readback/hearback, frequency management, ATIS)
- Modeling aircraft performance (speed constraints by altitude/phase, VNAV/LNAV profiles, weight-based performance)
- Designing airspace rules (classification, transition altitudes, SIDs/STARs, restricted areas)
- Creating or editing scenario data (realistic routes, waypoints, procedures, airline callsigns)
- Designing or modifying **phase transitions** (tower phases, pattern legs, ground operations) — phase sequencing, clearance gating, and state machine logic must reflect real-world procedures
- Adding or changing **command dispatch logic** that affects aviation behavior — e.g., how commands interact with phases (Allowed/Rejected/ClearsPhase), what happens when a pilot receives conflicting instructions
- Implementing **ground operations** (taxi routing, hold-short logic, runway crossing, pushback, following) — surface movement rules are just as regulated as airborne operations
- Designing **conflict detection** logic (separation, wake turbulence, ground proximity) — incorrect thresholds or missing cases undermine training value
- Setting **trigger conditions** for command blocks (reach altitude, reach fix, intercept radial) — these must match how real pilots anticipate and execute clearances
- Any logic that determines **when an aircraft should do something automatically** (go-around decision, pattern re-entry, speed reduction on approach) — pilot AI behavior is aviation logic

**How to invoke:** Use `Task` with `subagent_type: "aviation-sim-expert"` and a clear description of what needs review or design. The agent has access to all tools and can read the codebase.

**Do not guess aviation details.** Real-world ATC and flight operations have strict, well-defined rules (FAA 7110.65, AIM, ICAO Doc 4444). Getting them wrong breaks the training value of the simulator. When in doubt, ask the aviation-sim-expert agent rather than approximating.

**Local FAA reference library — DO NOT web-search for these:**
The full text of the FAA 7110.65 and AIM are available locally as markdown. Use `Read`, `Grep`, and `Glob` on these paths instead of web searches:
- **7110.65**: `C:\Users\Leftos\.claude\reference\faa\7110.65/` (index: `INDEX.md`)
- **AIM**: `C:\Users\Leftos\.claude\reference\faa\aim/` (index: `INDEX.md`)
- **Top-level index**: `C:\Users\Leftos\.claude\reference\faa\INDEX.md`

When invoking the aviation-sim-expert agent, always include this instruction in the prompt:
> "IMPORTANT: The FAA 7110.65 and AIM are available as local markdown files. Read them directly via the Read/Grep/Glob tools at `C:\Users\Leftos\.claude\reference\faa\7110.65/` and `C:\Users\Leftos\.claude\reference\faa\aim/`. Do NOT use web search tools (Exa, WebSearch, WebFetch) to look up 7110.65 or AIM content."

## Recommended Agents

Beyond aviation-sim-expert, use these specialized agents proactively when the task matches:

| Agent | When to use |
|-------|-------------|
| `csharp-developer` | C# implementation: async patterns, nullable annotations, LINQ optimization, collection expressions, pattern matching. Use for non-trivial C# code in Yaat.Sim or Yaat.Client. |
| `code-reviewer` | Before committing significant changes. Catches architecture issues, security problems, and missed edge cases. |
| `debugger` | Diagnosing runtime failures, SignalR connection issues, simulation tick bugs, phase state machine problems. |
| `test-automator` | Building test fixtures for command parsing, phase transitions, flight physics, geo math. |
| `refactoring-specialist` | Restructuring code while preserving behavior — e.g., extracting from CommandDispatcher, simplifying phase logic. |
| `architect-reviewer` | Evaluating design decisions: new phase types, command queue changes, DTO shape changes, Yaat.Sim API surface. |
| `performance-engineer` | Profiling simulation tick performance, SignalR broadcast throughput, ground conflict detection scaling. |
| `websocket-engineer` | SignalR connection lifecycle, reconnection logic, CRC raw WebSocket protocol, real-time broadcast patterns. |
| `game-developer` | Simulation loop design, tick-based state updates, real-time entity management — the sim engine is structurally a game loop. |
| `documentation-engineer` | USER_GUIDE.md updates, scenario format documentation, command reference docs. |

## User Guide

`USER_GUIDE.md` documents all user-facing features: commands, UI controls, keyboard shortcuts, etc. **Before each commit that adds, changes, or removes user-facing behavior, update USER_GUIDE.md to reflect the current state.** Keep it accurate — don't document features that aren't implemented, and remove documentation for features that are removed.

## Error Handling

Never swallow errors silently. No empty catch blocks without logging, no early returns on error without logging. At minimum, log the exception with `AppLog` (client) or `ILogger` (Yaat.Sim callers) — even if the intent is to prevent a crash. If you catch an exception, log it.

## Commits

Prefix subject with a ≤4-char type tag: `fix:` `feat:` `add:` `docs:` `ref:` `test:` `ci:` `dep:` `chore:` etc. Imperative mood, ≤72 char subject line.

## Memory Updates

When an Explore agent's findings reveal core architectural information — file responsibilities, key type relationships, data flow patterns, invariants, or conventions — distill the findings into the auto-memory files (`C:\Users\Leftos\.claude\projects\X--dev-yaat\memory\`). This avoids repeating the same exploration in future sessions. Only record stable facts confirmed by the code, not speculative conclusions from a single file.

## Milestone Roadmap

The project follows milestones defined in `docs/plans/main-plan.md`. M0 and M1 are complete; M2 (tower operations) is next. Pilot AI architecture is designed in `docs/plans/pilot-ai-architecture.md`.
