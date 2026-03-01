# Phase 3: Aircraft Generators

Procedural arrival traffic generation. Independent of Phase 2 — only depends on the expanded `AircraftGenerator` model from Phase 1.

## ScenarioSession.cs — GeneratorState

**File:** `X:\dev\yaat-server\src\Yaat.Server\Simulation\ScenarioSession.cs`

New class:
```csharp
public sealed class GeneratorState
{
    public required AircraftGenerator Config { get; init; }
    public required RunwayInfo Runway { get; init; }
    public double NextSpawnSeconds { get; set; }
    public double NextSpawnDistance { get; set; }
    public bool IsExhausted { get; set; }
}
```

Add to `ScenarioSession`:
```csharp
public List<GeneratorState> Generators { get; } = [];
```

## ScenarioLoader.cs — Passthrough

**File:** `X:\dev\yaat-server\src\Yaat.Server\Scenarios\ScenarioLoader.cs`

- Add `public List<AircraftGenerator> Generators { get; init; } = [];` to `ScenarioLoadResult`
- Remove the "deferred to M4" warning (lines 77-80)
- Set `Generators = scenario.AircraftGenerators` in the return value

## SimulationHostedService.cs — Generator Init

**File:** `X:\dev\yaat-server\src\Yaat.Server\Simulation\SimulationHostedService.cs`

In `LoadScenario`, after delayed/trigger queue population, for each `result.Generators`:
1. Validate: need `primaryAirportId` and `gen.Runway` — warn and skip if missing
2. Resolve `RunwayInfo` via `_runways.GetRunway(airportId, gen.Runway)` — warn and skip if null
3. Create `GeneratorState` with `NextSpawnSeconds = gen.StartTimeOffset`, `NextSpawnDistance = gen.InitialDistance`
4. Add to `session.Generators`

## SimulationHostedService.cs — ProcessGenerators

Same file. Call after `ProcessDelayedSpawns` in `RunTickLoop` (line 558).

`ProcessGenerators(session)` iterates each non-exhausted generator:
1. Skip if `ElapsedSeconds < NextSpawnSeconds`
2. Mark exhausted if `ElapsedSeconds > Config.MaxTime`
3. Build `SpawnRequest` with `PositionType = OnFinal`, `RunwayId`, `FinalDistanceNm = NextSpawnDistance`
4. Resolve weight/engine from config strings (map `"SmallPlus"` → `WeightClass.Large`)
5. If `RandomizeWeightCategory`, pick random weight with realistic distribution (15% Small, 70% Large, 15% Heavy)
6. Call `AircraftGenerator.Generate(request, primaryAirportId, _fixes, _runways, existingAircraft)`
7. Set `state.ScenarioId`, `state.Destination`
8. Apply auto-track if configured (construct temporary `LoadedAircraft`, call `ApplyAutoTrackConditions`)
9. Add to world, broadcast `AircraftSpawned`, log

## SimulationHostedService.cs — AdvanceGenerator

Called after each successful spawn:
- Add `IntervalTime` to `NextSpawnSeconds`
- If `RandomizeInterval`: apply +-25% jitter, floor 30 seconds
- Add `IntervalDistance` to `NextSpawnDistance`; wrap to `InitialDistance` if exceeds `MaxDistance`

## SimulationHostedService.cs — Cleanup

In `ExecuteUnloadScenario` (line 1597), add `session.Generators.Clear()` after `session.TriggerQueue.Clear()`.
