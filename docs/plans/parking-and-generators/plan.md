# Ground Ops, Parking & Generators Sprint

One sprint to make ground-level aircraft work end-to-end and enable procedural traffic generation. Three features that share a common foundation:

1. **Ground data infrastructure** — `IAirportGroundData` service on the server, wired through DI and into every path that needs a ground layout
2. **Parking & ground-level spawns** — Parking position type, Coordinates/FixOrFrd ground detection, heading field, ground layout in tick + command dispatch
3. **Aircraft generators** — procedural arrival traffic spawning on final during simulation

All three remove stale deferral messages and close schema gaps. Work is split into three phases with a shared foundation; phases 2 and 3 can run in parallel.

---

## Phase 1: Models & Infrastructure

Shared foundation — every subsequent phase depends on this.

- [ ] `ScenarioModels.cs` — add `Heading` (double?) and `Parking` (string?) to `StartingConditions`
- [ ] `ScenarioModels.cs` — expand `AircraftGenerator` stub to full 13-field model
- [ ] `YaatOptions.cs` — add `AirportFilesPath` (optional string, no `ValidateOnStart`)
- [ ] New `Data/AirportGroundDataService.cs` — `IAirportGroundData` impl with lazy GeoJSON loading + cache
- [ ] `Program.cs` — register `IAirportGroundData` singleton in DI
- [ ] `SimulationHostedService.cs` — inject `IAirportGroundData` in constructor

Details: [ground-infrastructure.md](ground-infrastructure.md)

## Phase 2: Parking & Ground Ops

Depends on Phase 1. Makes ground-level aircraft spawn correctly and respond to commands.

**Scenario loading:**
- [ ] `ScenarioLoader.cs` — implement `LoadAtParking()` (resolve airport → layout → parking node → `AircraftInitializer.InitializeAtParking`)
- [ ] `ScenarioLoader.cs` — Coordinates/FixOrFrd ground detection: `speed <= 0 && alt < 200` → set `IsOnGround`, create `AtParkingPhase`
- [ ] `ScenarioLoader.cs` — heading resolution: check `cond.Heading` first, fall back to `NavigationPath`
- [ ] `ScenarioLoader.cs` — thread `IAirportGroundData?` through `Load()` → `LoadAircraft()` → `LoadAtParking()`
- [ ] `ScenarioLoader.cs` — remove parking deferral message

**Runtime wiring:**
- [ ] `SimulationHostedService.cs` — add `ResolveGroundLayout(AircraftState)` helper
- [ ] `SimulationHostedService.cs` — pass ground layout in `SendCommandAsync` → `DispatchCompound`
- [ ] `SimulationHostedService.cs` — pass ground layout in `DispatchPresetCommands` → `Dispatch`
- [ ] `SimulationHostedService.cs` — add `GroundLayout` to `PhaseContext` in PreTick when `IsOnGround`
- [ ] `SimulationHostedService.cs` — pass `_groundData` to `ScenarioLoader.Load` call

**Yaat.Sim fix:**
- [ ] `CommandDispatcher.cs` — add `groundLayout` param to `Dispatch`; route ground commands through `DispatchCompound`
- [ ] `CommandDispatcher.cs` — reject ground commands in no-phase path with clear error (defense-in-depth)

Details: [parking-and-ground-ops.md](parking-and-ground-ops.md)

## Phase 3: Aircraft Generators

Depends on Phase 1. Independent of Phase 2.

- [ ] `ScenarioSession.cs` — add `GeneratorState` class + `Generators` list
- [ ] `ScenarioLoader.cs` — add `Generators` to `ScenarioLoadResult`; remove generator deferral message
- [ ] `SimulationHostedService.cs` — initialize generators in `LoadScenario` (validate runway, resolve `RunwayInfo`, create state)
- [ ] `SimulationHostedService.cs` — add `ProcessGenerators` to tick loop after `ProcessDelayedSpawns`
- [ ] `SimulationHostedService.cs` — add `AdvanceGenerator` (interval timing with jitter, distance wrapping)
- [ ] `SimulationHostedService.cs` — clean up generators in `ExecuteUnloadScenario`

Details: [generators.md](generators.md)

## Verification

- [ ] `dotnet build` — zero warnings (both repos)
- [ ] `dotnet test` — no regressions (both repos)
- [ ] Parking: load scenario with Parking aircraft + `AirportFilesPath` → aircraft at parking with `AtParkingPhase`
- [ ] Parking: verify graceful fallback without `AirportFilesPath` (warning + defer)
- [ ] Ground spawn: load scenario with ground-level Coordinates aircraft → `IsOnGround` + `AtParkingPhase`, heading matches `heading` field
- [ ] Ground commands: `TAXI B 28R` on ground aircraft → aircraft moves
- [ ] Generators: load scenario with `aircraftGenerators` → timed spawns on final for configured runway
- [ ] Generators: pausing freezes generator timers

## Post-implementation

- [ ] Update schema support status below to reflect new [x] items
- [ ] Update `CLAUDE.md` architecture section if structural changes warrant it

---

## Files Modified

| File | Repo | Phase | Changes |
|------|------|:-----:|---------|
| `Scenarios/ScenarioModels.cs` | server | 1 | `Heading` + `Parking` on StartingConditions; full `AircraftGenerator` model |
| `YaatOptions.cs` | server | 1 | `AirportFilesPath` |
| `Data/AirportGroundDataService.cs` | server | 1 | **New** — `IAirportGroundData` impl |
| `Program.cs` | server | 1 | DI registration |
| `Scenarios/ScenarioLoader.cs` | server | 1+2+3 | `LoadAtParking`; ground detection; heading; `groundData` param; generators passthrough; remove deferrals |
| `Simulation/ScenarioSession.cs` | server | 3 | `GeneratorState` class + `Generators` list |
| `Simulation/SimulationHostedService.cs` | server | 1+2+3 | Inject ground data; resolve+pass layout; generators init+tick+cleanup |
| `Commands/CommandDispatcher.cs` | yaat | 2 | `groundLayout` param on `Dispatch`; ground command routing |

