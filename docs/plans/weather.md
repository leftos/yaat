# Weather Support Plan

## Context

YAAT currently has zero weather/wind implementation. ATCTrainer uses standalone weather profile JSON files (not embedded in scenarios) containing altitude-layered wind data, METAR strings, and precipitation type. Three example files exist at `docs/atctrainer-weather-examples/`.

Adding weather means:
1. Aircraft are affected by wind — headwinds slow ground speed, crosswinds cause drift
2. Heading != track when there's wind (crab angle via WCA)
3. ATC speed commands are IAS; what shows on radar is ground speed (GS = f(TAS, wind))

This is the most impactful physics change since the original flight model — it touches FlightPhysics, AircraftState, SimulationWorld, navigation, all CRC DTOs, and adds new hub methods.

## Approach

5 chunks, sequential dependency. Chunks 1-2 are Yaat.Sim only. Chunk 3 is yaat-server. Chunk 4 is client. Chunk 5 is polish.

---

## Chunk 1: Data Models & Wind Interpolation (Yaat.Sim)

**New files:**

- [x] `src/Yaat.Sim/WeatherProfile.cs` — `WeatherProfile` class + `WindLayer` class
  - Deserializable from ATCTrainer JSON (camelCase: `id`, `artccId`, `name`, `precipitation`, `windLayers`, `metars`)
  - `WindLayer`: `Id`, `Altitude` (ft MSL), `Direction` (deg, wind FROM), `Speed` (kts), `Gusts` (kts, nullable)
  - `WeatherProfile`: `Id`, `ArtccId`, `Name`, `Precipitation`, `WindLayers` (sorted by altitude on load), `Metars`

- [x] `src/Yaat.Sim/WindInterpolator.cs` — static wind utilities
  - `WindAtAltitude` readonly record struct: `DirectionDeg`, `SpeedKts`
  - `GetWindAt(WeatherProfile?, double altitudeFt) -> WindAtAltitude`
    - null profile or empty layers -> zero wind `(0, 0)`
    - Below lowest layer -> clamp to lowest layer
    - Above highest layer -> clamp to highest layer
    - Between layers -> **vector interpolation** (not angular): decompose each layer into N/E components, lerp components, reconstruct direction+speed. Handles 0/360 wraparound correctly.
  - `GetWindComponents(WeatherProfile?, double altitudeFt) -> (double northKts, double eastKts)` — convenience for physics; returns the wind effect vector (reversed from "wind FROM" to "wind blows toward")
  - `IasToTas(double ias, double altitudeFt) -> double` — lookup table with linear interpolation:

    | Altitude | TAS/CAS Factor |
    |----------|----------------|
    | 0        | 1.000          |
    | 5,000    | 1.077          |
    | 10,000   | 1.165          |
    | 15,000   | 1.261          |
    | 20,000   | 1.370          |
    | 25,000   | 1.494          |
    | 30,000   | 1.634          |
    | 35,000   | 1.796          |
    | 40,000   | 2.014          |

    Accurate within 1-2% of standard atmosphere. At FL350, IAS 280 -> TAS ~503 kts.

  - `ComputeWindCorrectionAngle(double desiredTrackDeg, double tasKts, double windFromDeg, double windSpeedKts) -> double` — returns WCA in degrees (positive = right correction). Used by navigation and phases.

  - Gusts stored in model but not applied to physics (steady-state wind only for ATC training).

**Modified files:**

- [x] `src/Yaat.Sim/AircraftState.cs` — add two fields:
  - `double IndicatedAirspeed { get; set; }` — what the pilot flies (IAS)
  - `double Track { get; set; }` — ground track direction (heading + wind drift)

- [x] `src/Yaat.Sim/ControlTargets.cs` — doc-only: clarify `TargetSpeed` is IAS

**Tests:**

- [x] `tests/Yaat.Sim.Tests/WindInterpolatorTests.cs` (~20 tests)
  - Null profile, empty layers, single layer, below/above extremes, between layers
  - Vector interpolation: layers at 350deg and 10deg interpolate through 0, not 180
  - IasToTas: spot checks at FL100 (~1.165x), FL250 (~1.494x), FL350 (~1.796x)
  - WCA: headwind = 0 WCA, 90deg crosswind = nonzero WCA, tailwind = 0 WCA
  - GetWindComponents: wind FROM 270 at 10kts -> eastward effect (+east, 0 north)
  - Deserialization of all 3 example JSON files (verify layer counts, altitudes, speeds)

---

## Chunk 2: Physics Engine (Yaat.Sim)

**Core change: IAS becomes the primary airspeed state; GS becomes derived from IAS + wind.**

