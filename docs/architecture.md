# Architecture — File Tree

> **Read this file when you need to locate specific files or understand project structure.**
> CLAUDE.md contains the architectural summary; this file has the full annotated tree.

## Task Index — "I need to change X, which files?"

| Task | Key files (in order of relevance) |
|------|----------------------------------|
| **Add a new command** | `CommandRegistry.cs` → `CommandScheme.cs` → `CommandSchemeParser.cs` → `CommandDispatcher.cs` → appropriate `*CommandHandler.cs` |
| **Add a new phase** | `Phase.cs` (base) → new phase class → `PhaseList.cs` (registration) → `PhaseRunner.cs` (lifecycle) → `PhaseSnapshotDto.cs` (serialization) → `CommandDispatcher.cs` (acceptance) |
| **Altitude commands** | `AltitudeResolver.cs`, `FlightCommandHandler.cs`, `FlightPhysics.cs` (UpdateAltitude), `ControlTargets.cs` |
| **Speed commands** | `FlightCommandHandler.cs`, `FlightPhysics.cs` (UpdateSpeed/UpdateSpeedPlanning), `ControlTargets.cs`, `AircraftPerformance.cs` |
| **Heading/navigation** | `FlightCommandHandler.cs`, `NavigationCommandHandler.cs`, `FlightPhysics.cs` (UpdateNavigation/UpdateHeading), `ControlTargets.cs` |
| **Ground taxiing** | `GroundNavigator.cs`, `TaxiPathfinder.cs`, `TaxiingPhase.cs`, `TaxiRoute.cs`, `AirportGroundLayout.cs` |
| **Ground layout parsing** | `GeoJsonParser.cs`, `FilletArcGenerator.cs`, `TaxiwayGraphBuilder.cs`, `CoordinateIndex.cs` |
| **Runway exits** | `LandingPhase.cs`, `RunwayExitPhase.cs`, `ExitPreference.cs`, `AirportGroundLayout.cs` (FindExitPath) |
| **Approach procedures** | `ApproachCommandHandler.cs`, `ApproachNavigationPhase.cs`, `FinalApproachPhase.cs`, `CifpParser.cs` |
| **SID/STAR** | `DepartureClearanceHandler.cs`, `InitialClimbPhase.cs`, `CifpParser.cs`, `NavigationDatabase.cs` |
| **Radar rendering** | `RadarCanvas.cs` (input/zoom) → `RadarRenderer.cs` (drawing) → `TargetRenderer.cs` (datablocks) → `VideoMapRenderer.cs` (maps) |
| **Ground view rendering** | `GroundCanvas.cs` (input/hit-test) → `GroundRenderer.cs` (drawing, 3 layers) |
| **Command input UX** | `CommandInputController.cs` (parse pipeline) → `ArgumentSuggester.cs` (dropdown values) → `SignatureHelpState.cs` (inline hints) |
| **Weather** | `WeatherProfile.cs`, `WeatherTimeline.cs`, `WindInterpolator.cs`, `LiveWeatherService.cs` |
| **Scenarios** | `ScenarioLoader.cs`, `ScenarioModels.cs`, `AircraftInitializer.cs`, `ScenarioLifecycleService.cs` (server) |
| **Snapshots/replay** | `StateSnapshotDto.cs`, `AircraftSnapshotDto.cs`, `RecordingArchive.cs`, `SimulationEngine.cs` |
| **CRC protocol** | `CrcDtos*.cs` (wire format) → `DtoConverter.cs` (translation) → `CrcBroadcastService.cs` (dispatch) → `CrcWebSocketHandler.cs` (connection) |

## Integration Footguns

- **Modify `AircraftState`** → must mirror changes in `AircraftSnapshotDto.cs` + add migration in `SnapshotSchemaMigrator.cs`
- **New command type** → must add to `CanonicalCommandType` enum, `CommandRegistry` definitions, AND `CommandScheme.Default()`. Tests enforce completeness.
- **New phase** → must add `[JsonDerivedType]` attribute in `PhaseSnapshotDto.cs` for serialization
- **Modify `ControlTargets`** → check `ControlTargetsDto.cs` snapshot parity
- **Aircraft performance** → sync `AircraftProfiles.json` + `AircraftProfileDatabase` + `AircraftPerformance.cs` fallback logic

## Test Locations

- **Sim tests**: `tests/Yaat.Sim.Tests/` — commands, phases, physics, parsers, nav data
- **Client tests**: `tests/Yaat.Client.Tests/` — view model logic, command input
- **Test data**: `tests/Yaat.Sim.Tests/TestData/` — real NavData.dat, FAACIFP18.gz, airport GeoJSON
- **Shared loader**: `TestVnasData.EnsureInitialized()` — always use this, never synthetic stubs

## Root Scripts

```
Setup-CrcEnvironment.ps1          # Adds YAAT1 + YAAT Local to CRC's DevEnvironments.json
```

## Yaat.Client.Core — Shared library (`src/Yaat.Client.Core/`)

Shared code referenced by Yaat.Client and Yaat.VStrips. No LM-Kit, PortAudio, or SharpHook dependencies. Namespace stays `Yaat.Client.*`.

```
Logging/
  AppLog.cs                     # Static logger factory; Initialize(logFileName) called by each app's Program.cs
  FileLoggerProvider.cs         # Writes to YaatPaths.AppDataRoot/<logFileName> (yaat-client.log or yaat-vstrips.log)

Services/
  ServerConnection.cs           # SignalR client to /hubs/training (JSON); inline DTOs
  UserPreferences.cs            # JSON to YaatPaths.AppDataRoot/preferences.json (per-app: %LOCALAPPDATA%/yaat/ for Client, /yaat-vstrips/ for VStrips)
  UpdateService.cs              # Velopack auto-updater. Constructor takes channel? — null for Yaat.Client (default platform channel),
                                # "vstrips-{platform}" for Yaat.VStrips so each app downloads its own installer from the shared GitHub release.

ViewModels/
  VStripsViewModel.cs           # Root vStrips VM; manages strip bays, items, rack state
  StripItemViewModel.cs         # Per-strip observable model: flight data, annotations
  StripBayViewModel.cs          # Per-bay container: list of strips, visibility state
  StripRackViewModel.cs         # Rack (visual height) management per bay
  StripPrinterViewModel.cs      # Auto-print on aircraft departure/arrival
  ConnectViewModel.cs           # Room/identity connection flow

Views/
  ConnectWindow.axaml.cs        # Server/room/identity entry dialog
  VStripsView.axaml.cs          # Embedded vStrips control
  VStripsViewWindow.axaml.cs    # Pop-out window for vStrips view
  VStrips/FlightStripControl.axaml.cs  # Custom control rendering CRC-matching strip visuals (cream cells, barcode, handwriting, offset, disconnected ✗, selection ring)
  VStrips/InlineTextEditPopup.axaml.cs # Shared popup editor for annotations, half-strip lines, and separator labels

Services/
  VStripsCanonicalBuilder.cs    # Build canonical strip commands from UI mutations
  WindowGeometryHelper.cs       # Save/restore window position+size+topmost
  KeybindHelper.cs              # Keyboard shortcut resolution
  MacroDefinition.cs            # Macro model: Name, Expansion, ParameterNames
  GroundColorScheme.cs          # Theme/color scheme for strips
  TerminalEntry.cs              # Terminal/radio log entry (Kind: Command/Response/System/Say)
```

