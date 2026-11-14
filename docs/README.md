# YAAT Docs — Start Here

**Looking for code, or about to change a subsystem? Read the map before reading source.** YAAT's docs front-load each subsystem's overview, contracts, and footguns — starting here is faster and more accurate than grepping blind.

**Where docs live.** The repo root holds the user-facing docs only (`README`, `INSTALL`, `GETTING_STARTED`, `USER_GUIDE`, `COMMANDS`, `SOLO_TRAINING`, `ARTCC_CUSTOMIZATION`, `CHANGELOG`); developer docs live here in `docs/`, and the root README points users at the root set. A developer doc still at the root is moved here.

## Locating files

→ **[`architecture.md`](./architecture.md)** — the full annotated file tree. Its top section, *"Task Index — I need to change X, which files?"*, maps common tasks straight to the relevant files in order of relevance. **Read this first.**

## Subsystem docs

Open the matching doc **before exploring, searching, or editing** the code it lists; each carries its own overview, contracts, and footguns.

- Vocabulary and decisions: [`../CONTEXT.md`](../CONTEXT.md) (the glossary — sim-second, spine, host, run kind, action, arm, baked draw), [`adr/`](./adr/) (the tick-path and action-router decisions)
- [`landing-and-runway-exit.md`](./landing-and-runway-exit.md) — `LandingPhase`, `RunwayExitPhase`, `GroundNavigator` (analog rollout)
- [`ground/`](./ground/README.md) — ground/taxi stack index → [`fillet-generator`](./ground/fillet-generator.md), [`pathfinder`](./ground/pathfinder.md), [`navigator`](./ground/navigator.md), [`hold-short-placement`](./ground/hold-short-placement.md), [`pushback`](./ground/pushback.md)
- [`ground-layout-generation.md`](./ground-layout-generation.md) — `GeoJsonParser` → fillet arcs → ground-graph construction (the full import pipeline behind `AirportGroundLayout`)
- [`speech-recognition-pipeline.md`](./speech-recognition-pipeline.md) — `src/Yaat.Sim/Speech/`, `WhisperSttEngine`, `LocalLlm*`, `SpeechRecognitionService`, `tools/Yaat.SpeechSandbox` (STT input); also [`speech-context-decoder.md`](./speech-context-decoder.md), and measurements in [`research/`](./research/) (e.g. the controller-voice ouroboros baseline)
- [`solo-training-pilot-speech.md`](./solo-training-pilot-speech.md) — `src/Yaat.Sim/Pilot/`, `PendingPilotTransmissions`, `ActiveFrequency`, `PilotVoiceService` (TTS output)
- [`pilot-phraseology.md`](./pilot-phraseology.md) — `PilotResponder.Build*`, `PhraseologyVerbalizer`, `PilotSpeechText` (Terminal/Tts/RpoTerminal): what pilots say + AIM/7110.65 grounding (read before changing any pilot wording) → source corpus for `PhraseologyRule.PilotVariants`: [`pilot-phraseology-examples.md`](./pilot-phraseology-examples.md)
- [`flight-strips.md`](./flight-strips.md) — `FlightStripState`, `StripItemDto`, `CrcClientState.Strips.cs`, strip commands
- [`vtdls.md`](./vtdls.md) — Tower Data Link Services (PDC) emulation: `src/Yaat.Client.Tdls/`, `tools/Yaat.VTdls.Web`, server vTDLS state
- [`phases.md`](./phases.md) — anything under `src/Yaat.Sim/Phases/` (base contract, `CommandAcceptance`, `PhaseRunner`)
- [`snapshots-and-replay.md`](./snapshots-and-replay.md) — `Simulation/Snapshots/`, `Simulation/Replay/`, `RecordingArchive`, `SnapshotSchemaMigrator`, bug bundles
- [`tick-loop.md`](./tick-loop.md) — per-tick execution order (PrePhysics → Physics×4 → PostPhysics) + broadcast cadence
- [`command-pipeline.md`](./command-pipeline.md) — `SendCommandAsync`, `CommandSchemeParser`, `CommandDispatcher`, `RoomEngine`, `*CommandHandler.cs` (input→queue)
- [`command-chaining.md`](./command-chaining.md) — the `;`/`,` chaining contract: per-category completion, the three advancement regimes, fire-time abort, non-compoundable rejection (read before touching `UpdateCommandQueue`/`CommandBlock`)
- [`command-handlers.md`](./command-handlers.md) — `CommandDispatcher.cs` + `*CommandHandler.cs` internals
- [`aircraft-data-model.md`](./aircraft-data-model.md) — `AircraftState`, `ControlTargets`, `Aircraft*.cs` satellites, `SimulationWorld`; adding a per-aircraft field
- [`training-hub-contract.md`](./training-hub-contract.md) — `/hubs/training` JSON wire contract; adding a hub method or `AircraftUpdated` field
- [`server-rooms-and-hub.md`](./server-rooms-and-hub.md) — hosted tick loop, `RoomEngine`, `TickProcessor`, `AircraftChangeTracker`, `TrainingRoom*`, the scenario load's prepare/commit, load flag, resource pin and progress
- [`vatsim-auth.md`](./vatsim-auth.md) — VATSIM Connect (OAuth2) identity, access control, token flow; the server is the identity authority
- [`session-persistence.md`](./session-persistence.md) — preserving rooms across a *planned* server restart (admin prepare/shutdown flow; no crash recovery)
- [`crc-display-state.md`](./crc-display-state.md) — `CrcClientState`, `CrcBroadcastService`, `CrcVisibilityTracker`, `DtoConverter`, `CrcDtos*.cs`; hub-method status table in [`crc-protocol-support.md`](./crc-protocol-support.md)
- [`eram/README.md`](./eram/README.md) — ERAM command syntax, validation and feedback text (what yaat-server's ERAM handling is held to)
- [`logging.md`](./logging.md) — adding a logger / debugging missing log lines (SimLog & AppLog)
- [`navigation-database.md`](./navigation-database.md) — `NavigationDatabase`, `RouteExpander`, `FrdResolver`, `CustomFixLoader`, `ApproachGateDatabase` (+ RV-SID footgun)
- [`military-training-routes.md`](./military-training-routes.md) — `src/Yaat.Sim/Data/MilitaryRoutes/`, `MilitaryRoutePhase`, `AerialRefuelingAnchorPhase`, `MilitaryRouteCommandHandler`, `tools/build-mtr-data.py` (AP/1B IR/VR/SR + AR)
- [`flight-physics.md`](./flight-physics.md) — `FlightPhysics`, `ControlTargets`, `AircraftPerformance`, `CategoryPerformance` (the validated category table), `WindInterpolator`, kinematics
- [`live-traffic.md`](./live-traffic.md) — `src/Yaat.Sim/LiveTraffic/` (shadow aircraft from an external feed: `AircraftLiveTraffic`, `LiveTrafficKinematics`, `IsShadow` tick bypass + command gate, `RecordedLiveTrafficSample`)
- [`aircraft-performance.md`](./aircraft-performance.md) — per-type performance resolution + the contributor `AircraftProfileOverrides.json` correction layer (`AircraftProfileOverride`, `OverrideAwareProfileCorrectionAdapter`, `CategoryPerformance.BaselineProfile`)
- [`client-settings-and-menus.md`](./client-settings-and-menus.md) — the Settings window (`SettingsNavigation`, sections, search, OK/Apply/Cancel), Scenario defaults vs the session flyout, the Import / Export hub, the View menu and hotkeys, layouts
- The client-driver MCP (drives the real desktop client and CRC: reproducing UI-path bugs end to end, comparing CRC's display with YAAT's) lives in the private yaat-server repo; read yaat-server `docs/client-driver-mcp.md`. Setting up CRC against a local server (profile, connect, FPE): yaat-server `docs/crc-first-session.md`
- Tests: [`test-map.md`](./test-map.md) (which class of test pins what, and where a new one goes), [`test-harness.md`](./test-harness.md) (writing any Yaat.Sim test / "passes alone but flakes in the suite"), [`e2e-tdd-issue-debugging.md`](./e2e-tdd-issue-debugging.md), [`test-suite-speed.md`](./test-suite-speed.md)
- [`tick-animator.md`](./tick-animator.md) — recording tick-by-tick aircraft state with `TickRecorder` and animating it over a ground layout (LayoutInspector `--ticks --html`) to eyeball test behavior
- [`approach-and-pattern-geometry.md`](./approach-and-pattern-geometry.md) — `Phases/Approach/`, `Phases/Pattern/`, `PatternGeometry`, `AirborneFollowHelper`, `HoldingEntryCalculator`, `ApproachEvaluator`
- [`conflict-and-visual-detection.md`](./conflict-and-visual-detection.md) — `ConflictAlertDetector`, `GroundConflictDetector`, `AtpaProcessor`, `VisualDetection`, `WakeTurbulenceData`
- [`scenario-loading-and-generation.md`](./scenario-loading-and-generation.md) — `ScenarioLoader`, `AircraftInitializer`/`AircraftGenerator`/`SpawnParser`, `GroundSpawnSnap`
- [`airline-fleets.md`](./airline-fleets.md) / [`airport-airlines.md`](./airport-airlines.md) — the generator's callsign realism data (`airline-fleets.json`, `airport-airlines.json.br`) and the Python tools that rebuild them
- [`hold-for-release.md`](./hold-for-release.md) — `HeldReleaseService`, `DepartureSpawnClassifier`, the HFR/REL gates + rundown broadcast (departure-release control)
- [`solo-training-evaluation.md`](./solo-training-evaluation.md) — `SoloTrainingEvaluator`, `AircraftCompletion`, scoring findings
- [`runway-fouling.md`](./runway-fouling.md) — which "clear of the runway" definition each consumer uses (`RunwayOccupancy`, `OccupiedRunwayGoAround`, `RunwaySafetyAdvisor`, solo runway-user scoring), from P/CG, AIM 4-3-21 / 2-3-5 and 7110.65 3-9 / 3-10
- [`track-sharing-and-consolidation.md`](./track-sharing-and-consolidation.md) — `ConsolidationEngine`/`ConsolidationState`, `AircraftStarsState.SharedState`, `AircraftEramState`, `StarsConsolidation`
- [`client-mainviewmodel.md`](./client-mainviewmodel.md) — any `MainViewModel` partial, `MainWindow.axaml.cs`, SignalR-driven client features
- [`radar-rendering.md`](./radar-rendering.md) — `Views/Radar/`, `Views/Map/`, the `Views/Scope/` YAAT Scope engine (SkiaSharp two-thread snapshot split)
- [`ground-rendering.md`](./ground-rendering.md) — `Views/Ground/` (`GroundCanvas`, `GroundRenderer`, `GroundViewModel` taxi-route overlays + the client-side route-reconstruction contract)
- [`command-input-ux.md`](./command-input-ux.md) — `CommandInputController`, `ArgumentSuggester`/`FixSuggester`, `SignatureHelpState`, `CommandInputParseResult` (pre-send)
- [`weather-and-wind.md`](./weather-and-wind.md) — `WeatherProfile`/`WeatherTimeline`, `MetarParser`, `WindsAloftParser`, `WindInterpolator`, `MagneticDeclination`, `LiveWeatherService`
- [`airspace-database.md`](./airspace-database.md) — `src/Yaat.Sim/Data/Airspace/`, `AirspaceBoundaryHoldPhase`, `PilotProactive.TickAirspaceBoundaryRespect`, Class B/C separation
- [`minimum-vectoring-altitude.md`](./minimum-vectoring-altitude.md) — `src/Yaat.Sim/Data/Mva/`, `tools/build-mva-data.py`, the radar MVA display surfaces
- [`guide-capture.md`](./guide-capture.md) — writing a deterministic screenshot scene in `tools/Yaat.GuideCapture`
- [`vedst-sign-in.md`](./vedst-sign-in.md) — vEDST sign-in: enabling it on a server, connecting a vEDST checkout
- [`data-handling.md`](./data-handling.md) — what data YAAT collects, keeps and deletes (privacy)
- [`installer-release.md`](./installer-release.md) — Velopack packaging, auto-update, `release.yml`, versioning (and [`macos-code-signing.md`](./macos-code-signing.md) for the notarized macOS `.pkg`/`.app`)

## Reference material

- [`atctrainer-scenario-examples/`](./atctrainer-scenario-examples/) — Real ATCTrainer scenario JSONs (reference for scenario format)
- [`crc/`](./crc/) — CRC controller manual (STARS, Tower Cab, vStrips)
- [`eram/`](./eram/README.md) — ERAM command reference extracted from the FAA ERAM EDSM SRS: per-command formats, field checks, descriptors and our error texts, as YAML that yaat-server's `EramReferenceConformanceTests` enforces. Read it before touching `CrcClientState.Eram*.cs`
- [`vnas-artcc-config-examples/`](./vnas-artcc-config-examples/) — Real ARTCC config JSONs (facility hierarchy, positions, coordination channels)
- `..\_ext\vatsim-vnas\crc-decompiled\CRC\` (sibling of the yaat repo root) — private git repo of the ILSpy-decompiled CRC (2.15 baseline + 2.17; re-decompile + commit on each CRC update, by the `crc-update-check` skill). Key: `Vatsim.Nas.Crc.Ui.Displays.Stars.Elements/DisplayElementTracks.cs`, `…Stars.Consolidation/ConsolidationManager.cs`, `…Stars.Tracks/Track.cs`, `TrackOwnerExtensions.cs`
- [`scenario-validation.md`](./scenario-validation.md) — validating scenario preset commands (`VnasScenarioParseTests`); [`scenario-validation-known-failures.md`](./scenario-validation-known-failures.md) lists the failures that are *not* parser bugs (scenario typos, features we won't implement) — check it before chasing one
- [`discord-integration.md`](./discord-integration.md) — Discord bot and GitHub Actions workflows; the desktop client's Rich Presence (`src/Yaat.Client/Services/Discord/`)

## Reference repos and APIs

| Repo | Path | Purpose |
|------|------|---------|
| vzoa | `..\_ext\vzoa` | vZOA training files (auxiliary). Airport ground GeoJSONs are fetched from the vNAS data-api by `AirportLayoutDownloader`; do not read the sibling repo. |
| vatsim-server-rs | `..\_ext\csko\vatsim-server-rs` | CRC protocol reference (wire format, DTO ordering) — **read-only emulation**, use vNAS messaging-master for mutation-capable methods |
| vatsim-vnas | `..\_ext\vatsim-vnas` | vNAS source: common (GeoCalc), data (nav/scenarios), messaging (CRC DTOs — definitive reference) |
| vedst | `..\_ext\vFlightDataSystems\VATSIM_EDST_frontend` | vEDST web client (`vFlightDataSystems/VATSIM_EDST_frontend`) — **read-only reference** for the hub methods, callbacks, DTO shapes and `/vnas` endpoints yaat-server serves it ([`vedst.md`](./vedst.md)) |
| vatsim.dev | `..\_ext\vatsimnetwork\vatsim.dev` | VATSIM developer docs (`vatsimnetwork/vatsim.dev`) — **read-only reference** for VATSIM Connect (OAuth2, `/api/user`, `openapi/connect.yaml`) and its sandbox (`auth-dev.vatsim.net`, test accounts: `docs/services/connect/sandbox.md`) |

**vNAS APIs:** Config: `https://configuration.vnas.vatsim.net/` | Data: `https://data-api.vnas.vatsim.net/api/artccs/{id}` | Airport ground map: `https://data-api.vnas.vatsim.net/api/training/airports/{FAA}/map` (used by `AirportLayoutDownloader`, cached at `%LOCALAPPDATA%/yaat/cache/airports/`)

## Plans & roadmap

→ The plan lives in Linear, team YAAT; [`plans/MAIN.md`](./plans/MAIN.md) is a generated snapshot of it. [`plans/README.md`](./plans/README.md) says what each design file and programme folder under `plans/` holds; finished plans are deleted, not archived.

---

*Agents: the user-level `Explore` agent explores the codebase — it follows this docs-first protocol automatically.*
