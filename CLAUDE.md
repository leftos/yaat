# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

YAAT (Yet Another ATC Trainer) is an instructor/RPO desktop client for air traffic control training. It connects to a separate [yaat-server](https://github.com/leftos/yaat-server) instance via SignalR and displays/controls simulated aircraft. The server also feeds CRC (the VATSIM radar client) via a CRC-compatible SignalR+MessagePack hub.

## Build & Run

```bash
cd src/Yaat
dotnet build
dotnet run
```

Requires .NET 8 SDK and a running yaat-server instance (default `http://localhost:5000`).

## Architecture

**Single-project Avalonia 11 desktop app** using MVVM with CommunityToolkit.Mvvm source generators.

```
src/Yaat/
  Models/          # ObservableObject data models (source-gen'd properties via [ObservableProperty])
  Services/        # SignalR client (ServerConnection) + DTOs
  ViewModels/      # MVVM view models with [RelayCommand] source generators
  Views/           # Avalonia AXAML views + code-behind
```

**Key patterns:**
- `ServerConnection` is the single SignalR client connecting to `/hubs/training` (JSON protocol, not MessagePack)
- ViewModels use `[ObservableProperty]` and `[RelayCommand]` from CommunityToolkit.Mvvm — properties are declared as `_camelCase` fields and auto-generated as `PascalCase` properties
- UI thread dispatch via `Avalonia.Threading.Dispatcher.UIThread.Post()` for SignalR callbacks
- `ConnectButtonConverter` is an `IValueConverter` for toggling Connect/Disconnect button text

**Communication flow:**
```
YAAT Client (this repo)  ──SignalR JSON──>  yaat-server  <──SignalR+MessagePack──  CRC
     /hubs/training                                           /hubs/client
```

The training hub uses standard ASP.NET SignalR with JSON. The CRC hub uses raw WebSocket with varint+MessagePack binary framing (handled entirely by yaat-server).

## Tech Stack

- Avalonia UI 11.2.x with Fluent theme (dark mode)
- CommunityToolkit.Mvvm 8.4 for MVVM source generators
- Microsoft.AspNetCore.SignalR.Client for server communication
- .NET 8, C# with nullable enabled

## Related Repositories

- **yaat-server** (`X:\dev\yaat-server`) — ASP.NET Core 8 server with simulation engine, CRC protocol, training hub
- **vatsim-server-rs** (`X:\dev\vatsim-server-rs`) — Rust reference implementation for CRC protocol (DTO field ordering, varint framing)
- **lc-trainer** (`X:\dev\lc-trainer`) — Previous WPF ATC trainer (reference for flight physics, scenario format)

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

## Milestone Roadmap

The project follows milestones defined in `docs/plans/main-plan.md`. Currently at Milestone 0 (proof of concept): basic connection, aircraft list display, spawn functionality.