- [x] `src/Yaat.Sim/FlightPhysics.cs`:
  - Change `Update()` signature: add `WeatherProfile? weather = null` parameter
  - **`UpdateSpeed()`**: compare `IndicatedAirspeed` against `TargetSpeed` (both IAS). Today it compares `GroundSpeed` — change to `IndicatedAirspeed`.
  - **`UpdatePosition()`**:
    - **On ground** (`IsOnGround`): no wind effect. Keep `GroundSpeed` as-is (set by ground phases). Sync `IndicatedAirspeed = GroundSpeed`, `Track = Heading`. Existing displacement math unchanged.
    - **Airborne**: Compute TAS via `WindInterpolator.IasToTas()`. Get wind components at altitude via `WindInterpolator.GetWindComponents()`. Vector sum: `(tasN, tasE) = TAS*(cos(hdg), sin(hdg)) + (windN, windE)`. Derive `GS = |vector|`, `Track = atan2(E, N)`. Update position along the resultant vector. Set `aircraft.GroundSpeed` and `aircraft.Track`.
  - Auto-init guard: if `IndicatedAirspeed <= 0 && GroundSpeed > 0 && !IsOnGround` -> set `IndicatedAirspeed = GroundSpeed` (backward compat for tests/existing aircraft without explicit IAS)
  - **`UpdateNavigation()`**: Apply wind correction angle (WCA) when steering toward waypoints. Instead of `heading = bearing to fix`, compute `heading = bearing + WCA` using `WindInterpolator.ComputeWindCorrectionAngle()`. This gives straight-line ground tracks on the radar scope — critical for ATC training realism. Requires passing weather into `UpdateNavigation`.

- [x] `src/Yaat.Sim/SimulationWorld.cs`:
  - Add `WeatherProfile? Weather { get; set; }`
  - Pass `Weather` to `FlightPhysics.Update()` in `Tick()`

- [x] `src/Yaat.Sim/Phases/PhaseContext.cs`:
  - Add `WeatherProfile? Weather { get; init; }` for phases that need wind info

- [x] Initialize `IndicatedAirspeed = GroundSpeed` and `Track = Heading` at spawn sites:
  - `src/Yaat.Sim/Scenarios/AircraftGenerator.cs` (~4 sites)
  - `src/Yaat.Sim/Scenarios/AircraftInitializer.cs` (OnRunway, OnFinal, AtParking)
  - `yaat-server: ScenarioLoader.cs` (~4 sites)
  - `yaat-server: RoomEngine.cs` WarpAircraft

- [x] Phase transition edge cases:
  - `TakeoffPhase`: at liftoff, set `IndicatedAirspeed = GroundSpeed` (ground->air transition)
  - `LandingPhase`: at touchdown, set `IndicatedAirspeed = GroundSpeed` (air->ground transition)
  - `TouchAndGoPhase`: same at touchdown

- [x] Speed limit checks must use IAS:
  - 250kt below 10,000ft (14 CFR 91.117) — find where enforced, switch to `IndicatedAirspeed`
  - Holding speed limits (AIM 5-3-8: 200/230/265 KIAS) — `MaxHoldingSpeed()` must compare against `IndicatedAirspeed`

- [ ] Phases that set explicit headings (holding outbound, intercept courses, pattern legs) should apply WCA via `PhaseContext.Weather` when setting `TargetHeading` for fixed-course segments. *(deferred to Chunk 5)*

**Tests:**

- [x] `tests/Yaat.Sim.Tests/WindPhysicsTests.cs` (~15 tests)
  - Zero wind: IAS == GS, Track == Heading (within tolerance)
  - Headwind: GS < IAS, Track == Heading
  - Tailwind: GS > IAS, Track == Heading
  - Crosswind: Track != Heading (drift angle), reasonable GS
  - Ground aircraft: unaffected by wind
  - TAS at FL350: IAS 280 -> GS significantly higher than 280 with no wind
  - Backward compat: null weather = all existing behavior preserved
  - WCA in navigation: aircraft heading includes correction, track matches bearing to fix
- [x] Verify all 315+ existing Sim tests still pass (critical regression check)

---

## Chunk 3: Server Wiring (yaat-server)

- [x] `TrainingRoom.cs` — add `WeatherProfile? Weather { get; set; }`

- [x] `RoomEngine.cs` — add `LoadWeather(string json)` and `ClearWeather()`:
  - Deserialize `WeatherProfile` from JSON
  - Validate (non-empty wind layers)
  - Set `Room.Weather` and `World.Weather`
  - Broadcast `WeatherChanged` to training clients
  - Return `CommandResultDto`

- [x] `TrainingHub.cs` — add hub methods:
  - `LoadWeather(string weatherJson) -> CommandResultDto`
  - `ClearWeather()`

