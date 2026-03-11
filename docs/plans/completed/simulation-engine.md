# Plan: SimulationEngine + E2E Replay Tests

## Context

Investigating 5 ground taxi bugs at OAK (issues #42-46) after taxi speeds were doubled. To reproduce and test these bugs deterministically, we need a headless simulation engine that replays recorded sessions identically to the server.

## Architecture

The server's tick loop (TickProcessor + RoomEngine) interleaves pure simulation with SignalR broadcasting. Rather than extracting the server's code (too many broadcast side-effects woven in), we build a **new SimulationEngine in Yaat.Sim** that replicates the pure simulation path. The server keeps its own TickProcessor for now.

```
SimulationEngine (Yaat.Sim)          Server TickProcessor
├─ LoadScenario                      ├─ ProcessPrePhysics (+ broadcasts)
├─ TickOneSecond                     ├─ ProcessPhysics
│  ├─ ProcessDelayedSpawns           ├─ ProcessPostPhysics (+ broadcasts)
│  ├─ ProcessPresets                 └─ PreTick (+ broadcasts)
│  ├─ 4× World.Tick(0.25, PreTick)
│  └─ DrainWarnings
├─ Replay(recording, targetSeconds)
├─ ApplyRecordedAction
└─ SendCommand
```

## Steps

### Step 1: Move SpawnParser to Yaat.Sim
- [x] Move `yaat-server/src/Yaat.Server/Spawn/SpawnParser.cs` → `src/Yaat.Sim/Scenarios/SpawnParser.cs`
- [x] Change namespace to `Yaat.Sim.Scenarios`
- [x] Delete server copy, update server imports

### Step 2: Move scenario queue types to Yaat.Sim
- [x] Create `src/Yaat.Sim/Simulation/ScenarioQueues.cs` (DelayedSpawn, ScheduledPreset, ScheduledTrigger, GeneratorState)
- [x] Remove from server's `ScenarioState.cs`, update imports

### Step 3: Create SimScenarioState in Yaat.Sim
- [x] Create `src/Yaat.Sim/Simulation/SimScenarioState.cs`

### Step 4: Create SimulationEngine
- [x] Create `src/Yaat.Sim/Simulation/SimulationEngine.cs`
- [x] LoadScenario, TickOneSecond, Replay, SendCommand, FindAircraft
- [x] PreTick (builds PhaseContext, calls PhaseRunner.Tick)
- [x] ResolveGroundLayout, ProcessDelayedSpawns, ProcessTimedPresets, ProcessTriggers
- [x] ApplyRecordedAction, ReplayCommand (with track/coordination command filters)

### Step 5: Move ARTCC config models, ConsolidationState, ScratchpadRuleEngine to Yaat.Sim
- [x] Move `ArtccConfig.cs` (all model types) → `src/Yaat.Sim/Data/Vnas/ArtccConfig.cs`
- [x] Move `ConsolidationState.cs` → `src/Yaat.Sim/Simulation/ConsolidationState.cs`
- [x] Move `ScratchpadRuleEngine.cs` → `src/Yaat.Sim/Simulation/ScratchpadRuleEngine.cs`
- [x] Update all server imports

### Step 5b: Move remaining server→Sim candidates
- [x] `AtpaProcessor.cs` / `AtpaVolumeGeometry.cs` → `src/Yaat.Sim/Data/Vnas/`
- [x] `BeaconCodePool.cs` → `src/Yaat.Sim/Data/Vnas/BeaconCodePool.cs`
- [x] `TowerListTracker.cs` → `src/Yaat.Sim/TowerListTracker.cs`
- [x] `ConflictAlertState.cs` → `src/Yaat.Sim/ConflictAlertState.cs`
- [x] `ApproachEvaluator.cs` → `src/Yaat.Sim/Phases/ApproachEvaluator.cs`
- [x] `ScenarioLoader.cs`, `AircraftGenerator.cs`, `AircraftInitializer.cs` → `src/Yaat.Sim/Scenarios/`

### Step 6: Create TestAirportGroundData helper
- [x] Create `tests/Yaat.Sim.Tests/Helpers/TestAirportGroundData.cs`
- [x] Implements `IAirportGroundData`; loads layouts from `TestData/{id}.geojson` (lowercased)
- [x] Returns `null` for unknown airports (silently skips, like `AirportE2ETests`)
- [x] Normalises "KOAK" → "OAK" (strips leading K for standard 4-letter ICAO codes)

### Step 7: Add NavData loading to TestVnasData
- [x] Extend `tests/Yaat.Sim.Tests/TestVnasData.cs` with `FixDatabase` property
- [x] Loads `TestData/NavData.dat` via `NavDataSet.Parser.ParseFrom`, builds real `FixDatabase`
- [x] Returns `null` if file absent (tests silently skip)

### Step 8: Create E2E replay tests
- [x] Create `tests/Yaat.Sim.Tests/Simulation/SimulationEngineReplayTests.cs`
- [x] `Replay_OakTaxi_NKS2904_HasTaxiRoute` — after replay, NKS2904.AssignedTaxiRoute is not null
- [x] `Replay_OakTaxi_NKS2904_TaxiRouteFollowsExpectedPath` — route passes through S, T, U, W, W1
- [x] `Replay_OakTaxi_NKS2904_MovedFromParking` — after 96s, aircraft moved from parking 11 position

### Step 9: Verify
- [x] `dotnet build` both repos with TreatWarningsAsErrors — 0W 0E
- [x] `dotnet test` — Sim: 1161, Client: 209
- [x] New E2E replay tests pass (3/3)
- [x] Format: `dotnet format style && dotnet format analyzers && dotnet csharpier format .`

## Files in Yaat.Sim (current state)

### Created by this plan
- `src/Yaat.Sim/Simulation/SimulationEngine.cs`
- `src/Yaat.Sim/Simulation/SimScenarioState.cs`
- `src/Yaat.Sim/Simulation/ScenarioQueues.cs`
- `src/Yaat.Sim/Simulation/ConsolidationState.cs`
- `src/Yaat.Sim/Simulation/ScratchpadRuleEngine.cs`
- `src/Yaat.Sim/Simulation/SessionRecording.cs`
- `src/Yaat.Sim/Simulation/RecordedAction.cs`
- `src/Yaat.Sim/Scenarios/SpawnParser.cs` (moved from server)
- `src/Yaat.Sim/Scenarios/ScenarioLoader.cs` (moved from server)
- `src/Yaat.Sim/Scenarios/AircraftGenerator.cs` (moved from server)
- `src/Yaat.Sim/Scenarios/AircraftInitializer.cs` (moved from server)
- `src/Yaat.Sim/Data/Vnas/ArtccConfig.cs` (moved from server)
- `src/Yaat.Sim/Data/Vnas/BeaconCodePool.cs` (moved from server)
- `src/Yaat.Sim/Data/Vnas/AtpaProcessor.cs` (moved from server)
- `src/Yaat.Sim/Data/Vnas/AtpaVolumeGeometry.cs` (moved from server)
- `src/Yaat.Sim/TowerListTracker.cs` (moved from server)
- `src/Yaat.Sim/ConflictAlertState.cs` (moved from server)
- `src/Yaat.Sim/Phases/ApproachEvaluator.cs` (moved from server)

### Test data (already present)
- `tests/Yaat.Sim.Tests/TestData/oak-taxi-recording.json` — 2-action NKS2904 taxi recording, 96s
- `tests/Yaat.Sim.Tests/TestData/oak.geojson` — OAK airport ground layout
- `tests/Yaat.Sim.Tests/TestData/sfo.geojson` — SFO airport ground layout

## Remaining work summary

Steps 6 and 7 unblock Step 8. All three are small — the `TestAirportGroundData` helper wraps GeoJSON loading already done in `AirportE2ETests`; `TestOakFixLookup` follows the `StubRunwayLookup` pattern already in `GroundCommandHandlerTests`.

The recording has only 2 actions (both at t=0 for NKS2904) and runs for 96 seconds, making the replay test fast and deterministic.
