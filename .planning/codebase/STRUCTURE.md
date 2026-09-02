# Codebase Structure

**Analysis Date:** 2026-09-01

## Directory Layout

```
yaat/                                  # this repo — client + shared sim library
├── src/
│   ├── Yaat.Sim/                      # shared simulation library (no UI deps, no project refs)
│   ├── Yaat.Client/                   # Avalonia desktop app (entry point)
│   ├── Yaat.Client.Core/              # LM-Kit-free shared client services (SignalR, prefs, auth)
│   ├── Yaat.Client.Strips/            # vStrips UI, WASM-clean
│   └── Yaat.Client.Tdls/              # vTDLS UI, WASM-clean
├── tests/
│   ├── Yaat.Sim.Tests/                # sim/command/phase/parser tests + TestData fixtures
│   ├── Yaat.Client.Tests/             # view-model / command-input logic tests
│   └── Yaat.Client.UI.Tests/          # headless Avalonia window tests
├── tools/                             # dev tools, WASM front-ends, data-refresh scripts
│   ├── Yaat.CifpInspector/            # CIFP procedure inspection CLI
│   ├── Yaat.GuideCapture/             # regenerates USER_GUIDE.md screenshots
│   ├── Yaat.LayoutInspector/          # ground-graph query/render CLI (see layout-inspect skill)
│   ├── Yaat.RecordingConsolidator/    # dedupes recording .zip test fixtures
│   ├── Yaat.ScenarioValidator/        # scenario JSON validation CLI
│   ├── Yaat.Scratch/                  # empty throwaway scratchpad project
│   ├── Yaat.SpeechSandbox/            # speech pipeline sandbox GUI
│   ├── Yaat.VStrips.Web/              # Avalonia Browser/WASM front-end over Yaat.Client.Strips
│   ├── Yaat.VTdls.Web/                # Avalonia Browser/WASM front-end over Yaat.Client.Tdls
│   ├── mcp/                           # MCP stdio adapter scripts (Context7, Exa)
│   ├── hooks/                         # prek/Claude Code guard hook scripts
│   ├── bug_bundle.py                  # bug-bundle inspection CLI (see bug-bundle skill)
│   └── *.py / *.ps1                   # data-refresh scripts (navdata, CIFP, airlines, MTR, MVA, deploy)
├── docs/                              # ~50 hand-maintained subsystem docs + architecture.md file-tree index
│   ├── architecture.md                # annotated full file tree + task index (read before exploring)
│   ├── README.md                      # docs index
│   ├── ground/                        # ground/taxi stack sub-index (fillet, pathfinder, navigator, hold-short, pushback)
│   ├── command-aliases/               # per-command alias JSON data
│   ├── atctrainer-scenario-examples/  # reference ATCTrainer scenario JSONs
│   ├── vnas-artcc-config-examples/    # reference vNAS ARTCC config JSONs
│   ├── crc/                           # CRC controller manual mirror
│   └── plans/                         # active + archived planning docs (docs/plans/MAIN.md pattern)
├── reference/cifp/                    # git-untracked clones of cifparse/parseCifp (ARINC 424 reference parsers)
├── .claude/                           # skills, agents, commands, hook scripts, local FAA reference (7110.65/AIM)
├── .planning/                         # GSD workflow state (this file's own directory)
├── build/                             # packaging assets (e.g. build/macos/ entitlements, Info.plist template)
├── yaat.slnx                          # solution file (.slnx format)
├── CLAUDE.md                          # primary agent-facing project instructions
├── COMMANDS.md                        # canonical user-facing command reference (quick ref + detailed docs)
├── USER_GUIDE.md / SOLO_TRAINING.md   # end-user docs
└── CHANGELOG.md

yaat-server/                            # sibling repo (NOT under this repo root) — server process
└── src/Yaat.Server/                   # Hubs/, Simulation/ (RoomEngine, TickProcessor path), Auth/, Data/, Dtos/, LiveTraffic/, Udp/, Soak/
```

## Directory Purposes

