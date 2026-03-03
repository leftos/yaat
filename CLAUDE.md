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
  CommandScheme.cs              # CanonicalCommandType → CommandPattern; Default() unified scheme
  CommandSchemeParser.cs        # Parse/ParseCompound (;/, syntax); concatenation fallback; ToCanonical()
  CommandMetadata.cs            # Static CommandInfo registry per type
  CommandInputController.cs     # Autocomplete (callsign/command/fix/macro), history nav, FixDb binary search
  MacroDefinition.cs            # Macro model: Name, Expansion, ParameterNames (positional $1 or named $hdg)
  MacroExpander.cs              # Static TryExpand: scan-and-replace #NAME args in command text
  FixSuggester.cs               # Fix name suggestions from FixDb
  AddCommandSuggester.cs        # ADD command callsign/model suggestions
  SuggestionItem.cs             # Suggestion display model (text, kind, description)
  ScenarioDifficultyHelper.cs   # Scenario difficulty classification
  VideoMapService.cs            # Video map download/cache/parse
  UserPreferences.cs            # JSON to %LOCALAPPDATA%/yaat/preferences.json (incl. SavedMacro list)

ViewModels/
  MainViewModel.cs              # Root VM; SendCommandAsync pipeline; nav data init
  MainViewModel.Rooms.cs        # Partial: room lifecycle (create/join/leave)
  MainViewModel.Aircraft.cs     # Partial: aircraft management (spawn/delete/update)
  MainViewModel.Scenario.cs     # Partial: scenario load/unload
  GroundViewModel.cs            # Ground view; loads layout, A* pathfinding, commands
  RadarViewModel.cs             # Radar view; video map loading, toggle items, DCB, persistence
  SettingsViewModel.cs          # Alias editing; preset detection
  *Converter.cs                 # IValueConverters for UI bindings (Dock, Pause, SuggestionKindColor)

Views/
  MainWindow.axaml.cs           # Tab layout (DataGrid/Ground/Radar); room bar; pop-out management
  CommandInputView.axaml.cs     # Keyboard: Esc/Up/Down/Tab/Enter for suggestions/history
  DataGridView.axaml.cs         # Aircraft data grid (extracted from MainWindow)
  DataGridWindow.axaml.cs       # Pop-out data grid window
  TerminalPanelView.axaml.cs    # Auto-scroll with user-scroll detection
  TerminalWindow.axaml.cs       # Pop-out terminal (shares MainViewModel)
  SettingsWindow.axaml.cs       # Modal settings (Identity/Scenarios/Macros tabs)
  MacroImportWindow.axaml.cs    # Macro import selection dialog
  WindowGeometryHelper.cs       # Save/restore window position+size

Views/Map/
  MapViewport.cs                # Shared equirectangular projection for map views
  MapCanvasBase.cs              # ICustomDrawOperation base + pan/zoom input handling

Views/Ground/
  GroundView.axaml.cs           # Ground view control with context menus
  GroundViewWindow.axaml.cs     # Pop-out ground window
  GroundCanvas.cs               # SkiaSharp canvas with StyledProperties + hit-testing
  GroundRenderer.cs             # Stateless SkiaSharp ground renderer

Views/Radar/
  RadarView.axaml.cs            # Radar view control with DCB (range, map shortcuts, FIX, LOCK)
  RadarView.ContextMenus.cs     # Partial: context menu handlers
  RadarView.Popups.cs           # Partial: popup menu handlers (MAP, RR)
  RadarViewWindow.axaml.cs      # Pop-out radar window
  RadarCanvas.cs                # SkiaSharp canvas with pan/zoom lock
  RadarRenderer.cs              # Stateless SkiaSharp radar renderer
  VideoMapRenderer.cs           # Video map line/label rendering
  TargetRenderer.cs             # Aircraft target/datablock rendering
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

