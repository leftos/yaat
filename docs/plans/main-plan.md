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
  yaat.slnx                           # Solution file (.slnx XML format)
  docs/plans/                          # Milestone plans and architecture docs
  src/
    Yaat.Client/                       # Avalonia 11 desktop app (instructor/RPO)
      App.axaml / App.axaml.cs
      Program.cs
      Models/
        AircraftModel.cs               # ObservableObject aircraft representation
      Services/
        ServerConnection.cs            # SignalR client + DTOs (AircraftDto, SpawnAircraftDto)
      ViewModels/
        MainViewModel.cs               # Primary view model (connect, spawn, aircraft list)
        ConnectButtonConverter.cs      # IValueConverter for Connect/Disconnect label
      Views/
        MainWindow.axaml               # Primary layout (DockPanel with DataGrid)
        MainWindow.axaml.cs
    Yaat.Sim/                          # Shared simulation library
      AircraftState.cs                 # Mutable aircraft state with flight plan fields
      FlightPhysics.cs                 # Position update from heading + groundspeed
      SimulationWorld.cs               # Thread-safe aircraft collection with tick loop
```

### Key NuGet Packages

**yaat-server:**
- `Microsoft.AspNetCore.SignalR` (built-in)
- `MessagePack` (neuecc/MessagePack-CSharp) - for CRC binary protocol
- `System.Text.Json` - for scenario loading and training hub

**yaat (Yaat.Client):**
- `Avalonia` 11.2.5 + `Avalonia.Desktop` + `Avalonia.Themes.Fluent` + `Avalonia.Controls.DataGrid` + `Avalonia.Fonts.Inter`
- `CommunityToolkit.Mvvm` 8.4.0 - MVVM source generators
- `Microsoft.AspNetCore.SignalR.Client` 8.0.12 - SignalR client

---

## Milestones

### ~~Milestone 0: Proof of Concept~~ COMPLETE

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

### ~~Milestone 1: Scenario Loading & Basic RPO Commands~~ COMPLETE

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
2. **`ControlTargets` data model** (see [pilot-ai-architecture.md](pilot-ai-architecture.md) §8.1.7):
   - Each aircraft gets a `ControlTargets` instance with target heading, altitude, and speed
   - RPO commands set targets directly (no Phase structure yet)
   - Physics engine interpolates current values toward targets each tick
   - `NavigationTarget` on heading axis for waypoint-based navigation (`DCT` command)
   - `TurnDirection` preference for `TL`/`TR` commands
3. **Flight physics enhancement**:
   - Heading interpolation (standard rate turn ~3 deg/sec, 25° bank limit for transport category)
   - Altitude interpolation (climb/descent rates by aircraft category)
   - Speed interpolation (acceleration/deceleration)
   - Wind drift (configurable wind speed/direction)
   - Position update from heading + groundspeed (TAS adjusted for wind)
4. **CommandParser**: Parse ATCTrainer command syntax for core commands:
   - Heading: `FH`, `TL`, `TR`, `LT`, `RT`, `FPH`
   - Altitude: `CM`/`DM`
   - Speed: `SPD`, `MACH`
   - General: `DEL`, `PAUSE`, `UNPAUSE`, `SIMRATE`
   - Transponder: `SQ`, `SQI`, `SQV`, `SN`, `SS`, `ID`
5. **Training hub methods**:
   - `LoadScenario(scenarioJson)` - Load and start scenario
   - `SendCommand(callsign, command)` - Issue RPO command to aircraft
   - `GetAircraftList()` - Get all active aircraft
   - `SpawnAircraft(params)` - ADD command equivalent
   - `DeleteAircraft(callsign)` - DEL command
   - `SetSimRate(rate)` - SIMRATE command
   - `PauseSimulation()` / `ResumeSimulation()`
6. **Flight plan DTO population**: Proper route, departure, destination, aircraft type from scenario data

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

### Milestone 2: Local Control (Tower) — IMPLEMENTATION COMPLETE, VERIFICATION PENDING

**Goal:** Takeoff, landing, traffic pattern, touch-and-go.

#### yaat-server / Yaat.Sim

1. **~~Introduce `Phase` base class~~** ✓ (see [pilot-ai-architecture.md](pilot-ai-architecture.md) §8.1.1):
   - Abstract `Phase` with `OnStart`, `OnTick`, `OnEnd` lifecycle
   - `PhaseContext` providing access to aircraft state, targets, and services
   - `PhaseStatus` enum (Pending, Active, Completed, Skipped)
   - Each aircraft gets a simple phase list — no full `Plan`/`Intent` yet, just a current phase driving `ControlTargets`
2. **~~`ClearanceRequirement` for tower operations~~** ✓ (see [pilot-ai-architecture.md](pilot-ai-architecture.md) §8.1.4):
   - `ClearanceType.ClearedForTakeoff`, `LineUpAndWait`, `ClearedToLand`, `ClearedForOption`, `ClearedTouchAndGo`
   - RPO tower commands (`CTO`, `LUAW`, etc.) satisfy the corresponding requirement and trigger phase transitions
   - Landing clearance tracked on `PhaseList.LandingClearance` (not per-phase)
3. **~~Tower phases~~** ✓:
   - `LinedUpAndWaitingPhase` — clearance-gated, transitions to `TakeoffPhase` on CTO
   - `TakeoffPhase` — acceleration to rotation speed, liftoff, initial climb
   - `FinalApproachPhase` — glideslope tracking, clearance-gated go-around
   - `LandingPhase` — flare, touchdown, rollout deceleration
   - `GoAroundPhase` — climb on runway heading (or assigned), return to pattern or as directed
   - Pattern phases: `UpwindPhase`, `CrosswindPhase`, `DownwindPhase`, `BasePhase`
4. **~~Takeoff physics~~** ✓:
   - Acceleration on runway to rotation speed (Vr by category)
   - Rotation and liftoff
   - Initial climb rate based on aircraft category
5. **~~Landing physics~~** ✓:
   - Final approach descent path (3° glideslope via `GlideSlopeGeometry`)
   - Touchdown and rollout deceleration
6. **~~Traffic pattern~~** ✓:
   - Pattern legs: upwind, crosswind, downwind, base, final
   - Standard pattern altitude (~1000ft AGL by category)
   - Left/right traffic with `PatternGeometry` waypoint computation
   - Pattern entry points (downwind, base, final)
   - Pattern cycling: `TrafficDirection` on `PhaseList` auto-appends next circuit
7. **~~Tower commands~~** ✓:
   - `CTO [hdg]` - Cleared for takeoff
   - `CTOR{deg}`/`CTOL{deg}` - Cleared for takeoff, relative right/left turn
   - `LUAW` - Line up and wait
   - `CTOC` - Cancel takeoff clearance (aborts during ground roll)
   - `CTOMLT`/`CTOMRT` - Takeoff with left/right traffic
   - `GA [hdg] [alt]` - Go around with optional heading/altitude
   - `CTL` - Cleared to land
   - `TG`/`SG`/`LA`/`COPT` - Touch-and-go, stop-and-go, low approach, cleared for option
   - `HPPL`/`HPPR` - Hold present position via 360° turns left/right
   - `HPP` - Hold present position (helicopter hover)
   - `HFIX {fix}` / `HFIXL`/`HFIXR` - Hold at fix
   - Deferred to M3: `FS`, `EXIT`, `EL`/`ER`, `LAHSO`
8. **~~Pattern commands~~** ✓:
   - `ELD`/`ERD` - Enter left/right downwind
   - `ELB`/`ERB` - Enter left/right base
   - `EF` - Enter final
   - `MLT`/`MRT` - Make left/right traffic
   - `TC`/`TD`/`TB` - Turn crosswind/downwind/base
   - `EXT` - Extend leg (works on any pattern leg)
   - Deferred: `MSA`/`MNA`, `PS {nm}`
9. **~~Runway data~~** ✓: `FixDatabase` extended to implement `IRunwayLookup` from VNAS NavData.dat
10. **~~Scenario support~~** ✓: `OnRunway` and `OnFinal` starting conditions via `AircraftInitializer`

#### CRC Visibility (TowerCab + ASDEX + STARS filtering)

STARS tracks only appear when aircraft altitude >= field elevation + 100ft AGL. Ground and low-altitude aircraft (lined up, takeoff roll, landing rollout) need TowerCab and ASDEX DTOs to be visible in CRC. Note: vatsim-server-rs has STARS/ERAM/ASDEX but does **not** implement TowerCab — our filtering rules are our own design.

11. **TowerCab DTOs**: `TowerCabAircraftDto` via `ReceiveTowerCabAircrafts` — aircraft within 20nm of airport and <= 4000ft AGL
12. **ASDEX DTOs**: `AsdexTargetDto` + `AsdexTrackDto` via `ReceiveAsdexTargets`/`ReceiveAsdexTracks` — per-airport, within `targetVisibilityRange` nm and <= `targetVisibilityCeiling` ft (from ARTCC config)
13. **STARS altitude filtering**: Only include in `ReceiveStarsTracks` when altitude >= field elevation + 100ft AGL; delete events on transitions

#### yaat client

1. **~~Tower state display~~** ✓: Phase and Rwy columns in DataGrid
2. **Runway status**: Active runways, who's on them — deferred
3. **Pattern visualization**: Show which leg each aircraft is on — deferred

#### Definition of Done
- Takeoff roll and liftoff visible in CRC (via TowerCab/ASDEX)
- Aircraft fly traffic pattern
- Touch-and-go, go-around work
- Landing and runway exit
- Pattern entry from outside
- Tower commands satisfy clearance requirements and trigger phase transitions

---

### Milestone 3: Ground Operations — IMPLEMENTATION COMPLETE, VERIFICATION PENDING

**Goal:** Spawn aircraft at parking, pushback, taxi, hold short, collision avoidance.

Detailed chunk plan: [M3 Ground Operations Plan](../plans/encapsulated-crunching-sutherland.md) (6 chunks, all complete)

#### yaat-server / Yaat.Sim

1. **~~Airport ground data layer~~** ✓: GeoJSON parser → `AirportGroundLayout` graph (nodes, edges, intersections). A* pathfinder for explicit and auto-route taxi paths. Runtime-computed from GeoJSON — no pre-built data.
2. **~~Ground phases~~** ✓:
   - `AtParkingPhase` — speed=0, accepts Pushback/Taxi/Delete
   - `PushbackPhase` — reverse at ~5 kts on parking heading
   - `TaxiingPhase` — follow `TaxiRoute` edge-by-edge, auto-stops at hold-short points
   - `HoldingShortPhase` — clearance-gated (`RunwayCrossing`), notifies RPOs on arrival
   - `CrossingRunwayPhase` — cross runway at ~10 kts along taxiway alignment
   - `RunwayExitPhase` — post-landing auto-exit to nearest taxiway, generates "clear of runway" notification
   - `HoldingAfterExitPhase` — awaits taxi instructions after exiting runway
   - `FollowingPhase` — trail target aircraft on ground with safe following distance
3. **~~Ground commands~~** ✓:
   - `PUSH [hdg]` - Pushback (optional heading)
   - `TAXI {path} [HS {rwy}...]` - Taxi with implicit + explicit hold shorts
   - `HOLD` / `HP` - Hold position
   - `RES` / `RESUME` - Resume taxi
   - `CROSS {rwy}` - Cross runway (satisfies RunwayCrossing clearance)
   - `FOLLOW {callsign}` - Trail target aircraft
   - `GIVEWAY {callsign}` / `BEHIND {callsign}` - Condition prefix (like AT/LV)
4. **~~Collision avoidance~~** ✓: `GroundConflictDetector` computes speed overrides per-tick. Same-direction: trail at safe distance. Opposite-direction: both stop.
5. **~~Post-landing runway exit~~** ✓: `PhaseRunner` auto-appends `RunwayExitPhase` + `HoldingAfterExitPhase` after full-stop landing when ground layout available.
6. **~~Client UI~~** ✓: Ground fields (TaxiRoute, ParkingSpot, CurrentTaxiway) in DTOs and AircraftModel.

#### Definition of Done
- [x] Spawn aircraft at parking positions
- [x] Pushback and taxi to runway
- [x] Hold short of runway, clearance-gated phase transitions
- [x] Cross runway on command
- [x] Basic collision avoidance prevents taxi-through
- [x] Post-landing runway exit with notification
- [x] FOLLOW and GIVEWAY/BEHIND commands
- [ ] End-to-end verification with running server

---

### Milestone 4: STARS Track Operations

**Goal:** Track ownership, handoffs, point-outs, scratchpads, and all documented ATCTrainer/VICE track commands via both RPO and CRC.

Detailed chunk plan: [M4 STARS Track Operations](track-operations.md) (9 chunks)

#### Yaat.Sim

1. **Track ownership types**: `TrackOwner`, `TrackOwnerType`, `Tcp`, `StarsPointout`, `StarsPointoutStatus`
2. **AircraftState fields**: `Owner`, `HandoffPeer`, `HandoffRedirectedBy`, `Pointout`, `Scratchpad1/2`, `TemporaryAltitude`, `IsAnnotated`, `FrequencyChangeApproved`, `ContactPosition`, `OnHandoff`
3. **New CanonicalCommandType values**: `TrackAircraft`, `DropTrack`, `InitiateHandoff`, `AcceptHandoff`, `CancelHandoff`, `AcceptAllHandoffs`, `InitiateHandoffAll`, `PointOut`, `Acknowledge`, `Annotate`, `Scratchpad`, `TemporaryAltitude`, `Cruise`, `OnHandoff`, `FrequencyChange`, `ContactTcp`, `ContactTower`

#### yaat-server

4. **Position infrastructure**: `PositionRegistry` (CRC + RPO position tracking), `ArtccConfigService` extensions (resolve positionId → TrackOwner, TCP code → position)
5. **Scenario loading**: Process `atc[]`, `studentPositionId`, `autoTrackConditions`. Spawn with Owner + immediate handoff to student.
6. **CRC track broadcasting**: Populate `StarsTrackDto.Owner/HandoffPeer/Pointout/Scratchpad`. `StarsConsolidation` topic for TCP ownership.
7. **RPO track command handling**: Server-side dispatch for all track commands (mutate AircraftState ownership fields)
8. **ProcessStarsCommand handling**: Full vNAS protocol — parse `ProcessStarsCommandDto` for `Handoff`, `InitiateControl`, `TerminateControl`, `Implied`. CRC clients can initiate/accept/retract handoffs.
9. **Auto-accept timer**: Configurable delay (YAAT Client Settings). Auto-completes handoffs to unattended positions.

#### yaat client

10. **DataGrid columns**: Owner, Handoff, Scratchpad, TempAlt
11. **Settings**: Auto-accept delay (seconds), sent to server on scenario load
12. **Command patterns**: ATCTrainer (TRACK, DROP, HO, ACCEPT, CANCEL, PO, OK, SP, TA, etc.) + VICE (FC, CT, TO)

#### Definition of Done
- [x] Aircraft spawn with correct Owner from `autoTrackConditions`
- [x] Handoff flows work end-to-end (initiate → flash in CRC → accept/retract)
- [x] Auto-accept timer completes handoffs to unattended positions
- [x] All 17 track commands parsed and handled (both RPO and CRC paths)
- [x] CRC displays correct ownership, handoff, pointout, scratchpad in STARS data blocks
- [ ] StarsConsolidation topic correctly reports TCP ownership — **DEFERRED**
- [x] Training client DataGrid shows ownership columns
- [x] `CommandSchemeCompletenessTests` pass

---

### Milestone 5: Approach Control

**Goal:** Vectors, approach clearances, altitude/speed management.

#### yaat-server

1. **Approach commands**:
   - `CAPP [app] [apt]` / `JAPP [app] [apt]` - Cleared for approach / join approach
   - `CAPPSI [app] [apt]` / `JAPPSI [app] [apt]` - Join approach straight-in (skips hold-in-lieu at IAF)
   - `JFAC [app]` - Join final approach course
   - `JARR {star} [trans]` - Join STAR
   - ~~`DCT {wpt}` - Direct to waypoint~~ (basic DCT done in M1; M5 adds course intercepts and procedure-based nav)
   - `JRADO / JRAD {fix}{radial:3}` - Join radial outbound / join radial: fly present heading until intercepting the radial, then track outbound (away from fix). E.g., `JRADO OAK090` — fly until intercepting the OAK 090 radial, then fly heading 090 (outbound from OAK).
   - `JRADI / JICRS {fix}{radial:3}` - Join radial inbound  / join inbound course: fly present heading until intercepting the radial, then track inbound (toward the fix). E.g., `JRADI OAK090` — fly until intercepting the OAK 090 radial, then fly heading 270 (inbound to OAK on the 090 radial).
   - `HOLD {wpt} {crs} {dist} {dir} [entry]` - Holding pattern
     - `{dist}` accepts distance (`3`) for nm-based legs or time (`1M`) for minute-based legs
     - `[entry]` optional: `D` (direct), `T` (teardrop), `P` (parallel) — auto-determined from aircraft heading vs inbound course (70° sector boundaries) if omitted
   - `PTAC {hdg} {alt} {app}` - Position-Turn-Altitude-Clearance vector to final: fly heading, maintain altitude until established, cleared approach. Position report is implicit (not in the RPO command). E.g., `PTAC 280 025 ILS30` — fly heading 280, maintain 2,500 until established, cleared ILS runway 30
   - `DVIA [alt]` - Descend via path
   - `CFIX {wpt} {alt} [spd]` - Cross fix at altitude
   - `DEPART {wpt} {hdg}` - Depart fix on heading
   - Commands are composable via compound syntax: e.g., `DCT MYTOE; CFIX A040; CAPP ILS30` — direct MYTOE, cross at or above 4,000, cleared ILS 30 approach
2. **Approach intercept validation**: When joining an approach course (`CAPP`/`JAPP`/`PTAC`), the aircraft's heading must achieve a ≤30° intercept angle with the final approach course. If the angle exceeds 30°, the command warns RPOs via terminal broadcast and the aircraft does not turn onto the approach. `CAPPF`/`JAPPF` (force variants) override the check and join regardless of intercept angle.
3. **Hold-in-lieu of procedure turn**: When `DCT {fix}` or `JAPP` routes through an IAF that has a hold-in-lieu defined in NavData, the aircraft executes one circuit of the hold before continuing inbound on the approach. The hold-in-lieu inbound course and pattern come from NavData (no user input needed). `CAPPSI`/`JAPPSI` skips the hold-in-lieu and joins the approach course directly. Command feedback (`CommandResultDto`) should indicate when a hold-in-lieu will be executed and include the hold details (fix, inbound course, direction, leg length).
4. **Navigation**: Waypoint database from vNAS NavData.dat for DCT/procedure resolution
5. **Approach path following**: Aircraft track ILS/RNAV approach paths
6. **Altitude/speed management**: `CM`, `EXP`, `NORM`, `SPD`, `RFAS`, `RNS`

#### yaat client

1. **Route display**: Show assigned route/approach for selected aircraft
2. **Approach state**: Show approach clearance status

#### Definition of Done
- Vector aircraft to final approach course
- Clear for ILS/Visual approach
- Aircraft follows approach path to landing
- STAR and waypoint navigation works

---

### Milestone 6: Enroute Control & ERAM

**Goal:** Enroute navigation, airways, Mach, ERAM track operations.

#### yaat-server

1. **Enroute navigation**: Airway following, high-altitude operations
2. **Mach speed**: `MACH {mach}` command
3. **ERAM track operations**: `ProcessEramMessage` handling (HO, QD, PO tokens), `EramTrackDto` ownership population, `EramPointout` support (multiple simultaneous point-outs per track)
4. **ERAM data block format**: Context-aware Fdb/PairedLdb/UnpairedLdb based on sector ownership and quick-look state

#### Definition of Done
- Aircraft navigate airways
- High-altitude Mach operations
- ERAM track ownership and handoffs work in CRC

---

### Milestone 7: Multi-User & Deployment

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

### Milestone 8: v2 Scenario Format

**Goal:** Enhanced scenario format with features ATCTrainer doesn't support.

1. **v2 schema**: Wind/weather, timed events, scoring criteria, custom waypoints, multi-airport
2. **v1 import with upgrade path**: Auto-convert v1 scenarios to v2
3. **Scenario editor**: Create/edit scenarios in YAAT client

---

### Milestone 9: Automated Pilot Logic (Future)

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

### UDP Transport (Future Enhancement)
Real vNAS servers use UDP alongside WebSocket for high-frequency entity updates (StarsTracks, EramTargets, etc.). CRC expects a UDP endpoint and will attempt to register after calling `GetServerConfiguration()`.

**Current state:** A UDP stub server (`UdpStubServer`) listens on port 6809 and handles:
- `RegisterConnection` (message type 1) — stores client mapping, responds with ACK
- Keepalive ping (message type 0) — responds with pong
- Server-initiated keepalive (15s interval) — sends ACK to all registered clients

Entity updates are **not** sent over UDP yet; CRC receives them via WebSocket invocations (`ReceiveStarsTracks`, etc.).

**Future work:** Migrate entity updates to UDP for lower latency and smaller payloads (MessagePack without SignalR framing overhead). This becomes important when serving many simultaneous CRC clients. Reference implementations:
- `vatsim-server-rs/crates/server/src/udp.rs` — server-side UDP with entity dispatch
- `towercab-3d-vnas/src/udp/` — client-side UDP socket, message types, keepalive

---

## Detailed Milestone Plans

- [Milestone 0: Proof of Concept](completed/milestone-0.md) — COMPLETE
- [Milestone 1: Scenario Loading & Basic RPO Commands](completed/milestone-1.md) — COMPLETE
- [Milestone 2: Local Control (Tower)](milestone-2.md)
- [Milestone 3: Ground Operations](encapsulated-crunching-sutherland.md)
- [Milestone 4: STARS Track Operations](track-operations.md)

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
