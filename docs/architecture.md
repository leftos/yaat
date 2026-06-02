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
| **Ground layout parsing** | `GeoJsonParser.cs`, `IFilletArcGenerator` / `FilletGeneratorFactory`, `FilletArcGenerator.cs` + `Fillet/` (plan-then-execute edge-split), `TaxiwayGraphBuilder.cs`, `CoordinateIndex.cs` |
| **Runway exits** | `LandingPhase.cs`, `RunwayExitPhase.cs`, `ExitPreference.cs`, `AirportGroundLayout.cs` (FindExitPath) |
| **Approach procedures** | `ApproachCommandHandler.cs`, `ApproachNavigationPhase.cs`, `FinalApproachPhase.cs`, `CifpParser.cs` |
| **SID/STAR** | `DepartureClearanceHandler.cs`, `InitialClimbPhase.cs`, `CifpParser.cs`, `NavigationDatabase.cs` |
| **Radar rendering** | `RadarCanvas.cs` (input/zoom) → `RadarRenderer.cs` (drawing) → `TargetRenderer.cs` (datablocks) → `VideoMapRenderer.cs` (maps) |
| **Ground view rendering** | `GroundCanvas.cs` (input/hit-test) → `GroundRenderer.cs` (drawing, 3 layers) |
| **Command input UX** | `CommandInputController.cs` (parse pipeline) → `ArgumentSuggester.cs` (dropdown values) → `SignatureHelpState.cs` (inline hints) |
| **Weather** | `WeatherProfile.cs`, `WeatherTimeline.cs`, `WindInterpolator.cs`, `LiveWeatherService.cs`, `MetarComposer.cs`, `MetarIssuer.cs`, `SpeciCriteria.cs` |
| **Scenarios** | `ScenarioLoader.cs`, `ScenarioModels.cs`, `AircraftInitializer.cs`, `ScenarioLifecycleService.cs` (server) |
| **Snapshots/replay** | `StateSnapshotDto.cs`, `AircraftSnapshotDto.cs`, `RecordingArchive.cs`, `SimulationEngine.cs` |
| **CRC protocol** | `CrcDtos*.cs` (wire format) → `DtoConverter.cs` (translation) → `CrcBroadcastService.cs` (dispatch) → `CrcWebSocketHandler.cs` (connection) |

## Subsystem deep-dive docs

The Task Index above tells you *which files*; these docs explain *how each subsystem works* — read the relevant one before a non-trivial change. (Full annotated list with summaries in [CLAUDE.md](../CLAUDE.md) → Reference Docs.)

| Area | Deep-dive doc(s) |
|------|------------------|
| Command dispatch & per-domain handlers | [command-pipeline.md](command-pipeline.md), [command-handlers.md](command-handlers.md) |
| Command input (autocomplete / signature help) | [command-input-ux.md](command-input-ux.md) |
| Aircraft data model & `SimulationWorld` | [aircraft-data-model.md](aircraft-data-model.md) |
| Flight physics, airspeed frames & constants | [flight-physics.md](flight-physics.md) |
| Per-tick execution order | [tick-loop.md](tick-loop.md) |
| Phase system (base contract) | [phases.md](phases.md) |
| Airborne approach / pattern geometry | [approach-and-pattern-geometry.md](approach-and-pattern-geometry.md) |
| Landing rollout & runway exit | [landing-and-runway-exit.md](landing-and-runway-exit.md) |
| Ground stack (fillet / pathfinder / navigator) | [ground/README.md](ground/README.md) |
| Navigation database & route expansion | [navigation-database.md](navigation-database.md) |
| Conflict / alert / visual detection | [conflict-and-visual-detection.md](conflict-and-visual-detection.md) |
| Weather & wind | [weather-and-wind.md](weather-and-wind.md) |
| Airspace (Class B/C) & boundary crossing | [airspace-database.md](airspace-database.md) |
| Scenario loading & aircraft generation | [scenario-loading-and-generation.md](scenario-loading-and-generation.md) |
| Snapshots & replay | [snapshots-and-replay.md](snapshots-and-replay.md) |
| Solo-training evaluation & scoring | [solo-training-evaluation.md](solo-training-evaluation.md) |
| STARS/ERAM track sharing & consolidation | [track-sharing-and-consolidation.md](track-sharing-and-consolidation.md) |
| CRC display state & broadcast | [crc-display-state.md](crc-display-state.md) |
| Client↔server SignalR contract | [training-hub-contract.md](training-hub-contract.md), [server-rooms-and-hub.md](server-rooms-and-hub.md) |
| Client MainViewModel & orchestration | [client-mainviewmodel.md](client-mainviewmodel.md) |
| Radar display & rendering | [radar-rendering.md](radar-rendering.md) |
| Flight strips / vTDLS | [flight-strips.md](flight-strips.md), [vtdls.md](vtdls.md) |
| Speech (STT) & pilot speech (TTS) | [speech-recognition-pipeline.md](speech-recognition-pipeline.md), [solo-training-pilot-speech.md](solo-training-pilot-speech.md) |
| Logging | [logging.md](logging.md) |
| Test harness & fixtures | [test-harness.md](test-harness.md) |

## Integration Footguns

- **Modify `AircraftState`** → must mirror changes in `AircraftSnapshotDto.cs` + add migration in `SnapshotSchemaMigrator.cs`
- **New command type** → must add to `CanonicalCommandType` enum, `CommandRegistry` definitions, AND `CommandScheme.Default()`. Tests enforce completeness.
- **New phase** → must add `[JsonDerivedType]` attribute in `PhaseSnapshotDto.cs` for serialization
- **Modify `ControlTargets`** → check `ControlTargetsDto.cs` snapshot parity
- **Aircraft performance** → sync `AircraftProfiles.json` + `AircraftProfileDatabase` + `AircraftPerformance.cs` fallback logic

## Test Locations

- **Sim tests**: `tests/Yaat.Sim.Tests/` — commands, phases, physics, parsers, nav data
- **Client tests**: `tests/Yaat.Client.Tests/` — view model logic, command input
- **Test data**: `tests/Yaat.Sim.Tests/TestData/` — NavData.dat + `navdata-manifest.json`, FAACIFP18.gz + `cifp-manifest.json`, airport GeoJSON. Refresh pins: `tools/refresh-navdata.py`, FAA CIFP via `CifpPathResolver` at test load.
- **Shared loader**: `TestVnasData.EnsureInitialized()` — always use this, never synthetic stubs

## Root Scripts

```
AGENTS.md                         # Codex project wrapper; points Codex back to CLAUDE.md and maps Claude agents/commands/hooks to Codex behavior.
Setup-CrcEnvironment.ps1          # Adds YAAT1 + YAAT Local to CRC's DevEnvironments.json
tools/codex-yaat.ps1              # Launches Codex from the YAAT repo root and adds ..\yaat-server as an extra writable/readable directory.
tools/setup-codex.ps1             # Creates user-local Codex skill junctions and registers MCP servers without committing local state or token values.
tools/refresh-faa-airspace.ps1    # Reads vNAS training scenario primary airports by ARTCC, then downloads matching FAA AIS Class Airspace GeoJSON/Brotli.
tools/refresh-airport-airlines.ps1 # Builds Data/airport-airlines.json.br from BTS T-100 segment ZIPs, OurAirports, and OpenFlights carrier/route crosswalks.
tools/refresh-airline-fleets.py   # Parses Airfleets PDFs into Data/airline-fleets.json + .meta provenance sidecar.
tools/refresh-aircraft-display-names.py # One-shot tool that queries OpenAI for human-readable names of every ICAO type in AircraftSpecs.json and writes Data/aircraft-display-names.json + .meta sidecar. Re-run only when AircraftSpecs.json gains new types.
tools/parse_airfleets.py          # pdfplumber parser used by refresh-airline-fleets.py; maps fleet variants to ICAO Doc 8643 types.
tools/mcp/context7-stdio.ps1      # Context7 stdio adapter that reads CONTEXT7_API_KEY from the environment when Codex cannot express the custom header.
tools/mcp/exa-stdio.ps1           # Exa stdio adapter that reads EXA_API_KEY from the environment when a local authenticated Exa MCP is preferred.
```

See [installer-release.md](installer-release.md) for the Velopack packaging, auto-update, CRC install prompt, and tag-driven `release.yml` pipeline.

## Yaat.Client.Strips — WASM-clean strip layer (`src/Yaat.Client.Strips/`)

Foundation for the flight-strip view. Pure Avalonia + SignalR + CommunityToolkit.Mvvm — no Avalonia.Desktop, no Velopack, no file IO. The browser strips client (`tools/Yaat.VStrips.Web`) consumes only this assembly so its WASM publish closure stays free of Win32-only code. Yaat.Client.Core project-references Strips and exposes the shared types up to Yaat.Client.