# Coordination
CoordinationChannel.cs         # Channel config: ListId, Title, SendingTcps, Receivers, Items
CoordinationItem.cs            # Single coordination entry: status lifecycle, expiry, origin TCP
StarsCoordinationStatus.cs     # Enum: Unsent→Unacknowledged→Acknowledged→Recalled→Expiry→Void

# Commands/
Commands/CanonicalCommandType.cs    # Enum of every command type
Commands/ParsedCommand.cs           # Discriminated union records; CompoundCommand/ParsedBlock/BlockCondition
Commands/CommandDispatcher.cs       # Static: DispatchCompound (phase interaction), ApplyCommand, TryTaxi
Commands/CommandDescriber.cs        # Static: DescribeCommand, DescribeNatural, classification helpers
Commands/AltitudeResolver.cs        # Plain int or AGL format → feet MSL
Commands/RouteChainer.cs            # After DCT to on-route fix, appends remaining route fixes
Commands/ApproachCommandHandler.cs  # Approach clearance logic (CAPP/JAPP/PTAC/CAPPSI/JAPPSI)
Commands/DepartureClearanceHandler.cs  # Departure clearance command logic
Commands/GroundCommandHandler.cs    # Ground operation command logic (taxi, pushback, hold short)
Commands/PatternCommandHandler.cs   # Pattern operation command logic (extend, rock wings, etc.)

# Phases/ — clearance-gated behavior
Phases/Phase.cs                # Abstract: OnStart/OnTick/OnEnd, CanAcceptCommand→CommandAcceptance
Phases/PhaseList.cs            # Mutable list: AssignedRunway, TaxiRoute, LandingClearance, ActiveApproach, mutations
Phases/PhaseRunner.cs          # Static lifecycle: start→tick→advance; auto-appends exit/pattern phases
Phases/PhaseContext.cs         # Readonly tick context
Phases/PhaseStatus.cs          # Enum: phase lifecycle status
Phases/CommandAcceptance.cs    # Enum: Allowed, Rejected, ClearsPhase
Phases/ClearanceRequirement.cs # Clearance requirement definitions
Phases/ExitPreference.cs       # ExitSide enum + ExitPreference class for exit commands
Phases/ClearanceType.cs        # Enum: LineUpAndWait, ClearedForTakeoff/Land/Option/TouchAndGo/StopAndGo, RunwayCrossing
Phases/RunwayInfo.cs           # Runway geometry
Phases/GlideSlopeGeometry.cs   # Altitude/descent rate calculations (3° default)
Phases/PatternGeometry.cs      # 7 pattern waypoints from RunwayInfo + category + direction
Phases/PatternBuilder.cs       # BuildCircuit, BuildNextCircuit, UpdateWaypoints

# Phases/Tower/
LineUpPhase.cs                 # Taxi from hold-short to runway centerline + align heading
LinedUpAndWaitingPhase.cs      # Hold at threshold; await ClearedForTakeoff
TakeoffPhase.cs                # Ground roll→Vr→400ft AGL
InitialClimbPhase.cs           # Climb to 1500ft AGL or assigned
FinalApproachPhase.cs          # Glideslope; auto-go-around at 0.5nm; illegal intercept check (§5-9-1)
LandingPhase.cs                # Flare→touchdown→rollout to 20kts
GoAroundPhase.cs               # TOGA, runway heading, climb 2000ft AGL (pattern alt for VFR/pattern traffic)
TouchAndGoPhase.cs / StopAndGoPhase.cs / LowApproachPhase.cs
HoldAtFixPhase.cs / HoldPresentPositionPhase.cs

