# CLAUDE.md

## Project Overview

YAAT (Yet Another ATC Trainer) — instructor/RPO desktop client for ATC training. Connects to [yaat-server](https://github.com/leftos/yaat-server) via SignalR; server feeds CRC via SignalR+MessagePack.

## Build & Run

```bash
dotnet build                            # Build entire solution
dotnet run --project src/Yaat.Client    # Run client (needs yaat-server at localhost:5000)
```

.NET 8 SDK required. Solution uses `.slnx` format (`yaat.slnx`).

## Logs

Read log files first before speculating about runtime errors:
- **Client**: `%LOCALAPPDATA%/yaat/yaat-client.log`
- **Server**: `src/Yaat.Server/bin/Debug/net10.0/yaat-server.log` (relative to yaat-server repo root)

## Architecture

> **Keep up-to-date.** Before each commit, update this section to reflect structural changes.

Three projects across two repos. **Yaat.Sim** is shared by both Yaat.Client and yaat-server.

**Yaat.Sim owns all simulation/aviation logic** — physics, phases, pattern geometry, performance constants, command dispatch, command queue. Server is a thin comms layer.

### Yaat.Client — Avalonia desktop app (`src/Yaat.Client/`)

```
Logging/
  AppLog.cs                     # Static logger factory
  FileLoggerProvider.cs         # Writes to %LOCALAPPDATA%/yaat/yaat-client.log

Models/
  AircraftModel.cs              # ObservableObject wrapping AircraftDto; computed displays; FromDto/UpdateFromDto
  TerminalEntry.cs              # Terminal/radio log entry (Kind: Command/Response/System/Say)

Services/
  ServerConnection.cs           # SignalR client to /hubs/training (JSON); inline DTOs
  CommandScheme.cs              # CanonicalCommandType → CommandPattern; AtcTrainer()/Vice() factories
  AtcTrainerPreset.cs           # SpaceSeparated: FH 270, CM 240, DM 50, SPD 250
  VicePreset.cs                 # Concatenated: H270, C240, D50, S250
  CommandSchemeParser.cs        # Parse/ParseCompound (;/, syntax); ToCanonical() → ATCTrainer format
  CommandMetadata.cs            # Static CommandInfo registry per type
  CommandInputController.cs     # Autocomplete (callsign/command/fix), history nav, FixDb binary search
  UserPreferences.cs            # JSON to %LOCALAPPDATA%/yaat/preferences.json

ViewModels/
  MainViewModel.cs              # Root VM; SendCommandAsync pipeline; nav data init; distance reference
  SettingsViewModel.cs          # Alias editing; preset detection
  *Converter.cs                 # IValueConverters for UI bindings

Views/
  MainWindow.axaml.cs           # DataGrid column/sort persistence; distance reference flyout
  CommandInputView.axaml.cs     # Keyboard: Esc/Up/Down/Tab/Enter for suggestions/history
  TerminalPanelView.axaml.cs    # Auto-scroll with user-scroll detection
  TerminalWindow.axaml.cs       # Pop-out terminal (shares MainViewModel)
  SettingsWindow.axaml.cs       # Modal settings
  WindowGeometryHelper.cs       # Save/restore window position+size
```

### Yaat.Sim — Shared simulation library (`src/Yaat.Sim/`)

No UI deps. Deps: Google.Protobuf, Microsoft.Extensions.Logging.Abstractions.

```
# Core
AircraftState.cs               # Mutable entity: position, flight plan, identity, control, track ops
ControlTargets.cs              # Autopilot targets: heading, altitude, speed, NavigationRoute
FlightPhysics.cs               # Static 6-step Update: navigation→heading→altitude→speed→position→queue
GeoMath.cs                     # Static: DistanceNm (haversine), BearingTo, TurnHeadingToward
SimulationWorld.cs             # Thread-safe aircraft collection; GetSnapshot, Tick, DrainWarnings
CommandQueue.cs                # CommandBlock (trigger + closure + TrackedCommands), BlockTrigger
AircraftCategory.cs            # Enum + AircraftCategorization (static Init from AircraftSpecs.json)
                               # CategoryPerformance: all aviation constants (validated by aviation-sim-expert)
GroundConflictDetector.cs      # Static pairwise ground proximity → max-speed overrides

# Track operations
TrackOwner.cs                  # Record: Callsign, FacilityId, Subset, SectorId, OwnerType
TrackOwnerType.cs              # Enum: Other, Eram, Stars, Caats, Atop
Tcp.cs                         # Record: Subset, SectorId, Id, ParentTcpId
StarsPointout.cs / StarsPointoutStatus.cs  # Pointout state

# Commands/
Commands/CanonicalCommandType.cs  # Enum of every command type
Commands/ParsedCommand.cs      # Discriminated union records; CompoundCommand/ParsedBlock/BlockCondition
Commands/CommandDispatcher.cs  # Static: DispatchCompound (phase interaction), ApplyCommand, TryTaxi
Commands/CommandDescriber.cs   # Static: DescribeCommand, DescribeNatural, classification helpers
Commands/AltitudeResolver.cs   # Plain int or AGL format → feet MSL
Commands/RouteChainer.cs       # After DCT to on-route fix, appends remaining route fixes

# Phases/ — clearance-gated behavior
Phases/Phase.cs                # Abstract: OnStart/OnTick/OnEnd, CanAcceptCommand→CommandAcceptance
Phases/PhaseList.cs            # Mutable list: AssignedRunway, TaxiRoute, LandingClearance, mutations
Phases/PhaseRunner.cs          # Static lifecycle: start→tick→advance; auto-appends exit/pattern phases
Phases/PhaseContext.cs         # Readonly tick context
Phases/CommandAcceptance.cs    # Enum: Allowed, Rejected, ClearsPhase
Phases/ClearanceType.cs        # Enum: LineUpAndWait, ClearedForTakeoff/Land/Option/TouchAndGo/StopAndGo, RunwayCrossing
Phases/RunwayInfo.cs           # Runway geometry
Phases/GlideSlopeGeometry.cs   # Altitude/descent rate calculations (3° default)
Phases/PatternGeometry.cs      # 7 pattern waypoints from RunwayInfo + category + direction
Phases/PatternBuilder.cs       # BuildCircuit, BuildNextCircuit, UpdateWaypoints

# Phases/Tower/
LinedUpAndWaitingPhase.cs      # Hold at threshold; await ClearedForTakeoff
TakeoffPhase.cs                # Ground roll→Vr→400ft AGL
InitialClimbPhase.cs           # Climb to 1500ft AGL or assigned
FinalApproachPhase.cs          # Glideslope; auto-go-around at 0.5nm; illegal intercept check (§5-9-1)
LandingPhase.cs                # Flare→touchdown→rollout to 20kts
GoAroundPhase.cs               # TOGA, runway heading, climb 1500ft AGL
TouchAndGoPhase.cs / StopAndGoPhase.cs / LowApproachPhase.cs
HoldAtFixPhase.cs / HoldPresentPositionPhase.cs

# Phases/Pattern/
UpwindPhase / CrosswindPhase / DownwindPhase / BasePhase / MidfieldCrossingPhase

# Phases/Ground/
AtParkingPhase / PushbackPhase / TaxiingPhase / HoldingShortPhase
CrossingRunwayPhase / RunwayExitPhase / HoldingAfterExitPhase / FollowingPhase

# Data/
Data/IFixLookup.cs             # Interface: GetFixPosition, GetAirportElevation
Data/IRunwayLookup.cs          # Interface: GetRunway, GetRunways
Data/FixDatabase.cs            # Implements both; VNAS protobuf + custom fixes; AllFixNames, ExpandRoute
Data/CustomFixDefinition.cs / CustomFixLoader.cs  # Custom fix JSON loading
Data/FrdResolver.cs            # Fix-Radial-Distance → lat/lon

# Data/Airport/
IAirportGroundData.cs          # GetLayout(airportId) → AirportGroundLayout?
AirportGroundLayout.cs         # Graph: Nodes + Edges; FindNearestExit, GetRunwayHoldShortNodes
GroundNode.cs / GroundEdge.cs  # Graph primitives
TaxiRoute.cs                   # Resolved path: Segments + HoldShortPoints + completion
TaxiPathfinder.cs              # ResolveExplicitPath, FindRoute (A*), variant inference
GeoJsonParser.cs               # GeoJSON→layout; DetectRunwayCrossings via SplitEdgeAtNode

# Data/Vnas/
VnasDataService.cs             # Downloads NavData protobuf + specs; serial-based cache
AiracCycle.cs                  # AIRAC cycle calculator (epoch Jan 23 2025, 28-day)
VnasConfig.cs                  # Config API DTO
CifpDataService.cs             # FAA CIFP zip download/extract per AIRAC cycle
CifpParser.cs                  # ARINC 424 parser: FAF fixes + terminal waypoints
Data/ApproachGateDatabase.cs   # Static: min intercept distances from CIFP (§5-9-1)

# Scenarios/
AircraftInitializer.cs         # InitializeOnRunway/AtParking/OnFinal → PhaseInitResult
AircraftGenerator.cs           # SpawnRequest → AircraftState
SpawnRequest.cs                # Spawn descriptor

Proto/nav_data.proto           # Compiled by Grpc.Tools → NavDataSet
```

### yaat-server — ASP.NET Core server (`X:\dev\yaat-server\`)

Separate repo. References Yaat.Sim via sibling project ref. Provides: SignalR comms, CRC protocol, training rooms, scenario loading, broadcast fan-out.

```
src/Yaat.Server/
  Program.cs                   # DI setup, VNAS/CIFP init, route mapping, AdminPassword validation
  YaatOptions.cs               # IOptions: AdminPassword, ArtccResourcesPath

  Hubs/
    TrainingHub.cs             # /hubs/training (JSON); room lifecycle + delegates to RoomEngine
    CrcWebSocketHandler.cs     # Raw WebSocket /hubs/client for CRC; resolves room via JWT CID
    CrcClientState.cs          # Per-CRC state machine; holds RoomEngine ref; topic subscriptions
    CrcClientManager.cs        # Client registry; BroadcastAsync fan-out
    NegotiateHandler.cs        # POST /hubs/client/negotiate; JWT extraction → CrcNegotiateTokenStore
    CrcNegotiateTokenStore.cs  # ConcurrentDictionary token→CID for CRC room resolution
    ApiStubHandler.cs          # GET/POST /api/* → [] (CRC startup probes)

  Simulation/
    TrainingRoom.cs            # Room state: Members, World, ActiveScenario, Engine, GroupName
    TrainingRoomManager.cs     # Room registry + client→room + CID→room mapping + admin tracking
    RoomEngine.cs              # Per-room facade: tick, commands, scenario, broadcast
    RoomEngineFactory.cs       # Creates RoomEngine with shared singleton deps
    SimulationHostedService.cs # Thin orchestrator: 1s tick loop iterating rooms
    TickProcessor.cs           # Stateless tick logic (physics, spawns, triggers, auto-accept)
    TrackCommandHandler.cs     # Stateless track command logic (HO, ACCEPT, DROP, etc.)
    ScenarioLifecycleService.cs # Scenario load/unload/spawn/generator logic
    TrainingBroadcastService.cs # SignalR hub context wrapper for training clients
    CrcBroadcastService.cs     # CRC wire-protocol broadcast; per-room scoped via BroadcastBatch
    CrcVisibilityTracker.cs    # STARS/ASDEX/TowerCab visibility rules
    DtoConverter.cs            # AircraftState → CRC + training DTOs

  Commands/
    CommandParser.cs           # Server-side canonical parsing; IsTrackCommand()
    ServerCommands.cs          # Server-only records (DEL, PAUSE, etc.)

  Scenarios/
    ScenarioLoader.cs          # JSON→aircraft (5 position types + Parking); ground detection; generators passthrough
    ScenarioModels.cs          # Deserialization models

  Spawn/SpawnParser.cs         # ADD command → SpawnRequest
  Protocol/                    # CRC binary: VarintCodec, MessageFraming, SignalR MessagePack parser/builder
  Dtos/                        # TrainingDtos (JSON), CrcDtos (MessagePack), CrcEnums, TopicFormatter
  Data/
    AirportGroundDataService.cs  # IAirportGroundData impl; lazy GeoJSON load from ArtccResources/
    ArtccConfig.cs / ArtccConfigService.cs  # VNAS ARTCC config; position/TCP resolution
    PositionRegistry.cs        # Thread-safe CRC + RPO position tracking
  Udp/UdpStubServer.cs        # UDP port 6809 stub
  Logging/FileLoggerProvider.cs
```

### Key Patterns

- **SignalR**: JSON protocol on `/hubs/training`; DTOs as records in `ServerConnection.cs`
- **MVVM**: `[ObservableProperty]`/`[RelayCommand]` from CommunityToolkit.Mvvm; `_camelCase` fields → `PascalCase` props
- **Threading**: SignalR callbacks on background thread; marshal to UI via `Dispatcher.UIThread.Post()`
- **No DI**: `MainWindow` creates `MainViewModel` directly
- **Snapshots**: `SimulationWorld.GetSnapshot()` returns shallow copy; treat as read-only

### Command Pipeline

1. User input → `MainViewModel.SendCommandAsync()` resolves callsign via partial match
2. `CommandSchemeParser.ParseCompound()`: `;` = sequential blocks, `,` = parallel, `LV`/`AT` = conditions
3. Translated to canonical ATCTrainer format → sent to server via `SendCommand(callsign, canonical)`
4. Server builds `CommandQueue` of `CommandBlock`s; `FlightPhysics.UpdateCommandQueue()` checks triggers each tick

**Track commands** (TRACK, DROP, HO, ACCEPT, etc.) bypass CommandDispatcher — RoomEngine routes to TrackCommandHandler, mutating ownership fields. `AS` prefix resolves RPO identity.

### Command Rules

- Match existing ATCTrainer/VICE names. See `docs/command-aliases-reference.md`.
- **Completeness (MANDATORY):** Every `CanonicalCommandType` must exist in: `CommandScheme.AtcTrainer()`, `CommandScheme.Vice()`, `CommandMetadata.AllCommands`. Tests in `tests/Yaat.Client.Tests/CommandSchemeCompletenessTests.cs` enforce this.

### Communication Flow

```
YAAT Client ──SignalR JSON──> yaat-server <──SignalR+MessagePack── CRC
  /hubs/training                              /hubs/client
```

### SignalR Hub API

**Client→Server (room lifecycle):** `CreateRoom(cid, initials, artccId)`, `JoinRoom(roomId, cid, initials, artccId)`, `LeaveRoom`, `GetActiveRooms`

**Client→Server (scenario/sim):** `LoadScenario`, `UnloadScenarioAircraft`, `ConfirmUnloadScenario`, `SendCommand`, `SpawnAircraft`, `DeleteAircraft`, `PauseSimulation`, `ResumeSimulation`, `SetSimRate`, `SetAutoAcceptDelay`, `SetAutoDeleteMode`, `SendChat`

**Client→Server (admin):** `AdminAuthenticate`, `AdminGetScenarios`, `AdminSetScenarioFilter`, `Heartbeat`

**Server→Client:** `AircraftUpdated`, `AircraftSpawned`, `AircraftDeleted`, `SimulationStateChanged`, `TerminalBroadcast`, `RoomMemberChanged`

## Tech Stack

- .NET 10, C# with nullable enabled, implicit usings
- Avalonia UI 11.2.5 (Fluent dark) + DataGrid
- CommunityToolkit.Mvvm 8.4.0
- Microsoft.AspNetCore.SignalR.Client 10.0.3

## Related Repositories

| Repo | Path | Purpose |
|------|------|---------|
| yaat-server | `X:\dev\yaat-server` | ASP.NET Core server, simulation engine, CRC protocol |
| vzoa | `X:\dev\vzoa` | vZOA training files; airport GeoJSON at `training-files/atctrainer-airport-files/` |
| vatsim-server-rs | `X:\dev\vatsim-server-rs` | Rust CRC protocol reference (DTO ordering, varint framing) — **read-only emulation server**, see caveat below |
| lc-trainer | `X:\dev\lc-trainer` | Previous WPF trainer (**NOT trusted** — aviation details need expert review) |

> **vatsim-server-rs is a read-only emulation server.** It was designed to feed CRC with data, not to accept mutations. Many CRC→server methods (CreateFlightPlan, AmendFlightPlan, track operations, etc.) are stubbed with nil-ack responses because the Rust server never needed to implement them. YAAT's server needs full two-way interaction — students and RPOs create flight plans, amend them, drop tracks, etc. Use vatsim-server-rs as reference for wire format, DTO field ordering, and subscription/broadcast patterns, but evaluate feature needs independently. For mutation-capable methods, use the vNAS messaging-master interfaces and data-master models as the authoritative reference instead.

**vNAS source reference** (`X:\dev\towercab-3d-vnas\docs\repos\`):
- **common-master** — `GeoCalc`, `NavCalc`, `GeoPoint`, etc.
- **data-master** — Navigation, scenarios, aircraft specs, facility config models
- **messaging-master** — CRC hub DTOs/commands/topics (definitive reference)

**vNAS APIs:**
- Config: `https://configuration.vnas.vatsim.net/` (serials, URLs, environment endpoints)
- Data: `https://data-api.vnas.vatsim.net/api/artccs/{id}` (full ARTCC facility config)

## Aviation Realism — MANDATORY

**Every feature touching aviation must be reviewed by `aviation-sim-expert`** (via `Agent` with `subagent_type: "aviation-sim-expert"`). This is not optional.

**Use aviation-sim-expert when touching:** flight physics, pilot AI behavior, ATC logic, radio comms, aircraft performance, airspace rules, scenario data, phase transitions, command dispatch affecting aviation, ground operations, conflict detection, trigger conditions, or any automatic aircraft behavior.

**Do not guess aviation details.** Use FAA 7110.65, AIM, ICAO Doc 4444 as authorities.

**Local FAA references — DO NOT web-search:**
- **7110.65**: `C:\Users\Leftos\.claude\reference\faa\7110.65/` (index: `INDEX.md`)
- **AIM**: `C:\Users\Leftos\.claude\reference\faa\aim/` (index: `INDEX.md`)

When invoking aviation-sim-expert, always include:
> "IMPORTANT: The FAA 7110.65 and AIM are available as local markdown files. Read them directly via Read/Grep/Glob at `C:\Users\Leftos\.claude\reference\faa\7110.65/` and `C:\Users\Leftos\.claude\reference\faa\aim/`. Do NOT use web search tools to look up 7110.65 or AIM content."

## Recommended Agents

| Agent | When to use |
|-------|-------------|
| `aviation-sim-expert` | **Any aviation logic** (see above) |
| `csharp-developer` | Non-trivial C# in Yaat.Sim or Yaat.Client |
| `code-reviewer` | Before committing significant changes |
| `debugger` | Runtime failures, SignalR issues, phase state bugs |
| `test-automator` | Test fixtures for commands, phases, physics, geo |
| `refactoring-specialist` | Restructuring while preserving behavior |
| `architect-reviewer` | Design decisions: phases, command queue, DTOs, API surface |
| `performance-engineer` | Tick performance, broadcast throughput, conflict detection |
| `websocket-engineer` | SignalR lifecycle, CRC WebSocket protocol |
| `game-developer` | Sim loop design, tick-based updates |
| `documentation-engineer` | USER_GUIDE.md, scenario format, command reference |

## Rules

- **User Guide**: Update `USER_GUIDE.md` before committing user-facing changes.
- **Error Handling**: Never swallow exceptions. Log with `AppLog` (client) or `ILogger` (Sim).
- **Commits**: `fix:`/`feat:`/`add:`/`docs:`/`ref:`/`test:` etc. Imperative, ≤72 chars.
- **Memory Updates**: Distill Explore agent findings into auto-memory at `C:\Users\Leftos\.claude\projects\X--dev-yaat\memory\`.
- **Milestone Roadmap**: See `docs/plans/main-plan.md`. M0/M1 complete; M2 (tower ops) next.
