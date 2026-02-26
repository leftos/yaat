# YAAT

Yet Another ATC Trainer — an instructor/RPO desktop client for air traffic control training scenarios.

## Requirements

- .NET 8 SDK
- A running [yaat-server](https://github.com/leftos/yaat-server) instance

## Running

```bash
cd src/Yaat
dotnet run
```

Enter the server URL (default `http://localhost:5000`) and click **Connect** to see the aircraft list. Use **Spawn Aircraft** to add new aircraft to the simulation.

## Project Structure

```
src/Yaat/
  Models/          # Observable data models
  Services/        # SignalR client connection
  ViewModels/      # MVVM view models
  Views/           # Avalonia AXAML views
```

## Tech Stack

- [Avalonia UI 11](https://avaloniaui.net/) — cross-platform desktop framework
- [CommunityToolkit.Mvvm](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/) — MVVM source generators
- [ASP.NET SignalR Client](https://learn.microsoft.com/en-us/aspnet/core/signalr/) — real-time server communication
