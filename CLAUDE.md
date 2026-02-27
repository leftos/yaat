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
src/Yaat.Client/     # Avalonia 11 desktop app (MVVM, executable)
  Models/            # ObservableObject data models ([ObservableProperty] source-gen'd)
  Services/          # SignalR client (ServerConnection) + DTOs (AircraftDto, SpawnAircraftDto)
  ViewModels/        # [RelayCommand] view models, ConnectButtonConverter
  Views/             # Avalonia AXAML views + code-behind

src/Yaat.Sim/        # Shared simulation library (class library, no dependencies)
  AircraftState.cs   # Mutable aircraft state with flight plan fields
  FlightPhysics.cs   # Position update from heading + groundspeed
  SimulationWorld.cs # Thread-safe aircraft collection with tick loop
```

**Yaat.Client** is the Avalonia desktop app. **Yaat.Sim** is a standalone library intended to be shared with yaat-server (not currently referenced by Yaat.Client).

**Key patterns:**
- `ServerConnection` is the single SignalR client connecting to `/hubs/training` (JSON protocol, not MessagePack). DTOs (`AircraftDto`, `SpawnAircraftDto`) are records defined in the same file.
- ViewModels use `[ObservableProperty]` and `[RelayCommand]` from CommunityToolkit.Mvvm — fields are `_camelCase`, auto-generated properties are `PascalCase`
- SignalR callbacks arrive on a background thread; ViewModels marshal to UI via `Avalonia.Threading.Dispatcher.UIThread.Post()`
- No DI container — `MainWindow` creates `MainViewModel` directly, which instantiates `ServerConnection` as a field
- `SimulationWorld.GetSnapshot()` returns a shallow list copy; callers should treat returned `AircraftState` objects as read-only

**Command scheme:** RPO commands are parsed client-side using a configurable `CommandScheme` (ATCTrainer or VICE presets). The client translates user input into canonical ATCTrainer format before sending to the server. The server only understands canonical format.

**Communication flow:**
```
YAAT Client (this repo)  ──SignalR JSON──>  yaat-server  <──SignalR+MessagePack──  CRC
     /hubs/training                                           /hubs/client
```

The training hub uses standard ASP.NET SignalR with JSON. The CRC hub uses raw WebSocket with varint+MessagePack binary framing (handled entirely by yaat-server).

**SignalR hub methods used by the client:**
- `GetAircraftList()` → `List<AircraftDto>` — called on connect
- `SpawnAircraft(SpawnAircraftDto)` — spawns a new aircraft
- `AircraftUpdated` (server→client event) — pushed on each sim tick

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

## Commits

Prefix subject with a ≤4-char type tag: `fix:` `feat:` `add:` `docs:` `ref:` `test:` `ci:` `dep:` `chore:` etc. Imperative mood, ≤72 char subject line.

## Milestone Roadmap

The project follows milestones defined in `docs/plans/main-plan.md`. Currently at Milestone 0 (proof of concept): basic connection, aircraft list display, spawn functionality. Pilot AI architecture is designed in `docs/plans/pilot-ai-architecture.md`.