```
Logging/
  ConsoleLineLoggerProvider.cs  # ILoggerProvider that writes one text line per entry to Console.Out (browser DevTools console). Used by Yaat.VStrips.Web/Program.cs and AppLog.InitializeForBrowser.

Services/
  StripDtos.cs                  # StripItemType / StripItemDto / StripBayContentsDto / FlightStripsStateDto / StripBayConfigDto / FlightStripsConfigDto — wire-format records for the strip surface.
  StripsTransportDtos.cs        # AccessibleFacilityDto (return of GetAccessibleFacilities) + CommandResultDto (return of every strip command) + StripsWeatherDto (narrow WeatherChanged projection — raw METARs) + StripMetarEntry (parsed station + raw, for the METAR bar). Live here because the IStripsTransport surface returns/feeds them.
  IStripsTransport.cs           # Narrow contract VStripsViewModel depends on. IsConnected + transport-state events + StripsConfigChanged + FlightStripsStateChanged + StripItemsChanged + MetarsChanged (raw METARs for the current-METAR bar) + Get/RequestStrips RPC trio. ServerConnection (Core) and BrowserStripsTransport (here) both implement it.
  BrowserStripsTransport.cs     # WASM-side IStripsTransport. Owns its own HubConnection, wires JsonHubProtocol against YaatStripsHubJsonContext only, exposes auto-join helpers (FindRoomForMyCidAsync, JoinRoomAsync, SendCommandAsync, ConnectAsync, RoomAvailableForCid event) needed by tools/Yaat.VStrips.Web/MainView. Browser-only DTO subsets (BrowserRoomInfoDto, BrowserJoinRoomResultDto, BrowserScenarioLoadedDto) downscope the wire format so the WASM bundle ships only the fields the strip view reads.
  YaatStripsHubJsonContext.cs   # Source-generated JsonSerializerContext for the strip DTO subset. Inserted into the JsonHubProtocol resolver chain by both ServerConnection (alongside YaatHubJsonContext) and BrowserStripsTransport (alone).

ViewModels/
  VStripsViewModel.cs           # Root vStrips VM; manages strip bays, items, rack state. Ctor takes IStripsTransport + send-command delegate + Func<string>? getUserInitials.
  StripItemViewModel.cs         # Per-strip observable model: flight data, annotations
  StripBayViewModel.cs          # Per-bay container: list of strips, visibility state
  StripRackViewModel.cs         # Rack (visual height) management per bay
  StripPrinterViewModel.cs      # Auto-print on aircraft departure/arrival
  VStripsCanonicalBuilder.cs    # Build canonical strip commands from UI mutations

Views/VStrips/
  VStripsView.axaml(.cs)        # Embedded vStrips UserControl
  FlightStripControl.axaml(.cs) # Custom control rendering CRC-matching strip visuals (cream cells, barcode, handwriting, offset, disconnected ✗, selection ring)
  InlineTextEditPopup.axaml(.cs) # Shared popup editor for annotations, half-strip lines, separator labels

Resources/Fonts/                # JetBrainsMono-Regular.ttf, JetBrainsMono-Bold.ttf, JetBrainsMono-Italic.ttf, JetBrainsMono-BoldItalic.ttf, OFL.txt — embedded for cross-platform monospace consistency (Inter doesn't column-align); italic variants are needed so annotation cells (FontStyle=Italic + FontWeight=Bold) render real bold on WASM where Segoe Script / Lucida Handwriting aren't available

AppBuilderExtensions.cs         # WithJetBrainsMonoFont() — registers the embedded font collection at avares://Yaat.Client.Strips/Resources/Fonts. Called by tools/Yaat.VStrips.Web/Program.cs.
```

## Yaat.Client.Tdls — WASM-clean vTDLS layer (`src/Yaat.Client.Tdls/`)

Sibling of Yaat.Client.Strips for the vTDLS (Pre-Departure Clearance) view. Same constraints: pure Avalonia + SignalR + CommunityToolkit.Mvvm — no Avalonia.Desktop, no Velopack, no file IO. Both the embedded vTDLS tab in Yaat.Client and the browser app at `tools/Yaat.VTdls.Web` consume this assembly. ProjectReferences `Yaat.Sim` + `Yaat.Client.Strips` (the latter just for the shared JetBrains Mono font registration via `WithJetBrainsMonoFont()`).

```
Services/
  TdlsDtos.cs                   # Client-side JSON mirrors of the server vTDLS DTOs: TdlsStatus enum, TdlsItemDto, TdlsItemRemovedDto, TdlsStateDto, ClearanceDto, TdlsConfigDto + nested SID/transition/value records. Property names match the server one-for-one so System.Text.Json round-trips without converters.
  ITdlsTransport.cs             # Narrow contract VTdlsViewModel depends on. IsConnected + transport-state events + TdlsItemChanged/Removed/StateChanged broadcasts + GetAccessibleTdlsFacilities/GetTdlsConfigForFacility/RequestFullTdlsState RPC trio. ServerConnection (Core) and BrowserTdlsTransport (here) both implement it.
  BrowserTdlsTransport.cs       # WASM-side ITdlsTransport. Owns its own HubConnection, wires JsonHubProtocol against YaatTdlsHubJsonContext only, exposes auto-join helpers (FindRoomForMyCidAsync, JoinRoomAsync, SendCommandAsync, ConnectAsync, RoomAvailableForCid event) needed by tools/Yaat.VTdls.Web/MainView. Browser-only DTO subsets (BrowserTdlsRoomInfoDto, BrowserTdlsJoinRoomResultDto) downscope the wire format.
  YaatTdlsHubJsonContext.cs     # Source-generated JsonSerializerContext for the TDLS DTO subset. Inserted into the JsonHubProtocol resolver chain by both ServerConnection (alongside YaatHubJsonContext + YaatStripsHubJsonContext) and BrowserTdlsTransport (alone).

ViewModels/
  VTdlsViewModel.cs             # Root vTDLS VM; reconciles DCL (Pending) and PDC (Sent+Wilco) lists from broadcast events; surfaces accessible-facility list + Switch/Refresh. Ctor takes ITdlsTransport + send-command delegate + Func<string>? getUserInitials.
  TdlsItemViewModel.cs          # Per-item observable: AircraftId, Status, Sequence, timestamps, SentPayload. Instance identity preserved across reconciles so Avalonia bindings stay stable.
  TdlsFlightPlanEditorViewModel.cs # Nine-field editor; wraps a working ClearanceDto, exposes per-field dropdowns from the facility's TdlsConfigDto, applies SID+transition defaults on selection, gates Send button on mandatory-field completion.
  VTdlsCanonicalBuilder.cs      # Build canonical TDLS commands (TDLSQ / TDLSS Expect|Sid|... | LocalInfo / TDLSW / TDLSDUMP) from UI gestures.

Views/VTdls/
  VTdlsView.axaml(.cs)          # Embedded vTDLS UserControl. Layout mirrors upstream tdls.virtualnas.net: black header chrome, DCL list on top (full width, WrapPanel Vertical column wrap), PDC + decorative empty CPDLC split 50/50 below, flight-plan editor docks at bottom when a DCL item is selected, footer with CLEARANCE TYPE status + Zulu clock. Key bindings: F4 Dump, F10 close editor, F12 Send.
```

## Yaat.Client.Core — Shared library (`src/Yaat.Client.Core/`)

Code referenced by Yaat.Client that needs Avalonia.Desktop, Velopack, or file-system access. No LM-Kit, PortAudio, or SharpHook dependencies. Project-references Yaat.Client.Strips for the strip layer. Namespace stays `Yaat.Client.*`.

```
Logging/
  AppLog.cs                     # Static logger factory; Initialize(logFileName) called by each desktop app's Program.cs. Wraps SimLog. WASM has its own inline init in Program.cs that wires SimLog directly.
  FileLoggerProvider.cs         # Writes to YaatPaths.AppDataRoot/<logFileName> (yaat-client.log)

Services/
  ServerConnection.cs           # SignalR client to /hubs/training (JSON). Implements IStripsTransport from Strips. Inline DTOs for everything outside the strip surface (rooms, aircraft, weather, CRC, recordings). Includes PilotTransmissionBroadcastDto + PilotTransmissionReceived for solo-training audio.
  UserPreferences.cs            # JSON to YaatPaths.AppDataRoot/preferences.json (%LOCALAPPDATA%/yaat/). Stores PilotVoiceEnabled/Volume/RadioFxEnabled, default off.
  UpdateService.cs              # Velopack auto-updater. Constructor takes channel? — null for Yaat.Client (default platform channel).
  YaatHubJsonContext.cs         # Source-generated JsonSerializerContext for the broader DTO surface (room state, aircraft, weather, CRC, scenarios). Strip DTOs live in YaatStripsHubJsonContext (Strips); both contexts insert into the same resolver chain.
  WindowGeometryHelper.cs       # Save/restore window position+size+topmost; composes WindowSystemMenuHelper + WindowNativeMenuHelper for cross-platform always-on-top discoverability
  WindowSystemMenuHelper.cs     # Windows-only: injects "Always on Top" into the title-bar system menu via WM_SYSCOMMAND + SetWindowSubclass
  WindowNativeMenuHelper.cs     # macOS-only: adds "Window → Always on Top" to the menu bar via Avalonia NativeMenu
  KeybindHelper.cs              # Keyboard shortcut resolution
  MacroDefinition.cs            # Macro model: Name, Expansion, ParameterNames
  GroundColorScheme.cs          # Theme/color scheme for strips
  TerminalEntry.cs              # Terminal/radio log entry (Kind: Command/Response/System/Say)

Models/
  TerminalColorScheme.cs        # Operator-tunable per-Kind terminal foreground colors (Command/Response/System/Say/PilotSpeech/Warning/Error/Chat); defaults match the legacy hard-coded scheme

ViewModels/
  ConnectViewModel.cs           # Room/identity connection flow

Views/
  ConnectWindow.axaml.cs        # Server/room/identity entry dialog
  VStrips/VStripsViewWindow.axaml.cs # Pop-out window wrapper for VStripsView. Stays in Core because it depends on UserPreferences + WindowGeometryHelper + KeybindHelper; only the desktop hosts open this Window.
  VTdls/VTdlsViewWindow.axaml.cs # Pop-out window wrapper for VTdlsView. Same shape as VStripsViewWindow: per-facility geometry key (`VTdlsView:{facilityId}`), first-time topmost inherited from the global `VTdlsView` default, AlwaysOnTop hotkey from UserPreferences.
```

## Yaat.Client — Avalonia desktop app (`src/Yaat.Client/`)