## Yaat.Client — Avalonia desktop app (`src/Yaat.Client/`)

```
Models/
  AircraftModel.cs              # ObservableObject wrapping AircraftDto; computed displays; FromDto/UpdateFromDto
  TerminalEntry.cs              # Terminal/radio log entry (Kind: Command/Response/System/Say)

Services/
  ServerConnection.cs           # SignalR client to /hubs/training (JSON); inline DTOs
  CommandInputController.cs     # Autocomplete (callsign/command/fix/macro), history nav, signature help, FixDb binary search; unified ParseCommandInput drives both suggestion and signature pipelines
  CommandInputParseResult.cs    # Immutable parse result consumed by both autocomplete and signature help
  CommandSignature.cs           # SignaturePart record (AXAML DataType dependency)
  SignatureHelpState.cs         # Observable state for signature help tooltip (overload nav, active param, dedup)
  MacroDefinition.cs            # Macro model: Name, Expansion, ParameterNames (positional &1 or named &hdg)
  MacroExpander.cs              # Static TryExpand: scan-and-replace #NAME args in command text
  CommandHistoryFormatter.cs    # Pure formatter — canonicalizes partial callsign prefix in up-arrow recall history
  TrainingDataService.cs         # Fetches scenarios/weather from vNAS data API (data-api.vnas.vatsim.net)
  (UpdateService.cs lives in Yaat.Client.Core; MainViewModel constructs it with channel: null)
  ArgumentSuggester.cs           # Command argument autocomplete from CommandRegistry metadata (literal options + contextual fix/runway suggestions)
  FixSuggester.cs               # Fix name suggestions from FixDb
  AddCommandSuggester.cs        # ADD command callsign/model suggestions
  SuggestionItem.cs             # Suggestion display model (text, kind, description)
  ScenarioDifficultyHelper.cs   # Scenario difficulty classification
  VideoMapService.cs            # Video map download/cache/parse (conditional HTTP freshness check)
  VnasConfigService.cs          # Fetches vNAS configuration (base URLs for video maps, tower cab images)
  TowerCabImageService.cs       # Downloads/caches tower cab JPEG backgrounds with EXIF geo-referencing
  TowerCabMapParser.cs          # Parses tower cab GeoJSON video maps into filled polygons + colored lines
  LiveWeatherService.cs         # Fetches live METARs + FD winds from aviationweather.gov → WeatherProfile
  ArtccAirportResolver.cs       # Fetches vNAS ARTCC config → underlying airport IDs (cached)
  FdRegionMapping.cs            # Static ARTCC → FD region code mapping
  UserPreferences.cs            # JSON to %LOCALAPPDATA%/yaat/preferences.json (incl. SavedMacro list)
  MenuGroup.cs                  # Enum of context menu groups (Heading, Altitude, Speed, Tower, etc.)
  ContextMenuProfile.cs         # Record: Primary/Secondary/Hidden menu groups for a phase
  ContextMenuProfileService.cs  # Static: maps phase name + isOnGround → ContextMenuProfile
  BuildInfo.cs                  # Static: version (from AssemblyInformationalVersion) + release-vs-dev detection (VelopackLocator.Current); used by title bar, About window, and startup log line
  DocLinks.cs                   # Static: GitHub URLs for user-facing docs (README/USER_GUIDE/COMMANDS/CHANGELOG/issues), pinned to release tag for installed builds, main for dev
  UrlLauncher.cs                # Static: opens HTTPS URLs in OS default browser (Process.Start with UseShellExecute)

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
  WeatherPeriodViewModel.cs     # Per-period VM: wind layers, METARs, precipitation, start/transition minutes
  WeatherTimelineEditorViewModel.cs  # Timeline editor VM: period list, BuildJson (v1 if 1 period, v2 if 2+), FromJson
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
  WeatherEditorControl.axaml.cs # Per-period weather editing UserControl (precipitation, wind layers grid, METARs)
  AboutWindow.axaml.cs          # Help → About dialog: version, build kind, .NET runtime, log path, GitHub link
  WeatherTimelineEditorWindow.axaml.cs  # Timeline editor: period list (left) + WeatherEditorControl (right); v1/v2 auto-format on save
  ScenarioValidationWindow.axaml.cs  # Batch scenario validation report (DataGrid of failures, copy report)
  WindowGeometryHelper.cs       # Save/restore window position+size+topmost

Views/Map/
  MapViewport.cs                # Shared equirectangular projection for map views
  MapCanvasBase.cs              # ICustomDrawOperation base + pan/zoom input handling

Views/Ground/
  GroundView.axaml.cs           # Ground view control with context menus + layer toggles (SAT/MAP/GND)
  GroundViewWindow.axaml.cs     # Pop-out ground window
  GroundCanvas.cs               # SkiaSharp canvas with StyledProperties + hit-testing
  GroundRenderer.cs             # Stateless SkiaSharp ground renderer (3 layers: satellite, video map, YAAT layout)

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

## Yaat.VStrips — Standalone app (`tools/Yaat.VStrips/`)

Flight strip display client independent of Yaat.Client. References Yaat.Client.Core only. 109 MB self-contained publish (no LM-Kit, no PortAudio, no SharpHook). Students run this alongside CRC while YAAT awaits vNAS vStrips approval.

```
StandaloneViewModel.cs         # Owns ServerConnection + VStripsViewModel + connect/room flow; also holds the auto-update banner state (UpdateService with channel: vstrips-{platform})
RoomPickerWindow.axaml.cs      # Room selection after login
MainWindow.axaml{,.cs}         # DockPanel host: menu bar, update banner (visible when UpdateService finds a release), status bar, VStripsView
App.axaml.cs                   # XAML app root
Program.cs                     # Entry point. VStripsChannel constant — must match the --channel flag in release.yml's vpk pack invocations.
                               # Initializes YaatPaths to %LOCALAPPDATA%/yaat-vstrips/ so settings/log don't collide with Yaat.Client.
