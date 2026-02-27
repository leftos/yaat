# YAAT

Yet Another ATC Trainer — an instructor/RPO desktop client for air traffic control training scenarios. Works alongside [CRC](https://crc.virtualnas.net) (the VATSIM radar client) by connecting to a [yaat-server](https://github.com/leftos/yaat-server) instance that simulates aircraft and feeds them to CRC via its native protocol.

## Requirements

- .NET 8 SDK
- A running [yaat-server](https://github.com/leftos/yaat-server) instance

## Building & Running

```bash
# Build the entire solution
dotnet build

# Run the client app
dotnet run --project src/Yaat.Client
```

Enter the server URL (default `http://localhost:5000`) and click **Connect** to see the aircraft list. Use **Spawn Aircraft** to add new aircraft to the simulation.

## Project Structure

```
src/Yaat.Client/   # Avalonia desktop app (instructor/RPO UI)
  Models/          # Observable data models
  Services/        # SignalR client connection + DTOs
  ViewModels/      # MVVM view models
  Views/           # Avalonia AXAML views

src/Yaat.Sim/      # Shared simulation library (aircraft state, physics)
```

## Tech Stack

- [Avalonia UI 11](https://avaloniaui.net/) — cross-platform desktop framework (Fluent dark theme)
- [CommunityToolkit.Mvvm](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/) — MVVM source generators
- [ASP.NET SignalR Client](https://learn.microsoft.com/en-us/aspnet/core/signalr/) — real-time server communication
- .NET 8, C# with nullable reference types
