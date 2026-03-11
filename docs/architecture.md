# Architecture — File Tree

> **Read this file when you need to locate specific files or understand project structure.**
> CLAUDE.md contains a summary; this file has the full annotated tree.

Three projects across two repos. **Yaat.Sim** is shared by both Yaat.Client and yaat-server.

**Yaat.Sim owns all simulation/aviation logic** — physics, phases, pattern geometry, performance constants, command dispatch, command queue. Server is a thin comms layer.

## Yaat.Client — Avalonia desktop app (`src/Yaat.Client/`)

```
Logging/
  AppLog.cs                     # Static logger factory
  FileLoggerProvider.cs         # Writes to %LOCALAPPDATA%/yaat/yaat-client.log

Models/
  AircraftModel.cs              # ObservableObject wrapping AircraftDto; computed displays; FromDto/UpdateFromDto
  TerminalEntry.cs              # Terminal/radio log entry (Kind: Command/Response/System/Say)

Services/
  ServerConnection.cs           # SignalR client to /hubs/training (JSON); inline DTOs
  CommandScheme.cs              # CanonicalCommandType → CommandPattern (aliases only); Default() from registry
  CommandSchemeParser.cs        # Parse/ParseCompound (;/, syntax); concatenation fallback; ToCanonical()
  CommandRegistry.cs            # Single source of truth: CommandDefinition per type (label, category, aliases, overloads, modifiers)
  CommandInputController.cs     # Autocomplete (callsign/command/fix/macro), history nav, signature help, FixDb binary search
  CommandSignature.cs           # Records: CommandParameter, CommandSignature, CommandSignatureSet, SignaturePart; FromDefinition factory
  SignatureHelpState.cs         # Observable state for signature help tooltip (overload nav, active param, dedup)
  MacroDefinition.cs            # Macro model: Name, Expansion, ParameterNames (positional $1 or named $hdg)
  MacroExpander.cs              # Static TryExpand: scan-and-replace #NAME args in command text
  TrainingDataService.cs         # Fetches scenarios/weather from vNAS data API (data-api.vnas.vatsim.net)
  FixSuggester.cs               # Fix name suggestions from FixDb
  AddCommandSuggester.cs        # ADD command callsign/model suggestions
  SuggestionItem.cs             # Suggestion display model (text, kind, description)
  ScenarioDifficultyHelper.cs   # Scenario difficulty classification
  VideoMapService.cs            # Video map download/cache/parse
  LiveWeatherService.cs         # Fetches live METARs + FD winds from aviationweather.gov → WeatherProfile
  ArtccAirportResolver.cs       # Fetches vNAS ARTCC config → underlying airport IDs (cached)
  FdRegionMapping.cs            # Static ARTCC → FD region code mapping
  UserPreferences.cs            # JSON to %LOCALAPPDATA%/yaat/preferences.json (incl. SavedMacro list)
  MenuGroup.cs                  # Enum of context menu groups (Heading, Altitude, Speed, Tower, etc.)
  ContextMenuProfile.cs         # Record: Primary/Secondary/Hidden menu groups for a phase
  ContextMenuProfileService.cs  # Static: maps phase name + isOnGround → ContextMenuProfile

ViewModels/
  MainViewModel.cs              # Root VM; SendCommandAsync pipeline; nav data init
  MainViewModel.Rooms.cs        # Partial: room lifecycle (create/join/leave), aircraft assignments
  MainViewModel.Aircraft.cs     # Partial: aircraft management (spawn/delete/update)
  MainViewModel.Scenario.cs     # Partial: scenario load/unload
  MainViewModel.Weather.cs      # Partial: weather load/clear commands + WeatherChanged handler
  MainViewModel.Favorites.cs    # Partial: favorite commands (quick-access bar, scenario-scoped)
  GroundViewModel.cs            # Ground view; loads layout, A* pathfinding, commands
  RadarViewModel.cs             # Radar view; video map loading, toggle items, DCB, persistence
  SettingsViewModel.cs          # Alias editing; preset detection
  *Converter.cs                 # IValueConverters for UI bindings (Dock, Pause, SuggestionKindColor, SignatureHelp)

Views/
  MainWindow.axaml.cs           # Tab layout (DataGrid/Ground/Radar); room bar; pop-out management
  CommandInputView.axaml.cs     # Keyboard: Esc/Up/Down/Tab/Enter for suggestions/history
  FavoritesBarView.axaml.cs     # Favorite command buttons bar (click/ctrl+click/right-click)
  DataGridView.axaml.cs         # Aircraft data grid (extracted from MainWindow)
  DataGridWindow.axaml.cs       # Pop-out data grid window
  TerminalPanelView.axaml.cs    # Auto-scroll with user-scroll detection
  TerminalWindow.axaml.cs       # Pop-out terminal (shares MainViewModel)
  SettingsWindow.axaml.cs       # Modal settings (Identity/Scenarios/Macros tabs)
  MacroImportWindow.axaml.cs    # Macro import selection dialog
  LoadWeatherWindow.axaml.cs    # Weather profile picker modal (folder scan, name + layer count)
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

## Yaat.Sim — Shared simulation library (`src/Yaat.Sim/`)

No UI deps. Deps: Google.Protobuf, Microsoft.Extensions.Logging.Abstractions.

```
# Core
AircraftState.cs               # Mutable entity: position, flight plan, identity, control, track ops
                               # IndicatedAirspeed (IAS, primary speed state), Track (ground track = heading + wind drift)
                               # BankAngle (degrees, +right/-left, computed by FlightPhysics.UpdateHeading from TAS + turn rate)
                               # ActiveSidId/ActiveStarId, SidViaMode/StarViaMode, SidViaCeiling/StarViaFloor
                               # HasReportedFieldInSight, HasReportedTrafficInSight, FollowingCallsign (visual approach)
                               # PatternSizeOverrideNm (override for pattern downwind offset distance)
                               # IsExpediting (1.5x climb/descent rate multiplier, cleared at altitude snap or by NORM/CM/DM)