```

## Yaat.Sim — Shared simulation library (`src/Yaat.Sim/`)

No UI deps. Deps: Google.Protobuf, Microsoft.Extensions.Logging.Abstractions.

```
# Core
AircraftState.cs               # Mutable aircraft entity. Identity + kinematics flat at top; cohesive
                               # state grouped into sub-objects (FlightPlan, Transponder, Ground, Track,
                               # Stars, Eram, Approach, Procedure, Pattern, Clearance, HoldAnnotation,
                               # Ghost, Voice). Each sub-object owns its own ToSnapshot/FromSnapshot pair
                               # with a matching DTO under Simulation/Snapshots/.
                               # DeclinationCachePosition (LatLon?): null = "not cached", not serialized.
                               # Ground.Layout is [JsonIgnore]; Ground.LayoutAirportId preserves the
                               # reference so archive restore can reattach.
                               # PendingObservations: ephemeral pilot-side "watch for condition" state (not persisted in snapshots)
                               # FOOTGUN: changes here must be mirrored in AircraftSnapshotDto + SnapshotSchemaMigrator
ControlTargets.cs              # Autopilot targets: heading, altitude, speed (IAS), NavigationRoute
                               # NavigationTarget: Position (LatLon) + optional AltitudeRestriction + SpeedRestriction (for SID/STAR via mode)
                               # TargetMach: when set, UpdateSpeed recomputes equivalent IAS each tick (Mach hold)
LatLon.cs                      # Readonly record struct: public LatLon(double Lat, double Lon). The canonical coordinate type
                               # across Yaat.Sim / Yaat.Client / yaat-server. Field names match CRC Point DTO. No implicit tuple conversion
                               # (forces explicit `new LatLon(lat, lon)` at external-JSON boundaries so argument swaps don't slip through)
FlightPhysics.cs               # Static 8-step Update: navigation→descentPlan→climbPlan→speedPlan→heading→altitude→speed→position→queue
                               # UpdateSpeedPlanning: proactive speed look-ahead for procedure fixes (mirrors descent/climb planning)
                               # Auto speed schedule: skipped when ActiveApproach or ManagesSpeed (pattern phases)
                               # 14 CFR 91.117: 250 KIAS cap below 10,000 ft in UpdateSpeed() and ApplyFixConstraints()
                               # Wind physics: TAS = IasToTas(IAS, alt); GS/Track derived from TAS + wind vector; WCA applied to nav
                               # ApplyFixConstraints: SID/STAR via-mode constraint enforcement at waypoints
                               # Bank angle: computed in UpdateHeading from atan(TAS × turnRate × coeff); sign follows turn direction
                               # Expedite: IsExpediting → 1.5x climb/descent rate; Mach hold: TargetMach → recompute IAS each tick
GeoMath.cs                     # Static: DistanceNm (haversine), BearingTo, TurnHeadingToward, GenerateArcPoints (RF/AF)
                               # Each primary function has scalar (double, double, double, double) and LatLon (LatLon, LatLon) overloads
                               # FootOfPerpendicular returns (LatLon Foot, double AlongNm, bool Clamped)
SimLog.cs                      # Static logger factory for Yaat.Sim; Initialize(ILoggerFactory) at startup
SerializableRandom.cs          # Xoshiro256** PRNG with serializable state (RngState record); drop-in Random replacement
SimulationWorld.cs             # Thread-safe aircraft collection; GetSnapshot, Tick, DrainWarnings
                               # WeatherProfile? Weather — passed to FlightPhysics.Update() each tick
CommandQueue.cs                # CommandBlock (trigger + closure + TrackedCommands), BlockTrigger
                               # CommandDimension flags (Lateral|Vertical|Speed) for dimension-aware queue clearing
                               # ReadyToAdvance: lateral gates block advancement; altitude/speed are fire-and-forget
                               # SourceCommandText on CommandBlock/DeferredDispatch for snapshot restore
AircraftCategory.cs            # Enum + AircraftCategorization (static Init from AircraftSpecs.json)
                               # CategoryPerformance: fallback aviation constants (taxi, pattern geometry, flare, etc.)
                               # CornerSpeedForAngle: piecewise taxi speed curve (0-30° max, 30-90° corner, 90-150° tight corner)
AircraftPerformance.cs         # Unified perf API: profile-first with category fallback. Altitude-banded
                               # climb/descent rates, Mach-aware speeds, 91.117 waiver support
GroundConflictDetector.cs      # Static pairwise ground proximity → max-speed overrides
ConflictAlertDetector.cs       # Static STARS CA detection: 3nm/1000ft thresholds, 5s extrapolation, hysteresis, approach suppression
WeatherProfile.cs              # WeatherProfile + WindLayer; ATCTrainer-compatible JSON; layers sorted by altitude on load
                               # GetWeatherForAirport: cached METAR lookup via MetarInterpolator
WeatherPeriod.cs               # Single weather period in a v2 timeline: startMinutes, transitionMinutes, windLayers, metars, precipitation
WeatherTimeline.cs             # Time-based weather evolution: list of WeatherPeriods; GetWeatherAt(elapsedSeconds) interpolates wind
                               # layers during transitions (N/E vector decomposition); METARs/precipitation snap at transition start
                               # HasMeaningfulChange: rate-limits broadcasts (direction >1°, speed >0.5kt tolerance)
WeatherTimelineParser.cs       # Static v1/v2 auto-detection parser: checks for "periods" array → WeatherTimeline, else → WeatherProfile
                               # Returns WeatherParseResult discriminated union (Timeline | Profile | Error)
WindInterpolator.cs            # Static wind utilities: GetWindAt, GetWindComponents (vector lerp through 0/360), IasToTas (8-point
                               # lookup table), TasToIas, MachToIas (ISA speed-of-sound model), ComputeWindCorrectionAngle