```
Models/
  AircraftModel.cs              # ObservableObject wrapping AircraftDto; computed displays; FromDto/UpdateFromDto
  AircraftSpeechBubble.cs       # Per-aircraft speech bubble model for opt-in SAY/pilot (green) and WARN (amber) overlays on Radar/Ground views (text, severity, user-scaled duration, dismiss state).
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
  NaturalCommandNormalizer.cs   # Shared transcript-to-canonical normalizer for solo-mode typed natural-language ATC input
  PilotVoicePack.cs             # Shared Piper voice-pack discovery/validation for installer and sherpa-onnx playback.
  PiperVoiceInstaller.cs        # Settings-driven Piper voice-pack downloader/extractor into YaatPaths app data.
  PilotVoiceService.cs          # Off-by-default solo-training pilot voice queue. Consumes PilotTransmissionBroadcastDto FIFO; synthesizes with sherpa-onnx/Piper, applies NAudio.Dsp radio FX, plays through PortAudio.
  PilotSpeechAlertService.cs    # Optional RPO-mode pilot-speech ding for TerminalEntryKind.PilotSpeech; generated in code and played through PortAudio.
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
  UserPreferences.cs            # JSON to %LOCALAPPDATA%/yaat/preferences.json (incl. macros and favorite command scope/panel-grid/spacer metadata)
  MenuGroup.cs                  # Enum of context menu groups (Heading, Altitude, Speed, Tower, etc.)
  ContextMenuProfile.cs         # Record: Primary/Secondary/Hidden menu groups for a phase
  ContextMenuProfileService.cs  # Static: maps phase name + isOnGround → ContextMenuProfile
  RelativeTrafficActions.cs     # Static pure gating for selected→right-clicked traffic menu items (RTIS/FOLLOW in radar, GIVEWAY/FOLLOWG on ground): HasRelativeContext, ShouldOfferFollow (airborne + LastReportedTrafficCallsign match), ShouldOfferGroundActions
  BuildInfo.cs                  # Static: version (from AssemblyInformationalVersion) + release-vs-dev detection (VelopackLocator.Current); used by title bar, About window, and startup log line
  DocLinks.cs                   # Static: GitHub URLs for user-facing docs (README/USER_GUIDE/COMMANDS/CHANGELOG/issues), pinned to release tag for installed builds, main for dev
  UrlLauncher.cs                # Static: opens HTTPS URLs in OS default browser (Process.Start with UseShellExecute)
  CallsignPrefixResolver.cs     # Pure resolver: partial callsign prefix → unique aircraft or list of matching candidates. Used by MainViewModel.SendCommandAsync to disambiguate `N12` when multiple aircraft match.
  CommandErrorFormatter.cs      # Pure formatter for unrecognized-command errors: when the leading token is a known callsign (partial/complete), names the verb after it instead of blaming the callsign. Used by MainViewModel.SendCommandAsync.
  WindowProfileService.cs       # Saves/restores named window arrangements: per-window geometry + dock/pop-out state + DataGrid column layout. Persists to UserPreferences; surfaced via View → Window Profiles. StagePreferencesPartial applies a chosen subset for the Copy View Settings dialog.
  ViewSettingsCopyCatalog.cs    # Shared catalog of Ground/Radar per-scenario view-setting groups (Key/Label/Describe/AreEqual/Copy). Single source of truth for both CopyViewSettingsDialog's diff rows and MainWindow's merge-on-copy.
  ShownRouteBuilder.cs          # Pure builder for the radar "Show flight path" overlay. Produces a multi-segment path: published route + procedure vector tail (5 nm arrow off the last STAR fix on FM/VM/VA legs) + the expected approach line (IAF/transition → FAF → threshold, FAC extended back 5 nm when no transition is named).

ViewModels/
  MainViewModel.cs              # Root VM; SendCommandAsync pipeline; nav data init
  MainViewModel.Rooms.cs        # Partial: room lifecycle (create/join/leave), aircraft assignments
  MainViewModel.Aircraft.cs     # Partial: aircraft management (spawn/delete/update), terminal broadcast handling, and PilotTransmissionBroadcast gate to PilotVoiceService.
  MainViewModel.Scenario.cs     # Partial: scenario load/unload
  MainViewModel.ArrivalGenerators.cs # Partial: live arrival-generator editing (open editor window, push edits to sim, Save As)
  MainViewModel.HoldForRelease.cs # Partial: hold-for-release rundown mirror + REL release commands (HeldDeparturesChanged handler, RoomStateDto.Rundown seed)
  MainViewModel.Timers.cs       # Partial: TIMER countdown mirror + cancel command (TimersChanged handler, RoomStateDto.Timers seed, command-bar timers panel)
  MainViewModel.Weather.cs      # Partial: weather load/clear commands + WeatherChanged handler; retains raw METARs (Metars) for the METAR window
  MainViewModel.Controllers.cs  # Partial: online-controller list (OnlineControllers + CRC-grouped ControllerGroups) for the Controllers tab; refresh via GetOnlineControllers, re-fetched on CRC membership + scenario load/unload
  MainViewModel.Favorites.cs    # Partial: favorite commands (quick-access bar/panel, global/scenario/airport scope, ground overrides, blank spacers)
  MainViewModel.Timeline.cs     # Partial: rewind timeline markers — color-coded finding ticks (red Safety, amber Warning, blue Coach) + grey command ticks; periodic refresh, click-to-rewind, hover details, per-aircraft filter from the Session Report Aircraft tab.
  TimelineMarkerVm.cs           # Per-marker view-model: timestamp, kind, severity, title, callsign, canonical command (commands only).
  AutoClearedToLandSync.cs      # Subscribes to UserPreferences.AutoClearedToLand changes; pushes the new value to every aircraft (local + room-broadcast) so the toggle takes effect mid-session without a scenario reload.
  GroundViewModel.cs            # Ground view; loads layout, A* pathfinding, commands
  RadarViewModel.cs             # Radar view; video map loading, toggle items, DCB, persistence
  SettingsViewModel.cs          # Settings tabs: identity/admin/sim/audio/speech/visuals/keybinds; includes STT/TTS model download flows.
  WeatherPeriodViewModel.cs     # Per-period VM: wind layers, METARs, precipitation, start/transition minutes
  WeatherTimelineEditorViewModel.cs  # Timeline editor VM: period list, BuildJson (v1 if 1 period, v2 if 2+), FromJson
  ArrivalGeneratorsEditorViewModel.cs # Arrival generator editor VM: row list, Apply (push to sim), Save As (write scenario JSON)
  GeneratorRowViewModel.cs      # Per-row VM for ArrivalGeneratorsEditor: airport/runway/airline/type/rate/etc. fields
  *Converter.cs                 # IValueConverters for UI bindings (Dock, Pause, SuggestionKindColor, SignatureHelp)

Views/
  MainWindow.axaml.cs           # Tab layout (DataGrid/Ground/Radar); room bar; pop-out management
  CommandInputView.axaml.cs     # Keyboard: Esc/Up/Down/Tab/Enter for suggestions/history
  FavoritesBarView.axaml.cs     # Favorite command buttons bar and tabbed panel content (click/ctrl+click/right-click)
  FavoritesPanelWindow.axaml.cs # Pop-out favorite commands panel with saved geometry
  ControllersView.axaml.cs      # Controllers tab content: CRC-style facility-grouped list (handoff id / position name / freq) over MainViewModel.ControllerGroups
  ControllersWindow.axaml.cs    # Pop-out host for ControllersView (View > Pop Out Controllers)
  MetarView.axaml.cs            # METAR tab content: per-airport METAR list over MainViewModel.Metars
  MetarWindow.axaml.cs          # Pop-out host for MetarView (View > Pop Out METAR)
  AlwaysOnTopContextMenu.cs     # Shared right-click "Always On Top" toggle attached to every pop-out window
  FavoritesContextMenu.cs       # Builds the Favorite Commands submenu attached to aircraft right-click menus (list/ground/radar)
  FavoritesContextMenu.cs       # Builds the Favorite Commands submenu attached to aircraft right-click menus (list/ground/radar)
  FavoritesContextMenuModel.cs  # Pure model behind FavoritesContextMenu: resolves active favorites against the clicked aircraft for headless tests
  DataGridView.axaml.cs         # Aircraft data grid (extracted from MainWindow)
  DataGridView.ContextMenu.cs   # Partial: phase-aware right-click menu builders
  DataGridWindow.axaml.cs       # Pop-out data grid window
  TerminalPanelView.axaml.cs    # Auto-scroll with user-scroll detection
  TerminalWindow.axaml.cs       # Pop-out terminal (shares MainViewModel)
  SettingsWindow.axaml.cs       # Modal settings (Identity/Scenarios/Display/Colors/Commands/Macros/Audio/Speech/Advanced tabs)
  MacroImportWindow.axaml.cs    # Macro import selection dialog
  LoadWeatherWindow.axaml.cs    # Weather profile picker modal (folder scan, name + layer count)
  WeatherEditorControl.axaml.cs # Per-period weather editing UserControl (precipitation, wind layers grid, METARs)
  AboutWindow.axaml.cs          # Help → About dialog: version, build kind, .NET runtime, log path, GitHub link
  WeatherTimelineEditorWindow.axaml.cs  # Timeline editor: period list (left) + WeatherEditorControl (right); v1/v2 auto-format on save
  ArrivalGeneratorsEditorWindow.axaml.cs # Live arrival-generator editor: row grid + Apply (push to sim) / Save As (new scenario JSON)
  ScenarioValidationWindow.axaml.cs  # Batch scenario validation report (DataGrid of failures, copy report)
  SessionReportWindow.axaml.cs  # Live solo-training session report: score, coaching notes, separation timeline, approach/runway grids, per-aircraft debrief tab with "Show on Timeline" cross-link
  TimelineMarkerCanvas.cs       # SkiaSharp canvas overlaying TimelineMarkerVm ticks above the rewind scrub slider; finding markers above, command markers slightly lower; supports hit-testing and hover tooltips.
  ManageWindowProfilesDialog.axaml(.cs)  # View → Window Profiles dialog: list saved profiles, switch, rename, delete
  SaveWindowProfileDialog.axaml(.cs)     # Name-entry dialog for saving the current window arrangement as a new profile
  CopyViewSettingsDialog.axaml(.cs)      # View → Copy View Settings dialog: source picker (scenario or window profile), grouped Current-vs-Source diff with per-section checkboxes, airport-mismatch warning. Returns selected keys for MainWindow to apply via ViewSettingsCopyCatalog / WindowProfileService.
  CommandFlyout.cs              # Floating focused command-entry popup opened from aircraft right-click menus (radar/ground/flight list)
  ContextMenuExtensions.cs      # Helpers for building Avalonia context menus (right-click submenus, command items)
  HoldShortMenuHelper.cs        # Shared resolver: held runway from the "Holding Short {rwy}" phase, used by ground-map + aircraft-list cross/LUAW menu items
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
  RadarDatablockLayout.cs       # Datablock line/field layout (EuroScope tag + standard datablock geometry shared with renderer + click hit-testing)
  VideoMapRenderer.cs           # Video map line/label rendering
  TargetRenderer.cs             # Aircraft target/datablock rendering
  Flyouts/
    FlyoutAppearance.cs         # Shared visual styling for tag flyouts (altitude, speed, runway, scratchpad popups)
```