# Phases/Approach/
ApproachNavigationPhase.cs     # Navigate through CIFP fix sequence (IAF→IF→FAF) with alt/speed restrictions
InterceptCoursePhase.cs        # Fly current heading until intercepting final approach course
HoldingPatternPhase.cs         # AIM 5-3-8 holding with entry determination; MaxCircuits for hold-in-lieu
ApproachClearance.cs           # Record on PhaseList storing active approach state

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
Data/IApproachLookup.cs        # Interface: GetApproach, GetApproaches, ResolveApproachId
Data/ApproachDatabase.cs       # IApproachLookup impl; lazy CIFP per-airport parsing; shorthand resolution
Data/ApproachGateDatabase.cs   # Static: min intercept distances from CIFP (§5-9-1)
Data/VideoMapMetadata.cs       # Video map metadata model
Data/VideoMapData.cs           # Video map data structures (lines, labels, filters)
Data/VideoMapParser.cs         # GeoJSON → VideoMapData

# Data/Airport/
IAirportGroundData.cs          # Interface: GetLayout(airportId) → AirportGroundLayout?
AirportGroundLayout.cs         # Graph: Nodes + Edges (GroundNode, GroundEdge); FindNearestExit, GetRunwayHoldShortNodes
RunwayIdentifier.cs            # Struct: runway designator parsing/matching
TaxiRoute.cs                   # Resolved path: Segments + HoldShortPoints + completion
TaxiPathfinder.cs              # ResolveExplicitPath, FindRoute (A*), variant inference
TaxiVariantResolver.cs         # Variant path resolution (e.g., A vs A1)
TaxiwayGraphBuilder.cs         # Graph construction from GeoJSON nodes/edges
GeoJsonParser.cs               # GeoJSON→layout; DetectRunwayCrossings via SplitEdgeAtNode
CoordinateIndex.cs             # Spatial index for coordinate-based lookups
RunwayCrossingDetector.cs      # Detect taxiway/runway intersections
HoldShortAnnotator.cs          # Annotate hold-short points on taxi routes