ControlTargets.cs              # Autopilot targets: heading, altitude, speed (IAS), NavigationRoute
                               # NavigationTarget: optional AltitudeRestriction + SpeedRestriction (for SID/STAR via mode)
                               # TargetMach: when set, UpdateSpeed recomputes equivalent IAS each tick (Mach hold)
FlightPhysics.cs               # Static 6-step Update: navigation→heading→altitude→speed→position→queue
                               # 14 CFR 91.117: 250 KIAS cap below 10,000 ft in UpdateSpeed() and ApplyFixConstraints()
                               # Wind physics: TAS = IasToTas(IAS, alt); GS/Track derived from TAS + wind vector; WCA applied to nav
                               # ApplyFixConstraints: SID/STAR via-mode constraint enforcement at waypoints
                               # Bank angle: computed in UpdateHeading from atan(TAS × turnRate × coeff); sign follows turn direction
                               # Expedite: IsExpediting → 1.5x climb/descent rate; Mach hold: TargetMach → recompute IAS each tick
GeoMath.cs                     # Static: DistanceNm (haversine), BearingTo, TurnHeadingToward, GenerateArcPoints (RF/AF)
SimLog.cs                      # Static logger factory for Yaat.Sim; Initialize(ILoggerFactory) at startup
SimulationWorld.cs             # Thread-safe aircraft collection; GetSnapshot, Tick, DrainWarnings
                               # WeatherProfile? Weather — passed to FlightPhysics.Update() each tick
CommandQueue.cs                # CommandBlock (trigger + closure + TrackedCommands), BlockTrigger
AircraftCategory.cs            # Enum + AircraftCategorization (static Init from AircraftSpecs.json)
                               # CategoryPerformance: all aviation constants (validated by aviation-sim-expert)
GroundConflictDetector.cs      # Static pairwise ground proximity → max-speed overrides
ConflictAlertDetector.cs       # Static STARS CA detection: 3nm/1000ft thresholds, 5s extrapolation, hysteresis, approach suppression
WeatherProfile.cs              # WeatherProfile + WindLayer; ATCTrainer-compatible JSON; layers sorted by altitude on load
                               # GetWeatherForAirport: cached METAR lookup via MetarInterpolator
WindInterpolator.cs            # Static wind utilities: GetWindAt, GetWindComponents (vector lerp through 0/360), IasToTas (8-point
                               # lookup table), TasToIas, MachToIas (ISA speed-of-sound model), ComputeWindCorrectionAngle
MetarParser.cs                 # Static METAR parsing: station ID, ceiling (BKN/OVC), visibility (SM); ParsedMetar record
MetarInterpolator.cs           # Static: GetWeatherForAirport — exact station match then IDW interpolation within 50nm
WindsAloftParser.cs            # Static: parses FAA FD fixed-width text → StationWinds[]; DecodeWind handles 100+kt, light/variable
MagneticDeclination.cs         # Static: approximate CONUS magnetic declination from lon; TrueToMagnetic conversion
VisualDetection.cs             # Static: CanSeeAirport, CanSeeAirportForRunway, CanSeeTraffic, IsOccludedByBank
                               # Forward hemisphere, visibility, ceiling, bank angle occlusion (7110.65 §7-4-4.c.2), WTG-based traffic range
                               # FL180 gate on airport (visual approach eligibility) but NOT traffic (pilots can see in Class A)
