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

- .NET 8, C# with nullable enabled, implicit usings
- Avalonia UI 11.2.5 with Fluent theme (dark mode) + DataGrid
- CommunityToolkit.Mvvm 8.4.0 for MVVM source generators
- Microsoft.AspNetCore.SignalR.Client 8.0.12

## Related Repositories

- **yaat-server** (`X:\dev\yaat-server`) — ASP.NET Core 8 server with simulation engine, CRC protocol, training hub
- **vatsim-server-rs** (`X:\dev\vatsim-server-rs`) — Rust reference implementation for CRC protocol (DTO field ordering, varint framing)
- **lc-trainer** (`X:\dev\lc-trainer`) — Previous WPF ATC trainer (reference for flight physics, scenario format)

> **lc-trainer is NOT a trusted reference.** It is WIP, flawed, and unreviewed. It may be used as inspiration but every aviation detail drawn from it MUST be reviewed by the `aviation-sim-expert` agent. Do not port code from lc-trainer without independent validation. Prefer a fresh, well-organized approach over copying its patterns.

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

## Commits

Prefix subject with a ≤4-char type tag: `fix:` `feat:` `add:` `docs:` `ref:` `test:` `ci:` `dep:` `chore:` etc. Imperative mood, ≤72 char subject line.

## Milestone Roadmap

The project follows milestones defined in `docs/plans/main-plan.md`. M0 and M1 are complete; M2 (tower operations) is next. Pilot AI architecture is designed in `docs/plans/pilot-ai-architecture.md`.
