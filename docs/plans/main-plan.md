# YAAT Project Plan - Yet Another ATC Trainer

## Context

You have two existing projects as references:
- **vatsim-server-rs** (`X:\dev\vatsim-server-rs`) - A Rust server that ingests real-world FAA SWIM data and serves it to CRC via SignalR+MessagePack. We need to replicate its CRC-facing protocol in C#, but replace SWIM ingestion with simulated aircraft.
- **lc-trainer** (`X:\dev\lc-trainer`) - Your WPF ATC training app that got too convoluted trying to replicate CRC's displays. We study its architecture (especially PilotBehaviorLib and PhysicsService) but don't copy code directly.

The goal: create a training server + instructor/RPO app that works **alongside CRC** rather than replacing it, letting you focus on simulation and pilot control rather than radar display UI.

---

## Architecture Overview

### Two Repositories

```
X:\dev\yaat-server\     (C# ASP.NET Core 8, new repo)
  Server that CRC and YAAT clients connect to

X:\dev\yaat\            (C# Avalonia 11 + .NET 8, this repo)
  Instructor/RPO desktop app
```

### Communication Architecture

```
 CRC (radar display)              YAAT Client (instructor/RPO)
       |                                    |
       | SignalR+MessagePack                | SignalR (JSON)
       | /hubs/client                       | /hubs/training
       |                                    |
       +------------- yaat-server ----------+
                   (ASP.NET Core)
                        |
                  SimulationEngine
              (aircraft state, physics)
```

- **CRC hub** (`/hubs/client`): Read-only. Serves StarsTrackDto, EramTargetDto, FlightPlanDto, etc. using the exact same SignalR+MessagePack binary protocol that vatsim-server-rs implements.
- **Training hub** (`/hubs/training`): Read-write. YAAT clients use this for scenario management, RPO commands, aircraft spawning, simulation control. Uses standard SignalR with JSON (no custom binary framing needed).

### CRC Discovery

Users place/edit a `DevEnvironments.json` in their CRC installation:
```json
[
  {
    "name": "YAAT Local",
    "clientHubUrl": "http://localhost:5000/hubs/client",
    "apiBaseUrl": "http://localhost:5000/api",
    "isDisabled": false,
    "isSweatbox": true
  }
]
```

CRC fetches static data (nav data, video maps, ARTCC configs) from the real vNAS data API (`https://data-api.vnas.vatsim.net`). yaat-server only handles the real-time SignalR hub.

### Data API

yaat-server needs a minimal `/api` endpoint that CRC expects for session management. We'll stub the `/api` routes CRC calls during session setup and return compatible responses. Static data (nav data, video maps, ARTCC boundaries) continues to come from the real vNAS data API.

---

## Project Structure

### yaat-server (`X:\dev\yaat-server`)

```
yaat-server/
  src/
    YaatServer/                      # Main ASP.NET Core host
      Program.cs                     # Entry point, DI, hub registration
      Hubs/
        CrcHub.cs                    # /hubs/client - CRC-compatible hub
        TrainingHub.cs               # /hubs/training - YAAT client hub
      Protocol/
        MessagePackFraming.cs        # Varint + MessagePack binary framing
        SignalRProtocol.cs           # Handshake, negotiate, message dispatch
        CrcClientState.cs            # Per-CRC-client subscription/state
      Simulation/
        SimulationEngine.cs          # Main tick loop, aircraft lifecycle
        AircraftState.cs             # Aircraft position, heading, alt, speed, etc.
        FlightPhysics.cs             # Heading/altitude/speed interpolation
        GroundPhysics.cs             # Taxi, pushback, runway operations (M3+)
      Scenarios/
        ScenarioLoader.cs            # Load ATCTrainer v1 JSON scenarios
        ScenarioModels.cs            # Deserialization models for scenario JSON
      Commands/
        CommandParser.cs             # Parse ATCTrainer command syntax
        CommandDispatcher.cs         # Route commands to aircraft
        CommandHandlers/             # Per-category command handlers (M1+)
      Dtos/
        CrcDtos.cs                   # StarsTrackDto, EramTargetDto, etc.
        TrainingDtos.cs              # YAAT client DTOs
      Data/
        ArtccConfigs/                # ARTCC position configs (copied from vatsim-server-rs)
      appsettings.json
      Dockerfile
    YaatServer.Tests/                # xUnit tests
  yaat-server.sln
```