WakeTurbulenceData.cs          # Static: WTG code lookup from AircraftSpecs.json; TrafficDetectionRangeNm by WTG (A=15nm to F=3nm)

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
Commands/CommandDispatcher.cs       # Static: DispatchCompound (phase interaction), ApplyCommand (thin routing switch),
                                    # TryApplyTowerCommand, queue infrastructure, condition conversion, shared utilities
Commands/FlightCommandHandler.cs    # Heading, altitude, speed, squawk, direct-to, warp, wait/say commands
Commands/NavigationCommandHandler.cs # Multi-block navigation: JRADO/JRADI, depart/cross fix, JARR STAR resolution,
                                    # JAWY airway intercept, CVIA/DVIA (DVIA SPD fix), JFAC, holding pattern, RFIS/RTIS, list approaches
Commands/CommandDescriber.cs        # Static: DescribeCommand, DescribeNatural, classification helpers
Commands/AltitudeResolver.cs        # Plain int or AGL format → feet MSL
Commands/RouteChainer.cs            # After DCT to on-route fix, appends remaining route fixes
Commands/ApproachCommandHandler.cs  # Approach clearance logic (CAPP/JAPP/PTAC/CAPPSI/JAPPSI/CVA visual approach); RF/AF arc expansion in BuildApproachFixes
Commands/DepartureClearanceHandler.cs  # Departure clearance + CIFP SID resolution, CancelTakeoff, ClearedTakeoffPresent (CTOPP)
Commands/GroundCommandHandler.cs    # Ground operation command logic (taxi, pushback, hold short)
Commands/PatternCommandHandler.cs   # Pattern operation command logic (extend, rock wings, GoAround, CTL, sequence, etc.)

# Phases/ — clearance-gated behavior
Phases/Phase.cs                # Abstract: OnStart/OnTick/OnEnd, CanAcceptCommand→CommandAcceptance
Phases/PhaseList.cs            # Mutable list: AssignedRunway, TaxiRoute, LandingClearance, ActiveApproach, DepartureClearance, mutations
Phases/PhaseRunner.cs          # Static lifecycle: start→tick→advance; auto-appends exit/pattern phases
Phases/PhaseContext.cs         # Readonly tick context; includes WeatherProfile? Weather for wind-aware phases
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
InitialClimbPhase.cs           # Climb to 1500ft AGL or assigned; activates SID via mode when DepartureSidId set
FinalApproachPhase.cs          # Glideslope; auto-go-around at 0.5nm; illegal intercept check (§5-9-1)
LandingPhase.cs                # Flare→touchdown→rollout to 20kts; LAHSO-aware (kinematic stop before hold-short)
RunwayHoldingPhase.cs          # LAHSO: hold at 0kts on runway after landing; clearance-gated (RunwayCrossing)
GoAroundPhase.cs               # TOGA, runway heading, climb 2000ft AGL (pattern alt for VFR/pattern traffic)
TouchAndGoPhase.cs / StopAndGoPhase.cs / LowApproachPhase.cs
MakeTurnPhase.cs               # 360/270 turn tracking (cumulative degrees, exit heading); clones pattern phase for 360s
STurnPhase.cs                  # S-turn phase: alternating 30° deviations from final heading for spacing
HoldAtFixPhase.cs / HoldPresentPositionPhase.cs

# Phases/Approach/
ApproachNavigationPhase.cs     # Navigate through CIFP fix sequence (IAF→IF→FAF) with alt/speed restrictions
InterceptCoursePhase.cs        # Fly current heading until intercepting final approach course
HoldingPatternPhase.cs         # AIM 5-3-8 holding with entry determination; MaxCircuits for hold-in-lieu
ApproachClearance.cs           # Record on PhaseList storing active approach state + pre-built MAP fixes

# Phases/Pattern/
UpwindPhase / CrosswindPhase / DownwindPhase / BasePhase / MidfieldCrossingPhase

# Phases/Ground/
AtParkingPhase / PushbackPhase / PushbackToSpotPhase / TaxiingPhase / HoldingShortPhase
CrossingRunwayPhase / RunwayExitPhase / HoldingAfterExitPhase / FollowingPhase