MetarParser.cs                 # Static METAR parsing: station ID, ceiling (BKN/OVC), visibility (SM); ParsedMetar record
MetarInterpolator.cs           # Static: GetWeatherForAirport — exact station match then IDW interpolation within 50nm
WindsAloftParser.cs            # Static: parses FAA FD fixed-width text → StationWinds[]; DecodeWind handles 100+kt, light/variable
MagneticDeclination.cs         # Static: approximate CONUS magnetic declination from lon; TrueToMagnetic conversion
VisualDetection.cs             # Static: TryAcquireAirport, TryAcquireAirportForRunway, TryAcquireTraffic, IsOccludedByBank
                               # Returns VisualAcquisitionResult { Acquired, Reason, DistanceNm, MaxRangeNm }
                               # VisualAcquisitionFailure enum: InClassA, AboveCeiling, MixedCeiling, BehindOwnship, OccludedByBank, OutOfRange, OppositeSideOfRunway
                               # Forward hemisphere, visibility, ceiling, bank angle occlusion (7110.65 §7-4-4.c.2), WTG-based traffic range
                               # FL180 gate on airport (visual approach eligibility) but NOT traffic (pilots can see in Class A)
VisualAcquisition.cs           # Static helpers: TryAcquireTraffic(ownship, target, weather) and TryAcquireAirport(ownship, weather)
                               # Bundle METAR/elevation/bank-angle lookup around VisualDetection so RTIS/RFIS first-check and
                               # PilotObservationUpdater re-check use identical inputs. TryAcquireAirport returns null when the
                               # destination is missing or not in the nav db (caller drops the observation).
PilotObservation.cs            # Abstract record PilotObservation + TrafficAcquisitionObservation(TargetCallsign) + FieldAcquisitionObservation
                               # Pilot-side "watch for a condition" state — populated when RTIS/RFIS soft-fail (pilot keeps looking)
                               # Extension points for future "report leaving altitude", "report passing fix", etc.
PilotObservationUpdater.cs     # Static per-tick evaluator called from FlightPhysics.Update after UpdateCommandQueue
                               # Re-runs VisualAcquisition.TryAcquireTraffic / TryAcquireAirport; on success sets the matching
                               # HasReported* flag and pushes the in-sight pilot readback. Acquisition readbacks route through
                               # PendingWarnings (WRN/Orange) so the event catches RPO attention. Silently drops observations
                               # whose target has left the sim or whose destination is no longer lookupable.
WakeTurbulenceData.cs          # Static: WTG code lookup from AircraftSpecs.json; TrafficDetectionRangeNm by WTG (A=15nm to F=3nm)

# Track operations
TrackOwner.cs                  # Record: Callsign, FacilityId, Subset, SectorId, OwnerType
TrackOwnerType.cs              # Enum: Other, Eram, Stars, Caats, Atop
Tcp.cs                         # Record: Subset, SectorId, Id, ParentTcpId
StarsPointout.cs / StarsPointoutStatus.cs  # Pointout state
EramPointoutState.cs           # Per-aircraft ERAM pointout record (mirrors vatsim-server-rs radar_state::PointoutState)
                               # Runtime-only: not round-tripped through AircraftSnapshotDto, consistent with other ERAM per-track state

# Coordination
CoordinationChannel.cs         # Channel config: ListId, Title, SendingTcps, Receivers, Items
CoordinationItem.cs            # Single coordination entry: status lifecycle, expiry, origin TCP
StarsCoordinationStatus.cs     # Enum: Unsent→Unacknowledged→Acknowledged→Recalled→Expiry→Void

# Commands/
Commands/CanonicalCommandType.cs    # Enum of every command type
Commands/ParsedCommand.cs           # Discriminated union records; CompoundCommand/ParsedBlock/BlockCondition; includes server-only commands (DEL, PAUSE, ADD, etc.)
Commands/CommandDefinition.cs       # ArgMode enum, CommandDefinition/CommandOverload/CompoundModifier records
Commands/CommandRegistry.cs         # Single source of truth: CommandDefinition per type (label, category, aliases, overloads, modifiers)
Commands/CommandScheme.cs           # CanonicalCommandType → CommandPattern (aliases only); Default() from registry
Commands/CommandSchemeParser.cs     # Parse/ParseCompound (;/, syntax); ExpandSpeedUntil; concatenation fallback; ToCanonical()
Commands/CommandSignature.cs        # Records: CommandParameter, CommandSignature, CommandSignatureSet; FromDefinition factory
Commands/CommandDispatcher.cs       # Static: DispatchCompound (phase interaction), ApplyCommand (thin routing switch),
                                    # TryApplyTowerCommand, queue infrastructure, condition conversion, shared utilities
                                    # ClearConflictingBlocks: dimension-aware selective queue clearing
                                    # SplitBlockNonConflicting: splits mixed-dimension blocks on partial conflicts
Commands/DispatchContext.cs         # Record: GroundLayout, Rng, Weather, FindAircraft, ValidateDctFixes, AutoCrossRunway
                                    # Bundled at SimulationEngine/RoomEngine call sites; threaded through all internal helpers
Commands/FlightCommandHandler.cs    # Heading, altitude, speed, squawk, direct-to, warp, wait/say commands
Commands/NavigationCommandHandler.cs # Multi-block navigation: JRADO/JRADI, depart/cross fix, JARR STAR resolution,
                                    # JAWY airway intercept, CVIA/DVIA (DVIA SPD fix), JFAC, holding pattern, RFIS/RTIS, list approaches
Commands/CommandDescriber.cs        # Static: DescribeCommand, DescribeNatural, classification helpers
                                    # GetDimension, GetCommandDimension, GetCompoundDimensions for queue clearing
Commands/AltitudeResolver.cs        # Plain int or AGL format → feet MSL
Commands/RouteChainer.cs            # After DCT to on-route fix, appends remaining route fixes
Commands/ApproachCommandHandler.cs  # Approach clearance logic (CAPP/JAPP/PTAC/CAPPSI/JAPPSI/CAPPF/JAPPF/PTACF forced variants/CVA visual approach); RF/AF arc expansion in BuildApproachFixes
Commands/DepartureClearanceHandler.cs  # Departure clearance + CIFP SID resolution, CancelTakeoff, ClearedTakeoffPresent (CTOPP)
Commands/GroundCommandHandler.cs    # Ground operation command logic (taxi, pushback, hold short)
Commands/TrackEngine.cs             # Pure domain logic for STARS track ops: Track, Drop, Handoff, Accept, Cancel, PointOut, Acknowledge,
                                    # RejectPointout, RetractPointout, Scratchpad1/2, TempAlt, Cruise, PilotReportedAlt,
                                    # InhibitConflictAlert, LeaderDirection, JRing, Cone. All methods mutate AircraftState directly.
