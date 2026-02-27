# Milestone 0: YAAT Proof of Concept

## Context

YAAT (Yet Another ATC Trainer) needs two repos built from scratch:
- **yaat-server** (ASP.NET Core 8) — serves CRC via SignalR+MessagePack, simulates aircraft
- **yaat** (Avalonia 11) — instructor/RPO desktop client

The goal: CRC connects to yaat-server via `DevEnvironments.json` and sees aircraft moving on STARS display. The YAAT client connects to a separate training hub and shows an aircraft list.

Both repos will be published to GitHub under `leftos` (yaat-server private, yaat public).

## Architecture Decision

**Raw WebSocket for CRC hub** — ASP.NET's built-in SignalR+MessagePack protocol generates its own framing. CRC expects exact LEB128 varint + MessagePack binary frames matching vatsim-server-rs. Raw WebSocket handling gives full control over every byte.

**Standard ASP.NET SignalR for training hub** — our own protocol, JSON is fine.

**Two projects for yaat repo** — `Yaat.Client` (Avalonia app) and `Yaat.Sim` (shared simulation library, intended for use by yaat-server too). Uses `.slnx` solution format.

---

## Implementation Steps

### Phase 1: yaat-server project scaffolding

1. Create `X:\dev\yaat-server\` with `dotnet new sln` + `dotnet new web` under `src/YaatServer/`
2. Add NuGet: `MessagePack` (neuecc v3.x)
3. Set up `Program.cs` with WebSocket middleware, SignalR for training hub, route stubs
4. Copy `ZOA.json` from `X:\dev\vatsim-server-rs\data\ZOA.json` into `Data/ArtccConfigs/`
5. Add `.gitignore`, init git repo, create private GitHub repo `leftos/yaat-server`

### Phase 2: Protocol layer (`Protocol/`)

**Files:**
- `VarintCodec.cs` — LEB128 encode/decode (port from `vatsim-server-rs/crates/messaging/src/lib.rs`)
- `MessageFraming.cs` — frame (varint + payload) and parse framed messages
- `SignalRMessageBuilder.cs` — build Invocation `[1,{},null,target,args,[]]`, InvocationResponse `[3,{},id,3,result]`, Ping `[6]` using `MessagePackWriter`
- `SignalRMessageParser.cs` — parse incoming binary messages into `AppMessage` records

### Phase 3: CRC DTOs (`Dtos/`)

Port from `vatsim-server-rs/crates/messaging/src/dtos.rs` using `[MessagePackObject]` + `[Key(n)]` attributes:

- `CrcEnums.cs` — all enums (VoiceType, StarsCoastPhase, FlightPlanStatus, TrackOwnerType, LeaderDirection, StarsTpaType, EramTargetSymbolType, HaloType, EramConflictStatus, EramDataBlockFormat, PositionRole, NetworkRating, PositionType, StarsColorSet, StarsPointoutStatus)
- `CrcDtos.cs` — Point, Tcp, TrackOwner, StarsPointout, StarsTrackHistoryEntryDto, StarsTrackDto (37 fields), FlightPlanDto (32 fields), ParsedAltitude, ClearanceDto, HoldAnnotationsDto, EramTargetDto (11 fields), EramDataBlockDto (10 fields), SessionInfoDto, PositionSpecDto, OpenPositionDto, StarsConsolidationItemDto, StarsConfigurationItemDto
- `ArtccPositionDto.cs` — ArtccPosition, SectorConfiguration, StarsConfiguration (for session info)
- `TopicFormatter.cs` — custom `IMessagePackFormatter<Topic>` that serializes as 4-element tuple
- `TrainingDtos.cs` — AircraftStateDto, SpawnAircraftDto (JSON, no MessagePack attributes)

### Phase 4: ARTCC config loading (`Data/`)

- `ArtccConfig.cs` — JSON deserialization models for the facility tree (System.Text.Json with camelCase)
- `ArtccConfigService.cs` — singleton that loads ZOA.json, provides `GetPosition(id)` recursive lookup

### Phase 5: CRC WebSocket handler (`Hubs/`)

- `NegotiateHandler.cs` — `POST /hubs/client/negotiate` returns `{connectionId, connectionToken, negotiateVersion:1, availableTransports:[{transport:"WebSockets",transferFormats:["Text","Binary"]}]}`
- `CrcWebSocketHandler.cs` — accepts WebSocket at `/hubs/client?id={token}`, creates `CrcClientState`, runs message loop
- `CrcClientState.cs` — per-connection state machine:
  1. Handshake: first message starts with `{` → respond `{}\x1e`
  2. Message loop: parse varint-framed MessagePack → dispatch by target
  3. Handle: StartSession (return SessionInfoDto), GetServerConfiguration (return `[6809]`), Subscribe (add topic, ack), ActivateSession (SetSessionActive + ReceiveOpenPositions + ack), all others (ack with nil)
  4. Ping → respond with ping
- `CrcClientManager.cs` — singleton tracking all connected CRC clients, provides `BroadcastAsync(byte[])` + per-client subscription filtering

### Phase 6: Simulation engine (`Simulation/`)

- `AircraftState.cs` — mutable aircraft record: callsign, type, lat, lon, heading, altitude, groundSpeed, beaconCode, departure, destination, route
- `FlightPhysics.cs` — constant-speed straight-line position update per tick
- `DtoConverter.cs` — converts `AircraftState` to StarsTrackDto, FlightPlanDto, EramTargetDto, EramDataBlockDto
- `SimulationEngine.cs` — `IHostedService` with 1Hz `PeriodicTimer`:
  - Initializes 5 hardcoded aircraft around KOAK
  - Each tick: update positions via FlightPhysics, convert to DTOs, build broadcast payloads via `SignalRMessageBuilder`, send to subscribed clients via `CrcClientManager`

### Phase 7: API stubs + Training hub

- `ApiStubHandler.cs` — catch-all `GET /api/{**path}` returns `[]` (empty JSON array)
- `TrainingHub.cs` — standard SignalR hub at `/hubs/training` with methods: `GetAircraftList()`, `SpawnAircraft(dto)`. Engine broadcasts `AircraftUpdated` to connected training clients.

### Phase 8: Dockerfile

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/YaatServer/YaatServer.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 5000
ENTRYPOINT ["dotnet", "YaatServer.dll"]
```

### Phase 9: yaat client (Avalonia) — COMPLETED

1. Created `X:\dev\yaat\yaat.slnx` with two projects: `src/Yaat.Client/` (Avalonia app) and `src/Yaat.Sim/` (shared sim library)
2. NuGet (Yaat.Client): Avalonia 11.2.5, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Controls.DataGrid, Avalonia.Fonts.Inter, CommunityToolkit.Mvvm 8.4.0, Microsoft.AspNetCore.SignalR.Client 8.0.12
3. `Services/ServerConnection.cs` — SignalR JSON client to `/hubs/training` with DTOs (AircraftDto, SpawnAircraftDto) as records
4. `Models/AircraftModel.cs` — ObservableObject with callsign, type, lat, lon, heading, alt, speed, beaconCode
5. `ViewModels/MainViewModel.cs` — server URL, connect/disconnect toggle, aircraft ObservableCollection, spawn command (random B738 near KOAK)
6. `ViewModels/ConnectButtonConverter.cs` — IValueConverter for Connect/Disconnect button label
7. `Views/MainWindow.axaml` — DockPanel: top bar (URL + connect + status), center DataGrid (8 columns), bottom spawn button
8. `App.axaml` / `Program.cs` — standard Avalonia bootstrap, dark Fluent theme
9. `Yaat.Sim/` — AircraftState (mutable state), FlightPhysics (constant-speed position update), SimulationWorld (thread-safe collection with tick)

### Phase 10: GitHub repos

1. Init git in yaat-server, create private repo `leftos/yaat-server`, push
2. Init git in yaat (already has docs/), create public repo `leftos/yaat`, push

---

## Key Protocol Details (reference for implementation)

### Varint (LEB128)
Each byte: 7 data bits + MSB continuation flag. MSB=1 means more bytes follow.

### SignalR Message Types
- Invocation: `[1, {}, invocationId?, "Target", [args], []]`
- InvocationResponse: `[3, {}, invocationId, 3, result]`
- Ping: `[6]`

### Handshake
Client sends `{"protocol":"messagepack","version":1}\x1e` → server responds `{}\x1e` (both as binary WebSocket frames)

### Broadcast Format
`[1, {}, null, "ReceiveStarsTracks", [["StarsTracks","facilityId",null,null], [dto1,dto2,...]], []]`

The `SendDto` in Rust uses `#[serde(untagged)]` tuple variants, so `ReceiveStarsTracks(Topic, Vec<StarsTrackDto>)` serializes as `[topic, [dtos]]`. We replicate this by manually building the MessagePack array with `MessagePackWriter`.

### Topic Serialization
Custom: 4-element tuple `[name, facilityId, subset, sectorId]` (not a map)

---

## Critical Reference Files

| Purpose | Source |
|---------|--------|
| Varint encoding | `X:\dev\vatsim-server-rs\crates\messaging\src\lib.rs` |
| DTO field ordering | `X:\dev\vatsim-server-rs\crates\messaging\src\dtos.rs` |
| Message parsing | `X:\dev\vatsim-server-rs\crates\messaging\src\appmessage.rs` |
| Client lifecycle | `X:\dev\vatsim-server-rs\crates\server\src\clientstate\mod.rs` |
| Handshake + protocol | `X:\dev\vatsim-server-rs\crates\server\src\clientstate\protocol.rs` |
| Update generation | `X:\dev\vatsim-server-rs\crates\server\src\clientstate\updates.rs` |
| ARTCC config format | `X:\dev\vatsim-server-rs\data\ZOA.json` |

## Verification

1. `dotnet run` yaat-server on port 5000
2. Place `DevEnvironments.json` in CRC pointing to `http://localhost:5000`
3. Open CRC, select "YAAT Local", connect to any ZOA position
4. Verify: 5 aircraft appear on STARS display, moving at constant speed/heading
5. Open yaat client, connect to `http://localhost:5000`, verify aircraft list populates
6. Click spawn, verify new aircraft appears in both CRC and YAAT client