### yaat (`X:\dev\yaat`)

```
yaat/
  src/
    Yaat/                            # Main Avalonia application
      App.axaml / App.axaml.cs
      Program.cs
      ViewModels/
        MainViewModel.cs             # Primary view model
        ScenarioViewModel.cs         # Scenario loading/management
        AircraftListViewModel.cs     # Aircraft list and selection
        CommandInputViewModel.cs     # Command line input
      Views/
        MainView.axaml               # Primary layout
        ScenarioView.axaml           # Scenario browser/loader
        AircraftListView.axaml       # Aircraft status list
        CommandInputView.axaml       # Command input bar
      Services/
        ServerConnection.cs          # SignalR client to training hub
        CommandService.cs            # Parse and send commands
      Models/
        AircraftModel.cs             # Client-side aircraft representation
        ScenarioModel.cs             # Client-side scenario representation
    Yaat.Tests/                      # xUnit tests
  yaat.sln
```

### Key NuGet Packages

**yaat-server:**
- `Microsoft.AspNetCore.SignalR` (built-in)
- `MessagePack` (neuecc/MessagePack-CSharp) - for CRC binary protocol
- `System.Text.Json` - for scenario loading and training hub

**yaat:**
- `Avalonia` + `Avalonia.Desktop` + `Avalonia.Themes.Fluent`
- `CommunityToolkit.Mvvm` - MVVM source generators
- `Microsoft.AspNetCore.SignalR.Client` - SignalR client

---

## Milestones

### Milestone 0: Proof of Concept

**Goal:** CRC connects to yaat-server and sees aircraft moving on radar.

#### yaat-server

1. **ASP.NET Core project setup** with WebSocket support
2. **CRC-compatible SignalR endpoint** (`/hubs/client`):
   - `POST /hubs/client/negotiate` - Return connectionId, token, available transports
   - `GET /hubs/client` - WebSocket upgrade
   - Handshake: receive `{"protocol":"messagepack","version":1}\x1e`, respond `{}\x1e`
   - Varint+MessagePack framing (port from `vatsim-server-rs/crates/messaging/src/lib.rs`)
   - Handle message types: Invocation (1), Ping (6)
3. **StartSession handling**: Accept session with any ARTCC/facility/position, return SessionInfoDto
4. **Subscribe handling**: Accept topic subscriptions (StarsTracks, FlightPlans, EramTargets, etc.)
5. **SimulationEngine**: 1Hz tick loop that:
   - Maintains a list of hardcoded aircraft (3-5 aircraft at various positions around an airport)
   - Applies basic flight physics: heading-based position update, constant speed/altitude
   - Converts aircraft state to CRC DTOs (StarsTrackDto, FlightPlanDto, EramTargetDto, EramDataBlockDto)
   - Broadcasts to subscribed CRC clients
6. **Minimal /api stub**: Return empty/default responses for any API calls CRC makes during startup
7. **ARTCC config**: Load at least ZOA config from vatsim-server-rs data files
8. **Docker**: Dockerfile for containerized deployment

#### yaat client

1. **Avalonia project setup** with basic MVVM
2. **Server connection**: Connect to `ws://localhost:5000/hubs/training` via SignalR
3. **Minimal UI**: Server URL input, connect button, aircraft list showing current state
4. **Spawn button**: Trigger spawning of a test aircraft on the server

#### Definition of Done
- CRC connects to yaat-server via DevEnvironments.json
- CRC shows 3-5 aircraft moving on STARS display
- YAAT client connects and shows aircraft list
- Server runs in Docker

---

### Milestone 1: Scenario Loading & Basic RPO Commands

**Goal:** Load ATCTrainer scenarios, spawn aircraft from them, issue heading/altitude/speed commands.

#### yaat-server