Commands/PatternCommandHandler.cs   # Pattern operation command logic (extend, rock wings, GoAround, CTL, sequence, etc.); EF loop detection via turn-arc geometry
Commands/StripCommandHandler.cs     # Flight strip CRUD (STRIP, STRIPD, STRIPO, AN, HSC, HSA, HSD, HSM, HSO, HSS, SEP, SEPD, BLANK, BLANKD); dispatches to StripMutations

# Phases/ — clearance-gated behavior
Phases/Phase.cs                # Abstract: OnStart/OnTick/OnEnd, CanAcceptCommand→CommandAcceptance, ManagesSpeed (suppresses auto schedule)
Phases/PhaseList.cs            # Mutable list: AssignedRunway, TaxiRoute, LandingClearance, ActiveApproach, DepartureClearance, mutations
Phases/PhaseRunner.cs          # Static lifecycle: start→tick→advance; auto-appends exit/pattern phases
Phases/PhaseContext.cs         # Readonly tick context; includes Weather, TowerPosition for RV SID heading hold
Phases/PhaseStatus.cs          # Enum: phase lifecycle status
Phases/CommandAcceptance.cs    # Enum: Allowed, Rejected, ClearsPhase
Phases/ClearanceRequirement.cs # Clearance requirement definitions
Phases/ExitPreference.cs       # ExitSide enum, ExitPreference class, ResolvedExitInfo (branch point + path + turn-off speed)
Phases/ClearanceType.cs        # Enum: LineUpAndWait, ClearedForTakeoff/Land/Option/TouchAndGo/StopAndGo, RunwayCrossing
Phases/RunwayInfo.cs           # Runway geometry
Phases/GlideSlopeGeometry.cs   # Altitude/descent rate calculations (3° default)
Phases/PatternGeometry.cs      # 7 pattern waypoints from RunwayInfo + category + direction
Phases/PatternBuilder.cs       # BuildCircuit, BuildNextCircuit, UpdateWaypoints

# Phases/Tower/
LineUpPhase.cs                 # State-machine lineup via LineUpGeometry: Aligned (straight → fillet arc → rollout) or Pivot (SlowTurn → perpendicular straight → SlowTurn → rollout) chosen by waste-straight vs remaining-runway. Faulted stays stopped (user recovers via TAXI / CANCEL CLEARANCE)
LineUpGeometry.cs              # Pure geometry: classifies aircraft pose as Aligned, Pivot, or Fault; builds LineUpPathPlan with closed-form primitives (nose-out, arc, pivot turns, straight, rollout). Pivot fallback used when straight path would waste >20% of remaining runway (issue #142)
LineUpArcPlayback.cs           # Closed-form circular-arc playback (invariant I2: position and heading are functions of a single scalar)
LinedUpAndWaitingPhase.cs      # Hold at threshold; await ClearedForTakeoff
TakeoffPhase.cs                # Ground roll→Vr→400ft AGL
InitialClimbPhase.cs           # Climb to 1500ft AGL or assigned; activates SID via mode; RV SID heading hold until handoff+5s
FinalApproachPhase.cs          # Glideslope; auto-go-around at 0.5nm; illegal intercept check (§5-9-1)
LandingPhase.cs                # Flare→touchdown→rollout; continuous exit evaluation (resolve→brake→commit/abandon→relax preference); LAHSO-aware
RunwayHoldingPhase.cs          # LAHSO: hold at 0kts on runway after landing; clearance-gated (RunwayCrossing)
GoAroundPhase.cs               # TOGA, runway heading, climb 2000ft AGL (pattern alt for VFR/pattern traffic)
TouchAndGoPhase.cs / StopAndGoPhase.cs / LowApproachPhase.cs
MakeTurnPhase.cs               # 360/270 turn tracking (cumulative degrees, exit heading); clones pattern phase for 360s
STurnPhase.cs                  # S-turn phase: alternating 30° deviations from final heading for spacing
VfrHoldPhase.cs                # VFR hold: orbit at current position (HPP) or navigate-then-orbit at fix (HFIX)

# Phases/Approach/
ApproachNavigationPhase.cs     # Navigate through CIFP fix sequence (IAF→IF→FAF) with alt/speed restrictions + next-fix speed look-ahead
InterceptCoursePhase.cs        # Fly current heading until intercepting final approach course; detects bust-through (sign flip or 180s timeout) and notifies RPO. ForcedIntercept (PTACF, CAPPF implied-PTAC) bypasses the 30° capture gate — forces capture on steep cuts, overshoots expected
HoldingPatternPhase.cs         # AIM 5-3-8 holding with entry determination; MaxCircuits for hold-in-lieu
ApproachClearance.cs           # Record on PhaseList storing active approach state + pre-built MAP fixes

# Phases/Pattern/
UpwindPhase / CrosswindPhase / DownwindPhase / BasePhase / MidfieldCrossingPhase / PatternEntryPhase
VfrFollowPhase.cs              # VFR FOLLOW command phase. Pursues lead (heading + speed with spacing correction, altitude untouched); auto-joins lead's pattern when within 3 nm of the downwind abeam point AND within 5 nm of the lead AND on the correct side of the runway. Runaway-distance cancel after 30 s of growing gap. Spacing uses wider free-flight distances (1.5/2.0/2.5 nm) vs pattern-tight (1.0/1.5/2.0 nm)
AirborneFollowHelper.cs        # Shared spacing math. GetAdjustedSpeed for pattern phases (ctx-based) + AdjustedFreeFlightSpeed for VfrFollowPhase (wider margins). Auto-cancels with warning if follower can't maintain separation at min speed

# Phases/Ground/
AtParkingPhase / PushbackPhase / PushbackToSpotPhase / TaxiingPhase / HoldingShortPhase
CrossingRunwayPhase / HoldingAfterExitPhase / FollowingPhase
GroundNavigator.cs             # Core ground nav: angle-based speed scaling, multi-segment kinematic braking, bezier arc following (carrot-on-a-stick path tracking with dynamic curvature speed)
RunwayExitPhase.cs             # Rolls on centerline until exit found; builds TaxiRoute from exit path and hands off to TaxiingPhase
HoldingAfterExitPhase.cs       # Post-exit hold: broadcasts "clear of runway", faces away from runway, awaits taxi command

