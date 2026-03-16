# YAAT

Yet Another ATC Trainer — an instructor/RPO desktop client for air traffic control training. Connects to a [yaat-server](https://github.com/leftos/yaat-server) instance that simulates aircraft and feeds them to [CRC](https://vnas.vatsim.net/crc) (the VATSIM radar client) via its native SignalR+MessagePack protocol.

Instructors and RPOs use YAAT to create training rooms, load scenarios, issue ATC commands, control weather, and manage simulated traffic while students work the scopes in CRC.

## Features

- **Cross-platform** — built to run natively on Windows, MacOS, and Linux
- **Training rooms** — create or join isolated rooms; multiple concurrent sessions supported
- **Scenario management** — load predefined traffic scenarios with automatic aircraft spawning
- **Command input** — text-based ATC commands with autocomplete, macros, and compound syntax
- **Ground view** — SkiaSharp-rendered airport surface map with taxi routing and context menus
- **Radar view** — STARS-style radar display with video maps, range rings, and data blocks
- **Weather** — load ATCTrainer-compatible wind/weather profiles; wind affects aircraft physics
- **Full nav data** — VNAS protobuf nav data, FAA CIFP procedures (SIDs, STARs, approaches), and aircraft performance specs downloaded automatically
- **CRC integration** — CRC clients connect to the same server and see all simulated traffic

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [yaat-server](https://github.com/leftos/yaat-server) cloned as a sibling directory (private repo — access required)
- **Linux only:** `libfontconfig1` and `libfreetype6` (required by SkiaSharp for font rendering)

### Quick Start

The included `start.ps1` / `start.sh` script builds both projects and launches the server and client together. Press Ctrl-C to stop the server.

```powershell
.\start.ps1
```

The client auto-connects to the server on startup. Create or join a training room, load a scenario, and start issuing commands.

### Building and Running Manually

**Server** (from the yaat-server repo):

```bash
dotnet build src/Yaat.Server
dotnet run --project src/Yaat.Server
```

The server listens on `http://localhost:5000` by default.

**Client** (from this repo):

```bash
dotnet build
dotnet run --project src/Yaat.Client
```

The client connects to `http://localhost:5000` by default.

**CRC setup** (for students connecting via the radar client): see the [User Guide — Connecting CRC](USER_GUIDE.md#connecting-crc-optional) for setup script and manual configuration options.

## Project Structure

```
src/Yaat.Client/    Avalonia desktop app (instructor/RPO UI)
  Models/           Observable data models (aircraft, terminal entries)
  Services/         SignalR client, command scheme, macros, nav data
  ViewModels/       MVVM view models (main, ground, radar, settings)
  Views/            Avalonia AXAML views, SkiaSharp canvases

src/Yaat.Sim/       Shared simulation library (no UI dependencies)
  Commands/         Command parsing, dispatch, approach/departure handlers
  Data/             Nav data, CIFP, airport ground layouts, video maps
  Phases/           Clearance-gated flight phases (tower, approach, ground, pattern)
  Scenarios/        Aircraft initialization and spawning

tests/              Unit tests for both projects
```

## Tech Stack

- [.NET 10](https://dotnet.microsoft.com/) / C# with nullable reference types
- [Avalonia UI 11](https://avaloniaui.net/) — cross-platform desktop framework (Fluent dark theme)
- [SkiaSharp](https://github.com/mono/SkiaSharp) — 2D rendering for ground and radar views
- [CommunityToolkit.Mvvm](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/) — MVVM source generators
- [ASP.NET SignalR Client](https://learn.microsoft.com/en-us/aspnet/core/signalr/) — real-time server communication
- [Google.Protobuf](https://protobuf.dev/) — VNAS nav data deserialization

## Documentation

See [USER_GUIDE.md](USER_GUIDE.md) for detailed usage instructions, command reference, and feature documentation.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for development setup, code style, and formatting instructions.

## License

[MIT](LICENSE)