1. **ScenarioLoader**: Parse ATCTrainer JSON scenarios
   - Deserialize all `startingConditions` types: `Parking`, `OnRunway`, `OnFinal`, `FixOrFrd`, `Coordinates`
   - Resolve FRD positions in `FixOrFrd` type (e.g., "OAK270010" = 10nm on 270 radial from OAK)
   - Handle `spawnDelay` (staggered aircraft spawning)
   - Execute `presetCommands` (e.g., "PUSH S", "WAIT 42 TAXI ...", "SQALL")
   - Execute `initializationTriggers` at their `timeOffset`
   - Populate `autoTrackConditions` (pre-tracked aircraft with position/altitude)
   - Handle `aircraftGenerators` for dynamic traffic spawning
   - For M0, only `Coordinates` and `FixOrFrd` types need to work (airborne aircraft). `Parking`/`OnRunway`/`OnFinal` require tower/ground physics (M2/M3).
2. **Flight physics enhancement**:
   - Heading interpolation (standard rate turn ~3 deg/sec)
   - Altitude interpolation (climb/descent rates by aircraft category)
   - Speed interpolation (acceleration/deceleration)
   - Wind drift (configurable wind speed/direction)
3. **CommandParser**: Parse ATCTrainer command syntax for core commands:
   - Heading: `FH`, `TL`, `TR`, `LT`, `RT`, `FPH`
   - Altitude: `CM`/`DM`
   - Speed: `SPD`, `MACH`
   - General: `DEL`, `PAUSE`, `UNPAUSE`, `SIMRATE`
   - Transponder: `SQ`, `SQI`, `SQV`, `SN`, `SS`, `ID`
4. **Training hub methods**:
   - `LoadScenario(scenarioJson)` - Load and start scenario
   - `SendCommand(callsign, command)` - Issue RPO command to aircraft
   - `GetAircraftList()` - Get all active aircraft
   - `SpawnAircraft(params)` - ADD command equivalent
   - `DeleteAircraft(callsign)` - DEL command
   - `SetSimRate(rate)` - SIMRATE command
   - `PauseSimulation()` / `ResumeSimulation()`
5. **Flight plan DTO population**: Proper route, departure, destination, aircraft type from scenario data

#### yaat client

1. **Scenario browser**: List available scenario files, preview, load
2. **Aircraft list**: Show all aircraft with callsign, type, position, altitude, speed, heading, assigned values
3. **Command input**: Text input bar with ATCTrainer command syntax
   - Select aircraft (click or type callsign prefix)
   - Type command, press Enter
   - Show command history
4. **Aircraft selection**: Click aircraft in list to select, commands apply to selected aircraft
5. **Simulation controls**: Pause/Resume/SimRate buttons

#### Definition of Done
- Load an existing ATCTrainer scenario file without modification
- Aircraft spawn at correct positions
- Issue FH/CM/SPD commands and see aircraft respond in CRC
- Pause/resume/simrate work
- Transponder codes display correctly in CRC

---

### Milestone 2: Local Control (Tower)

**Goal:** Takeoff, landing, traffic pattern, touch-and-go.

#### yaat-server

1. **Takeoff physics**:
   - Acceleration on runway to rotation speed
   - Rotation and liftoff
   - Initial climb rate based on aircraft type
2. **Landing physics**:
   - Final approach descent path (3 deg glideslope)
   - Touchdown and rollout deceleration
   - Runway exit
3. **Traffic pattern**:
   - Pattern legs: upwind, crosswind, downwind, base, final
   - Standard pattern altitude (typically 1000ft AGL)
   - Left/right traffic
   - Pattern entry points
4. **Tower commands**:
   - `CTO [hdg]` - Cleared for takeoff
   - `LUAW` - Line up and wait
   - `CTOC` - Cancel takeoff clearance
   - `CTOMLT`/`CTOMRT` - Takeoff with left/right traffic
   - `GA` - Go around
   - `TG`/`SG`/`LA`/`FS` - Touch-and-go, stop-and-go, low approach, full stop
   - `LAHSO {rwy}` - Land and hold short
   - `EXIT {twy}` - Exit at taxiway
   - `EL`/`ER` - Exit left/right