# Pilot/ — solo-training pilot AI (deterministic readbacks)
Pilot/PhraseologyVerbalizer.cs # Static: inverts a PhraseologyRule for a given accepted ParsedCommand → spoken-English readback string.
                               # Picks the first-declared rule per CanonicalCommandType (textbook form), substitutes captures via AtcNumberParser
Pilot/PilotResponder.cs        # Static: BuildReadback(CompoundCommand, AircraftState) → readback line for solo-training mode.
                               # Uses PhraseologyVerbalizer for rule-backed commands; spawn check-in / "going around" live here directly
Pilot/PilotPersonality.cs      # Enum (Verbatim) controlling readback variation; Verbatim emits the textbook form for every command

# Speech/ — STT + phraseology rule engine (Yaat.Sim layer)
Speech/PhraseologyMapper.cs    # Static: transcript → canonical command (rule-based layer of the hybrid NLU).
                               # Pipeline: digit normalize → tokenize/strip filler → callsign extract → condition extract → longest-match against PhraseologyRules.All
Speech/PhraseologyRule.cs      # Single rule record: Pattern (literal / literal? / {capture} tokens) + OutputTemplate + CanonicalType
Speech/PhraseologyRules.cs     # Static catalog of all phraseology → canonical rules, organized by command category to mirror CommandRegistry
Speech/PhraseologyCommandMapper.cs  # ISpeechCommandMapper adapter so the rule engine can sit alongside the LLM fallback in the speech pipeline list
Speech/ISpeechCommandMapper.cs # Interface + MapContext record (active callsigns, programmed fixes, custom-fix patterns) shared by rule + LLM mappers
Speech/CanonicalCommandGrammar.cs   # GBNF grammar generated from CommandRegistry.AliasToCanonicType; constrains LLM fallback output to valid canonical commands
Speech/AtcNumberParser.cs      # Bidirectional spoken-numbers ↔ digit conversion (NormalizeDigits, FlightNumberToWords, AltitudeToWords)
Speech/CallsignParser.cs       # Spoken callsign ↔ ICAO callsign (TryParseLeading/Trailing for transcripts; IcaoToSpoken for prompt seeding)
Speech/AirlineTelephony.cs     # Static bidirectional airline ICAO ↔ telephony map; data from OpenFlights airlines.dat (ODbL 1.0)
Speech/AircraftTypeNames.cs    # Static ICAO type designator → spoken manufacturer/family name (e.g. C25C → "Citation"); preprocessed from vNAS AircraftSpecs
Speech/ScenarioCallsignExtractor.cs # Pulls custom telephony designators from scenario flight-plan remarks for whisper initial_prompt seeding
Speech/NatoPhoneticAlphabet.cs # Single canonical NATO letter ↔ word map consumed by every other Speech/ class
Speech/NatoLetterNormalizer.cs # Collapses runs of NATO words ("tango uniform whiskey") into single taxiway tokens; topology-aware via the airport taxiway set
Speech/NatoNearMissResolver.cs # Levenshtein-1 rewrite of Whisper NATO mishears; runs after custom-fix collapse and before callsign extraction
Speech/PhoneticFixMatcher.cs   # Fuzzy-match transcribed tokens against known fix names (Whisper transcribes fixes phonetically — this restores the canonical id)
Speech/CustomFixSpeechPattern.cs # Multi-token spoken pattern → custom-fix canonical alias (e.g. "runway 30 numbers" → OAK30NUM); built at NavigationDatabase load
Speech/WhisperBiasingPrompt.cs # Static initial_prompt assembled from ATC numbers, all PhraseologyRules literals, and the SCRAMBLED NATO alphabet (avoids the alphabetical-extrapolation prior)
Speech/Data/                   # Static reference data: airlines.tsv (OpenFlights), aircraft-types.tsv (ICAO Doc 8643 via vNAS), source .meta + LICENSE-OPENFLIGHTS.txt

# Data/
Data/NavigationDatabase.cs     # Static singleton: unified NavData fixes/runways/airways/SID/STAR indexes + lazy CIFP procedures.
                               # Access via NavigationDatabase.Instance (initialized at startup, SetInstance for tests).
Data/RouteExpander.cs          # Static: expands route strings (SID/STAR/airway/fix tokens) into ordered fix lists
Data/CustomFixDefinition.cs / CustomFixLoader.cs  # Custom fix JSON loading
Data/FrdResolver.cs            # Fix-Radial-Distance → lat/lon
Data/ApproachGateDatabase.cs   # Static: min intercept distances from CIFP (§5-9-1)
Data/VideoMapMetadata.cs       # Video map metadata model
Data/VideoMapData.cs           # Video map data structures (lines, labels, filters)
Data/VideoMapParser.cs         # GeoJSON → VideoMapData

# Data/Airport/
IAirportGroundData.cs          # Interface: GetLayout(airportId) → AirportGroundLayout?
AirportGroundLayout.cs         # Graph: IGroundEdge interface, GroundNode, GroundEdge (straight), GroundArc (bezier fillet arc: P1/P2 control points + MinRadiusOfCurvatureFt), DirectionalEdge (traversal direction)
                               # AllEdges (Edges+Arcs), FindAdjacentHoldShort (BFS, max 12 hops), FindExitPath, FindNearestHoldShortAhead, FindExitAheadOnRunway, ComputeExitAngle
CubicBezier.cs                 # Bezier math utilities; used by FilletArcGenerator (arc generation) and GroundNavigator (path following)
FilletArcGenerator.cs          # Replaces intersection nodes with bezier fillet arcs; plan-then-execute: compute tangent points → create arcs → rebuild edges → delete node
                               # Radius fits to edge length, collinear merges produce inner straight edges, coincident node merge pass, applied as Step 8 in GeoJsonParser
RunwayIdentifier.cs            # Struct: runway designator parsing/matching
TaxiRoute.cs                   # Resolved path: TaxiRouteSegment (DirectionalEdge wrapping IGroundEdge) + HoldShortPoints (with dynamic lat/lon offset) + DestinationParking/DestinationSpot + completion
TaxiPathfinder.cs              # 3-strategy A* + Yen's K-shortest: FewestTurns (minimize taxiway transitions), Shortest (distance), Fastest (time with arc speed limits)
                               # ResolveExplicitPath, FindRoute (single best), FindRoutes (multi-route suggestions), variant inference