# Data/Vnas/
VnasDataService.cs             # Downloads NavData protobuf + specs; serial-based cache
AiracCycle.cs                  # AIRAC cycle calculator (epoch Jan 23 2025, 28-day)
VnasConfig.cs                  # Config API DTO
CacheManifest.cs               # Cache manifest tracking serials
AircraftSpecEntry.cs           # VNAS aircraft specs model
AircraftCwtEntry.cs            # VNAS aircraft CWT model
CifpDataService.cs             # FAA CIFP zip download/extract per AIRAC cycle
CifpParser.cs                  # ARINC 424 parser: FAF fixes, terminal waypoints, full approach procedures
CifpModels.cs                  # CIFP data models: CifpApproachProcedure, CifpLeg, CifpTransition, etc.

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
    CrcClientState.Session.cs  # Partial: session-related state (StartSession, ActivateSession)
    CrcClientState.Stars.cs    # Partial: STARS display-related state (consolidation, datablock format)
    CrcClientManager.cs        # Client registry; BroadcastAsync fan-out
    NegotiateHandler.cs        # POST /hubs/client/negotiate; JWT extraction → CrcNegotiateTokenStore
    CrcNegotiateTokenStore.cs  # ConcurrentDictionary token→CID for CRC room resolution
    ApiStubHandler.cs          # GET/POST /api/* → [] (CRC startup probes)

  Simulation/
    TrainingRoom.cs            # Room state: Members, World, ActiveScenario, Weather, Engine, GroupName
    TrainingRoomManager.cs     # Room registry + client→room + CID→room mapping + admin tracking
    RoomEngine.cs              # Per-room facade: tick, commands, scenario, broadcast
    RoomEngineFactory.cs       # Creates RoomEngine with shared singleton deps
    SimulationHostedService.cs # Thin orchestrator: 1s tick loop iterating rooms
    TickProcessor.cs           # Stateless tick logic (physics, spawns, triggers, auto-accept, coordination timers)
    TrackCommandHandler.cs     # Stateless track command logic (HO, ACCEPT, DROP, etc.)
    CoordinationCommandHandler.cs # Stateless coordination logic (RD, RDH, RDR, RDACK, RDAUTO)
    ScenarioLifecycleService.cs # Scenario load/unload/spawn/generator logic
    ScenarioState.cs           # Per-room active scenario state: queues, positions, generators, channels
    TrainingBroadcastService.cs # SignalR hub context wrapper for training clients
    CrcBroadcastService.cs     # CRC wire-protocol broadcast; per-room scoped via BroadcastBatch
    CrcVisibilityTracker.cs    # STARS/ASDEX/TowerCab visibility rules
    DtoConverter.cs            # AircraftState → CRC + training DTOs

  Commands/
    CommandParser.cs           # Server-side canonical parsing; IsTrackCommand(), IsCoordinationCommand()
    DepartureCommandParser.cs  # Departure-specific command parsing
    GroundCommandParser.cs     # Ground operation command parsing
    ServerCommands.cs          # Server-only records (DEL, PAUSE, etc.)

  Scenarios/
    ScenarioLoader.cs          # JSON→aircraft (5 position types + Parking); ground detection; generators passthrough
    ScenarioModels.cs          # Deserialization models

  Spawn/SpawnParser.cs         # ADD command → SpawnRequest
  Protocol/                    # CRC binary: VarintCodec, MessageFraming, SignalRMessageParser, SignalRMessageBuilder
  Dtos/
    TrainingDtos.cs            # JSON DTOs for training client communication
    CrcDtos.cs                 # Main CRC binary DTOs (MessagePack)
    CrcDtos.FlightPlan.cs      # Partial: flight plan-related CRC DTOs
    CrcDtos.Session.cs         # Partial: session/StartSession CRC DTOs
    CrcDtos.Stars.cs           # Partial: STARS display-related CRC DTOs
    CrcEnums.cs                # Enums for CRC protocol
    CrcFormatters.cs           # Formatting helpers for CRC DTOs
    TopicFormatter.cs          # Topic subscription/message formatting
  Data/
    AirportGroundDataService.cs  # IAirportGroundData impl; lazy GeoJSON load from ArtccResources/
    ArtccConfig.cs             # VNAS ARTCC config deserialization models (VideoMapConfig, StarsAreaConfig, etc.)
    ArtccConfigService.cs      # Downloads + caches ARTCC config; position/TCP resolution
    ArtccConfigService.Consolidation.cs  # Partial: STARS consolidation hierarchy
    ArtccConfigService.VideoMaps.cs      # Partial: video map extraction
    PositionRegistry.cs        # Thread-safe CRC + RPO position tracking
  Udp/UdpStubServer.cs        # UDP port 6809 stub (CRC keepalive/registration)
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
3. Translated to canonical format → sent to server via `SendCommand(callsign, canonical)`
4. Server builds `CommandQueue` of `CommandBlock`s; `FlightPhysics.UpdateCommandQueue()` checks triggers each tick

**Track commands** (TRACK, DROP, HO, ACCEPT, etc.) bypass CommandDispatcher — RoomEngine routes to TrackCommandHandler, mutating ownership fields. `AS` prefix resolves RPO identity.

**Coordination commands** (RD, RDH, RDR, RDACK, RDAUTO) bypass CommandDispatcher — RoomEngine routes to CoordinationCommandHandler. Coordination channels are loaded from ARTCC config on scenario load. Items auto-expire (5min after ack, 2min warning) and are removed on radar acquisition (TRACK).

### Command Rules

- Match existing ATCTrainer/VICE names where applicable. See `docs/command-aliases/reference.md` for the full comparison (🟢 marks YAAT-only commands). Source JSON extracts and the build script that produces them are in the same directory.
- **Completeness (MANDATORY):** Every `CanonicalCommandType` must exist in: `CommandScheme.Default()`, `CommandMetadata.AllCommands`. Tests in `tests/Yaat.Client.Tests/CommandSchemeCompletenessTests.cs` enforce this.

### Communication Flow