## Reused Infrastructure

| Component | File | Used by |
|-----------|------|---------|
| `AircraftInitializer.InitializeAtParking()` | `Yaat.Sim/Scenarios/AircraftInitializer.cs` | Parking |
| `IAirportGroundData` interface | `Yaat.Sim/Data/Airport/IAirportGroundData.cs` | All |
| `AirportGroundLayout.FindParkingByName()` | `Yaat.Sim/Data/Airport/AirportGroundLayout.cs` | Parking |
| `GeoJsonParser.Parse()` / `ParseMultiple()` | `Yaat.Sim/Data/Airport/GeoJsonParser.cs` | Ground data service |
| `FixDatabase.GetAirportElevation()` | `Yaat.Sim/Data/FixDatabase.cs` | Parking |
| `AircraftGenerator.Generate()` | `Yaat.Sim/Scenarios/AircraftGenerator.cs` | Generators |
| `SpawnRequest` (OnFinal variant) | `Yaat.Sim/Scenarios/SpawnRequest.cs` | Generators |
| `ApplyAutoTrackConditions()` | `SimulationHostedService.cs` | Generators |
| `ProcessDelayedSpawns()` pattern | `SimulationHostedService.cs` | Generators (same structure) |
| `AutoTrackConditions` model | `ScenarioModels.cs` | Generators |

---

## Scenario Schema Support Status

Living reference — update checkboxes as each phase completes.
Reference examples: `X:\dev\lc-trainer\docs\atctrainer-scenario-examples\`

**Legend:** [x] Implemented — [~] Parsed only — [ ] Not supported

### Top-Level Scenario Fields

- [x] `id` — scenario session ID
- [x] `name` — UI and logs
- [x] `artccId` — VNAS data context
- [x] `aircraft[]` — loaded into simulation
- [x] `initializationTriggers[]` — queued at time offsets (SQALL supported)
- [~] `aircraftGenerators[]` — stub (`Id` only) → **Phase 3 implements**
- [x] `atc[]` — resolved against ARTCC config
- [x] `primaryAirportId` — stored on session
- [~] `primaryApproach` — parsed, not used
- [~] `studentPositionId` — parsed, not used
- [~] `autoDeleteMode` — parsed, not used
- [~] `minimumRating` — parsed, not used
- [~] `flightStripConfigurations[]` — stub (`Id` only), not used

### Per-Aircraft Fields (`aircraft[]`)

- [x] `id`, `aircraftId`, `aircraftType`, `transponderMode`
- [x] `startingConditions`, `flightplan`
- [x] `presetCommands[]` — timeOffset=0 only
- [x] `spawnDelay`, `airportId`
- [~] `onAltitudeProfile`, `difficulty`, `autoTrackConditions`, `expectedApproach`

### Starting Conditions

**Position types:**
- [x] `Coordinates`, `FixOrFrd`, `OnRunway`, `OnFinal`
- [ ] `Parking` → **Phase 2 implements**

**Fields:**
- [x] `type`, `fix`, `coordinates`, `runway`, `altitude`, `speed`, `navigationPath`
- [ ] `heading` → **Phase 2 implements**
- [ ] `parking` → **Phase 2 implements**
- [ ] `distanceFromRunway` — not in this sprint

### Aircraft Generators (`aircraftGenerators[]`)

All stubbed → **Phase 3 implements all:**
- [ ] `runway`, `engineType`, `weightCategory`
- [ ] `initialDistance`, `maxDistance`, `intervalDistance`
- [ ] `startTimeOffset`, `maxTime`, `intervalTime`
- [ ] `randomizeInterval`, `randomizeWeightCategory`
- [ ] `autoTrackConfiguration`

### Other (unchanged by this sprint)

- **Flight plan:** 7 implemented, 1 parsed (`remarks`)
- **Preset commands:** 2 implemented, 1 not supported (`timeOffset > 0`)
- **Init triggers:** all implemented
- **ATC positions:** 5 implemented, 1 parsed (`autoTrackAirportIds`)
- **Flight strip configs:** all not supported

### Summary (current → after sprint)

| Category | Implemented | Parsed only | Not supported |
|----------|:-----------:|:-----------:|:-------------:|
| Top-level scenario | 6 → 7 | 6 → 5 | 0 |
| Per-aircraft | 7 | 4 | 0 |
| Starting conditions | 9 → 12 | 0 | 4 → 1 |
| Flight plan | 7 | 1 | 0 |
| Preset commands | 2 | 0 | 1 |
| Init triggers | 3 | 0 | 0 |
| Aircraft generators | 0 → 12 | 0 | 12 → 0 |
| ATC positions | 5 | 1 | 0 |
| Flight strip configs | 0 | 0 | 4 |
| **Total** | **39 → 53** | **12 → 11** | **21 → 6** |

---

## Gaps Remaining After Sprint

1. `distanceFromRunway` — OnFinal spawn distance from threshold
2. Navigation path as route — aircraft should fly the path, not just point toward first fix
3. `onAltitudeProfile` — VNAV descent along STAR altitude constraints
4. Timed preset commands (`timeOffset > 0`)
5. `remarks` — flight plan remarks
6. ATC `autoTrackAirportIds` / per-aircraft `autoTrackConditions`
7. Auto-delete mode — auto-remove aircraft on landing or parking
8. Flight strip configurations