TaxiVariantResolver.cs         # Variant path resolution (e.g., A vs A1)
VirtualNode.cs                 # Factory for virtual ground nodes (negative IDs); CreateEdge, CreateSegment, OffsetBefore/OffsetPast
TaxiwayGraphBuilder.cs         # Graph construction from GeoJSON nodes/edges
GeoJsonParser.cs               # GeoJSON→layout; DetectRunwayCrossings via SplitEdgeAtNode
CoordinateIndex.cs             # Spatial index for coordinate-based lookups
RunwayCrossingDetector.cs      # Detect taxiway/runway intersections
RunwayIntersectionCalculator.cs # LAHSO: runway centerline intersection + hold-short distance
HoldShortAnnotator.cs          # Annotate hold-short points on taxi routes; ComputeHoldShortPositions offsets taxiway HS by fuselage length

# Data/
AircraftProfile.cs             # Per-type performance profile record (from AircraftProfiles.json)
AircraftProfileDatabase.cs     # Static lookup: Get(aircraftType) → AircraftProfile?; 163 types
AircraftProfiles.json          # ATCTrainer per-type perf data: altitude-banded climb/descent, Mach speeds

# Data/Faa/
FaaAircraftRecord.cs           # Full FAA ACD row: wingspan, length, tail height, gear geometry, MTOW, classifications
FaaAircraftDatabase.cs         # Static lookup: Get(aircraftType) → FaaAircraftRecord?; used for physical dimensions
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
ScenarioValidator.cs           # Validates preset commands via CommandParser.ParseCompound; shared by CLI tool + client
                               # ScenarioValidationResult, PresetParseFailure, ProcedureIssue, ProcedureIssueKind records
                               # Detects outdated procedure versions (VersionChanged) and missing procedures (NotFound)
AircraftInitializer.cs         # InitializeOnRunway/AtParking/OnFinal → PhaseInitResult
AircraftGenerator.cs           # SpawnRequest → AircraftState (runtime spawn generator)
SpawnRequest.cs                # Spawn descriptor

# Simulation/
SimulationEngine.cs            # Scenario load, tick orchestration, replay (ReplayTo, ReplayRange, ReplayWithSnapshots)
                               # CaptureSnapshot/RestoreFromSnapshot; reattaches GroundLayouts to delayed spawns on restore
SimScenarioState.cs            # Per-scenario runtime state: queues, settings, ATC positions, coordination
SessionRecording.cs            # v1 (commands) + v2 (commands + snapshots) recording format; Version, Snapshots fields
RecordedAction.cs              # Polymorphic recorded actions: Command, AmendFlightPlan, WeatherChange, SettingChange
RecordingCompression.cs        # Brotli compress/decompress; auto-detects Brotli, gzip, or plain JSON on read
RecordingArchive.cs            # v4 ZIP archive reader: on-demand snapshot loading, layout reading, seek API
                               # ToBaseSessionRecording (no snapshots), FindNearestSnapshotIndex, ReadSnapshotAt
RecordingArchiveWriter.cs      # v4 ZIP archive writer: streaming snapshots + deduplicated ground layouts
RecordingManifest.cs           # Archive manifest: snapshot index, LayoutAirportIds, metadata
RecordingJsonOptions.cs        # Shared JsonSerializerOptions for recording serialization
ScenarioQueues.cs              # DelayedSpawn, ScheduledTrigger, ScheduledPreset, GeneratorState, DelayedHandoff
ConsolidationState.cs          # Thread-safe manual consolidation overrides

# Simulation/Snapshots/
StateSnapshotDto.cs            # Top-level snapshot DTO + TimedSnapshot (elapsed + action index + state)
AircraftSnapshotDto.cs         # Aircraft state DTO (~100 fields) + nested DTOs (TrackOwner, Tcp, Pointout, SharedState, etc.)
ControlTargetsDto.cs           # Control targets + NavigationTarget + altitude/speed restriction DTOs
CommandQueueDto.cs             # CommandBlock/TrackedCommand/BlockTrigger/DeferredDispatch DTOs
PhaseSnapshotDto.cs            # Polymorphic PhaseDto with [JsonDerivedType] for all ~35 Phase subclasses
                               # RunwayInfoDto, ApproachClearanceDto, DepartureClearanceDto, PatternWaypointsDto, etc.
ScenarioSnapshotDto.cs         # SimScenarioState DTO: queues, generators, settings, coordination channels
ServerSnapshotDto.cs           # Server-side state: consolidation overrides, conflict alerts, beacon code pool
TaxiRouteDto.cs                # Taxi route segments + hold-short points (re-resolved from ground layout on restore)
SnapshotSchemaMigrator.cs      # Sequential migration chain for snapshot DTO versioning; SnapshotSchemaException

# Testing/
TestVnasData.cs                # Shared test data loader: NavData, CIFP, AircraftSpecs, AircraftCwt, FaaAcd, AircraftProfiles

Proto/nav_data.proto           # Compiled by Grpc.Tools → NavDataSet
```

## Yaat.LayoutInspector — CLI tool (`tools/Yaat.LayoutInspector/`)

Loads airport GeoJSON and queries the ground graph (nodes, taxiways, runways, exits, BFS path traces, pathfinder route forensics, parking/spots), renders interactive HTML maps with optional tick overlays, and prints text tick-tables from `TickRecorder` JSON. Output modes are mutually exclusive: `--html` → HtmlRenderCommand, `--dump` → DumpCommand, `--tick-table`/`--tick-summary` → TickTableCommand, otherwise → QueryCommand (text or `--json`).

```
Program.cs                     # Thin entry: parse args → bootstrap → dispatch ICommand
CliOptions.cs                  # Options record + TryParse (all arg parsing lives here, including comma-separated --node id lists, batch query flags, --html-route, --pathfinder)
UsageText.cs                   # --help text
Bootstrap.cs                   # NavData auto-discovery (walks up to yaat.slnx) + debug logger wiring

Commands/
  ICommand.cs                  # int Execute(LayoutAnalyzer, CliOptions)
  QueryCommand.cs              # Default: text/json query dispatch (--taxiway, --runway, --node, --exits, --bfs, --pathfinder, --parking, --spots, --intersection, --validate)
  HtmlRenderCommand.cs         # --html <path>: interactive HTML render; honors all --html-* highlights and overlays --ticks animation when present
  DumpCommand.cs               # --dump: full airport JSON to stdout
  TickTableCommand.cs          # --tick-table / --tick-summary: TickRecorder JSON → fixed-width text table; optional --tick-ref + --tick-hold-shorts add cross-track / along-track columns