## Yaat.VStrips.Web — Browser strip client (`tools/Yaat.VStrips.Web/`)

WebAssembly Avalonia client for flight strips. Hosted by yaat-server at `/vstrips/` so users open it in any browser without an install. References only Yaat.Client.Strips (no Avalonia.Desktop, no Velopack, no file IO) so the WASM publish closure stays small. Identity (CID, initials, ARTCC) flows in via URL query — first-time visitors fill a landing form that redirects with the params filled in.

```
Program.cs                       # Entry point. Wires SimLog to ConsoleLineLoggerProvider (browser DevTools console). Stores window.location.search + window.location.origin on App so MainView can decide live-connect vs. offline spike.
App.axaml(.cs)                   # XAML app root. Holds LocationSearch/LocationOrigin static strings populated by Program.Main.
MainView.axaml(.cs)              # Root view. Hosts VStripsView, handles auto-join via BrowserStripsTransport, surfaces the missing-identity landing form.
wwwroot/index.html               # WASM host page; loaded by yaat-server's static-file middleware at /vstrips/.
wwwroot/main.js                  # Boot script — passes window.location.search + origin into the WASM Main args.
wwwroot/app.css                  # Page chrome (status footer, landing form).
runtimeconfig.template.json      # net10.0-browser runtime config (JsonSerializerIsReflectionEnabledByDefault=true so SignalR JoinRoom works in WASM).
test/smoke.mjs                   # Headless WASM smoke test (Playwright).
test/live.mjs                    # Live-server smoke test against a running yaat-server instance.
```

## Yaat.VTdls.Web — Browser vTDLS client (`tools/Yaat.VTdls.Web/`)

WebAssembly Avalonia client for the vTDLS view. Hosted by yaat-server at `/vtdls/` (mapped in `YaatHost`). Mirrors the VStrips.Web shape: references Yaat.Client.Tdls + Yaat.Client.Strips (for the shared JetBrains Mono font registration), no Avalonia.Desktop / Velopack / file IO. Identity (CID, initials, ARTCC) flows in via URL query — first-time visitors fill a landing form that redirects with the params filled in.

```
Program.cs                       # Entry point. Wires SimLog to ConsoleLineLoggerProvider. Stores window.location.search + origin on App.
App.axaml(.cs)                   # XAML app root with the Light theme variant (upstream vTDLS is light-themed) + SubtleTextBrush/MonoFont resources.
MainView.axaml(.cs)              # Root view. Hosts VTdlsView, handles auto-join via BrowserTdlsTransport, calls RefreshAccessibleFacilities + SwitchFacility on the first facility after JoinRoom.
wwwroot/index.html               # WASM host page; landing form (DOM-only, no innerHTML); gates the WASM boot on identity params; localStorage key `yaat-vtdls-identity`.
wwwroot/main.js                  # Boot script.
wwwroot/app.css                  # Page chrome (status footer + landing form).
runtimeconfig.template.json      # net10.0-browser runtime config.
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
Callsign.cs                    # Static IsValid(string?): regex ^[A-Z0-9\-]{1,7}$. Boundary check used by STARS DA/VP/FP creation
                               # to reject typos like "*T <fix>" before they create stray flight plans.
FlightPlanAltitude.cs          # Parser + formatter for the CRC altitude grammar used in FP forms and STARS DA/VP:
                               # `VFR` (rules-only), `VFR/045` / `OTP/120` (rules + altitude), `045` (IFR + altitude), blank.
                               # Returns FlightRules + AltitudeFeet?; round-trips through CRC FP edits without synthesizing fields.
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
ClientKind.cs                  # Static constants: Main / VStrips / VTdls identify which YAAT app a SignalR client is running.
                               # Sent on CreateRoom/JoinRoom; stored in RoomMember.Kind; DisplaySuffix appends e.g.
                               # " (Flight Strips)" / " (vTDLS)" to terminal-broadcast verbs ("joined the room (vTDLS)").
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
AircraftStatusDescriber.cs     # Pure AircraftState→text projection for the Aircraft List "Info" column.
                               # Describe(AircraftState) / Describe(AircraftStatusView); server computes once
                               # per broadcast → AircraftDto.SmartStatus (client just displays it), TickRecorder
                               # calls it too. One implementation so all surfaces agree.
AircraftPerformance.cs         # Unified perf API: profile-first with category fallback. Altitude-banded
                               # climb/descent rates, Mach-aware speeds, 91.117 waiver support
GroundConflictDetector.cs      # Static pairwise ground proximity → SpeedLimit overrides.
                               # Single-pass pair classifier (SameEdgeTrailing/SameEdgeHeadOn/
                               # Converging/Crossing/Pushback/Stationary). Honors Ground.Hold
                               # (HoldPosition or GiveWay) via IsImmobile predicate for
                               # parked-obstacle classification; self-pin recovery for
                               # un-held but conflict-pinned aircraft. DebugSink logs the
                               # specific hold kind so the controller GIVEWAY relationship is
                               # observable ("ControllerGiveWay A→B" pair line).
HoldDirective.cs               # Structured ground-hold directive: HoldKind { HoldPosition,
                               # GiveWay } + optional YieldTarget callsign. Replaces the
                               # historical IsHeld+GiveWayTarget pair on AircraftGroundOps.
                               # Construct via HoldDirective.HoldPosition or
                               # HoldDirective.GiveWay(target). IsGiveWayFor(callsign)
                               # tests the pair relationship for the conflict detector.
GiveWayConstants.cs            # Auto-release tuning for direct GIVEWAY holds (FlightPhysics.UpdateGiveWayResume):
                               # safety-timeout (300s), target-stationary threshold (30s),
                               # stationary speed threshold, timeout clear-distance. Direct holds
                               # only — deferred BEHIND keeps pure-geometry release.
ConflictAlertDetector.cs       # Static STARS CA detection: 3nm/1000ft thresholds, 5s extrapolation, hysteresis, approach suppression
Training/SoloTrainingEvaluator.cs  # Solo-training scorecard: FAA separation, wake, runway-operation separation, structured traffic-advisory/safety-alert/wake-advisory/field-proof events, ARTCC WakeDirectives, Class C outer-area/no-minima advisory scoring, active timeline, report buckets
Training/AircraftCompletion.cs     # Per-aircraft lifecycle stamps: spawn time, completion time, completion reason (Landed / Handed off / Dropped / Departed), filed route + operation classification used by the Session Report Aircraft tab.
Training/AircraftDebriefCoachingTemplates.cs  # Pure templates: one-line coaching note per completion reason + severity profile, consumed when aggregating per-aircraft debrief blocks from existing findings.
WeatherProfile.cs              # WeatherProfile + WindLayer; ATCTrainer-compatible JSON; layers sorted by altitude on load
                               # GetWeatherForAirport: cached METAR lookup via MetarInterpolator
WeatherPeriod.cs               # Single weather period in a v2 timeline: startMinutes, transitionMinutes, windLayers, metars, precipitation
WeatherTimeline.cs             # Time-based weather evolution: list of WeatherPeriods; GetWeatherAt(elapsedSeconds) interpolates wind
                               # layers during transitions (N/E vector decomposition); METARs/precipitation snap at transition start
                               # HasMeaningfulChange: rate-limits broadcasts (direction >1°, speed >0.5kt tolerance)
WeatherTimelineParser.cs       # Static v1/v2 auto-detection parser: checks for "periods" array → WeatherTimeline, else → WeatherProfile
                               # Returns WeatherParseResult discriminated union (Timeline | Profile | Error)
WindInterpolator.cs            # Static wind utilities: GetWindAt, GetWindComponents (vector lerp through 0/360),
                               # IasToTas/TasToIas/MachToIas/IasToMach (ISA compressible-flow equations), ComputeWindCorrectionAngle
MetarParser.cs                 # Static METAR parsing: station ID, ceiling (BKN/OVC), visibility (SM); ParsedMetar record
MetarInterpolator.cs           # Static: GetWeatherForAirport — exact station match then IDW interpolation within 50nm
ReportedConditions.cs          # Snapshot of modeled surface weather for one station (true wind, vis, sky/ceiling, altimeter, precip)
MetarComposer.cs               # Static: reconstructs a reported METAR by patching dynamic groups into a base METAR (AIM 7-1-28)
SpeciCriteria.cs               # Static: SPECI decision vs last issued (wind shift, vis/ceiling crossings, precip) — AIM TBL 7-1-1
MetarIssuer.cs                 # Per-room state machine: routine METAR at :53 + SPECI on change; freezes conditions at issuance
WindsAloftParser.cs            # Static: parses FAA FD fixed-width text → StationWinds[]; DecodeWind handles 100+kt, light/variable
MagneticDeclination.cs         # Static: NOAA World Magnetic Model (WMM) declination via the Geo library; TrueToMagnetic/MagneticToTrue conversion
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
                               # HasReported* flag and pushes the in-sight pilot readback through PilotResponder.RouteRpoSayReadback:
                               # RPO+RpoShowPilotSpeech → PendingPilotSpeech (green spelled-out via BuildTrafficInSight/BuildFieldInSight),
                               # solo → typed PendingPilotTransmissions for delayed SAY/audio; RPO default → PendingPilotReadbacks only
                               # (SAY channel, "Have <target> in sight" / "Have the field in sight").
                               # Silently drops observations whose target has left the sim or whose
                               # destination is no longer lookupable.
WakeTurbulenceData.cs          # Static: WTG code lookup from AircraftSpecs.json; TrafficDetectionRangeNm by WTG (A=15nm to F=3nm)

# Track operations
TrackOwner.cs                  # Record: Callsign, FacilityId, Subset, SectorId, OwnerType
TrackOwnerType.cs              # Enum: Other, Eram, Stars, Caats, Atop
Tcp.cs                         # Record: Subset, SectorId, Id, ParentTcpId
StarsPointout.cs / StarsPointoutStatus.cs  # Pointout state
StarsDatablockClassifier.cs    # Pure: projects a track's STARS view for a TCP (color White/Green/Yellow/Cyan, level LDB/PDB/FDB, leader dir); mirrors CRC DisplayElementTracks. Used by DtoConverter to fill AircraftStateDto.Student* for the instructor radar
EramPointoutState.cs           # Per-aircraft ERAM pointout record (mirrors vatsim-server-rs radar_state::PointoutState)
                               # Round-tripped via the Eram satellite (AircraftEramStateDto.Pointouts/ForcedPointoutsTo), like other serialized ERAM state

# Coordination
CoordinationChannel.cs         # Channel config: ListId, Title, SendingTcps, Receivers, Items
CoordinationItem.cs            # Single coordination entry: status lifecycle, expiry, origin TCP
StarsCoordinationStatus.cs     # Enum: Unsent→Unacknowledged→Acknowledged→Recalled→Expiry→Void

# Commands/
Commands/CanonicalCommandType.cs    # Enum of every command type
Commands/ParsedCommand.cs           # Discriminated union records; CompoundCommand/ParsedBlock/BlockCondition; includes server-only commands (DEL, PAUSE, ADD, etc.)
Commands/CommandDefinition.cs       # ArgMode enum, CommandDefinition/CommandOverload/CompoundModifier records; command metadata includes solo-pilot unable eligibility
Commands/CommandRegistry.cs         # Single source of truth: CommandDefinition per type (label, category, aliases, overloads, modifiers, pilot-unable eligibility)
Commands/CommandScheme.cs           # CanonicalCommandType → CommandPattern (aliases only); Default() from registry
Commands/CommandSchemeParser.cs     # Parse/ParseCompound (;/, syntax); ExpandSpeedUntil; concatenation fallback; ToCanonical()
Commands/CommandSignature.cs        # Records: CommandParameter, CommandSignature, CommandSignatureSet; FromDefinition factory
Commands/CommandDispatcher.cs       # Static: DispatchCompound (phase interaction), ApplyCommand (thin routing switch),
                                    # TryApplyTowerCommand, queue infrastructure, condition conversion, shared utilities
                                    # ClearConflictingBlocks: dimension-aware selective queue clearing
                                    # SplitBlockNonConflicting: splits mixed-dimension blocks on partial conflicts
Commands/DispatchContext.cs         # Record: GroundLayout, Rng, Weather, FindAircraft/ListAircraft, ValidateDctFixes, AutoCrossRunway
                                    # Bundled at SimulationEngine/RoomEngine call sites; threaded through all internal helpers
Commands/FlightCommandHandler.cs    # Heading, altitude, speed, squawk, direct-to, warp, wait/say commands
Commands/NavigationCommandHandler.cs # Multi-block navigation: JRADO/JRADI, depart/cross fix, JARR STAR resolution,
                                    # JAWY airway intercept, CVIA/DVIA (DVIA SPD fix), JFAC, holding pattern, RFIS/RTIS/SAFAL, list approaches
Commands/CommandDescriber.cs        # Static: DescribeCommand, DescribeNatural, classification helpers
                                    # GetDimension, GetCommandDimension, GetCompoundDimensions for queue clearing
Commands/TrafficAdvisoryMatcher.cs  # Shared structured RTIS/SAFAL target matching: clock, whole-mile distance, direction, type, altitude
Commands/AltitudeResolver.cs        # Plain int or AGL format → feet MSL
Commands/NodeRefToken.cs            # Parses user-typed `#<id>` node-reference tokens used in TAXI clearances; co-located with the parser since the token format is grammar, not routing
Commands/RouteChainer.cs            # After DCT to on-route fix, appends remaining route fixes
Commands/ApproachCommandHandler.cs  # Approach clearance logic (CAPP/JAPP/PTAC/CAPPSI/JAPPSI/CAPPF/JAPPF/PTACF forced variants/CVA visual approach); RF/AF arc expansion in BuildApproachFixes
Commands/DepartureClearanceHandler.cs  # Departure clearance + CIFP SID resolution, CancelTakeoff, ClearedTakeoffPresent (CTOPP)
Commands/GroundCommandHandler.cs    # Ground operation command logic (taxi, pushback, hold short)
Commands/TrackEngine.cs             # Pure domain logic for STARS track ops: Track, Drop, Handoff, Accept, Cancel, PointOut, Acknowledge,
                                    # RejectPointout, RetractPointout, Scratchpad1/2, TempAlt, Cruise, PilotReportedAlt,
                                    # InhibitConflictAlert, LeaderDirection, JRing, Cone. All methods mutate AircraftState directly.
                                    # Dispatch(parsed, ac, identity, scenario, ?artccConfig): top-level switch routing any track ParsedCommand to
                                    # the right HandleX/ApplyX. Server's TrackCommandHandler delegates to this for the pure cases; replay applier uses it.
