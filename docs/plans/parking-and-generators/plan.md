# Ground Ops, Parking & Generators Sprint

One sprint to make ground-level aircraft work end-to-end and enable procedural traffic generation. Three features that share a common foundation:

1. **Ground data infrastructure** — `IAirportGroundData` service on the server, wired through DI and into every path that needs a ground layout
2. **Parking & ground-level spawns** — Parking position type, Coordinates/FixOrFrd ground detection, heading field, ground layout in tick + command dispatch
3. **Aircraft generators** — procedural arrival traffic spawning on final during simulation

All three remove stale deferral messages and close schema gaps. Work is split into three phases with a shared foundation; phases 2 and 3 can run in parallel.

---

## Phase 1: Models & Infrastructure

Shared foundation — every subsequent phase depends on this.

- [x] `ScenarioModels.cs` — add `Heading` (double?) and `Parking` (string?) to `StartingConditions`
- [x] `ScenarioModels.cs` — expand `AircraftGenerator` stub to full 13-field model
- [x] `YaatOptions.cs` — add `ArtccResourcesPath` (optional string, no `ValidateOnStart`)
- [x] New `Data/AirportGroundDataService.cs` — `IAirportGroundData` impl with lazy GeoJSON loading + cache
- [x] `Program.cs` — register `IAirportGroundData` singleton in DI
- [x] `SimulationHostedService.cs` — inject `IAirportGroundData` in constructor

Details: [ground-infrastructure.md](ground-infrastructure.md)

## Phase 2: Parking & Ground Ops

Depends on Phase 1. Makes ground-level aircraft spawn correctly and respond to commands.

**Scenario loading:**
- [x] `ScenarioLoader.cs` — implement `LoadAtParking()` (resolve airport → layout → parking node → `AircraftInitializer.InitializeAtParking`)
- [x] `ScenarioLoader.cs` — Coordinates/FixOrFrd ground detection: `speed <= 0 && alt < 200` → set `IsOnGround`, create `AtParkingPhase`
- [x] `ScenarioLoader.cs` — heading resolution: check `cond.Heading` first, fall back to `NavigationPath`
- [x] `ScenarioLoader.cs` — thread `IAirportGroundData?` through `Load()` → `LoadAircraft()` → `LoadAtParking()`
- [x] `ScenarioLoader.cs` — remove parking deferral message

**Runtime wiring:**
- [x] `SimulationHostedService.cs` — add `ResolveGroundLayout(AircraftState)` helper
- [x] `SimulationHostedService.cs` — pass ground layout in `SendCommandAsync` → `DispatchCompound`
- [x] `SimulationHostedService.cs` — pass ground layout in `DispatchPresetCommands` → `Dispatch`
- [x] `SimulationHostedService.cs` — add `GroundLayout` to `PhaseContext` in PreTick when `IsOnGround`
- [x] `SimulationHostedService.cs` — pass `_groundData` to `ScenarioLoader.Load` call

**Yaat.Sim fix:**
- [x] `CommandDispatcher.cs` — add `groundLayout` param to `Dispatch`; route ground commands through `DispatchCompound`
- [x] `CommandDispatcher.cs` — reject ground commands in no-phase path with clear error (defense-in-depth)

Details: [parking-and-ground-ops.md](parking-and-ground-ops.md)

## Phase 3: Aircraft Generators

Depends on Phase 1. Independent of Phase 2.

- [x] `ScenarioSession.cs` — add `GeneratorState` class + `Generators` list
- [x] `ScenarioLoader.cs` — add `Generators` to `ScenarioLoadResult`; remove generator deferral message
- [x] `SimulationHostedService.cs` — initialize generators in `LoadScenario` (validate runway, resolve `RunwayInfo`, create state)
- [x] `SimulationHostedService.cs` — add `ProcessGenerators` to tick loop after `ProcessDelayedSpawns`
- [x] `SimulationHostedService.cs` — add `AdvanceGenerator` (interval timing with jitter, distance wrapping)
- [x] `SimulationHostedService.cs` — clean up generators in `ExecuteUnloadScenario`

Details: [generators.md](generators.md)

## Verification