```
YAAT Client ──SignalR JSON──> yaat-server <──SignalR+MessagePack── CRC
  /hubs/training                              /hubs/client
```

### SignalR Hub API

**Client→Server (room lifecycle):** `CreateRoom(cid, initials, artccId)`, `JoinRoom(roomId, cid, initials, artccId)`, `LeaveRoom`, `GetActiveRooms`

**Client→Server (scenario/sim):** `LoadScenario`, `UnloadScenarioAircraft`, `ConfirmUnloadScenario`, `SendCommand`, `SpawnAircraft`, `DeleteAircraft`, `PauseSimulation`, `ResumeSimulation`, `SetSimRate`, `SetAutoAcceptDelay`, `SetAutoDeleteMode`, `SendChat`, `LoadWeather(weatherJson) → CommandResultDto`, `ClearWeather()`

**Client→Server (data queries):** `GetAirportGroundLayout(airportId)`, `GetFacilityVideoMaps(artccId, facilityId)`, `GetFacilityVideoMapsForArtcc(artccId)`

**Client→Server (CRC management):** `GetCrcLobbyClients`, `PullCrcClient(clientId)`, `KickCrcClient(clientId)`, `GetCrcRoomMembers`

**Client→Server (admin):** `AdminAuthenticate`, `AdminGetScenarios`, `AdminSetScenarioFilter`, `Heartbeat`

**Server→Client:** `AircraftUpdated`, `AircraftSpawned`, `AircraftDeleted`, `SimulationStateChanged`, `TerminalBroadcast`, `RoomMemberChanged`, `CrcLobbyChanged`, `CrcRoomMembersChanged`, `WeatherChanged(WeatherChangedDto)`

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

## Reference Docs

- `docs/atctrainer-scenario-examples/` — 9 real ATCTrainer scenario JSON files (all "Easy" difficulty). Use as reference for scenario format when building scenario loading/parsing.
- `docs/crc/` — CRC (Consolidated Radar Client) controller manual pages scraped from vNAS docs. Covers overview, STARS terminal radar, Tower Cab mode, and vStrips. Use as reference for understanding CRC display behavior and terminology when implementing CRC protocol features.
- `docs/vnas-artcc-config-examples/` — Real vNAS ARTCC configuration JSON files (e.g., `zoa.json`). Contains facility hierarchy, positions, TCPs, STARS lists with coordination channels, ASDEX/TowerCab configs. Use as reference for parsing ARTCC API responses and understanding coordination channel adaptation.

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

- **Unreleased software**: YAAT has no public release and no external users. Do not add backwards-compatibility shims, migration paths, deprecated aliases, or dual config formats. There is nothing to stay compatible with. Delete and replace freely.
- **User Guide**: Update `USER_GUIDE.md` before committing user-facing changes.
- **No newlines in text strings**: Never split literal text across lines in `.axaml` or `.cs` files. The indentation whitespace becomes visible at runtime (huge gaps in UI text). Keep `Text="..."`, `Content="..."`, and interpolated strings on one line, even if long.
- **Error Handling**: Never swallow exceptions. Log with `AppLog` (client) or `ILogger` (Sim).
- **Line width**: 150 characters, not 80 or 120. CSharpier is configured accordingly.
- **Pre-commit formatting**: Run `dotnet format style`, `dotnet format analyzers`, then `dotnet csharpier format .` before each commit, followed by a final `dotnet build` to verify nothing broke. Do NOT run bare `dotnet format` (its whitespace rules fight with CSharpier).
- **Commits**: `fix:`/`feat:`/`add:`/`docs:`/`ref:`/`test:` etc. Imperative, ≤72 chars.
- **Memory Updates**: Distill Explore agent findings into auto-memory at `C:\Users\Leftos\.claude\projects\X--dev-yaat\memory\`.
- **Milestone Roadmap**: See `docs/plans/main-plan.md`. M0/M1 complete; M2 (tower ops) next.
