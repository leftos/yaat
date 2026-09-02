# CLAUDE.md

## Project Overview

YAAT (Yet Another ATC Trainer) — instructor/RPO desktop client for ATC training. Connects to [yaat-server](https://github.com/leftos/yaat-server) via SignalR; server feeds CRC via SignalR+MessagePack. Uses ATCTrainer scenarios directly (same JSON format via vNAS data-api).

## Build & Run

```bash
dotnet workload restore                                 # Once per clone: installs wasm-tools (the .Web projects need it)
dotnet build                                            # Build entire solution
dotnet run --project src/Yaat.Client                    # Run client (needs yaat-server; see the port note below)
dotnet run --project tools/Yaat.Scratch                 # Ad-hoc throwaway scratchpad (intentionally empty placeholder)
dotnet run --project tools/Yaat.SpeechSandbox           # Speech sandbox GUI (or `-- --pipeline <wav>`, `--lmkit-stt`, `--lmkit-models`, `--lmkit-gpus`, `--yaat-catalog`, `--llm-probe`)
dotnet run --project tools/Yaat.GuideCapture            # Regenerate USER_GUIDE.md screenshots into docs/user-guide/img/ (or `-- --scene <name>`)
dotnet run --project tools/Yaat.RecordingConsolidator    # Dedupe recording .zip fixtures in tests/Yaat.Sim.Tests/TestData (see the `consolidate-recordings` skill)
pwsh tools/test-all.ps1                                 # Build + test both yaat and yaat-server (excludes Nightly + PathfinderGrid sweeps for speed)
pwsh tools/test-all.ps1 -Full                           # ...including the heavy Nightly + PathfinderGrid sweeps (CI/nightly run these)
qodana scan --results-dir .tmp/qodana-results           # Static analysis (local only)
```

.NET 10 SDK required. Solution uses `.slnx` format (`yaat.slnx`). Close Yaat.Client before builds to avoid DLL lock warnings.

**`wasm-tools` is a build prerequisite.** `yaat.slnx` includes `tools/Yaat.VStrips.Web` and `tools/Yaat.VTdls.Web` (Avalonia Browser/WASM), so a solution-wide `dotnet build` fails on a fresh clone until `dotnet workload restore` installs the workload.

**Server port: `dotnet run --project src/Yaat.Server` binds `http://localhost:5130`,** not 5000. 5130 comes from the first `launchSettings.json` profile (which also sets `ASPNETCORE_ENVIRONMENT=Development`); 5000 is only the Docker/production bind. The client's auto-connect default is still `http://localhost:5000`, so point it at 5130 when running the server from source.

## Layout Inspector Tool

`tools/Yaat.LayoutInspector/` loads an airport GeoJSON and queries the ground graph, renders interactive HTML maps, and analyzes per-tick CSVs from `TickRecorder`. Use it to understand airport topology when debugging ground/exit/taxi bugs, and to inspect aircraft trajectories after a failing test writes a tick CSV.

**Invoke the `layout-inspect` skill instead of composing CLI invocations by hand** — it carries the current flag reference, so you don't re-derive syntax from memory or work from a stale copy of it. Flag-level docs live in [`tools/Yaat.LayoutInspector/CLAUDE.md`](tools/Yaat.LayoutInspector/CLAUDE.md); worked examples in [`docs/e2e-tdd-issue-debugging.md`](docs/e2e-tdd-issue-debugging.md).

Typical questions it answers: trace multi-hop exit paths (`--bfs`), find all exits from a runway (`--exits`), inspect node connectivity (`--node`), verify hold-short runway IDs (`--runway`), measure along-graph distance and per-leg bearings (`--path-distance`), resolve an explicit taxi route with a diagnostic trace (`--pathfinder`), dump the airport to JSON for grepping (`--dump`), overlay or tabulate a recorded trajectory against a runway reference (`--html --ticks`, `--tick-table --tick-ref`).

## Bug Bundle Tool

`tools/bug_bundle.py` inspects, extracts, installs, and validates v4 bug bundles (`*.yaat-bug-report-bundle.zip`, `*-recording.zip`). Requires `brotli` (`pip install brotli`). Use it whenever a bundle is attached to an issue — faster than throwaway C# or unzipping by hand.

When a `.yaat-bug-report-bundle.zip` or `*-recording.zip` path (or any bundle-shaped filename) appears in user input, **invoke the `bug-bundle` skill before running any subcommand** — it carries the full reference so you don't re-derive syntax from memory. For single-aircraft triage start with `history --callsign X` (replaces 5+ targeted `snapshot --at` calls with one chronological view).

Subcommands: `info`, `snapshot`, `track`, `proximity`, `actions`, `history`, `live-status`, `phases`, `commands`, `scenario`, `weather`, `layouts`, `logs`, `trim`, `install`, `validate`. Full reference: `.claude/skills/bug-bundle/SKILL.md`.

**Extend the tools when you find yourself doing repeat custom work.** If you write more than two ad-hoc Python/C# snippets that pull the same kind of data from a bundle, layout, or snapshot — coordinate vs runway centerline, all exits with current occupancy, two-aircraft positional comparison over a range — turn that snippet into a subcommand of `tools/bug_bundle.py` or `tools/Yaat.LayoutInspector/` first, then use it. The next agent will have the same investigation; bake the lookup into the tool. Keep custom snippets only for genuinely one-off questions.

## Logs

Read log files first before speculating about runtime errors:
- **Client**: `%LOCALAPPDATA%/yaat/yaat-client.log`
- **Server**: `src/Yaat.Server/bin/Debug/net10.0/yaat-server.log` (relative to yaat-server repo root)

## Architecture

> **Full annotated file tree: [`docs/architecture.md`](docs/architecture.md).** Read it when you need to locate files or understand project structure. Keep it up-to-date before each commit.
>
> **Exploring the codebase?** Start from the docs map ([`docs/README.md`](docs/README.md) → `architecture.md` Task Index → the matching subsystem doc), not source from scratch. When delegating exploration to a subagent, prefer the `yaat-explore` agent (it follows this docs-first protocol automatically); otherwise put "read `docs/architecture.md` + the relevant `docs/*.md` first" in the agent's prompt.

**Yaat.Sim** is shared by every client project and by yaat-server.

- **Yaat.Sim** (`src/Yaat.Sim/`) — Shared simulation library. All aviation logic: flight physics, phases, pattern geometry, performance constants, command dispatch, command queue. No UI deps.
- **Yaat.Client** (`src/Yaat.Client/`) — Avalonia desktop app: radar/ground rendering, `MainViewModel`, command input, the windows the instructor drives. MVVM (CommunityToolkit.Mvvm), SkiaSharp rendering. **Sole owner of the LM-Kit.NET dependency** (speech) — nothing else in `src/` may reference it.
- **Yaat.Client.Core** (`src/Yaat.Client.Core/`) — LM-Kit-free client core: `ServerConnection` (SignalR + DTOs), `UserPreferences`, `AppLog`, `ClientVersionGate`, `UpdateService`, CRC alias parsing, `ConnectViewModel`. Split out so the browser/WASM front-ends and the desktop app share it.
- **Yaat.Client.Strips** (`src/Yaat.Client.Strips/`) — vStrips UI (strip bays, find, printing).
- **Yaat.Client.Tdls** (`src/Yaat.Client.Tdls/`) — vTDLS UI; references Yaat.Client.Strips.
- **Yaat.VStrips.Web / Yaat.VTdls.Web** (`tools/`) — Avalonia Browser (WASM) front-ends over Strips/Tdls. **yaat-server builds and hosts these** from its `src/Yaat.Server/Dockerfile` via `extern/yaat/`; they are why `wasm-tools` is a build prerequisite here.
- **yaat-server** (`..\yaat-server\`) — ASP.NET Core server. SignalR comms, CRC protocol, training rooms, scenario loading. References Yaat.Sim via sibling project ref.

Project-reference direction: `Yaat.Client` → `Yaat.Client.Core` → `Yaat.Client.Strips`/`Yaat.Client.Tdls` → `Yaat.Sim`. Adding a client type used by both the desktop app and a web front-end means putting it in Core (or Strips/Tdls), not `Yaat.Client`.

### Key Patterns

- **SignalR**: JSON on `/hubs/training`; DTOs as records in `src/Yaat.Client.Core/Services/ServerConnection.cs`. Hub API signatures are in the code — read `ServerConnection.cs` (client) and `TrainingHub.cs` (server).
- **MVVM**: `[ObservableProperty]`/`[RelayCommand]`; `_camelCase` fields → `PascalCase` props
- **Threading**: SignalR callbacks → `Dispatcher.UIThread.Post()` to marshal to UI
- **NavigationDatabase**: Static singleton. `Initialize(navData)` at startup; tests use `SetInstance(db)`.
- **No DI**: `MainWindow` creates `MainViewModel` directly
- **Snapshots**: `SimulationWorld.GetSnapshot()` → shallow copy; treat as read-only

### Command Pipeline

1. User input → `MainViewModel.SendCommandAsync()` resolves callsign via partial match
2. `CommandSchemeParser.ParseCompound()`: `;` = sequential, `,` = parallel, `LV`/`AT` = conditions
3. Canonical format → server via `SendCommand(callsign, canonical)`
4. Server builds `CommandQueue` of `CommandBlock`s; `FlightPhysics.UpdateCommandQueue()` checks triggers each tick

**Track commands** (TRACK, DROP, HO, ACCEPT) bypass CommandDispatcher → TrackCommandHandler. `AS` prefix resolves RPO identity.

**Coordination commands** (RD, RDH, RDR, RDACK, RDAUTO) bypass CommandDispatcher → CoordinationCommandHandler. Channels from ARTCC config. Auto-expire 5min after ack.

### Command Rules

- Match ATCTrainer/VICE names where applicable. Canonical reference is `COMMANDS.md`; alias data lives in `docs/command-aliases/*.json`.
- **Completeness (MANDATORY):** Every `CanonicalCommandType` must exist in `CommandScheme.Default()` and `CommandRegistry.All`. Tests enforce this.
- **Altitude arguments:** Always use `AltitudeResolver.Resolve()` for parsing altitude arguments in commands. It handles both shorthand (`15` → 1500ft) and full (`1500` → 1500ft) formats, plus AGL notation (`KOAK+010`).

### Command Input UX

Unified parse-once architecture in `CommandInputController`:
- `ParseCommandInput()` produces a `CommandInputParseResult` (verb, definition, param index, typed args)
- **Autocomplete**: `ArgumentSuggester` consumes the parse result → dropdown value suggestions
- **Signature help**: `SignatureHelpState` consumes the parse result → inline parameter hints

All commands including rewrite verbs (e.g. `RWY`) go through `CommandRegistry` — no special-case code paths needed.

## Tech Stack

.NET 10, C# (nullable, implicit usings) | Avalonia UI 12.1.0 (Fluent dark, compiled bindings; SkiaSharp comes in transitively via `Avalonia.Skia` — not a direct package reference) | CommunityToolkit.Mvvm 8.4.0 | SignalR.Client 10.0.3 | Velopack 1.2.0 (installer/auto-update) | LM-Kit.NET 2026.7.3 (`Yaat.Client` + `Yaat.SpeechSandbox` only)

## Related Repositories

| Repo | Path | Purpose |
|------|------|---------|
| yaat-server | `..\yaat-server` | ASP.NET Core server, simulation engine, CRC protocol |
| vzoa | `..\vzoa` | vZOA training files (auxiliary). Airport ground GeoJSONs are fetched from the vNAS data-api by `AirportLayoutDownloader`; do not read the sibling repo. |
| vatsim-server-rs | `..\vatsim-server-rs` | CRC protocol reference (wire format, DTO ordering) — **read-only emulation**, use vNAS messaging-master for mutation-capable methods |
| lc-trainer | `..\lc-trainer` | Previous WPF trainer (**NOT trusted** — needs expert review) |
| vatsim-vnas | `..\vatsim-vnas` | vNAS source: common (GeoCalc), data (nav/scenarios), messaging (CRC DTOs — definitive reference) |

**vNAS APIs:** Config: `https://configuration.vnas.vatsim.net/` | Data: `https://data-api.vnas.vatsim.net/api/artccs/{id}` | Airport ground map: `https://data-api.vnas.vatsim.net/api/training/airports/{FAA}/map` (used by `AirportLayoutDownloader`, cached at `%LOCALAPPDATA%/yaat/cache/airports/`)

**RV-SID synthetic transitions:** `NavData.dat` encodes radar-vectors SIDs (e.g. NIMI5/OAK6) with synthetic `[OAK, X]` "transitions" that are not published CIFP transitions. Flight-plan callers must pass `RouteExpander.Expand(..., includeAllTransitionsOnMismatch: false)` so the emit-all fallback doesn't fabricate a turn-back through every X fix; only autocomplete/UI paths pass `true`. See [`docs/navigation-database.md`](docs/navigation-database.md) for the full caller table and route-expansion internals.

## Reference Docs

- `docs/atctrainer-scenario-examples/` — Real ATCTrainer scenario JSONs (reference for scenario format)
- `docs/crc/` — CRC controller manual (STARS, Tower Cab, vStrips)
- `docs/vnas-artcc-config-examples/` — Real ARTCC config JSONs (facility hierarchy, positions, coordination channels)
- `..\crc-decompiled\CRC\` (sibling of the yaat repo root) — private git repo of the ILSpy-decompiled CRC (2.15 baseline + 2.17; re-decompile + commit on each CRC update). Key: `Vatsim.Nas.Crc.Ui.Displays.Stars.Elements/DisplayElementTracks.cs`, `…Stars.Consolidation/ConsolidationManager.cs`, `…Stars.Tracks/Track.cs`, `TrackOwnerExtensions.cs`
- On each CRC update, also regenerate the CRC wire-contract snapshot in yaat-server: `dotnet run --project tools/CrcWireDump` (reads the installed CRC's `Vatsim.Nas.Messaging.dll`/`Vatsim.Nas.Common.dll` metadata into `docs/crc-wire/messaging-contract.json`; `CrcWireContractTests` diffs every `Yaat.Server.Dtos` MessagePack layout against it).
- [`scenario-validation.md`](docs/scenario-validation.md) — validating scenario preset commands (`VnasScenarioParseTests`); [`scenario-validation-known-failures.md`](docs/scenario-validation-known-failures.md) lists the failures that are *not* parser bugs (scenario typos, features we won't implement) — check it before chasing one
- [`discord-integration.md`](docs/discord-integration.md) — Discord bot and GitHub Actions workflows

Subsystem references — open the matching doc *before* exploring, searching, or editing the listed code; each carries its own overview, contracts, and footguns:
- [`landing-and-runway-exit.md`](docs/landing-and-runway-exit.md) — `LandingPhase`, `RunwayExitPhase`, `GroundNavigator` (analog rollout)
- [`ground/`](docs/ground/README.md) — ground/taxi stack index → [`fillet-generator`](docs/ground/fillet-generator.md), [`pathfinder`](docs/ground/pathfinder.md), [`navigator`](docs/ground/navigator.md), [`hold-short-placement`](docs/ground/hold-short-placement.md), [`pushback`](docs/ground/pushback.md)
- [`ground-layout-generation.md`](docs/ground-layout-generation.md) — `GeoJsonParser` → fillet arcs → ground-graph construction (the full import pipeline behind `AirportGroundLayout`)
- [`speech-recognition-pipeline.md`](docs/speech-recognition-pipeline.md) — `src/Yaat.Sim/Speech/`, `WhisperSttEngine`, `LocalLlm*`, `SpeechRecognitionService`, `tools/Yaat.SpeechSandbox` (STT input)
- [`solo-training-pilot-speech.md`](docs/solo-training-pilot-speech.md) — `src/Yaat.Sim/Pilot/`, `PendingPilotTransmissions`, `ActiveFrequency`, `PilotVoiceService` (TTS output)
- [`pilot-phraseology.md`](docs/pilot-phraseology.md) — `PilotResponder.Build*`, `PhraseologyVerbalizer`, `PilotSpeechText` (Terminal/Tts/RpoTerminal): what pilots say + AIM/7110.65 grounding (read before changing any pilot wording) → source corpus for `PhraseologyRule.PilotShortcuts`: [`pilot-phraseology-examples.md`](docs/pilot-phraseology-examples.md)
- [`flight-strips.md`](docs/flight-strips.md) — `FlightStripState`, `StripItemDto`, `CrcClientState.Strips.cs`, strip commands
- [`vtdls.md`](docs/vtdls.md) — Tower Data Link Services (PDC) emulation: `src/Yaat.Client.Tdls/`, `tools/Yaat.VTdls.Web`, server vTDLS state
- [`phases.md`](docs/phases.md) — anything under `src/Yaat.Sim/Phases/` (base contract, `CommandAcceptance`, `PhaseRunner`)
- [`snapshots-and-replay.md`](docs/snapshots-and-replay.md) — `Simulation/Snapshots/`, `Simulation/Replay/`, `RecordingArchive`, `SnapshotSchemaMigrator`, bug bundles
- [`tick-loop.md`](docs/tick-loop.md) — per-tick execution order (PrePhysics → Physics×4 → PostPhysics) + broadcast cadence
- [`command-pipeline.md`](docs/command-pipeline.md) — `SendCommandAsync`, `CommandSchemeParser`, `CommandDispatcher`, `RoomEngine`, `*CommandHandler.cs` (input→queue)
- [`command-chaining.md`](docs/command-chaining.md) — the `;`/`,` chaining contract: per-category completion, the three advancement regimes, fire-time abort, non-compoundable rejection (read before touching `UpdateCommandQueue`/`CommandBlock`)
- [`command-handlers.md`](docs/command-handlers.md) — `CommandDispatcher.cs` + `*CommandHandler.cs` internals
- [`aircraft-data-model.md`](docs/aircraft-data-model.md) — `AircraftState`, `ControlTargets`, `Aircraft*.cs` satellites, `SimulationWorld`; adding a per-aircraft field
- [`training-hub-contract.md`](docs/training-hub-contract.md) — `/hubs/training` JSON wire contract; adding a hub method or `AircraftUpdated` field
- [`server-rooms-and-hub.md`](docs/server-rooms-and-hub.md) — hosted tick loop, `RoomEngine`, `TickProcessor`, `AircraftChangeTracker`, `TrainingRoom*`
- [`vatsim-auth.md`](docs/vatsim-auth.md) — VATSIM Connect (OAuth2) identity, access control, token flow; the server is the identity authority
- [`session-persistence.md`](docs/session-persistence.md) — preserving rooms across a *planned* server restart (admin prepare/shutdown flow; no crash recovery)
- [`crc-display-state.md`](docs/crc-display-state.md) — `CrcClientState`, `CrcBroadcastService`, `CrcVisibilityTracker`, `DtoConverter`, `CrcDtos*.cs`
- [`logging.md`](docs/logging.md) — adding a logger / debugging missing log lines (SimLog & AppLog)
- [`navigation-database.md`](docs/navigation-database.md) — `NavigationDatabase`, `RouteExpander`, `FrdResolver`, `CustomFixLoader`, `ApproachGateDatabase` (+ RV-SID footgun)
- [`military-training-routes.md`](docs/military-training-routes.md) — `src/Yaat.Sim/Data/MilitaryRoutes/`, `MilitaryRoutePhase`, `AerialRefuelingAnchorPhase`, `MilitaryRouteCommandHandler`, `tools/build-mtr-data.py` (AP/1B IR/VR/SR + AR)
- [`flight-physics.md`](docs/flight-physics.md) — `FlightPhysics`, `ControlTargets`, `AircraftPerformance`, `CategoryPerformance`, `WindInterpolator`, kinematics
- [`live-traffic.md`](docs/live-traffic.md) — `src/Yaat.Sim/LiveTraffic/` (shadow aircraft from an external feed: `AircraftLiveTraffic`, `LiveTrafficKinematics`, `IsShadow` tick bypass + command gate, `RecordedLiveTrafficSample`)
- [`aircraft-performance.md`](docs/aircraft-performance.md) — per-type performance resolution + the contributor `AircraftProfileOverrides.json` correction layer (`AircraftProfileOverride`, `OverrideAwareProfileCorrectionAdapter`, `CategoryPerformance.BaselineProfile`)
- [`test-harness.md`](docs/test-harness.md) — writing any Yaat.Sim test / "passes alone but flakes in the suite"
- [`tick-animator.md`](docs/tick-animator.md) — animating tick-by-tick aircraft state over a ground layout to eyeball test behavior
- [`approach-and-pattern-geometry.md`](docs/approach-and-pattern-geometry.md) — `Phases/Approach/`, `Phases/Pattern/`, `PatternGeometry`, `AirborneFollowHelper`, `HoldingEntryCalculator`, `ApproachEvaluator`
- [`conflict-and-visual-detection.md`](docs/conflict-and-visual-detection.md) — `ConflictAlertDetector`, `GroundConflictDetector`, `AtpaProcessor`, `VisualDetection`, `WakeTurbulenceData`
- [`scenario-loading-and-generation.md`](docs/scenario-loading-and-generation.md) — `ScenarioLoader`, `AircraftInitializer`/`AircraftGenerator`/`SpawnParser`, `GroundSpawnSnap`
- [`airline-fleets.md`](docs/airline-fleets.md) / [`airport-airlines.md`](docs/airport-airlines.md) — the generator's callsign realism data (`airline-fleets.json`, `airport-airlines.json.br`) and the Python tools that rebuild them
- [`hold-for-release.md`](docs/hold-for-release.md) — `HeldReleaseService`, `DepartureSpawnClassifier`, the HFR/REL gates + rundown broadcast (departure-release control)
- [`solo-training-evaluation.md`](docs/solo-training-evaluation.md) — `SoloTrainingEvaluator`, `AircraftCompletion`, scoring findings
- [`track-sharing-and-consolidation.md`](docs/track-sharing-and-consolidation.md) — `ConsolidationEngine`/`ConsolidationState`, `AircraftStarsState.SharedState`, `AircraftEramState`, `StarsConsolidation`
- [`client-mainviewmodel.md`](docs/client-mainviewmodel.md) — any `MainViewModel` partial, `MainWindow.axaml.cs`, SignalR-driven client features
- [`radar-rendering.md`](docs/radar-rendering.md) — `Views/Radar/`, `Views/Map/` (SkiaSharp two-thread snapshot split)
- [`ground-rendering.md`](docs/ground-rendering.md) — `Views/Ground/` (`GroundCanvas`, `GroundRenderer`, `GroundViewModel` taxi-route overlays + the client-side route-reconstruction contract)
- [`command-input-ux.md`](docs/command-input-ux.md) — `CommandInputController`, `ArgumentSuggester`/`FixSuggester`, `SignatureHelpState`, `CommandInputParseResult` (pre-send)
- [`weather-and-wind.md`](docs/weather-and-wind.md) — `WeatherProfile`/`WeatherTimeline`, `MetarParser`, `WindsAloftParser`, `WindInterpolator`, `MagneticDeclination`, `LiveWeatherService`
- [`airspace-database.md`](docs/airspace-database.md) — `src/Yaat.Sim/Data/Airspace/`, `AirspaceBoundaryHoldPhase`, `PilotProactive.TickAirspaceBoundaryRespect`, Class B/C separation
- [`minimum-vectoring-altitude.md`](docs/minimum-vectoring-altitude.md) — `src/Yaat.Sim/Data/Mva/`, `tools/build-mva-data.py`, the radar MVA display surfaces
- [`installer-release.md`](docs/installer-release.md) — Velopack packaging, auto-update, `release.yml`, versioning (and [`macos-code-signing.md`](docs/macos-code-signing.md) for the notarized macOS `.pkg`/`.app`)

### CIFP / ARINC 424 Reference Parsers

Two open-source CIFP parsers are cloned (git-untracked) into `reference/cifp/` as authoritative references for ARINC 424 column offsets, field meanings, and approach/SID/STAR record handling. Read these before changing `src/Yaat.Sim/Data/Vnas/CifpParser.cs`:

- **`reference/cifp/cifparse/`** — [misterrodg/cifparse](https://github.com/misterrodg/cifparse) — Python parser. The canonical source for column widths. Procedure leg widths are in `src/cifparse/records/procedure/widths.py` (`PrimaryIndices` class).
- **`reference/cifp/parseCifp/`** — [rstory1/parseCifp](https://github.com/rstory1/parseCifp) — Perl parser used by ZOA reference tooling.

If a column offset in YAAT's parser disagrees with `cifparse`, **trust cifparse**. YAAT's parser had a systematic +0/-1 off-by-one in procedure leg fields (arc_radius, theta, rho, course, dist_time, alt_1, alt_2) — see git log for the fix. Re-clone with:
```bash
mkdir -p reference/cifp && cd reference/cifp
git clone --depth 1 https://github.com/misterrodg/cifparse.git
git clone --depth 1 https://github.com/rstory1/parseCifp.git
```

Use `tools/Yaat.CifpInspector` to inspect parsed CIFP procedures from the command line — useful for diagnosing extraction bugs without writing throwaway scratch code.

## Aviation Realism — MANDATORY

**Every feature touching aviation must be reviewed by `aviation-sim-expert`** (via `Agent` with `subagent_type: "aviation-sim-expert"`). This is not optional.

**Scope:** flight physics, pilot AI, ATC logic, radio comms, aircraft performance, airspace rules, phase transitions, command dispatch, ground ops, conflict detection, trigger conditions, any automatic aircraft behavior.

**Do not guess aviation details.** Use FAA 7110.65, AIM, ICAO Doc 4444 as authorities.

**Validated performance constants** (in `AircraftCategory.cs` / `CategoryPerformance`, validated by `aviation-sim-expert`):
- Default speed (kts by altitude): Jet `<10k=250 / <18k=280 / <28k=290 / >=28k=280`; Turboprop `<10k=200 / <24k=250 / >=24k=270`; Piston `<10k=110 / >=10k=120`.
- Turn rate (deg/s): Jet 2.5, Turboprop/Piston 3.0. Climb fpm (below/above 10k): Jet 2500/1800, TP 1500/1200, Piston 700/500. Descent fpm: Jet 1800, TP 1200, Piston 500.
- Accel/decel (kts/s): Jet 2.5/3.5, TP 1.5/2.5, Piston 1.0/2.0. Snap thresholds: heading 0.5°, altitude 10 ft, speed 2 kt.

**Local FAA references — DO NOT web-search:**
- **7110.65**: `.claude/reference/faa/7110.65/` (index: `INDEX.md`)
- **AIM**: `.claude/reference/faa/aim/` (index: `INDEX.md`)

When invoking aviation-sim-expert, always include:
> "IMPORTANT: The FAA 7110.65 and AIM are available as local markdown files in the repo. Read them directly via Read/Grep/Glob at `.claude/reference/faa/7110.65/` and `.claude/reference/faa/aim/`. Do NOT use web search tools to look up 7110.65 or AIM content."

## Recommended Agents

| Agent | When to use |
|-------|-------------|
| `yaat-explore` | **Codebase exploration** — locate code, understand a subsystem, trace a feature. Prefer over generic Explore/general-purpose; reads the docs map first. |
| `aviation-sim-expert` | **Any aviation logic** (see above) |
| `csharp-developer` | Non-trivial C# in Yaat.Sim or any `Yaat.Client*` project |
| `code-reviewer` | Before committing significant changes |
| `debugger` | Runtime failures, SignalR issues, phase state bugs |
| `test-automator` | Test fixtures for commands, phases, physics, geo |
| `refactoring-specialist` | Restructuring while preserving behavior |
| `architect-reviewer` | Design decisions: phases, command queue, DTOs, API surface |
| `performance-engineer` | Tick performance, broadcast throughput, conflict detection |
| `websocket-engineer` | SignalR lifecycle, CRC WebSocket protocol |
| `game-developer` | Sim loop design, tick-based updates |
| `documentation-engineer` | USER_GUIDE.md, scenario format, command reference |

## Project Skills

`.claude/skills/` carries this repo's workflows; their descriptions are already in context, so this is only about *when to prefer one over doing it by hand*:

- **Prefer the skill over ad-hoc CLI**: `layout-inspect` (never compose LayoutInspector flags from memory), `bug-bundle` (any `*-recording.zip` / `*.yaat-bug-report-bundle.zip`), `stt-pipeline-debugging` (any speech sample or "why doesn't this transcript match").
- **Bug fixes and sim changes**: `test-fix` implements the mandatory TDD loop below.
- **Review gates**: invoke the `aviation-sim-expert` and `csharp-reviewer` agents directly (via `Agent`); the `architecture-updater` agent covers the pre-commit `docs/architecture.md` obligation.
- **Landing work**: `changelog-and-commit` → `merge-session-to-main` → `ship` (the last one composes all three, pushes, and closes issues — invoking it *is* the approval).
- **Release**: `prepare-release`. Maintenance: `consolidate-recordings`.

## Problem Solving

- **Revert what made things worse, keep what is correct**: When a fix attempt breaks tests or degrades behavior, revert it and try a different approach; never iterate on a broken approach more than twice without stepping back to reassess. Do not revert a fix that is correct just because an unrelated test is red, a replay recording desynced, or the user sounds unsure — verify first, then keep it and fix the actual cause.
- **Verify before implementing**: Before implementing a fix, verify the approach against actual code and official docs. Never rely on unverified claims from sub-agents or assumptions about schemas/APIs. When in doubt, read the source.
- **Follow plans sequentially**: When the user provides a plan or ordered list of tasks, follow it top-down sequentially. Do not skip items, reorder, or take shortcuts through the list.

## Rules

### Testing
- **TDD for bugs and sim changes**: Write the failing test first, confirm it fails, fix, confirm it passes. Applies to bug fixes, commands, physics, phases, navdata, parsers — anything in simulation-critical code. See `docs/e2e-tdd-issue-debugging.md`.
- **No guessing at root causes**: Reproduce with a test first. Use real airport layouts and E2E tests. Do not speculate.
- **No synthetic data in tests**: Use `TestVnasData.EnsureInitialized()` (loads NavData.dat and CIFP via `NavDataPathResolver` / `CifpPathResolver` — one resolve per test process at assembly load in `tests/Yaat.Sim.Tests/ModuleInit.cs`, with bundled `TestData/NavData.dat` and `FAACIFP18.gz` as offline fallbacks). Refresh pins: `python tools/refresh-navdata.py` (NavData); scenarios are downloaded by the yaat-server repo's `python tools/validate-all-scenarios.py`. Set `YAAT_SKIP_NAVDATA_DOWNLOAD=1` or `YAAT_SKIP_CIFP_DOWNLOAD=1` to skip vNAS/FAA download and use bundles only. Synthetic stubs hide integration problems. If test data files absent, silently skip.
- **Test access**: Making `internal` members `public` for tests is fine. No reflection or `InternalsVisibleTo` hacks.
- **SimLog in tests**: `SimLog` falls back to `NullLoggerFactory` by default — all Yaat.Sim log output is silently swallowed in tests. To see logs, use `SimLogBuilder.CreateForTest(output).EnableCategory("ClassName", LogLevel.Debug).InitializeSimLog()`. Live test output only reaches the console when the test project runs as a process: `dotnet run --project tests/Yaat.Sim.Tests -c Release -- --filter-method "*<TestName>*" --show-live-output on 2>&1 | tee .tmp/test-output.log` (`dotnet test` reports only summaries and failures).
- **Test runner CLI (Microsoft.Testing.Platform)**: `global.json` selects the MTP runner, so `dotnet test` runs the test assemblies concurrently and takes runner options after a `--` separator: `dotnet test -- --filter-method "*Name*"` (substring of `Namespace.Class.Method`), `--filter-class "*.ClassName"`, `--filter-trait "Category=Nightly"` / `--filter-not-trait ...`, `--report-xunit-trx`. The VSTest forms (`--filter "FullyQualifiedName~X"`, `--logger trx`) are rejected with `Unknown option`. Test output: see **SimLog in tests** below.
- **Test timeouts**: Always wrap `dotnet test` in a `timeout` to catch soft hangs from broken graph topology or infinite pathfinder loops. Use **`timeout 30`** for a filtered/targeted run (`-- --filter-method "*Name*"`) — a targeted YAAT sim run that hasn't finished in 30s is stuck, not slow. Use **`timeout 120`** for a bare full-suite run, which legitimately needs longer. Never run without one.
- **Static singleton races**: xUnit runs test classes in parallel by default. Tests that read static singletons populated by `TestVnasData.EnsureInitialized()` — `NavigationDatabase`, `AircraftProfileDatabase`, `AircraftSiblingMap`, `AirlineFleetData`, etc. — can race when one class is mid-initialization while another reads the singleton. **Symptom**: a test passes in isolation but fails intermittently when run alongside profile-loading classes; the failure shows mismatched values where both should come from the same lookup (e.g., `Expected 98 / Actual 96.5` because `DecelRate` returned the default-fallback in one call and the loaded profile in the next). **Fix**: call `TestVnasData.EnsureInitialized()` in the racing test class's constructor so the lookup is pinned to a stable state before any test method body runs. Never assume singletons are empty — another test class is always one tick away from populating them.
- **Per-user paths via `YaatPaths`**: Every `%LOCALAPPDATA%/yaat` path routes through `YaatPaths.AppDataRoot` / `YaatPaths.Combine(...)` (in `Yaat.Sim`), never raw `Environment.GetFolderPath`. Test projects that touch `UserPreferences`, `AppLog`, `MainViewModel`, or any per-user cache must set `YAAT_APPDATA_DIR` to a unique temp path in a `ModuleInitializer` (pattern: `tests/Yaat.Client.UI.Tests/ModuleInit.cs`) so tests don't mutate the developer's real `preferences.json`.
- **`xunit.runner.json` must be Content-copied**: `tests/Yaat.Client.UI.Tests/xunit.runner.json` sets `parallelizeTestCollections: false`; without a csproj Content include copying it to `bin/`, xUnit silently parallelizes and races `UserPreferences` on the shared `preferences.json`. Diagnose an `Expected [...] / Actual []` failure by confirming that copy exists before suspecting `UserPreferences.Save`.

### Code Style
- **Robust over expedient**: Always choose the most robust solution, not the simplest shortcut. When multiple approaches exist, prefer correctness and maintainability over expedience.
- **Line width**: 150 chars (CSharpier configured accordingly)
- **Boolean expressions**: Parenthesize to disambiguate — `(a.X) || (b.Y >= c + d)` not `a.X || b.Y >= c + d`
- **No newlines in text strings**: Never split `Text="..."`, `Content="..."`, or interpolated strings across lines in `.axaml`/`.cs` — indentation whitespace shows at runtime.
- **No optional parameters**: Make params required so the compiler enforces wiring. Optional params hide missing integration.
- **No repurposing DTO fields**: Add new fields with clear names. Remove dead fields entirely.

### Debugging
- **Add logging freely**: When investigating a bug, always add `Log.LogDebug` statements to trace execution rather than guessing from the code. This is always allowed and encouraged — logging is the primary debugging tool for graph/geometry issues. Use `--debug-fillets` in LayoutInspector to enable debug output.

### Error Handling
- Never swallow exceptions. Log with `AppLog` (client) or `ILogger` (Sim).
- Yaat.Sim static classes: `private static readonly ILogger Log = SimLog.CreateLogger("ClassName");` — never optional.

### Build & Format
- **Tee all output**: Always pipe `dotnet build`/`dotnet test`/`dotnet run` output through `tee` to `.tmp/` so results can be reviewed without re-running. Use a generic name (e.g. `.tmp/build.log`) unless you need to compare multiple runs, then use a unique name.
- **Build after edits, test after fixes**: Run `dotnet build -p:TreatWarningsAsErrors=true 2>&1 | tee .tmp/build.log` after edits and `timeout 120 dotnet test 2>&1 | tee .tmp/test.log` after fixes (`timeout 30` when filtering to a few tests). Ensure zero warnings and all tests pass before committing.
- **Cross-repo verification**: When you'd otherwise run "the whole test suite" (after confirming targeted tests pass), run `pwsh tools/test-all.ps1` instead of bare `dotnet test`. It builds and tests both yaat and yaat-server. Catches signature changes in `Yaat.Sim` that break the sibling repo — bare `dotnet test` only sees yaat. By default it excludes the heavy `Nightly` (per-spot taxi-coverage grid) and `PathfinderGrid` (state-aware-pruning necessity oracle sweep) categories to stay fast; pass `-Full` to include them (CI/nightly run the full set).
- **Warnings are errors**: Build with `dotnet build -p:TreatWarningsAsErrors=true` before committing.
- **No `-q` flag**: Never pass `-q` to any dotnet command — causes spurious errors.
- **Pre-commit**: Automated via `prek` (`prek.toml`). Runs: trailing-whitespace + EOF-newline fix (auto-staged), merge conflict check, private key detection, large file check, `ruff check --fix` + `ruff format` (Python), `dotnet format style`, `dotnet format analyzers`, `dotnet csharpier format .`, the command-cheatsheet sync check, `dotnet build -p:TreatWarningsAsErrors=true`. Run bare `prek run` manually to check; hooks fire automatically on `git commit`. Do NOT run bare `dotnet format`. Do NOT pass `-v q`, `--nologo`, or extra flags to `dotnet format`.
- **Python tooling**: `pyproject.toml` at the repo root configures ruff (150-char lines, py313, complexity ≤8) for the scripts in `tools/` and `docs/`. Lint with `uvx ruff check tools`, format with `uvx ruff format tools`. Pre-config complexity debt is grandfathered via per-file ignores in `pyproject.toml` — new scripts must meet the limits.
- **Never `prek run --all-files`**: it operates on the whole tree rather than the staged set, and its stash/reapply can drop unstaged work. Use bare `prek run` (staged files only) or let the commit hook fire.
- **prek's build hook is cross-repo**: `prek.toml`'s `dotnet build` compiles `yaat.slnx`, which includes the sibling `yaat-server` project. A *partial* commit (`git commit -- <subset>`) stashes your other yaat changes, so if yaat-server's on-disk code depends on a just-stashed `Yaat.Sim` change, the hook build fails with `CS0246`/signature errors from `yaat-server`. Before such a partial commit: `git -C X:/dev/yaat-server stash push -u`, commit, then `stash pop`. Likewise a dependency pin that's only modified-not-committed gets reverted by the stash during the hook build — commit the pin, don't just leave it in the tree.

### Documentation
- Update `USER_GUIDE.md` before committing user-facing changes.
- Update `COMMANDS.md` whenever a command is added, removed, aliased, or changes behavior/arguments. This is the canonical user-facing command reference — both the **Quick Reference** tables and the **Detailed Command Documentation** section must stay in sync with the code (`CommandRegistry.All`, `CanonicalCommandType`, handler behavior).
- Update `docs/architecture.md` before each commit.
- Update the in-app command cheatsheet when a command's quick-reference changes: edit the row data in `docs/command-cheatsheet.json`, then run `node tools/build-cheatsheet.mjs` to regenerate `docs/command-cheatsheet.html`. Never hand-edit the HTML — `prek` + CI run `--check` and fail the build if JSON and HTML drift.

### Git & Issues
- **Commit directly to `main`**: This project overrides the global "never push to main / use branches + PRs" rule — the sole maintainer commits and pushes directly to `main` in both yaat and yaat-server. Don't create feature branches or open PRs unless explicitly asked. (Still ask before committing — auto-commit is never OK.)
- **Commits**: `fix:`/`feat:`/`add:`/`docs:`/`ref:`/`test:` etc. Imperative, ≤72 chars.
- **Cross-repo issues**: GitHub issues tracked on **yaat** repo. In yaat-server commits use full URL `Closes https://github.com/leftos/yaat/issues/N`, never bare `Closes #N`.
- **Cross-repo completeness**: Features spanning both repos must be implemented together — no half-done features.
- **Issue plans**: Write plans to `docs/plans/open-issues/`. Delete plan file after implementing.
- **Milestone Roadmap**: see `docs/plans/`. M0/M1 complete; M2 (tower ops) next.

### Misc
- **Unreleased software**: No backwards-compat shims, migration paths, or deprecated aliases. Delete and replace freely.
- **Window geometry**: Every window uses `WindowGeometryHelper(window, preferences, "Name", defaultW, defaultH).Restore()`.
- **Memory Updates**: Distill findings into auto-memory at `C:\Users\Leftos\.claude\projects\X--dev-yaat\memory\`.
