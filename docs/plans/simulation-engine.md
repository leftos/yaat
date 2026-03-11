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

### Step 6: Create TestAirportGroundData helper
- [ ] Create `tests/Yaat.Sim.Tests/Helpers/TestAirportGroundData.cs`

### Step 7: Add NavData loading to TestVnasData
- [ ] Extend `tests/Yaat.Sim.Tests/TestVnasData.cs` with FixDatabase property

### Step 8: Create E2E replay tests
- [ ] Create `tests/Yaat.Sim.Tests/Simulation/SimulationEngineReplayTests.cs`
- [ ] Replay_OakTaxi_NKS2904_HasTaxiRoute
- [ ] Replay_OakTaxi_NKS2904_TaxiRouteFollowsExpectedPath
- [ ] Replay_OakTaxi_NKS2904_MovedFromParking

### Step 9: Verify
- [x] `dotnet build` both repos with TreatWarningsAsErrors — 0W 0E
- [x] `dotnet test` — Sim: 1158, Client: 209, Server: 216
- [ ] New E2E replay tests pass
- [x] Format: `dotnet format style && dotnet format analyzers && dotnet csharpier format .`

## Files created in Yaat.Sim
- `src/Yaat.Sim/Simulation/SimulationEngine.cs`
- `src/Yaat.Sim/Simulation/SimScenarioState.cs`
- `src/Yaat.Sim/Simulation/ScenarioQueues.cs`
- `src/Yaat.Sim/Simulation/ConsolidationState.cs`
- `src/Yaat.Sim/Simulation/ScratchpadRuleEngine.cs`
- `src/Yaat.Sim/Scenarios/SpawnParser.cs` (moved from server)
- `src/Yaat.Sim/Data/Vnas/ArtccConfig.cs` (moved from server)

## Files deleted from server
- `src/Yaat.Server/Spawn/SpawnParser.cs`
- `src/Yaat.Server/Data/ArtccConfig.cs`
- `src/Yaat.Server/Simulation/ConsolidationState.cs`
- `src/Yaat.Server/Simulation/ScratchpadRuleEngine.cs`

## Next: More server→Sim migration candidates
Potential files to move from yaat-server's Simulation folder:
- `AtpaProcessor.cs` / `AtpaVolumeGeometry.cs` — ATPA logic, depends on config types (now in Sim)
- `BeaconCodePool.cs` — beacon code assignment, depends on config types (now in Sim)
- `TowerListTracker.cs` — tower list logic
- `ConflictAlertState.cs` — conflict detection state
- `DtoConverter.cs` — partial move (non-DTO parts)
- More analysis needed to identify clean boundaries