5. **Pattern commands**:
   - `ELD`/`ERD` - Enter left/right downwind
   - `ELB`/`ERB` - Enter left/right base
   - `EF` - Enter final
   - `MLT`/`MRT` - Make left/right traffic
   - `TC`/`TD`/`TB` - Turn crosswind/downwind/base
   - `EXT` - Extend leg
   - `MSA`/`MNA` - Short/normal approach
   - `PS {nm}` - Pattern size

#### yaat client

1. **Tower state display**: Show takeoff/landing/pattern state
2. **Runway status**: Active runways, who's on them
3. **Pattern visualization**: Show which leg each aircraft is on

#### Definition of Done
- Takeoff roll and liftoff visible in CRC
- Aircraft fly traffic pattern
- Touch-and-go, go-around work
- Landing and runway exit
- Pattern entry from outside

---

### Milestone 3: Ground Operations

**Goal:** Spawn aircraft at parking, pushback, taxi, hold short, collision avoidance.

#### yaat-server

1. **Taxiway graph data**: Load airport taxiway graph (nodes + edges with names)
   - Source: airport GeoJSON files from lc-trainer/vzoa or generate from vNAS data
   - Node types: parking, taxiway intersection, hold short line, runway threshold
2. **Ground physics**:
   - Taxi speed management (10-20 kts typical)
   - Turn physics on ground (slow speed turns at intersections)
   - Pushback physics (straight back or to heading, ~5 kts)
   - Stop/start acceleration/deceleration
3. **Ground commands**:
   - `PUSH [twy/spot]` - Pushback
   - `TAXI {path} [HS ...]` - Taxi with hold shorts
   - `RWY {rwy} TAXI {path} [HS ...]` - Taxi to runway
   - `HS {twy/rwy}` - Hold short
   - `CROSS {rwy/twy}` - Cross
   - `HOLD` - Hold position
   - `RES` - Resume
   - `BREAK` - Break from conflicts
   - `GIVEWAY {cs}` - Give way
4. **Pathfinding**: A* or Dijkstra on taxiway graph
5. **ASDEX DTOs**: Populate AsdexTargetDto and AsdexTrackDto for ground aircraft
6. **Collision avoidance**: Basic detection (stop if another aircraft is ahead on same taxiway segment)

#### yaat client

1. **Ground state display**: Show ground status (at parking, taxiing, hold short, etc.)
2. **Taxi route display**: Show assigned taxi route for selected aircraft
3. **Ground commands in command bar**: Support all ground commands

#### Definition of Done
- Spawn aircraft at parking positions
- Pushback and taxi to runway
- Hold short of runway
- Cross runway on command
- CRC ASDEX display shows ground targets
- Basic collision avoidance prevents taxi-through

---

### Milestone 4: Approach Control

**Goal:** Vectors, approach clearances, altitude/speed management.

#### yaat-server

1. **Approach commands**:
   - `CAPP [app] [apt]` - Cleared for approach
   - `JFAC [app]` - Join final approach course
   - `JARR {star} [trans]` - Join STAR
   - `DCT {wpt}` - Direct to waypoint
   - `HOLD {wpt} {crs} {dist} {dir}` - Holding pattern
   - `DVIA [alt]` - Descend via path
   - `CFIX {wpt} {alt} [spd]` - Cross fix at altitude
   - `DEPART {wpt} {hdg}` - Depart fix on heading
2. **Navigation**: Waypoint database from vNAS NavData.dat for DCT/procedure resolution
3. **Approach path following**: Aircraft track ILS/RNAV approach paths
4. **Altitude/speed management**: `CM`, `EXP`, `NORM`, `SPD`, `RFAS`, `RNS`

#### yaat client

1. **Route display**: Show assigned route/approach for selected aircraft
2. **Approach state**: Show approach clearance status

#### Definition of Done
- Vector aircraft to final approach course
- Clear for ILS/Visual approach
- Aircraft follows approach path to landing
- STAR and waypoint navigation works

---

### Milestone 5: Center (Enroute) Control

**Goal:** Enroute navigation, airways, holds, Mach, sector handoffs.

#### yaat-server