Commands/TrackResolver.cs           # AS-prefix extraction (e.g. "AS 3Y ACCEPT" → "ACCEPT" + "3Y" override), scenario-first TCP→TrackOwner
                                    # resolution with optional ARTCC-config fallback, owner→TCP lookup. Shared by yaat-server's live track path
                                    # and Sim's replay applier.
Commands/PatternCommandHandler.cs   # Pattern operation command logic (extend, rock wings, GoAround, CTL, sequence, etc.); EF loop detection via turn-arc geometry
Commands/StripCommandHandler.cs     # Flight strip CRUD (STRIP, SCAN, STRIPD, STRIPO, AN, HSC, HSA, HSD, HSM, HSO, HSS, SEP, SEPD, BLANK, BLANKD); dispatches to StripMutations
Commands/FlightPlanCommandHandler.cs # Flight-plan amendment validation: TryChangeDestination resolves FAA/ICAO airport input via NavigationDatabase.TryResolveAirport,
                                    # writes canonical ICAO to FlightPlan.Destination, rejects unknown airports. Called from yaat-server's RoomEngine APT handler.

# Phases/ — clearance-gated behavior
Phases/Phase.cs                # Abstract: OnStart/OnTick/OnEnd, CanAcceptCommand→CommandAcceptance, OnCommandAccepted (release internal state machines on accept), ManagesSpeed (suppresses auto schedule)
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
Phases/PhaseClearSummary.cs    # Builds short label ("pattern to RWY 28R", "approach to RWY 28R", or phase Name) for the cancellation warning surfaced when a command clears the active phase chain

# Phases/Tower/
LineUpPhase.cs                 # State-machine lineup via LineUpGeometry: Aligned (straight → fillet arc → rollout) or Pivot (SlowTurn → perpendicular straight → SlowTurn → rollout) chosen by waste-straight vs remaining-runway. Faulted stays stopped (user recovers via TAXI / CANCEL CLEARANCE)
LineUpGeometry.cs              # Pure geometry: classifies aircraft pose as Aligned, Pivot, or Fault; builds LineUpPathPlan with closed-form primitives (nose-out, arc, pivot turns, straight, rollout). Pivot fallback used when straight path would waste >20% of remaining runway (issue #142)
LineUpArcPlayback.cs           # Closed-form circular-arc playback (invariant I2: position and heading are functions of a single scalar)
LinedUpAndWaitingPhase.cs      # Hold at threshold; await ClearedForTakeoff
TakeoffPhase.cs                # Ground roll→Vr→400ft AGL
InitialClimbPhase.cs           # Climb to 1500ft AGL or assigned; activates SID via mode; RV SID heading hold until handoff+5s
FinalApproachPhase.cs          # Glideslope; no-clearance warning/go-around at DA/MDA when published, otherwise 200ft AGL; illegal intercept check (§5-9-1)
LandingPhase.cs                # Flare→touchdown→rollout; continuous exit evaluation (resolve→brake→commit/abandon→relax preference); LAHSO-aware
RunwayHoldingPhase.cs          # LAHSO: hold at 0kts on runway after landing; clearance-gated (RunwayCrossing)
GoAroundPhase.cs               # TOGA, runway heading, climb 2000ft AGL (pattern alt for VFR/pattern traffic)
TouchAndGoPhase.cs / StopAndGoPhase.cs / LowApproachPhase.cs
MakeTurnPhase.cs               # 360/270 turn tracking (cumulative degrees, exit heading); clones pattern phase for 360s; slows to holding speed then resumes
STurnPhase.cs                  # S-turn phase: alternating 30° deviations from final heading for spacing
VfrHoldPhase.cs                # VFR hold: orbit at current position (HPP) or navigate-then-orbit at fix (HFIX); slows to holding speed then resumes
ManeuverSpeedController.cs     # Shared holding-speed slow-down + resume for tight maneuvers (MakeTurn/VfrHold/STurn)
AirspaceBoundaryHoldPhase.cs   # Solo-training VFR boundary hold outside Class B/C until the Bravo clearance or two-way-comms gate is satisfied.

# Phases/Approach/
ApproachNavigationPhase.cs     # Navigate through CIFP fix sequence (IAF→IF→FAF) with alt/speed restrictions + next-fix speed look-ahead
InterceptCoursePhase.cs        # Fly current heading until intercepting final approach course; detects bust-through (sign flip or 180s timeout) and notifies RPO. ForcedIntercept (PTACF, CAPPF implied-PTAC) bypasses the 30° capture gate — forces capture on steep cuts, overshoots expected
HoldingPatternPhase.cs         # AIM 5-3-8 holding with entry determination; MaxCircuits for hold-in-lieu
ProcedureTurnPhase.cs          # AIM 5-4-9 procedure turn (PI leg): outbound on FAC reciprocal → 45° offset → 180° turn back → intercept inbound. Engaged by CAPP when DCT matches PT anchor or intercept angle > 90°
ApproachClearance.cs           # Record on PhaseList storing active approach state + pre-built MAP fixes