**`src/Yaat.Sim/`:**
- Purpose: every piece of aviation/simulation logic — shared verbatim between the client and yaat-server
- Contains: `Simulation/` (world, engine, snapshots/replay, recording archive), `Phases/` (`Approach/`, `Ground/`, `Pattern/`, `Tower/` subfolders + base `Phase.cs`), `Commands/` (parser, dispatcher, one `*CommandHandler.cs` per command family), `Data/` (`Vnas/` navdata+CIFP, `Airspace/`, `Mva/`, `Faa/`, `Artcc/`, `ARTCCs/`, `FacilityOps/`, `MilitaryRoutes/`, plus flat reference JSON like `AircraftProfiles.json`, `AircraftProfileOverrides.json`), `Pilot/` (pilot AI + phraseology), `Speech/` (STT engines), `ControllerAi/`, `Scenarios/` (loader/generator), `LiveTraffic/` (shadow-aircraft feed), `Asdex/`, `Training/`, `Testing/` (test-only helpers exported from the library, e.g. `TestVnasData`, `SimLogBuilder`)
- Key files: `Simulation/SimulationEngine.cs`, `Simulation/SimulationWorld.cs` (implied by `Tick`), `Commands/CommandDispatcher.cs`, `Commands/CommandRegistry.cs`, `Phases/PhaseRunner.cs`, `Data/Vnas/NavigationDatabase.cs`, `Data/Vnas/CifpParser.cs`

**`src/Yaat.Client/`:**
- Purpose: the desktop application — the only project allowed to reference LM-Kit.NET (speech)
- Contains: `Models/` (client-only observable wrappers, e.g. `AircraftModel.cs`), `Services/` (command input, autocomplete, speech playback, favorites, video maps, CRC alias execution), `ViewModels/` (`MainViewModel.cs` + partials by concern: `.Rooms.cs`, `.Aircraft.cs`, `.Scenario.cs`, `.Weather.cs`, `.Timeline.cs`, `.Bookmarks.cs`, `.Strips.cs`, etc.; plus `GroundViewModel.cs`, `RadarViewModel.cs`, `SettingsViewModel.cs`), `Views/` (AXAML + code-behind, radar/ground canvases, dialogs, pop-out windows)
- Generated/build output: `bin/`, `obj/` (never edit; git-ignored)

**`src/Yaat.Client.Core/`:**
- Purpose: shared client plumbing usable by both the desktop app and (indirectly, via Strips/Tdls) the browser front-ends, without pulling in LM-Kit/PortAudio/SharpHook
- Contains: `Logging/AppLog.cs`, `Services/ServerConnection.cs` (the SignalR hub client + inline DTOs), `Services/VatsimAuthClient.cs`, `Services/UserPreferences.cs`, `Services/UpdateService.cs` (Velopack), `Services/ClientVersionGate.cs`, `Services/CrcAlias*.cs`, `Models/`, `ViewModels/ConnectViewModel.cs`, `Views/` (connect dialog, `WindowGeometryHelper.cs`, `WindowGroupRaiser.cs`)

**`src/Yaat.Client.Strips/` and `src/Yaat.Client.Tdls/`:**
- Purpose: WASM-publishable UI layers — zero Avalonia.Desktop/Velopack/file-IO dependency so the browser bundle stays clean
- Contains (Strips): `Services/` (DTOs, `IStripsTransport`/`BrowserStripsTransport`), `ViewModels/` (`VStripsViewModel.cs`, per-strip/bay/rack VMs), `Find/` (shared Ctrl+F logic, reused by Tdls), `Views/VStrips/`, `Resources/Fonts/` (embedded JetBrains Mono)
- Contains (Tdls): mirrors Strips' shape — `Services/TdlsDtos.cs`, `ITdlsTransport`/`BrowserTdlsTransport`, `ViewModels/VTdlsViewModel.cs`, `Views/VTdls/`

**`tests/`:**
- Purpose: xUnit v3 test projects, one per source-tier boundary
- `Yaat.Sim.Tests/`: commands, phases, physics, parsers, nav data; `TestData/` holds `NavData.dat`, `FAACIFP18.gz`, airport GeoJSON, and recording `.zip` fixtures (e.g. `oak-u-w-fillet-corner-recording.zip`)
- `Yaat.Client.Tests/`: view-model and command-input logic (no windowing)
- `Yaat.Client.UI.Tests/`: headless Avalonia window tests; carries its own `xunit.runner.json` (must stay Content-copied to `bin/` or tests silently race)

**`tools/`:**
- Purpose: developer-facing CLIs, data-refresh scripts, and the two WASM front-end projects
- `Yaat.LayoutInspector/`, `bug_bundle.py`: primary debugging tools, each with a dedicated project skill (`layout-inspect`, `bug-bundle`) — prefer the skill over composing CLI flags from memory
- `*.py`/`*.ps1` at the top level: one-shot or periodic data-refresh scripts (NavData, CIFP, airline fleets, MTR, MVA, ARTCC boundaries, deploy scripts) — each has an inline header comment describing its cadence