1. **Enroute navigation**: Airway following, high-altitude operations
2. **Mach speed**: `MACH {mach}` command
3. **ATC commands**: `HO`, `ACCEPT`, `TRACK`, `PO`, `CANCEL`, `DROP`
4. **Handoff simulation**: Simulate sector boundaries and handoffs
5. **ERAM DTO refinement**: Full ERAM display support

#### Definition of Done
- Aircraft navigate airways
- Handoff between sectors
- High-altitude Mach operations

---

### Milestone 6: Multi-User & Deployment

**Goal:** Multiple YAAT clients, role management, public deployment.

#### yaat-server

1. **Session management**: Multiple YAAT clients connect to same scenario
2. **Roles**: Mentor (full control), RPO (pilot control only), Observer (read-only)
3. **Aircraft assignment**: RPOs claim specific aircraft to control
4. **DigitalOcean deployment**: Docker Compose + HTTPS

#### yaat client

1. **Session browser**: See active sessions, join existing scenarios
2. **Role selection**: Choose role on connect
3. **Multi-user awareness**: See who else is connected, who controls which aircraft

#### Definition of Done
- 2+ YAAT clients controlling different aircraft in same scenario
- Mentor can manage scenario while RPOs control pilots
- Deployed on DigitalOcean, accessible from CRC

---

### Milestone 7: v2 Scenario Format

**Goal:** Enhanced scenario format with features ATCTrainer doesn't support.

1. **v2 schema**: Wind/weather, timed events, scoring criteria, custom waypoints, multi-airport
2. **v1 import with upgrade path**: Auto-convert v1 scenarios to v2
3. **Scenario editor**: Create/edit scenarios in YAAT client

---

### Milestone 8: Automated Pilot Logic (Future)

**Goal:** AI pilots for self-study (no mentor/RPO needed).

1. **ATC instruction interpretation**: Parse ATC instructions (vs RPO commands)
2. **Intent-based planning**: Study PilotBehaviorLib from lc-trainer as reference
3. **Readback simulation**: Generate readback text
4. **Lost comms behavior**: Pilot acts autonomously when no instructions

---

## Key Technical Decisions

### CRC Protocol Compatibility
The CRC-compatible hub must produce **byte-identical** MessagePack frames to what vatsim-server-rs produces. This means:
- Same varint length encoding (LEB128)
- Same MessagePack field ordering in DTOs
- Same SignalR invocation structure: `[1, {headers}, invocation_id, "MethodName", [args]]`
- Same response structure: `[3, {}, invocation_id, 3, result]`
- Reference: `vatsim-server-rs/crates/messaging/src/dtos.rs` for exact field order

### Flight Physics
Start minimal (constant speed straight flight) and build up. Reference LCTrainer's PhysicsService.cs (`X:\dev\lc-trainer\src\LCTrainer\Services\Simulation\PhysicsService.cs`) for proven C# flight physics, but rewrite fresh - don't copy the conditionals that made it hard to extend.