- [x] `dotnet build` — zero warnings (both repos)
- [x] `dotnet test` — no regressions (both repos)
- [ ] Parking: load scenario with Parking aircraft + `ArtccResourcesPath` → aircraft at parking with `AtParkingPhase`
- [ ] Parking: verify graceful fallback without `ArtccResourcesPath` (warning + defer)
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
| `YaatOptions.cs` | server | 1 | `ArtccResourcesPath` |
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
| `GeoJsonParser.Parse()` | `Yaat.Sim/Data/Airport/GeoJsonParser.cs` | Ground data service |
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
- [x] `aircraftGenerators[]` — full model deserialized, generators initialized at load
- [x] `atc[]` — resolved against ARTCC config
- [x] `primaryAirportId` — stored on session
- [~] `primaryApproach` — parsed, not used
- [~] `studentPositionId` — parsed, not used
- [x] `autoDeleteMode` — session auto-delete with client override; OnLanding/Parked modes
- [~] `minimumRating` — parsed, not used
- [~] `flightStripConfigurations[]` — stub (`Id` only), not used

### Per-Aircraft Fields (`aircraft[]`)

- [x] `id`, `aircraftId`, `aircraftType`, `transponderMode`
- [x] `startingConditions`, `flightplan`
- [x] `presetCommands[]` — all timeOffsets supported (0=immediate, >0=timed queue)
- [x] `spawnDelay`, `airportId`
- [x] `autoTrackConditions` — resolved at load; handoff/scratchpad/altitude
- [~] `onAltitudeProfile`, `difficulty`, `expectedApproach`

### Starting Conditions

**Position types:**
- [x] `Coordinates`, `FixOrFrd`, `OnRunway`, `OnFinal`
- [x] `Parking` — resolves via ground layout, defers gracefully if no data

**Fields:**
- [x] `type`, `fix`, `coordinates`, `runway`, `altitude`, `speed`
- [x] `navigationPath` — resolves fixes, populates NavigationRoute, chains filed route via RouteChainer
- [x] `heading` — used directly when set, falls back to nav-path bearing
- [x] `parking` — parking node name for Parking position type
- [x] `distanceFromRunway` — OnFinal spawn distance override; altitude derived from glideslope

### Aircraft Generators (`aircraftGenerators[]`)

- [x] `runway`, `engineType`, `weightCategory`
- [x] `initialDistance`, `maxDistance`, `intervalDistance`
- [x] `startTimeOffset`, `maxTime`, `intervalTime`
- [x] `randomizeInterval`, `randomizeWeightCategory`
- [x] `autoTrackConfiguration`

### Other

- **Flight plan:** 8 implemented (including `remarks`)
- **Preset commands:** 3 implemented (immediate + timed)
- **Init triggers:** all implemented
- **ATC positions:** 6 implemented (including `autoTrackAirportIds`)
- **Flight strip configs:** all not supported

### Summary (after schema gaps sprint)

| Category | Implemented | Parsed only | Not supported |
|----------|:-----------:|:-----------:|:-------------:|
| Top-level scenario | 8 | 4 | 0 |
| Per-aircraft | 8 | 3 | 0 |
| Starting conditions | 14 | 0 | 0 |
| Flight plan | 8 | 0 | 0 |
| Preset commands | 3 | 0 | 0 |
| Init triggers | 3 | 0 | 0 |
| Aircraft generators | 12 | 0 | 0 |
| ATC positions | 6 | 0 | 0 |
| Flight strip configs | 0 | 0 | 4 |
| **Total** | **62** | **7** | **4** |

---

## Gaps Remaining

1. ~~`distanceFromRunway`~~ — **resolved**
2. ~~Navigation path as route~~ — **resolved** (NavigationRoute populated + RouteChainer wired to DCT)
3. `onAltitudeProfile` — **deferred** (needs CIFP STAR constraints + new physics mode; aviation-sim-expert required)
4. ~~Timed preset commands~~ — **resolved**
5. ~~`remarks`~~ — **resolved**
6. ~~`autoTrackAirportIds` / `autoTrackConditions`~~ — **already done** (ApplyAutoTrackConditions)
7. ~~Auto-delete mode~~ — **resolved** (session flag + client override + NODEL on TAXI)
8. Flight strip configurations — **deferred** (needs client UI design)
