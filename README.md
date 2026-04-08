# YAAT

Yet Another ATC Trainer — an instructor/RPO desktop client for air traffic control training. Connects to a [yaat-server](https://github.com/leftos/yaat-server) instance that simulates aircraft and feeds them to [CRC](https://vnas.vatsim.net/crc) (the VATSIM radar client) via its native SignalR+MessagePack protocol.

Instructors and RPOs use YAAT to create training rooms, load scenarios, issue ATC commands, control weather, and manage simulated traffic while students work the scopes in CRC.

## Features

- **Cross-platform** — runs natively on Windows, macOS, and Linux
- **Training rooms** — create or join isolated rooms; multiple concurrent sessions supported
- **Scenario management** — load predefined traffic scenarios with automatic aircraft spawning
- **Command input** — text-based ATC commands with autocomplete, macros, and compound syntax
- **Ground view** — SkiaSharp-rendered airport surface map with taxi routing and context menus
- **Radar view** — STARS-style radar display with video maps, range rings, and data blocks
- **Weather** — load ATCTrainer-compatible wind/weather profiles; wind affects aircraft physics
- **Full nav data** — VNAS protobuf nav data, FAA CIFP procedures (SIDs, STARs, approaches), and aircraft performance specs downloaded automatically
- **CRC integration** — CRC clients connect to the same server and see all simulated traffic
- **Rewind** — scrub back through a session and replay from any point

## Documentation

| Document | Audience | Content |
|----------|----------|---------|
| **[Installation Guide](INSTALL.md)** | New users | System prerequisites, downloading the code, building and running |
| **[Getting Started](GETTING_STARTED.md)** | First-time users | First connection, identity setup, loading your first scenario |
| **[User Guide](USER_GUIDE.md)** | Active users | Interface, views, scenarios, weather, settings, and workflows |
| **[Command Reference](COMMANDS.md)** | Active users | Complete command reference — every verb, alias, and example |
| **[Contributing](CONTRIBUTING.md)** | Developers | Development setup, code style, and formatting |

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

## License

[MIT](LICENSE)