### Scenario Format
ATCTrainer scenarios must load **without modification**. Only the ATCTrainer format matters (not LCTrainer's internal format). The schema is:

**Top-level fields:** `id`, `name`, `artccId`, `primaryAirportId`, `studentPositionId`, `autoDeleteMode` ("None" | "Parked" | "OnLanding"), `minimumRating`, `primaryApproach`

**`aircraft[]`** - each aircraft has:
- `id`, `aircraftId` (callsign), `aircraftType` (e.g., "B738/L"), `transponderMode` ("C" | "Standby")
- `difficulty` ("Easy" | "Medium" | "Hard")
- `spawnDelay` (seconds after scenario start)
- `airportId` (3-letter airport code)
- `onAltitudeProfile` (bool)
- `expectedApproach` (e.g., "I28R", "R28L")
- `startingConditions` - one of:
  - `{"type": "Parking", "parking": "29"}`
  - `{"type": "OnRunway", "runway": "29R"}`
  - `{"type": "OnFinal", "runway": "28R", "distanceFromRunway": 10}`
  - `{"type": "FixOrFrd", "fix": "OAK270010", "altitude": 2500, "speed": 280, "navigationPath": "OAK"}`
  - `{"type": "Coordinates", "coordinates": {"lat": 37.71, "lon": -121.93}, "altitude": 2500, "speed": 90, "heading": 264, "navigationPath": "OAK"}`
- `autoTrackConditions` - `{"positionId": "...", "handoffDelay": 0, "clearedAltitude": "110"}`
- `flightplan` (optional) - `{rules, departure, destination, cruiseAltitude, cruiseSpeed, route, remarks, aircraftType}`
- `presetCommands[]` - `{id, command, timeOffset}` (e.g., `"PUSH S"`, `"WAIT 42 TAXI S T U W W1 30"`, `"SQALL"`, `"SAY ..."`, `"SQV"`)

**`atc[]`** - controller positions: `{id, artccId, facilityId, positionId, autoConnect, autoTrackAirportIds[]}`

**`aircraftGenerators[]`** - dynamic traffic: `{runway, engineType, weightCategory, initialDistance, maxDistance, intervalDistance, startTimeOffset, maxTime, intervalTime, randomizeInterval, randomizeWeightCategory, autoTrackConfiguration}`

**`initializationTriggers[]`** - timed global commands: `{id, command, timeOffset}` (e.g., `"SQALL"` every 3 minutes)

**`flightStripConfigurations[]`** - pre-populated strip bays: `{facilityId, bayId, rack, aircraftIds[]}`

### Training Hub Protocol
Use standard ASP.NET Core SignalR with JSON serialization (not custom MessagePack). This is a separate hub from the CRC-compatible one, so we don't need binary compatibility. Simpler to develop and debug.

---

## Detailed Milestone Plans

- [Milestone 0: Proof of Concept](milestone-0.md)

---

## What to Build First (Milestone 0 Implementation Order)

1. Create `yaat-server` solution with ASP.NET Core project
2. Implement MessagePack framing (varint encode/decode + MessagePack serialization)
3. Implement `/hubs/client/negotiate` endpoint
4. Implement WebSocket handler at `/hubs/client` with handshake
5. Implement StartSession and Subscribe message handling
6. Create SimulationEngine with hardcoded aircraft and 1Hz tick
7. Create CRC DTOs (StarsTrackDto, FlightPlanDto, EramTargetDto, EramDataBlockDto)
8. Broadcast aircraft updates to subscribed CRC clients
9. Add Dockerfile
10. Create `yaat` Avalonia project with basic connection UI
11. Test with CRC

---

## Reference Files

| Purpose | File |
|---------|------|
| CRC DTO definitions (field order) | `X:\dev\vatsim-server-rs\crates\messaging\src\dtos.rs` |
| SignalR binary framing | `X:\dev\vatsim-server-rs\crates\messaging\src\lib.rs` |
| Client lifecycle & message dispatch | `X:\dev\vatsim-server-rs\crates\server\src\clientstate\mod.rs` |
| Protocol (handshake, encode, subscribe) | `X:\dev\vatsim-server-rs\crates\server\src\clientstate\protocol.rs` |
| DTO update generation | `X:\dev\vatsim-server-rs\crates\server\src\clientstate\updates.rs` |
| WebSocket server setup | `X:\dev\vatsim-server-rs\crates\server\src\websocket.rs` |
| Flight physics (C# reference) | `X:\dev\lc-trainer\src\LCTrainer\Services\Simulation\PhysicsService.cs` |
| Scenario format examples | `X:\dev\lc-trainer\docs\atctrainer-scenario-examples\` |
| ATCTrainer command reference | `https://atctrainer.collinkoldoff.dev/docs/commands` |
| vNAS configuration (environments) | `https://configuration.vnas.vatsim.net/` |
| DevEnvironments.json example | `X:\dev\vatsim-server-rs\DevEnvironments.json` |
| ARTCC configs | `X:\dev\vatsim-server-rs\data\*.json` |

---

## Verification

### Milestone 0 End-to-End Test
1. Start yaat-server (`dotnet run` or Docker)
2. Place DevEnvironments.json in CRC installation pointing to `http://localhost:5000`
3. Open CRC, select "YAAT Local" environment
4. Connect to any ZOA position
5. Verify aircraft appear on STARS display and move
6. Open YAAT client, connect to same server
7. Verify aircraft list shows same aircraft
8. Trigger spawn from YAAT, verify new aircraft appears in CRC