**`docs/`:**
- Purpose: the canonical, hand-maintained subsystem reference set — read before exploring or editing matching code
- `architecture.md`: full annotated file tree + "I need to change X, which files?" task index — the fastest way to locate code
- `README.md`: docs index/entry point
- ~50 subsystem docs at the top level (tick-loop, command-pipeline, phases, aircraft-data-model, radar-rendering, etc.) — each is scoped to specific source paths and listed in `CLAUDE.md`'s "Subsystem references" table
- `ground/README.md`: sub-index for the ground/taxi stack (fillet-generator, pathfinder, navigator, hold-short-placement, pushback)
- `plans/`: `docs/plans/MAIN.md` is the entry-point plan file; `docs/plans/open-issues/` holds per-issue plans (deleted after implementation); `docs/plans/archive/` holds completed subplans

**`.claude/`:**
- Purpose: Claude Code project configuration
- Contains: `skills/` (project-specific workflows, e.g. `layout-inspect`, `bug-bundle`, `test-fix`, `changelog-and-commit`, `ship`), `agents/` (subagent definitions, e.g. `yaat-explore`, `aviation-sim-expert`, `csharp-developer`), `commands/`, `reference/faa/7110.65/` and `reference/faa/aim/` (local FAA reference markdown — read directly, never web-search), `settings.json`/`settings.local.json` (hooks/guards)

## Key File Locations

**Entry Points:**
- `src/Yaat.Client/Program.cs`: desktop app process entry, builds the Avalonia `AppBuilder`
- `src/Yaat.Client/App.axaml.cs`: `Application` subclass, `OnFrameworkInitializationCompleted` wires the desktop lifetime and `MainWindow`
- `tools/Yaat.VStrips.Web/Program.cs`, `tools/Yaat.VTdls.Web/Program.cs`: WASM browser entry points
- (cross-repo) `..\yaat-server\src\Yaat.Server\Program.cs` → `YaatHost.BuildAsync`

**Configuration:**
- `yaat.slnx`: solution project list (`.slnx` format, not `.sln`)
- `*.csproj` per project: `<ProjectReference>` declares the dependency graph — treat as ground truth over prose docs
- `.claude/settings.json`: Claude Code hooks/guards (tracked, shared with contributors)
- Per-user runtime config lives under `%LOCALAPPDATA%/yaat/` (`preferences.json`, `favorites/`, `cache/airports/`), always accessed through `YaatPaths` (`src/Yaat.Sim/` — never raw `Environment.GetFolderPath`)

**Core Logic:**
- `src/Yaat.Sim/Commands/CommandDispatcher.cs`: command application entry point, `CommandResult` return type
- `src/Yaat.Sim/Commands/CommandRegistry.cs` + `CanonicalCommandType.cs`: the completeness-enforced command catalog
- `src/Yaat.Sim/Phases/PhaseRunner.cs` + `Phase.cs`: phase lifecycle contract
- `src/Yaat.Sim/Simulation/SimulationEngine.cs`: per-tick orchestration methods shared by tests/replay and (cross-repo) the live server
- `src/Yaat.Client/ViewModels/MainViewModel.cs` (+ partials): client-side command send pipeline and all room/aircraft/scenario mirroring

**Testing:**
- `tests/Yaat.Sim.Tests/TestData/`: NavData/CIFP bundles + manifests, airport GeoJSON, recording fixtures
- `tests/Yaat.Sim.Tests/ModuleInit.cs`: `TestVnasData.EnsureInitialized()` assembly-load hook
- `tests/Yaat.Client.UI.Tests/ModuleInit.cs`: sets `YAAT_APPDATA_DIR` to an isolated temp path per test process

## Naming Conventions

**Files:**
- One public type per file, file name matches the type name exactly (`CommandDispatcher.cs` → `class CommandDispatcher`)
- Partial classes split by concern with a dotted suffix: `MainViewModel.Rooms.cs`, `MainViewModel.Aircraft.cs`, `CrcClientState.Strips.cs` (yaat-server) — the base file (`MainViewModel.cs`) holds the constructor/shared fields, each partial owns one feature area
- Command handlers: `<Domain>CommandHandler.cs` (`FlightCommandHandler.cs`, `GroundCommandHandler.cs`, `ApproachCommandHandler.cs`) — one per command family, paired with a `<Domain>CommandParser.cs` where parsing is nontrivial
- DTOs: `<Thing>Dto.cs` (`AircraftSnapshotDto.cs`, `StripItemDto.cs`); wire-format record collections sometimes grouped in one file (`StripDtos.cs`, `TdlsDtos.cs`)
- Test files: `<TypeUnderTest>Tests.cs`, grouped into subfolders that mirror the source area under test (`Pathfinding/`, `Fillet/`, `Simulation/GroundTaxi/`)