# Phases/Pattern/
UpwindPhase / CrosswindPhase / DownwindPhase / BasePhase / MidfieldCrossingPhase / PatternEntryPhase
PatternLateralOffset.cs        # OFL / OFR (OFFSETL / OFFSETR) state holder. One-shot lateral dogleg + parallel hold on the current pattern leg (upwind/crosswind/downwind/base). Default 0.5 NM, range 0.1–1.5 NM. State lives on the active phase only; discards on the next leg transition.
VfrFollowPhase.cs              # VFR FOLLOW command phase. Pursues lead (heading + speed with spacing correction, altitude untouched); auto-joins lead's pattern when within 3 nm of the downwind abeam point AND within 5 nm of the lead AND on the correct side of the runway. Runaway-distance cancel after 30 s of growing gap. Spacing uses wider free-flight distances (1.5/2.0/2.5 nm) vs pattern-tight (1.0/1.5/2.0 nm)
AirborneFollowHelper.cs        # Shared spacing math. GetAdjustedSpeed for pattern phases (ctx-based) + AdjustedFreeFlightSpeed for VfrFollowPhase (wider margins). Auto-cancels with warning if follower can't maintain separation at min speed

# Phases/Ground/
AtParkingPhase / PushbackPhase / PushbackToSpotPhase / TaxiingPhase / HoldingShortPhase
CrossingRunwayPhase / HoldingAfterExitPhase / FollowingPhase
GroundNavigator.cs           # Core ground nav: closed-form arc playback (plays the real cubic Bezier), pure-pursuit tracking, turn-rate-feasibility corner-speed cap, entry-alignment rounding, orbit invariant
RunwayExitPhase.cs             # Rolls on centerline until exit found; builds TaxiRoute from exit path and hands off to TaxiingPhase
HoldingAfterExitPhase.cs       # Post-exit hold: broadcasts "clear of runway", faces away from runway, awaits taxi command

# Pilot/ — solo-training pilot AI (deterministic readbacks)
Pilot/PhraseologyVerbalizer.cs # Static: inverts a PhraseologyRule for a given accepted ParsedCommand → spoken-English readback string.
                               # Picks the first-declared rule per CanonicalCommandType by default; Varied mode can use PilotShortcuts when the frequency is busy.
Pilot/FrequencyActivityMeter.cs # Rolling 60-second pilot-transmission counter; classifies active frequency load as Quiet/Moderate/Busy/Saturated.
Pilot/FrequencyState.cs        # Sim-level active-frequency queue. Serializes solo pilot SAY/audio transmissions and gives awaited command readbacks priority over proactive calls.
Pilot/PilotTransmission.cs     # Record: Callsign, Text, SpeechText, SourceKind, Kind. Transient typed side queue for solo-training SAY/audio broadcasts.
Pilot/PilotPendingRequest.cs   # Snapshot-serialized pending pilot request model for solo-training follow-up reminders.
Pilot/PilotRequestTracker.cs   # Records pilot-originated requests, applies controller responses, and schedules normal/standby follow-up reminders.
Pilot/PilotResponder.cs        # Static: BuildReadback(CompoundCommand, AircraftState) → readback line for solo-training mode.
                               # Uses PhraseologyVerbalizer for rule-backed commands; ground spawn / "going around" / airborne-spawn / VFR closed-traffic check-ins live here directly
                               # Adds light deterministic Quiet-frequency flavor and preserves runway/callsign-critical readback content.
                               # Also: BuildTrafficInSight / BuildFieldInSight / BuildHoldingShortCrossing / BuildClearOfRunway / BuildGoingAround / BuildApproachingMinimumsNoLandingClearance / BuildUnable / BuildLostSightOf*
                               # / BuildUnableTo* — the spelled-out spoken forms used by RPO PilotSpeech routing.
                               # QueueSoloPilotTransmission / QueueSoloPilotReadback put solo pilot speech into PendingPilotTransmissions;
                               # command RSP lines stay immediate while delayed SAY lines represent what the pilot says on frequency.
                               # RouteRpoTransmission(aircraft, soloMode, rpoShowPilotSpeech, pilotSpeechText, warningText) — three-way helper
                               # used by every sim-initiated pilot transmission site to pick the right destination collection.
Pilot/PilotProactive.cs        # Static: TickAirborneCheckIn(AircraftState, SimScenarioState, airportLookup) — fires once-per-aircraft when first ticked airborne in solo mode.
                               # Idempotent via HasMadeInitialContact. Called from SimulationEngine.TickPostPhysics. Also inserts solo-training VFR Class B/C boundary holds from FAA AIS airspace data and ticks pending-request reminders.
Pilot/PilotPersonality.cs      # Enum controlling readback variation. Verbatim emits textbook form; Varied enables activity-aware solo-training shortcuts.
Pilot/PilotSayBuilder.cs       # Static: pilot-style transmission text for SAY-class verbs (SALT/SHDG/SPOS/SSPD/SMACH/SEAPP).
                               # AIM-compliant spoken phraseology (digit-by-digit, "thousand"/"hundred"/"flight level", "Mach point X").
                               # Used by CommandDispatcher for triggered/sequenced SAY blocks AND by yaat-server's SayCommandHandler
                               # for direct controller queries — same text, different routing layer adds the controller's initials.

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
Data/CustomFixDefinition.cs / CustomFixLoader.cs  # Custom fix JSON loading from Data/ARTCCs/{ARTCC}/CustomFixes/*.json
Data/TaxiRouteDefinition.cs / TaxiRouteLoader.cs / TaxiRouteCatalog.cs  # Per-airport preset taxi routes from Data/ARTCCs/{ARTCC}/TaxiRoutes/*.json
                               # Validation against airport graph is lazy at menu-build time via TaxiPathfinder.ResolveExplicitPath.
                               # Right-click "Preset taxi route" submenu in GroundView surfaces applicable routes per aircraft.
Data/AvoidTaxiwayDefinition.cs / AvoidTaxiwayLoader.cs / AvoidTaxiwayCatalog.cs  # Per-airport taxiways the AUTO pathfinder avoids, from Data/ARTCCs/{ARTCC}/AvoidTaxiways/*.json
                               # Read by SearchContext.Compile (NavigationDatabase.AvoidTaxiways) for auto routes only; two-pass in TaxiPathfinder uses an avoided taxiway only when the destination is otherwise unreachable. Explicit TAXI unaffected.
Data/InitialContactTransferRule.cs / InitialContactTransferLoader.cs / InitialContactTransferCatalog.cs
                               # ARTCC/airport SOP exceptions for pilot initial-contact comm transfer, loaded from Data/ARTCCs/{ARTCC}/InitialContactTransfers/*.json.
Data/WakeDirectiveRule.cs / WakeDirectiveLoader.cs / WakeDirectiveCatalog.cs
                               # ARTCC static wake waivers and wake-advisory scoring directives, loaded from Data/ARTCCs/{ARTCC}/WakeDirectives/*.json.
Data/Airspace/AirspaceDatabase.cs # FAA AIS GeoJSON loader/query service: loads all Data/Airspace/*.geojson and *.geojson.br, volume containment, projected Class B/C boundary entry.
Data/Airspace/AirspaceVolume.cs / AirspaceBoundaryCrossing.cs / AirspacePoint.cs / AirspaceClass.cs # Airspace model primitives plus crossing result.
Data/Airspace/faa-training-primary-class-bc.geojson.br # Checked-in Brotli FAA AIS fixture for B/C airspace at all vNAS training primary airports.
Data/ARTCCs/                   # User-submitted per-ARTCC data root (CustomFixes, FixPronunciations, TaxiRoutes, AvoidTaxiways, InitialContactTransfers, WakeDirectives — see Data/ARTCCs/README.md).
Data/FrdResolver.cs            # Fix-Radial-Distance → lat/lon
Data/ApproachGateDatabase.cs   # Static: min intercept distances from CIFP (§5-9-1)
Data/VideoMapMetadata.cs       # Video map metadata model
Data/VideoMapData.cs           # Video map data structures (lines, labels, filters)
Data/VideoMapParser.cs         # GeoJSON → VideoMapData

# Data/Airport/
IAirportGroundData.cs          # Interface: GetLayout(airportId) → AirportGroundLayout?
AirportLayoutDownloader.cs     # Fetches airport ground GeoJSON from vNAS training API; caches under %LOCALAPPDATA%/yaat/cache/airports/
AirportGroundLayout.cs         # Graph: IGroundEdge interface, GroundNode, GroundEdge (straight), GroundArc (bezier fillet arc: P1/P2 control points + MinRadiusOfCurvatureFt), DirectionalEdge (traversal direction)
                               # AllEdges (Edges+Arcs), FindAdjacentHoldShort (BFS, max 12 hops; returns Side), FindExitFromCenterline (walk centerlines, returns side+walk node), FindOnSidePreferredExit (lookahead: defer off-side, prefer later on-side), FindExitPath, FindNearestHoldShortAhead, FindExitAheadOnRunway, ComputeExitAngle