- [x] `TrainingDtos.cs` — add DTOs:
  - `WeatherChangedDto(string? Name, List<WindLayerDto>? WindLayers, string? Precipitation, List<string>? Metars)`
  - `WindLayerDto(int Altitude, int Direction, int Speed, int? Gusts)`

- [x] `TrainingBroadcastService.cs` — add `BroadcastWeatherChanged(TrainingRoom)`

- [x] `TickProcessor.cs` — pass `Room.Weather` to `PhaseContext.Weather` in `PreTick`

- [x] `DtoConverter.cs` — use `aircraft.Track` for CRC DTO `GroundTrack` fields:
  - `StarsTrackDto`, `EramTargetDto`, `TowerCabAircraftDto`, `AsdexTargetDto`

- [x] `AircraftChangeTracker.cs` — add `Track` to fingerprint so track direction changes trigger CRC updates

- [x] `LoadScenarioResult` / response DTO — include `WeatherName` if room has active weather

- [x] Verify all 64 server tests still pass

---

## Chunk 4: Client UI (Yaat.Client)

- [x] `ServerConnection.cs`:
  - Add `LoadWeatherAsync(string json) -> Task<CommandResultDto>`
  - Add `ClearWeatherAsync() -> Task`
  - Add `WeatherChanged` event + handler registration
  - Add client-side DTOs: `WeatherChangedDto`, `WindLayerDto`

- [x] `MainViewModel.cs`:
  - Add `ActiveWeatherName` observable property
  - Add `LoadWeatherAsync()` relay command (file picker -> read JSON -> send to server)
  - Add `ClearWeatherAsync()` relay command
  - Wire `WeatherChanged` event to update `ActiveWeatherName`

- [x] `MainWindow.axaml` — add `_Weather` menu with Load Weather / Clear Weather items

- [x] `LoadWeatherWindow.axaml` + `.cs` — folder browser modal, scans JSONs for `windLayers`, shows name + layer count

- [x] `UserPreferences.cs` — add last-used weather directory (like scenario directory)

---

## Chunk 5: Polish & Docs

- [x] **Update `USER_GUIDE.md`** with weather loading instructions
- [x] **Update CLAUDE.md** Architecture section with weather fields
- [ ] **Holding pattern wind compensation** (tier 2, optional): after first circuit, measure actual inbound leg time and adjust outbound timing so inbound leg matches target (1 min / 1.5 min). ~20 lines in `HoldingPatternPhase`.

---

## Key Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Weather scope | Room-level, not scenario-level | ATCTrainer treats weather as standalone; same scenario can run with different weather |
| Speed model | IAS primary, GS computed | Matches real aviation: ATC commands IAS, radar shows GS |
| TAS conversion | Standard atmosphere lookup table (8 points) | aviation-sim-expert: proposed +2%/1000ft is severely wrong (35% error at FL350). Lookup table is accurate within 1-2% |
| Wind interpolation | Vector decomposition (N/E lerp) | aviation-sim-expert: angular interpolation fails at 0/360 boundary. Vector lerp is correct and simple |
| WCA (wind correction) | Mandatory for navigation | aviation-sim-expert: without WCA, aircraft fly pursuit curves, not straight lines. Unacceptable for ATC training |
| Gusts | Stored, not applied to physics | Steady-state wind sufficient for ATC training; gusts are METAR info |
| Ground aircraft | Wind-immune | Aircraft on ground don't drift; IAS synced to GS on ground |
| CRC METAR/SSA | Deferred to later | Core value is wind physics; CRC display is separate feature |
| NEXRAD overlay | Deferred to later | Requires external radar imagery source |
| Backward compat | null weather = zero wind | All existing scenarios/tests work unchanged |
| Holding wind comp | Deferred to chunk 5 (optional) | Tier 1 (no outbound timing adjustment) is acceptable initially |

## Verification

1. Load a scenario without weather -> aircraft behave exactly as before
2. Load weather profile `01J7EWDDP3P87KHP0Z5W9SMDMY.json` (27-37kt winds at 1000ft) -> aircraft ground speeds visibly differ from airspeed; aircraft crab into wind
3. Load a headwind scenario -> aircraft approaching runway have lower GS (visible in CRC)
4. Clear weather -> aircraft return to IAS == GS behavior
5. TAS sanity: aircraft at FL350 indicating 280 should show GS ~500+ (no wind) due to TAS correction
6. Navigation tracks: aircraft flying DCT to a fix should fly a straight line on the radar scope (WCA applied), not a curved pursuit path
7. Run full test suite: `dotnet test` across all 3 test projects (450+ tests)
8. Build both repos: `dotnet build` with 0 warnings