Tick/
  TickRecording.cs             # Top-level TickRecorder JSON schema (mirrors Yaat.Sim.Tests.Helpers.TickRecording); rejects unknown major versions
  TickJsonReader.cs            # JSON file → TickRecording
  TickDataRow.cs               # One per-tick aircraft state row used by both HTML overlay and text formatter
  RunwayReference.cs           # Runway centerline (lat/lon + true heading) for signed xteFt / hdgErr columns
  HoldShortResolver.cs         # Resolve --tick-hold-shorts taxiway letters → GroundNode list + along-track distance math

LayoutAnalyzer.cs              # Core query engine over AirportGroundLayout
LayoutValidator.cs             # Post-fillet sanity checks: stale node refs, degenerate arcs, tangent misalignment (run via --validate)
QueryResults.cs                # Result record DTOs for all queries
IFormatter.cs                  # Output formatter interface (text vs JSON)
TextFormatter.cs               # Human-readable stdout formatter for query results
JsonFormatter.cs               # JSON stdout formatter (--json flag)
TickTableFormatter.cs          # Fixed-width text formatter for --tick-table / --tick-summary (not an IFormatter — operates on a row list, not single results)
HtmlRenderer.cs                # Interactive HTML+Canvas renderer; embeds layout JSON in inspector-template.html, all rendering happens client-side
inspector-template.html        # Page shell — pan/zoom, search, toggle highlights, tick-overlay player, URL-hash persisted view
inspector.css / inspector.js   # Extracted styles + client logic (layout polish, forensic restyle); kept out of the C# string template
```

## Yaat.TickAnimator — CLI tool (`tools/Yaat.TickAnimator/`)

Renders animated GIFs of aircraft movement over airport ground layouts. Reads tick CSV data (from `TickRecorder` in tests) + airport GeoJSON, renders frames with SkiaSharp, combines via ffmpeg. See `docs/tick-animator.md`.

```
Program.cs                     # CLI entry: --layout, --ticks, --aircraft, --output, --start/--end, --fit-layout
FrameRenderer.cs               # SkiaSharp frame rendering: layout, aircraft shape, trail, overlay
```

## Yaat.SpeechSandbox — GUI/CLI tool (`tools/Yaat.SpeechSandbox/`)

Interactive sandbox for the speech pipeline (STT) and text-to-speech (TTS) experiments. Loads `UserPreferences` from the standard YAAT config location so the sandbox uses the same models/settings as the live app.

```
Program.cs                     # Entry: dispatches CLI subcommands (--pipeline, --lmkit-stt, --lmkit-models, --lmkit-gpus, --yaat-catalog, --llm-probe) or launches the GUI
App.axaml{,.cs}                # Avalonia app shell; Fluent dark theme + h2/subtle styles
MainWindow.axaml{,.cs}         # TabControl host with two tabs: STT pipeline (existing) + TTS sandbox (M10.0)
TtsSandboxView.axaml{,.cs}     # TTS tab: sherpa-onnx + Piper LibriTTS-R + tunable radio FX (band-pass/Q/drive/squelch); auto-detects voice pack at .tmp/voices/, plays through PortAudio
```

## Yaat.ScenarioValidator — CLI tool (`tools/Yaat.ScenarioValidator/`)

Standalone console app for validating ARTCC scenario preset commands. Downloads NavData from vNAS for procedure version validation, then fetches and validates scenarios from the vNAS API or local files.

```
Program.cs                     # CLI entry: --artcc, --file, --dir, --json flags; downloads NavData for procedure checks
VnasClient.cs                  # HttpClient wrapper for vNAS data API scenario fetches + NavData download
```

## yaat-server — ASP.NET Core server (`..\yaat-server\`)

Separate repo. References Yaat.Sim via sibling project ref. Provides: SignalR comms, CRC protocol, training rooms, scenario loading, broadcast fan-out.

```
src/Yaat.Server/
  Program.cs                   # DI setup, VNAS/CIFP init, route mapping, AdminPassword validation
  YaatOptions.cs               # IOptions: AdminPassword (Yaat section); NexradOptions (Nexrad section: Enabled, RefreshMinutes)

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
    CrcVisibilityTracker.cs    # STARS/ASDEX/TowerCab visibility rules; STARS hysteresis (add at elev+100, remove at elev); AircraftState.IsVehicle excluded from STARS
    StarsLineNumberAssigner.cs # Per-room sequential line number assignment (1-99 wrap)
    StripCommandHandler.cs     # Flight strip command dispatch (all 14 canonical verbs)
    StripBroadcaster.cs        # Flight strip broadcast coordination: SignalR + CRC topic paths
    StripMutations.cs          # Stateless strip mutation helper: create/delete/amend logic
    StripCommandTranslator.cs  # Translate CRC MessagePack invocations → canonical command strings
    DtoConverter.cs            # AircraftState → CRC + training DTOs + ASDEX/strip converters
    INexradProvider.cs         # NEXRAD imagery provider contract (INexradProvider); gated on room.Weather == null (preset-weather short-circuit)
    EmptyNexradProvider.cs     # Kill-switch: returns NexradDataDto.Empty() regardless of inputs (Nexrad:Enabled=false)
    WmsNexradProvider.cs       # Default: fetches NOAA opengeo conus_cref_qcd PNG, 5-min TTL cache, cos(mid_lat) width
    NexradRefreshHostedService.cs # PeriodicTimer (Nexrad:RefreshMinutes, default 5); refreshes cache + broadcasts ReceiveNexradData per room with live weather

  Commands/
    CommandParser.cs           # Server-side canonical parsing; IsTrackCommand(), IsCoordinationCommand()
    DepartureCommandParser.cs  # Departure-specific command parsing
    GroundCommandParser.cs     # Ground operation command parsing

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
    FlightStripsConfigDto.cs   # Flight strip bay layout config (delivered on ScenarioLoaded/RoomStateDto)
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
    NexradBoundsLoader.cs      # Parses Data/Nexrad/NexradBoundingBoxes.geojson (24 ARTCCs) → per-ARTCC NexradBounds (N/S/E/W)
  Udp/UdpStubServer.cs        # UDP port 6809 stub (CRC keepalive/registration)
  Logging/FileLoggerProvider.cs
```
