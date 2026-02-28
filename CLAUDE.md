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

Two projects in `yaat.slnx`:

```
src/Yaat.Client/        # Avalonia 11 desktop app (MVVM, executable)
  Logging/              # AppLog static factory + FileLoggerProvider
  Models/               # ObservableObject data models ([ObservableProperty] source-gen'd)
  Services/             # SignalR client, command parsing, user preferences
  ViewModels/           # [RelayCommand] view models, value converters
  Views/                # Avalonia AXAML views + code-behind

src/Yaat.Sim/           # Shared simulation library (class library, no dependencies)
  AircraftState.cs      # Mutable aircraft state with flight plan fields
  AircraftCategory.cs   # AircraftCategorization (static lookup) + CategoryPerformance constants
  ControlTargets.cs     # Target heading/altitude/speed/navigation for physics interpolation
  CommandQueue.cs       # CommandBlock/BlockTrigger/TrackedCommand for chained command execution
  FlightPhysics.cs      # 6-step Update: navigation → heading → altitude → speed → position → queue
  SimulationWorld.cs    # Thread-safe aircraft collection with tick loop (per-scenario support)
  Commands/             # CanonicalCommandType enum (FlyHeading, ClimbMaintain, Speed, etc.)
  Data/                 # CustomFixDefinition/Loader for scenario-defined waypoints
```

**Yaat.Client** is the Avalonia desktop app. **Yaat.Sim** is a standalone library shared with yaat-server (referenced by both projects).

**Yaat.Sim owns all simulation and aviation logic.** All flight physics, phase behavior, pattern geometry, performance constants, command dispatching logic that builds/mutates phase sequences — everything that constitutes aviation knowledge or simulation behavior belongs in Yaat.Sim. The server (yaat-server) should be a thin layer: comms with CRC and Yaat.Client, scenario loading, hub plumbing. If code in yaat-server needs to build phases, compute pattern geometry, or make aviation decisions, move it to Yaat.Sim instead.

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
When adding new commands, match the existing command names from ATCTrainer and/or VICE where possible:
- ATCTrainer reference: https://atctrainer.collinkoldoff.dev/docs/commands
- VICE reference: https://pharr.org/vice/
Commands unique to YAAT (not present in either app) can use any suitable name.

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
- Avalonia UI 11.2.5 with Fluent theme (dark mode) + DataGrid
- CommunityToolkit.Mvvm 8.4.0 for MVVM source generators
- Microsoft.AspNetCore.SignalR.Client 10.0.3

## Related Repositories

- **yaat-server** (`X:\dev\yaat-server`) — ASP.NET Core 10 server with simulation engine, CRC protocol, training hub
- **vatsim-server-rs** (`X:\dev\vatsim-server-rs`) — Rust reference implementation for CRC protocol (DTO field ordering, varint framing)
- **lc-trainer** (`X:\dev\lc-trainer`) — Previous WPF ATC trainer (reference for flight physics, scenario format)

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

**How to invoke:** Use `Task` with `subagent_type: "aviation-sim-expert"` and a clear description of what needs review or design. The agent has access to all tools and can read the codebase.

**Do not guess aviation details.** Real-world ATC and flight operations have strict, well-defined rules (FAA 7110.65, AIM, ICAO Doc 4444). Getting them wrong breaks the training value of the simulator. When in doubt, ask the aviation-sim-expert agent rather than approximating.

**Local FAA reference library — DO NOT web-search for these:**
The full text of the FAA 7110.65 and AIM are available locally as markdown. Use `Read`, `Grep`, and `Glob` on these paths instead of web searches:
- **7110.65**: `C:\Users\Leftos\.claude\reference\faa\7110.65/` (index: `INDEX.md`)
- **AIM**: `C:\Users\Leftos\.claude\reference\faa\aim/` (index: `INDEX.md`)
- **Top-level index**: `C:\Users\Leftos\.claude\reference\faa\INDEX.md`

When invoking the aviation-sim-expert agent, always include this instruction in the prompt:
> "IMPORTANT: The FAA 7110.65 and AIM are available as local markdown files. Read them directly via the Read/Grep/Glob tools at `C:\Users\Leftos\.claude\reference\faa\7110.65/` and `C:\Users\Leftos\.claude\reference\faa\aim/`. Do NOT use web search tools (Exa, WebSearch, WebFetch) to look up 7110.65 or AIM content."

## User Guide

`USER_GUIDE.md` documents all user-facing features: commands, UI controls, keyboard shortcuts, etc. **Before each commit that adds, changes, or removes user-facing behavior, update USER_GUIDE.md to reflect the current state.** Keep it accurate — don't document features that aren't implemented, and remove documentation for features that are removed.

## Error Handling

Never swallow errors silently. No empty catch blocks without logging, no early returns on error without logging. At minimum, log the exception with `AppLog` (client) or `ILogger` (Yaat.Sim callers) — even if the intent is to prevent a crash. If you catch an exception, log it.

## Commits

Prefix subject with a ≤4-char type tag: `fix:` `feat:` `add:` `docs:` `ref:` `test:` `ci:` `dep:` `chore:` etc. Imperative mood, ≤72 char subject line.

## Milestone Roadmap

The project follows milestones defined in `docs/plans/main-plan.md`. M0 and M1 are complete; M2 (tower operations) is next. Pilot AI architecture is designed in `docs/plans/pilot-ai-architecture.md`.