# Data/
Data/IFixLookup.cs             # Interface: GetFixPosition, GetAirportElevation
Data/IRunwayLookup.cs          # Interface: GetRunway, GetRunways
Data/FixDatabase.cs            # Implements both; VNAS protobuf + custom fixes; AllFixNames, ExpandRoute
Data/CustomFixDefinition.cs / CustomFixLoader.cs  # Custom fix JSON loading
Data/FrdResolver.cs            # Fix-Radial-Distance → lat/lon
Data/IApproachLookup.cs        # Interface: GetApproach, GetApproaches, ResolveApproachId
Data/ApproachDatabase.cs       # IApproachLookup impl; lazy CIFP per-airport parsing; shorthand resolution
Data/IProcedureLookup.cs       # Interface: GetSid, GetSids, GetStar, GetStars
Data/ProcedureDatabase.cs      # IProcedureLookup impl; lazy CIFP per-airport SID/STAR parsing
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
RunwayIntersectionCalculator.cs # LAHSO: runway centerline intersection + hold-short distance
HoldShortAnnotator.cs          # Annotate hold-short points on taxi routes

# Data/Faa/
FaaAircraftRecord.cs           # Full FAA ACD row: wingspan, length, tail height, gear geometry, MTOW, speeds, classifications
FaaAircraftDatabase.cs         # Static lookup: Get(aircraftType) → FaaAircraftRecord?; strips type prefixes
FaaAircraftDataService.cs      # Downloads FAA ACD xlsx, parses all columns, caches per AIRAC cycle

# Data/Vnas/
VnasDataService.cs             # Downloads NavData protobuf + specs; serial-based cache
AiracCycle.cs                  # AIRAC cycle calculator (epoch Jan 23 2025, 28-day)
VnasConfig.cs                  # Config API DTO
CacheManifest.cs               # Cache manifest tracking serials
AircraftSpecEntry.cs           # VNAS aircraft specs model
AircraftCwtEntry.cs            # VNAS aircraft CWT model
CifpDataService.cs             # FAA CIFP zip download/extract per AIRAC cycle
CifpParser.cs                  # ARINC 424 parser: approaches (subsection F), SIDs (D), STARs (E); FAF fixes, terminal waypoints
                               # ParseTerminalWaypoints: per-airport section-C waypoints for RF center fix resolution
CifpModels.cs                  # CIFP data models: CifpApproachProcedure, CifpSidProcedure, CifpStarProcedure, CifpLeg, CifpTransition
                               # CifpLeg: ArcRadiusNm, ArcCenterLat/Lon (RF), RecommendedNavaidId, Theta, Rho (AF)

# Scenarios/
ScenarioLoader.cs              # JSON → ScenarioLoadResult; resolves starting conditions, nav routes, beacon codes
ScenarioModels.cs              # Scenario JSON DTOs: Scenario, ScenarioAircraft, StartingConditions, PresetCommand, etc.
                               # ScenarioGeneratorConfig (renamed to avoid collision with AircraftGenerator static class)
AircraftInitializer.cs         # InitializeOnRunway/AtParking/OnFinal → PhaseInitResult
AircraftGenerator.cs           # SpawnRequest → AircraftState (runtime spawn generator)
SpawnRequest.cs                # Spawn descriptor