CubicBezier.cs                 # Bezier math utilities; used by FilletArcGenerator (arc generation) and GroundNavigator (path following)
IFilletArcGenerator.cs         # Pluggable fillet contract; None + Standard implementations; FilletMode on GeoJsonParser.Parse
FilletMode.cs                  # Fillet mode enum: None (no-op pass) / Standard (the real generator)
NullFilletArcGenerator.cs      # No-op generator for FilletMode.None and raw-layout tests
FilletGeneratorFactory.cs    # FilletMode → IFilletArcGenerator (None / Standard)
FilletArcGeneratorRegistry.cs# Enumerates implemented generators (None, Standard)
FilletStatistics.cs          # Per-pass fillet tallies returned by Apply
FilletArcGenerator.cs      # The fillet generator: classify junctions → resolve cuts → plan → execute → normalize
Fillet/                        # Plan-then-execute fillet pipeline (edge-split connectivity)
  FilletGeometry.cs            # Turn angle, ideal tangent, cubic-bezier build (control points project toward the junction)
  FilletGraphNormalizer.cs     # Post-execute: recompute distances, drop self-loops/degenerate arcs, sweep isolated nodes (no coincident-node merge — plan guarantees none)
  CutId.cs                     # Type-distinct cut identifier (no Origin-string parsing)
  FilletEndpoint.cs            # Type-distinct fillet endpoint; with CutId lets cleanup passes pattern-match instead of parsing Origin strings
  TaxiwayArmBuilder.cs         # One arm per outbound edge; TaxiwayWalk along same-named taxiway
  JunctionClassifier.cs        # Eligibility + Skip/Simple/MultiCorner/Preserve + collinear pairs
  CornerPlanner.cs             # Arm-pair corners (≥15°) and collinear pairs (<15°)
  ArmCutResolver.cs            # Tangent-cut placement per arm; corner arcs + straight connectors; tangent merges
  FilletEdgeSplitPlanner.cs    # Order-independent connectivity: split each original edge once by its cuts, drop only removed-junction stubs → SurvivingEdgeOp
  FilletPlanBuilder.cs         # Assemble the immutable FilletPlan (cuts, merges, corner arcs, surviving edges, nodes/edges to remove)
  FilletPlanExecutor.cs        # Materialize cut nodes + surviving edges + corner arcs (degenerate arc → straight chord) in one pass; remove consumed edges + removed junctions
  FilletPlanCutRedirect.cs     # Union-find survivor map for tangent merges + stable-anchor binding
  (also FilletPlan/JunctionPlan/CornerSpec/ResolvedArmCut plan model + TaxiwayArm(Terminus), JunctionKind, FilletEligibility, ManualArcDetector, SharedArmTangentPass, PlanWarning, FilletConstants, FilletPlanConsistency)
RunwayIdentifier.cs            # Struct: runway designator parsing/matching
TaxiRoute.cs                   # Resolved path: TaxiRouteSegment (DirectionalEdge wrapping IGroundEdge) + HoldShortPoints (with dynamic lat/lon offset) + DestinationParking/DestinationSpot + completion
TaxiRouteAutoCross.cs          # Applies AutoCrossRunway toggle to a route's RunwayCrossing hold-shorts; reused at TAXI-resolution and on mid-session toggle (SimulationWorld.ApplyAutoCrossToActiveTaxiRoutes)
TaxiPathfinder.cs            # Taxi pathfinder (static): ResolveExplicitPath (SegmentExpander), FindRoute/FindRoutes (A* AutoRouter, per-preference), FindFullLengthLineupHoldShort. See Data/Airport/Pathfinding/ + docs/ground/pathfinder.md
ExplicitPathOptions.cs         # RoutePreference enum + ExplicitPathOptions input bag (pathfinder inputs)
VirtualNode.cs                 # Factory for virtual ground nodes (negative IDs); CreateEdge, CreateSegment, OffsetBefore/OffsetPast
TaxiwayGraphBuilder.cs         # Graph construction from GeoJSON nodes/edges
GeoJsonParser.cs               # GeoJSON→layout; DetectRunwayCrossings via SplitEdgeAtNode
CoordinateIndex.cs             # Spatial index for coordinate-based lookups
RunwayCrossingDetector.cs      # Detect taxiway/runway intersections
RunwayIntersectionCalculator.cs # Runway centerline/projected-path intersections for LAHSO and solo-training runway scoring
HoldShortAnnotator.cs          # Annotate hold-short points on taxi routes; ComputeHoldShortPositions offsets taxiway HS by fuselage length

# Data/
AircraftProfile.cs             # Per-type performance profile record (from AircraftProfiles.json)
AircraftProfileDatabase.cs     # Static lookup: Get(aircraftType) → AircraftProfile?; 163 types
AircraftProfiles.json          # ATCTrainer per-type perf data: altitude-banded climb/descent, Mach speeds
AirlineFleets.cs               # Static map: airline ICAO ↔ ICAO Doc 8643 aircraft type with airframe counts
                               # Both directions pre-computed; loaded lazily from airline-fleets.json
                               # Refresh via tools/refresh-airline-fleets.py — see docs/airline-fleets.md
airline-fleets.json            # Generated map (Airfleets World Fleet Listing, paid quarterly snapshot)
airline-fleets.meta            # Provenance sidecar (per-PDF SHA-256, parsed counts) — committed alongside
AirportAirlines.cs             # Static map: airport IATA/ICAO -> served airline ICAO list for arrival-generator callsign selection
                               # Loaded lazily from airport-airlines.json.br; normalizes K/P-prefixed U.S. ICAOs to local IDs
airport-airlines.json.br       # Generated Brotli fixture from BTS T-100 segment data for current generator airports
                               # OpenFlights route backfill is used only for airports missing BTS carrier hits
airport-airlines.meta          # Provenance sidecar with source ZIP row counts, target airports, and unmapped BTS carriers
AircraftDisplayNames.cs        # Static map: ICAO aircraft type → human-readable display name (e.g. "B738" → "Boeing 737-800").
                               # Loaded lazily from aircraft-display-names.json; used by the Aircraft List Name column and radar/EuroScope/right-click fallbacks when no flight plan is filed.
aircraft-display-names.json    # Generated map from tools/refresh-aircraft-display-names.py (one entry per ICAO type in AircraftSpecs.json).
aircraft-display-names-source.meta # Provenance sidecar: model + prompt hash + ICAO type list used at generation time.

# Data/Faa/
FaaAircraftRecord.cs           # Full FAA ACD row: wingspan, length, tail height, gear geometry, MTOW, classifications
FaaAircraftDatabase.cs         # Static lookup: Get(aircraftType) → FaaAircraftRecord?; used for physical dimensions
FaaAircraftDataService.cs      # Downloads FAA ACD xlsx, parses all columns, caches per AIRAC cycle

# Data/Vnas/
VnasDataService.cs             # Downloads NavData protobuf + specs; serial-based cache
NavDataPathResolver.cs         # Test/offline NavData.dat resolve: vNAS cache, download, TestData fallback
CifpPathResolver.cs            # Current AIRAC CIFP: cache, FAA download, bundled gz fallback; supplementary = newest cached prior cycle (retired/renamed procedures)
AiracCycle.cs                  # AIRAC cycle calculator (epoch Jan 23 2025, 28-day)
VnasConfig.cs                  # Config API DTO
CacheManifest.cs               # Cache manifest tracking serials
AircraftSpecEntry.cs           # VNAS aircraft specs model
AircraftCwtEntry.cs            # VNAS aircraft CWT model
ArtccConfig.cs                 # ARTCC config models (ArtccConfigRoot, FacilityConfig, PositionConfig, TcpConfig, StarsConfig, etc.)
ArtccConfigResolver.cs         # Pure-function resolvers as extension methods on ArtccConfigRoot:
                               # ResolvePosition / ResolveTcpCode / ResolveEramCode / FindPositionByCallsign / FindTcpByCode /
                               # ExpandTcpShorthand / GetCoordinationChannels / GetAllAsdexAirports / GetAllTowerCabAirports /
                               # GetAllAccessibleStripBays / GetAccessibleFacilities / GetConsolidationItems / GetConsolidationOwner / etc.
                               # Server's ArtccConfigService delegates to these; replay applier uses them via TrackResolver.
ArtccAccessRecords.cs          # AccessibleBay, AccessibleFacility, AsdexAirportInfo, TowerCabAirportInfo records used by the resolvers.
CifpDataService.cs             # FAA CIFP zip download/extract per AIRAC cycle
CifpParser.cs                  # ARINC 424 parser: approaches (subsection F), SIDs (D), STARs (E); FAF fixes, terminal waypoints
                               # ParseTerminalWaypoints: per-airport section-C waypoints for RF center fix resolution
CifpModels.cs                  # CIFP data models: CifpApproachProcedure, CifpSidProcedure, CifpStarProcedure, CifpLeg, CifpTransition
                               # CifpLeg: ArcRadiusNm, ArcCenterLat/Lon (RF), RecommendedNavaidId, Theta, Rho (AF)

# Scenarios/
ScenarioLoader.cs              # JSON → ScenarioLoadResult; resolves starting conditions, nav routes, beacon codes
ScenarioModels.cs              # Scenario JSON DTOs: Scenario, ScenarioAircraft, StartingConditions, PresetCommand, etc.
                               # ScenarioGeneratorConfig (renamed to avoid collision with AircraftGenerator static class)
ScenarioIdentity.cs            # Shared scenario ID fallback hashing/normalization for server load and sim replay
ScenarioValidator.cs           # Validates preset commands via CommandParser.ParseCompound; shared by client + yaat-server CLI
                               # ScenarioValidationResult, PresetParseFailure, ProcedureIssue, ProcedureIssueKind records
                               # Detects outdated procedure versions (VersionChanged) and missing procedures (NotFound)
AircraftInitializer.cs         # InitializeOnRunway/AtParking/OnFinal → PhaseInitResult
DepartureSpawnClassifier.cs    # IsHeldSpawnCandidate(loaded) — classifies a departure for hold-for-release spawn-gating
AircraftGenerator.cs           # SpawnRequest → AircraftState (runtime spawn generator)
SpawnRequest.cs                # Spawn descriptor
ScenarioRatingTier.cs          # ScenarioRatingTier enum (Ungated/S3/C1/I1) + ScenarioRatingClassifier
                               # (ordinal-based, hierarchical IsAccessible). Maps both short and long form
                               # rating names (S3 / Student3 etc.) from the vNAS data-api. Shared by the
                               # client picker filter and the server-side gating decision.

# Simulation/
SimulationEngine.cs            # Scenario load, tick orchestration, replay (ReplayFromStartTo — full from-scratch replay;
                               # FastForwardTo — advance from current time; ReplayRange — between two timestamps;
                               # ReplayRangeWithVerification — diff-against-bundled-snapshots; ReplayOneSecond/SubTick — stepping);
                               # CaptureSnapshot/RestoreFromSnapshot; reattaches GroundLayouts to delayed spawns on restore