**Directories:**
- Project directories are `Yaat.<Area>` matching the `.csproj`/assembly name (`Yaat.Sim`, `Yaat.Client.Strips`)
- Inside a project, top-level folders are MVVM/domain buckets: `Models/`, `Services/`, `ViewModels/`, `Views/` (client tiers) or `Phases/`, `Commands/`, `Data/`, `Simulation/` (Yaat.Sim)
- `Phases/` subfolders are named by flight regime, not by class category: `Approach/`, `Ground/`, `Pattern/`, `Tower/`
- `Data/` subfolders are named by data source/domain: `Vnas/`, `Airspace/`, `Mva/`, `Faa/`, `MilitaryRoutes/`

## Where to Add New Code

**New command:**
- Handler + parser (if nontrivial): `src/Yaat.Sim/Commands/<Domain>CommandHandler.cs` (+ `<Domain>CommandParser.cs`)
- Register the verb in `CanonicalCommandType` and `CommandScheme.Default()` AND `CommandRegistry.All` (all three or tests fail — `docs/architecture.md` "Integration Footguns")
- Update `COMMANDS.md` (canonical user-facing reference) and `docs/command-cheatsheet.json` (regenerate `docs/command-cheatsheet.html` via `node tools/build-cheatsheet.mjs`)
- Tests: `tests/Yaat.Sim.Tests/` in the subfolder matching the command's domain

**New phase:**
- New class under `src/Yaat.Sim/Phases/<Regime>/` implementing `Phase.cs`'s contract
- Register in `PhaseList.cs`; add a `[JsonDerivedType]` entry in `PhaseSnapshotDto.cs` for serialization; wire acceptance rules in `CommandDispatcher.cs`
- Tests: `tests/Yaat.Sim.Tests/Phases/` (subfolder pattern mirrors source)

**New client feature (view model + view):**
- If it's desktop-only (LM-Kit/PortAudio/SharpHook, or genuinely single-surface UI): `src/Yaat.Client/{Models,Services,ViewModels,Views}/`
- If it must also work in the browser WASM front-ends: put shared logic in `Yaat.Client.Core` (needs file IO/Avalonia.Desktop) or `Yaat.Client.Strips`/`Yaat.Client.Tdls` (must stay WASM-clean) — never duplicate into `Yaat.Client` and the web tools separately
- Large view models use the partial-class-per-concern pattern (`MainViewModel.<Concern>.cs`); follow it for any new cross-cutting `MainViewModel` feature rather than growing the base file

**New wire contract:**
- SignalR/JSON: add a record to `src/Yaat.Client.Core/Services/ServerConnection.cs` (client) and the matching yaat-server `Dtos/`/Hub method — register any new type in the relevant `*HubJsonContext.cs` source-generated context
- CRC/MessagePack (cross-repo, yaat-server only): additive per-topic, explicit `Delete*` — never repurpose an existing field (project rule: "No repurposing DTO fields")

**Data-refresh scripts:**
- New periodic data-pull script goes in `tools/` as a standalone `.py`/`.ps1`, with a one-line header comment describing what it fetches/writes and its refresh cadence — follow the existing `refresh-*.py`/`build-*.py` naming pattern

## Special Directories

**`src/*/bin/`, `src/*/obj/`, `tests/*/bin/`, `tests/*/obj/`:**
- Purpose: MSBuild output
- Generated: Yes
- Committed: No

**`reference/cifp/`:**
- Purpose: git-untracked clones of two open-source ARINC 424 CIFP parsers (cifparse, parseCifp), kept as an authoritative column-offset reference for `CifpParser.cs`
- Generated: cloned on demand (`git clone --depth 1 ...`, commands in `CLAUDE.md`)
- Committed: No (untracked by design)

**`.tmp/`:**
- Purpose: scratch output for build/test logs (`dotnet build ... | tee .tmp/build.log`), per project convention
- Generated: Yes
- Committed: No (gitignored)

**`docs/plans/`:**
- Purpose: active planning documents; `MAIN.md` is the always-current entry point, task checkboxes track progress
- Generated: No (hand-maintained)
- Committed: Yes, but pruned — completed subplans move to `docs/plans/archive/`, issue-specific plans under `docs/plans/open-issues/` are deleted once implemented

**`build/macos/`:**
- Purpose: macOS packaging assets — entitlements, `Info.plist` template, code-signing inputs for the notarized `.pkg`/`.app` (see `docs/macos-code-signing.md`)
- Generated: No
- Committed: Yes

---

*Structure analysis: 2026-09-01*
