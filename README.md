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

## Download

Pre-built installers and portable binaries are published on the [Releases page](https://github.com/leftos/yaat/releases/latest). No terminal, Git, or .NET SDK required.

| Platform | Installer (recommended) | Portable (single file) |
|----------|-------------------------|------------------------|
| Windows  | `YaatClient-Setup.exe` — auto-updates in the background | `Yaat.Client-win-x64.exe` |
| Linux    | `YaatClient-linux-x64.AppImage` | `Yaat.Client-linux-x64` |
| macOS    | `YaatClient-osx-arm64.pkg` | `Yaat.Client-osx-arm64` |

A second installer, **YAAT Flight Strips** (`YaatVStrips-*`), ships the flight-strips UI on its own for students who want to replace vStrips but don't need the full trainer.

Installers keep themselves up to date automatically. Portable binaries don't auto-update — download the next release when you want it. Either launcher can connect to a hosted YAAT server (ask your instructor for the URL) or a local server you run yourself.

**NVIDIA GPU acceleration** (Windows): the installer ships with Vulkan/CPU support out of the box. Users with an NVIDIA card can opt in to CUDA 13 from Settings → Speech → Acceleration — YAAT downloads the runtime on demand (~534 MB) so the base installer stays small.

See the [Installation Guide](INSTALL.md) for step-by-step instructions. If you want to run a server yourself, build from source, or contribute changes, the same guide covers [Building from source](INSTALL.md#building-from-source).

## Documentation

| Document | Audience | Content |
|----------|----------|---------|
| **[Installation Guide](INSTALL.md)** | New users | Download the installer, or build from source if you want to host a server or develop locally |
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
- [LM-Kit.NET](https://lm-kit.com/products/lm-kit-net/) — on-device LLM inference and Whisper speech recognition (single engine for both)

## Acknowledgements

YAAT's speech recognition and command interpretation pipelines are powered by [LM-Kit.NET](https://lm-kit.com/products/lm-kit-net/) (Community Edition). LM-Kit handles GGUF model loading, GPU backend selection (CUDA / Vulkan / Metal), and grammar-constrained text generation — letting YAAT use a single inference engine for both Whisper STT and the canonical-command LLM mapper.

Airline telephony data is derived from [OpenFlights](https://openflights.org/data.html), licensed under ODbL 1.0. See `NOTICE` and `src/Yaat.Sim/Speech/Data/LICENSE-OPENFLIGHTS.txt`.

## License

[MIT](LICENSE)