Proto/nav_data.proto           # Compiled by Grpc.Tools → NavDataSet
```

## yaat-server — ASP.NET Core server (`..\yaat-server\`)

Separate repo. References Yaat.Sim via sibling project ref. Provides: SignalR comms, CRC protocol, training rooms, scenario loading, broadcast fan-out.

```
src/Yaat.Server/
  Program.cs                   # DI setup, VNAS/CIFP init, route mapping, AdminPassword validation
  YaatOptions.cs               # IOptions: AdminPassword

  Hubs/
    TrainingHub.cs             # /hubs/training (JSON); room lifecycle + delegates to RoomEngine
    CrcWebSocketHandler.cs     # Raw WebSocket /hubs/client for CRC; resolves room via JWT CID
    CrcClientState.cs          # Per-CRC state machine; holds RoomEngine ref; topic subscriptions; BuildTopicPayload helper
    CrcClientState.Session.cs  # Partial: session lifecycle (StartSession, EndSession, lifecycle push helpers)
    CrcClientState.Stars.cs    # Partial: STARS display-related state (consolidation, datablock format)
    CrcClientState.Asdex.cs    # Partial: ASDEX handlers (temp data, presets, safety config) with event broadcasts
    CrcClientState.Strips.cs   # Partial: flight strip CRUD with event broadcasts
    CrcClientManager.cs        # Client registry; BroadcastAsync fan-out
    NegotiateHandler.cs        # POST /hubs/client/negotiate; JWT extraction → CrcNegotiateTokenStore
    CrcNegotiateTokenStore.cs  # ConcurrentDictionary token→CID for CRC room resolution
    ApiStubHandler.cs          # GET/POST /api/* → [] (CRC startup probes)

  Simulation/
    TrainingRoom.cs            # Room state: Members, World, ActiveScenario, Weather, Engine, GroupName, ConsolidationState, LineNumbers, AircraftAssignments
    TrainingRoomManager.cs     # Room registry + client→room + CID→room mapping + admin tracking
    RoomEngine.cs              # Per-room facade: tick, commands, scenario, broadcast, consolidation
    ConsolidationState.cs      # Thread-safe manual consolidation overrides per room
    RoomEngineFactory.cs       # Creates RoomEngine with shared singleton deps
    SimulationHostedService.cs # Thin orchestrator: 1s tick loop iterating rooms
    TickProcessor.cs           # Stateless tick logic (physics, spawns, triggers, auto-accept, coordination timers)
    TrackCommandHandler.cs     # Stateless track command logic (HO, ACCEPT, DROP, etc.)
    CoordinationCommandHandler.cs # Stateless coordination logic (RD, RDH, RDR, RDACK, RDAUTO)
    ScenarioLifecycleService.cs # Scenario load/unload/spawn/generator logic
    ScenarioState.cs           # Per-room active scenario state: queues, positions, generators, channels
    TrainingBroadcastService.cs # SignalR hub context wrapper for training clients
    CrcBroadcastService.cs     # CRC wire-protocol broadcast; per-room scoped via BroadcastBatch; BroadcastToTopicSubscribersAsync
    CrcVisibilityTracker.cs    # STARS/ASDEX/TowerCab visibility rules
    StarsLineNumberAssigner.cs # Per-room sequential line number assignment (1-99 wrap)
    DtoConverter.cs            # AircraftState → CRC + training DTOs + ASDEX/strip converters

  Commands/
    CommandParser.cs           # Server-side canonical parsing; IsTrackCommand(), IsCoordinationCommand()
    DepartureCommandParser.cs  # Departure-specific command parsing
    GroundCommandParser.cs     # Ground operation command parsing
    ServerCommands.cs          # Server-only records (DEL, PAUSE, etc.)

  Spawn/SpawnParser.cs         # ADD command → SpawnRequest
  Protocol/                    # CRC binary: VarintCodec, MessageFraming, SignalRMessageParser, SignalRMessageBuilder
  Dtos/
    TrainingDtos.cs            # JSON DTOs for training client communication
    CrcDtos.cs                 # Main CRC binary DTOs (MessagePack)
    CrcDtos.FlightPlan.cs      # Partial: flight plan-related CRC DTOs
    CrcDtos.Session.cs         # Partial: session/StartSession CRC DTOs
    CrcDtos.Stars.cs           # Partial: STARS display-related CRC DTOs (line numbers, short-term conflicts, readout area)
    CrcDtos.Asdex.cs           # ASDEX event DTOs (temp data, presets, safety config, hold bars, alerts)
    CrcDtos.Strips.cs          # Flight strip DTOs (StripItemDto, FlightStripsStateDto, StripBayContentsDto)
    CrcEnums.cs                # Enums for CRC protocol
    CrcFormatters.cs           # Formatting helpers for CRC DTOs
    TopicFormatter.cs          # Topic subscription/message formatting
  Data/
    AirportGroundDataService.cs  # IAirportGroundData impl; fetches GeoJSON from vNAS training API
    ArtccConfig.cs             # VNAS ARTCC config deserialization models (VideoMapConfig, StarsAreaConfig, etc.)
    ArtccConfigService.cs      # Downloads + caches ARTCC config; position/TCP resolution
    ArtccConfigService.Consolidation.cs  # Partial: STARS consolidation hierarchy + manual override integration
    ArtccConfigService.VideoMaps.cs      # Partial: video map extraction + position display config resolution
    PositionRegistry.cs        # Thread-safe CRC + RPO position tracking
  Udp/UdpStubServer.cs        # UDP port 6809 stub (CRC keepalive/registration)
  Logging/FileLoggerProvider.cs
```