SimScenarioState.cs            # Per-scenario runtime state: queues, settings, ATC positions, coordination, ArtccConfig (loaded from bundle on replay)
ScenarioPacing.cs              # Shared solo-training pacing helpers for parking call-up intervals and arrival generator rates
SessionRecording.cs            # v1 (commands) + v2 (commands + snapshots) recording format; ArtccConfigJson optional bundle
RecordedAction.cs              # Polymorphic recorded actions: Command, AmendFlightPlan, WeatherChange, SettingChange, AircraftSpawn
RecordedCommandClassifier.cs   # Shared replay-time RecordedCommand classifier. RecordedCommandKind enum + Classify(string)
                               # static fn. Drives the switch in both SimulationEngine.ReplayCommand and the server's
                               # RecordingManager.ReplayCommand so the parse-and-decide flow stays in lockstep across repos.
                               # Compound is the default arm — both single-parse failure and the catch-all route to it.
RecordingCompression.cs        # Brotli compress/decompress; auto-detects Brotli, gzip, or plain JSON on read
RecordingArchive.cs            # v4 ZIP archive reader: on-demand snapshot loading, layout reading, seek API
                               # ToBaseSessionRecording (no snapshots), FindNearestSnapshotIndex, ReadSnapshotAt, ReadArtccConfigJson
RecordingArchiveWriter.cs      # v4 ZIP archive writer: streaming snapshots + deduplicated ground layouts + bundled ArtccConfig
RecordingManifest.cs           # Archive manifest: snapshot index, LayoutAirportIds, HasArtccConfig, metadata
RecordingJsonOptions.cs        # Shared JsonSerializerOptions for recording serialization

# Simulation/Replay/
ReplayTrackApplier.cs          # Replay-time dispatcher for track + AS-prefix commands. Maintains per-connection active-position map;
                               # routes parsed commands through TrackEngine.Dispatch with identity resolved via TrackResolver.
SnapshotDiff.cs                # Pure-function diff between an engine's live aircraft state and a captured snapshot's DTOs.
                               # Used by ReplayRangeWithVerification to surface drift between replay and recorded snapshots.
ReplayResult.cs                # ReplayResult / SnapshotDriftReport / AircraftDrift / FieldDrift records.
ScenarioQueues.cs              # DelayedSpawn (+ HeldForRelease), ScheduledTrigger, ScheduledPreset, ScheduledRelease, ActiveTimer (TIMER countdowns), GeneratorState, DelayedHandoff
HeldReleaseService.cs          # Hold-for-release: Arm/Disarm/Release an airport's IFR departures + BuildRundown. See docs/hold-for-release.md
ConsolidationState.cs          # Thread-safe manual consolidation overrides

# Simulation/Snapshots/
StateSnapshotDto.cs            # Top-level snapshot DTO + TimedSnapshot (elapsed + action index + state)
AircraftSnapshotDto.cs         # Aircraft state DTO (~100 fields) + nested DTOs (TrackOwner, Tcp, Pointout, SharedState, student-frequency eligibility, etc.)
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

## Yaat.GuideCapture — CLI tool (`tools/Yaat.GuideCapture/`)

Headless screenshot harness for `USER_GUIDE.md`. Boots an in-process `yaat-server` on a free loopback port, runs `Yaat.Client` under `Avalonia.Headless` with the real Skia backend (`UseHeadlessDrawing = false` + `UseSkia()`), then drives every Scene in `SceneCatalog.All` through `HeadlessUnitTestSession.Dispatch`. Each scene captures one PNG via `Window.CaptureRenderedFrame()`. Rerun whenever a UI surface in the guide changes.

```
Program.cs                     # Entry: parses --scene/--out, starts InProcessServer, dispatches scenes; calls Environment.Exit so headless threads don't keep the process alive
ModuleInit.cs                  # Redirects YAAT_APPDATA_DIR to a temp folder + seeds preferences.json (CID/initials/ARTCC) so MainViewModel.AttemptConnectAsync passes its identity gates
Server/InProcessServer.cs      # Port allocation (TcpListener trick) + YaatHost.BuildAsync + StartAsync/StopAsync lifecycle
Capture/Scene.cs               # Abstract scene: BeforeWindowAsync, CreateWindow, AfterShowAsync, GetCaptureTarget for popout children
Capture/Runner.cs              # Per-scene flow: setup → show → settle → capture PNG; Width/Height of 0 means "use the window's natural size"
Capture/CaptureContext.cs      # Per-run state: ServerUrl, RepoRoot (walks up to yaat.slnx)
Capture/SceneActions.cs        # WaitUntilAsync + WaitForConnectionAsync / CreateRoomAsync / LoadScenarioAsync helpers shared by scenarios
Capture/SceneCatalog.cs        # Static catalog of every scene
Scenes/                        # ScenarioSceneBase (connect → room → load → tab) + per-scene subclasses
                               # MainWindow*Scene — empty / connected-empty / overview / popped-out
                               # AircraftListScene / GroundViewScene / RadarViewScene / FlightStripsScene
                               # GroundViewPopoutScene / RadarViewPopoutScene
                               # FlightPlanEditorScene
                               # FavoritesBarScene / FavoritesPanelScene
                               # ArrivalGeneratorsEditorScene
                               # StandaloneWindowSceneBase + Settings/LoadScenario/LoadWeather/Weather/About
Fakes/FakeFilePickerService.cs (not yet — MainWindow uses real AvaloniaFilePickerService against the headless Window which is fine)
```

The OAK clearances scenario `docs/atctrainer-scenario-examples/01H06NVK7VN8BS7MCDXHKJZ7MQ.json` is the canonical fixture for every "scenario loaded" scene.

## yaat-crc-config — Standalone Rust binary (`tools/yaat-crc-config/`)

Tiny (~200 KB) standalone tool that ports the YAAT client's `Tools → Configure CRC Environments` flow into a single signed binary. Lets students who only want to point CRC at YAAT skip installing the full client. Released independently from the `crc-config-v*` tag via `.github/workflows/yaat-crc-config.yml`.

```
Cargo.toml                     # Size-optimized release profile (opt-level=z, lto=fat, panic=abort, strip)
build.rs                       # Validates ../../docs/crc-environments.json schema at build time
src/main.rs                    # Flow: detect dir → already configured? → confirm → upsert → success. windows_subsystem="windows" so double-click doesn't flash a console.
src/config.rs                  # Mirrors CrcConfigService.cs: find_crc_config_dir (Win registry + LOCALAPPDATA, Mac/Linux home paths), are_entries_present, upsert_entries (preserves unrelated keys via serde_json::Value)
src/dialog.rs                  # Cross-platform native dialogs: MessageBoxW (Win), osascript (Mac), zenity/kdialog/console (Linux)
```

The canonical YAAT environment list (`YAAT1` + `YAAT Local`) lives in `docs/crc-environments.json` — a single source of truth shared by this tool, `Yaat.Client.Core/Services/CrcConfigService.cs` (embedded resource), and `Setup-CrcEnvironment.ps1` (read at script execution time when run from the repo).

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

See [session-persistence.md](session-persistence.md) for planned-restart room checkpoints (yaat-server `Simulation/Persistence/`).
    RoomEngine.cs              # Per-room facade: tick, commands, scenario, broadcast, consolidation
    ConsolidationState.cs      # Thread-safe manual consolidation overrides per room
    RoomEngineFactory.cs       # Creates RoomEngine with shared singleton deps
    SimulationHostedService.cs # Thin orchestrator: 1s tick loop iterating rooms
    TickProcessor.cs           # Stateless tick logic (physics, spawns, triggers, pilot proactive hooks, auto-accept, coordination timers); FP-creator and airport-based deferred autotrack; drains ready solo frequency transmissions as SAY entries and emits PilotTransmissionBroadcast
    TrackCommandHandler.cs     # Stateless track command logic (HO, ACCEPT, DROP, etc.)
    CoordinationCommandHandler.cs # Stateless coordination logic (RD, RDH, RDR, RDACK, RDAUTO)
    ScenarioLifecycleService.cs # Scenario load/unload/spawn/generator logic
    ScenarioState.cs           # Per-room active scenario state: queues, positions, generators, channels
    TrainingBroadcastService.cs # SignalR hub context wrapper for training clients, including PilotTransmissionBroadcast fan-out.
    PilotVoiceAssigner.cs      # Pure deterministic `(scenario rng seed, callsign) -> speaker id 0..903` helper for pilot voice events.
    CrcBroadcastService.cs     # CRC wire-protocol broadcast; per-room scoped via BroadcastBatch; BroadcastToTopicSubscribersAsync
    CrcVisibilityTracker.cs    # STARS/ASDEX/TowerCab visibility rules; STARS hysteresis (add at elev+100, remove at elev); AircraftState.IsVehicle excluded from STARS
    StarsLineNumberAssigner.cs # Per-room sequential line number assignment (1-99 wrap)
    StripCommandHandler.cs     # Flight strip command dispatch (all 15 canonical verbs incl. SCAN)
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
    ArtccConfig.cs             # VNAS ARTCC config deserialization models (VideoMapConfig, StarsAreaConfig, etc.) — lives in Yaat.Sim/Data/Vnas/
    ArtccConfigService.cs      # Loader: downloads + caches ARTCC config from vNAS; resolution methods delegate to ArtccConfigResolver in Sim
    ArtccConfigService.Consolidation.cs  # Partial: thin facade over Sim's ArtccConfigResolver consolidation methods
    ArtccConfigService.VideoMaps.cs      # Partial: facility video maps + position display DTO builders (CRC wire-format, kept server-side)
    PositionRegistry.cs        # Thread-safe CRC + RPO position tracking
    NexradBoundsLoader.cs      # Parses Data/Nexrad/NexradBoundingBoxes.geojson (24 ARTCCs) → per-ARTCC NexradBounds (N/S/E/W)
  Udp/UdpStubServer.cs        # UDP port 6809 stub (CRC keepalive/registration)
  Logging/FileLoggerProvider.cs
```
